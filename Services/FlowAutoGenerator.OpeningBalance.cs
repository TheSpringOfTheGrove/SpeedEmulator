using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    public FlowAutoGenerationResult ApplyAutomaticOpeningBalance(FlowAutoGenerationRequest request,
        IEnumerable<FlowRecord> records, double openingBalance)
        => CompleteGeneratedDateCorrection(request, EnsureAutomaticOpeningBalance(request,
            CreateAutomaticBalanceResult(records.Select(row => row.Clone()).ToList(), openingBalance), recalculateInterest: true));

    private static FlowAutoGenerationResult EnsureAutomaticOpeningBalance(FlowAutoGenerationRequest request,
        FlowAutoGenerationResult source, double? openingOverride = null, bool cloneRecords = false, bool recalculateInterest = false)
    {
        var opening = RoundMoney(openingOverride ?? source.OpeningBalance);
        if (!double.IsFinite(opening)) throw new InvalidOperationException("期初余额无效");
        var records = cloneRecords ? source.Records.Select(row => row.Clone()).ToList() : source.Records;
        var start = request.Config.StartTime.Date; var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start) (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        Random? random = null;
        for (var pass = 0; pass < 8; pass++)
        {
            if (recalculateInterest && BankInterestPolicy.ShouldCalculate(request.Bank, request.BankUser))
                BankInterestCalculationService.RecalculatePreservingImported(request.Bank, request.BankUser, request.InterestSetting,
                    records, opening, start, end,
                    (time, kind) => CreateInterestRecord(request, request.InterestSetting!, time, 0, kind,
                        kind == InterestTaxRowKind ? InterestTaxText : InterestText, records),
                    random ??= new Random(CreateRunSeed(request, start, end, records.Count ^ 0x0A070)));
            ApplyBalances(records, opening);
            var minimum = GetMinimumBalance(records, opening);
            if (minimum < -.009d)
            {
                // Ordinary timing/variable-spend shortages are repaired first.
                // Only unavoidable fixed/imported obligations may raise opening.
                random ??= new Random(CreateRunSeed(request, start, end, records.Count ^ 0x0A070));
                opening = UsesReferencePriority(request.Config, records)
                    ? FundReferencePriorityCashflow(request, records, opening, start, end, random)
                    : PlanCashflowBeforeOpeningFunding(request, records, opening, start, end, random);
                if (!double.IsFinite(opening)) throw new InvalidOperationException("流水金额超出可计算余额范围");
                recalculateInterest = true;
            }
            if (recalculateInterest && BankInterestPolicy.ShouldCalculate(request.Bank, request.BankUser))
                BankInterestCalculationService.RecalculatePreservingImported(request.Bank, request.BankUser, request.InterestSetting,
                    records, opening, start, end,
                    (time, kind) => CreateInterestRecord(request, request.InterestSetting!, time, 0, kind,
                        kind == InterestTaxRowKind ? InterestTaxText : InterestText, records),
                    random ??= new Random(CreateRunSeed(request, start, end, records.Count ^ 0x0A070)));
            ApplyBalances(records, opening);
            minimum = GetMinimumBalance(records, opening);
            if (minimum >= -.009d) return CreateAutomaticBalanceResult(records, opening);
            recalculateInterest = true;
        }
        throw new InvalidOperationException("自动补足期初余额后仍有负余额，请检查结息配置");
    }

    private static FlowAutoGenerationResult CreateAutomaticBalanceResult(List<FlowRecord> records, double opening)
    {
        ApplyBalances(records, opening);
        var minimum = GetMinimumBalance(records, opening);
        return new FlowAutoGenerationResult
        {
            Records = records, OpeningBalance = opening, IncomeTotal = SumIncome(records), ExpenseTotal = SumExpense(records),
            FinalBalance = RoundMoney(records.LastOrDefault()?.Balance ?? opening), MinimumBalance = minimum,
            RequiresOpeningBalanceCorrection = minimum < -.009d,
            RequiredOpeningBalance = minimum < -.009d ? RoundMoney(opening - minimum + 1d) : opening
        };
    }
}
