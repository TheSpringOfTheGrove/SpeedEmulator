using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private const string DateDistributionFrame = "__GeneratedDateDistributionFrame";
    private sealed record DistributedDateEntry(FlowRecord Row, CashflowWindow Window, DateTime[] Days);

    private static FlowAutoGenerationResult CompleteGeneratedDateCorrection(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
    {
        // Funding can change chronological neighbors. Repair concentrated
        // dates before considering amount changes, then verify the final order.
        result = CompleteGeneratedDatePhase(request, result);
        return CompleteGeneratedDatePhase(request, CompleteGeneratedAdjacentAmounts(request, result));
    }

    private static FlowAutoGenerationResult CompleteGeneratedDatePhase(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult result, bool generated = false)
    {
        if (!generated && !result.Records.Any(row => row.ExtraFields.GetValueOrDefault(DateDistributionFrame) == "1")) return result;
        result = CompleteGeneratedDateDistribution(request, result);
        foreach (var row in result.Records.Where(row => row.Id == 0 && !row.IsDocumentImported && IsGeneratedReferenceRecord(row)))
            row.ExtraFields[DateDistributionFrame] = "1";
        return result;
    }

    // Distribution is checked after amount/funding repair. It must not rewrite
    // healthy samples, fixed dates, imports, source amount bounds or monthly budgets.
    private static FlowAutoGenerationResult CompleteGeneratedDateDistribution(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source)
    {
        var start = request.Config.StartTime.Date; var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start) (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        var groups = DateDistributionGroups(request, source.Records, start, end);
        if (!groups.Any(group => HasDatePileup(group))) return source;

        var rows = source.Records.Select(row => row.Clone()).ToList();
        var ledger = new CashflowLedger(source.OpeningBalance);
        // Do not spend generated interest that may disappear after dates move.
        foreach (var row in rows.Where(row => row.IsDocumentImported || !IsSystemInterestRecord(row)))
            if (row.AccountTime is { } time) ledger.Add(time, row.TradeMoney ?? 0);
        if (ledger.Headroom(DateTime.MinValue) < -.009d)
        {
            TraceReferenceNaturalGeneration(request, "date-distribution-interest-required", source.Records, 0, source.OpeningBalance);
            return source;
        }
        var used = rows.Where(row => row.AccountTime.HasValue).Select(row => row.AccountTime!.Value).ToHashSet();
        var usage = rows.Where(row => row.AccountTime.HasValue).GroupBy(row => row.AccountTime!.Value.Date)
            .ToDictionary(group => group.Key, group => group.Count());
        var random = new Random(CreateRunSeed(request, start, end, rows.Count ^ 0x0DA7E));
        var moved = false;
        foreach (var group in DateDistributionGroups(request, rows, start, end).Where(HasDatePileup))
        {
            var counts = group.GroupBy(entry => entry.Row.AccountTime!.Value.Date)
                .ToDictionary(items => items.Key, items => items.Count());
            var targets = DateDistributionTargets(group);
            foreach (var entry in group.OrderBy(entry => Math.Abs(entry.Row.TradeMoney ?? 0)))
            {
                var row = entry.Row; var original = row.AccountTime!.Value;
                var target = targets[DateDistributionDayMask(entry)];
                if (counts[original.Date] <= target) continue;
                var amount = row.TradeMoney!.Value;
                foreach (var day in entry.Days.Where(day => day != original.Date && counts.GetValueOrDefault(day) < target)
                             .OrderBy(day => counts.GetValueOrDefault(day)).ThenBy(day => usage.GetValueOrDefault(day)).ThenBy(_ => random.Next()))
                {
                    if (!TryBalancedCashflowTime(request, entry.Window, MaxDateTime(entry.Window.Start, day),
                            MinDateTime(entry.Window.End, day.AddDays(1).AddTicks(-1)), random, used, usage, out var time)
                        || !SameExplicitBudgetPeriods(request, original, time)) continue;
                    ledger.Add(original, -amount); ledger.Add(time, amount);
                    if (ledger.Headroom(DateTime.MinValue) < -.009d)
                    {
                        ledger.Add(time, -amount); ledger.Add(original, amount); continue;
                    }
                    MoveCashflowRow(request, row, time, used);
                    counts[original.Date]--; counts[time.Date] = counts.GetValueOrDefault(time.Date) + 1;
                    usage[original.Date] = Math.Max(0, usage.GetValueOrDefault(original.Date) - 1);
                    usage[time.Date] = usage.GetValueOrDefault(time.Date) + 1;
                    moved = true; break;
                }
            }
        }
        if (!moved)
        {
            TraceReferenceNaturalGeneration(request, "date-distribution-cash-required", source.Records, 0, source.OpeningBalance);
            return source;
        }
        if (BankInterestPolicy.ShouldCalculate(request.Bank, request.BankUser))
            BankInterestCalculationService.RecalculatePreservingImported(request.Bank, request.BankUser, request.InterestSetting,
                rows, source.OpeningBalance, start, end,
                (time, kind) => CreateInterestRecord(request, request.InterestSetting!, time, 0, kind,
                    kind == InterestTaxRowKind ? InterestTaxText : InterestText, rows), random);
        var result = CreateAutomaticBalanceResult(rows, source.OpeningBalance);
        if (result.MinimumBalance < -.009d) return source;
        // Moving a row changes chronological neighbors. Preserve amounts even
        // during this check: only permute existing times inside the same month.
        result = CompleteAlipayResult(request, result);
        if (HasDateMoveAmountConflict(request, result))
        {
            result = PermuteAdjacentAmountTimes(request, result, preserveMonths: true);
            if (HasDateMoveAmountConflict(request, result))
            {
                TraceReferenceNaturalGeneration(request, "date-distribution-pattern-protected", source.Records, 0, source.OpeningBalance);
                return source;
            }
        }
        result = StampGeneratedAmountPatterns(request, result);
        result = CompleteReferencePriorityFunding(request, result);
        TraceReferenceNaturalGeneration(request, "date-distribution-complete", result.Records, 0, result.OpeningBalance);
        return result;
    }

    private static bool HasDateMoveAmountConflict(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
    {
        var entries = result.Records.Select((row, index) => CreateAdjacentAmountEntry(request, row, index)).ToArray();
        var patterns = new AmountPatternIndex(entries);
        var amounts = result.Records.Select(row => AdjacentAmountCents(row.TradeMoney ?? 0)).ToArray();
        return Enumerable.Range(1, Math.Max(0, amounts.Length - 1)).Any(index => patterns.Conflict(amounts, index));
    }

    private static List<List<DistributedDateEntry>> DateDistributionGroups(FlowAutoGenerationRequest request,
        List<FlowRecord> rows, DateTime start, DateTime end)
    {
        var entries = new List<DistributedDateEntry>();
        var cache = new Dictionary<(FlowRuleBase Rule, DateTime Start, DateTime End), DateTime[]>();
        foreach (var row in rows)
        {
            if (row.AccountTime is not { } time || row.IsDocumentImported || row.Id != 0
                || IsSystemInterestRecord(row) || !IsGeneratedReferenceRecord(row) || Math.Abs(row.TradeMoney ?? 0) < .009d) continue;
            var window = ResolveCashflowWindow(request, row, start, end);
            if (window.Rule is not GenerateReferenceRule rule || !window.Mutable) continue;
            var month = BudgetMonth(time);
            window = window with { Start = MaxDateTime(window.Start, month), End = MinDateTime(window.End, month.AddMonths(1).AddTicks(-1)) };
            if (request.Config.SelectIndex == 2)
                foreach (var period in request.Config.MonthGenData.Where(period => time >= period.StartTime && time <= NormalizeEndDate(period.EndTime)))
                    window = window with { Start = MaxDateTime(window.Start, period.StartTime), End = MinDateTime(window.End, NormalizeEndDate(period.EndTime)) };
            var key = ((FlowRuleBase)rule, window.Start, window.End);
            if (!cache.TryGetValue(key, out var days))
            {
                var from = Math.Clamp(rule.StartDay ?? 9, 0, 23); var to = Math.Clamp(rule.EndDay ?? 17, 0, 23);
                if (to < from) (from, to) = (to, from);
                days = EnumerateDates(window.Start.Date, window.End.Date).Where(day => PriorityDateAllowed(request, day, rule)
                    && MaxDateTime(window.Start, day.AddHours(from)) <= MinDateTime(window.End, day.AddHours(to).AddMinutes(59).AddSeconds(59)))
                    .ToArray();
                cache[key] = days;
            }
            if (days.Length > 1) entries.Add(new DistributedDateEntry(row, window, days));
        }
        return entries.GroupBy(entry => (Month: BudgetMonth(entry.Row.AccountTime!.Value), Income: entry.Row.TradeMoney > 0))
            .Select(group => group.ToList()).ToList();
    }

    private static bool HasDatePileup(List<DistributedDateEntry> entries)
    {
        if (entries.Count < 6) return false;
        // Guard gross concentration, not ordinary random variation in a dense
        // month. The repair target below still spreads a detected pile evenly.
        var capacity = Math.Max(4, Math.Max((int)Math.Ceiling(entries.Count * .35),
            (int)Math.Ceiling(DateDistributionTargets(entries).Values.Max() * 1.5)));
        return entries.GroupBy(entry => entry.Row.AccountTime!.Value.Date).Any(group => group.Count() > capacity);
    }

    private static uint DateDistributionDayMask(DistributedDateEntry entry) =>
        entry.Days.Aggregate(0u, (mask, day) => mask | (1u << (day.Day - 1)));

    private static Dictionary<uint, int> DateDistributionTargets(List<DistributedDateEntry> entries)
    {
        var average = (int)Math.Ceiling(entries.Count / (double)entries.SelectMany(entry => entry.Days).Distinct().Count());
        // A weekend-only source may have far fewer dates than the month-wide
        // union. Its required daily capacity must not be divided by weekdays
        // belonging only to other sources, making a feasible spread impossible.
        return entries.GroupBy(DateDistributionDayMask).ToDictionary(group => group.Key,
            group => Math.Max(average, (int)Math.Ceiling(group.Count() / (double)group.First().Days.Length)));
    }

    private static bool SameExplicitBudgetPeriods(FlowAutoGenerationRequest request, DateTime from, DateTime to) =>
        request.Config.SelectIndex != 2 || request.Config.MonthGenData.All(period =>
            (from >= period.StartTime && from <= NormalizeEndDate(period.EndTime))
            == (to >= period.StartTime && to <= NormalizeEndDate(period.EndTime)));
}
