using System.Globalization;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private const string CentTailBaseline = "__CentTailBaselineCents";
    private const string CentTailLast = "__CentTailLastCents";
    private const int CentTailMaximumMoves = 8192;

    private static void ValidateGeneratedCentTailRules(FlowAutoGenerationRequest request)
    {
        var activeConstItems = request.ConstItems.Where(rule => !string.IsNullOrWhiteSpace(rule.FixDay));
        foreach (var rule in request.References.Cast<FlowRuleBase>().Concat(activeConstItems).Where(rule => rule.IsCheck && rule.FloutLength == 2))
        {
            if (TryGetRuleAmountBounds(rule, out _)) continue;
            var source = rule is GenerateConstRule ? "固定日期项目" : "参照明细";
            throw new InvalidOperationException(
                $"{source}第 {rule.Index} 行金额范围 {rule.MinMoney:0.00}～{rule.MaxMoney:0.00} 与尾数规则冲突：" +
                "普通生成流水严格禁止 .01～.09，且两位小数的末位不能为 0。请调整该行金额范围、固定金额或小数位。");
        }
    }

    private static FlowAutoGenerationResult EnsureGeneratedCentTailResult(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
    {
        foreach (var row in result.Records)
        {
            if (row.IsDocumentImported || IsSystemInterestRecord(row) || row.BankId != request.Bank.Id || row.BankUserId != request.BankUser.Id) continue;
            var source = row.ExtraFields.GetValueOrDefault(SourceKindField);
            // Persisted/manual rows have no generation metadata. ID-zero rows
            // also cover newly synthesized ordinary rows without a source rule.
            if (row.Id != 0 && source is not (ReferenceSourceKind or ConstSourceKind)) continue;
            var money = row.TradeMoney ?? 0;
            if (!double.IsFinite(money)) continue;
            var tail = Math.Abs(Math.Round(money * 100d, MidpointRounding.AwayFromZero) % 100d);
            if (tail is < 1 or > 9) continue;
            var item = source == ConstSourceKind ? "固定日期项目" : "参照明细";
            var index = row.ExtraFields.GetValueOrDefault(SourceIndexField) ?? row.Index.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException(
                $"生成结果未通过严格尾数校验：{item}第 {index} 行金额 {money:0.00} 命中禁止的 .01～.09 尾数。" +
                "当前金额范围、固定金额或收支目标无法同时满足本次校验，请调整配置后重新生成；不会返回含禁用尾数的生成结果。");
        }
        return result;
    }

    private static FlowAutoGenerationResult CompleteCorrectedGeneratedResult(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source, FlowAutoGenerationResult corrected)
    {
        // Revisit low-tail cleanup only if a repair changed reference amounts.
        // Chronological spacing is checked after time-only repairs as well.
        var amounts = new Dictionary<(string Source, double Amount), int>();
        foreach (var row in source.Records.Where(IsGeneratedReferenceRecord))
        {
            var key = (row.ExtraFields.GetValueOrDefault(SourceIndexField) ?? string.Empty, RoundMoney(row.TradeMoney ?? 0));
            amounts[key] = amounts.GetValueOrDefault(key) + 1;
        }
        foreach (var row in corrected.Records.Where(IsGeneratedReferenceRecord))
        {
            var key = (row.ExtraFields.GetValueOrDefault(SourceIndexField) ?? string.Empty, RoundMoney(row.TradeMoney ?? 0));
            if (!amounts.TryGetValue(key, out var count)) return CompleteGeneratedResult(request, corrected);
            if (count == 1) amounts.Remove(key); else amounts[key] = count - 1;
        }
        return amounts.Count == 0
            ? EnsureGeneratedCentTailResult(request, CompleteGeneratedTailSpacing(request, CompleteAlipayResult(request, corrected), preserveAmounts: true))
            : CompleteGeneratedResult(request, corrected);
    }

    private static FlowAutoGenerationResult CompleteGeneratedResult(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
    {
        result = EnsureGeneratedCentTailResult(request, CompleteGeneratedResultCore(request, result));
        return EnsureGeneratedCentTailResult(request, CompleteGeneratedTailSpacing(request, result));
    }

    private static FlowAutoGenerationResult CompleteGeneratedResultCore(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
    {
        // First establish the final serial order, including any existing Alipay
        // date corrections. The optional amount pass must preserve this baseline.
        result = CompleteAlipayResult(request, result);
        var entries = CreateCentTailEntries(request, result.Records);
        if (!entries.GroupBy(entry => entry.Group).Any(CentTailGroupNeedsAdjustment)) return result;

        if (TryCompleteCentTails(request, result, entries, sameDayOnly: false, out var adjusted)) return adjusted;
        entries = CreateCentTailEntries(request, result.Records);
        if (TryCompleteCentTails(request, result, entries, sameDayOnly: false, out adjusted, preferSmallMoves: true)) return adjusted;
        // Keep safe individual moves when a batch crosses an interest rounding
        // boundary. One rejected move must not discard every other valid pair.
        entries = CreateCentTailEntries(request, result.Records);
        if (TryCompleteCentTails(request, result, entries, sameDayOnly: false, out adjusted, checkEachMove: true, preferSmallMoves: true)) return adjusted;
        // A same-day retry preserves the daily ledger even when the interest
        // configuration cannot verify a cross-day move. The outer guard rejects
        // any ordinary low tail that the bounded cleanup cannot safely remove.
        entries = CreateCentTailEntries(request, result.Records);
        return TryCompleteCentTails(request, result, entries, sameDayOnly: true, out adjusted) ? adjusted : result;
    }

    private static bool TryCompleteCentTails(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source,
        List<CentTailEntry> entries, bool sameDayOnly, out FlowAutoGenerationResult adjusted, bool checkEachMove = false, bool preferSmallMoves = false)
    {
        adjusted = source;
        var moves = 0;
        var interestChecks = 0;
        var working = checkEachMove ? source.Records.Select(row => row.Clone()).ToList() : null;
        var balances = new CentTailBalanceHeadroom(source.Records.Select(row => row.Balance ?? source.OpeningBalance).ToArray());
        var balanceFloor = Math.Max(0, source.MinimumBalance);
        foreach (var group in entries.GroupBy(entry => entry.Group))
        {
            if (moves >= CentTailMaximumMoves) break;
            if (!CentTailGroupNeedsAdjustment(group)) continue;
            var rows = group.OrderBy(entry => entry.Index).ToArray();
            var usage = new int[100];
            foreach (var row in rows) usage[row.Tail]++;
            var regularTailLimit = Math.Max(3, (int)Math.Ceiling(rows.Length * 0.04d));
            var lowCount = usage.Skip(1).Take(9).Sum();
            var targets = rows.Select((row, index) => (Row: row, Position: index))
                .Where(item => item.Row.Tail is >= 1 and <= 9)
                .OrderBy(item => item.Row.Tail == 1 ? 0 : 1)
                .ThenBy(item => item.Row.Sign > 0 ? item.Row.Index : -item.Row.Index).ToArray();
            foreach (var (target, position) in targets)
            {
                if (moves >= CentTailMaximumMoves || lowCount == 0) break;
                if (target.Tail is < 1 or > 9) continue;
                var targetChecks = 0;
                var partners = GetCentTailPartners(rows, position, sameDayOnly);
                var regularTails = Enumerable.Range(11, 79).Where(tail => tail % 10 != 0);
                var tails = preferSmallMoves
                    ? regularTails.OrderBy(tail => Math.Min(
                        target.Accepts(target.Cents + tail - target.Tail) ? Math.Abs(tail - target.Tail) : int.MaxValue,
                        target.Accepts(target.Cents + tail - target.Tail - 100) ? Math.Abs(tail - target.Tail - 100) : int.MaxValue)).ThenBy(tail => usage[tail])
                    : regularTails.OrderBy(tail => usage[tail]).ThenBy(tail => tail - target.Tail);
                var applied = false;
                foreach (var tail in tails)
                {
                    if (targetChecks >= 32) break;
                    if (usage[tail] >= regularTailLimit) continue;
                    // Both directions stay within one yuan. Borrowing from the
                    // integer part is allowed only inside all original bounds.
                    var forward = tail - target.Tail;
                    foreach (var delta in new[] { forward, forward - 100 })
                    {
                        if (targetChecks >= 32) break;
                        var nextTarget = target.Cents + delta;
                        if (!target.Accepts(nextTarget)) continue;
                        foreach (var partner in partners)
                        {
                            if (targetChecks >= 32) break;
                            var first = Math.Min(target.Index, partner.Index);
                            var last = Math.Max(target.Index, partner.Index) - 1;
                            var balanceChange = target.Sign * delta * (target.Index < partner.Index ? 1 : -1) / 100d;
                            // Positive balances provide room for either direction.
                            // Protect the original minimum and never create a new
                            // negative balance when using that room.
                            if (balanceChange < 0 && balances.Minimum(first, last) + balanceChange < balanceFloor - 0.000001d) continue;
                            var nextPartner = partner.Cents - delta;
                            var partnerTail = (int)(nextPartner % 100);
                            if (!partner.Accepts(nextPartner) || partnerTail < 11 || partnerTail >= 90 || partnerTail % 10 == 0) continue;
                            // Bound both resulting tails. Balancing the target
                            // alone can repeatedly leave its donors on one tail.
                            var partnerTailCount = usage[partnerTail] + 1 + (tail == partnerTail ? 1 : 0)
                                - (partner.Tail == partnerTail ? 1 : 0);
                            if (partnerTailCount > regularTailLimit) continue;
                            var oldTargetTail = target.Tail;
                            var oldPartnerTail = partner.Tail;
                            if (checkEachMove)
                            {
                                var sameDay = target.Time.Date == partner.Time.Date;
                                if (!sameDay && interestChecks >= 2048) continue;
                                var oldTarget = target.Cents; var oldPartner = partner.Cents;
                                target.Cents = nextTarget; partner.Cents = nextPartner;
                                ApplySignedAmount(working![target.Index], target.Sign, nextTarget / 100d);
                                ApplySignedAmount(working[partner.Index], partner.Sign, nextPartner / 100d);
                                if (!sameDay) { interestChecks++; targetChecks++; }
                                var allChanges = entries.Where(entry => entry.Cents != entry.OriginalCents).ToArray();
                                if (!CentTailInterestUnchanged(request, source, working, allChanges))
                                {
                                    target.Cents = oldTarget; partner.Cents = oldPartner;
                                    ApplySignedAmount(working[target.Index], target.Sign, oldTarget / 100d);
                                    ApplySignedAmount(working[partner.Index], partner.Sign, oldPartner / 100d);
                                    continue;
                                }
                            }
                            usage[oldTargetTail]--; usage[oldPartnerTail]--;
                            target.Cents = nextTarget; partner.Cents = nextPartner;
                            balances.Add(first, last, balanceChange);
                            usage[target.Tail]++; usage[partner.Tail]++;
                            lowCount -= (oldTargetTail is >= 1 and <= 9 ? 1 : 0) + (oldPartnerTail is >= 1 and <= 9 ? 1 : 0);
                            moves++; applied = true; break;
                        }
                        if (applied) break;
                    }
                    if (applied) break;
                }
            }
        }

        var changed = entries.Where(entry => entry.Cents != entry.OriginalCents).ToArray();
        if (changed.Length == 0) return false;
        var records = source.Records.Select(row => row.Clone()).ToList();
        foreach (var entry in changed)
        {
            var row = records[entry.Index];
            ApplySignedAmount(row, entry.Sign, entry.Cents / 100d);
        }

        if (!CentTailInterestUnchanged(request, source, records, changed)) return false;
        foreach (var entry in entries.Where(entry => entry.Cents != entry.OriginalCents))
        {
            records[entry.Index].ExtraFields[CentTailBaseline] = entry.BaselineCents.ToString(CultureInfo.InvariantCulture);
            records[entry.Index].ExtraFields[CentTailLast] = entry.Cents.ToString(CultureInfo.InvariantCulture);
        }
        ApplyBalances(records, source.OpeningBalance);
        var proposed = CreateCentTailResult(request, source, records);
        // A deferred-payment serial may contain the amount in cents. Refresh it
        // after the amount pass, then validate its equal-timestamp sort order too.
        proposed = CompleteAlipayResult(request, proposed);
        if (SumIncome(proposed.Records) != SumIncome(source.Records)
            || SumExpense(proposed.Records) != SumExpense(source.Records)
            || Math.Abs(proposed.FinalBalance - source.FinalBalance) > 0.009d
            || proposed.MinimumBalance < source.MinimumBalance - 0.009d) return false;
        adjusted = proposed;
        return true;
    }

    private static bool CentTailInterestUnchanged(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source,
        List<FlowRecord> proposed, IReadOnlyList<CentTailEntry> changed)
    {
        if (!BankInterestPolicy.ShouldCalculate(request.Bank, request.BankUser)) return true;
        var dailyDeltas = changed.GroupBy(entry => entry.Time.Date)
            .Select(group => group.Sum(entry => entry.Sign * (entry.Cents - entry.OriginalCents)));
        if (dailyDeltas.All(delta => delta == 0)) return true;
        if (request.InterestSetting is null) return !source.Records.Any(IsSystemInterestRecord);

        // Recalculate on an isolated copy. Accepted moves keep every original
        // interest/tax row (including user-configured display fields) unchanged.
        var verification = proposed.Select(row => row.Clone()).ToList();
        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start) (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        BankInterestCalculationResult calculation;
        try
        {
            calculation = RecalculateInterestRecords(verification, request, source.OpeningBalance, start, end, new Random(0x43E17));
        }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
        var oldInterest = source.Records.Where(IsSystemInterestRecord)
            .Select(row => (row.GeneratedRowKind, row.AccountTime, Money: RoundMoney(row.TradeMoney ?? 0)))
            .OrderBy(item => item.AccountTime).ThenBy(item => item.GeneratedRowKind).ToArray();
        if (oldInterest.Length > 0 && calculation.InterestRecordCount == 0) return false;
        var newInterest = verification.Where(IsSystemInterestRecord)
            .Select(row => (row.GeneratedRowKind, row.AccountTime, Money: RoundMoney(row.TradeMoney ?? 0)))
            .OrderBy(item => item.AccountTime).ThenBy(item => item.GeneratedRowKind).ToArray();
        return oldInterest.SequenceEqual(newInterest);
    }

    private static FlowAutoGenerationResult CreateCentTailResult(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source, List<FlowRecord> records)
    {
        var minimum = GetMinimumBalance(records, source.OpeningBalance);
        var final = records.LastOrDefault()?.Balance ?? source.OpeningBalance;
        return new FlowAutoGenerationResult
        {
            Records = records, OpeningBalance = source.OpeningBalance,
            IncomeTotal = source.IncomeTotal, ExpenseTotal = source.ExpenseTotal,
            FinalBalance = RoundMoney(final), MinimumBalance = minimum,
            RequiresOpeningBalanceCorrection = minimum < -0.009d
                || IsFinalBalanceOutsideTolerance(final, source.OpeningBalance, request.Config.LastMoney),
            RequiredOpeningBalance = minimum < -0.009d ? RoundMoney(source.OpeningBalance - minimum) : source.OpeningBalance
        };
    }

    private static bool CentTailGroupNeedsAdjustment(IEnumerable<CentTailEntry> group)
    {
        return group.Any(entry => entry.Tail is >= 1 and <= 9);
    }

    private static List<CentTailEntry> CreateCentTailEntries(
        FlowAutoGenerationRequest request, IReadOnlyList<FlowRecord> records)
    {
        var result = new List<CentTailEntry>();
        var budgetKeys = new Dictionary<DateTime, string>();
        for (var i = 0; i < records.Count; i++)
        {
            var row = records[i];
            if (row.IsDocumentImported || IsSystemInterestRecord(row) || !IsGeneratedReferenceRecord(row)
                || row.BankId != request.Bank.Id || row.BankUserId != request.BankUser.Id
                || row.AccountTime is not { } time || !double.IsFinite(row.TradeMoney ?? double.NaN)) continue;
            var bounds = GetRecordAmountBounds(row);
            if (bounds.Precision != 2 || Math.Abs(bounds.Unit - 0.01d) > 0.0000001d
                || !double.IsFinite(bounds.Max) || bounds.Max * 100d >= long.MaxValue / 2d) continue;
            var min = bounds.Min; var max = bounds.Max;
            if (row.ExtraFields.TryGetValue(CcbStratumMin, out var lower) && TryParseDouble(lower, out var bandMin)) min = Math.Max(min, bandMin);
            if (row.ExtraFields.TryGetValue(CcbStratumMax, out var upper) && TryParseDouble(upper, out var bandMax)) max = Math.Min(max, bandMax);
            var minimum = (long)Math.Ceiling(min * 100d - 0.000001d);
            var maximum = (long)Math.Floor(max * 100d + 0.000001d);
            if (maximum <= minimum || !CentTailHasRegularValue(minimum, maximum)) continue;
            var cents = (long)Math.Round(Math.Abs(row.TradeMoney!.Value) * 100d, MidpointRounding.AwayFromZero);
            if (cents < minimum || cents > maximum || cents % 10 == 0) continue;
            var baseline = cents;
            if (row.ExtraFields.TryGetValue(CentTailLast, out var last) && long.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastCents)
                && lastCents == cents && row.ExtraFields.TryGetValue(CentTailBaseline, out var original)
                && long.TryParse(original, NumberStyles.Integer, CultureInfo.InvariantCulture, out var originalCents)
                && originalCents > 0 && originalCents < long.MaxValue / 2 && Math.Abs(originalCents - cents) <= 99) baseline = originalCents;
            var sign = row.TradeMoney > 0 ? 1 : -1;
            var cacheKey = request.Config.SelectIndex == 2 ? time : time.Date;
            if (!budgetKeys.TryGetValue(cacheKey, out var key))
            {
                key = time.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                if (request.Config.SelectIndex == 2)
                    key += ":" + string.Join(',', request.Config.MonthGenData.Select((period, index) => (period, index))
                        .Where(item => time >= item.period.StartTime && time <= NormalizeEndDate(item.period.EndTime)).Select(item => item.index));
                budgetKeys[cacheKey] = key;
            }
            // Preserve automatic monthly income caps too. Expense budgets may
            // span months only in total-allocation mode, as before this pass.
            var periodKey = request.Config.SelectIndex == 0 && sign < 0 ? string.Empty : key;
            result.Add(new CentTailEntry(i, time, sign, row.Account, periodKey, cents, baseline, minimum, maximum));
        }
        return result;
    }

    private static bool CentTailHasRegularValue(long minimum, long maximum)
    {
        for (var offset = 0; offset < 100 && minimum + offset <= maximum; offset++)
        {
            var tail = (minimum + offset) % 100;
            if (tail is >= 11 and <= 99 && tail % 10 != 0) return true;
        }
        return false;
    }

    private static CentTailEntry[] GetCentTailPartners(CentTailEntry[] rows, int position, bool sameDayOnly)
    {
        var positions = new HashSet<int>();
        for (var offset = 1; offset <= 32; offset++)
        {
            if (position - offset >= 0) positions.Add(position - offset);
            if (position + offset < rows.Length) positions.Add(position + offset);
        }
        for (var sample = 0; sample < 64; sample++) positions.Add((int)((long)sample * (rows.Length - 1) / 63));
        var time = rows[position].Time;
        return positions.Where(index => index != position).Select(index => rows[index])
            .Where(entry => !sameDayOnly || entry.Time.Date == time.Date)
            .OrderBy(entry => entry.Time.Date == time.Date ? 0 : 1)
            .ThenBy(entry => entry.Time.Year == time.Year && entry.Time.Month == time.Month ? 0 : 1)
            .ThenBy(entry => Math.Abs((entry.Time - time).Ticks)).ThenBy(entry => entry.Index).ToArray();
    }

    private sealed class CentTailEntry(
        int index, DateTime time, int sign, string account, string period,
        long cents, long baseline, long minimum, long maximum)
    {
        public int Index { get; } = index;
        public DateTime Time { get; } = time;
        public int Sign { get; } = sign;
        public (int Sign, string Account, string Period) Group { get; } = (sign, account, period);
        public long OriginalCents { get; } = cents;
        public long BaselineCents { get; } = baseline;
        public long Cents { get; set; } = cents;
        public int Tail => (int)(Cents % 100);
        public bool Accepts(long value) => value >= minimum && value <= maximum && value > 0
            && value % 10 != 0 && Math.Abs(value - BaselineCents) <= 99;
    }

    // Range minima and cumulative deltas for intermediate ledger balances.
    // Updates stay logarithmic even for statements containing many thousands of rows.
    private sealed class CentTailBalanceHeadroom
    {
        private readonly int count;
        private readonly double[] minima;
        private readonly double[] lazy;

        public CentTailBalanceHeadroom(double[] values)
        {
            count = values.Length; minima = new double[Math.Max(1, count * 4)]; lazy = new double[minima.Length];
            if (count > 0) Build(1, 0, count - 1, values);
        }
        public double Minimum(int first, int last) => Visit(1, 0, count - 1, first, last, 0, false);
        public void Add(int first, int last, double delta) => Visit(1, 0, count - 1, first, last, delta, true);
        private void Build(int node, int left, int right, double[] values)
        {
            if (left == right) { minima[node] = values[left]; return; }
            var mid = (left + right) / 2;
            Build(node * 2, left, mid, values); Build(node * 2 + 1, mid + 1, right, values);
            minima[node] = Math.Min(minima[node * 2], minima[node * 2 + 1]);
        }
        private double Visit(int node, int left, int right, int first, int last, double delta, bool update)
        {
            if (last < left || first > right) return double.PositiveInfinity;
            if (first <= left && right <= last)
            {
                if (update) { minima[node] += delta; lazy[node] += delta; }
                return minima[node];
            }
            if (lazy[node] != 0)
            {
                minima[node * 2] += lazy[node]; lazy[node * 2] += lazy[node];
                minima[node * 2 + 1] += lazy[node]; lazy[node * 2 + 1] += lazy[node]; lazy[node] = 0;
            }
            var mid = (left + right) / 2;
            var a = Visit(node * 2, left, mid, first, last, delta, update);
            var b = Visit(node * 2 + 1, mid + 1, right, first, last, delta, update);
            if (update) minima[node] = Math.Min(minima[node * 2], minima[node * 2 + 1]);
            return Math.Min(a, b);
        }
    }
}
