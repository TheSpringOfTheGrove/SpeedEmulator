using System.Globalization;
using System.Reflection;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public static class FlowGeneratedRowKinds
{
    public const string Interest = "Interest";
    public const string InterestTax = "InterestTax";
    public const string LegacyExtraField = "__GeneratedSystemRowKind";

    public static bool IsInterest(FlowRecord record)
    {
        return HasKind(record, Interest);
    }

    public static bool IsInterestTax(FlowRecord record)
    {
        return HasKind(record, InterestTax);
    }

    public static bool IsSystemInterest(FlowRecord record)
    {
        return IsInterest(record) || IsInterestTax(record);
    }

    public static void SetKind(FlowRecord record, string rowKind)
    {
        record.GeneratedRowKind = rowKind;
        record.ExtraFields[LegacyExtraField] = rowKind;
    }

    private static bool HasKind(FlowRecord record, string rowKind)
    {
        return string.Equals(record.GeneratedRowKind, rowKind, StringComparison.Ordinal)
            || (record.ExtraFields.TryGetValue(LegacyExtraField, out var legacyKind)
                && string.Equals(legacyKind, rowKind, StringComparison.Ordinal));
    }
}

public readonly record struct BankInterestCalculationResult(
    int InterestRecordCount,
    int InterestTaxRecordCount,
    double InterestTotal,
    bool RecordsChanged);

/// <summary>
/// Creates or reuses scheduled settlement rows and calculates interest from daily closing balances.
/// The same implementation is used by automatic generation and by the flow details "calculate" action.
/// </summary>
public static class BankInterestCalculationService
{
    private const string InterestText = "结息";
    private const string InterestTaxText = "利息税";
    private const string PersonalCurrentInterestRemark = "个人活期结息";

    private static readonly HashSet<string> ProtectedSettingFields = new(StringComparer.Ordinal)
    {
        nameof(FlowRecord.Index),
        nameof(FlowRecord.Id),
        nameof(FlowRecord.ReplaceIndex),
        nameof(FlowRecord.BankId),
        nameof(FlowRecord.BankUserId),
        nameof(FlowRecord.MoveFlag),
        nameof(FlowRecord.GeneratedRowKind),
        nameof(FlowRecord.AccountTime),
        nameof(FlowRecord.TradeMoney),
        nameof(FlowRecord.Balance),
        nameof(FlowRecord.BalanceAmount),
        nameof(FlowRecord.CreditAmount),
        nameof(FlowRecord.DebitAmount),
        nameof(FlowRecord.IncomeAttribute),
        nameof(FlowRecord.IncomeFlag),
        nameof(FlowRecord.Account)
    };

    public static BankInterestCalculationResult Recalculate(
        Bank bank,
        BankUser bankUser,
        BankInterestSetting? setting,
        List<FlowRecord> records,
        double openingBalance,
        DateTime start,
        DateTime end,
        Func<DateTime, string, FlowRecord>? recordFactory = null,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(bankUser);
        ArgumentNullException.ThrowIfNull(records);

        if (!TryParseConfiguration(setting, out var configuration))
        {
            return default;
        }

        NormalizeRange(ref start, ref end);
        var settlementDates = EnumerateSettlementDates(start, end, configuration).ToList();
        if (settlementDates.Count == 0)
        {
            return default;
        }

        var settlementDateSet = settlementDates.ToHashSet();
        var changed = MigrateRecognizableRows(records, settlementDateSet);
        changed |= RemoveObsoleteMarkedRows(records, start, end, settlementDateSet);

        var interestRows = new Dictionary<DateTime, FlowRecord>();
        var taxRows = new Dictionary<DateTime, FlowRecord>();
        var appendTaxRows = setting!.GenerateInterestTaxRow ?? ShouldAppendInterestTaxRecord(bank);

        foreach (var settlementDate in settlementDates)
        {
            var interestRow = FindOrCreateScheduledRow(
                bank,
                bankUser,
                setting!,
                records,
                settlementDate,
                FlowGeneratedRowKinds.Interest,
                configuration,
                recordFactory,
                random,
                ref changed);
            interestRows[settlementDate] = interestRow;

            if (appendTaxRows)
            {
                var taxRow = FindOrCreateScheduledRow(
                    bank,
                    bankUser,
                    setting!,
                    records,
                    settlementDate,
                    FlowGeneratedRowKinds.InterestTax,
                    configuration,
                    recordFactory,
                    random,
                    ref changed);
                taxRows[settlementDate] = taxRow;
            }
        }

        changed |= RemoveDuplicateScheduledRows(records, settlementDates, interestRows, taxRows);

        var originalSystemAmounts = interestRows.Values
            .Concat(taxRows.Values)
            .Distinct()
            .ToDictionary(row => row, CaptureSystemAmountState);
        foreach (var row in originalSystemAmounts.Keys)
        {
            SetSystemAmount(row, 0);
        }

        var normalDailyAmounts = records
            .Where(record => !FlowGeneratedRowKinds.IsSystemInterest(record) && record.AccountTime.HasValue)
            .Where(record => record.AccountTime!.Value.Date >= start.Date
                && record.AccountTime.Value.Date <= settlementDates[^1])
            .GroupBy(record => record.AccountTime!.Value.Date)
            .ToDictionary(
                group => group.Key,
                group => RoundMoney(group.Sum(record => record.TradeMoney ?? 0)));

        var balance = RoundMoney(openingBalance);
        var dailyProduct = 0d;
        var interestTotal = 0d;
        var lastSettlementDate = settlementDates[^1];
        for (var date = start.Date; date <= lastSettlementDate; date = date.AddDays(1))
        {
            if (settlementDateSet.Contains(date))
            {
                var interest = RoundMoney(dailyProduct * configuration.RatePercent / 36500d);
                SetSystemAmount(interestRows[date], interest);
                if (taxRows.TryGetValue(date, out var taxRow))
                {
                    SetSystemAmount(taxRow, 0);
                }

                interestTotal = RoundMoney(interestTotal + interest);
                dailyProduct = 0;
                balance = RoundMoney(balance + interest);
            }

            if (normalDailyAmounts.TryGetValue(date, out var normalAmount))
            {
                balance = RoundMoney(balance + normalAmount);
            }

            if (date < lastSettlementDate)
            {
                dailyProduct += Math.Max(0, balance);
            }
        }

        changed |= originalSystemAmounts.Any(item => item.Value != CaptureSystemAmountState(item.Key));

        return new BankInterestCalculationResult(
            interestRows.Count,
            taxRows.Count,
            interestTotal,
            changed);
    }

    private static FlowRecord FindOrCreateScheduledRow(
        Bank bank,
        BankUser bankUser,
        BankInterestSetting setting,
        List<FlowRecord> records,
        DateTime settlementDate,
        string rowKind,
        InterestConfiguration configuration,
        Func<DateTime, string, FlowRecord>? recordFactory,
        Random? random,
        ref bool changed)
    {
        var existing = records.FirstOrDefault(record =>
            record.AccountTime?.Date == settlementDate
            && HasExpectedKind(record, rowKind));
        if (existing is not null)
        {
            if (!string.Equals(existing.GeneratedRowKind, rowKind, StringComparison.Ordinal))
            {
                changed = true;
            }

            FlowGeneratedRowKinds.SetKind(existing, rowKind);
            return existing;
        }

        var accountTime = CreateSettlementTime(
            settlementDate,
            configuration.StartHour,
            configuration.EndHour,
            bank.Id,
            bankUser.Id,
            random);
        var created = recordFactory?.Invoke(accountTime, rowKind)
            ?? CreateDefaultInterestRecord(bank, bankUser, setting, accountTime, rowKind);
        created.BankId = bank.Id;
        created.BankUserId = bankUser.Id;
        created.AccountTime = accountTime;
        FlowGeneratedRowKinds.SetKind(created, rowKind);
        SetSystemAmount(created, 0);
        records.Add(created);
        changed = true;
        return created;
    }

    private static bool MigrateRecognizableRows(List<FlowRecord> records, IReadOnlySet<DateTime> settlementDates)
    {
        var changed = false;
        foreach (var record in records.Where(record => record.AccountTime.HasValue
                     && settlementDates.Contains(record.AccountTime.Value.Date)))
        {
            if (FlowGeneratedRowKinds.IsSystemInterest(record))
            {
                if (string.IsNullOrWhiteSpace(record.GeneratedRowKind))
                {
                    record.GeneratedRowKind = FlowGeneratedRowKinds.IsInterestTax(record)
                        ? FlowGeneratedRowKinds.InterestTax
                        : FlowGeneratedRowKinds.Interest;
                    changed = true;
                }

                continue;
            }

            var rowKind = RecognizeLegacyRowKind(record);
            if (rowKind is null)
            {
                continue;
            }

            FlowGeneratedRowKinds.SetKind(record, rowKind);
            changed = true;
        }

        return changed;
    }

    private static bool RemoveObsoleteMarkedRows(
        List<FlowRecord> records,
        DateTime start,
        DateTime end,
        IReadOnlySet<DateTime> settlementDates)
    {
        return records.RemoveAll(record =>
            FlowGeneratedRowKinds.IsSystemInterest(record)
            && record.AccountTime.HasValue
            && record.AccountTime.Value >= start
            && record.AccountTime.Value <= end
            && !settlementDates.Contains(record.AccountTime.Value.Date)) > 0;
    }

    private static bool RemoveDuplicateScheduledRows(
        List<FlowRecord> records,
        IReadOnlyList<DateTime> settlementDates,
        IReadOnlyDictionary<DateTime, FlowRecord> interestRows,
        IReadOnlyDictionary<DateTime, FlowRecord> taxRows)
    {
        var retained = interestRows.Values.Concat(taxRows.Values).ToHashSet();
        var dates = settlementDates.ToHashSet();
        return records.RemoveAll(record =>
            !retained.Contains(record)
            && record.AccountTime.HasValue
            && dates.Contains(record.AccountTime.Value.Date)
            && FlowGeneratedRowKinds.IsSystemInterest(record)) > 0;
    }

    private static bool HasExpectedKind(FlowRecord record, string rowKind)
    {
        return rowKind == FlowGeneratedRowKinds.Interest
            ? FlowGeneratedRowKinds.IsInterest(record)
            : FlowGeneratedRowKinds.IsInterestTax(record);
    }

    private static string? RecognizeLegacyRowKind(FlowRecord record)
    {
        var values = new[]
        {
            record.ProductName,
            record.ProductBrief,
            record.Remark,
            record.TradeExplain,
            record.Usage
        };
        if (values.Any(value => value?.Contains(InterestTaxText, StringComparison.Ordinal) == true))
        {
            return FlowGeneratedRowKinds.InterestTax;
        }

        return values.Any(value => value?.Contains(InterestText, StringComparison.Ordinal) == true)
            ? FlowGeneratedRowKinds.Interest
            : null;
    }

    private static FlowRecord CreateDefaultInterestRecord(
        Bank bank,
        BankUser bankUser,
        BankInterestSetting setting,
        DateTime accountTime,
        string rowKind)
    {
        var isTax = rowKind == FlowGeneratedRowKinds.InterestTax;
        var record = new FlowRecord
        {
            BankId = bank.Id,
            BankUserId = bankUser.Id,
            AccountTime = accountTime,
            MoveFlag = false,
            Account = ResolveAccount(bank, bankUser),
            ProductBrief = isTax ? InterestTaxText : InterestText,
            ProductName = isTax ? InterestTaxText : InterestText,
            ProductType = "活期",
            Usage = isTax ? InterestTaxText : InterestText,
            TradeExplain = isTax ? InterestTaxText : InterestText,
            Remark = isTax ? InterestTaxText : PersonalCurrentInterestRemark,
            SerialNum = isTax ? "0000000002" : "0000000001",
            LogNum = "0000000001",
            Currency = FirstNotBlank(bankUser.Currency, "RMB"),
            TradeCurrency = FirstNotBlank(bankUser.Currency, "RMB")
        };

        ApplyConfiguredFields(record, setting);
        if (isTax)
        {
            ConvertLabelsToInterestTax(record);
        }

        FlowGeneratedRowKinds.SetKind(record, rowKind);
        return record;
    }

    private static void ApplyConfiguredFields(FlowRecord record, BankInterestSetting setting)
    {
        foreach (var field in setting.Fields.Where(field =>
                     !string.IsNullOrWhiteSpace(field.Field)
                     && !string.IsNullOrWhiteSpace(field.Value)
                     && !IsProtectedSettingField(field.Field)))
        {
            SetRecordValue(record, field.Field, field.Value);
        }
    }

    private static bool IsProtectedSettingField(string field)
    {
        var normalized = NormalizeIndexerField(field);
        return ProtectedSettingFields.Contains(normalized);
    }

    private static void SetRecordValue(FlowRecord record, string field, string value)
    {
        var normalized = NormalizeIndexerField(field);
        var property = typeof(FlowRecord).GetProperty(normalized, BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite == true && property.PropertyType == typeof(string))
        {
            property.SetValue(record, value);
            return;
        }

        record[normalized] = value;
    }

    private static string NormalizeIndexerField(string field)
    {
        var trimmed = field.Trim();
        return trimmed.StartsWith('[')
            && trimmed.EndsWith(']')
            && trimmed.Length > 2
                ? trimmed[1..^1]
                : trimmed;
    }

    private static void ConvertLabelsToInterestTax(FlowRecord record)
    {
        record.ProductName = ConvertLabelToInterestTax(record.ProductName);
        record.ProductBrief = ConvertLabelToInterestTax(record.ProductBrief);
        record.ProductType = ConvertLabelToInterestTax(record.ProductType);
        record.Usage = ConvertLabelToInterestTax(record.Usage);
        record.TradeExplain = ConvertLabelToInterestTax(record.TradeExplain);
        record.Remark = ConvertLabelToInterestTax(record.Remark);
    }

    private static string ConvertLabelToInterestTax(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.Contains(InterestTaxText, StringComparison.Ordinal))
        {
            return value;
        }

        if (value.Contains(InterestText, StringComparison.Ordinal))
        {
            return value.Replace(InterestText, InterestTaxText, StringComparison.Ordinal);
        }

        return string.Equals(value, "利息", StringComparison.Ordinal) ? InterestTaxText : value;
    }

    private static SystemAmountState CaptureSystemAmountState(FlowRecord record)
    {
        return new SystemAmountState(
            record.TradeMoney,
            record.CreditAmount,
            record.DebitAmount,
            record.IncomeAttribute,
            record.IncomeFlag);
    }

    private static void SetSystemAmount(FlowRecord record, double amount)
    {
        amount = RoundMoney(Math.Max(0, amount));
        record.TradeMoney = amount;
        record.CreditAmount = amount > 0 ? amount : null;
        record.DebitAmount = null;
        record.IncomeAttribute = "收入";
        record.IncomeFlag = "C";
    }

    private static IEnumerable<DateTime> EnumerateSettlementDates(
        DateTime start,
        DateTime end,
        InterestConfiguration configuration)
    {
        var cursor = new DateTime(start.Year, start.Month, 1);
        var finalMonth = new DateTime(end.Year, end.Month, 1);
        while (cursor <= finalMonth)
        {
            if (configuration.Months.Contains(cursor.Month))
            {
                var day = Math.Min(configuration.SettlementDay, DateTime.DaysInMonth(cursor.Year, cursor.Month));
                var date = new DateTime(cursor.Year, cursor.Month, day);
                if (date >= start.Date && date <= end.Date)
                {
                    yield return date;
                }
            }

            cursor = cursor.AddMonths(1);
        }
    }

    private static DateTime CreateSettlementTime(
        DateTime date,
        int startHour,
        int endHour,
        long bankId,
        long bankUserId,
        Random? random)
    {
        if (random is not null)
        {
            return date.AddHours(random.Next(startHour, endHour + 1))
                .AddMinutes(random.Next(0, 60))
                .AddSeconds(random.Next(0, 60));
        }

        var value = unchecked(
            (date.Year * 397L)
            ^ (date.Month * 31L)
            ^ (date.Day * 17L)
            ^ (bankId * 13L)
            ^ bankUserId);
        var normalized = (ulong)(value == long.MinValue ? long.MaxValue : Math.Abs(value));
        var hour = startHour + (int)(normalized % (ulong)(endHour - startHour + 1));
        var minute = (int)((normalized / 29UL) % 60UL);
        var second = (int)((normalized / 1741UL) % 60UL);
        return date.AddHours(hour).AddMinutes(minute).AddSeconds(second);
    }

    private static bool TryParseConfiguration(
        BankInterestSetting? setting,
        out InterestConfiguration configuration)
    {
        configuration = default;
        if (setting is null
            || !TryParseInt(setting.SettlementDay, out var day)
            || day is < 1 or > 31
            || !TryParseDouble(setting.RatePercent, out var ratePercent)
            || ratePercent <= 0)
        {
            return false;
        }

        var months = setting.Months
            .Split([',', ';', '，', '；', ':', '：', '|', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => TryParseInt(value, out var month) ? month : 0)
            .Where(month => month is >= 1 and <= 12)
            .Distinct()
            .ToHashSet();
        if (months.Count == 0)
        {
            return false;
        }

        var startHour = TryParseInt(setting.StartTime, out var parsedStartHour) ? parsedStartHour : 0;
        var endHour = TryParseInt(setting.EndTime, out var parsedEndHour) ? parsedEndHour : 23;
        startHour = Math.Clamp(startHour, 0, 23);
        endHour = Math.Clamp(endHour, 0, 23);
        if (endHour < startHour)
        {
            (startHour, endHour) = (endHour, startHour);
        }

        configuration = new InterestConfiguration(day, months, startHour, endHour, ratePercent);
        return true;
    }

    private static bool TryParseInt(string? value, out int number)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            || int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out number);
    }

    private static bool TryParseDouble(string? value, out double number)
    {
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out number)
            || double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out number);
    }

    private static void NormalizeRange(ref DateTime start, ref DateTime end)
    {
        if (end < start)
        {
            (start, end) = (end.Date, start.Date.AddDays(1).AddTicks(-1));
            return;
        }

        if (end.TimeOfDay == TimeSpan.Zero)
        {
            end = end.Date.AddDays(1).AddTicks(-1);
        }
    }

    private static bool ShouldAppendInterestTaxRecord(Bank bank)
    {
        return bank.Name.Contains("农行", StringComparison.Ordinal)
            || bank.Name.Contains("农业", StringComparison.Ordinal)
            || bank.Type.Contains("农行", StringComparison.Ordinal)
            || bank.Type.Contains("农业", StringComparison.Ordinal);
    }

    private static string ResolveAccount(Bank bank, BankUser bankUser)
    {
        var accountColumn = bank.FlowColumns.FirstOrDefault(column =>
            string.Equals(column.Field, nameof(FlowRecord.Account), StringComparison.Ordinal));
        var columnName = accountColumn?.Name ?? string.Empty;
        var preferCard = columnName.Contains("卡号", StringComparison.Ordinal)
            && !columnName.Contains("账号", StringComparison.Ordinal)
            && !columnName.Contains("帐户", StringComparison.Ordinal);
        return preferCard
            ? FirstNotBlank(bankUser.CardNo, bankUser.AccountNo)
            : FirstNotBlank(bankUser.AccountNo, bankUser.CardNo);
    }

    private static string FirstNotBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static double RoundMoney(double value)
    {
        return Math.Round(value, 2);
    }

    private readonly record struct InterestConfiguration(
        int SettlementDay,
        HashSet<int> Months,
        int StartHour,
        int EndHour,
        double RatePercent);

    private readonly record struct SystemAmountState(
        double? TradeMoney,
        double? CreditAmount,
        double? DebitAmount,
        string IncomeAttribute,
        string IncomeFlag);
}
