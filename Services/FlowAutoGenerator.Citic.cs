using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private static void EnsureCiticFixedExpenseCoverage(FlowAutoGenerationRequest request, DateTime start, DateTime end,
        double openingBalance, Random random, List<FlowRecord> records, NativeScheduleState state)
    {
        if (!CiticGenerationPolicy.AppliesTo(request.Bank, request.Config)
            || SumIncome(records) <= request.Config.LastMoney + .009d
            || records.Any(row => row.TradeMoney < 0)) return;

        // A fixed payment may exceed the soft income-minus-ending target while
        // remaining fully payable. Keep the source amount and actual cash flow;
        // do not delete all debits or cut income to force the ending reserve.
        var available = RoundMoney(openingBalance + SumIncome(records));
        var candidates = request.References.Where(rule => rule.IsCheck && !IsIncomeRuleSafe(rule)
                && GetRequiredMonthlyCount(rule) == 0)
            .Select(rule => (Rule: rule, Bounds: GetRuleAmountBounds(rule), Dates: ReferenceNaturalAllowedDates(state, start, end, rule)))
            .Where(item => item.Bounds.Min == item.Bounds.Max && item.Bounds.Min <= available + .009d && item.Dates.Count > 0)
            .ToArray();
        if (candidates.Length == 0) return;
        var selected = candidates[random.Next(candidates.Length)];
        AddReferenceNaturalRecord(request, selected.Rule, selected.Dates[random.Next(selected.Dates.Count)],
            selected.Bounds.Min, random, records, state);
    }
}
