using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private static FlowAutoGenerationResult CompleteAlipayResult(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
    {
        if (!AlipayGeneratedNumberRules.AppliesTo(request.Bank)
            || !AlipayGeneratedNumberRules.Complete(request.Bank, result.Records)) return result;

        // Updating a serial can change the tie-break order of equal timestamps.
        // Recalculate reported balances in exactly the generator's persisted order.
        ApplyBalances(result.Records, result.OpeningBalance);
        var minimum = GetMinimumBalance(result.Records, result.OpeningBalance);
        var final = result.Records.LastOrDefault()?.Balance ?? result.OpeningBalance;
        return new FlowAutoGenerationResult
        {
            Records = result.Records,
            OpeningBalance = result.OpeningBalance,
            IncomeTotal = result.IncomeTotal,
            ExpenseTotal = result.ExpenseTotal,
            FinalBalance = RoundMoney(final),
            MinimumBalance = minimum,
            RequiresOpeningBalanceCorrection = minimum < -0.009d
                || IsFinalBalanceOutsideTolerance(final, result.OpeningBalance, request.Config.LastMoney),
            RequiredOpeningBalance = minimum < -0.009d
                ? RoundMoney(result.OpeningBalance - minimum) : result.OpeningBalance
        };
    }
}
