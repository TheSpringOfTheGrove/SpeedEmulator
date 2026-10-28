using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>
/// Simulation formats observed in the supplied Alipay statement. Numeric suffixes
/// are synthetic; no account-specific segments from the statement are reused.
/// Ownership metadata is temporary and is removed by the flow repository on save.
/// </summary>
internal static class AlipayGeneratedNumberRules
{
    private const string StateKey = "__AlipayGeneratedNumbers";
    private static readonly string[] ContextKeys =
    [
        "AlipayOriginalTradeNumber", "原交易订单号", "原流水号",
        "AlipayOriginalMerchantNumber", "原商家订单号",
        "AlipayOriginalOrderDate", "原下单日期"
    ];
    private static readonly Regex OriginalNumber = new(
        @"(?:原交易订单号|原流水号|原订单号)\s*[:：=]\s*(\d{20,40})(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex FullOrderDate = new(
        @"(?:下单日期|下单时间|您于|原下单日期)\s*[:：=]?\s*(\d{4})[-/.年](\d{1,2})[-/.月](\d{1,2})日?", RegexOptions.CultureInvariant);
    private static readonly Regex MonthDayOrderDate = new(
        @"(?:您于|下单日期|原下单日期)\s*[:：=]?\s*(\d{1,2})月(\d{1,2})日", RegexOptions.CultureInvariant);

    public static bool AppliesTo(Bank bank) => bank.Type == BankTypes.Personal && bank.Name == "支付宝";

    public static void CopyGenerationContext(Bank bank, FlowRuleBase rule, FlowRecord record)
    {
        if (!AppliesTo(bank)) return;
        foreach (var key in ContextKeys)
            if (rule.ExtraFields.TryGetValue(key, out var value)) record.ExtraFields[key] = value;
    }

    public static void Apply(Bank bank, FlowRecord record)
    {
        if (!AppliesTo(bank) || record.IsDocumentImported) return;
        var state = ReadState(record);
        if (state is null)
        {
            var serial = HasField(bank, nameof(FlowRecord.SerialNum)) && string.IsNullOrWhiteSpace(record.SerialNum);
            var merchant = HasField(bank, nameof(FlowRecord.MerchantName)) && string.IsNullOrWhiteSpace(record.MerchantName);
            if (!serial && !merchant) return;
            state = new NumberState { OwnSerial = serial, OwnMerchant = merchant };
        }
        else
        {
            RespectEdits(record, state);
        }
        Render(bank, record, state);
        WriteState(record, state);
    }

    /// <summary>Refresh only owned fields after scheduling/amount repair or formula evaluation.</summary>
    public static bool Complete(Bank bank, IReadOnlyList<FlowRecord> records)
    {
        if (!AppliesTo(bank)) return false;
        var entries = records.Select(record => new Entry(record,
            record.IsDocumentImported ? null : ReadState(record))).ToList();
        if (!entries.Any(entry => entry.State is not null)) return false;
        var before = entries.Select(e => (e.Record.SerialNum, e.Record.MerchantName)).ToArray();
        var duplicatedIds = entries.Where(e => e.State is not null).GroupBy(e => e.State!.Id)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.State is not null))
        {
            var state = entry.State!;
            if (!ids.Add(state.Id)) { state.Id = Guid.NewGuid().ToString("N"); ids.Add(state.Id); }
            if (duplicatedIds.Contains(state.ParentId)) { state.ParentId = string.Empty; state.OriginalSerial = string.Empty; }
        }
        var aliases = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        var merchantAliases = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            AddAlias(aliases, entry.Record.SerialNum, entry);
            AddAlias(merchantAliases, entry.Record.MerchantName, entry);
            if (entry.State is not { } state) continue;
            RespectEdits(entry.Record, state);
            AddAlias(aliases, state.LastSerial, entry);
            Render(bank, entry.Record, state);
        }

        // Reserve provided/imported numbers first. Only newly generated identifiers
        // participate in collision repair; repeated user-provided values are retained.
        var serials = entries.Where(e => e.State?.OwnSerial != true)
            .Select(e => e.Record.SerialNum).Where(s => !string.IsNullOrWhiteSpace(s)).ToHashSet(StringComparer.Ordinal);
        var merchants = entries.Where(e => e.State?.OwnMerchant != true && !IsRelated(Classify(e.Record)))
            .Select(e => e.Record.MerchantName).Where(s => !string.IsNullOrWhiteSpace(s)).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.State is not null))
        {
            var state = entry.State!;
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var serialCollision = state.OwnSerial && !string.IsNullOrWhiteSpace(entry.Record.SerialNum)
                    && serials.Contains(entry.Record.SerialNum);
                var merchantCollision = state.OwnMerchant && IsUniqueMerchant(Classify(entry.Record))
                    && !string.IsNullOrWhiteSpace(entry.Record.MerchantName) && merchants.Contains(entry.Record.MerchantName);
                if (!serialCollision && !merchantCollision) break;
                state.Nonce = Guid.NewGuid().ToString("N");
                if (serialCollision && Classify(entry.Record) == Kind.YuebaoYield) state.SerialVariant++;
                Render(bank, entry.Record, state);
            }
            if (state.OwnSerial && !string.IsNullOrWhiteSpace(entry.Record.SerialNum)) serials.Add(entry.Record.SerialNum);
            if (state.OwnMerchant && !string.IsNullOrWhiteSpace(entry.Record.MerchantName)) merchants.Add(entry.Record.MerchantName);
            AddAlias(aliases, entry.Record.SerialNum, entry);
            AddAlias(merchantAliases, entry.Record.MerchantName, entry);
        }

        var byId = entries.Where(e => e.State is not null).ToDictionary(e => e.State!.Id, StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.State is not null && IsRelated(Classify(e.Record))))
        {
            var state = entry.State!;
            var kind = Classify(entry.Record);
            var original = First(entry.Record, "AlipayOriginalTradeNumber", "原交易订单号", "原流水号");
            if (string.IsNullOrWhiteSpace(original)) original = OriginalNumber.Match(BusinessText(entry.Record)).Groups[1].Value;
            if (string.IsNullOrWhiteSpace(original) && !state.OwnSerial && entry.Record.SerialNum.Contains('_'))
                original = entry.Record.SerialNum.Split('_', 2)[0];
            var declaredMerchant = state.OwnMerchant ? string.Empty : entry.Record.MerchantName;
            if (state.DeclaredOriginal != original || state.DeclaredMerchant != declaredMerchant)
            {
                state.ParentId = state.OriginalSerial = state.OriginalMerchant = string.Empty;
                state.DeclaredOriginal = original;
                state.DeclaredMerchant = declaredMerchant;
            }
            Entry? parent = null;
            if (!string.IsNullOrEmpty(state.ParentId)) byId.TryGetValue(state.ParentId, out parent);
            if (string.IsNullOrWhiteSpace(original)) original = state.OriginalSerial;
            if (parent is null && IsOriginalSerial(original) && aliases.TryGetValue(original, out var matches))
                parent = UniqueParent(matches, entry);
            if (parent is null && !state.OwnMerchant && !string.IsNullOrWhiteSpace(entry.Record.MerchantName)
                && !ContainsFormula(entry.Record.MerchantName)
                && merchantAliases.TryGetValue(entry.Record.MerchantName, out var merchantMatches))
                parent = UniqueParent(merchantMatches, entry);

            if (parent is not null && IsParent(parent, entry) && IsOriginalSerial(parent.Record.SerialNum))
            {
                original = parent.Record.SerialNum;
                state.ParentId = parent.State?.Id ?? string.Empty;
                state.OriginalMerchant = parent.Record.MerchantName;
                state.Relation = "Linked";
            }
            else
            {
                parent = null;
                state.ParentId = string.Empty;
                state.OriginalMerchant = First(entry.Record, "AlipayOriginalMerchantNumber", "原商家订单号");
                state.Relation = IsOriginalSerial(original) ? "ExplicitOriginal" : "MissingOriginal";
            }
            state.OriginalSerial = IsOriginalSerial(original) ? original : string.Empty;
            var time = entry.Record.AccountTime;
            if (time.HasValue && state.OriginalSerial.Length > 0)
            {
                if (state.OwnSerial)
                {
                    var suffix = kind == Kind.Refund
                        ? $"{Date(time.Value)}3000{Digits(state.Nonce + ":refund", 20)}"
                        : state.OriginalMerchant.Length > 0 && !ContainsFormula(state.OriginalMerchant)
                            ? state.OriginalMerchant + "-" + AmountCents(entry.Record)
                            : string.Empty;
                    if (suffix.Length > 0)
                    {
                        var candidate = state.OriginalSerial + "_" + suffix;
                        // Deferred charges for the same order and amount can occur
                        // twice. Without an explicit distinct child ID, keep the
                        // second independent rather than inventing a false linkage.
                        if (candidate == entry.Record.SerialNum || !serials.Contains(candidate))
                        {
                            serials.Remove(entry.Record.SerialNum);
                            SetSerial(bank, entry.Record, state, candidate);
                            serials.Add(candidate);
                        }
                    }
                }
                if (state.OwnMerchant && state.OriginalMerchant.Length > 0 && !ContainsFormula(state.OriginalMerchant))
                    SetMerchant(bank, entry.Record, state, state.OriginalMerchant);
            }
        }
        foreach (var entry in entries)
            if (entry.State is { } state) WriteState(entry.Record, state);
        return entries.Where((entry, i) => entry.Record.SerialNum != before[i].SerialNum
            || entry.Record.MerchantName != before[i].MerchantName).Any();
    }

    public static void CopyState(Bank bank, FlowRecord source, FlowRecord target)
    {
        if (AppliesTo(bank) && source.ExtraFields.TryGetValue(StateKey, out var value)) target.ExtraFields[StateKey] = value;
    }

    private static bool Render(Bank bank, FlowRecord record, NumberState state)
    {
        if (!record.AccountTime.HasValue) return false;
        var time = record.AccountTime.Value;
        var date = Date(time);
        var kind = Classify(record);
        var orderDate = ResolveOrderDate(record, time);
        var profile = string.Join('|', record.BankId, record.BankUserId, record.Account,
            record.OppositeAccount, record.OppositeUsername, record.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex"), kind);
        var serial = kind switch
        {
            Kind.Transfer or Kind.Withdrawal => date + "2000" + Digits(state.Nonce + ":serial", 20),
            Kind.FamilyCard => date + "0420" + Digits(state.Nonce + ":serial", 16),
            Kind.HuabeiRepayment => date + "2902" + Digits(state.Nonce + ":serial", 16),
            Kind.Water or Kind.CreditCardRepayment => date + "0000" + Digits(state.Nonce + ":serial", 16),
            Kind.Yulibao or Kind.BalanceUpgradeInterest => date + "1025" + Digits(state.Nonce + ":serial", 20),
            Kind.YulibaoBankIn => date + "1012" + Digits(state.Nonce + ":serial", 28),
            Kind.YuebaoBankOut => date + "0191" + Digits(state.Nonce + ":serial", 20),
            Kind.YuebaoDatedYield => date + Digits(state.Nonce + ":serial", 12),
            Kind.YuebaoYield => Digits(profile + ":yield:" + state.SerialVariant, 20) + date,
            Kind.Refund => date + "3000" + Digits(state.Nonce + ":refund", 20),
            _ => date + "2300" + Digits(state.Nonce + ":serial", 16)
        };
        var merchant = kind switch
        {
            Kind.Transfer or Kind.HuabeiRepayment or Kind.CreditCardRepayment or Kind.YuebaoYield
                or Kind.YuebaoDatedYield or Kind.YulibaoBankIn or Kind.Refund => string.Empty,
            Kind.FamilyCard => date + "2300" + Digits(state.Nonce + ":merchant", 16),
            Kind.QrCode => Digits(state.Nonce + ":merchant", 23),
            Kind.DouyinRecharge => "200107" + time.ToString("yyMMdd", CultureInfo.InvariantCulture) + Digits(state.Nonce + ":merchant", 16),
            Kind.DouyinCommerce => "200105" + time.ToString("yyMMdd", CultureInfo.InvariantCulture) + Digits(state.Nonce + ":merchant", 16),
            Kind.Withdrawal or Kind.HexMerchant => Hex(state.Nonce + ":merchant"),
            Kind.Deferred when orderDate.HasValue => "CDP10" + orderDate.Value.ToString("yyMMdd", CultureInfo.InvariantCulture) + Digits(state.Nonce + ":merchant", 20),
            Kind.Yulibao => date + "0201" + Digits(state.Nonce + ":merchant", 8),
            Kind.YuebaoBankOut => "LC" + time.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Digits(state.Nonce + ":merchant", 20),
            Kind.Water => Digits(profile + ":utility", 8),
            Kind.Electricity => Digits(profile + ":utility", 13),
            // Only one such sample exists. Use a declared synthetic format, rather
            // than treating its institution/account-specific middle as public data.
            Kind.BalanceUpgradeInterest => date + Digits(state.Nonce + ":merchant", 53) + "INTEREST",
            _ => time.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Digits(state.Nonce + ":merchant", 8)
        };
        var changed = false;
        if (state.OwnSerial) changed |= SetSerial(bank, record, state, serial);
        if (state.OwnMerchant) changed |= SetMerchant(bank, record, state, merchant);
        return changed;
    }

    private static Kind Classify(FlowRecord record)
    {
        var text = BusinessText(record);
        var brand = text + " " + record.OppositeUsername;
        if (new[] { record.ProductBrief, record.ProductName, record.Remark, record.TradeExplain }
            .Any(value => value?.TrimStart().StartsWith("退款", StringComparison.Ordinal) == true
                || value?.TrimStart().StartsWith("退货退款", StringComparison.Ordinal) == true)) return Kind.Refund;
        if (text.Contains("先用后付", StringComparison.Ordinal)) return Kind.Deferred;
        if (text.Contains("亲情卡", StringComparison.Ordinal)) return Kind.FamilyCard;
        if (text.Contains("余额升级服务收益", StringComparison.Ordinal)) return Kind.BalanceUpgradeInterest;
        if (text.Contains("余利宝", StringComparison.Ordinal))
            return text.Contains("银行卡转入", StringComparison.Ordinal) ? Kind.YulibaoBankIn : Kind.Yulibao;
        if (text.Contains("余额宝", StringComparison.Ordinal))
        {
            if (text.Contains("转出到银行卡", StringComparison.Ordinal)) return Kind.YuebaoBankOut;
            if (text.Contains("收益", StringComparison.Ordinal))
                return Regex.IsMatch(text, @"\d{4}[.年/-]\d{1,2}[.月/-]\d{1,2}") ? Kind.YuebaoDatedYield : Kind.YuebaoYield;
        }
        if (text.Contains("提现", StringComparison.Ordinal)) return Kind.Withdrawal;
        if (text.Contains("还款", StringComparison.Ordinal))
        {
            if (brand.Contains("花呗", StringComparison.Ordinal) || brand.Contains("信用购", StringComparison.Ordinal)) return Kind.HuabeiRepayment;
            if (text.Contains("信用卡", StringComparison.Ordinal)) return Kind.CreditCardRepayment;
        }
        if (text.Contains("水费", StringComparison.Ordinal)) return Kind.Water;
        if (text.Contains("电费", StringComparison.Ordinal)) return Kind.Electricity;
        if (brand.Contains("钻石充值", StringComparison.Ordinal) && (brand.Contains("微播", StringComparison.Ordinal) || brand.Contains("抖音", StringComparison.Ordinal))) return Kind.DouyinRecharge;
        if (brand.Contains("抖音电商", StringComparison.Ordinal)) return Kind.DouyinCommerce;
        if (brand.Contains("美宜佳", StringComparison.Ordinal)) return Kind.HexMerchant;
        if (text.Contains("收钱码收款", StringComparison.Ordinal) || text.Contains("经营码交易", StringComparison.Ordinal)
            || text.Contains("扫码收款", StringComparison.Ordinal)) return Kind.QrCode;
        // CashCheck defaults to “转账” for every bank, so it cannot identify an Alipay transfer.
        if (text.Contains("转账", StringComparison.Ordinal) || text.Contains("生活费", StringComparison.Ordinal)
            || text.Contains("手机款", StringComparison.Ordinal) || text.Trim() == "收款") return Kind.Transfer;
        return Kind.Consumer;
    }

    private static string BusinessText(FlowRecord r) => string.Join(' ', new[]
        { r.ProductBrief, r.ProductName, r.Remark, r.TradeExplain }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());

    private static DateTime? ResolveOrderDate(FlowRecord record, DateTime time)
    {
        var explicitDate = First(record, "AlipayOriginalOrderDate", "原下单日期");
        if (DateTime.TryParseExact(explicitDate, ["yyyyMMdd", "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) && date.Date <= time.Date) return date.Date;
        if (Regex.IsMatch(record.MerchantName ?? string.Empty, @"^CDP10\d{26}$")
            && DateTime.TryParseExact("20" + record.MerchantName!.Substring(5, 6), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && date.Date <= time.Date) return date.Date;
        var match = FullOrderDate.Match(BusinessText(record));
        if (match.Success && TryDate(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), out date))
            return date.Date <= time.Date ? date.Date : null;
        match = MonthDayOrderDate.Match(BusinessText(record));
        if (match.Success)
        {
            var month = int.Parse(match.Groups[1].Value);
            var day = int.Parse(match.Groups[2].Value);
            if (TryDate(time.Year, month, day, out date) && date.Date <= time.Date) return date;
            if (TryDate(time.Year - 1, month, day, out date)) return date;
        }
        return null;
    }

    private static bool TryDate(int year, int month, int day, out DateTime date)
    {
        date = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateTime(year, month, day);
        return true;
    }

    private static bool IsParent(Entry parent, Entry child) => !ReferenceEquals(parent.Record, child.Record)
        && parent.Record.BankId == child.Record.BankId && parent.Record.BankUserId == child.Record.BankUserId
        && parent.Record.Account == child.Record.Account && parent.Record.AccountTime <= child.Record.AccountTime
        && Classify(parent.Record) != Kind.Refund && parent.Record.SerialNum?.Contains('_') != true;

    private static Entry? UniqueParent(IEnumerable<Entry> candidates, Entry child)
    {
        var matches = candidates.Where(candidate => IsParent(candidate, child)).DistinctBy(e => e.Record).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static void AddAlias(Dictionary<string, List<Entry>> map, string? value, Entry entry)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!map.TryGetValue(value, out var list)) map[value] = list = [];
        if (!list.Contains(entry)) list.Add(entry);
    }

    private static bool IsRelated(Kind kind) => kind is Kind.Refund or Kind.Deferred;
    private static bool IsUniqueMerchant(Kind kind) => kind is not (Kind.Water or Kind.Electricity or Kind.Refund or Kind.Deferred);
    private static bool IsOriginalSerial(string? value) => value is not null && Regex.IsMatch(value, @"^\d{20,40}$");
    private static bool ContainsFormula(string value) => value.Contains("\\\\", StringComparison.Ordinal);
    private static bool HasField(Bank bank, string field) => bank.FlowColumns.Any(column => column.Field == field);
    private static string First(FlowRecord record, params string[] keys) => keys.Select(key => record.ExtraFields.GetValueOrDefault(key))
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    private static string Date(DateTime time) => time.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    private static string AmountCents(FlowRecord record) => decimal.Round((decimal)Math.Abs(record.TradeMoney ?? 0) * 100m,
        0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static void RespectEdits(FlowRecord record, NumberState state)
    {
        if (state.OwnSerial && record.SerialNum != state.LastSerial
            && !(state.LastSerial.Length == 0 && string.IsNullOrWhiteSpace(record.SerialNum))) state.OwnSerial = false;
        if (state.OwnMerchant && record.MerchantName != state.LastMerchant
            && !(state.LastMerchant.Length == 0 && string.IsNullOrWhiteSpace(record.MerchantName))) state.OwnMerchant = false;
    }

    private static bool SetSerial(Bank bank, FlowRecord record, NumberState state, string value)
    {
        var changed = record.SerialNum != value;
        record.SerialNum = state.LastSerial = value;
        SyncAliases(bank, record, nameof(FlowRecord.SerialNum), value, "流水号", "交易订单号");
        return changed;
    }

    private static bool SetMerchant(Bank bank, FlowRecord record, NumberState state, string value)
    {
        var changed = record.MerchantName != value;
        record.MerchantName = state.LastMerchant = value;
        SyncAliases(bank, record, nameof(FlowRecord.MerchantName), value, "商家订单号");
        return changed;
    }

    private static void SyncAliases(Bank bank, FlowRecord record, string field, string value, params string[] names)
    {
        foreach (var key in bank.FlowColumns.Where(c => c.Field == field).Select(c => c.Name).Concat(names).Distinct())
            if (!string.IsNullOrEmpty(key) && record.ExtraFields.ContainsKey(key)) record.ExtraFields[key] = value;
    }

    private static NumberState? ReadState(FlowRecord record)
    {
        if (!record.ExtraFields.TryGetValue(StateKey, out var value)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<NumberState>(value);
            return state is { Id.Length: > 0, Nonce.Length: > 0, LastSerial: not null, LastMerchant: not null,
                ParentId: not null, OriginalSerial: not null, OriginalMerchant: not null,
                DeclaredOriginal: not null, DeclaredMerchant: not null } ? state : null;
        }
        catch (JsonException) { return null; }
    }
    private static void WriteState(FlowRecord record, NumberState state) => record.ExtraFields[StateKey] = JsonSerializer.Serialize(state);
    private static string Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16)).ToLowerInvariant();

    private static string Digits(string key, int length)
    {
        var result = new StringBuilder(length);
        for (var block = 0; result.Length < length; block++)
            foreach (var value in SHA256.HashData(Encoding.UTF8.GetBytes(key + ":" + block)))
            {
                if (value >= 250) continue;
                result.Append((char)('0' + value % 10));
                if (result.Length == length) break;
            }
        return result.ToString();
    }

    private sealed record Entry(FlowRecord Record, NumberState? State);
    private sealed class NumberState
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Nonce { get; set; } = Guid.NewGuid().ToString("N");
        public int SerialVariant { get; set; }
        public bool OwnSerial { get; set; }
        public bool OwnMerchant { get; set; }
        public string LastSerial { get; set; } = string.Empty;
        public string LastMerchant { get; set; } = string.Empty;
        public string ParentId { get; set; } = string.Empty;
        public string OriginalSerial { get; set; } = string.Empty;
        public string OriginalMerchant { get; set; } = string.Empty;
        public string DeclaredOriginal { get; set; } = string.Empty;
        public string DeclaredMerchant { get; set; } = string.Empty;
        public string Relation { get; set; } = string.Empty;
    }

    private enum Kind
    {
        Consumer, Transfer, Withdrawal, FamilyCard, HuabeiRepayment, CreditCardRepayment,
        Water, Electricity, QrCode, DouyinRecharge, DouyinCommerce, HexMerchant, Refund, Deferred,
        Yulibao, YulibaoBankIn, YuebaoBankOut, YuebaoYield, YuebaoDatedYield, BalanceUpgradeInterest
    }
}
