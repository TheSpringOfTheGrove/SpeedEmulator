using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    // Only genuine amount-budget failures are eligible for this fallback.
    // Invalid dates, source precision, missing fields and capacity limits keep
    // their original errors rather than being mistaken for funding shortages.
    private sealed class ReferenceBudgetConflictException(string message) : InvalidOperationException(message);

    private static FlowAutoGenerationResult GenerateWithReferenceBudgetFallback(
        FlowAutoGenerationRequest request, DateTime start, DateTime end, double opening, int seed,
        Func<FlowAutoGenerationResult> generate)
    {
        try { return generate(); }
        catch (ReferenceBudgetConflictException) when (request.Config.SelectIndex == 0)
        {
            if (!TryGenerateReferencePriorityResultCore(request, start, end, opening, new Random(seed), seed,
                    forceBudgetPriority: true, out var result)) throw;
            TraceReferenceNaturalGeneration(request, "reference-priority-budget-fallback", result!.Records, seed, result.OpeningBalance);
            return result;
        }
    }

    private static void GenerateBudgetFallbackRequiredReferences(
        FlowAutoGenerationRequest request, DateTime start, DateTime end, int seed, Random random,
        List<FlowRecord> records, NativeScheduleState state)
    {
        var windows = new List<(GenerateReferenceRule Rule, DateTime Start, DateTime End, DateTime? Date)>();
        if (ConstructionBankGenerationPolicy.AppliesTo(request.Bank))
        {
            // Retain CCB's required counts and exclusive calendar. These plans
            // contain no budget allocation and therefore cannot clamp amounts.
            foreach (var plan in CreateCcbRequiredPlans(request, start, end, new Random(seed)))
            if (plan.Rule is GenerateReferenceRule rule && !plan.Optional)
                windows.AddRange(plan.Windows.Select(window => (rule, window.Start, window.End, (DateTime?)null)));
        }
        else if (BankOfChinaGenerationBudget.AppliesTo(request.Bank))
        {
            // Replay the dedicated planner so full months keep their counts
            // and partial months keep BOC's existing date-attempt convention.
            var planningRandom = new Random(seed);
            var plan = CreateMonthlyAmountPlan(request, start, end, RoundMoney(request.Config.AllInMoney),
                BankOfChinaGenerationBudget.ExpenseTarget(request.Config), planningRandom);
            windows.AddRange(CreateBankOfChinaReferenceOccurrences(request, plan, new NativeScheduleState(), planningRandom)
                .Select(occurrence => (occurrence.Rule, occurrence.Start, occurrence.End, occurrence.Date)));
        }
        else
        {
            // WeChat retains the configured required count in every covered
            // month; the generic date-attempt sampler must not drop these rows.
            foreach (var rule in request.References.Where(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0))
            foreach (var month in EnumerateMonths(start, end))
            for (var occurrence = 0; occurrence < GetRequiredMonthlyCount(rule); occurrence++)
            {
                if (windows.Count + records.Count >= MaximumWechatNaturalRecordCount)
                    throw new InvalidOperationException("微信必现项目超过单次生成上限");
                windows.Add((rule, month.Start, month.End, null));
            }
        }

        var datesByWindow = new Dictionary<(GenerateReferenceRule Rule, DateTime Start, DateTime End), DateTime[]>();
        foreach (var occurrence in windows)
        {
            var window = (occurrence.Rule, Start: occurrence.Start < start ? start : occurrence.Start,
                End: occurrence.End > end ? end : occurrence.End, occurrence.Date);
            if (window.Start > window.End) continue;
            var key = (window.Rule, window.Start, window.End);
            if (!datesByWindow.TryGetValue(key, out var dates))
            {
                dates = EnumerateDates(window.Start.Date, window.End.Date)
                    .Where(day => PriorityDateAllowed(request, day, window.Rule)
                        && CcbTimeWindow(day, new CcbWindow(window.Start, window.End), window.Rule) is not null).ToArray();
                datesByWindow[key] = dates;
            }
            if (window.Date is { } attempted && !dates.Contains(attempted.Date)) continue;
            if (dates.Length == 0)
                throw new InvalidOperationException($"{request.Bank.Name}参照明细第{window.Rule.Index}行在{window.Start:yyyy-MM}没有符合日期及交易时段的日期");
            if (!TrySampleReferenceNaturalRangeAmount(window.Rule, double.MaxValue, random, out var amount)) continue;
            if (records.Count >= MaximumFixedRuleRecordCount)
                throw new InvalidOperationException($"{request.Bank.Name}生成条数超过单次上限");
            var day = window.Date ?? dates.OrderBy(day => state.GetTotalCount(day)).ThenBy(_ => random.Next()).First();
            var time = PickCcbTime(day, new CcbWindow(window.Start, window.End), window.Rule, random, state);
            var row = CreateRecordFromRule(request, window.Rule, request.Bank.ReferenceColumns,
                time, IsIncomeRuleSafe(window.Rule) ? amount : -amount);
            records.Add(row); state.Register(row);
        }
    }
}
