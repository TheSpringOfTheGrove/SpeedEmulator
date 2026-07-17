using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using SpeedEmulator.Models;
using ColumnDefinition = SpeedEmulator.Models.ColumnDefinition;

namespace SpeedEmulator.Services;

public sealed class FlowAutoGenerationRequest
{
    public required Bank Bank { get; init; }

    public required BankUser BankUser { get; init; }

    public required FlowGenerationConfig Config { get; init; }

    public required IReadOnlyList<GenerateReferenceRule> References { get; init; }

    public required IReadOnlyList<GenerateConstRule> ConstItems { get; init; }

    public BankInterestSetting? InterestSetting { get; init; }

    public double? OpeningBalanceOverride { get; init; }
}

public sealed class FlowAutoGenerationResult
{
    public required List<FlowRecord> Records { get; init; }

    public required double OpeningBalance { get; init; }

    public required double IncomeTotal { get; init; }

    public required double ExpenseTotal { get; init; }

    public required double FinalBalance { get; init; }

    public required double MinimumBalance { get; init; }

    public required bool RequiresOpeningBalanceCorrection { get; init; }

    public required double RequiredOpeningBalance { get; init; }
}

public sealed class FlowAutoGenerator
{
    private const string AmountUnitField = "__GeneratedAmountUnit";
    private const string AmountMinField = "__GeneratedAmountMin";
    private const string AmountMaxField = "__GeneratedAmountMax";
    private const string RequiredOccurrenceField = "__GeneratedRequiredOccurrence";
    private const string SourceKindField = "__GeneratedSourceKind";
    private const string SourceIndexField = "__GeneratedSourceIndex";
    private const string SmallDiscreteAmountField = "__GeneratedSmallDiscreteAmount";
    private const string AgriculturalSmallExpenseField = "__GeneratedAgriculturalSmallExpense";
    private const string AvoidSyntheticFractionField = "__GeneratedAvoidSyntheticFraction";
    private const string ReferenceSourceKind = "Reference";
    private const string ConstSourceKind = "Const";
    private const string SystemRowKindField = "__GeneratedSystemRowKind";
    private const string InterestRowKind = "Interest";
    private const string InterestTaxRowKind = "InterestTax";
    private const string InterestText = "\u7ed3\u606f";
    private const string InterestTaxText = "\u5229\u606f\u7a0e";
    private const string PersonalCurrentInterestRemark = "\u4e2a\u4eba\u6d3b\u671f\u7ed3\u606f";
    private const int SmallDiscreteAmountStepLimit = 160;
    private const int ReferenceAmountDistributionStepLimit = 600;
    private const double SmallDiscreteAverageMinRatio = 0.35d;
    private const double SmallDiscreteAverageMaxRatio = 0.62d;
    private const double ReferenceAmountEdgeUsageRatio = 0.025d;
    private const double NaturalAmountTargetRatio = 0.80d;
    private const double MinimumLowRecordRatio = 0.35d;
    private const double MaximumLowRecordRatio = 0.50d;
    private const double MinimumLowRecordTarget = 0.38d;
    private const double MaximumLowRecordTarget = 0.45d;
    private const double MaximumPlannedLowRecordRatio = 0.38d;
    private const double MinimumMediumRecordTarget = 0.20d;
    private const double MaximumMediumRecordTarget = 0.30d;
    private const double MinimumLowCapacityTierWeight = 0.10d;
    private const double MaximumLowCapacityTierPoolRatio = 0.20d;
    private const double MaximumMediumCapacityTierWeight = 0.45d;
    private const double FinalBalanceTolerance = 1000d;
    private const string ExternalSystemFlowColumnName = "\u5916\u90e8\u7cfb\u7edf\u6d41\u6c34";
    private static readonly HashSet<string> InterestSettingProtectedFields = new(StringComparer.Ordinal)
    {
        nameof(FlowRecord.Index),
        nameof(FlowRecord.Id),
        nameof(FlowRecord.ReplaceIndex),
        nameof(FlowRecord.BankId),
        nameof(FlowRecord.BankUserId),
        nameof(FlowRecord.MoveFlag),
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
    private static readonly string[] TokenSeparators = [",", ";", ":", "，", "；", "：", "|", "、", " "];
    private static readonly string[] AgriculturalUaPostscriptKeywords =
    [
        "财付通",
        "微信",
        "微信支付",
        "扫二维码",
        "二维码付款",
        "支付宝",
        "多多支付",
        "拼多多",
        "手机充值",
        "特约商户"
    ];
    private static readonly string[] ConsumerExpenseKeywords =
    [
        "\u6d88\u8d39",
        "\u652f\u4ed8",
        "\u5feb\u6377",
        "\u65e0\u5361",
        "\u8d22\u4ed8\u901a",
        "\u652f\u4ed8\u5b9d",
        "\u5fae\u4fe1",
        "\u4e91\u95ea\u4ed8",
        "\u94f6\u8054",
        "\u5237\u5361",
        "\u626b\u7801",
        "\u5546\u6237",
        "\u4eac\u4e1c",
        "\u7f8e\u56e2",
        "\u62fc\u591a\u591a",
        "\u591a\u591a",
        "\u6296\u97f3",
        "\u6dd8\u5b9d",
        "\u5929\u732b",
        "\u997f\u4e86\u4e48",
        "\u751f\u6d3b\u7f34\u8d39",
        "\u624b\u673a\u5145\u503c",
        "POS"
    ];
    private static readonly string[] NonConsumerExpenseKeywords =
    [
        "\u7ed3\u606f",
        "\u5229\u606f",
        "\u5229\u606f\u7a0e",
        "\u5de5\u8d44",
        "\u4ed6\u884c\u6c47\u5165",
        "\u6c47\u5165",
        "\u8f6c\u5165",
        "\u6536\u6b3e",
        "\u8f6c\u8d26",
        "\u8f6c\u652f",
        "\u8f6c\u5b58",
        "\u63d0\u73b0",
        "\u53d6\u73b0",
        "\u5b58\u6b3e",
        "\u67dc\u9762",
        "\u51b2\u6b63",
        "\u51b2\u9500",
        "\u8d37\u6b3e",
        "\u8fd8\u6b3e"
    ];

    private static readonly HashSet<string> IgnoredRuleProperties = new(StringComparer.Ordinal)
    {
        nameof(FlowRuleBase.Index),
        nameof(FlowRuleBase.Id),
        nameof(FlowRuleBase.BankId),
        nameof(FlowRuleBase.IsCheck),
        nameof(FlowRuleBase.MinMoney),
        nameof(FlowRuleBase.MaxMoney),
        nameof(FlowRuleBase.FloutLength),
        nameof(FlowRuleBase.StartDay),
        nameof(FlowRuleBase.EndDay),
        nameof(FlowRuleBase.TradeHoliday),
        nameof(FlowRuleBase.TradeWeekend),
        nameof(FlowRuleBase.CrossBankRate),
        nameof(FlowRuleBase.CrossBankMin),
        nameof(FlowRuleBase.CrossBankMax),
        nameof(FlowRuleBase.OffSiteBankRate),
        nameof(FlowRuleBase.OffSiteBankMin),
        nameof(FlowRuleBase.OffSiteBankMax),
        nameof(FlowRuleBase.CreditAmount),
        nameof(FlowRuleBase.DebitAmount),
        nameof(FlowRuleBase.BalanceAmount),
        nameof(FlowRuleBase.ExtraFields),
        nameof(GenerateReferenceRule.PercentMonth),
        nameof(GenerateConstRule.FixDay),
        nameof(GenerateConstRule.ReCnt)
    };

    public FlowAutoGenerationResult Generate(FlowAutoGenerationRequest request)
    {
        request = ExcludeUngeneratableReferenceRules(request);
        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start)
        {
            (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        }

        var openingBalance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
        var random = new Random(CreateRunSeed(request, start, end));
        var records = new List<FlowRecord>();
        var perfTrace = FlowGenerationPerfTrace.Start();

        var scheduleState = NativeScheduleState.From(records);
        var amountUsage = AmountUsageTracker.From(records);
        var targetIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney));
        var finalBalanceTarget = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
        var plannedExpense = RoundMoney(Math.Min(targetIncome, Math.Max(0, targetIncome - request.Config.LastMoney)));
        var monthlyPlan = CreateMonthlyAmountPlan(request, start, end, targetIncome, plannedExpense, random);

        GenerateNativeConstRecords(request, start, end, targetIncome, random, records, scheduleState, amountUsage);
        GenerateNativeRequiredReferenceRecords(request, monthlyPlan, targetIncome, random, records, scheduleState, amountUsage);
        if (!IsWechatBank(request.Bank) && request.References.Count < 200)
        {
            EnsureNativeOptionalReferenceRuleCoverage(request, monthlyPlan, targetIncome, random, records, scheduleState, amountUsage);
        }
        GenerateNativeOptionalReferenceRecords(request, monthlyPlan, targetIncome, random, records, scheduleState, amountUsage);
        EnsureNativeOptionalReferenceRuleCoverage(request, monthlyPlan, targetIncome, random, records, scheduleState, amountUsage);
        CompleteNativeConfiguredTotals(request, monthlyPlan, start, end, targetIncome, random, records, scheduleState, amountUsage);
        RedistributeFirstMonthIncomeRecords(request, monthlyPlan, random, records, scheduleState);
        perfTrace.Mark("base generation", records);

        if (request.BankUser.AutoCalculateInterest)
        {
            GenerateInterestRecords(request, openingBalance, start, end, random, records);
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
        }
        perfTrace.Mark("interest", records);

        if (records.Count == 0)
        {
            return CreateEmptyResult(openingBalance, request.Config.LastMoney);
        }

        if (UseVeryHighVolumeNativeFastPath(records.Count))
        {
            var result = FinalizeVeryHighVolumeNativeResult(
                records,
                request,
                openingBalance,
                start,
                end,
                random);
            perfTrace.Mark("very high volume finalize", records);
            return result;
        }

        ReconcileNativeGeneratedRecords(records, request, openingBalance, start, end, random);
        if (request.BankUser.AutoCalculateInterest)
        {
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
            ReconcileNativeGeneratedRecords(records, request, openingBalance, start, end, random);
        }
        perfTrace.Mark("reconcile", records);

        PruneZeroAmountRecords(records);
        ApplyBalances(records, openingBalance);
        var scheduleChanged = SmoothNativeSchedule(records, request, start, end, random);
        if (scheduleChanged && !UseHighVolumeNativePostProcessing(request.Bank, records.Count))
        {
            ReconcileNativeGeneratedRecords(records, request, openingBalance, start, end, random);
        }
        perfTrace.Mark("smooth schedule", records);
        BalanceNativeIncomeExpenseRhythm(records, request, start, end, openingBalance, random, requireNonNegativeBalance: false);
        if (!IsWechatBank(request.Bank))
        {
            EnsureLargeIncomeMonthlyPresence(records, request, start, end, openingBalance, random, requireNonNegativeBalance: false);
        }
        perfTrace.Mark("rhythm initial", records);

        ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
        ReducePositiveFinalBalanceSafely(records, request, openingBalance);
        AddSafeExpenseRecordsForPositiveFinalBalance(records, request, start, end, openingBalance, random);
        ReducePositiveFinalBalanceSafely(records, request, openingBalance);
        perfTrace.Mark("balance repair 1", records);
        // Temporarily disabled: max-amount splitting can inflate one direction and make the
        // timeline look mechanically segmented. Re-enable only when the split strategy is revised.
        // SplitNativeMaxAmountRecords(records, request, start, end, openingBalance, random, requireNonNegativeBalance: false);
        ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
        ReducePositiveFinalBalanceSafely(records, request, openingBalance);
        EnsureNativeDailyCoverage(records, request, start, end, openingBalance, random);
        if (!IsWechatBank(request.Bank))
        {
            EnsureLargeIncomeMonthlyPresence(records, request, start, end, openingBalance, random, requireNonNegativeBalance: false);
        }
        perfTrace.Mark("daily coverage", records);

        var highVolumeTimelinePostProcessing = UseHighVolumeNativePostProcessing(request.Bank, records.Count);
        ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
        ReducePositiveFinalBalanceSafely(records, request, openingBalance);
        // SplitNativeMaxAmountRecords(records, request, start, end, openingBalance, random, requireNonNegativeBalance: true);
        BalanceNativeIncomeExpenseRhythm(
            records,
            request,
            start,
            end,
            openingBalance,
            random,
            requireNonNegativeBalance: !highVolumeTimelinePostProcessing);
        if (!IsWechatBank(request.Bank))
        {
            EnsureLargeIncomeMonthlyPresence(records, request, start, end, openingBalance, random, requireNonNegativeBalance: false);
            ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            // SplitNativeMaxAmountRecords(records, request, start, end, openingBalance, random, requireNonNegativeBalance: true);
        }
        ShapeMonthlyBalanceSwing(
            records,
            request,
            start,
            end,
            openingBalance,
            random,
            requireNonNegativeBalance: !highVolumeTimelinePostProcessing);
        BreakNativeIncomeRuns(
            records,
            request,
            start,
            end,
            openingBalance,
            random,
            requireNonNegativeBalance: !highVolumeTimelinePostProcessing);
        BalanceNativeIncomeExpenseRhythm(
            records,
            request,
            start,
            end,
            openingBalance,
            random,
            requireNonNegativeBalance: !highVolumeTimelinePostProcessing);
        ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
        perfTrace.Mark("monthly rhythm", records);
        var highVolumeAmountPostProcessing = UseHighVolumeNativePostProcessing(request.Bank, records.Count);
        PruneZeroAmountRecords(records);
        EnsureDistinctSignedAmounts(records, random);
        NormalizeSmallDiscreteAmountDistribution(records, request, random);
        NormalizeReferenceAmountDistribution(records, random);
        RestoreFinalBalanceAfterDistinctAmounts(records, request, openingBalance);
        if (!highVolumeAmountPostProcessing)
        {
            NormalizeSmallDiscreteAmountDistribution(records, request, random);
            NormalizeReferenceAmountDistribution(records, random);
            RestoreFinalBalanceAfterDistinctAmounts(records, request, openingBalance);
            NormalizeReferenceAmountDistribution(records, random);
            if (request.BankUser.AutoCalculateInterest)
            {
                RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
                NormalizeReferenceAmountDistribution(records, random);
                RestoreFinalBalanceAfterDistinctAmounts(records, request, openingBalance);
                NormalizeReferenceAmountDistribution(records, random);
            }
        }
        else if (request.BankUser.AutoCalculateInterest)
        {
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
            RestoreFinalBalanceAfterDistinctAmounts(records, request, openingBalance);
        }
        perfTrace.Mark("amount normalize 1", records);

        if (UseHighVolumeNativePostProcessing(request.Bank, records.Count)
            && IsFinalBalanceOutsideTolerance(records.LastOrDefault()?.Balance ?? openingBalance, openingBalance, request.Config.LastMoney))
        {
            ForceHighVolumeFinalBalanceWithinTolerance(records, request, start, end, openingBalance, random);
            NormalizeSmallDiscreteAmountDistribution(records, request, random);
            NormalizeReferenceAmountDistribution(records, random);
            RestoreFinalBalanceAfterDistinctAmounts(records, request, openingBalance);
            NormalizeSmallDiscreteAmountDistribution(records, request, random);
            NormalizeReferenceAmountDistribution(records, random);
        }
        perfTrace.Mark("high volume final", records);

        NormalizeSmallDiscreteAmountDistribution(records, request, random);
        perfTrace.Mark("rebalance normalize small pre", records);
        NormalizeReferenceAmountDistribution(records, random);
        perfTrace.Mark("rebalance normalize ref pre", records);
        RebalanceSmallAmountTotalsToLargeRules(records, request, start, end, openingBalance, random);
        perfTrace.Mark("rebalance small totals", records);
        ReduceSmallDiscreteEdgePileupsToLargeRules(records, request, start, end, openingBalance, random);
        perfTrace.Mark("rebalance edge pileups", records);
        NormalizeSmallDiscreteAmountDistribution(records, request, random);
        perfTrace.Mark("rebalance normalize small post", records);
        NormalizeReferenceAmountDistribution(records, random);
        perfTrace.Mark("amount rebalance", records);
        ShapeBalanceHighLowWaves(records, request, start, end, openingBalance, random);
        ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
        RestoreFinalBalancePreservingNonNegative(records, request, openingBalance);
        if (!highVolumeAmountPostProcessing)
        {
            NormalizeSmallDiscreteAmountDistribution(records, request, random);
            NormalizeReferenceAmountDistribution(records, random);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            AddSafeExpenseRecordsForPositiveFinalBalance(records, request, start, end, openingBalance, random);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
            RestoreFinalBalancePreservingNonNegative(records, request, openingBalance);
        }
        else
        {
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            RestoreFinalBalancePreservingNonNegative(records, request, openingBalance);
        }
        ApplyBalances(records, openingBalance);
        if (IsFinalBalanceOutsideTolerance(
                records.LastOrDefault()?.Balance ?? openingBalance,
                openingBalance,
                request.Config.LastMoney))
        {
            ForceCloseExternalFinalBalance(request, records);
        }
        RestoreConfiguredIncomeWithMatchedExpense(records, request, start, end, openingBalance, random);
        RedistributeMonthlyIncomeRecords(
            request,
            monthlyPlan,
            random,
            records,
            openingBalance);
        perfTrace.Mark("final balance", records);
        PruneZeroAmountRecords(records);
        EnforceConfiguredTotals(records, request);
        EnsureNativeFinalReferenceRuleCoverage(records, request, start, end, openingBalance, random);
        EnforceLowReferenceRecordRatio(records, request, start, end, openingBalance, random);
        RebalanceLowReferenceNaturalSegmentTotals(records, openingBalance, random);
        EnsureConfiguredFractionalReferenceAmounts(records, random);
        ApplyBalances(records, openingBalance);
        if (GetMinimumBalance(records, openingBalance) < -0.009d)
        {
            ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
            RestoreFinalBalancePreservingNonNegative(records, request, openingBalance);
            ApplyBalances(records, openingBalance);
        }

        var finalIncome = SumIncome(records);
        var finalExpense = SumExpense(records);
        var finalBalanceBeforeClose = records.LastOrDefault()?.Balance ?? openingBalance;
        if (finalIncome > Math.Max(0, request.Config.AllInMoney) + 0.009d
            || finalExpense > finalIncome + 0.009d
            || GetMinimumBalance(records, openingBalance) < -0.009d
            || IsFinalBalanceOutsideTolerance(finalBalanceBeforeClose, openingBalance, request.Config.LastMoney))
        {
            ForceCloseExternalFinalBalance(request, records);
        }

        ApplyBalances(records, openingBalance);
        if (GetMinimumBalance(records, openingBalance) < -0.009d)
        {
            RepairVeryHighVolumeNegativeBalances(records, openingBalance);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            AddSafeExpenseRecordsForPositiveFinalBalance(records, request, start, end, openingBalance, random);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            ApplyBalances(records, openingBalance);
        }
        perfTrace.Mark("final apply", records);

        var incomeTotal = SumIncome(records);
        var expenseTotal = SumExpense(records);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimumBalance = records.Select(item => item.Balance ?? openingBalance).Append(openingBalance).Min();
        var requiredOpeningBalance = openingBalance;
        var needsCorrection = false;

        if (minimumBalance < -0.009d)
        {
            requiredOpeningBalance = RoundMoney(openingBalance - minimumBalance);
            needsCorrection = true;
        }

        if (IsFinalBalanceOutsideTolerance(finalBalance, openingBalance, request.Config.LastMoney))
        {
            needsCorrection = true;
        }

        return new FlowAutoGenerationResult
        {
            Records = records,
            OpeningBalance = openingBalance,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            FinalBalance = RoundMoney(finalBalance),
            MinimumBalance = RoundMoney(minimumBalance),
            RequiresOpeningBalanceCorrection = needsCorrection,
            RequiredOpeningBalance = RoundMoney(Math.Max(0, requiredOpeningBalance))
        };
    }

    public FlowAutoGenerationResult ApplyNegativeBalanceCorrection(
        FlowAutoGenerationRequest request,
        FlowAutoGenerationResult source)
    {
        request = ExcludeUngeneratableReferenceRules(request);
        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start)
        {
            (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        }

        var openingBalance = RoundMoney(source.OpeningBalance);
        var random = new Random(CreateRunSeed(request, start, end, source.Records.Count ^ 0x42B1A7C3));
        var records = source.Records
            .Select(item =>
            {
                var copy = item.Clone();
                copy.Id = 0;
                return copy;
            })
            .ToList();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            ReconcileNativeGeneratedRecords(records, request, openingBalance, start, end, random);
            ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            AddSafeExpenseRecordsForPositiveFinalBalance(records, request, start, end, openingBalance, random);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            PruneZeroAmountRecords(records);
            ApplyBalances(records, openingBalance);
            if (GetMinimumBalance(records, openingBalance) >= -0.009d)
            {
                break;
            }
        }

        PruneZeroAmountRecords(records);
        EnsureDistinctSignedAmounts(records, random);
        RestoreFinalBalancePreservingNonNegative(records, request, openingBalance);
        for (var attempt = 0; attempt < 6 && GetMinimumBalance(records, openingBalance) < -0.009d; attempt++)
        {
            ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            AddSafeExpenseRecordsForPositiveFinalBalance(records, request, start, end, openingBalance, random);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            PruneZeroAmountRecords(records);
            RestoreFinalBalancePreservingNonNegative(records, request, openingBalance);
            ApplyBalances(records, openingBalance);
        }

        if (GetMinimumBalance(records, openingBalance) < -0.009d)
        {
            ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
            PruneZeroAmountRecords(records);
        }

        ApplyBalances(records, openingBalance);

        var incomeTotal = SumIncome(records);
        var expenseTotal = SumExpense(records);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimumBalance = GetMinimumBalance(records, openingBalance);

        return new FlowAutoGenerationResult
        {
            Records = records,
            OpeningBalance = openingBalance,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            FinalBalance = RoundMoney(finalBalance),
            MinimumBalance = RoundMoney(minimumBalance),
            RequiresOpeningBalanceCorrection = minimumBalance < -0.009d
                || IsFinalBalanceOutsideTolerance(finalBalance, openingBalance, request.Config.LastMoney),
            RequiredOpeningBalance = source.RequiredOpeningBalance
        };
    }

    public FlowAutoGenerationResult ApplyOpeningBalanceCorrection(
        FlowAutoGenerationRequest request,
        FlowAutoGenerationResult source,
        double correctedOpeningBalance)
    {
        request = ExcludeUngeneratableReferenceRules(request);
        var openingBalance = RoundMoney(correctedOpeningBalance);
        var records = source.Records
            .Select(item =>
            {
                var copy = item.Clone();
                copy.Id = 0;
                return copy;
            })
            .ToList();

        if (records.Count == 0)
        {
            return CreateEmptyResult(openingBalance, request.Config.LastMoney);
        }

        PruneZeroAmountRecords(records);
        EnsureDistinctSignedAmounts(records);
        ApplyBalances(records, openingBalance);
        EnforceConfiguredTotals(records, request);
        BringFinalBalanceWithinTolerance(records, request, openingBalance);
        EnforceConfiguredTotals(records, request);

        PruneZeroAmountRecords(records);
        RestoreFinalBalanceAfterDistinctAmounts(records, request, openingBalance);
        ApplyBalances(records, openingBalance);

        var incomeTotal = SumIncome(records);
        var expenseTotal = SumExpense(records);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimumBalance = records.Select(item => item.Balance ?? openingBalance).Append(openingBalance).Min();
        var requiresCorrection = minimumBalance < -0.009d
            || IsFinalBalanceOutsideTolerance(finalBalance, openingBalance, request.Config.LastMoney);
        var requiredOpeningBalance = requiresCorrection
            ? RoundMoney(Math.Max(openingBalance, openingBalance - minimumBalance))
            : openingBalance;

        return new FlowAutoGenerationResult
        {
            Records = records,
            OpeningBalance = openingBalance,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            FinalBalance = RoundMoney(finalBalance),
            MinimumBalance = RoundMoney(minimumBalance),
            RequiresOpeningBalanceCorrection = requiresCorrection,
            RequiredOpeningBalance = RoundMoney(Math.Max(0, requiredOpeningBalance))
        };
    }

    internal static FlowRecord CreateRecordFromExternalPlan(
        FlowAutoGenerationRequest request,
        FlowRuleBase rule,
        IReadOnlyList<ColumnDefinition> ruleColumns,
        DateTime accountTime,
        double amount)
    {
        return CreateRecordFromRule(request, rule, ruleColumns, accountTime, amount);
    }

    internal static FlowAutoGenerationResult FinalizeExternalPlanResult(
        FlowAutoGenerationRequest request,
        List<FlowRecord> records)
    {
        request = ExcludeUngeneratableReferenceRules(request);
        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start)
        {
            (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        }

        var openingBalance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
        var random = new Random(CreateRunSeed(request, start, end));

        if (request.BankUser.AutoCalculateInterest)
        {
            GenerateInterestRecords(request, openingBalance, start, end, random, records);
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
        }

        PruneZeroAmountRecords(records);
        ApplyBalances(records, openingBalance);
        EnforceConfiguredTotals(records, request);
        BringFinalBalanceWithinTolerance(records, request, openingBalance);

        if (request.BankUser.AutoCalculateInterest)
        {
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
        }

        EnforceConfiguredTotals(records, request);
        PruneZeroAmountRecords(records);
        EnsureDistinctSignedAmounts(records, random);
        RestoreFinalBalanceAfterDistinctAmounts(records, request, openingBalance);
        ApplyBalances(records, openingBalance);

        var incomeTotal = SumIncome(records);
        var expenseTotal = SumExpense(records);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimumBalance = records.Select(item => item.Balance ?? openingBalance).Append(openingBalance).Min();
        var requiredOpeningBalance = openingBalance;
        var needsCorrection = false;

        if (minimumBalance < -0.009d)
        {
            requiredOpeningBalance = RoundMoney(openingBalance - minimumBalance);
            needsCorrection = true;
        }

        if (IsFinalBalanceOutsideTolerance(finalBalance, openingBalance, request.Config.LastMoney))
        {
            needsCorrection = true;
        }

        return new FlowAutoGenerationResult
        {
            Records = records,
            OpeningBalance = openingBalance,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            FinalBalance = RoundMoney(finalBalance),
            MinimumBalance = RoundMoney(minimumBalance),
            RequiresOpeningBalanceCorrection = needsCorrection,
            RequiredOpeningBalance = RoundMoney(Math.Max(0, requiredOpeningBalance))
        };
    }

    internal static void ForceCloseExternalFinalBalance(
        FlowAutoGenerationRequest request,
        List<FlowRecord> records)
    {
        request = ExcludeUngeneratableReferenceRules(request);
        if (records.Count == 0)
        {
            return;
        }

        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start)
        {
            (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        }

        var openingBalance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
        var random = new Random(CreateRunSeed(request, start, end, records.Count ^ 0x6C8E9CF5));
        var maxAttempts = IsWechatBank(request.Bank) ? 6 : 18;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            PruneZeroAmountRecords(records);
            ApplyBalances(records, openingBalance);
            if (EnforceConfiguredTotals(records, request))
            {
                continue;
            }

            if (TryFindFirstNegativeBalance(records, openingBalance, out var firstNegativeTime, out var minimumBalance)
                && minimumBalance < -0.009d)
            {
                var incomeAmount = RoundMoney(Math.Abs(minimumBalance) + 1d);
                DecreaseSignedRecordsWithinBounds(records, isIncome: false, incomeAmount, allowDropOptional: true);
                continue;
            }

            var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
            var targetBalance = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
            var diff = RoundMoney(finalBalance - targetBalance);
            if (Math.Abs(diff) <= FinalBalanceTolerance)
            {
                break;
            }

            var beforeNet = CalculateNetAmount(records);
            if (diff > 0)
            {
                DecreaseFinalBalanceWithinRules(records, request, start, end, random, Math.Abs(diff));
                ApplyBalances(records, openingBalance);
                diff = RoundMoney((records.LastOrDefault()?.Balance ?? openingBalance) - targetBalance);
                if (Math.Abs(diff) <= FinalBalanceTolerance)
                {
                    break;
                }

                if (CalculateNetAmount(records) >= beforeNet - 0.009d
                    || diff > FinalBalanceTolerance)
                {
                    ForceAddBalanceAdjustmentRecords(
                        records,
                        request,
                        start,
                        end,
                        random,
                        isIncome: false,
                        targetAmount: Math.Abs(diff));
                }
            }
            else
            {
                IncreaseFinalBalanceWithinRules(records, request, start, end, random, Math.Abs(diff));
                ApplyBalances(records, openingBalance);
                diff = RoundMoney((records.LastOrDefault()?.Balance ?? openingBalance) - targetBalance);
                if (Math.Abs(diff) <= FinalBalanceTolerance)
                {
                    break;
                }

                if (CalculateNetAmount(records) <= beforeNet + 0.009d
                    || diff < -FinalBalanceTolerance)
                {
                    ForceAddBalanceAdjustmentRecords(
                        records,
                        request,
                        start,
                        end,
                        random,
                        isIncome: true,
                        targetAmount: Math.Abs(diff),
                        allowConstRules: false);
                }
            }
        }

        PruneZeroAmountRecords(records);
        EnforceConfiguredTotals(records, request);
        ApplyBalances(records, openingBalance);
    }

    private static void ReconcileNativeGeneratedRecords(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance,
        DateTime start,
        DateTime end,
        Random random)
    {
        var highVolumePostProcessing = UseHighVolumeNativePostProcessing(request.Bank, records.Count);
        var maxAttempts = IsWechatBank(request.Bank) ? 3 : highVolumePostProcessing ? 1 : 8;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var changed = false;
            PruneZeroAmountRecords(records);
            ApplyBalances(records, openingBalance);

            changed = EnforceConfiguredTotals(records, request) || changed;
            changed = AlignNativeExpenseToFinalTarget(records, request, start, end, random) || changed;
            PruneZeroAmountRecords(records);
            ApplyBalances(records, openingBalance);

            changed = MoveNativeIncomeBeforeNegativeBalances(records, request, openingBalance, start, end, random) || changed;
            ApplyBalances(records, openingBalance);

            changed = MoveNativeRecordsToAvoidNegativeBalances(records, request, openingBalance, start, end, random) || changed;
            ApplyBalances(records, openingBalance);

            changed = PreventNativeNegativeBalances(records, openingBalance) || changed;
            ApplyBalances(records, openingBalance);

            changed = AdjustNativeFinalBalanceWithExistingRecords(records, request, openingBalance) || changed;
            PruneZeroAmountRecords(records);
            ApplyBalances(records, openingBalance);

            changed = MoveNativeIncomeBeforeNegativeBalances(records, request, openingBalance, start, end, random) || changed;
            ApplyBalances(records, openingBalance);

            changed = MoveNativeRecordsToAvoidNegativeBalances(records, request, openingBalance, start, end, random) || changed;
            ApplyBalances(records, openingBalance);

            changed = PreventNativeNegativeBalances(records, openingBalance) || changed;
            ApplyBalances(records, openingBalance);

            if (!changed)
            {
                break;
            }
        }
    }

    private static bool AlignNativeExpenseToFinalTarget(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random)
    {
        var incomeTotal = SumIncome(records);
        var targetExpense = RoundMoney(Math.Min(incomeTotal, Math.Max(0, incomeTotal - request.Config.LastMoney)));
        var before = SumExpense(records);
        RebalanceSignedRecords(GetSignedRecords(records, isIncome: false), targetExpense);
        PruneZeroAmountRecords(records);
        var after = SumExpense(records);
        var remaining = RoundMoney(targetExpense - after);
        if (remaining > 0.009d)
        {
            var rules = GetNativeFillReferenceRules(request, isIncome: false, includeRequiredRules: false, random);
            var scheduleState = NativeScheduleState.From(records);
            var amountUsage = AmountUsageTracker.From(records);
            GenerateNativeOptionalForMonth(
                request,
                start,
                end,
                rules,
                isIncome: false,
                remaining,
                Math.Max(0, request.Config.AllInMoney),
                random,
                records,
                scheduleState,
                amountUsage);
            RebalanceSignedRecords(GetSignedRecords(records, isIncome: false), targetExpense);
            PruneZeroAmountRecords(records);
        }

        return Math.Abs(SumExpense(records) - before) > 0.009d;
    }

    private static bool PreventNativeNegativeBalances(List<FlowRecord> records, double openingBalance)
    {
        var changed = false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ApplyBalances(records, openingBalance);
            if (!TryFindFirstNegativeBalance(records, openingBalance, out var firstNegativeTime, out var minimumBalance)
                || minimumBalance >= -0.009d)
            {
                break;
            }

            var needed = RoundMoney(Math.Abs(minimumBalance) + 1d);
            var earlyRecords = records
                .Where(item => item.AccountTime <= firstNegativeTime)
                .ToList();
            var beforeExpense = SumExpense(earlyRecords);
            DecreaseSignedRecordsWithinBounds(earlyRecords, isIncome: false, needed, allowDropOptional: true);
            PruneZeroAmountRecords(records);
            changed = changed || SumExpense(earlyRecords) < beforeExpense - 0.009d;
            if (!changed)
            {
                break;
            }
        }

        return changed;
    }

    private static bool ForceNativeNonNegativeBalances(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance,
        DateTime start,
        DateTime end,
        Random random)
    {
        var changed = false;
        var highVolumePostProcessing = UseHighVolumeNativePostProcessing(request.Bank, records.Count);
        var maxAttempts = IsWechatBank(request.Bank) ? highVolumePostProcessing ? 8 : 24 : highVolumePostProcessing ? 18 : 36;
        for (var attempt = 0; attempt < Math.Min(maxAttempts, Math.Max(1, records.Count)); attempt++)
        {
            ApplyBalances(records, openingBalance);
            if (!TryFindFirstNegativeBalance(records, openingBalance, out _, out var minimumBalance)
                || minimumBalance >= -0.009d)
            {
                break;
            }

            var passChanged = MoveNativeRecordsToAvoidNegativeBalances(records, request, openingBalance, start, end, random);
            ApplyBalances(records, openingBalance);

            passChanged = MoveNativeIncomeBeforeNegativeBalances(records, request, openingBalance, start, end, random) || passChanged;
            ApplyBalances(records, openingBalance);

            passChanged = PreventNativeNegativeBalances(records, openingBalance) || passChanged;
            ApplyBalances(records, openingBalance);

            passChanged = ForceTrimExpensesBeforeFirstNegative(records, request, openingBalance) || passChanged;
            ApplyBalances(records, openingBalance);

            passChanged = TrimExpensePrefixesForNonNegativeBalance(records, openingBalance) || passChanged;
            ApplyBalances(records, openingBalance);

            changed = changed || passChanged;
            if (!passChanged)
            {
                break;
            }
        }

        return changed;
    }

    private static bool TrimExpensePrefixesForNonNegativeBalance(List<FlowRecord> records, double openingBalance)
    {
        if (records.Count == 0)
        {
            return false;
        }

        ApplyBalances(records, openingBalance);
        var changed = false;
        var runningBalance = RoundMoney(openingBalance);
        var adjustableExpenses = new List<FlowRecord>();
        foreach (var record in records)
        {
            var amount = RoundMoney(record.TradeMoney ?? 0);
            if (amount < -0.009d && !IsSystemInterestRecord(record))
            {
                adjustableExpenses.Add(record);
            }

            runningBalance = RoundMoney(runningBalance + amount);
            if (runningBalance >= 1d)
            {
                continue;
            }

            var needed = RoundMoney(1d - runningBalance);
            foreach (var candidate in adjustableExpenses
                         .OrderBy(item => IsRequiredGeneratedRecord(item) ? 1 : 0)
                         .ThenByDescending(item => GetExpenseReducibleAmount(item))
                         .ThenByDescending(item => item.AccountTime ?? DateTime.MinValue)
                         .ToList())
            {
                if (needed <= 0.009d)
                {
                    break;
                }

                var reducible = GetExpenseReducibleAmount(candidate);
                if (reducible <= 0.009d)
                {
                    continue;
                }

                var reduction = Math.Min(reducible, needed);
                var before = Math.Abs(candidate.TradeMoney ?? 0);
                var bounds = GetRecordAmountBounds(candidate);
                var isRequired = IsRequiredGeneratedRecord(candidate);
                var nextAmount = before - reduction;
                if (!isRequired && nextAmount < bounds.Min - 0.009d)
                {
                    nextAmount = 0;
                }
                else
                {
                    nextAmount = ClampAmountToBounds(nextAmount, bounds);
                }

                var actualReduction = RoundMoney(before - nextAmount);
                if (actualReduction <= 0.009d)
                {
                    continue;
                }

                ApplySignedAmount(candidate, -1d, nextAmount);
                needed = RoundMoney(needed - actualReduction);
                runningBalance = RoundMoney(runningBalance + actualReduction);
                changed = true;
            }
        }

        if (changed)
        {
            PruneZeroAmountRecords(records);
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static double GetExpenseReducibleAmount(FlowRecord record)
    {
        var absolute = Math.Abs(record.TradeMoney ?? 0);
        if (absolute <= 0.009d)
        {
            return 0;
        }

        var bounds = GetRecordAmountBounds(record);
        return RoundMoney(IsRequiredGeneratedRecord(record)
            ? Math.Max(0, absolute - bounds.Min)
            : absolute);
    }

    private static bool ForceTrimExpensesBeforeFirstNegative(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance)
    {
        var changed = false;
        var maxAttempts = IsWechatBank(request.Bank) ? 6 : 24;
        for (var attempt = 0; attempt < Math.Min(maxAttempts, records.Count); attempt++)
        {
            ApplyBalances(records, openingBalance);
            var firstNegative = records
                .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
                .ThenBy(item => item.Index)
                .FirstOrDefault(item => (item.Balance ?? openingBalance) < -0.009d);
            if (firstNegative is null)
            {
                break;
            }

            var needed = RoundMoney(Math.Abs(firstNegative.Balance ?? 0) + 1d);
            var candidates = records
                .Where(item => item.AccountTime <= firstNegative.AccountTime)
                .Where(item => item.TradeMoney < -0.009d)
                .Where(item => !IsSystemInterestRecord(item))
                .OrderBy(item => IsRequiredGeneratedRecord(item) ? 1 : 0)
                .ThenBy(item => item.ExtraFields.TryGetValue(SourceKindField, out var sourceKind) && sourceKind == ConstSourceKind ? 1 : 0)
                .ThenByDescending(item => Math.Abs(item.TradeMoney ?? 0))
                .ThenByDescending(item => item.AccountTime ?? DateTime.MinValue)
                .ToList();
            if (candidates.Count == 0)
            {
                break;
            }

            var beforeExpense = SumExpense(candidates);
            ForceDecreaseExpenseRecords(candidates, needed);
            PruneZeroAmountRecords(records);
            var afterExpense = SumExpense(candidates);
            if (afterExpense < beforeExpense - 0.009d)
            {
                changed = true;
                continue;
            }

            break;
        }

        return changed;
    }

    private static void ForceDecreaseExpenseRecords(IReadOnlyList<FlowRecord> candidates, double amount)
    {
        var remaining = RoundMoney(Math.Max(0, amount));
        foreach (var record in candidates)
        {
            if (remaining <= 0.009d)
            {
                break;
            }

            var absolute = Math.Abs(record.TradeMoney ?? 0);
            if (absolute <= 0.009d)
            {
                continue;
            }

            var bounds = GetRecordAmountBounds(record);
            var reducibleToMin = RoundMoney(Math.Max(0, absolute - bounds.Min));
            if (remaining <= reducibleToMin + 0.009d)
            {
                var nextAmount = ClampAmountToBounds(absolute - remaining, bounds);
                ApplySignedAmount(record, -1d, nextAmount);
                remaining = 0;
                break;
            }

            if (reducibleToMin > 0.009d)
            {
                ApplySignedAmount(record, -1d, bounds.Min);
                remaining = RoundMoney(remaining - reducibleToMin);
            }

            if (remaining <= 0.009d)
            {
                break;
            }

            if (IsRequiredGeneratedRecord(record))
            {
                continue;
            }

            ApplySignedAmount(record, -1d, 0);
            remaining = RoundMoney(remaining - Math.Min(bounds.Min, absolute));
        }
    }

    private static bool MoveNativeIncomeBeforeNegativeBalances(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance,
        DateTime start,
        DateTime end,
        Random random)
    {
        if (UseHighVolumeNativePostProcessing(request.Bank, records.Count))
        {
            return MoveNativeIncomeBatchBeforeNegativeBalances(records, request, openingBalance, start, random);
        }

        var changed = false;
        var maxAttempts = IsWechatBank(request.Bank) ? 4 : 24;
        for (var attempt = 0; attempt < Math.Min(maxAttempts, records.Count); attempt++)
        {
            ApplyBalances(records, openingBalance);
            if (!TryFindFirstNegativeBalance(records, openingBalance, out var firstNegativeTime, out var minimumBalance)
                || minimumBalance >= -0.009d)
            {
                break;
            }

            var candidates = records
                .Where(item => item.AccountTime > firstNegativeTime)
                .Where(item => item.TradeMoney > 0.009d)
                .Where(item => !IsSystemInterestRecord(item))
                .Where(IsGeneratedReferenceRecord)
                .OrderByDescending(item => Math.Abs(item.TradeMoney ?? 0))
                .ThenBy(item => item.AccountTime ?? DateTime.MaxValue)
                .Take(IsWechatBank(request.Bank) ? 8 : int.MaxValue)
                .ToList();

            var moved = false;
            foreach (var record in candidates)
            {
                if (TryMoveNativeIncomeEarlier(records, request, record, openingBalance, start, firstNegativeTime, minimumBalance, random))
                {
                    changed = true;
                    moved = true;
                    break;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        return changed;
    }

    private static bool MoveNativeIncomeBatchBeforeNegativeBalances(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance,
        DateTime start,
        Random random)
    {
        var changed = false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            ApplyBalances(records, openingBalance);
            if (!TryFindFirstNegativeBalance(records, openingBalance, out var firstNegativeTime, out var minimumBalance)
                || minimumBalance >= -0.009d)
            {
                break;
            }

            var needed = RoundMoney(Math.Abs(minimumBalance) + 1d);
            var candidates = new List<FlowRecord>();
            var gathered = 0d;
            foreach (var record in records
                         .Where(item => item.AccountTime > firstNegativeTime)
                         .Where(item => item.TradeMoney > 0.009d)
                         .Where(item => !IsSystemInterestRecord(item))
                         .Where(IsGeneratedReferenceRecord)
                         .OrderByDescending(item => Math.Abs(item.TradeMoney ?? 0))
                         .ThenBy(item => item.AccountTime ?? DateTime.MaxValue))
            {
                candidates.Add(record);
                gathered = RoundMoney(gathered + Math.Abs(record.TradeMoney ?? 0));
                if (gathered >= needed - 0.009d || candidates.Count >= 256)
                {
                    break;
                }
            }

            if (candidates.Count == 0)
            {
                break;
            }

            var candidateSet = candidates.ToHashSet();
            var state = NativeScheduleState.From(records.Where(item => !candidateSet.Contains(item)));
            var latest = firstNegativeTime.AddSeconds(-1);
            foreach (var candidate in candidates)
            {
                if (ResolveRecordSourceRule(request, candidate) is not GenerateReferenceRule rule)
                {
                    continue;
                }

                if (TryPickNativeTimeBefore(start, latest, rule, random, state, out var accountTime))
                {
                    candidate.AccountTime = accountTime;
                    state.Register(candidate);
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return changed;
    }

    private static bool TryPickNativeTimeBefore(
        DateTime start,
        DateTime latest,
        FlowRuleBase rule,
        Random random,
        NativeScheduleState state,
        out DateTime accountTime)
    {
        accountTime = default;
        if (latest < start)
        {
            return false;
        }

        var candidates = state.GetCandidateDates(start, latest, rule)
            .Where(date => date.Date <= latest.Date)
            .Select(date => new
            {
                Date = date.Date,
                Same = state.GetSameDirectionCount(date, isIncome: true),
                NeighborSame = state.GetNeighborSameDirectionCount(date, isIncome: true),
                Total = state.GetTotalCount(date)
            })
            .OrderBy(item => item.Same)
            .ThenBy(item => item.NeighborSame)
            .ThenBy(item => item.Total)
            .ThenByDescending(item => item.Date)
            .Take(16)
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            var dayStart = MaxDateTime(start, candidate.Date);
            var dayEnd = MinDateTime(latest, candidate.Date.AddDays(1).AddTicks(-1));
            if (dayStart > dayEnd)
            {
                continue;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                var time = TrimToSecond(PickTime(dayStart, dayEnd, rule, random));
                if (!state.IsTimeUsed(time))
                {
                    state.MarkTime(time);
                    accountTime = time;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryMoveNativeIncomeEarlier(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        FlowRecord record,
        double openingBalance,
        DateTime start,
        DateTime firstNegativeTime,
        double originalMinimumBalance,
        Random random)
    {
        if (!record.AccountTime.HasValue || ResolveRecordSourceRule(request, record) is not GenerateReferenceRule rule)
        {
            return false;
        }

        var originalTime = record.AccountTime.Value;
        var latestTime = firstNegativeTime.AddSeconds(-1);
        if (latestTime < start)
        {
            return false;
        }

        var baseState = NativeScheduleState.From(records.Where(item => !ReferenceEquals(item, record)));
        var candidateDates = EnumerateNativeCandidateDates(start, latestTime, rule)
            .Select(date => new
            {
                Date = date,
                Same = baseState.GetSameDirectionCount(date, isIncome: true),
                Total = baseState.GetTotalCount(date)
            })
            .OrderBy(item => item.Date == originalTime.Date ? 1 : 0)
            .ThenBy(item => item.Same)
            .ThenBy(item => item.Total)
            .ThenByDescending(item => item.Date)
            .Take(IsWechatBank(request.Bank) ? 6 : 12)
            .ToList();
        if (candidateDates.Count == 0)
        {
            return false;
        }

        DateTime? bestTime = null;
        var bestMinimumBalance = originalMinimumBalance;
        foreach (var candidate in candidateDates)
        {
            var dayStart = MaxDateTime(start, candidate.Date);
            var dayEnd = MinDateTime(latestTime, candidate.Date.AddDays(1).AddTicks(-1));
            if (dayStart > dayEnd)
            {
                continue;
            }

            record.AccountTime = PickTime(dayStart, dayEnd, rule, random);
            ApplyBalances(records, openingBalance);
            var minimumBalance = GetMinimumBalance(records, openingBalance);
            if (minimumBalance >= -0.009d)
            {
                return true;
            }

            if (minimumBalance > bestMinimumBalance + 0.009d)
            {
                bestMinimumBalance = minimumBalance;
                bestTime = record.AccountTime;
            }
        }

        if (bestTime.HasValue)
        {
            record.AccountTime = bestTime.Value;
            ApplyBalances(records, openingBalance);
            return true;
        }

        record.AccountTime = originalTime;
        ApplyBalances(records, openingBalance);
        return false;
    }

    private static bool MoveNativeRecordsToAvoidNegativeBalances(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance,
        DateTime start,
        DateTime end,
        Random random)
    {
        if (UseHighVolumeNativePostProcessing(request.Bank, records.Count))
        {
            return MoveNativeExpenseBatchAfterNegativeBalances(records, request, openingBalance, end, random);
        }

        var changed = false;
        var maxAttempts = IsWechatBank(request.Bank) ? 4 : 12;
        for (var attempt = 0; attempt < Math.Min(maxAttempts, records.Count); attempt++)
        {
            ApplyBalances(records, openingBalance);
            if (!TryFindFirstNegativeBalance(records, openingBalance, out var firstNegativeTime, out var minimumBalance)
                || minimumBalance >= -0.009d)
            {
                break;
            }

            var candidates = records
                .Where(item => item.AccountTime <= firstNegativeTime)
                .Where(item => item.TradeMoney < -0.009d)
                .Where(item => !IsSystemInterestRecord(item))
                .Where(IsGeneratedReferenceRecord)
                .OrderByDescending(item => Math.Abs(item.TradeMoney ?? 0))
                .ThenByDescending(item => item.AccountTime ?? DateTime.MinValue)
                .Take(IsWechatBank(request.Bank) ? 8 : int.MaxValue)
                .ToList();

            var moved = false;
            foreach (var record in candidates)
            {
                if (TryMoveNativeExpenseLater(records, request, record, openingBalance, start, end, firstNegativeTime, minimumBalance, random))
                {
                    changed = true;
                    moved = true;
                    break;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        return changed;
    }

    private static bool MoveNativeExpenseBatchAfterNegativeBalances(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance,
        DateTime end,
        Random random)
    {
        var changed = false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            ApplyBalances(records, openingBalance);
            if (!TryFindFirstNegativeBalance(records, openingBalance, out var firstNegativeTime, out var minimumBalance)
                || minimumBalance >= -0.009d)
            {
                break;
            }

            var needed = RoundMoney(Math.Abs(minimumBalance) + 1d);
            var candidates = new List<FlowRecord>();
            var gathered = 0d;
            foreach (var record in records
                         .Where(item => item.AccountTime <= firstNegativeTime)
                         .Where(item => item.TradeMoney < -0.009d)
                         .Where(item => !IsSystemInterestRecord(item))
                         .Where(IsGeneratedReferenceRecord)
                         .OrderBy(item => IsRequiredGeneratedRecord(item) ? 1 : 0)
                         .ThenByDescending(item => Math.Abs(item.TradeMoney ?? 0))
                         .ThenByDescending(item => item.AccountTime ?? DateTime.MinValue))
            {
                candidates.Add(record);
                gathered = RoundMoney(gathered + Math.Abs(record.TradeMoney ?? 0));
                if (gathered >= needed - 0.009d || candidates.Count >= 256)
                {
                    break;
                }
            }

            if (candidates.Count == 0)
            {
                break;
            }

            var candidateSet = candidates.ToHashSet();
            var state = NativeScheduleState.From(records.Where(item => !candidateSet.Contains(item)));
            var earliest = firstNegativeTime.AddSeconds(1);
            foreach (var candidate in candidates)
            {
                if (ResolveRecordSourceRule(request, candidate) is not GenerateReferenceRule rule)
                {
                    continue;
                }

                if (TryPickNativeTimeAfter(earliest, end, rule, random, state, out var accountTime))
                {
                    candidate.AccountTime = accountTime;
                    state.Register(candidate);
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return changed;
    }

    private static bool TryPickNativeTimeAfter(
        DateTime earliest,
        DateTime end,
        FlowRuleBase rule,
        Random random,
        NativeScheduleState state,
        out DateTime accountTime)
    {
        accountTime = default;
        if (end < earliest)
        {
            return false;
        }

        var candidates = state.GetCandidateDates(earliest, end, rule)
            .Where(date => date.Date >= earliest.Date)
            .Select(date => new
            {
                Date = date.Date,
                Same = state.GetSameDirectionCount(date, isIncome: false),
                NeighborSame = state.GetNeighborSameDirectionCount(date, isIncome: false),
                Total = state.GetTotalCount(date)
            })
            .OrderBy(item => item.Same)
            .ThenBy(item => item.NeighborSame)
            .ThenBy(item => item.Total)
            .ThenBy(item => item.Date)
            .Take(16)
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            var dayStart = MaxDateTime(earliest, candidate.Date);
            var dayEnd = MinDateTime(end, candidate.Date.AddDays(1).AddTicks(-1));
            if (dayStart > dayEnd)
            {
                continue;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                var time = TrimToSecond(PickTime(dayStart, dayEnd, rule, random));
                if (!state.IsTimeUsed(time))
                {
                    state.MarkTime(time);
                    accountTime = time;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryMoveNativeExpenseLater(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        FlowRecord record,
        double openingBalance,
        DateTime start,
        DateTime end,
        DateTime firstNegativeTime,
        double originalMinimumBalance,
        Random random)
    {
        if (!record.AccountTime.HasValue || ResolveRecordSourceRule(request, record) is not GenerateReferenceRule rule)
        {
            return false;
        }

        var originalTime = record.AccountTime.Value;
        var amount = Math.Abs(record.TradeMoney ?? 0);
        var searchStart = MaxDateTime(start.Date, MaxDateTime(originalTime.Date, firstNegativeTime.Date));
        var baseState = NativeScheduleState.From(records.Where(item => !ReferenceEquals(item, record)));
        var candidateDates = EnumerateNativeCandidateDates(searchStart, end, rule)
            .Select(date => new
            {
                Date = date,
                BalanceBeforeDay = EstimateBalanceBeforeDate(records, openingBalance, date, record),
                Same = baseState.GetSameDirectionCount(date, isIncome: false),
                NeighborSame = baseState.GetNeighborSameDirectionCount(date, isIncome: false),
                Total = baseState.GetTotalCount(date)
            })
            .OrderByDescending(item => item.BalanceBeforeDay >= amount)
            .ThenByDescending(item => item.BalanceBeforeDay)
            .ThenBy(item => item.Same)
            .ThenBy(item => item.NeighborSame)
            .ThenBy(item => item.Total)
            .ThenBy(item => item.Date)
            .Take(IsWechatBank(request.Bank) ? 4 : 8)
            .ToList();
        if (candidateDates.Count == 0)
        {
            return false;
        }

        DateTime? bestTime = null;
        var bestMinimumBalance = originalMinimumBalance;
        foreach (var candidate in candidateDates)
        {
            var candidateState = NativeScheduleState.From(records.Where(item => !ReferenceEquals(item, record)));
            record.AccountTime = PickNativeTimeOnDate(candidate.Date, rule, random, candidateState, candidateState.NextSequence());
            ApplyBalances(records, openingBalance);
            var minimumBalance = GetMinimumBalance(records, openingBalance);
            if (minimumBalance >= -0.009d)
            {
                return true;
            }

            if (minimumBalance > bestMinimumBalance + 0.009d)
            {
                bestMinimumBalance = minimumBalance;
                bestTime = record.AccountTime;
            }
        }

        if (bestTime.HasValue)
        {
            record.AccountTime = bestTime.Value;
            ApplyBalances(records, openingBalance);
            return true;
        }

        record.AccountTime = originalTime;
        ApplyBalances(records, openingBalance);
        return false;
    }

    private static double EstimateBalanceBeforeDate(
        IEnumerable<FlowRecord> records,
        double openingBalance,
        DateTime date,
        FlowRecord excludedRecord)
    {
        var balance = openingBalance + records
            .Where(item => !ReferenceEquals(item, excludedRecord))
            .Where(item => item.AccountTime.HasValue && item.AccountTime.Value.Date < date.Date)
            .Sum(item => item.TradeMoney ?? 0);
        return RoundMoney(balance);
    }

    private static double GetMinimumBalance(IEnumerable<FlowRecord> records, double openingBalance)
    {
        return RoundMoney(records
            .Select(item => item.Balance ?? openingBalance)
            .Append(openingBalance)
            .Min());
    }

    private static bool AdjustNativeFinalBalanceWithExistingRecords(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance)
    {
        ApplyBalances(records, openingBalance);
        var targetBalance = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var diff = RoundMoney(finalBalance - targetBalance);
        if (Math.Abs(diff) <= FinalBalanceTolerance)
        {
            return false;
        }

        var beforeNet = CalculateNetAmount(records);
        if (diff > 0)
        {
            IncreaseSignedRecordsWithinBounds(records, isIncome: false, diff);
            var moved = RoundMoney(beforeNet - CalculateNetAmount(records));
            var remaining = RoundMoney(diff - moved);
            if (remaining > 0.009d)
            {
                DecreaseSignedRecordsWithinBounds(records, isIncome: true, remaining, allowDropOptional: true);
            }
        }
        else
        {
            var need = Math.Abs(diff);
            DecreaseSignedRecordsWithinBounds(records, isIncome: false, need, allowDropOptional: true);
            var moved = RoundMoney(CalculateNetAmount(records) - beforeNet);
            var remaining = RoundMoney(need - moved);
            if (remaining > 0.009d)
            {
                IncreaseSignedRecordsWithinBounds(
                    records,
                    isIncome: true,
                    Math.Min(remaining, GetAvailableIncomeRoom(records, request)));
            }
        }

        return Math.Abs(CalculateNetAmount(records) - beforeNet) > 0.009d;
    }

    private static void RestoreFinalBalanceAfterDistinctAmounts(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance)
    {
        if (records.Count == 0)
        {
            return;
        }

        var targetBalance = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ApplyBalances(records, openingBalance);
            var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
            var diff = RoundMoney(finalBalance - targetBalance);
            if (Math.Abs(diff) <= 0.009d)
            {
                if (!EnsureDistinctSignedAmounts(records))
                {
                    break;
                }

                continue;
            }

            var beforeNet = CalculateNetAmount(records);
            if (diff > 0)
            {
                IncreaseSignedRecordsWithinBounds(records, isIncome: false, diff);
                var moved = RoundMoney(beforeNet - CalculateNetAmount(records));
                var remaining = RoundMoney(diff - moved);
                if (remaining > 0.009d)
                {
                    DecreaseSignedRecordsWithinBounds(records, isIncome: true, remaining, allowDropOptional: true);
                }
            }
            else
            {
                var need = Math.Abs(diff);
                DecreaseSignedRecordsWithinBounds(records, isIncome: false, need, allowDropOptional: true);
                var moved = RoundMoney(CalculateNetAmount(records) - beforeNet);
                var remaining = RoundMoney(need - moved);
                if (remaining > 0.009d)
                {
                    IncreaseSignedRecordsWithinBounds(
                        records,
                        isIncome: true,
                        Math.Min(remaining, GetAvailableIncomeRoom(records, request)));
                }
            }

            PruneZeroAmountRecords(records);
            EnsureDistinctSignedAmounts(records);
        }

        PruneZeroAmountRecords(records);
        ApplyBalances(records, openingBalance);
    }

    private static void RestoreFinalBalancePreservingNonNegative(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance)
    {
        if (records.Count == 0)
        {
            return;
        }

        ApplyBalances(records, openingBalance);
        if (GetMinimumBalance(records, openingBalance) < -0.009d)
        {
            return;
        }

        var backup = records
            .Select(item =>
            {
                var copy = item.Clone();
                copy.Id = item.Id;
                return copy;
            })
            .ToList();

        RestoreFinalBalanceAfterDistinctAmounts(records, request, openingBalance);
        ApplyBalances(records, openingBalance);
        if (GetMinimumBalance(records, openingBalance) >= -0.009d)
        {
            return;
        }

        records.Clear();
        records.AddRange(backup);
        ApplyBalances(records, openingBalance);
    }

    private static bool ReducePositiveFinalBalanceSafely(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance)
    {
        if (records.Count == 0)
        {
            return false;
        }

        ApplyBalances(records, openingBalance);
        var targetBalance = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var remaining = RoundMoney(finalBalance - targetBalance);
        if (remaining <= FinalBalanceTolerance)
        {
            return false;
        }

        var suffixMinimumBalances = new double[records.Count];
        var suffixMinimum = double.MaxValue;
        for (var index = records.Count - 1; index >= 0; index--)
        {
            suffixMinimum = Math.Min(suffixMinimum, records[index].Balance ?? openingBalance);
            suffixMinimumBalances[index] = suffixMinimum;
        }

        var changed = false;
        var suffixReduction = 0d;
        var hasNonLowCapacityExpense = records.Any(item =>
            item.TradeMoney < -0.009d
            && !IsSystemInterestRecord(item)
            && !IsGeneratedLowAmountReferenceRecord(item));
        for (var index = records.Count - 1; index >= 0 && remaining > FinalBalanceTolerance; index--)
        {
            var record = records[index];
            if (record.TradeMoney >= -0.009d || IsSystemInterestRecord(record))
            {
                continue;
            }

            if (hasNonLowCapacityExpense && IsGeneratedLowAmountReferenceRecord(record))
            {
                continue;
            }

            var absolute = Math.Abs(record.TradeMoney ?? 0);
            var bounds = GetRecordAmountBounds(record);
            var room = RoundMoney(bounds.Max - absolute);
            var safeRoom = RoundMoney(suffixMinimumBalances[index] - 1d - suffixReduction);
            var allowed = Math.Min(room, safeRoom);
            if (allowed <= 0.009d)
            {
                continue;
            }

            var targetIncrease = Math.Min(allowed, remaining - FinalBalanceTolerance);
            var nextAmount = ClampAmountToBounds(absolute + targetIncrease, bounds);
            var actualIncrease = RoundMoney(nextAmount - absolute);
            if (actualIncrease <= 0.009d
                || actualIncrease > allowed + 0.009d
                || actualIncrease > targetIncrease + 0.009d)
            {
                continue;
            }

            ApplySignedAmount(record, -1d, nextAmount);
            remaining = RoundMoney(remaining - actualIncrease);
            suffixReduction = RoundMoney(suffixReduction + actualIncrease);
            changed = true;
        }

        if (remaining > FinalBalanceTolerance && hasNonLowCapacityExpense)
        {
            var targetExpense = RoundMoney(Math.Max(0, SumIncome(records) - request.Config.LastMoney));
            var lowCapacityLimit = RoundMoney(targetExpense * MaximumLowCapacityTierPoolRatio);
            var currentLowCapacityExpense = RoundMoney(records
                .Where(item => item.TradeMoney < -0.009d)
                .Where(IsGeneratedLowAmountReferenceRecord)
                .Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            var lowCapacityRoom = RoundMoney(Math.Max(0, lowCapacityLimit - currentLowCapacityExpense));
            for (var index = records.Count - 1;
                 index >= 0 && remaining > FinalBalanceTolerance && lowCapacityRoom > 0.009d;
                 index--)
            {
                var record = records[index];
                if (record.TradeMoney >= -0.009d
                    || IsSystemInterestRecord(record)
                    || !IsGeneratedLowAmountReferenceRecord(record))
                {
                    continue;
                }

                var absolute = Math.Abs(record.TradeMoney ?? 0);
                var bounds = GetRecordAmountBounds(record);
                var room = RoundMoney(bounds.Max - absolute);
                var safeRoom = RoundMoney(suffixMinimumBalances[index] - 1d - suffixReduction);
                var allowed = Math.Min(Math.Min(room, safeRoom), lowCapacityRoom);
                if (allowed <= 0.009d)
                {
                    continue;
                }

                var targetIncrease = Math.Min(allowed, remaining - FinalBalanceTolerance);
                var nextAmount = ClampAmountToBounds(absolute + targetIncrease, bounds);
                var actualIncrease = RoundMoney(nextAmount - absolute);
                if (actualIncrease <= 0.009d
                    || actualIncrease > allowed + 0.009d
                    || actualIncrease > targetIncrease + 0.009d)
                {
                    continue;
                }

                ApplySignedAmount(record, -1d, nextAmount);
                remaining = RoundMoney(remaining - actualIncrease);
                suffixReduction = RoundMoney(suffixReduction + actualIncrease);
                lowCapacityRoom = RoundMoney(lowCapacityRoom - actualIncrease);
                changed = true;
            }
        }

        if (changed)
        {
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static bool AddSafeExpenseRecordsForPositiveFinalBalance(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        if (records.Count == 0)
        {
            return false;
        }

        var rules = GetNativeFillReferenceRules(request, isIncome: false, includeRequiredRules: false, random);
        if (rules.Count == 0)
        {
            return false;
        }

        var totalDays = Math.Max(0, (NormalizeEndDate(end).Date - start.Date).Days);
        var topUpStart = start.Date.AddDays((int)Math.Floor(totalDays * 0.35d));
        if (topUpStart > end)
        {
            topUpStart = start;
        }

        var changed = false;
        var scheduleState = NativeScheduleState.From(records);
        var cursor = 0;
        var guard = Math.Max(1000, rules.Count * 64);
        var referenceExpenseCount = records.Count(item =>
            item.TradeMoney < -0.009d && IsGeneratedReferenceRecord(item));
        var lowReferenceExpenseCount = records.Count(item =>
            item.TradeMoney < -0.009d
            && IsGeneratedReferenceRecord(item)
            && GetGeneratedAmountCapacityTier(Math.Abs(item.TradeMoney ?? 0)) == ReferenceCapacityTier.Low);
        while (guard-- > 0)
        {
            ApplyBalances(records, openingBalance);
            var targetBalance = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
            var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
            var balanceDiff = RoundMoney(finalBalance - targetBalance);
            if (balanceDiff <= FinalBalanceTolerance)
            {
                break;
            }

            var incomeTotal = SumIncome(records);
            var targetExpense = RoundMoney(Math.Min(incomeTotal, Math.Max(0, incomeTotal - request.Config.LastMoney)));
            var expenseRoom = RoundMoney(targetExpense - SumExpense(records));
            var remaining = RoundMoney(Math.Min(balanceDiff - FinalBalanceTolerance, expenseRoom));
            if (remaining <= 0.009d)
            {
                break;
            }

            var rule = rules[cursor % rules.Count];
            cursor++;
            var bounds = GetRuleAmountBounds(rule);
            if (remaining < bounds.Min - 0.009d)
            {
                continue;
            }

            var amount = ClampAmountToBounds(Math.Min(bounds.Max, remaining), bounds);
            if (amount < bounds.Min - 0.009d)
            {
                continue;
            }

            var isLowAmount = GetGeneratedAmountCapacityTier(amount) == ReferenceCapacityTier.Low;
            if (isLowAmount
                && (lowReferenceExpenseCount + 1d) / Math.Max(1d, referenceExpenseCount + 1d)
                > MaximumLowRecordRatio + 0.0001d)
            {
                continue;
            }

            FlowRecord? minimumBalanceRecord = null;
            DateTime? latestFloorBalanceTime = null;
            var currentMinimumBalance = double.MaxValue;
            foreach (var item in records)
            {
                if (!item.AccountTime.HasValue)
                {
                    continue;
                }

                var balance = item.Balance ?? openingBalance;
                if (balance < currentMinimumBalance)
                {
                    currentMinimumBalance = balance;
                    minimumBalanceRecord = item;
                }

                if (balance <= 1.009d
                    && (!latestFloorBalanceTime.HasValue || item.AccountTime.Value > latestFloorBalanceTime.Value))
                {
                    latestFloorBalanceTime = item.AccountTime.Value;
                }
            }

            var effectiveStart = latestFloorBalanceTime.HasValue
                ? MaxDateTime(topUpStart, latestFloorBalanceTime.Value.AddSeconds(1))
                : minimumBalanceRecord?.Balance <= 1.009d && minimumBalanceRecord.AccountTime.HasValue
                ? MaxDateTime(topUpStart, minimumBalanceRecord.AccountTime.Value.AddSeconds(1))
                : topUpStart;
            if (effectiveStart > end)
            {
                continue;
            }

            var accountTime = PickNativeDistributedTime(effectiveStart, end, rule, isIncome: false, random, scheduleState);
            var record = CreateRecordFromRule(
                request,
                rule,
                request.Bank.ReferenceColumns,
                accountTime,
                -amount);
            record.ExtraFields[RequiredOccurrenceField] = "false";
            records.Add(record);
            scheduleState.Register(record);
            ApplyBalances(records, openingBalance);

            var minimumBalance = GetMinimumBalance(records, openingBalance);
            if (minimumBalance >= 1d)
            {
                referenceExpenseCount++;
                if (isLowAmount)
                {
                    lowReferenceExpenseCount++;
                }
                changed = true;
                continue;
            }

            var safeAmount = ClampAmountToBounds(amount + minimumBalance - 1d, bounds);
            if (safeAmount >= bounds.Min - 0.009d)
            {
                ApplySignedAmount(record, -1d, safeAmount);
                ApplyBalances(records, openingBalance);
                if (GetMinimumBalance(records, openingBalance) >= -0.009d)
                {
                    referenceExpenseCount++;
                    if (GetGeneratedAmountCapacityTier(safeAmount) == ReferenceCapacityTier.Low)
                    {
                        lowReferenceExpenseCount++;
                    }
                    changed = true;
                    continue;
                }
            }

            records.Remove(record);
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static bool SmoothNativeSchedule(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random)
    {
        return DeduplicateNativeTimes(records, request, start, end, random);
    }

    private static bool EnsureNativeDailyCoverage(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        if (records.Count == 0)
        {
            return false;
        }

        var normalizedStart = start.Date;
        var normalizedEnd = NormalizeEndDate(end).Date;
        if (normalizedEnd < normalizedStart)
        {
            return false;
        }

        var dayCounts = records
            .Where(item => item.AccountTime.HasValue)
            .Select(item => item.AccountTime!.Value.Date)
            .Where(date => date >= normalizedStart && date <= normalizedEnd)
            .GroupBy(date => date)
            .ToDictionary(group => group.Key, group => group.Count());
        var missingDates = EnumerateDates(normalizedStart, normalizedEnd)
            .Where(date => !dayCounts.ContainsKey(date))
            .ToList();
        if (missingDates.Count == 0)
        {
            return false;
        }

        var coverageRandom = new Random(HashCode.Combine(
            request.Bank.Id,
            request.BankUser.Id,
            normalizedStart,
            normalizedEnd,
            records.Count,
            random.Next(),
            0x741C9A2D));
        var changed = false;
        var scheduleState = NativeScheduleState.From(records);
        foreach (var missingDate in missingDates)
        {
            var candidate = FindDailyCoverageMoveCandidate(records, request, dayCounts, missingDate);
            if (candidate is null)
            {
                continue;
            }

            var sourceDate = candidate.Record.AccountTime!.Value.Date;
            var originalTime = candidate.Record.AccountTime.Value;
            candidate.Record.AccountTime = PickNativeTimeOnDate(
                missingDate,
                candidate.Rule,
                coverageRandom,
                scheduleState,
                scheduleState.NextSequence());
            scheduleState.Register(candidate.Record);
            ApplyBalances(records, openingBalance);
            if (GetMinimumBalance(records, openingBalance) < -0.009d)
            {
                candidate.Record.AccountTime = originalTime;
                ApplyBalances(records, openingBalance);
                continue;
            }

            dayCounts[sourceDate] = Math.Max(0, dayCounts.GetValueOrDefault(sourceDate) - 1);
            dayCounts[missingDate] = dayCounts.GetValueOrDefault(missingDate) + 1;
            changed = true;
        }

        return changed;
    }

    private sealed record DailyCoverageMoveCandidate(FlowRecord Record, FlowRuleBase Rule);

    private static DailyCoverageMoveCandidate? FindDailyCoverageMoveCandidate(
        IEnumerable<FlowRecord> records,
        FlowAutoGenerationRequest request,
        IReadOnlyDictionary<DateTime, int> dayCounts,
        DateTime missingDate)
    {
        return records
            .Where(item => item.AccountTime.HasValue)
            .Where(item => dayCounts.GetValueOrDefault(item.AccountTime!.Value.Date) > 1)
            .Where(item => !IsSystemInterestRecord(item))
            .Where(item => !IsRequiredGeneratedRecord(item))
            .Where(IsGeneratedReferenceRecord)
            .Select(item => new
            {
                Record = item,
                Rule = ResolveRecordSourceRule(request, item),
                SourceDate = item.AccountTime!.Value.Date,
                Amount = Math.Abs(item.TradeMoney ?? 0)
            })
            .Where(item => item.Rule is not null && IsNativeDateAllowedForRule(missingDate, item.Rule))
            .OrderByDescending(item => IsBalanceFriendlyDailyCoverageMove(item.Record, missingDate))
            .ThenByDescending(item => IsSameMonth(item.SourceDate, missingDate))
            .ThenByDescending(item => dayCounts.GetValueOrDefault(item.SourceDate))
            .ThenBy(item => item.Amount)
            .ThenBy(item => Math.Abs((item.SourceDate - missingDate.Date).TotalDays))
            .Select(item => new DailyCoverageMoveCandidate(item.Record, item.Rule!))
            .FirstOrDefault();
    }

    private static bool IsNativeDateAllowedForRule(DateTime date, FlowRuleBase? rule)
    {
        if (rule is null)
        {
            return false;
        }

        return rule.TradeWeekend != false
            || date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;
    }

    private static bool IsBalanceFriendlyDailyCoverageMove(FlowRecord record, DateTime targetDate)
    {
        if (!record.AccountTime.HasValue)
        {
            return false;
        }

        var sourceDate = record.AccountTime.Value.Date;
        var amount = record.TradeMoney ?? 0;
        return amount > 0.009d
            ? targetDate.Date <= sourceDate
            : amount < -0.009d
                ? targetDate.Date >= sourceDate
                : true;
    }

    private static bool IsSameMonth(DateTime left, DateTime right)
    {
        return left.Year == right.Year && left.Month == right.Month;
    }

    private sealed record ExpenseRun(IReadOnlyList<FlowRecord> Records);

    private sealed record NativeMoveCandidate(
        FlowRecord Record,
        FlowRuleBase Rule,
        AmountBounds Bounds,
        bool IsGeneratedReference,
        bool IsMovableGeneratedReference,
        bool IsSplittableGeneratedReference);

    private static List<NativeMoveCandidate> BuildNativeMoveCandidates(
        IEnumerable<FlowRecord> records,
        FlowAutoGenerationRequest request)
    {
        var result = new List<NativeMoveCandidate>();
        foreach (var record in records)
        {
            if (!record.AccountTime.HasValue)
            {
                continue;
            }

            var rule = ResolveRecordSourceRule(request, record);
            if (rule is null)
            {
                continue;
            }

            result.Add(new NativeMoveCandidate(
                record,
                rule,
                GetRecordAmountBounds(record),
                IsGeneratedReferenceRecord(record),
                IsMovableGeneratedReferenceRecord(record),
                IsSplittableGeneratedReferenceRecord(record)));
        }

        return result;
    }

    private static bool BalanceNativeIncomeExpenseRhythm(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance)
    {
        if (records.Count < 2)
        {
            return false;
        }

        const int maxConsecutiveExpenses = 6;
        var isWechat = IsWechatBank(request.Bank);
        var highVolumePostProcessing = UseHighVolumeNativePostProcessing(request.Bank, records.Count);
        var maxPasses = isWechat ? 2 : highVolumePostProcessing ? 5 : 10;
        var maxSegmentsPerPass = isWechat ? 16 : highVolumePostProcessing ? 64 : 96;
        var maxIncomeBreaksPerRun = isWechat ? 2 : highVolumePostProcessing ? 12 : 24;
        var maxTotalMoves = isWechat ? 48 : highVolumePostProcessing ? 180 : 360;
        var changed = false;
        var moves = 0;
        var rhythmRandom = new Random(HashCode.Combine(
            request.Bank.Id,
            request.BankUser.Id,
            start.Date,
            NormalizeEndDate(end).Date,
            records.Count,
            random.Next(),
            0x4D72B31A));

        for (var pass = 0; pass < maxPasses && moves < maxTotalMoves; pass++)
        {
            ApplyBalances(records, openingBalance);
            IReadOnlyList<FlowRecord> ordered = records;
            var expenseRuns = FindExpenseRuns(ordered, maxConsecutiveExpenses)
                .OrderByDescending(run => run.Records.Count)
                .Take(maxSegmentsPerPass)
                .ToList();
            if (expenseRuns.Count == 0)
            {
                break;
            }

            var dayCounts = BuildDayCounts(records, start, end);
            var moveCandidates = BuildNativeMoveCandidates(records, request);
            var passChanged = false;
            foreach (var run in expenseRuns)
            {
                if (moves >= maxTotalMoves)
                {
                    break;
                }

                var runChanged = false;
                var targetDates = GetPreferredRunDates(run, maxConsecutiveExpenses)
                    .Take(maxIncomeBreaksPerRun)
                    .ToList();
                foreach (var targetDate in targetDates)
                {
                    if (moves >= maxTotalMoves)
                    {
                        break;
                    }

                    if (TryMoveIncomeToDate(
                            records,
                            request,
                            targetDate,
                            dayCounts,
                            openingBalance,
                            rhythmRandom,
                            requireNonNegativeBalance,
                            moveCandidates)
                        || TrySplitIncomeToDate(
                            records,
                            request,
                            targetDate,
                            dayCounts,
                            openingBalance,
                            rhythmRandom,
                            requireNonNegativeBalance,
                            moveCandidates))
                    {
                        changed = true;
                        passChanged = true;
                        runChanged = true;
                        moves++;
                    }
                }

                if (runChanged || moves >= maxTotalMoves)
                {
                    continue;
                }

                if (TryMoveExpenseOutOfRun(
                        records,
                        request,
                        run,
                        dayCounts,
                        start,
                        end,
                        openingBalance,
                        rhythmRandom,
                        requireNonNegativeBalance,
                        moveCandidates))
                {
                    changed = true;
                    passChanged = true;
                    moves++;
                }
            }

            if (!passChanged)
            {
                break;
            }
        }

        if (changed)
        {
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static bool ShapeMonthlyBalanceSwing(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance)
    {
        if (records.Count < 4)
        {
            return false;
        }

        var months = EnumerateMonths(start, end).ToList();
        if (months.Count == 0)
        {
            return false;
        }

        var isWechat = IsWechatBank(request.Bank);
        var highVolumePostProcessing = UseHighVolumeNativePostProcessing(request.Bank, records.Count);
        var maxMovesPerMonth = isWechat ? 12 : highVolumePostProcessing ? 32 : 48;
        var maxTotalMoves = isWechat ? 96 : highVolumePostProcessing ? 260 : 480;
        var swingRandom = new Random(HashCode.Combine(
            request.Bank.Id,
            request.BankUser.Id,
            start.Date,
            NormalizeEndDate(end).Date,
            records.Count,
            random.Next(),
            0x2C66A91D));
        var dayCounts = BuildDayCounts(records, start, end);
        var changed = false;
        var totalMoves = 0;

        foreach (var month in months)
        {
            if (totalMoves >= maxTotalMoves)
            {
                break;
            }

            var monthStart = month.Start.Date;
            var monthEnd = NormalizeEndDate(month.End).Date;
            var days = Math.Max(1, (monthEnd - monthStart).Days + 1);
            if (days < 3)
            {
                continue;
            }

            var monthRecords = records
                .Where(item => item.AccountTime.HasValue)
                .Where(item => item.AccountTime!.Value.Date >= monthStart && item.AccountTime.Value.Date <= monthEnd)
                .Where(item => !IsSystemInterestRecord(item))
                .ToList();
            var monthMoveCandidates = BuildNativeMoveCandidates(monthRecords, request);
            var monthIncome = RoundMoney(monthRecords.Where(item => (item.TradeMoney ?? 0) > 0.009d).Sum(item => item.TradeMoney ?? 0));
            var monthExpense = RoundMoney(monthRecords.Where(item => (item.TradeMoney ?? 0) < -0.009d).Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            if (monthIncome <= 0.009d || monthExpense <= 0.009d)
            {
                continue;
            }

            var incomeRecordCount = monthRecords.Count(item => (item.TradeMoney ?? 0) > 0.009d);
            var expenseRecordCount = monthRecords.Count(item => (item.TradeMoney ?? 0) < -0.009d);
            var cycleCount = CalculateMonthlySwingCycleCount(days, incomeRecordCount, expenseRecordCount, isWechat);
            if (cycleCount <= 0)
            {
                continue;
            }

            var movesThisMonth = 0;
            var expenseMovesPerCycle = Math.Clamp(
                (int)Math.Ceiling(expenseRecordCount / (double)cycleCount),
                1,
                isWechat ? 3 : 5);
            var cycleSpan = days / (double)cycleCount;
            for (var cycleIndex = 0; cycleIndex < cycleCount && totalMoves < maxTotalMoves && movesThisMonth < maxMovesPerMonth; cycleIndex++)
            {
                var cycleStart = monthStart.AddDays(Math.Min(days - 1, (int)Math.Floor(cycleIndex * cycleSpan)));
                var nextCycleStart = cycleIndex == cycleCount - 1
                    ? monthEnd.AddDays(1)
                    : monthStart.AddDays(Math.Min(days - 1, (int)Math.Floor((cycleIndex + 1) * cycleSpan)));
                if (nextCycleStart <= cycleStart)
                {
                    nextCycleStart = cycleStart.AddDays(1);
                }

                var incomeWindowEnd = MinDateTime(monthEnd, cycleStart.AddDays(Math.Max(0, (int)Math.Floor(cycleSpan * 0.25d))));
                var incomeMoves = MoveMonthSwingRecords(
                    records,
                    request,
                    cycleStart,
                    incomeWindowEnd,
                    dayCounts,
                    openingBalance,
                    swingRandom,
                    requireNonNegativeBalance,
                    isIncome: true,
                    sourceDateFilter: date => date >= monthStart && date <= monthEnd && (date < cycleStart || date > incomeWindowEnd),
                    maxMoves: Math.Min(1, Math.Min(maxMovesPerMonth - movesThisMonth, maxTotalMoves - totalMoves)),
                    monthMoveCandidates);
                movesThisMonth += incomeMoves;
                totalMoves += incomeMoves;
                changed = changed || incomeMoves > 0;

                if (totalMoves >= maxTotalMoves || movesThisMonth >= maxMovesPerMonth)
                {
                    break;
                }

                var expenseStart = MaxDateTime(monthStart, incomeWindowEnd.AddDays(1));
                var expenseEnd = MinDateTime(monthEnd, nextCycleStart.AddDays(-1));
                if (expenseEnd < expenseStart)
                {
                    expenseStart = cycleStart;
                    expenseEnd = MinDateTime(monthEnd, nextCycleStart.AddDays(-1));
                }

                var expenseMoves = MoveMonthSwingRecords(
                    records,
                    request,
                    expenseStart,
                    expenseEnd,
                    dayCounts,
                    openingBalance,
                    swingRandom,
                    requireNonNegativeBalance,
                    isIncome: false,
                    sourceDateFilter: date => date >= monthStart && date <= monthEnd && (date < expenseStart || date > expenseEnd),
                    maxMoves: Math.Min(expenseMovesPerCycle, Math.Min(maxMovesPerMonth - movesThisMonth, maxTotalMoves - totalMoves)),
                    monthMoveCandidates);
                movesThisMonth += expenseMoves;
                totalMoves += expenseMoves;
                changed = changed || expenseMoves > 0;
            }
        }

        if (changed)
        {
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static bool ShapeBalanceHighLowWaves(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        if (records.Count < 4)
        {
            return false;
        }

        var months = EnumerateMonths(start, end).ToList();
        if (months.Count == 0)
        {
            return false;
        }

        var isWechat = IsWechatBank(request.Bank);
        var highVolumePostProcessing = UseHighVolumeNativePostProcessing(request.Bank, records.Count);
        var maxTotalMoves = isWechat ? 80 : highVolumePostProcessing ? 180 : 260;
        var maxMovesPerMonth = isWechat ? 10 : highVolumePostProcessing ? 24 : 36;
        var waveRandom = new Random(HashCode.Combine(
            request.Bank.Id,
            request.BankUser.Id,
            start.Date,
            NormalizeEndDate(end).Date,
            records.Count,
            random.Next(),
            0x4D3772B1));
        var dayCounts = BuildDayCounts(records, start, end);
        var changed = false;
        var totalMoves = 0;

        foreach (var month in months)
        {
            if (totalMoves >= maxTotalMoves)
            {
                break;
            }

            var monthStart = month.Start.Date;
            var monthEnd = NormalizeEndDate(month.End).Date;
            var days = Math.Max(1, (monthEnd - monthStart).Days + 1);
            if (days < 4)
            {
                continue;
            }

            var monthGenerated = records
                .Where(item => item.AccountTime.HasValue)
                .Where(item => item.AccountTime!.Value.Date >= monthStart && item.AccountTime.Value.Date <= monthEnd)
                .Where(item => !IsSystemInterestRecord(item))
                .Where(IsGeneratedReferenceRecord)
                .ToList();
            var monthMoveCandidates = BuildNativeMoveCandidates(monthGenerated, request);
            var incomeCount = monthGenerated.Count(item => (item.TradeMoney ?? 0) > 0.009d);
            var expenseCount = monthGenerated.Count(item => (item.TradeMoney ?? 0) < -0.009d);
            if (incomeCount <= 0 || expenseCount <= 0)
            {
                continue;
            }

            var cycleLimitByDays = Math.Max(1, days / 4);
            var cycleLimitByRecords = Math.Max(1, Math.Min(incomeCount, expenseCount / 2));
            var cycleCount = Math.Clamp(
                cycleLimitByRecords,
                1,
                Math.Min(cycleLimitByDays, isWechat ? 8 : 12));
            var cycleSpan = days / (double)cycleCount;
            var movesThisMonth = 0;

            for (var cycleIndex = 0;
                 cycleIndex < cycleCount && totalMoves < maxTotalMoves && movesThisMonth < maxMovesPerMonth;
                 cycleIndex++)
            {
                var cycleStart = monthStart.AddDays(Math.Min(days - 1, (int)Math.Floor(cycleIndex * cycleSpan)));
                var nextCycleStart = cycleIndex == cycleCount - 1
                    ? monthEnd.AddDays(1)
                    : monthStart.AddDays(Math.Min(days - 1, (int)Math.Floor((cycleIndex + 1) * cycleSpan)));
                if (nextCycleStart <= cycleStart)
                {
                    nextCycleStart = cycleStart.AddDays(1);
                }

                var incomeWindowEnd = MinDateTime(monthEnd, cycleStart.AddDays(Math.Max(0, (int)Math.Floor(cycleSpan * 0.20d))));
                var incomeMoves = MoveMonthSwingRecords(
                    records,
                    request,
                    cycleStart,
                    incomeWindowEnd,
                    dayCounts,
                    openingBalance,
                    waveRandom,
                    requireNonNegativeBalance: false,
                    isIncome: true,
                    sourceDateFilter: date => date >= monthStart
                        && date <= monthEnd
                        && (date < cycleStart || date > incomeWindowEnd),
                    maxMoves: Math.Min(1, Math.Min(maxMovesPerMonth - movesThisMonth, maxTotalMoves - totalMoves)),
                    monthMoveCandidates);
                movesThisMonth += incomeMoves;
                totalMoves += incomeMoves;
                changed = changed || incomeMoves > 0;

                if (totalMoves >= maxTotalMoves || movesThisMonth >= maxMovesPerMonth)
                {
                    break;
                }

                var expenseStart = MaxDateTime(monthStart, incomeWindowEnd.AddDays(1));
                var expenseEnd = MinDateTime(monthEnd, nextCycleStart.AddDays(-1));
                if (expenseEnd < expenseStart)
                {
                    expenseStart = cycleStart;
                    expenseEnd = MinDateTime(monthEnd, nextCycleStart.AddDays(-1));
                }

                var expenseMovesPerCycle = Math.Clamp(
                    (int)Math.Ceiling(expenseCount / (double)Math.Max(1, cycleCount)),
                    2,
                    isWechat ? 4 : 6);
                var expenseMoves = MoveMonthSwingRecords(
                    records,
                    request,
                    expenseStart,
                    expenseEnd,
                    dayCounts,
                    openingBalance,
                    waveRandom,
                    requireNonNegativeBalance: false,
                    isIncome: false,
                    sourceDateFilter: date => date >= monthStart
                        && date <= monthEnd
                        && (date < expenseStart || date > expenseEnd),
                    maxMoves: Math.Min(expenseMovesPerCycle, Math.Min(maxMovesPerMonth - movesThisMonth, maxTotalMoves - totalMoves)),
                    monthMoveCandidates);
                movesThisMonth += expenseMoves;
                totalMoves += expenseMoves;
                changed = changed || expenseMoves > 0;
            }
        }

        if (changed)
        {
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static int CalculateMonthlySwingCycleCount(int days, int incomeRecordCount, int expenseRecordCount, bool isWechat)
    {
        if (days <= 0 || incomeRecordCount <= 0 || expenseRecordCount <= 0)
        {
            return 0;
        }

        var upperByDays = Math.Max(1, days / 2);
        var upperByPerformance = isWechat ? 12 : 18;
        var targetByRecords = Math.Max(1, Math.Min(incomeRecordCount, Math.Max(1, expenseRecordCount / 2)));
        return Math.Clamp(targetByRecords, 1, Math.Min(upperByDays, upperByPerformance));
    }

    private static int MoveMonthSwingRecords(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime targetStart,
        DateTime targetEnd,
        Dictionary<DateTime, int> dayCounts,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance,
        bool isIncome,
        Func<DateTime, bool> sourceDateFilter,
        int maxMoves,
        IReadOnlyList<NativeMoveCandidate>? moveCandidates = null)
    {
        if (maxMoves <= 0 || targetEnd.Date < targetStart.Date)
        {
            return 0;
        }

        var candidates = (moveCandidates ?? BuildNativeMoveCandidates(records, request))
            .Where(item => item.Record.AccountTime.HasValue)
            .Where(item => isIncome ? (item.Record.TradeMoney ?? 0) > 0.009d : (item.Record.TradeMoney ?? 0) < -0.009d)
            .Where(item => item.IsGeneratedReference)
            .Select(item => new
            {
                item.Record,
                item.Rule,
                SourceDate = item.Record.AccountTime!.Value.Date,
                Amount = Math.Abs(item.Record.TradeMoney ?? 0)
            })
            .Where(item => sourceDateFilter(item.SourceDate))
            .Where(item => dayCounts.GetValueOrDefault(item.SourceDate) > 1)
            .OrderByDescending(item => item.Amount)
            .ThenBy(item => isIncome ? item.SourceDate : DateTime.MaxValue.AddTicks(-item.SourceDate.Ticks))
            .Take(Math.Max(maxMoves * 4, 12))
            .ToList();

        var moves = 0;
        var moveState = NativeScheduleState.From(records);
        foreach (var candidate in candidates)
        {
            if (moves >= maxMoves)
            {
                break;
            }

            var targetDate = PickMonthSwingTargetDate(
                records,
                targetStart,
                targetEnd,
                candidate.SourceDate,
                candidate.Rule,
                dayCounts,
                isIncome,
                random,
                moveState);
            if (!targetDate.HasValue)
            {
                continue;
            }

            if (TryMoveRecordToDate(
                    records,
                    candidate.Record,
                    candidate.Rule,
                    targetDate.Value,
                    dayCounts,
                    openingBalance,
                    random,
                    requireNonNegativeBalance,
                    moveState))
            {
                moves++;
            }
        }

        return moves;
    }

    private static DateTime? PickMonthSwingTargetDate(
        IEnumerable<FlowRecord> records,
        DateTime targetStart,
        DateTime targetEnd,
        DateTime sourceDate,
        FlowRuleBase rule,
        IReadOnlyDictionary<DateTime, int> dayCounts,
        bool isIncome,
        Random random,
        NativeScheduleState? scheduleState = null)
    {
        var start = targetStart.Date;
        var end = targetEnd.Date;
        if (end < start)
        {
            return null;
        }

        var preferred = isIncome
            ? start.AddDays(Math.Max(0, (end - start).Days) * 0.25d)
            : start.AddDays(Math.Max(0, (end - start).Days) * 0.75d);
        return EnumerateDates(start, end)
            .Where(date => date != sourceDate)
            .Where(date => IsNativeDateAllowedForRule(date, rule))
            .Select(date => new
            {
                Date = date,
                SameDirectionCount = scheduleState?.GetSameDirectionCount(date, isIncome)
                    ?? CountSameDirectionOnDate(records, date, isIncome),
                TotalCount = dayCounts.GetValueOrDefault(date),
                Distance = Math.Abs((date - preferred).TotalDays)
            })
            .OrderBy(item => item.SameDirectionCount)
            .ThenBy(item => item.TotalCount)
            .ThenBy(item => item.Distance)
            .ThenBy(_ => random.Next())
            .Select(item => (DateTime?)item.Date)
            .FirstOrDefault();
    }

    private static bool BreakNativeIncomeRuns(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance)
    {
        if (records.Count < 2)
        {
            return false;
        }

        const int maxConsecutiveIncomes = 1;
        var isWechat = IsWechatBank(request.Bank);
        var highVolumePostProcessing = UseHighVolumeNativePostProcessing(request.Bank, records.Count);
        var maxPasses = isWechat ? 2 : highVolumePostProcessing ? 4 : 6;
        var maxSegmentsPerPass = isWechat ? 16 : highVolumePostProcessing ? 64 : 96;
        var maxExpenseBreaksPerRun = isWechat ? 2 : highVolumePostProcessing ? 10 : 16;
        var maxTotalMoves = isWechat ? 48 : highVolumePostProcessing ? 180 : 260;
        var changed = false;
        var moves = 0;
        var rhythmRandom = new Random(HashCode.Combine(
            request.Bank.Id,
            request.BankUser.Id,
            start.Date,
            NormalizeEndDate(end).Date,
            records.Count,
            random.Next(),
            0x6E37D4A1));

        for (var pass = 0; pass < maxPasses && moves < maxTotalMoves; pass++)
        {
            ApplyBalances(records, openingBalance);
            IReadOnlyList<FlowRecord> ordered = records;
            var incomeRuns = FindIncomeRuns(ordered, maxConsecutiveIncomes)
                .OrderByDescending(run => run.Records.Count)
                .Take(maxSegmentsPerPass)
                .ToList();
            if (incomeRuns.Count == 0)
            {
                break;
            }

            var dayCounts = BuildDayCounts(records, start, end);
            var moveCandidates = BuildNativeMoveCandidates(records, request);
            var passChanged = false;
            foreach (var run in incomeRuns)
            {
                if (moves >= maxTotalMoves)
                {
                    break;
                }

                var targetDates = GetPreferredRunDates(run, maxConsecutiveIncomes)
                    .Take(maxExpenseBreaksPerRun)
                    .ToList();
                foreach (var targetDate in targetDates)
                {
                    if (moves >= maxTotalMoves)
                    {
                        break;
                    }

                    var breakDates = new[] { targetDate.AddDays(1), targetDate }
                        .Where(date => date.Date >= start.Date && date.Date <= NormalizeEndDate(end).Date)
                        .Distinct()
                        .ToList();
                    foreach (var breakDate in breakDates)
                    {
                        if (TryMoveExpenseToDate(
                                records,
                                request,
                                breakDate,
                                dayCounts,
                                openingBalance,
                                rhythmRandom,
                                requireNonNegativeBalance,
                                moveCandidates))
                        {
                            changed = true;
                            passChanged = true;
                            moves++;
                            break;
                        }
                    }
                }
            }

            if (!passChanged)
            {
                break;
            }
        }

        if (changed)
        {
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static List<ExpenseRun> FindIncomeRuns(IReadOnlyList<FlowRecord> orderedRecords, int maxConsecutiveIncomes)
    {
        var result = new List<ExpenseRun>();
        var current = new List<FlowRecord>();
        foreach (var record in orderedRecords)
        {
            if ((record.TradeMoney ?? 0) > 0.009d)
            {
                current.Add(record);
                continue;
            }

            AddExpenseRunIfNeeded(result, current, maxConsecutiveIncomes);
            current = [];
        }

        AddExpenseRunIfNeeded(result, current, maxConsecutiveIncomes);
        return result;
    }

    private static bool TryMoveExpenseToDate(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime targetDate,
        Dictionary<DateTime, int> dayCounts,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance,
        IReadOnlyList<NativeMoveCandidate>? moveCandidates = null)
    {
        var candidates = (moveCandidates ?? BuildNativeMoveCandidates(records, request))
            .Where(item => (item.Record.TradeMoney ?? 0) < -0.009d)
            .Where(item => item.Record.AccountTime.HasValue)
            .Where(item => item.IsGeneratedReference)
            .Select(item => new
            {
                item.Record,
                item.Rule,
                SourceDate = item.Record.AccountTime!.Value.Date,
                Amount = Math.Abs(item.Record.TradeMoney ?? 0)
            })
            .Where(item => item.SourceDate == targetDate || dayCounts.GetValueOrDefault(item.SourceDate) > 1)
            .Where(item => IsNativeDateAllowedForRule(targetDate, item.Rule))
            .OrderByDescending(item => IsSameMonth(item.SourceDate, targetDate))
            .ThenByDescending(item => item.SourceDate >= targetDate)
            .ThenByDescending(item => dayCounts.GetValueOrDefault(item.SourceDate))
            .ThenByDescending(item => item.Amount)
            .ThenBy(item => Math.Abs((item.SourceDate - targetDate).TotalDays))
            .Take(16)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (TryMoveRecordToDate(
                    records,
                    candidate.Record,
                    candidate.Rule,
                    targetDate,
                    dayCounts,
                    openingBalance,
                    random,
                    requireNonNegativeBalance))
            {
                return true;
            }
        }

        return false;
    }

    private static List<ExpenseRun> FindExpenseRuns(IReadOnlyList<FlowRecord> orderedRecords, int maxConsecutiveExpenses)
    {
        var result = new List<ExpenseRun>();
        var current = new List<FlowRecord>();
        foreach (var record in orderedRecords)
        {
            if ((record.TradeMoney ?? 0) < -0.009d)
            {
                current.Add(record);
                continue;
            }

            AddExpenseRunIfNeeded(result, current, maxConsecutiveExpenses);
            current = [];
        }

        AddExpenseRunIfNeeded(result, current, maxConsecutiveExpenses);
        return result;
    }

    private static void AddExpenseRunIfNeeded(
        ICollection<ExpenseRun> runs,
        List<FlowRecord> current,
        int maxConsecutiveExpenses)
    {
        if (current.Count > maxConsecutiveExpenses)
        {
            runs.Add(new ExpenseRun(current.ToList()));
        }
    }

    private static Dictionary<DateTime, int> BuildDayCounts(IEnumerable<FlowRecord> records, DateTime start, DateTime end)
    {
        var normalizedStart = start.Date;
        var normalizedEnd = NormalizeEndDate(end).Date;
        return records
            .Where(item => item.AccountTime.HasValue)
            .Select(item => item.AccountTime!.Value.Date)
            .Where(date => date >= normalizedStart && date <= normalizedEnd)
            .GroupBy(date => date)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private static bool TryMoveIncomeToDate(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime targetDate,
        Dictionary<DateTime, int> dayCounts,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance,
        IReadOnlyList<NativeMoveCandidate>? moveCandidates = null)
    {
        var candidates = (moveCandidates ?? BuildNativeMoveCandidates(records, request))
            .Where(item => (item.Record.TradeMoney ?? 0) > 0.009d)
            .Where(item => item.IsMovableGeneratedReference)
            .Select(item => new
            {
                item.Record,
                item.Rule,
                SourceDate = item.Record.AccountTime!.Value.Date,
                Amount = Math.Abs(item.Record.TradeMoney ?? 0)
            })
            .Where(item => item.SourceDate == targetDate || dayCounts.GetValueOrDefault(item.SourceDate) > 1)
            .Where(item => IsNativeDateAllowedForRule(targetDate, item.Rule))
            .Where(item => CanMoveIncomeRecordForRhythm(records, item.Record))
            .OrderByDescending(item => targetDate <= item.SourceDate)
            .ThenByDescending(item => IsSameMonth(item.SourceDate, targetDate))
            .ThenByDescending(item => dayCounts.GetValueOrDefault(item.SourceDate))
            .ThenBy(item => item.Amount)
            .ThenBy(item => Math.Abs((item.SourceDate - targetDate).TotalDays))
            .Take(10)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (TryMoveRecordToDate(
                    records,
                    candidate.Record,
                    candidate.Rule,
                    targetDate,
                    dayCounts,
                    openingBalance,
                    random,
                    requireNonNegativeBalance))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySplitIncomeToDate(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime targetDate,
        Dictionary<DateTime, int> dayCounts,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance,
        IReadOnlyList<NativeMoveCandidate>? moveCandidates = null)
    {
        var candidates = (moveCandidates ?? BuildNativeMoveCandidates(records, request))
            .Where(item => (item.Record.TradeMoney ?? 0) > 0.009d)
            .Where(item => item.IsSplittableGeneratedReference)
            .Select(item => new
            {
                item.Record,
                item.Rule,
                SourceDate = item.Record.AccountTime!.Value.Date,
                Amount = Math.Abs(item.Record.TradeMoney ?? 0),
                item.Bounds
            })
            .Where(item => IsNativeDateAllowedForRule(targetDate, item.Rule))
            .Where(item => CanSplitAmount(item.Amount, item.Bounds))
            .OrderByDescending(item => targetDate <= item.SourceDate)
            .ThenByDescending(item => IsSameMonth(item.SourceDate, targetDate))
            .ThenByDescending(item => item.Amount)
            .ThenBy(item => Math.Abs((item.SourceDate - targetDate).TotalDays))
            .Take(10)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (TrySplitRecordToDate(
                    records,
                    candidate.Record,
                    candidate.Rule,
                    targetDate,
                    dayCounts,
                    openingBalance,
                    random,
                    requireNonNegativeBalance))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMoveExpenseOutOfRun(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        ExpenseRun run,
        Dictionary<DateTime, int> dayCounts,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance,
        IReadOnlyList<NativeMoveCandidate>? moveCandidates = null)
    {
        var runRecordSet = new HashSet<FlowRecord>(run.Records);
        var sourceCandidates = moveCandidates is null
            ? BuildNativeMoveCandidates(run.Records, request)
            : moveCandidates.Where(item => runRecordSet.Contains(item.Record));
        var runCandidates = sourceCandidates
            .Where(item => item.IsMovableGeneratedReference)
            .Select(item => new
            {
                item.Record,
                item.Rule,
                SourceDate = item.Record.AccountTime!.Value.Date,
                Amount = Math.Abs(item.Record.TradeMoney ?? 0)
            })
            .Where(item => dayCounts.GetValueOrDefault(item.SourceDate) > 1)
            .OrderBy(item => item.Amount)
            .Take(6)
            .ToList();

        foreach (var candidate in runCandidates)
        {
            var targetDates = EnumerateDates(candidate.SourceDate, NormalizeEndDate(end).Date)
                .Where(date => date >= start.Date && date <= NormalizeEndDate(end).Date)
                .Where(date => date != candidate.SourceDate)
                .Where(date => IsNativeDateAllowedForRule(date, candidate.Rule))
                .OrderBy(date => dayCounts.GetValueOrDefault(date))
                .ThenBy(date => CountSameDirectionOnDate(records, date, isIncome: false))
                .ThenByDescending(date => IsSameMonth(date, candidate.SourceDate))
                .ThenBy(date => Math.Abs((date - candidate.SourceDate).TotalDays))
                .Take(10)
                .ToList();

            foreach (var targetDate in targetDates)
            {
                if (TryMoveRecordToDate(
                        records,
                        candidate.Record,
                        candidate.Rule,
                    targetDate,
                    dayCounts,
                    openingBalance,
                    random,
                    requireNonNegativeBalance))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool EnsureLargeIncomeMonthlyPresence(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance)
    {
        if (records.Count == 0)
        {
            return false;
        }

        var months = EnumerateMonths(start, end).ToList();
        if (months.Count == 0)
        {
            return false;
        }

        const int minimumLargeIncomeRecordsPerMonth = 3;
        var incomeCountsByMonth = records
            .Where(item => (item.TradeMoney ?? 0) > 0.009d && item.AccountTime.HasValue)
            .GroupBy(item => (item.AccountTime!.Value.Year, item.AccountTime.Value.Month))
            .ToDictionary(group => group.Key, group => group.Count());
        var underfilledMonths = months
            .Select(month => new
            {
                Month = month,
                Count = incomeCountsByMonth.GetValueOrDefault((month.Start.Year, month.Start.Month))
            })
            .Where(item => item.Count < minimumLargeIncomeRecordsPerMonth)
            .ToList();
        if (underfilledMonths.Count == 0)
        {
            return false;
        }

        var dayCounts = BuildDayCounts(records, start, end);
        var changed = false;
        foreach (var item in underfilledMonths)
        {
            var needed = minimumLargeIncomeRecordsPerMonth - item.Count;
            for (var index = 0; index < needed; index++)
            {
                var candidates = records
                    .Where(IsSplittableGeneratedIncomeRecord)
                    .Select(record => new
                    {
                        Record = record,
                        Rule = ResolveRecordSourceRule(request, record),
                        Amount = Math.Abs(record.TradeMoney ?? 0),
                        Bounds = GetRecordAmountBounds(record)
                    })
                    .Where(candidate => candidate.Rule is not null)
                    .Where(candidate => candidate.Bounds.Min >= 1000d && candidate.Bounds.Max > candidate.Bounds.Min * 4d)
                    .Where(candidate => CanSplitAmount(candidate.Amount, candidate.Bounds))
                    .OrderByDescending(candidate => candidate.Amount)
                    .Take(16)
                    .ToList();

                foreach (var candidate in candidates)
                {
                    var targetDate = PickMonthlyPresenceDate(
                        item.Month.Start,
                        item.Month.End,
                        candidate.Rule!,
                        dayCounts);
                    if (!targetDate.HasValue)
                    {
                        continue;
                    }

                    if (TrySplitRecordToDate(
                            records,
                            candidate.Record,
                            candidate.Rule!,
                            targetDate.Value,
                            dayCounts,
                            openingBalance,
                            random,
                            requireNonNegativeBalance))
                    {
                        changed = true;
                        break;
                    }
                }
            }
        }

        if (changed)
        {
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static DateTime? PickMonthlyPresenceDate(
        DateTime monthStart,
        DateTime monthEnd,
        FlowRuleBase rule,
        IReadOnlyDictionary<DateTime, int> dayCounts)
    {
        var normalizedStart = monthStart.Date;
        var normalizedEnd = NormalizeEndDate(monthEnd).Date;
        var center = normalizedStart.AddDays(Math.Max(0, (normalizedEnd - normalizedStart).Days) / 2d);
        return EnumerateDates(normalizedStart, normalizedEnd)
            .Where(date => IsNativeDateAllowedForRule(date, rule))
            .OrderBy(date => dayCounts.GetValueOrDefault(date))
            .ThenBy(date => Math.Abs((date - center).TotalDays))
            .Select(date => (DateTime?)date)
            .FirstOrDefault();
    }

    private static bool SplitNativeMaxAmountRecords(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance)
    {
        if (records.Count == 0)
        {
            return false;
        }

        var isWechat = IsWechatBank(request.Bank);
        if (isWechat)
        {
            return false;
        }

        var incomeRecordCount = records.Count(item => (item.TradeMoney ?? 0) > 0.009d);
        var expenseRecordCount = records.Count(item => (item.TradeMoney ?? 0) < -0.009d);
        if (incomeRecordCount == expenseRecordCount)
        {
            return false;
        }

        var splitIncome = incomeRecordCount < expenseRecordCount;
        var maxSplits = Math.Min(1400, Math.Abs(incomeRecordCount - expenseRecordCount));
        if (maxSplits <= 0)
        {
            return false;
        }

        var dayCounts = BuildDayCounts(records, start, end);
        var scheduleState = NativeScheduleState.From(records);
        var changed = false;
        var splitCount = 0;
        var candidates = records
            .Where(IsSplittableGeneratedReferenceRecord)
            .Select(record => new
            {
                Record = record,
                Rule = ResolveRecordSourceRule(request, record),
                Amount = Math.Abs(record.TradeMoney ?? 0),
                IsIncome = (record.TradeMoney ?? 0) > 0.009d,
                Bounds = GetRecordAmountBounds(record)
            })
            .Where(item => item.Rule is not null)
            .Where(item => item.IsIncome == splitIncome)
            .Where(item => IsAtRecordMaxAmount(item.Amount, item.Bounds))
            .Where(item => CanSplitAmount(item.Amount, item.Bounds))
            .OrderByDescending(item => item.Amount)
            .ThenByDescending(item => item.Record.AccountTime ?? DateTime.MinValue)
            .Take(maxSplits)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (splitCount >= maxSplits || !records.Contains(candidate.Record))
            {
                break;
            }

            var currentAmount = Math.Abs(candidate.Record.TradeMoney ?? 0);
            var bounds = GetRecordAmountBounds(candidate.Record);
            if (!IsAtRecordMaxAmount(currentAmount, bounds) || !CanSplitAmount(currentAmount, bounds))
            {
                continue;
            }

            var splitAmount = CalculateBalancedSplitAmount(currentAmount, bounds);
            foreach (var targetDate in GetPreferredMaxSplitDates(
                         candidate.Record,
                         candidate.Rule!,
                         start,
                         end,
                         dayCounts,
                         splitCount,
                         candidate.IsIncome,
                         preferBalanceFriendly: requireNonNegativeBalance))
            {
                if (TrySplitRecordToDate(
                        records,
                        candidate.Record,
                        candidate.Rule!,
                        targetDate,
                        dayCounts,
                        openingBalance,
                        random,
                        requireNonNegativeBalance: false,
                        splitAmount,
                        scheduleState))
                {
                    changed = true;
                    splitCount++;
                    break;
                }
            }
        }

        if (changed)
        {
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static bool IsAtRecordMaxAmount(double amount, AmountBounds bounds)
    {
        return bounds.Max < double.MaxValue / 2
            && bounds.Max > bounds.Min + 0.009d
            && amount >= bounds.Max - Math.Max(0.009d, bounds.Unit / 2d);
    }

    private static double CalculateBalancedSplitAmount(double amount, AmountBounds bounds)
    {
        var half = RoundAmountToUnit(amount / 2d, bounds.Unit);
        var minSplit = bounds.Min;
        var maxSplit = Math.Min(bounds.Max, amount - bounds.Min);
        if (maxSplit < minSplit - 0.009d)
        {
            return 0;
        }

        return ClampAmountToBounds(Math.Clamp(half, minSplit, maxSplit), bounds);
    }

    private static IReadOnlyList<DateTime> GetPreferredMaxSplitDates(
        FlowRecord record,
        FlowRuleBase rule,
        DateTime start,
        DateTime end,
        IReadOnlyDictionary<DateTime, int> dayCounts,
        int splitSequence,
        bool isIncome,
        bool preferBalanceFriendly)
    {
        if (!record.AccountTime.HasValue)
        {
            return [];
        }

        var sourceDate = record.AccountTime.Value.Date;
        var normalizedStart = start.Date;
        var normalizedEnd = NormalizeEndDate(end).Date;
        if (normalizedEnd < normalizedStart)
        {
            (normalizedStart, normalizedEnd) = (normalizedEnd, normalizedStart);
        }

        if (preferBalanceFriendly)
        {
            if (isIncome)
            {
                normalizedEnd = MinDateTime(normalizedEnd, sourceDate);
            }
            else
            {
                normalizedStart = MaxDateTime(normalizedStart, sourceDate);
            }
        }

        var totalDays = Math.Max(0, (normalizedEnd - normalizedStart).Days);
        var spreadRatio = ((splitSequence + 1) * 0.6180339887498948d) % 1d;
        var desiredDate = normalizedStart.AddDays((int)Math.Round(totalDays * spreadRatio));

        var candidates = new List<DateTime>(48);
        var seen = new HashSet<DateTime>();
        for (var offset = 0; offset <= totalDays && candidates.Count < 48; offset++)
        {
            TryAddMaxSplitCandidateDate(desiredDate.AddDays(-offset));
            if (offset > 0)
            {
                TryAddMaxSplitCandidateDate(desiredDate.AddDays(offset));
            }
        }

        var result = candidates
            .OrderBy(date => dayCounts.GetValueOrDefault(date))
            .ThenBy(date => Math.Abs((date - desiredDate).TotalDays))
            .ThenBy(date => date == sourceDate ? 1 : 0)
            .ThenByDescending(date => IsSameMonth(date, sourceDate))
            .ThenBy(date => Math.Abs((date - sourceDate).TotalDays))
            .Take(48)
            .ToList();

        if (result.Count == 0 && IsNativeDateAllowedForRule(sourceDate, rule))
        {
            result.Add(sourceDate);
        }

        return result;

        void TryAddMaxSplitCandidateDate(DateTime date)
        {
            date = date.Date;
            if (date < normalizedStart || date > normalizedEnd || !seen.Add(date))
            {
                return;
            }

            if (IsNativeDateAllowedForRule(date, rule))
            {
                candidates.Add(date);
            }
        }
    }

    private static bool CanSplitAmount(double amount, AmountBounds bounds)
    {
        var splitAmount = CalculateSplitAmount(amount, bounds);
        return splitAmount >= bounds.Min - 0.009d
            && amount - splitAmount >= bounds.Min - 0.009d;
    }

    private static double CalculateSplitAmount(double amount, AmountBounds bounds)
    {
        var maxSplit = Math.Min(bounds.Max, amount - bounds.Min);
        if (maxSplit < bounds.Min - 0.009d)
        {
            return 0;
        }

        return ClampAmountToBounds(Math.Min(maxSplit, bounds.Min), bounds);
    }

    private static bool TrySplitRecordToDate(
        List<FlowRecord> records,
        FlowRecord record,
        FlowRuleBase rule,
        DateTime targetDate,
        Dictionary<DateTime, int> dayCounts,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance,
        double? requestedSplitAmount = null,
        NativeScheduleState? scheduleState = null)
    {
        if (!record.AccountTime.HasValue)
        {
            return false;
        }

        var originalAmount = Math.Abs(record.TradeMoney ?? 0);
        var bounds = GetRecordAmountBounds(record);
        var splitAmount = requestedSplitAmount.HasValue
            ? ClampAmountToBounds(requestedSplitAmount.Value, new AmountBounds(bounds.Min, Math.Min(bounds.Max, originalAmount - bounds.Min), bounds.Unit))
            : CalculateSplitAmount(originalAmount, bounds);
        var remainingAmount = ClampAmountToBounds(originalAmount - splitAmount, bounds);
        if (splitAmount < bounds.Min - 0.009d || remainingAmount < bounds.Min - 0.009d)
        {
            return false;
        }

        var newRecord = record.Clone();
        newRecord.Id = 0;
        newRecord.Index = 0;
        var state = scheduleState ?? NativeScheduleState.From(records);
        newRecord.AccountTime = PickNativeTimeOnDate(
            targetDate,
            rule,
            random,
            state,
            records.Count + dayCounts.GetValueOrDefault(targetDate));
        newRecord.ExtraFields[RequiredOccurrenceField] = "false";

        var sign = (record.TradeMoney ?? 0) < -0.009d ? -1d : 1d;
        ApplySignedAmount(record, sign, remainingAmount);
        ApplySignedAmount(newRecord, sign, splitAmount);
        records.Add(newRecord);
        if (!requireNonNegativeBalance)
        {
            dayCounts[targetDate] = dayCounts.GetValueOrDefault(targetDate) + 1;
            scheduleState?.Register(newRecord);
            return true;
        }

        ApplyBalances(records, openingBalance);
        if (GetMinimumBalance(records, openingBalance) >= -0.009d)
        {
            dayCounts[targetDate] = dayCounts.GetValueOrDefault(targetDate) + 1;
            scheduleState?.Register(newRecord);
            return true;
        }

        records.Remove(newRecord);
        ApplySignedAmount(record, sign, originalAmount);
        ApplyBalances(records, openingBalance);
        return false;
    }

    private static IReadOnlyList<DateTime> GetPreferredRunDates(ExpenseRun run, int maxConsecutiveExpenses)
    {
        var result = new List<(DateTime Date, int Priority)>();
        var priority = 0;
        var firstDate = run.Records.FirstOrDefault()?.AccountTime?.Date ?? DateTime.MinValue;
        if (firstDate != DateTime.MinValue)
        {
            result.Add((firstDate, priority++));
        }

        for (var index = maxConsecutiveExpenses; index < run.Records.Count; index += maxConsecutiveExpenses + 1)
        {
            var date = run.Records[index].AccountTime?.Date ?? DateTime.MinValue;
            if (date != DateTime.MinValue)
            {
                result.Add((date, priority++));
            }
        }

        var center = run.Records.Count / 2;
        var centerDate = run.Records[center].AccountTime?.Date ?? DateTime.MinValue;
        if (centerDate != DateTime.MinValue)
        {
            result.Add((centerDate, priority));
        }

        return result
            .GroupBy(item => item.Date)
            .Select(group => new
            {
                Date = group.Key,
                Priority = group.Min(item => item.Priority)
            })
            .OrderBy(item => item.Priority)
            .Select(item => item.Date)
            .ToList();
    }

    private static bool TryMoveRecordToDate(
        List<FlowRecord> records,
        FlowRecord record,
        FlowRuleBase rule,
        DateTime targetDate,
        Dictionary<DateTime, int> dayCounts,
        double openingBalance,
        Random random,
        bool requireNonNegativeBalance,
        NativeScheduleState? scheduleState = null)
    {
        if (!record.AccountTime.HasValue)
        {
            return false;
        }

        var originalTime = record.AccountTime.Value;
        var sourceDate = originalTime.Date;
        var state = scheduleState ?? NativeScheduleState.From(records.Where(item => !ReferenceEquals(item, record)));
        record.AccountTime = PickNativeTimeOnDate(targetDate, rule, random, state, state.NextSequence());
        if (requireNonNegativeBalance)
        {
            ApplyBalances(records, openingBalance);
            if (GetMinimumBalance(records, openingBalance) < -0.009d)
            {
                record.AccountTime = originalTime;
                ApplyBalances(records, openingBalance);
                return false;
            }
        }

        if (sourceDate != targetDate)
        {
            dayCounts[sourceDate] = Math.Max(0, dayCounts.GetValueOrDefault(sourceDate) - 1);
            dayCounts[targetDate] = dayCounts.GetValueOrDefault(targetDate) + 1;
        }

        scheduleState?.Move(record, originalTime);
        return true;
    }

    private static bool IsMovableGeneratedReferenceRecord(FlowRecord record)
    {
        return record.AccountTime.HasValue
            && !IsSystemInterestRecord(record)
            && !IsRequiredGeneratedRecord(record)
            && IsGeneratedReferenceRecord(record);
    }

    private static bool IsSplittableGeneratedIncomeRecord(FlowRecord record)
    {
        return IsSplittableGeneratedReferenceRecord(record)
            && (record.TradeMoney ?? 0) > 0.009d
            && IsGeneratedReferenceRecord(record);
    }

    private static bool CanMoveIncomeRecordForRhythm(IEnumerable<FlowRecord> records, FlowRecord record)
    {
        if ((record.TradeMoney ?? 0) <= 0.009d || !record.AccountTime.HasValue)
        {
            return true;
        }

        var bounds = GetRecordAmountBounds(record);
        if (bounds.Min < 1000d || bounds.Max <= bounds.Min * 4d)
        {
            return true;
        }

        var month = record.AccountTime.Value;
        var sameMonthCount = records.Count(item =>
            (item.TradeMoney ?? 0) > 0.009d
            && item.AccountTime.HasValue
            && item.AccountTime.Value.Year == month.Year
            && item.AccountTime.Value.Month == month.Month);
        return sameMonthCount > 3;
    }

    private static bool IsSplittableGeneratedReferenceRecord(FlowRecord record)
    {
        return record.AccountTime.HasValue
            && !IsSystemInterestRecord(record)
            && !IsRequiredGeneratedRecord(record)
            && Math.Abs(record.TradeMoney ?? 0) > 0.009d
            && IsGeneratedReferenceRecord(record);
    }

    private static int CountSameDirectionOnDate(IEnumerable<FlowRecord> records, DateTime date, bool isIncome)
    {
        return records.Count(item => item.AccountTime.HasValue
            && item.AccountTime.Value.Date == date.Date
            && (isIncome ? (item.TradeMoney ?? 0) > 0.009d : (item.TradeMoney ?? 0) < -0.009d));
    }

    private static bool DeduplicateNativeTimes(
        IEnumerable<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random)
    {
        var changed = false;
        var state = new NativeScheduleState();
        foreach (var record in records.OrderBy(item => item.AccountTime ?? DateTime.MinValue).ThenBy(item => item.Index))
        {
            if (!record.AccountTime.HasValue)
            {
                continue;
            }

            var current = TrimToSecond(record.AccountTime.Value);
            if (!state.IsTimeUsed(current))
            {
                state.Register(record);
                continue;
            }

            var rule = ResolveRecordSourceRule(request, record);
            if (rule is not null)
            {
                var date = record.AccountTime.Value.Date;
                if (date < start.Date || date > end.Date)
                {
                    date = start.Date;
                }

                record.AccountTime = PickNativeTimeOnDate(date, rule, random, state, state.NextSequence());
            }
            else
            {
                var next = current;
                for (var offset = 1; offset < 3600 && state.IsTimeUsed(next); offset++)
                {
                    next = current.AddSeconds(offset);
                }

                record.AccountTime = next;
                state.MarkTime(next);
            }

            changed = true;
            state.Register(record);
        }

        return changed;
    }

    private static FlowRuleBase? ResolveRecordSourceRule(FlowAutoGenerationRequest request, FlowRecord record)
    {
        if (!record.ExtraFields.TryGetValue(SourceKindField, out var sourceKind)
            || !record.ExtraFields.TryGetValue(SourceIndexField, out var sourceIndexText)
            || ParseInt(sourceIndexText) is not { } sourceIndex)
        {
            return null;
        }

        if (sourceKind == ReferenceSourceKind)
        {
            return request.References.FirstOrDefault(item => item.Index == sourceIndex);
        }

        return sourceKind == ConstSourceKind
            ? request.ConstItems.FirstOrDefault(item => item.Index == sourceIndex)
            : null;
    }

    private static bool IsGeneratedReferenceRecord(FlowRecord record)
    {
        return record.ExtraFields.TryGetValue(SourceKindField, out var sourceKind)
            && sourceKind == ReferenceSourceKind;
    }

    private static bool EnforceConfiguredTotals(List<FlowRecord> records, FlowAutoGenerationRequest request)
    {
        var changed = false;
        var targetIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney));
        var incomeTotal = SumIncome(records);
        var incomeExcess = RoundMoney(incomeTotal - targetIncome);
        if (incomeExcess > 0.009d)
        {
            DecreaseSignedRecordsWithinBounds(records, isIncome: true, incomeExcess, allowDropOptional: true);
            PruneZeroAmountRecords(records);
            changed = SumIncome(records) < incomeTotal - 0.009d;
        }

        var expenseTotal = SumExpense(records);
        incomeTotal = SumIncome(records);
        var expenseExcess = RoundMoney(expenseTotal - incomeTotal);
        if (expenseExcess > 0.009d)
        {
            DecreaseSignedRecordsWithinBounds(records, isIncome: false, expenseExcess, allowDropOptional: true);
            PruneZeroAmountRecords(records);
            changed = changed || SumExpense(records) < expenseTotal - 0.009d;
        }

        return changed;
    }

    private static double LimitAdjustmentAmountByTotals(
        IEnumerable<FlowRecord> records,
        FlowAutoGenerationRequest request,
        bool isIncome,
        double amount)
    {
        var limit = isIncome
            ? GetAvailableIncomeRoom(records, request)
            : GetAvailableExpenseRoom(records);
        return RoundMoney(Math.Min(Math.Max(0, amount), limit));
    }

    private static double GetAvailableIncomeRoom(IEnumerable<FlowRecord> records, FlowAutoGenerationRequest request)
    {
        return RoundMoney(Math.Max(0, Math.Max(0, request.Config.AllInMoney) - SumIncome(records)));
    }

    private static double GetAvailableExpenseRoom(IEnumerable<FlowRecord> records)
    {
        return RoundMoney(Math.Max(0, SumIncome(records) - SumExpense(records)));
    }

    private static bool TryFindFirstNegativeBalance(
        IEnumerable<FlowRecord> records,
        double openingBalance,
        out DateTime firstNegativeTime,
        out double minimumBalance)
    {
        firstNegativeTime = default;
        minimumBalance = RoundMoney(openingBalance);
        var orderedRecords = records as IReadOnlyList<FlowRecord>;
        var source = orderedRecords is not null && AreRecordsInBalanceOrder(orderedRecords)
            ? orderedRecords
            : records
                .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
                .ThenBy(item => item.Index)
                .ToList();

        foreach (var record in source)
        {
            var balance = record.Balance ?? minimumBalance;
            if (balance < minimumBalance)
            {
                minimumBalance = balance;
            }

            if (balance < -0.009d)
            {
                firstNegativeTime = record.AccountTime ?? DateTime.MinValue;
                return true;
            }
        }

        return false;
    }

    private static bool ForceAddBalanceAdjustmentRecords(
        ICollection<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        bool isIncome,
        double targetAmount,
        bool allowConstRules = false)
    {
        var remaining = RoundMoney(Math.Max(0, targetAmount));
        if (remaining <= 0.009d)
        {
            return true;
        }

        remaining = LimitAdjustmentAmountByTotals(records, request, isIncome, remaining);
        if (remaining <= 0.009d)
        {
            return false;
        }

        var openingBalance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
        var referenceRules = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .ToList();
        var selectedReferenceRules = SelectNativeAmountFillRules(referenceRules);
        var candidates = selectedReferenceRules
            .Select(item => new BalanceAdjustmentRule((FlowRuleBase)item, request.Bank.ReferenceColumns))
            .Concat(allowConstRules
                ? request.ConstItems
                    .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
                    .Select(item => new BalanceAdjustmentRule((FlowRuleBase)item, request.Bank.ConstColumns))
                : [])
            .OrderBy(item => IsRequiredRule(item.Rule) ? 1 : 0)
            .ThenByDescending(item => GetRuleAmountBounds(item.Rule).Max)
            .ThenBy(item => GetRuleAmountBounds(item.Rule).Min)
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        var changed = false;
        var sequence = 0;
        var maxSequence = IsWechatBank(request.Bank) ? 120 : 2000;
        while (remaining > FinalBalanceTolerance && sequence < maxSequence)
        {
            remaining = LimitAdjustmentAmountByTotals(records, request, isIncome, remaining);
            if (remaining <= 0.009d)
            {
                break;
            }

            var candidate = PickBalanceAdjustmentRule(candidates, remaining);
            if (candidate is null)
            {
                break;
            }

            var bounds = GetRuleAmountBounds(candidate.Rule);
            if (remaining < bounds.Min - 0.009d)
            {
                break;
            }

            var amount = Math.Min(remaining, bounds.Max);
            amount = ClampAmountToBounds(amount, bounds);
            if (amount <= 0.009d)
            {
                break;
            }

            var accountTime = PickAdjustmentTime(
                records,
                openingBalance,
                start,
                end,
                candidate.Rule,
                random,
                sequence,
                isIncome ? amount : -amount);
            records.Add(CreateRecordFromRule(
                request,
                candidate.Rule,
                candidate.RuleColumns,
                accountTime,
                isIncome ? amount : -amount));
            remaining = RoundMoney(Math.Abs(remaining - amount));
            changed = true;
            sequence++;
        }

        return changed;
    }

    private static BalanceAdjustmentRule? PickBalanceAdjustmentRule(
        IReadOnlyList<BalanceAdjustmentRule> candidates,
        double remaining)
    {
        return candidates
            .Where(candidate =>
            {
                var bounds = GetRuleAmountBounds(candidate.Rule);
                return bounds.Min <= remaining + 0.009d;
            })
            .OrderBy(candidate =>
            {
                var bounds = GetRuleAmountBounds(candidate.Rule);
                return bounds.Max >= remaining ? 0 : 1;
            })
            .ThenBy(candidate => GetRuleAmountBounds(candidate.Rule).Min)
            .FirstOrDefault()
            ?? candidates
                .OrderBy(candidate => GetRuleAmountBounds(candidate.Rule).Min)
                .FirstOrDefault();
    }

    private sealed record BalanceAdjustmentRule(
        FlowRuleBase Rule,
        IReadOnlyList<ColumnDefinition> RuleColumns);

    private sealed class MonthlyAmountPlan
    {
        public required IReadOnlyList<(DateTime Start, DateTime End)> Months { get; init; }

        public required double[] IncomeTargets { get; init; }

        public required double[] ExpenseTargets { get; init; }
    }

    private sealed class NativeScheduleState
    {
        private readonly Dictionary<DateTime, int> totalByDay = [];
        private readonly Dictionary<DateTime, int> incomeByDay = [];
        private readonly Dictionary<DateTime, int> expenseByDay = [];
        private readonly HashSet<DateTime> usedTimes = [];
        private readonly Dictionary<string, DateTime[]> candidateDatesByRange = [];
        private int sequence;

        public int NextSequence()
        {
            return sequence++;
        }

        public static NativeScheduleState From(IEnumerable<FlowRecord> records)
        {
            var state = new NativeScheduleState();
            foreach (var record in records)
            {
                state.Register(record);
            }

            return state;
        }

        public void Register(FlowRecord record)
        {
            if (!record.AccountTime.HasValue)
            {
                return;
            }

            var date = record.AccountTime.Value.Date;
            totalByDay[date] = totalByDay.GetValueOrDefault(date) + 1;
            if ((record.TradeMoney ?? 0) > 0.009d)
            {
                incomeByDay[date] = incomeByDay.GetValueOrDefault(date) + 1;
            }
            else if ((record.TradeMoney ?? 0) < -0.009d)
            {
                expenseByDay[date] = expenseByDay.GetValueOrDefault(date) + 1;
            }

            usedTimes.Add(TrimToSecond(record.AccountTime.Value));
        }

        public void Move(FlowRecord record, DateTime originalTime)
        {
            var originalDate = originalTime.Date;
            Decrement(totalByDay, originalDate);
            if ((record.TradeMoney ?? 0) > 0.009d)
            {
                Decrement(incomeByDay, originalDate);
            }
            else if ((record.TradeMoney ?? 0) < -0.009d)
            {
                Decrement(expenseByDay, originalDate);
            }

            usedTimes.Remove(TrimToSecond(originalTime));
            Register(record);
        }

        private static void Decrement(Dictionary<DateTime, int> values, DateTime date)
        {
            date = date.Date;
            var next = values.GetValueOrDefault(date) - 1;
            if (next <= 0)
            {
                values.Remove(date);
            }
            else
            {
                values[date] = next;
            }
        }

        public int GetTotalCount(DateTime date)
        {
            return totalByDay.GetValueOrDefault(date.Date);
        }

        public int GetSameDirectionCount(DateTime date, bool isIncome)
        {
            return isIncome
                ? incomeByDay.GetValueOrDefault(date.Date)
                : expenseByDay.GetValueOrDefault(date.Date);
        }

        public int GetNeighborSameDirectionCount(DateTime date, bool isIncome)
        {
            return GetSameDirectionCount(date.AddDays(-1), isIncome)
                + GetSameDirectionCount(date.AddDays(1), isIncome);
        }

        public bool IsTimeUsed(DateTime value)
        {
            return usedTimes.Contains(TrimToSecond(value));
        }

        public void MarkTime(DateTime value)
        {
            usedTimes.Add(TrimToSecond(value));
        }

        public IReadOnlyList<DateTime> GetCandidateDates(DateTime start, DateTime end, FlowRuleBase rule)
        {
            var normalizedEnd = NormalizeEndDate(end);
            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{start.Date.Ticks}:{normalizedEnd.Date.Ticks}:{rule.TradeWeekend}");
            if (!candidateDatesByRange.TryGetValue(key, out var dates))
            {
                dates = EnumerateNativeCandidateDates(start, normalizedEnd, rule).ToArray();
                candidateDatesByRange[key] = dates;
            }

            return dates;
        }
    }

    private sealed class AmountUsageTracker
    {
        private readonly Dictionary<string, Dictionary<double, int>> usageByGroup = new(StringComparer.Ordinal);

        public static AmountUsageTracker From(IEnumerable<FlowRecord> records)
        {
            var tracker = new AmountUsageTracker();
            foreach (var record in records)
            {
                tracker.Register(record);
            }

            return tracker;
        }

        public void Register(FlowRecord record)
        {
            var sign = GetAmountSign(record.TradeMoney ?? 0);
            if (sign == 0)
            {
                return;
            }

            var sourceKey = GetGeneratedAmountDistributionKey(record);
            var groupKey = CreateGroupKey(sourceKey, sign);
            if (!usageByGroup.TryGetValue(groupKey, out var usage))
            {
                usage = [];
                usageByGroup[groupKey] = usage;
            }

            var amount = RoundMoney(Math.Abs(record.TradeMoney ?? 0));
            usage[amount] = usage.GetValueOrDefault(amount) + 1;
        }

        public Dictionary<double, int> GetUsage(string sourceKey, int sign, AmountBounds bounds)
        {
            if (!usageByGroup.TryGetValue(CreateGroupKey(sourceKey, sign), out var usage)
                || usage.Count == 0)
            {
                return [];
            }

            var result = new Dictionary<double, int>();
            foreach (var (amount, count) in usage)
            {
                var clamped = ClampAmountToBounds(amount, bounds);
                if (clamped < bounds.Min - 0.009d || clamped > bounds.Max + 0.009d)
                {
                    continue;
                }

                result[clamped] = result.GetValueOrDefault(clamped) + count;
            }

            return result;
        }

        private static string CreateGroupKey(string sourceKey, int sign)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sign}:{sourceKey}");
        }
    }

    private sealed class FlowGenerationPerfTrace
    {
        private static readonly FlowGenerationPerfTrace Disabled = new(null);
        private readonly Stopwatch? stopwatch;
        private TimeSpan lastElapsed;

        private FlowGenerationPerfTrace(Stopwatch? stopwatch)
        {
            this.stopwatch = stopwatch;
        }

        public static bool IsEnabled => string.Equals(
            Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_PERF"),
            "1",
            StringComparison.Ordinal);

        public static FlowGenerationPerfTrace Start()
        {
            return IsEnabled ? new FlowGenerationPerfTrace(Stopwatch.StartNew()) : Disabled;
        }

        public void Mark(string stage, ICollection<FlowRecord> records)
        {
            if (stopwatch is null)
            {
                return;
            }

            var elapsed = stopwatch.Elapsed;
            var delta = elapsed - lastElapsed;
            lastElapsed = elapsed;
            Console.Error.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[FlowPerf] {stage}: +{delta.TotalMilliseconds:0} ms, total={elapsed.TotalMilliseconds:0} ms, rows={records.Count}"));
        }
    }

    private static void GenerateNativeConstRecords(
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double incomeCap,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        foreach (var rule in request.ConstItems.Where(item => item.IsCheck))
        {
            if (string.IsNullOrWhiteSpace(rule.FixDay))
            {
                continue;
            }

            var repeatCount = GetNativeRepeatCount(rule.ReCnt, random);
            foreach (var day in ResolveFixedDays(rule, start, end))
            {
                var normalizedDay = NormalizeNativeFixedDay(day, rule, end);
                if (normalizedDay < start.Date || normalizedDay > end.Date)
                {
                    continue;
                }

                for (var index = 0; index < repeatCount; index++)
                {
                    var isIncome = IsIncomeRuleSafe(rule);
                    var availableIncome = RoundMoney(incomeCap - SumIncome(records));
                    if (isIncome && availableIncome <= 0.009d)
                    {
                        continue;
                    }

                    if (!TryCreateNativeUniqueAmount(rule, isIncome ? availableIncome : double.MaxValue, random, preferLower: false, records, isIncome, amountUsage, out var amount))
                    {
                        continue;
                    }

                    var accountTime = PickNativeTimeOnDate(normalizedDay, rule, random, scheduleState, index);
                    var record = CreateRecordFromRule(
                        request,
                        rule,
                        request.Bank.ConstColumns,
                        accountTime,
                        isIncome ? amount : -amount);
                    records.Add(record);
                    scheduleState.Register(record);
                    amountUsage.Register(record);
                }
            }
        }
    }

    private static void GenerateNativeRequiredReferenceRecords(
        FlowAutoGenerationRequest request,
        MonthlyAmountPlan monthlyPlan,
        double incomeCap,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        var requiredRules = request.References
            .Where(item => item.IsCheck && GetRequiredMonthlyCount(item) > 0)
            .ToList();
        if (requiredRules.Count == 0)
        {
            return;
        }

        var requiredIncomeRules = requiredRules.Where(IsIncomeRuleSafe).ToList();
        var minimumIncomePresenceAmount = requiredIncomeRules.Count == 0
            ? 0
            : requiredIncomeRules.Min(rule => GetRuleAmountBounds(rule).Min);

        for (var monthIndex = 0; monthIndex < monthlyPlan.Months.Count; monthIndex++)
        {
            var month = monthlyPlan.Months[monthIndex];
            var futureMonthsWithoutIncome = monthlyPlan.Months
                .Skip(monthIndex + 1)
                .Count(futureMonth => !records.Any(item =>
                    item.TradeMoney > 0.009d
                    && IsRecordInRange(item, futureMonth.Start, futureMonth.End)));
            var futureIncomeReserve = RoundMoney(futureMonthsWithoutIncome * minimumIncomePresenceAmount);
            var orderedRules = requiredIncomeRules
                .OrderBy(_ => random.Next())
                .Concat(requiredRules.Where(rule => !IsIncomeRuleSafe(rule)))
                .ToList();
            foreach (var rule in orderedRules)
            {
                var count = GetRequiredMonthlyCount(rule);
                for (var index = 0; index < count; index++)
                {
                    var isIncome = IsIncomeRuleSafe(rule);
                    var existingMonthTotal = isIncome
                        ? SumIncome(records.Where(item => IsRecordInRange(item, month.Start, month.End)))
                        : SumExpense(records.Where(item => IsRecordInRange(item, month.Start, month.End)));
                    var monthTarget = isIncome ? monthlyPlan.IncomeTargets[monthIndex] : monthlyPlan.ExpenseTargets[monthIndex];
                    var remainingMonthTarget = RoundMoney(Math.Max(0, monthTarget - existingMonthTotal));
                    var availableTotal = isIncome
                        ? RoundMoney(incomeCap - SumIncome(records))
                        : double.MaxValue;
                    var bounds = GetRuleAmountBounds(rule);
                    double available;
                    if (isIncome)
                    {
                        var availableForCurrentMonth = RoundMoney(Math.Max(0, availableTotal - futureIncomeReserve));
                        var monthHasIncome = existingMonthTotal > 0.009d;
                        var desiredForCurrent = monthHasIncome
                            ? remainingMonthTarget
                            : RoundMoney(Math.Max(bounds.Min, remainingMonthTarget));
                        if (desiredForCurrent < bounds.Min - 0.009d
                            || availableForCurrentMonth < bounds.Min - 0.009d)
                        {
                            continue;
                        }

                        available = Math.Min(bounds.Max, Math.Min(desiredForCurrent, availableForCurrentMonth));
                    }
                    else
                    {
                        available = IsWideAmountRange(bounds)
                            ? Math.Min(availableTotal, bounds.Max)
                            : Math.Min(
                                availableTotal,
                                remainingMonthTarget > 0
                                    ? Math.Max(remainingMonthTarget, bounds.Min)
                                    : availableTotal);
                    }

                    if (!TryCreateNativeUniqueAmount(rule, available, random, preferLower: false, records, isIncome, amountUsage, out var amount))
                    {
                        continue;
                    }

                    var accountTime = PickNativeDistributedTime(month.Start, month.End, rule, isIncome, random, scheduleState);
                    var record = CreateRecordFromRule(
                        request,
                        rule,
                        request.Bank.ReferenceColumns,
                        accountTime,
                        isIncome ? amount : -amount);
                    records.Add(record);
                    scheduleState.Register(record);
                    amountUsage.Register(record);
                }
            }
        }
    }

    private static void GenerateNativeOptionalReferenceRecords(
        FlowAutoGenerationRequest request,
        MonthlyAmountPlan monthlyPlan,
        double incomeCap,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        var optionalIncomeRules = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) && GetRequiredMonthlyCount(item) <= 0)
            .OrderBy(item => GetRuleAmountBounds(item).Min)
            .ThenBy(_ => random.Next())
            .ToList();
        var optionalExpenseRules = OrderNativeOptionalReferenceRules(
            request.Bank,
            request.References
            .Where(item => item.IsCheck && !IsIncomeRuleSafe(item) && GetRequiredMonthlyCount(item) <= 0)
                .ToList(),
            isIncome: false,
            random)
            .ToList();

        for (var monthIndex = 0; monthIndex < monthlyPlan.Months.Count; monthIndex++)
        {
            var month = monthlyPlan.Months[monthIndex];
            var monthIncomeTarget = monthlyPlan.IncomeTargets[monthIndex];
            var monthExpenseTarget = monthlyPlan.ExpenseTargets[monthIndex];

            GenerateNativeOptionalForMonth(
                request,
                month.Start,
                month.End,
                optionalIncomeRules,
                isIncome: true,
                targetAmount: CalculateNativeRemainingMonthAmount(records, month.Start, month.End, monthIncomeTarget, isIncome: true, incomeCap),
                incomeCap,
                random,
                records,
                scheduleState,
                amountUsage);

            var monthIncome = SumIncome(records.Where(item => IsRecordInRange(item, month.Start, month.End)));
            var expenseTarget = Math.Min(monthExpenseTarget, Math.Max(0, monthIncome));
            GenerateNativeOptionalForMonth(
                request,
                month.Start,
                month.End,
                optionalExpenseRules,
                isIncome: false,
                targetAmount: CalculateNativeRemainingMonthAmount(records, month.Start, month.End, expenseTarget, isIncome: false, incomeCap),
                incomeCap,
                random,
                records,
                scheduleState,
                amountUsage);
        }
    }

    private static void EnsureNativeOptionalReferenceRuleCoverage(
        FlowAutoGenerationRequest request,
        MonthlyAmountPlan monthlyPlan,
        double incomeCap,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        var existingReferenceCounts = records
            .Where(IsGeneratedReferenceRecord)
            .Select(record => record.ExtraFields.TryGetValue(SourceIndexField, out var sourceIndexText)
                && ParseInt(sourceIndexText) is { } sourceIndex
                    ? sourceIndex
                    : (int?)null)
            .Where(index => index.HasValue)
            .Select(index => index!.Value)
            .GroupBy(index => index)
            .ToDictionary(group => group.Key, group => group.Count());

        var rules = request.References
            .Where(item => item.IsCheck)
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .OrderBy(item => IsIncomeRuleSafe(item) ? 0 : 1)
            .ThenBy(item => GetRuleAmountBounds(item).Min)
            .ThenBy(_ => random.Next())
            .ToList();
        foreach (var rule in rules)
        {
            var targetCount = GetFinalOptionalReferenceCoverageTarget(request.Bank, rule);
            var currentCount = existingReferenceCounts.GetValueOrDefault(rule.Index);
            for (var count = currentCount; count < targetCount; count++)
            {
                var isIncome = IsIncomeRuleSafe(rule);
                var bounds = GetRuleAmountBounds(rule);
                var availableTotal = isIncome
                    ? RoundMoney(Math.Max(0, incomeCap - SumIncome(records)))
                    : RoundMoney(Math.Max(0, SumIncome(records) - SumExpense(records)));
                if (!TrySelectNativeCoverageMonth(
                        monthlyPlan,
                        rule,
                        isIncome,
                        availableTotal,
                        records,
                        scheduleState,
                        random,
                        out var month,
                        out var budget))
                {
                    break;
                }

                if (!TryCreateNativeUniqueAmount(
                        rule,
                        isIncome ? budget : Math.Min(bounds.Max, budget),
                        random,
                        preferLower: false,
                        records,
                        isIncome,
                        amountUsage,
                        out var amount))
                {
                    break;
                }

                var accountTime = PickNativeDistributedTime(month.Start, month.End, rule, isIncome, random, scheduleState);
                var record = CreateRecordFromRule(
                    request,
                    rule,
                    request.Bank.ReferenceColumns,
                    accountTime,
                    isIncome ? amount : -amount);
                record.ExtraFields[RequiredOccurrenceField] = "false";
                records.Add(record);
                scheduleState.Register(record);
                amountUsage.Register(record);
                existingReferenceCounts[rule.Index] = existingReferenceCounts.GetValueOrDefault(rule.Index) + 1;
            }
        }
    }

    private static bool TrySelectNativeCoverageMonth(
        MonthlyAmountPlan monthlyPlan,
        GenerateReferenceRule rule,
        bool isIncome,
        double availableTotal,
        IEnumerable<FlowRecord> records,
        NativeScheduleState scheduleState,
        Random random,
        out (DateTime Start, DateTime End) month,
        out double budget)
    {
        month = default;
        budget = 0;
        var bounds = GetRuleAmountBounds(rule);
        if (availableTotal < bounds.Min - 0.009d)
        {
            return false;
        }

        var candidates = Enumerable.Range(0, monthlyPlan.Months.Count)
            .Select(index =>
            {
                var range = monthlyPlan.Months[index];
                var monthRecords = records.Where(item => IsRecordInRange(item, range.Start, range.End));
                var current = isIncome ? SumIncome(monthRecords) : SumExpense(monthRecords);
                var target = isIncome ? monthlyPlan.IncomeTargets[index] : monthlyPlan.ExpenseTargets[index];
                var fundingRoom = isIncome
                    ? availableTotal
                    : Math.Min(availableTotal, Math.Max(0, SumIncome(monthRecords) - SumExpense(monthRecords)));
                var remaining = RoundMoney(Math.Min(fundingRoom, Math.Max(0, target - current)));
                return new
                {
                    Range = range,
                    Current = current,
                    Target = target,
                    Remaining = remaining,
                    Completion = target > 0.009d ? current / target : double.MaxValue
                };
            })
            .Where(item => item.Remaining >= bounds.Min - 0.009d)
            .Where(item => scheduleState.GetCandidateDates(item.Range.Start, item.Range.End, rule).Count > 0)
            .OrderBy(item => item.Completion)
            .ThenByDescending(item => item.Remaining)
            .ThenBy(_ => random.Next())
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        var selected = candidates[0];
        month = selected.Range;
        budget = RoundMoney(Math.Min(bounds.Max, selected.Remaining));
        return budget >= bounds.Min - 0.009d;
    }

    private static bool EnsureNativeFinalReferenceRuleCoverage(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        if (request.References.Count == 0)
        {
            return false;
        }

        var existingReferenceCounts = GetExistingGeneratedReferenceIndexCounts(records);
        var coverageRules = request.References
            .Where(item => item.IsCheck)
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .OrderBy(item => IsIncomeRuleSafe(item) ? 0 : 1)
            .ThenBy(item => GetRuleAmountBounds(item).Min)
            .ThenBy(_ => random.Next())
            .ToList();
        if (coverageRules.Count == 0)
        {
            return false;
        }

        var changed = false;
        var scheduleState = NativeScheduleState.From(records);
        var amountUsage = AmountUsageTracker.From(records);
        foreach (var rule in coverageRules)
        {
            var targetCount = GetFinalOptionalReferenceCoverageTarget(request.Bank, rule);
            var currentCount = existingReferenceCounts.GetValueOrDefault(rule.Index);
            for (var count = currentCount; count < targetCount; count++)
            {
                if (!TryAddFinalReferenceCoverageRecord(
                        records,
                        request,
                        rule,
                        start,
                        end,
                        openingBalance,
                        random,
                        scheduleState,
                        amountUsage))
                {
                    break;
                }

                changed = true;
                existingReferenceCounts[rule.Index] = existingReferenceCounts.GetValueOrDefault(rule.Index) + 1;
                amountUsage = AmountUsageTracker.From(records);
            }
        }

        if (changed)
        {
            PruneZeroAmountRecords(records);
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static Dictionary<int, int> GetExistingGeneratedReferenceIndexCounts(IEnumerable<FlowRecord> records)
    {
        return records
            .Where(IsGeneratedReferenceRecord)
            .Where(item => Math.Abs(item.TradeMoney ?? 0) > 0.009d)
            .Select(record => record.ExtraFields.TryGetValue(SourceIndexField, out var sourceIndexText)
                && ParseInt(sourceIndexText) is { } sourceIndex
                    ? sourceIndex
                    : (int?)null)
            .Where(index => index.HasValue)
            .Select(index => index!.Value)
            .GroupBy(index => index)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private static int GetFinalOptionalReferenceCoverageTarget(Bank bank, GenerateReferenceRule rule)
    {
        var bounds = GetRuleAmountBounds(rule);
        if (!IsIncomeRuleSafe(rule) && bounds.Max <= 100d && bounds.Min <= 10d)
        {
            return 3;
        }

        return 1;
    }

    private static bool TryAddFinalReferenceCoverageRecord(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        GenerateReferenceRule rule,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        var isIncome = IsIncomeRuleSafe(rule);
        var bounds = GetRuleAmountBounds(rule);
        var compensationCandidates = GetFinalReferenceCoverageCompensationCandidates(records, isIncome, bounds)
            .ToList();
        var reducibleCapacity = CalculateReducibleSignedCapacity(compensationCandidates);
        var compensationUnit = GetFinalReferenceCoverageCompensationUnit(compensationCandidates, bounds);
        var budget = Math.Min(bounds.Max, FloorAmountToUnit(reducibleCapacity, compensationUnit));
        if (budget < bounds.Min - 0.009d)
        {
            return false;
        }

        var attempts = Math.Min(12, Math.Max(3, compensationCandidates.Count));
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (!TryCreateNativeUniqueAmount(
                    rule,
                    budget,
                    random,
                    preferLower: false,
                    records,
                    isIncome,
                    amountUsage,
                    out var amount))
            {
                return false;
            }

            amount = ClampAmountToBounds(RoundAmountToUnit(amount, compensationUnit), bounds);
            if (amount < bounds.Min - 0.009d || amount > budget + 0.009d)
            {
                continue;
            }

            var candidateSnapshot = CaptureRecordAmountSnapshot(compensationCandidates);
            var beforeTotal = GetNativeSignedTotal(records, isIncome);
            if (!DecreaseFinalReferenceCoverageCompensation(compensationCandidates, isIncome, amount))
            {
                RestoreRecordAmounts(candidateSnapshot);
                continue;
            }

            var afterDecreaseTotal = GetNativeSignedTotal(records, isIncome);
            var actualDecrease = RoundMoney(beforeTotal - afterDecreaseTotal);
            if (Math.Abs(actualDecrease - amount) > 0.009d)
            {
                RestoreRecordAmounts(candidateSnapshot);
                continue;
            }

            var accountTime = isIncome
                ? PickNativeDistributedTime(start, end, rule, isIncome: true, random, scheduleState)
                : PickAdjustmentTime(records, openingBalance, start, end, rule, random, attempt, -amount);
            var record = CreateRecordFromRule(
                request,
                rule,
                request.Bank.ReferenceColumns,
                accountTime,
                isIncome ? amount : -amount);
            record.ExtraFields[RequiredOccurrenceField] = "false";
            records.Add(record);
            scheduleState.Register(record);

            ApplyBalances(records, openingBalance);
            if (GetMinimumBalance(records, openingBalance) >= -0.009d
                && SumIncome(records) <= Math.Max(0, request.Config.AllInMoney) + 0.009d
                && SumExpense(records) <= SumIncome(records) + 0.009d)
            {
                return true;
            }

            records.Remove(record);
            RestoreRecordAmounts(candidateSnapshot);
            ApplyBalances(records, openingBalance);
        }

        return false;
    }

    private static IEnumerable<FlowRecord> GetFinalReferenceCoverageCompensationCandidates(
        IEnumerable<FlowRecord> records,
        bool isIncome,
        AmountBounds targetBounds)
    {
        return GetSignedRecords(records, isIncome)
            .Where(IsGeneratedReferenceRecord)
            .Where(item => Math.Abs(item.TradeMoney ?? 0) > 0.009d)
            .Where(item => !IsLowValueFinalCoverageRecord(item))
            .Where(item => GetRecordReducibleSignedCapacity(item) > 0.009d)
            .OrderBy(item => IsSmallDiscreteAmountRecord(item) ? 1 : 0)
            .ThenBy(item => IsRequiredGeneratedRecord(item) ? 1 : 0)
            .ThenBy(item => GetRecordAmountBounds(item).Unit > targetBounds.Unit + 0.0000001d ? 1 : 0)
            .ThenBy(item => GetRecordAmountBounds(item).Unit)
            .ThenByDescending(item => GetRecordAmountBounds(item).Max)
            .ThenByDescending(item => Math.Abs(item.TradeMoney ?? 0))
            .ThenByDescending(item => item.AccountTime ?? DateTime.MinValue);
    }

    private static bool IsLowValueFinalCoverageRecord(FlowRecord record)
    {
        var bounds = GetRecordAmountBounds(record);
        return bounds.Min <= 10d && bounds.Max <= 100d;
    }

    private static double GetFinalReferenceCoverageCompensationUnit(
        IReadOnlyList<FlowRecord> compensationCandidates,
        AmountBounds targetBounds)
    {
        var candidateUnit = compensationCandidates
            .Select(item => GetRecordAmountBounds(item).Unit)
            .Where(item => item > 0.0000001d)
            .DefaultIfEmpty(targetBounds.Unit)
            .Min();
        return Math.Max(targetBounds.Unit, candidateUnit);
    }

    private static bool DecreaseFinalReferenceCoverageCompensation(
        IReadOnlyList<FlowRecord> candidates,
        bool isIncome,
        double amount)
    {
        var remaining = RoundMoney(Math.Max(0, amount));
        var sign = isIncome ? 1d : -1d;
        foreach (var record in candidates)
        {
            if (remaining <= 0.009d)
            {
                break;
            }

            var absolute = Math.Abs(record.TradeMoney ?? 0);
            var bounds = GetRecordAmountBounds(record);
            var reducible = RoundMoney(absolute - bounds.Min);
            if (reducible <= 0.009d)
            {
                continue;
            }

            var reduction = Math.Min(reducible, FloorAmountToUnit(remaining, bounds.Unit));
            if (reduction <= 0.009d)
            {
                continue;
            }

            ApplySignedAmount(record, sign, ClampAmountToBounds(absolute - reduction, bounds));
            remaining = RoundMoney(remaining - reduction);
        }

        return remaining <= 0.009d;
    }

    private static bool EnsureConfiguredFractionalReferenceAmounts(
        List<FlowRecord> records,
        Random random)
    {
        var changed = false;
        var groups = records
            .Where(IsGeneratedReferenceRecord)
            .Where(item => !IsSystemInterestRecord(item))
            .Where(item => Math.Abs(item.TradeMoney ?? 0) > 0.009d)
            .Select(item =>
            {
                var bounds = GetRecordAmountBounds(item);
                return new
                {
                    Record = item,
                    Sign = GetAmountSign(item.TradeMoney ?? 0),
                    Bounds = bounds
                };
            })
            .Where(item => item.Sign != 0 && ShouldPreferFractionalAmount(item.Bounds))
            .GroupBy(item => (item.Sign, Unit: RoundMoney(item.Bounds.Unit)))
            .ToList();

        foreach (var group in groups)
        {
            var groupRecords = group
                .Select(item => item.Record)
                .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
                .ThenBy(item => item.Index)
                .ToList();
            var wholeRecords = groupRecords
                .Where(item => IsWholeAmount(Math.Abs(item.TradeMoney ?? 0)))
                .ToList();

            while (wholeRecords.Count >= 2)
            {
                var paired = false;
                for (var firstIndex = 0; firstIndex < wholeRecords.Count - 1 && !paired; firstIndex++)
                {
                    for (var secondIndex = firstIndex + 1; secondIndex < wholeRecords.Count; secondIndex++)
                    {
                        if (!TryApplyConfiguredFractionalPair(
                                wholeRecords[firstIndex],
                                wholeRecords[secondIndex],
                                group.Key.Sign,
                                random))
                        {
                            continue;
                        }

                        wholeRecords.RemoveAt(secondIndex);
                        wholeRecords.RemoveAt(firstIndex);
                        changed = true;
                        paired = true;
                        break;
                    }
                }

                if (!paired)
                {
                    break;
                }
            }

            if (wholeRecords.Count == 1)
            {
                var wholeRecord = wholeRecords[0];
                foreach (var partner in groupRecords
                             .Where(item => !ReferenceEquals(item, wholeRecord))
                             .Where(item => !IsWholeAmount(Math.Abs(item.TradeMoney ?? 0)))
                             .OrderBy(_ => random.Next()))
                {
                    if (!TryApplyConfiguredFractionalPair(wholeRecord, partner, group.Key.Sign, random))
                    {
                        continue;
                    }

                    changed = true;
                    break;
                }
            }
        }

        return changed;
    }

    private static bool TryApplyConfiguredFractionalPair(
        FlowRecord first,
        FlowRecord second,
        int sign,
        Random random)
    {
        var earlier = (first.AccountTime ?? DateTime.MinValue) <= (second.AccountTime ?? DateTime.MinValue)
            ? first
            : second;
        var later = ReferenceEquals(earlier, first) ? second : first;
        var earlierBounds = GetRecordAmountBounds(earlier);
        var laterBounds = GetRecordAmountBounds(later);
        if (Math.Abs(earlierBounds.Unit - laterBounds.Unit) > 0.0000001d)
        {
            return false;
        }

        var earlierAmount = Math.Abs(earlier.TradeMoney ?? 0);
        var laterAmount = Math.Abs(later.TradeMoney ?? 0);

        // The earlier change always improves the running balance; the later change
        // offsets it, so totals and the final balance stay exactly unchanged.
        var earlierCapacity = sign > 0
            ? earlierBounds.Max - earlierAmount
            : earlierAmount - earlierBounds.Min;
        var laterCapacity = sign > 0
            ? laterAmount - laterBounds.Min
            : laterBounds.Max - laterAmount;
        var maxDelta = Math.Min(earlierCapacity, laterCapacity);
        if (maxDelta < earlierBounds.Unit - 0.0000001d)
        {
            return false;
        }

        var maxStepsBelowOne = (int)Math.Floor((1d - earlierBounds.Unit + 0.0000001d) / earlierBounds.Unit);
        var maxStepsByCapacity = (int)Math.Floor((maxDelta + 0.0000001d) / earlierBounds.Unit);
        var maxSteps = Math.Min(999, Math.Min(maxStepsBelowOne, maxStepsByCapacity));
        if (maxSteps <= 0)
        {
            return false;
        }

        var steps = Enumerable.Range(1, maxSteps)
            .OrderBy(_ => random.Next())
            .ToList();
        foreach (var step in steps)
        {
            var delta = RoundAmountToUnit(step * earlierBounds.Unit, earlierBounds.Unit);
            var nextEarlierAmount = sign > 0
                ? earlierAmount + delta
                : earlierAmount - delta;
            var nextLaterAmount = sign > 0
                ? laterAmount - delta
                : laterAmount + delta;
            nextEarlierAmount = ClampAmountToBounds(nextEarlierAmount, earlierBounds);
            nextLaterAmount = ClampAmountToBounds(nextLaterAmount, laterBounds);
            if (IsWholeAmount(nextEarlierAmount)
                || IsWholeAmount(nextLaterAmount)
                || HasUnnaturalFractionTail(nextEarlierAmount)
                || HasUnnaturalFractionTail(nextLaterAmount))
            {
                continue;
            }

            ApplySignedAmount(earlier, sign, nextEarlierAmount);
            ApplySignedAmount(later, sign, nextLaterAmount);
            return true;
        }

        return false;
    }

    private static double CalculateReducibleSignedCapacity(IEnumerable<FlowRecord> records)
    {
        var capacity = 0d;
        foreach (var record in records)
        {
            var reducible = GetRecordReducibleSignedCapacity(record);
            if (reducible > 0.009d)
            {
                capacity = RoundMoney(capacity + reducible);
            }
        }

        return capacity;
    }

    private static double GetRecordReducibleSignedCapacity(FlowRecord record)
    {
        var absolute = Math.Abs(record.TradeMoney ?? 0);
        var bounds = GetRecordAmountBounds(record);
        return RoundMoney(absolute - bounds.Min);
    }

    private static void CompleteNativeConfiguredTotals(
        FlowAutoGenerationRequest request,
        MonthlyAmountPlan monthlyPlan,
        DateTime start,
        DateTime end,
        double targetIncome,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        FillNativeSignedTotalToTarget(
            request,
            monthlyPlan,
            start,
            end,
            targetIncome,
            isIncome: true,
            random,
            records,
            scheduleState,
            amountUsage);

        var incomeTotal = SumIncome(records);
        var targetExpense = RoundMoney(Math.Min(incomeTotal, Math.Max(0, incomeTotal - request.Config.LastMoney)));
        FillNativeSignedTotalToTarget(
            request,
            monthlyPlan,
            start,
            end,
            targetExpense,
            isIncome: false,
            random,
            records,
            scheduleState,
            amountUsage);
    }

    private static bool RestoreConfiguredIncomeWithMatchedExpense(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        var targetIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney));
        var shortfall = RoundMoney(targetIncome - SumIncome(records));
        if (shortfall <= 0.009d)
        {
            return false;
        }

        var originalRecords = records.ToHashSet();
        var amountSnapshot = CaptureRecordAmountSnapshot(records);
        var incomeBefore = SumIncome(records);
        var expenseBefore = SumExpense(records);
        TransferSmallAmountTotalToLargeRules(
            records,
            request,
            start,
            end,
            openingBalance,
            random,
            isIncome: true,
            shortfall);
        var addedIncome = RoundMoney(SumIncome(records) - incomeBefore);
        if (addedIncome <= 0.009d)
        {
            return false;
        }

        TransferSmallAmountTotalToLargeRules(
            records,
            request,
            start,
            end,
            openingBalance,
            random,
            isIncome: false,
            addedIncome);
        var addedExpense = RoundMoney(SumExpense(records) - expenseBefore);
        ApplyBalances(records, openingBalance);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        if (Math.Abs(addedIncome - addedExpense) <= 0.009d
            && SumIncome(records) <= targetIncome + 0.009d
            && SumExpense(records) <= SumIncome(records) + 0.009d
            && GetMinimumBalance(records, openingBalance) >= -0.009d
            && !IsFinalBalanceOutsideTolerance(finalBalance, openingBalance, request.Config.LastMoney))
        {
            return true;
        }

        records.RemoveAll(item => !originalRecords.Contains(item));
        RestoreRecordAmounts(amountSnapshot);
        ApplyBalances(records, openingBalance);
        return false;
    }

    private static void RedistributeFirstMonthIncomeRecords(
        FlowAutoGenerationRequest request,
        MonthlyAmountPlan monthlyPlan,
        Random random,
        List<FlowRecord> records,
        NativeScheduleState scheduleState,
        double? openingBalance = null)
    {
        var monthCount = monthlyPlan.Months.Count;
        if (monthCount < 4 || records.Count == 0)
        {
            return;
        }

        var monthIncomes = monthlyPlan.Months
            .Select(month => SumIncome(records.Where(item => IsRecordInRange(item, month.Start, month.End))))
            .ToArray();
        var totalIncome = RoundMoney(monthIncomes.Sum());
        var firstIncome = monthIncomes[0];
        if (totalIncome <= 0.009d || firstIncome <= 0.009d)
        {
            return;
        }

        var average = totalIncome / monthCount;
        var otherMax = monthIncomes.Skip(1).DefaultIfEmpty(0).Max();
        if (firstIncome <= average * 1.45d + 0.009d
            || firstIncome <= otherMax * 1.18d + 0.009d)
        {
            return;
        }

        var plannedFirst = monthlyPlan.IncomeTargets.Length > 0 ? monthlyPlan.IncomeTargets[0] : average;
        var softCap = Math.Max(average * 1.18d, plannedFirst * 1.12d);
        if (otherMax > 0.009d)
        {
            softCap = Math.Min(softCap, Math.Max(average, otherMax * 1.08d));
        }

        softCap = RoundMoney(Math.Max(0, softCap));
        if (firstIncome <= softCap + 0.009d)
        {
            return;
        }

        var firstMonth = monthlyPlan.Months[0];
        var movableRecords = records
            .Where(item => item.TradeMoney > 0.009d)
            .Where(item => item.AccountTime.HasValue)
            .Where(item => IsRecordInRange(item, firstMonth.Start, firstMonth.End))
            .Where(item => !IsSystemInterestRecord(item))
            .Where(item => IsGeneratedReferenceRecord(item))
            .Where(item => !IsRequiredGeneratedRecord(item))
            .Select(item => new
            {
                Record = item,
                Rule = ResolveRecordSourceRule(request, item) as GenerateReferenceRule,
                Amount = RoundMoney(Math.Abs(item.TradeMoney ?? 0))
            })
            .Where(item => item.Rule is not null && IsIncomeRuleSafe(item.Rule) && item.Amount > 0.009d)
            .OrderByDescending(item => item.Amount)
            .ThenBy(_ => random.Next())
            .ToList();

        foreach (var item in movableRecords)
        {
            if (monthIncomes[0] <= softCap + 0.009d)
            {
                break;
            }

            var destinationIndex = Enumerable.Range(1, monthCount - 1)
                .Where(index => scheduleState.GetCandidateDates(monthlyPlan.Months[index].Start, monthlyPlan.Months[index].End, item.Rule!).Count > 0)
                .OrderBy(index => monthIncomes[index] / Math.Max(1d, monthlyPlan.IncomeTargets[index]))
                .ThenBy(index => monthIncomes[index])
                .ThenBy(_ => random.Next())
                .FirstOrDefault(-1);
            if (destinationIndex < 1)
            {
                continue;
            }

            var destination = monthlyPlan.Months[destinationIndex];
            var originalTime = item.Record.AccountTime!.Value;
            item.Record.AccountTime = PickNativeDistributedTime(
                destination.Start,
                destination.End,
                item.Rule!,
                isIncome: true,
                random,
                scheduleState);
            scheduleState.Move(item.Record, originalTime);
            if (openingBalance.HasValue)
            {
                ApplyBalances(records, openingBalance.Value);
                if (GetMinimumBalance(records, openingBalance.Value) < -0.009d)
                {
                    var movedTime = item.Record.AccountTime!.Value;
                    item.Record.AccountTime = originalTime;
                    scheduleState.Move(item.Record, movedTime);
                    ApplyBalances(records, openingBalance.Value);
                    continue;
                }
            }

            monthIncomes[0] = RoundMoney(monthIncomes[0] - item.Amount);
            monthIncomes[destinationIndex] = RoundMoney(monthIncomes[destinationIndex] + item.Amount);
        }
    }

    private static void RedistributeMonthlyIncomeRecords(
        FlowAutoGenerationRequest request,
        MonthlyAmountPlan monthlyPlan,
        Random random,
        List<FlowRecord> records,
        double openingBalance)
    {
        var monthCount = monthlyPlan.Months.Count;
        var firstHalfCount = monthCount / 2;
        if (monthCount < 6
            || firstHalfCount <= 0
            || records.Count == 0
            || UseHighVolumeNativePostProcessing(request.Bank, records.Count))
        {
            return;
        }

        var totalIncome = SumIncome(records);
        if (totalIncome <= 0.009d)
        {
            return;
        }

        var targetIncomes = ScaleNativeTargets(monthlyPlan.IncomeTargets, totalIncome, monthCount);
        var targetFirstHalf = targetIncomes.Take(firstHalfCount).Sum();
        var allowedFirstHalf = RoundMoney(Math.Max(totalIncome * 0.60d, targetFirstHalf + (totalIncome * 0.04d)));
        var maxMoves = Math.Min(records.Count, monthCount * 8);
        for (var move = 0; move < maxMoves; move++)
        {
            var monthIncomes = monthlyPlan.Months
                .Select(month => SumIncome(records.Where(item => IsRecordInRange(item, month.Start, month.End))))
                .ToArray();
            var firstHalfIncome = RoundMoney(monthIncomes.Take(firstHalfCount).Sum());
            if (firstHalfIncome <= allowedFirstHalf + 0.009d)
            {
                break;
            }

            var sourceIndexes = Enumerable.Range(0, firstHalfCount)
                .Where(index => monthIncomes[index] > targetIncomes[index] + 0.009d)
                .OrderByDescending(index => monthIncomes[index] - targetIncomes[index])
                .ThenBy(_ => random.Next())
                .ToList();
            if (sourceIndexes.Count == 0)
            {
                break;
            }

            var moved = false;
            foreach (var sourceIndex in sourceIndexes)
            {
                var sourceMonth = monthlyPlan.Months[sourceIndex];
                var sourceExcess = RoundMoney(monthIncomes[sourceIndex] - targetIncomes[sourceIndex]);
                var incomeCandidates = records
                    .Where(item => item.TradeMoney > 0.009d)
                    .Where(item => item.AccountTime.HasValue && IsRecordInRange(item, sourceMonth.Start, sourceMonth.End))
                    .Where(IsGeneratedReferenceRecord)
                    .Where(item => !IsRequiredGeneratedRecord(item) && !IsSystemInterestRecord(item))
                    .Select(item => new
                    {
                        Record = item,
                        Rule = ResolveRecordSourceRule(request, item) as GenerateReferenceRule,
                        Amount = RoundMoney(Math.Abs(item.TradeMoney ?? 0))
                    })
                    .Where(item => item.Rule is not null && IsIncomeRuleSafe(item.Rule))
                    .OrderBy(item => item.Amount > sourceExcess * 1.25d ? 1 : 0)
                    .ThenBy(item => Math.Abs(sourceExcess - item.Amount))
                    .ThenBy(_ => random.Next())
                    .ToList();

                foreach (var candidate in incomeCandidates)
                {
                    var destinationIndexes = Enumerable.Range(firstHalfCount, monthCount - firstHalfCount)
                        .Where(index => index > sourceIndex)
                        .Where(index => NativeScheduleState.From(records)
                            .GetCandidateDates(monthlyPlan.Months[index].Start, monthlyPlan.Months[index].End, candidate.Rule!)
                            .Count > 0)
                        .OrderBy(index => monthIncomes[index] / Math.Max(1d, targetIncomes[index]))
                        .ThenBy(index => monthIncomes[index])
                        .ThenBy(_ => random.Next())
                        .ToList();
                    foreach (var destinationIndex in destinationIndexes)
                    {
                        if (TryMoveNativeIncomeBundleLater(
                                records,
                                request,
                                candidate.Record,
                                candidate.Rule!,
                                monthlyPlan.Months[destinationIndex],
                                openingBalance,
                                random))
                        {
                            moved = true;
                            break;
                        }
                    }

                    if (moved)
                    {
                        break;
                    }
                }

                if (moved)
                {
                    break;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        ApplyBalances(records, openingBalance);
    }

    private static bool TryMoveNativeIncomeBundleLater(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        FlowRecord incomeRecord,
        GenerateReferenceRule incomeRule,
        (DateTime Start, DateTime End) destination,
        double openingBalance,
        Random random)
    {
        if (!incomeRecord.AccountTime.HasValue)
        {
            return false;
        }

        var timeSnapshot = records
            .Select(item => (Record: item, AccountTime: item.AccountTime))
            .ToList();
        var scheduleState = NativeScheduleState.From(records);
        var originalIncomeTime = incomeRecord.AccountTime.Value;
        incomeRecord.AccountTime = PickNativeDistributedTime(
            destination.Start,
            destination.End,
            incomeRule,
            isIncome: true,
            random,
            scheduleState);
        scheduleState.Move(incomeRecord, originalIncomeTime);
        ApplyBalances(records, openingBalance);

        for (var attempt = 0; attempt < 32; attempt++)
        {
            if (!TryFindFirstNegativeBalance(records, openingBalance, out var firstNegativeTime, out var minimumBalance)
                || minimumBalance >= -0.009d)
            {
                return true;
            }

            var moveStart = MaxDateTime(destination.Start, incomeRecord.AccountTime.Value.AddMinutes(5));
            if (moveStart > destination.End)
            {
                break;
            }

            var expenseCandidates = records
                .Where(item => item.AccountTime.HasValue && item.AccountTime <= firstNegativeTime)
                .Where(item => item.TradeMoney < -0.009d)
                .Where(IsGeneratedReferenceRecord)
                .Where(item => !IsRequiredGeneratedRecord(item) && !IsSystemInterestRecord(item))
                .Select(item => new
                {
                    Record = item,
                    Rule = ResolveRecordSourceRule(request, item) as GenerateReferenceRule,
                    Amount = Math.Abs(item.TradeMoney ?? 0)
                })
                .Where(item => item.Rule is not null && !IsIncomeRuleSafe(item.Rule))
                .OrderByDescending(item => item.Amount)
                .ThenBy(_ => random.Next())
                .ToList();
            var expenseMoved = false;
            foreach (var candidate in expenseCandidates)
            {
                if (scheduleState.GetCandidateDates(moveStart, destination.End, candidate.Rule!).Count == 0)
                {
                    continue;
                }

                var originalExpenseTime = candidate.Record.AccountTime!.Value;
                candidate.Record.AccountTime = PickNativeDistributedTime(
                    moveStart,
                    destination.End,
                    candidate.Rule!,
                    isIncome: false,
                    random,
                    scheduleState);
                scheduleState.Move(candidate.Record, originalExpenseTime);
                ApplyBalances(records, openingBalance);
                expenseMoved = true;
                break;
            }

            if (!expenseMoved)
            {
                break;
            }
        }

        foreach (var snapshot in timeSnapshot)
        {
            snapshot.Record.AccountTime = snapshot.AccountTime;
        }

        ApplyBalances(records, openingBalance);
        return false;
    }

    private static void FillNativeSignedTotalToTarget(
        FlowAutoGenerationRequest request,
        MonthlyAmountPlan monthlyPlan,
        DateTime start,
        DateTime end,
        double targetTotal,
        bool isIncome,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        targetTotal = RoundMoney(Math.Max(0, targetTotal));
        if (!isIncome)
        {
            targetTotal = RoundMoney(Math.Min(targetTotal, SumIncome(records)));
        }

        if (targetTotal <= 0.009d)
        {
            return;
        }

        for (var pass = 0; pass < 6; pass++)
        {
            var beforeTotal = GetNativeSignedTotal(records, isIncome);
            var beforeCount = records.Count;
            var remaining = RoundMoney(targetTotal - beforeTotal);
            if (remaining <= 0.009d)
            {
                return;
            }

            var optionalRules = GetNativeFillReferenceRules(request, isIncome, includeRequiredRules: false, random);
            GenerateNativeFillReferenceRecords(
                request,
                monthlyPlan,
                start,
                end,
                optionalRules,
                targetTotal,
                isIncome,
                random,
                records,
                scheduleState,
                amountUsage);

            remaining = RoundMoney(targetTotal - GetNativeSignedTotal(records, isIncome));
            if (remaining <= 0.009d)
            {
                return;
            }

            // Generate from the configured ranges first. Existing rows only absorb the
            // final remainder, otherwise repeatedly filling them pushes values to max.
            IncreaseSignedRecordsWithinBounds(records, isIncome, remaining);
            remaining = RoundMoney(targetTotal - GetNativeSignedTotal(records, isIncome));
            if (remaining <= 0.009d)
            {
                return;
            }

            var afterTotal = GetNativeSignedTotal(records, isIncome);
            if (afterTotal <= beforeTotal + 0.009d && records.Count == beforeCount)
            {
                return;
            }
        }
    }

    private static IReadOnlyList<GenerateReferenceRule> GetNativeFillReferenceRules(
        FlowAutoGenerationRequest request,
        bool isIncome,
        bool includeRequiredRules,
        Random random)
    {
        var rules = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
            .Where(item => GetRequiredMonthlyCount(item) <= 0);

        var orderedRules = OrderNativeFillReferenceRules(request.Bank, rules, isIncome, random);
        return orderedRules;
    }

    private static IReadOnlyList<GenerateReferenceRule> OrderNativeOptionalReferenceRules(
        Bank bank,
        IEnumerable<GenerateReferenceRule> rules,
        bool isIncome,
        Random random)
    {
        if (isIncome || !IsAgriculturalBank(bank))
        {
            return rules
                .OrderBy(item => GetRuleAmountBounds(item).Min)
                .ThenBy(_ => random.Next())
                .ToList();
        }

        return rules
            .OrderBy(item => IsAgriculturalSmallExpenseRule(bank, item) ? 1 : 0)
            .ThenByDescending(item => GetRuleAmountBounds(item).Max)
            .ThenBy(_ => random.Next())
            .ToList();
    }

    private static IReadOnlyList<GenerateReferenceRule> OrderNativeFillReferenceRules(
        Bank bank,
        IEnumerable<GenerateReferenceRule> rules,
        bool isIncome,
        Random random)
    {
        var query = rules.Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome);
        if (!isIncome && IsAgriculturalBank(bank))
        {
            return query
                .OrderBy(item => IsAgriculturalSmallExpenseRule(bank, item) ? 1 : 0)
                .ThenByDescending(item => GetRuleAmountBounds(item).Max)
                .ThenBy(_ => random.Next())
                .ToList();
        }

        return query
            .OrderByDescending(item => GetRuleAmountBounds(item).Max)
            .ThenBy(_ => random.Next())
            .ToList();
    }

    private static void GenerateNativeFillReferenceRecords(
        FlowAutoGenerationRequest request,
        MonthlyAmountPlan monthlyPlan,
        DateTime start,
        DateTime end,
        IReadOnlyList<GenerateReferenceRule> rules,
        double targetTotal,
        bool isIncome,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        if (rules.Count == 0)
        {
            return;
        }

        var monthlyTargets = ScaleNativeTargets(
            isIncome ? monthlyPlan.IncomeTargets : monthlyPlan.ExpenseTargets,
            targetTotal,
            monthlyPlan.Months.Count);

        for (var monthIndex = 0; monthIndex < monthlyPlan.Months.Count; monthIndex++)
        {
            var globalRemaining = RoundMoney(targetTotal - GetNativeSignedTotal(records, isIncome));
            if (globalRemaining <= 0.009d)
            {
                return;
            }

            var month = monthlyPlan.Months[monthIndex];
            var monthCurrent = GetNativeSignedTotal(
                records.Where(item => IsRecordInRange(item, month.Start, month.End)),
                isIncome);
            var monthRemaining = RoundMoney(Math.Max(0, monthlyTargets[monthIndex] - monthCurrent));
            monthRemaining = RoundMoney(Math.Min(monthRemaining, globalRemaining));
            if (monthRemaining <= 0.009d)
            {
                continue;
            }

            GenerateNativeOptionalForMonth(
                request,
                month.Start,
                month.End,
                rules,
                isIncome,
                monthRemaining,
                Math.Max(0, request.Config.AllInMoney),
                random,
                records,
                scheduleState,
                amountUsage);
        }

        var remaining = RoundMoney(targetTotal - GetNativeSignedTotal(records, isIncome));
        if (remaining <= 0.009d)
        {
            return;
        }

        GenerateNativeOptionalForMonth(
            request,
            start,
            end,
            rules,
            isIncome,
            remaining,
            Math.Max(0, request.Config.AllInMoney),
            random,
            records,
            scheduleState,
            amountUsage);
    }

    private static double[] ScaleNativeTargets(IReadOnlyList<double> sourceTargets, double targetTotal, int count)
    {
        targetTotal = RoundMoney(Math.Max(0, targetTotal));
        if (count <= 0)
        {
            return [];
        }

        var result = new double[count];
        if (targetTotal <= 0.009d)
        {
            return result;
        }

        var sourceTotal = RoundMoney(sourceTargets.Take(count).Sum(item => Math.Max(0, item)));
        var runningTotal = 0d;
        for (var index = 0; index < count; index++)
        {
            var target = index == count - 1
                ? RoundMoney(targetTotal - runningTotal)
                : sourceTotal > 0.009d
                    ? RoundMoney(targetTotal * Math.Max(0, sourceTargets[index]) / sourceTotal)
                    : RoundMoney(targetTotal / count);
            result[index] = Math.Max(0, target);
            runningTotal = RoundMoney(runningTotal + result[index]);
        }

        var diff = RoundMoney(targetTotal - result.Sum());
        if (Math.Abs(diff) > 0.009d)
        {
            var index = result
                .Select((value, itemIndex) => new { value, itemIndex })
                .OrderByDescending(item => item.value)
                .First().itemIndex;
            result[index] = RoundMoney(Math.Max(0, result[index] + diff));
        }

        return result;
    }

    private static double GetNativeSignedTotal(IEnumerable<FlowRecord> records, bool isIncome)
    {
        return isIncome ? SumIncome(records) : SumExpense(records);
    }

    private static double CalculateNativeRemainingMonthAmount(
        IEnumerable<FlowRecord> records,
        DateTime monthStart,
        DateTime monthEnd,
        double monthTarget,
        bool isIncome,
        double incomeCap)
    {
        var existing = isIncome
            ? SumIncome(records.Where(item => IsRecordInRange(item, monthStart, monthEnd)))
            : SumExpense(records.Where(item => IsRecordInRange(item, monthStart, monthEnd)));
        var remaining = RoundMoney(Math.Max(0, monthTarget - existing));
        if (isIncome)
        {
            remaining = Math.Min(remaining, RoundMoney(Math.Max(0, incomeCap - SumIncome(records))));
        }

        return RoundMoney(remaining);
    }

    private static void GenerateNativeOptionalForMonth(
        FlowAutoGenerationRequest request,
        DateTime monthStart,
        DateTime monthEnd,
        IReadOnlyList<GenerateReferenceRule> rules,
        bool isIncome,
        double targetAmount,
        double incomeCap,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        var availableRules = rules
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .ToList();
        var remaining = RoundMoney(Math.Max(0, targetAmount));
        if (availableRules.Count == 0 || remaining <= 0.009d)
        {
            return;
        }

        GenerateNativeOptionalByCapacityTier(
            request,
            monthStart,
            monthEnd,
            availableRules,
            isIncome,
            remaining,
            incomeCap,
            random,
            records,
            scheduleState,
            amountUsage);
    }

    private static void GenerateNativeOptionalByCapacityTier(
        FlowAutoGenerationRequest request,
        DateTime monthStart,
        DateTime monthEnd,
        IReadOnlyList<GenerateReferenceRule> rules,
        bool isIncome,
        double targetAmount,
        double incomeCap,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        var groups = CreateCapacityTierRuleGroups(rules, random);
        if (groups.Count == 0)
        {
            return;
        }

        var availableByTotals = isIncome
            ? Math.Max(0, incomeCap - SumIncome(records))
            : Math.Max(0, SumIncome(records) - SumExpense(records));
        var availableTarget = RoundMoney(Math.Min(Math.Max(0, targetAmount), availableByTotals));
        if (availableTarget <= 0.009d)
        {
            return;
        }

        var dailySlots = isIncome
            ? []
            : CreateDailyNaturalConsumptionSlots(
                request,
                monthStart,
                monthEnd,
                rules,
                records,
                random);
        var plans = CreateCapacityTierCountPlans(
            groups,
            dailySlots,
            records,
            monthStart,
            monthEnd,
            isIncome,
            availableTarget,
            random);

        foreach (var plan in plans.OrderByDescending(item => item.Tier))
        {
            var generated = new List<FlowRecord>(plan.Count);
            if (!isIncome && plan.Tier == ReferenceCapacityTier.Low && plan.DailySlots.Count > 0)
            {
                var regularMinimum = CalculateRuleSequenceCapacity(plan.Rules, plan.RegularCount).Minimum;
                var dailyBudget = RoundMoney(Math.Clamp(
                    plan.DailyExpectedTotal,
                    plan.DailyMinimumTotal,
                    Math.Max(plan.DailyMinimumTotal, plan.Budget - regularMinimum)));
                generated.AddRange(GenerateNativeDailyConsumptionRecords(
                    request,
                    plan.DailySlots,
                    dailyBudget,
                    random,
                    records,
                    scheduleState,
                    amountUsage));
            }

            var regularCount = Math.Max(0, plan.Count - generated.Count);
            var regularBudget = RoundMoney(Math.Max(0, plan.Budget - generated.Sum(item => Math.Abs(item.TradeMoney ?? 0))));
            if (regularCount > 0 && regularBudget > 0.009d)
            {
                var result = GenerateNativeOptionalForMonthCore(
                    request,
                    monthStart,
                    monthEnd,
                    plan.Rules,
                    plan.Tier,
                    isIncome,
                    regularBudget,
                    incomeCap,
                    regularCount,
                    random,
                    records,
                    scheduleState,
                    amountUsage);
                generated.AddRange(result.Records);
            }

            var generatedTotal = RoundMoney(generated.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            if (generated.Count > 0 && generatedTotal < plan.Budget - 0.009d)
            {
                IncreaseGeneratedPlanRecordsBalanced(
                    generated,
                    isIncome,
                    plan.Budget - generatedTotal,
                    random);
            }
        }
    }

    private static void IncreaseGeneratedPlanRecordsBalanced(
        List<FlowRecord> records,
        bool isIncome,
        double amount,
        Random random)
    {
        var remaining = RoundMoney(Math.Max(0, amount));
        if (records.Count == 0 || remaining <= 0.009d)
        {
            return;
        }

        var groups = records
            .Where(item => Math.Abs(item.TradeMoney ?? 0) > 0.009d)
            .GroupBy(item =>
            {
                var bounds = GetRecordAmountBounds(item);
                return (
                    SourceKey: GetGeneratedAmountDistributionKey(item),
                    bounds.Min,
                    bounds.Max,
                    bounds.Unit);
            })
            .Select(group =>
            {
                var groupRecords = group.ToList();
                var bounds = new AmountBounds(group.Key.Min, group.Key.Max, group.Key.Unit);
                var current = RoundMoney(groupRecords.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
                var expected = RoundMoney(EstimateNaturalSegmentAverage(bounds) * groupRecords.Count);
                return new
                {
                    Records = groupRecords,
                    Deficit = RoundMoney(Math.Max(0, expected - current))
                };
            })
            .OrderByDescending(item => item.Deficit)
            .ThenBy(_ => random.Next())
            .ToList();

        foreach (var group in groups)
        {
            if (remaining <= 0.009d)
            {
                break;
            }

            var requested = RoundMoney(Math.Min(group.Deficit, remaining));
            if (requested <= 0.009d)
            {
                continue;
            }

            var before = RoundMoney(group.Records.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            IncreaseSignedRecordsWithinBounds(group.Records, isIncome, requested);
            var after = RoundMoney(group.Records.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            remaining = RoundMoney(Math.Max(0, remaining - Math.Max(0, after - before)));
        }

        if (remaining > 0.009d)
        {
            IncreaseSignedRecordsWithinBounds(records, isIncome, remaining);
        }

        NormalizeReferenceAmountDistribution(records, random);
    }

    private static List<CapacityTierGenerationPlan> CreateCapacityTierCountPlans(
        IReadOnlyList<CapacityTierRuleGroup> groups,
        IReadOnlyList<DailyNaturalConsumptionSlot> dailySlots,
        IEnumerable<FlowRecord> records,
        DateTime monthStart,
        DateTime monthEnd,
        bool isIncome,
        double targetAmount,
        Random random)
    {
        var groupsByTier = groups.ToDictionary(item => item.Tier);
        var weights = CreateCapacityTierRecordWeights(groupsByTier.Keys, random);
        var existingCounts = Enum.GetValues<ReferenceCapacityTier>().ToDictionary(tier => tier, _ => 0);
        foreach (var record in records
                     .Where(item => IsRecordInRange(item, monthStart, monthEnd))
                     .Where(IsReferenceGeneratedRecord)
                     .Where(item => GetAmountSign(item.TradeMoney ?? 0) == (isIncome ? 1 : -1))
                     .Where(item => !IsSystemInterestRecord(item)))
        {
            existingCounts[GetGeneratedAmountCapacityTier(Math.Abs(record.TradeMoney ?? 0))]++;
        }

        var weightedAverage = groups.Sum(group =>
            weights.GetValueOrDefault(group.Tier) * EstimateRuleSequenceAverage(group.Rules));
        weightedAverage = Math.Max(0.01d, weightedAverage);
        var dailyTargetCount = groupsByTier.ContainsKey(ReferenceCapacityTier.Low)
            ? dailySlots.Count
            : 0;
        var desiredFromDaily = weights.GetValueOrDefault(ReferenceCapacityTier.Low) > 0.0001d
            ? (int)Math.Ceiling(dailyTargetCount / weights[ReferenceCapacityTier.Low])
            : 0;
        var totalToAdd = Math.Max(
            groups.Count,
            Math.Max(
                desiredFromDaily,
                (int)Math.Ceiling(targetAmount / weightedAverage)));
        totalToAdd = Math.Clamp(totalToAdd, 1, 100000);

        List<CapacityTierGenerationPlan> plans = [];
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var counts = CreateCapacityTierAddedCounts(
                groupsByTier.Keys,
                weights,
                existingCounts,
                totalToAdd);
            plans = CreateCapacityTierPlansForCounts(
                groupsByTier,
                counts,
                dailySlots,
                random);
            var minimum = plans.Sum(item => item.MinimumTotal);
            var maximum = plans.Sum(item => item.MaximumTotal);
            if (targetAmount >= minimum - 0.009d && targetAmount <= maximum + 0.009d)
            {
                break;
            }

            if (targetAmount > maximum + 0.009d)
            {
                var weightedMaximum = Math.Max(0.01d, plans.Sum(item =>
                    weights.GetValueOrDefault(item.Tier) * Math.Max(0.01d, item.MaximumTotal / Math.Max(1, item.Count))));
                totalToAdd = Math.Clamp(
                    totalToAdd + Math.Max(1, (int)Math.Ceiling((targetAmount - maximum) / weightedMaximum)),
                    1,
                    100000);
                continue;
            }

            if (totalToAdd <= 1)
            {
                break;
            }

            var reduction = Math.Max(1, (int)Math.Ceiling((minimum - targetAmount) / weightedAverage));
            totalToAdd = Math.Max(1, totalToAdd - reduction);
        }

        AllocateCapacityTierPlanBudgets(plans, targetAmount);
        return plans;
    }

    private static Dictionary<ReferenceCapacityTier, double> CreateCapacityTierRecordWeights(
        IEnumerable<ReferenceCapacityTier> availableTiers,
        Random random)
    {
        var available = availableTiers.ToHashSet();
        var result = Enum.GetValues<ReferenceCapacityTier>().ToDictionary(tier => tier, _ => 0d);
        if (available.Count == 0)
        {
            return result;
        }

        if (available.Count == 1)
        {
            result[available.First()] = 1d;
            return result;
        }

        if (available.Contains(ReferenceCapacityTier.Low))
        {
            result[ReferenceCapacityTier.Low] = MinimumLowRecordTarget
                + random.NextDouble() * (MaximumLowRecordTarget - MinimumLowRecordTarget);
        }

        if (available.Contains(ReferenceCapacityTier.Medium))
        {
            result[ReferenceCapacityTier.Medium] = MinimumMediumRecordTarget
                + random.NextDouble() * (MaximumMediumRecordTarget - MinimumMediumRecordTarget);
        }

        var assigned = result.Values.Sum();
        if (available.Contains(ReferenceCapacityTier.High))
        {
            result[ReferenceCapacityTier.High] = Math.Max(0, 1d - assigned);
        }

        var remainingTiers = available.Where(tier => result[tier] <= 0.0001d).ToList();
        if (remainingTiers.Count > 0)
        {
            var remaining = Math.Max(0, 1d - result.Values.Sum());
            foreach (var tier in remainingTiers)
            {
                result[tier] = remaining / remainingTiers.Count;
            }
        }

        var total = available.Sum(tier => result[tier]);
        foreach (var tier in available)
        {
            result[tier] /= Math.Max(0.0001d, total);
        }

        if (available.Contains(ReferenceCapacityTier.Low) && available.Any(tier => tier != ReferenceCapacityTier.Low))
        {
            result[ReferenceCapacityTier.Low] = Math.Clamp(
                result[ReferenceCapacityTier.Low],
                MinimumLowRecordRatio,
                MaximumPlannedLowRecordRatio);
            var nonLowTotal = available
                .Where(tier => tier != ReferenceCapacityTier.Low)
                .Sum(tier => result[tier]);
            foreach (var tier in available.Where(tier => tier != ReferenceCapacityTier.Low))
            {
                result[tier] = (1d - result[ReferenceCapacityTier.Low])
                    * (nonLowTotal <= 0.0001d ? 1d / (available.Count - 1) : result[tier] / nonLowTotal);
            }
        }

        return result;
    }

    private static Dictionary<ReferenceCapacityTier, int> CreateCapacityTierAddedCounts(
        IEnumerable<ReferenceCapacityTier> availableTiers,
        IReadOnlyDictionary<ReferenceCapacityTier, double> weights,
        IReadOnlyDictionary<ReferenceCapacityTier, int> existingCounts,
        int totalToAdd)
    {
        var available = availableTiers.ToList();
        var result = Enum.GetValues<ReferenceCapacityTier>().ToDictionary(tier => tier, _ => 0);
        var existingTotal = existingCounts.Values.Sum();
        for (var index = 0; index < totalToAdd; index++)
        {
            var finalTotal = existingTotal + index + 1d;
            var selected = available
                .OrderByDescending(tier =>
                    (finalTotal * weights.GetValueOrDefault(tier))
                    - existingCounts.GetValueOrDefault(tier)
                    - result[tier])
                .ThenBy(tier => tier)
                .First();
            result[selected]++;
        }

        if (available.Contains(ReferenceCapacityTier.Low) && available.Any(tier => tier != ReferenceCapacityTier.Low))
        {
            while ((existingCounts[ReferenceCapacityTier.Low] + result[ReferenceCapacityTier.Low])
                   / Math.Max(1d, existingTotal + totalToAdd) < MinimumLowRecordRatio - 0.0001d)
            {
                var donor = available
                    .Where(tier => tier != ReferenceCapacityTier.Low && result[tier] > 0)
                    .OrderByDescending(tier => result[tier])
                    .FirstOrDefault((ReferenceCapacityTier)(-1));
                if ((int)donor < 0)
                {
                    break;
                }

                result[donor]--;
                result[ReferenceCapacityTier.Low]++;
            }

            while ((existingCounts[ReferenceCapacityTier.Low] + result[ReferenceCapacityTier.Low])
                   / Math.Max(1d, existingTotal + totalToAdd) > MaximumPlannedLowRecordRatio + 0.0001d
                   && result[ReferenceCapacityTier.Low] > 0)
            {
                var receiver = available
                    .Where(tier => tier != ReferenceCapacityTier.Low)
                    .OrderByDescending(tier => weights.GetValueOrDefault(tier))
                    .First();
                result[ReferenceCapacityTier.Low]--;
                result[receiver]++;
            }
        }

        return result;
    }

    private static List<CapacityTierGenerationPlan> CreateCapacityTierPlansForCounts(
        IReadOnlyDictionary<ReferenceCapacityTier, CapacityTierRuleGroup> groupsByTier,
        IReadOnlyDictionary<ReferenceCapacityTier, int> counts,
        IReadOnlyList<DailyNaturalConsumptionSlot> dailySlots,
        Random random)
    {
        var result = new List<CapacityTierGenerationPlan>();
        foreach (var (tier, group) in groupsByTier)
        {
            var count = counts.GetValueOrDefault(tier);
            if (count <= 0)
            {
                continue;
            }

            var dailySlotLimit = tier == ReferenceCapacityTier.Low
                ? Math.Clamp(
                    (int)Math.Round(
                        count * (0.28d + (random.NextDouble() * 0.10d)),
                        MidpointRounding.AwayFromZero),
                    0,
                    count)
                : 0;
            var selectedDailySlots = dailySlotLimit > 0
                ? dailySlots.OrderBy(_ => random.Next()).Take(dailySlotLimit).ToList()
                : [];
            var regularCount = count - selectedDailySlots.Count;
            var regularCapacity = CalculateRuleSequenceCapacity(group.Rules, regularCount);
            var dailyMinimum = selectedDailySlots.Sum(item => item.Minimum);
            var dailyMaximum = selectedDailySlots.Sum(item => item.Maximum);
            var dailyExpected = selectedDailySlots.Sum(item => item.Expected);
            result.Add(new CapacityTierGenerationPlan(
                tier,
                group.Rules,
                count,
                selectedDailySlots,
                RoundMoney(dailyMinimum + regularCapacity.Minimum),
                RoundMoney(dailyExpected + regularCapacity.Expected),
                RoundMoney(dailyMaximum + regularCapacity.Maximum)));
        }

        return result;
    }

    private static (double Minimum, double Expected, double Maximum) CalculateRuleSequenceCapacity(
        IReadOnlyList<GenerateReferenceRule> rules,
        int count)
    {
        if (rules.Count == 0 || count <= 0)
        {
            return (0, 0, 0);
        }

        var minimum = 0d;
        var expected = 0d;
        var maximum = 0d;
        for (var index = 0; index < count; index++)
        {
            var bounds = GetRuleAmountBounds(rules[index % rules.Count]);
            minimum += bounds.Min;
            expected += EstimateNaturalSegmentAverage(bounds);
            maximum += bounds.Max;
        }

        return (RoundMoney(minimum), RoundMoney(expected), RoundMoney(maximum));
    }

    private static double EstimateRuleSequenceAverage(IReadOnlyList<GenerateReferenceRule> rules)
    {
        return rules.Count == 0
            ? 0d
            : rules.Average(rule => EstimateNaturalSegmentAverage(GetRuleAmountBounds(rule)));
    }

    private static double EstimateNaturalSegmentAverage(AmountBounds bounds)
    {
        var segments = CreateRuleAmountSegments(bounds);
        return segments.Count == 0
            ? bounds.Min
            : segments.Average(segment => segment.Min + ((segment.Max - segment.Min) * 0.5d));
    }

    private static void AllocateCapacityTierPlanBudgets(
        IReadOnlyList<CapacityTierGenerationPlan> plans,
        double targetAmount)
    {
        if (plans.Count == 0)
        {
            return;
        }

        foreach (var plan in plans)
        {
            plan.Budget = plan.MinimumTotal;
        }

        var boundedTarget = Math.Clamp(
            targetAmount,
            plans.Sum(item => item.MinimumTotal),
            plans.Sum(item => item.MaximumTotal));
        var remaining = RoundMoney(boundedTarget - plans.Sum(item => item.Budget));
        foreach (var plan in plans.OrderBy(item => item.Tier))
        {
            if (remaining <= 0.009d)
            {
                break;
            }

            var expectedRoom = RoundMoney(Math.Max(0, plan.ExpectedTotal - plan.Budget));
            var increase = RoundMoney(Math.Min(expectedRoom, remaining));
            plan.Budget = RoundMoney(plan.Budget + increase);
            remaining = RoundMoney(remaining - increase);
        }

        remaining = RoundMoney(boundedTarget - plans.Sum(item => item.Budget));
        DistributeCapacityTierBudget(plans, remaining);
    }

    private static void DistributeCapacityTierBudget(
        IReadOnlyList<CapacityTierGenerationPlan> plans,
        double amount)
    {
        var remaining = RoundMoney(Math.Max(0, amount));
        for (var pass = 0; pass < 4 && remaining > 0.009d; pass++)
        {
            var candidates = plans
                .Select(plan => new
                {
                    Plan = plan,
                    Limit = plan.MaximumTotal,
                    Weight = plan.Tier switch
                    {
                        ReferenceCapacityTier.High => 3d,
                        ReferenceCapacityTier.Medium => 2d,
                        _ => 1d
                    }
                })
                .Where(item => item.Limit > item.Plan.Budget + 0.009d)
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var weightedRoom = candidates.Sum(item => (item.Limit - item.Plan.Budget) * item.Weight);
            foreach (var candidate in candidates)
            {
                var room = RoundMoney(candidate.Limit - candidate.Plan.Budget);
                var share = weightedRoom <= 0.009d
                    ? remaining / candidates.Count
                    : remaining * room * candidate.Weight / weightedRoom;
                var increase = RoundMoney(Math.Min(room, Math.Max(0.01d, share)));
                increase = Math.Min(increase, remaining);
                candidate.Plan.Budget = RoundMoney(candidate.Plan.Budget + increase);
                remaining = RoundMoney(remaining - increase);
                if (remaining <= 0.009d)
                {
                    return;
                }
            }
        }
    }

    private static List<DailyNaturalConsumptionSlot> CreateDailyNaturalConsumptionSlots(
        FlowAutoGenerationRequest request,
        DateTime monthStart,
        DateTime monthEnd,
        IReadOnlyList<GenerateReferenceRule> rules,
        IEnumerable<FlowRecord> records,
        Random random)
    {
        var result = new List<DailyNaturalConsumptionSlot>();
        var normalizedEnd = NormalizeEndDate(monthEnd);
        for (var date = monthStart.Date; date <= normalizedEnd.Date; date = date.AddDays(1))
        {
            var dayStart = date == monthStart.Date ? monthStart : date;
            var dayEnd = date == normalizedEnd.Date ? normalizedEnd : date.AddDays(1).AddTicks(-1);
            if (dayStart > dayEnd)
            {
                continue;
            }

            var slices = rules
                .Where(rule => rule.TradeWeekend != false
                    || date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                .Select(rule => TryCreateDailyNaturalConsumptionSlice(rule, out var slice) ? slice : null)
                .Where(rule => rule is not null)
                .Cast<GenerateReferenceRule>()
                .OrderBy(_ => random.Next())
                .ToList();
            if (slices.Count == 0)
            {
                continue;
            }

            var existingCount = records.Count(item =>
                item.AccountTime?.Date == date
                && item.TradeMoney < -0.009d
                && Math.Abs(item.TradeMoney ?? 0) >= 10d - 0.009d
                && Math.Abs(item.TradeMoney ?? 0) < 30d - 0.009d
                && IsReferenceGeneratedRecord(item));
            if (existingCount > 0)
            {
                continue;
            }

            var activity = random.NextDouble();
            var desiredCount = activity < 0.15d ? 0 : activity < 0.80d ? 1 : 2;
            for (var index = 0; index < desiredCount; index++)
            {
                result.Add(new DailyNaturalConsumptionSlot(dayStart, dayEnd, slices));
            }
        }

        return result;
    }

    private static bool TryCreateDailyNaturalConsumptionSlice(
        GenerateReferenceRule rule,
        out GenerateReferenceRule slice)
    {
        slice = null!;
        var bounds = GetRuleAmountBounds(rule);
        var unit = Math.Max(0.01d, bounds.Unit);
        var minimum = CeilAmountToUnit(Math.Max(10d, bounds.Min), unit);
        var maximum = FloorAmountToUnit(Math.Min(bounds.Max, 30d - unit), unit);
        if (maximum < minimum - 0.009d)
        {
            return false;
        }

        slice = rule.Clone();
        slice.MinMoney = RoundMoney(minimum);
        slice.MaxMoney = RoundMoney(maximum);
        return true;
    }

    private static IReadOnlyList<FlowRecord> GenerateNativeDailyConsumptionRecords(
        FlowAutoGenerationRequest request,
        IReadOnlyList<DailyNaturalConsumptionSlot> slots,
        double targetAmount,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        if (slots.Count == 0 || targetAmount <= 0.009d)
        {
            return [];
        }

        var generated = new List<FlowRecord>(slots.Count);
        var remaining = RoundMoney(Math.Clamp(
            targetAmount,
            slots.Sum(item => item.Minimum),
            slots.Sum(item => item.Maximum)));
        var suffixMinimum = new double[slots.Count + 1];
        for (var index = slots.Count - 1; index >= 0; index--)
        {
            suffixMinimum[index] = RoundMoney(suffixMinimum[index + 1] + slots[index].Minimum);
        }

        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            var availableForRecord = RoundMoney(Math.Max(0, remaining - suffixMinimum[index + 1]));
            var rule = slot.Rules
                .Where(item => GetRuleAmountBounds(item).Min <= availableForRecord + 0.009d)
                .OrderBy(_ => random.Next())
                .FirstOrDefault();
            if (rule is null
                || !TryCreateNativeUniqueAmount(
                    rule,
                    availableForRecord,
                    random,
                    preferLower: false,
                    records,
                    isIncome: false,
                    amountUsage,
                    out var amount))
            {
                continue;
            }

            var accountTime = PickNativeDistributedTime(
                slot.Start,
                slot.End,
                rule,
                isIncome: false,
                random,
                scheduleState);
            var record = CreateRecordFromRule(
                request,
                rule,
                request.Bank.ReferenceColumns,
                accountTime,
                -amount);
            record.ExtraFields[RequiredOccurrenceField] = "false";
            records.Add(record);
            generated.Add(record);
            scheduleState.Register(record);
            amountUsage.Register(record);
            remaining = RoundMoney(Math.Max(0, remaining - amount));
        }

        var generatedTotal = RoundMoney(generated.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
        if (generatedTotal < targetAmount - 0.009d)
        {
            IncreaseSignedRecordsWithinBounds(generated, isIncome: false, targetAmount - generatedTotal);
        }

        return generated;
    }

    private static double GetCapacityTierMinimumAmount(IEnumerable<GenerateReferenceRule> rules)
    {
        return rules
            .Select(rule => GetRuleAmountBounds(rule).Min)
            .DefaultIfEmpty(double.MaxValue)
            .Min();
    }

    private static IReadOnlyList<CapacityTierRuleGroup> CreateCapacityTierRuleGroups(
        IReadOnlyList<GenerateReferenceRule> rules,
        Random random)
    {
        var slicesByAmountTier = Enum.GetValues<ReferenceCapacityTier>()
            .ToDictionary(tier => tier, _ => new List<(ReferenceCapacityTier SourceTier, GenerateReferenceRule Rule)>());

        // Maximum amount classifies the original reference rule. A spanning rule can
        // still produce amounts in more than one tier; the generated amount determines
        // which tier budget is consumed.
        foreach (var sourceGroup in rules
                     .GroupBy(GetReferenceCapacityTier)
                     .OrderBy(group => group.Key))
        {
            foreach (var rule in sourceGroup.OrderBy(_ => random.Next()))
            {
                foreach (var amountTier in Enum.GetValues<ReferenceCapacityTier>())
                {
                    if (TryCreateCapacityTierAmountSlice(rule, amountTier, out var slice))
                    {
                        slicesByAmountTier[amountTier].Add((sourceGroup.Key, slice));
                    }
                }
            }
        }

        return slicesByAmountTier
            .Where(item => item.Value.Count > 0)
            .Select(item => new CapacityTierRuleGroup(
                item.Key,
                GetReferenceCapacityTierWeight(item.Key),
                item.Value
                    .OrderBy(entry => entry.SourceTier == item.Key ? 0 : 1)
                    .ThenBy(entry => GetRuleAmountBounds(entry.Rule).Min)
                    .ThenBy(_ => random.Next())
                    .Select(entry => entry.Rule)
                    .ToList()))
            .OrderBy(group => group.Tier)
            .ToList();
    }

    private static bool TryCreateCapacityTierAmountSlice(
        GenerateReferenceRule rule,
        ReferenceCapacityTier amountTier,
        out GenerateReferenceRule slice)
    {
        slice = null!;
        var bounds = GetRuleAmountBounds(rule);
        var unit = Math.Max(0.01d, bounds.Unit);
        double minimum;
        double maximum;
        switch (amountTier)
        {
            case ReferenceCapacityTier.Low:
                minimum = bounds.Min;
                maximum = Math.Min(bounds.Max, FloorAmountToUnit(1000d, unit));
                break;
            case ReferenceCapacityTier.Medium:
                minimum = Math.Max(bounds.Min, CeilAmountToUnit(1000d + unit, unit));
                maximum = Math.Min(bounds.Max, FloorAmountToUnit(5000d - unit, unit));
                break;
            default:
                minimum = Math.Max(bounds.Min, CeilAmountToUnit(5000d, unit));
                maximum = bounds.Max;
                break;
        }

        minimum = RoundMoney(minimum);
        maximum = RoundMoney(maximum);
        if (maximum < minimum - 0.009d)
        {
            return false;
        }

        slice = rule.Clone();
        slice.MinMoney = minimum;
        slice.MaxMoney = maximum;
        return true;
    }

    private static CapacityTierGenerationResult GenerateNativeOptionalForMonthCore(
        FlowAutoGenerationRequest request,
        DateTime monthStart,
        DateTime monthEnd,
        IReadOnlyList<GenerateReferenceRule> fillRules,
        ReferenceCapacityTier budgetTier,
        bool isIncome,
        double targetAmount,
        double incomeCap,
        int maxRecordCount,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState,
        AmountUsageTracker amountUsage)
    {
        var remaining = RoundMoney(Math.Max(0, targetAmount));
        if (fillRules.Count == 0 || maxRecordCount <= 0 || remaining <= 0.009d)
        {
            return new CapacityTierGenerationResult(0, []);
        }

        var incomeTotal = isIncome ? SumIncome(records) : 0d;
        var preferLowerIncome = ShouldPreferLowerOptionalIncome(fillRules, isIncome);
        if (isIncome)
        {
            remaining = Math.Min(remaining, RoundMoney(Math.Max(0, incomeCap - incomeTotal)));
        }

        var plannedRules = Enumerable.Range(0, maxRecordCount)
            .Select(index => fillRules[index % fillRules.Count])
            .ToList();
        var suffixMinimum = new double[plannedRules.Count + 1];
        for (var index = plannedRules.Count - 1; index >= 0; index--)
        {
            suffixMinimum[index] = RoundMoney(
                suffixMinimum[index + 1] + GetRuleAmountBounds(plannedRules[index]).Min);
        }

        var generated = new List<FlowRecord>(maxRecordCount);
        for (var index = 0; index < plannedRules.Count; index++)
        {
            var rule = plannedRules[index];
            var bounds = GetRuleAmountBounds(rule);
            var availableForRecord = RoundMoney(Math.Max(0, remaining - suffixMinimum[index + 1]));
            if (availableForRecord < bounds.Min - 0.009d)
            {
                break;
            }

            var budget = Math.Min(availableForRecord, bounds.Max);
            if (!TryCreateNativeUniqueAmount(rule, budget, random, preferLower: preferLowerIncome, records, isIncome, amountUsage, out var amount))
            {
                break;
            }

            if (amount > remaining + 0.009d)
            {
                break;
            }

            if (GetGeneratedAmountCapacityTier(amount) != budgetTier)
            {
                break;
            }

            var accountTime = PickNativeDistributedTime(monthStart, monthEnd, rule, isIncome, random, scheduleState);
            var record = CreateRecordFromRule(
                request,
                rule,
                request.Bank.ReferenceColumns,
                accountTime,
                isIncome ? amount : -amount);
            record.ExtraFields[RequiredOccurrenceField] = "false";
            records.Add(record);
            generated.Add(record);
            scheduleState.Register(record);
            amountUsage.Register(record);
            remaining = RoundMoney(remaining - amount);
            if (isIncome)
            {
                incomeTotal = RoundMoney(incomeTotal + amount);
            }
        }

        return new CapacityTierGenerationResult(
            RoundMoney(Math.Max(0, targetAmount - remaining)),
            generated);
    }

    private static IReadOnlyList<GenerateReferenceRule> SelectNativeAmountFillRules(
        IReadOnlyList<GenerateReferenceRule> rules)
    {
        if (rules.Count <= 1)
        {
            return rules;
        }

        var largestMaximum = rules
            .Select(item => GetRuleAmountBounds(item).Max)
            .DefaultIfEmpty(0)
            .Max();
        if (largestMaximum < 1000d)
        {
            return rules;
        }

        var primaryRules = rules
            .Where(item =>
            {
                var bounds = GetRuleAmountBounds(item);
                return bounds.Max >= 1000d && bounds.Max >= largestMaximum * 0.20d;
            })
            .ToList();
        return primaryRules.Count > 0 ? primaryRules : rules;
    }

    private static ReferenceCapacityTier GetReferenceCapacityTier(FlowRuleBase rule)
    {
        var maximum = GetRuleAmountBounds(rule).Max;
        if (maximum >= 5000d)
        {
            return ReferenceCapacityTier.High;
        }

        return maximum > 1000d
            ? ReferenceCapacityTier.Medium
            : ReferenceCapacityTier.Low;
    }

    private static ReferenceCapacityTier GetGeneratedAmountCapacityTier(double amount)
    {
        amount = Math.Abs(RoundMoney(amount));
        if (amount >= 5000d - 0.009d)
        {
            return ReferenceCapacityTier.High;
        }

        return amount > 1000d + 0.009d
            ? ReferenceCapacityTier.Medium
            : ReferenceCapacityTier.Low;
    }

    private static double GetReferenceCapacityTierWeight(ReferenceCapacityTier tier)
    {
        return tier switch
        {
            ReferenceCapacityTier.Low => 0.15d,
            ReferenceCapacityTier.Medium => 0.30d,
            _ => 0.55d
        };
    }

    private static IReadOnlyDictionary<ReferenceCapacityTier, double> GetValidatedCapacityTierPoolWeights(Random random)
    {
        var weights = Enum.GetValues<ReferenceCapacityTier>()
            .ToDictionary(tier => tier, tier => Math.Max(0, GetReferenceCapacityTierWeight(tier)));
        var total = weights.Values.Sum();
        if (total <= 0.0001d)
        {
            weights[ReferenceCapacityTier.Low] = MinimumLowCapacityTierWeight;
            weights[ReferenceCapacityTier.Medium] = 0d;
            weights[ReferenceCapacityTier.High] = 1d - MinimumLowCapacityTierWeight;
            return weights;
        }

        foreach (var tier in weights.Keys.ToList())
        {
            weights[tier] /= total;
        }

        var low = MinimumLowCapacityTierWeight
            + random.NextDouble() * (MaximumLowCapacityTierPoolRatio - MinimumLowCapacityTierWeight);
        var availableForOthers = 1d - low;
        var otherTotal = weights[ReferenceCapacityTier.Medium] + weights[ReferenceCapacityTier.High];
        weights[ReferenceCapacityTier.Low] = low;
        weights[ReferenceCapacityTier.Medium] = otherTotal <= 0.0001d
            ? 0d
            : availableForOthers * weights[ReferenceCapacityTier.Medium] / otherTotal;
        weights[ReferenceCapacityTier.High] = availableForOthers - weights[ReferenceCapacityTier.Medium];

        var medium = weights[ReferenceCapacityTier.Medium];
        if (medium > MaximumMediumCapacityTierWeight)
        {
            var excess = medium - MaximumMediumCapacityTierWeight;
            weights[ReferenceCapacityTier.Medium] = MaximumMediumCapacityTierWeight;
            weights[ReferenceCapacityTier.High] += excess;
        }

        return weights;
    }

    private static bool TryCreateNativeAmount(
        FlowRuleBase rule,
        double budget,
        Random random,
        bool preferLower,
        out double amount)
    {
        amount = 0;
        var bounds = GetRuleAmountBounds(rule);
        var max = double.IsInfinity(budget) || budget >= double.MaxValue / 2
            ? bounds.Max
            : Math.Min(bounds.Max, FloorAmountToUnit(Math.Max(0, budget), bounds.Unit));
        if (max < bounds.Min - 0.009d)
        {
            return false;
        }

        var ratio = CreateAmountBucketRatio(random);
        var raw = bounds.Min + ((max - bounds.Min) * ratio);
        amount = ClampAmountToBounds(raw, new AmountBounds(bounds.Min, max, bounds.Unit));
        amount = MoveAmountAwayFromEdges(amount, new AmountBounds(bounds.Min, max, bounds.Unit), random);

        return amount >= bounds.Min - 0.009d && amount <= max + 0.009d;
    }

    private static bool TryCreateNativeUniqueAmount(
        FlowRuleBase rule,
        double budget,
        Random random,
        bool preferLower,
        IEnumerable<FlowRecord> existingRecords,
        bool isIncome,
        AmountUsageTracker? amountUsage,
        out double amount)
    {
        if (TryPickLeastUsedRuleAmount(rule, budget, existingRecords, isIncome, random, amountUsage, out amount))
        {
            return true;
        }

        if (!TryCreateNativeAmount(rule, budget, random, preferLower, out amount))
        {
            return false;
        }

        var bounds = GetRuleAmountBounds(rule);
        var max = double.IsInfinity(budget) || budget >= double.MaxValue / 2
            ? bounds.Max
            : Math.Min(bounds.Max, FloorAmountToUnit(Math.Max(0, budget), bounds.Unit));
        if (max < bounds.Min - 0.009d)
        {
            return false;
        }

        amount = MakeAmountDistinctWithinBounds(
            amount,
            new AmountBounds(bounds.Min, max, bounds.Unit),
            existingRecords,
            isIncome,
            random);
        return amount >= bounds.Min - 0.009d && amount <= max + 0.009d;
    }

    private static bool TryPickLeastUsedRuleAmount(
        FlowRuleBase rule,
        double budget,
        IEnumerable<FlowRecord> existingRecords,
        bool isIncome,
        Random random,
        AmountUsageTracker? amountUsage,
        out double amount)
    {
        amount = 0;
        var bounds = GetRuleAmountBounds(rule);
        var max = double.IsInfinity(budget) || budget >= double.MaxValue / 2
            ? bounds.Max
            : Math.Min(bounds.Max, FloorAmountToUnit(Math.Max(0, budget), bounds.Unit));
        if (max < bounds.Min - 0.009d)
        {
            return false;
        }

        var effectiveBounds = new AmountBounds(bounds.Min, max, bounds.Unit);
        var values = CreateRuleAmountPoolValues(effectiveBounds);
        if (values.Count == 0)
        {
            return false;
        }

        var sourceKey = GetRuleAmountDistributionKey(rule);
        var sign = isIncome ? 1 : -1;
        var usage = amountUsage?.GetUsage(sourceKey, sign, effectiveBounds)
            ?? existingRecords
                .Where(item => GetAmountSign(item.TradeMoney ?? 0) == sign)
                .Where(item => string.Equals(GetGeneratedAmountDistributionKey(item), sourceKey, StringComparison.Ordinal))
                .Select(item => ClampAmountToBounds(Math.Abs(item.TradeMoney ?? 0), effectiveBounds))
                .Where(item => item >= effectiveBounds.Min - 0.009d && item <= effectiveBounds.Max + 0.009d)
                .GroupBy(item => item)
                .ToDictionary(group => group.Key, group => group.Count());

        var naturalSegments = CreateRuleAmountSegments(effectiveBounds);
        if (naturalSegments.Count > 1 && values.Count > 1)
        {
            var valuesByNaturalSegment = values
                .GroupBy(value => GetAmountSegmentIndex(value, naturalSegments))
                .Where(group => group.Key >= 0)
                .ToDictionary(group => group.Key, group => group.ToList());
            if (valuesByNaturalSegment.Count > 1)
            {
                var selectedNaturalSegment = naturalSegments
                    .Where(segment => valuesByNaturalSegment.ContainsKey(segment.Index))
                    .OrderBy(segment => usage
                        .Where(item => item.Key >= segment.Min - 0.009d && item.Key <= segment.Max + 0.009d)
                        .Sum(item => item.Value))
                    .ThenBy(_ => random.Next())
                    .First();
                values = valuesByNaturalSegment[selectedNaturalSegment.Index];
            }
        }

        if (ShouldPreferFractionalAmount(effectiveBounds) && values.Count > 1)
        {
            var wholeValues = values.Where(IsWholeAmount).ToList();
            var fractionalValues = values.Where(value => !IsWholeAmount(value)).ToList();
            if (wholeValues.Count > 0 && fractionalValues.Count > 0)
            {
                var wholeUsage = usage
                    .Where(item => IsWholeAmount(item.Key))
                    .Sum(item => item.Value);
                var fractionalUsage = usage.Values.Sum() - wholeUsage;
                var totalUsage = wholeUsage + fractionalUsage;
                var wholeDeficit = ((totalUsage + 1d) * NaturalAmountTargetRatio) - wholeUsage;
                var fractionalDeficit = ((totalUsage + 1d) * (1d - NaturalAmountTargetRatio)) - fractionalUsage;
                values = wholeDeficit >= fractionalDeficit ? wholeValues : fractionalValues;
            }
        }

        amount = values
            .OrderBy(value => usage.GetValueOrDefault(value)
                + (IsReferenceEdgeValue(value, effectiveBounds.Min, effectiveBounds.Max) ? 1 : 0))
            .ThenBy(value => IsReferenceEdgeValue(value, effectiveBounds.Min, effectiveBounds.Max) ? 1 : 0)
            .ThenBy(_ => random.Next())
            .First();

        return amount >= effectiveBounds.Min - 0.009d && amount <= effectiveBounds.Max + 0.009d;
    }

    private static string GetRuleAmountDistributionKey(FlowRuleBase rule)
    {
        var sourceKind = rule is GenerateConstRule ? ConstSourceKind : ReferenceSourceKind;
        return CreateAmountDistributionKey(sourceKind, GetRuleAmountBounds(rule));
    }

    private static string CreateAmountDistributionKey(string sourceKind, AmountBounds bounds)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sourceKind}:{bounds.Min:0.########}:{bounds.Max:0.########}:{bounds.Unit:0.########}");
    }

    private static List<double> CreateRuleAmountPoolValues(AmountBounds bounds)
    {
        const int exactLimit = 2048;
        var values = EnumerateDiscreteAmounts(bounds, exactLimit);
        if (values.Count > 0)
        {
            return values;
        }

        var unit = Math.Max(0.01d, bounds.Unit);
        var totalSteps = Math.Floor((bounds.Max - bounds.Min + 0.0000001d) / unit) + 1d;
        if (totalSteps <= 0)
        {
            return [];
        }

        var sampleCount = (int)Math.Clamp(Math.Min(totalSteps, 512d), 1d, 512d);
        var result = new List<double>(sampleCount);
        if (sampleCount == 1)
        {
            result.Add(ClampAmountToBounds(bounds.Min, bounds));
        }
        else
        {
            for (var index = 0; index < sampleCount; index++)
            {
                var ratio = index / (sampleCount - 1d);
                result.Add(ClampAmountToBounds(bounds.Min + ((bounds.Max - bounds.Min) * ratio), bounds));
            }
        }

        if (unit < 1d - 0.0000001d)
        {
            var firstWhole = (long)Math.Ceiling(bounds.Min - 0.0000001d);
            var lastWhole = (long)Math.Floor(bounds.Max + 0.0000001d);
            var wholeCount = Math.Max(0L, lastWhole - firstWhole + 1L);
            if (wholeCount > 0)
            {
                var wholeSampleCount = (int)Math.Min(512L, wholeCount);
                for (var index = 0; index < wholeSampleCount; index++)
                {
                    var ratio = wholeSampleCount == 1 ? 0d : index / (wholeSampleCount - 1d);
                    var wholeOffset = (long)Math.Round(
                        ratio * (wholeCount - 1d),
                        MidpointRounding.AwayFromZero);
                    result.Add(ClampAmountToBounds(firstWhole + wholeOffset, bounds));
                }
            }
        }

        return result
            .Distinct()
            .OrderBy(item => item)
            .ToList();
    }

    private static List<AmountSegment> CreateRuleAmountSegments(AmountBounds bounds)
    {
        var unit = Math.Max(0.01d, bounds.Unit);
        if (bounds.Max <= bounds.Min + unit - 0.0000001d)
        {
            return [new AmountSegment(bounds.Min, bounds.Max, 0)];
        }

        var result = new List<AmountSegment>();
        var current = bounds.Min;
        var index = 0;
        while (current <= bounds.Max + 0.009d && result.Count < 512)
        {
            var segmentSize = current < 10d
                ? 10d
                : Math.Pow(10d, Math.Floor(Math.Log10(current)));
            segmentSize = Math.Max(unit, segmentSize);
            var nextBoundary = current < 10d
                ? 10d
                : (Math.Floor(current / segmentSize) + 1d) * segmentSize;
            if (nextBoundary <= current + 0.009d)
            {
                nextBoundary += segmentSize;
            }

            var segmentMax = FloorAmountToUnit(Math.Min(bounds.Max, nextBoundary - unit), unit);
            if (segmentMax < current - 0.009d)
            {
                segmentMax = current;
            }

            result.Add(new AmountSegment(RoundMoney(current), RoundMoney(segmentMax), index++));
            current = CeilAmountToUnit(segmentMax + unit, unit);
        }

        if (result.Count > 1)
        {
            var last = result[^1];
            if (last.Max - last.Min < unit - 0.0000001d)
            {
                var previous = result[^2];
                result[^2] = new AmountSegment(previous.Min, last.Max, previous.Index);
                result.RemoveAt(result.Count - 1);
            }
        }

        return result.Count == 0 ? [new AmountSegment(bounds.Min, bounds.Max, 0)] : result;
    }

    private static List<AmountSegment> CreateRuleAmountThirdSegments(AmountBounds bounds)
    {
        var unit = Math.Max(0.01d, bounds.Unit);
        var totalSteps = Math.Floor((bounds.Max - bounds.Min + 0.0000001d) / unit) + 1d;
        var segmentCount = (int)Math.Clamp(Math.Min(3d, totalSteps), 1d, 3d);
        if (segmentCount <= 1)
        {
            return [new AmountSegment(bounds.Min, bounds.Max, 0)];
        }

        var result = new List<AmountSegment>(segmentCount);
        for (var index = 0; index < segmentCount; index++)
        {
            var startStep = Math.Floor(index * totalSteps / segmentCount);
            var endStep = Math.Floor((index + 1d) * totalSteps / segmentCount) - 1d;
            if (index == segmentCount - 1)
            {
                endStep = totalSteps - 1d;
            }

            var minimum = ClampAmountToBounds(bounds.Min + (startStep * unit), bounds);
            var maximum = ClampAmountToBounds(bounds.Min + (Math.Max(startStep, endStep) * unit), bounds);
            result.Add(new AmountSegment(RoundMoney(minimum), RoundMoney(maximum), index));
        }

        return result;
    }

    private static List<AmountSegment> CreateRuleAmountDistributionSegments(AmountBounds bounds)
    {
        return CreateRuleAmountSegments(bounds);
    }

    private static int GetAmountSegmentIndex(double amount, IReadOnlyList<AmountSegment> segments)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (amount >= segment.Min - 0.009d && amount <= segment.Max + 0.009d)
            {
                return segment.Index;
            }
        }

        return -1;
    }

    private static double CreateAmountBucketRatio(Random random)
    {
        return 0.05d + (random.NextDouble() * 0.90d);
    }

    private static bool IsWideAmountRange(AmountBounds bounds)
    {
        return bounds.Min >= 1000d && bounds.Max >= bounds.Min * 4d;
    }

    private static bool IsSmallDiscreteAmountRule(Bank bank, FlowRuleBase rule, AmountBounds bounds)
    {
        return IsWechatBank(bank)
            && IsSmallDiscreteAmountBounds(bounds);
    }

    private static bool IsSmallDiscreteAmountRecord(FlowRecord record)
    {
        return record.ExtraFields.TryGetValue(SmallDiscreteAmountField, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed
            && IsSmallDiscreteAmountBounds(GetRecordAmountBounds(record));
    }

    private static bool IsLowCapacityReferenceRecord(FlowRecord record)
    {
        if (!IsGeneratedReferenceRecord(record))
        {
            return false;
        }

        var bounds = GetRecordAmountBounds(record);
        return bounds.Max <= 1000d + 0.009d;
    }

    private static bool IsGeneratedLowAmountReferenceRecord(FlowRecord record)
    {
        return IsGeneratedReferenceRecord(record)
            && Math.Abs(record.TradeMoney ?? 0) > 0.009d
            && GetGeneratedAmountCapacityTier(Math.Abs(record.TradeMoney ?? 0)) == ReferenceCapacityTier.Low;
    }

    private static bool IsSmallDiscreteAmountBounds(AmountBounds bounds)
    {
        if (bounds.Unit < 1d - 0.0000001d || bounds.Max > 200d)
        {
            return false;
        }

        var stepCount = CountDiscreteAmountSteps(bounds, SmallDiscreteAmountStepLimit);
        return stepCount is >= 3 and <= SmallDiscreteAmountStepLimit;
    }

    private static int CountDiscreteAmountSteps(AmountBounds bounds, int limit)
    {
        var unit = Math.Max(0.01d, bounds.Unit);
        if (bounds.Max < bounds.Min - 0.009d)
        {
            return 0;
        }

        var count = (int)Math.Floor((bounds.Max - bounds.Min + 0.0000001d) / unit) + 1;
        return count > limit ? limit + 1 : count;
    }

    private static string GetSmallDiscreteAmountTradeTypeKey(FlowRecord record)
    {
        var tradeType = FirstNonEmpty(
            record.ProductName,
            record["交易类型"],
            record["交易名称"],
            record["交易种类"],
            record["业务类型"],
            record.ProductBrief,
            record.Remark,
            record.Usage);
        if (!string.IsNullOrWhiteSpace(tradeType))
        {
            return NormalizeName(tradeType);
        }

        var sourceKind = record.ExtraFields.GetValueOrDefault(SourceKindField) ?? string.Empty;
        var sourceIndex = record.ExtraFields.GetValueOrDefault(SourceIndexField) ?? string.Empty;
        return $"{sourceKind}:{sourceIndex}";
    }

    private static string GetGeneratedAmountDistributionKey(FlowRecord record)
    {
        var sourceKind = record.ExtraFields.GetValueOrDefault(SourceKindField) ?? string.Empty;
        var sourceIndex = record.ExtraFields.GetValueOrDefault(SourceIndexField) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sourceKind)
            && record.ExtraFields.ContainsKey(AmountMinField)
            && record.ExtraFields.ContainsKey(AmountMaxField)
            && record.ExtraFields.ContainsKey(AmountUnitField))
        {
            return CreateAmountDistributionKey(sourceKind, GetRecordAmountBounds(record));
        }

        if (!string.IsNullOrWhiteSpace(sourceKind) || !string.IsNullOrWhiteSpace(sourceIndex))
        {
            return $"{sourceKind}:{sourceIndex}";
        }

        var bounds = GetRecordAmountBounds(record);
        return $"{GetSmallDiscreteAmountTradeTypeKey(record)}:{bounds.Min:0.##}:{bounds.Max:0.##}:{bounds.Unit:0.##}";
    }

    private static bool NormalizeSmallDiscreteAmountDistribution(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        Random random)
    {
        if (!IsWechatBank(request.Bank))
        {
            return false;
        }

        var changed = false;
        var groups = records
            .Where(item => IsSmallDiscreteAmountRecord(item))
            .Where(item => !IsRequiredGeneratedRecord(item))
            .Where(item => Math.Abs(item.TradeMoney ?? 0) > 0.009d)
            .Where(item => !IsSystemInterestRecord(item))
            .GroupBy(item =>
            {
                var bounds = GetRecordAmountBounds(item);
                return (
                    SourceKey: GetGeneratedAmountDistributionKey(item),
                    TradeType: GetSmallDiscreteAmountTradeTypeKey(item),
                    Sign: GetAmountSign(item.TradeMoney ?? 0),
                    bounds.Min,
                    bounds.Max,
                    bounds.Unit);
            })
            .Where(group => group.Key.Sign != 0)
            .ToList();

        foreach (var group in groups)
        {
            var groupRecords = group.ToList();
            changed = EnsureSmallDiscreteAmountGroupCoverage(records, request, groupRecords, group.Key.Sign, random) || changed;
            changed = NormalizeSmallDiscreteAmountGroup(groupRecords, group.Key.Sign, random) || changed;
        }

        return changed;
    }

    private static bool RebalanceSmallAmountTotalsToLargeRules(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        if (records.Count == 0)
        {
            return false;
        }

        var changed = false;
        var traceEnabled = FlowGenerationPerfTrace.IsEnabled;
        var traceStopwatch = traceEnabled ? Stopwatch.StartNew() : null;
        var targetMs = 0d;
        var transferMs = 0d;
        var balanceMs = 0d;
        var processedGroups = 0;
        var attemptedGroups = 0;
        var highVolumePostProcessing = UseHighVolumeNativePostProcessing(request.Bank, records.Count);
        var maxAttemptedGroups = highVolumePostProcessing ? IsWechatBank(request.Bank) ? 4 : 8 : int.MaxValue;
        var groups = records
            .Where(item => IsSmallDiscreteAmountRecord(item))
            .Where(item => Math.Abs(item.TradeMoney ?? 0) > 0.009d)
            .Where(item => !IsSystemInterestRecord(item))
            .GroupBy(item =>
            {
                var bounds = GetRecordAmountBounds(item);
                return (
                    SourceKey: GetGeneratedAmountDistributionKey(item),
                    Sign: GetAmountSign(item.TradeMoney ?? 0),
                    bounds.Min,
                    bounds.Max,
                    bounds.Unit);
            })
            .Where(group => group.Key.Sign != 0)
            .OrderByDescending(group => group.Sum(item => Math.Abs(item.TradeMoney ?? 0)))
            .ToList();

        foreach (var group in groups)
        {
            var groupRecords = group
                .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
                .ThenBy(item => item.Index)
                .ToList();
            if (groupRecords.Count < 4)
            {
                continue;
            }

            var bounds = GetRecordAmountBounds(groupRecords[0]);
            var values = EnumerateDiscreteAmounts(bounds, SmallDiscreteAmountStepLimit);
            if (values.Count < 2)
            {
                continue;
            }

            var currentTotal = RoundMoney(groupRecords.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            var naturalTotal = CalculateNaturalSmallAmountTotal(values, bounds, groupRecords.Count);
            if (currentTotal <= naturalTotal * 1.10d + Math.Max(bounds.Unit, 1d))
            {
                continue;
            }

            if (attemptedGroups >= maxAttemptedGroups && !changed)
            {
                break;
            }

            attemptedGroups++;
            var stepStart = traceStopwatch?.Elapsed ?? TimeSpan.Zero;
            var originalRecordCount = records.Count;
            var groupBackup = CaptureRecordAmountSnapshot(groupRecords);
            var transferBackup = CaptureRecordAmountSnapshot(
                GetLargeSignedTransferCandidates(records, group.Key.Sign > 0));
            var targetAmounts = CreateBalancedSmallDiscreteAmounts(values, bounds, groupRecords.Count, naturalTotal, random);
            if (traceStopwatch is not null)
            {
                targetMs += (traceStopwatch.Elapsed - stepStart).TotalMilliseconds;
            }

            if (targetAmounts.Length != groupRecords.Count)
            {
                continue;
            }

            targetAmounts = OrderSmallDiscreteAmountsForTimeline(targetAmounts, group.Key.Sign, random);
            for (var index = 0; index < groupRecords.Count; index++)
            {
                ApplySignedAmount(groupRecords[index], group.Key.Sign, targetAmounts[index]);
            }

            var nextTotal = RoundMoney(groupRecords.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            var transferAmount = RoundMoney(currentTotal - nextTotal);
            if (transferAmount <= 0.009d)
            {
                RestoreRebalanceSnapshots(records, groupBackup, transferBackup, originalRecordCount);
                ApplyBalances(records, openingBalance);
                continue;
            }

            stepStart = traceStopwatch?.Elapsed ?? TimeSpan.Zero;
            var transferredAmount = TransferSmallAmountTotalToLargeRules(
                records,
                request,
                start,
                end,
                openingBalance,
                random,
                group.Key.Sign > 0,
                transferAmount);
            if (traceStopwatch is not null)
            {
                transferMs += (traceStopwatch.Elapsed - stepStart).TotalMilliseconds;
            }

            if (transferredAmount <= 0.009d)
            {
                RestoreRebalanceSnapshots(records, groupBackup, transferBackup, originalRecordCount);
                ApplyBalances(records, openingBalance);
                continue;
            }

            var shortfall = RoundMoney(transferAmount - transferredAmount);
            if (shortfall > 0.009d)
            {
                IncreaseSmallDiscreteRecordsWithinBounds(groupRecords, group.Key.Sign, shortfall, random);
            }

            stepStart = traceStopwatch?.Elapsed ?? TimeSpan.Zero;
            ApplyBalances(records, openingBalance);
            if (GetMinimumBalance(records, openingBalance) < -0.009d)
            {
                RestoreRebalanceSnapshots(records, groupBackup, transferBackup, originalRecordCount);
                ApplyBalances(records, openingBalance);
                continue;
            }
            if (traceStopwatch is not null)
            {
                balanceMs += (traceStopwatch.Elapsed - stepStart).TotalMilliseconds;
            }

            processedGroups++;
            changed = true;
        }

        if (traceEnabled && traceStopwatch is not null)
        {
            Console.Error.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[FlowPerf] rebalance small detail: attempts={attemptedGroups}, groups={processedGroups}, target={targetMs:0} ms, transfer={transferMs:0} ms, balance={balanceMs:0} ms, total={traceStopwatch.Elapsed.TotalMilliseconds:0} ms"));
        }

        return changed;
    }

    private static bool ReduceSmallDiscreteEdgePileupsToLargeRules(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        if (!IsWechatBank(request.Bank) || records.Count == 0)
        {
            return false;
        }

        var changed = false;
        var groups = records
            .Where(IsSmallDiscreteAmountRecord)
            .Where(item => Math.Abs(item.TradeMoney ?? 0) > 0.009d)
            .Where(item => !IsSystemInterestRecord(item))
            .GroupBy(item =>
            {
                var bounds = GetRecordAmountBounds(item);
                return (
                    TradeType: GetSmallDiscreteAmountTradeTypeKey(item),
                    Sign: GetAmountSign(item.TradeMoney ?? 0),
                    bounds.Min,
                    bounds.Max,
                    bounds.Unit);
            })
            .Where(group => group.Key.Sign != 0)
            .ToList();

        foreach (var group in groups)
        {
            var groupRecords = group.ToList();
            var bounds = GetRecordAmountBounds(groupRecords[0]);
            var values = EnumerateDiscreteAmounts(bounds, SmallDiscreteAmountStepLimit)
                .OrderBy(item => item)
                .ToArray();
            if (values.Length < 4 || groupRecords.Count < values.Length)
            {
                continue;
            }

            var maxValue = values[^1];
            var maxRecords = groupRecords
                .Where(item => Math.Abs(Math.Abs(item.TradeMoney ?? 0) - maxValue) <= 0.009d)
                .OrderBy(_ => random.Next())
                .ToList();
            var fairMaxUsage = Math.Max(
                GetSmallDiscreteEdgeUsageLimit(groupRecords.Count),
                (int)Math.Ceiling(groupRecords.Count / (double)values.Length * 1.8d));
            var excess = maxRecords.Count - fairMaxUsage;
            if (excess <= 0)
            {
                continue;
            }

            var usage = groupRecords
                .Select(item => RoundAmountToUnit(Math.Abs(item.TradeMoney ?? 0), Math.Max(1d, bounds.Unit)))
                .GroupBy(item => item)
                .ToDictionary(groupItem => groupItem.Key, groupItem => groupItem.Count());
            var maxAdjustments = Math.Min(excess, IsWechatBank(request.Bank) ? 160 : 320);
            var averageValue = values.Average();
            var pendingAdjustments = new List<(FlowRecord Record, double OriginalAmount, double TargetAmount, double Reduction)>();
            var totalReduction = 0d;
            for (var index = 0; index < maxAdjustments; index++)
            {
                var record = maxRecords[index];
                var current = RoundAmountToUnit(Math.Abs(record.TradeMoney ?? 0), Math.Max(1d, bounds.Unit));
                if (current < maxValue - 0.009d)
                {
                    continue;
                }

                var targetValue = values
                    .Where(value => value < maxValue - 0.009d)
                    .OrderBy(value => usage.GetValueOrDefault(value))
                    .ThenBy(value => Math.Abs(value - averageValue))
                    .ThenBy(_ => random.Next())
                    .FirstOrDefault(double.NaN);
                if (double.IsNaN(targetValue))
                {
                    break;
                }

                var reduction = RoundMoney(current - targetValue);
                if (reduction <= 0.009d)
                {
                    continue;
                }

                pendingAdjustments.Add((record, current, targetValue, reduction));
                totalReduction = RoundMoney(totalReduction + reduction);
                usage[maxValue] = Math.Max(0, usage.GetValueOrDefault(maxValue) - 1);
                usage[targetValue] = usage.GetValueOrDefault(targetValue) + 1;
            }

            if (pendingAdjustments.Count == 0 || totalReduction <= 0.009d)
            {
                continue;
            }

            foreach (var adjustment in pendingAdjustments)
            {
                ApplySignedAmount(adjustment.Record, group.Key.Sign, adjustment.TargetAmount);
            }

            var transferred = TransferSmallAmountTotalToLargeRules(
                records,
                request,
                start,
                end,
                openingBalance,
                random,
                group.Key.Sign > 0,
                totalReduction);
            if (transferred <= 0.009d)
            {
                foreach (var adjustment in pendingAdjustments)
                {
                    ApplySignedAmount(adjustment.Record, group.Key.Sign, adjustment.OriginalAmount);
                }

                continue;
            }

            var shortfall = RoundMoney(totalReduction - transferred);
            if (shortfall > 0.009d)
            {
                IncreaseSmallDiscreteRecordsWithinBounds(
                    pendingAdjustments.Select(item => item.Record).ToList(),
                    group.Key.Sign,
                    shortfall,
                    random);
            }

            if (pendingAdjustments.Any(adjustment =>
                    Math.Abs(Math.Abs(adjustment.Record.TradeMoney ?? 0) - adjustment.OriginalAmount) > 0.009d))
            {
                changed = true;
            }
        }

        if (changed)
        {
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static double CalculateNaturalSmallAmountTotal(
        IReadOnlyList<double> values,
        AmountBounds bounds,
        int count)
    {
        if (values.Count == 0 || count <= 0)
        {
            return 0;
        }

        var average = values.Average();
        var range = Math.Max(bounds.Unit, bounds.Max - bounds.Min);
        var lower = bounds.Min + (range * SmallDiscreteAverageMinRatio);
        var upper = bounds.Min + (range * SmallDiscreteAverageMaxRatio);
        var targetAverage = Math.Clamp(average, lower, upper);
        return RoundAmountToUnit(targetAverage * count, Math.Max(1d, bounds.Unit));
    }

    private static double TransferSmallAmountTotalToLargeRules(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        bool isIncome,
        double amount)
    {
        var requested = RoundMoney(Math.Max(0, amount));
        var remaining = LimitAdjustmentAmountByTotals(records, request, isIncome, requested);
        if (remaining <= 0.009d)
        {
            return 0;
        }

        var transferred = IncreaseLargeSignedRecordsWithinBoundsAmount(records, isIncome, remaining);
        remaining = RoundMoney(remaining - transferred);
        remaining = LimitAdjustmentAmountByTotals(records, request, isIncome, remaining);
        if (remaining <= 0.009d)
        {
            return RoundMoney(Math.Min(requested, transferred));
        }

        transferred = RoundMoney(transferred
            + AddLargeReferenceAdjustmentRecords(records, request, start, end, openingBalance, random, isIncome, remaining));
        return RoundMoney(Math.Min(requested, transferred));
    }

    private static double IncreaseSmallDiscreteRecordsWithinBounds(
        IReadOnlyList<FlowRecord> records,
        int sign,
        double amount,
        Random random)
    {
        var remaining = RoundMoney(Math.Max(0, amount));
        if (remaining <= 0.009d || records.Count == 0)
        {
            return 0;
        }

        var bounds = GetRecordAmountBounds(records[0]);
        var values = EnumerateDiscreteAmounts(bounds, SmallDiscreteAmountStepLimit)
            .OrderBy(item => item)
            .ToArray();
        if (values.Length < 2)
        {
            return 0;
        }

        var unit = Math.Max(1d, bounds.Unit);
        var valueSet = values.ToHashSet();
        var currentValues = new double[records.Count];
        var usage = new Dictionary<double, int>();
        for (var index = 0; index < records.Count; index++)
        {
            var current = RoundAmountToUnit(Math.Abs(records[index].TradeMoney ?? 0), unit);
            currentValues[index] = current;
            usage[current] = usage.GetValueOrDefault(current) + 1;
        }

        var increased = 0d;
        var attempts = Math.Min(60000, Math.Max(200, records.Count * values.Length));
        for (var attempt = 0; attempt < attempts && remaining >= unit - 0.009d; attempt++)
        {
            var candidateIndex = -1;
            var candidateNext = 0d;
            var bestUsage = int.MaxValue;
            var bestEdgePenalty = int.MaxValue;
            var bestTieBreaker = int.MaxValue;
            for (var index = 0; index < currentValues.Length; index++)
            {
                var next = RoundAmountToUnit(currentValues[index] + unit, unit);
                if (!valueSet.Contains(next))
                {
                    continue;
                }

                var nextUsage = usage.GetValueOrDefault(next);
                var edgePenalty = IsSmallDiscreteEdgeValue(next, values[0], values[^1]) ? 1 : 0;
                var tieBreaker = random.Next();
                if (nextUsage > bestUsage
                    || nextUsage == bestUsage && edgePenalty > bestEdgePenalty
                    || nextUsage == bestUsage && edgePenalty == bestEdgePenalty && tieBreaker >= bestTieBreaker)
                {
                    continue;
                }

                candidateIndex = index;
                candidateNext = next;
                bestUsage = nextUsage;
                bestEdgePenalty = edgePenalty;
                bestTieBreaker = tieBreaker;
            }

            if (candidateIndex < 0)
            {
                break;
            }

            var previous = currentValues[candidateIndex];
            usage[previous] = Math.Max(0, usage.GetValueOrDefault(previous) - 1);
            usage[candidateNext] = usage.GetValueOrDefault(candidateNext) + 1;
            currentValues[candidateIndex] = candidateNext;
            ApplySignedAmount(records[candidateIndex], sign, candidateNext);
            remaining = RoundMoney(remaining - unit);
            increased = RoundMoney(increased + unit);
        }

        return increased;
    }

    private static bool IncreaseLargeSignedRecordsWithinBounds(IEnumerable<FlowRecord> records, bool isIncome, double amount)
    {
        return IncreaseLargeSignedRecordsWithinBoundsAmount(records, isIncome, amount) >= amount - 0.009d;
    }

    private static double IncreaseLargeSignedRecordsWithinBoundsAmount(IEnumerable<FlowRecord> records, bool isIncome, double amount)
    {
        var remaining = RoundMoney(amount);
        var sign = isIncome ? 1d : -1d;
        var increased = 0d;
        var candidates = GetLargeSignedTransferCandidates(records, isIncome)
            .OrderByDescending(item => GetRecordAmountBounds(item).Max)
            .ThenBy(item => GetRecordAmountUnit(item))
            .ThenByDescending(item => item.AccountTime ?? DateTime.MinValue)
            .ToList();

        foreach (var record in candidates)
        {
            if (remaining <= 0.009d)
            {
                break;
            }

            var bounds = GetRecordAmountBounds(record);
            var absolute = Math.Abs(record.TradeMoney ?? 0);
            var room = RoundMoney(bounds.Max - absolute);
            if (room <= 0.009d)
            {
                continue;
            }

            var increase = Math.Min(room, remaining);
            increase = RoundAmountToUnit(increase, Math.Max(0.01d, bounds.Unit));
            if (increase <= 0.009d || increase > remaining + 0.009d)
            {
                increase = FloorAmountToUnit(remaining, Math.Max(0.01d, bounds.Unit));
            }

            if (increase <= 0.009d)
            {
                continue;
            }

            ApplySignedAmount(record, sign, ClampAmountToBounds(absolute + increase, bounds));
            remaining = RoundMoney(remaining - increase);
            increased = RoundMoney(increased + increase);
        }

        return increased;
    }

    private static List<FlowRecord> GetLargeSignedTransferCandidates(IEnumerable<FlowRecord> records, bool isIncome)
    {
        return GetSignedRecords(records, isIncome)
            .Where(IsGeneratedReferenceRecord)
            .Where(item => !IsRequiredGeneratedRecord(item))
            .Where(item => !IsSmallDiscreteAmountRecord(item))
            .Where(item => IsLargeAmountTransferBounds(GetRecordAmountBounds(item)))
            .ToList();
    }

    private static double AddLargeReferenceAdjustmentRecords(
        ICollection<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        bool isIncome,
        double targetAmount)
    {
        var remaining = RoundMoney(Math.Max(0, targetAmount));
        remaining = LimitAdjustmentAmountByTotals(records, request, isIncome, remaining);
        if (remaining <= 0.009d)
        {
            return 0;
        }

        var rules = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .Where(item =>
            {
                var bounds = GetRuleAmountBounds(item);
                return !IsSmallDiscreteAmountRule(request.Bank, item, bounds)
                    && IsLargeAmountTransferBounds(bounds);
            })
            .OrderByDescending(item => GetRuleAmountBounds(item).Max)
            .ThenBy(_ => random.Next())
            .ToList();
        if (rules.Count == 0)
        {
            return 0;
        }

        var scheduleState = NativeScheduleState.From(records);
        var amountUsage = AmountUsageTracker.From(records);
        var added = 0d;
        var largestRuleAmount = rules.Select(item => GetRuleAmountBounds(item).Max).DefaultIfEmpty(1d).Max();
        var maxAttemptLimit = Math.Max(rules.Count, IsWechatBank(request.Bank) ? 80 : 320);
        var maxAttempts = Math.Clamp(
            (int)Math.Ceiling(remaining / Math.Max(1d, largestRuleAmount)) + rules.Count * 3,
            rules.Count,
            maxAttemptLimit);
        for (var index = 0; index < maxAttempts && remaining > 0.009d; index++)
        {
            remaining = LimitAdjustmentAmountByTotals(records, request, isIncome, remaining);
            if (remaining <= 0.009d)
            {
                break;
            }

            var rule = rules[index % rules.Count];
            var bounds = GetRuleAmountBounds(rule);
            if (remaining < bounds.Min - 0.009d)
            {
                continue;
            }

            if (!TryPickLeastUsedRuleAmount(rule, Math.Min(remaining, bounds.Max), records, isIncome, random, amountUsage, out var amount))
            {
                continue;
            }

            if (amount <= 0.009d || amount > remaining + 0.009d)
            {
                continue;
            }

            var accountTime = PickAdjustmentTime(records, openingBalance, start, end, rule, random, index, isIncome ? amount : -amount);
            var record = CreateRecordFromRule(request, rule, request.Bank.ReferenceColumns, accountTime, isIncome ? amount : -amount);
            record.ExtraFields[RequiredOccurrenceField] = "false";
            records.Add(record);
            scheduleState.Register(record);
            amountUsage.Register(record);
            remaining = RoundMoney(remaining - amount);
            added = RoundMoney(added + amount);
        }

        return added;
    }

    private static bool IsLargeAmountTransferBounds(AmountBounds bounds)
    {
        return !IsSmallDiscreteAmountBounds(bounds)
            && (bounds.Max >= 500d || bounds.Max >= bounds.Min * 4d || bounds.Unit >= 10d);
    }

    private readonly record struct FlowRecordAmountSnapshot(
        FlowRecord Record,
        int Index,
        double? TradeMoney,
        double? Balance,
        double? BalanceAmount,
        double? CreditAmount,
        double? DebitAmount,
        string? IncomeAttribute,
        string? IncomeFlag);

    private static List<FlowRecordAmountSnapshot> CaptureRecordAmountSnapshot(IReadOnlyList<FlowRecord> records)
    {
        var snapshot = new List<FlowRecordAmountSnapshot>(records.Count);
        foreach (var record in records)
        {
            snapshot.Add(new FlowRecordAmountSnapshot(
                record,
                record.Index,
                record.TradeMoney,
                record.Balance,
                record.BalanceAmount,
                record.CreditAmount,
                record.DebitAmount,
                record.IncomeAttribute,
                record.IncomeFlag));
        }

        return snapshot;
    }

    private static void RestoreRecordSnapshot(List<FlowRecord> records, IReadOnlyList<FlowRecordAmountSnapshot> snapshot)
    {
        records.Clear();
        foreach (var item in snapshot)
        {
            item.Record.Index = item.Index;
            item.Record.TradeMoney = item.TradeMoney;
            item.Record.Balance = item.Balance;
            item.Record.BalanceAmount = item.BalanceAmount;
            item.Record.CreditAmount = item.CreditAmount;
            item.Record.DebitAmount = item.DebitAmount;
            item.Record.IncomeAttribute = item.IncomeAttribute ?? string.Empty;
            item.Record.IncomeFlag = item.IncomeFlag ?? string.Empty;
            records.Add(item.Record);
        }
    }

    private static void RestoreRebalanceSnapshots(
        List<FlowRecord> records,
        IReadOnlyList<FlowRecordAmountSnapshot> groupSnapshot,
        IReadOnlyList<FlowRecordAmountSnapshot> transferSnapshot,
        int originalRecordCount)
    {
        if (records.Count > originalRecordCount)
        {
            records.RemoveRange(originalRecordCount, records.Count - originalRecordCount);
        }

        RestoreRecordAmounts(groupSnapshot);
        RestoreRecordAmounts(transferSnapshot);
    }

    private static void RestoreRecordAmounts(IReadOnlyList<FlowRecordAmountSnapshot> snapshot)
    {
        foreach (var item in snapshot)
        {
            item.Record.Index = item.Index;
            item.Record.TradeMoney = item.TradeMoney;
            item.Record.Balance = item.Balance;
            item.Record.BalanceAmount = item.BalanceAmount;
            item.Record.CreditAmount = item.CreditAmount;
            item.Record.DebitAmount = item.DebitAmount;
            item.Record.IncomeAttribute = item.IncomeAttribute ?? string.Empty;
            item.Record.IncomeFlag = item.IncomeFlag ?? string.Empty;
        }
    }

    private static bool EnforceLowReferenceRecordRatio(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        if (records.Count == 0 || records.Count >= 700)
        {
            return false;
        }

        var changed = false;
        changed = EnforceLowReferenceRecordRatioForDirection(
            records,
            request,
            start,
            end,
            openingBalance,
            random,
            isIncome: true) || changed;
        changed = EnforceLowReferenceRecordRatioForDirection(
            records,
            request,
            start,
            end,
            openingBalance,
            random,
            isIncome: false) || changed;
        return changed;
    }

    private static bool EnforceLowReferenceRecordRatioForDirection(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random,
        bool isIncome)
    {
        var sign = isIncome ? 1 : -1;
        var signedRecords = records
            .Where(IsReferenceGeneratedRecord)
            .Where(item => GetAmountSign(item.TradeMoney ?? 0) == sign)
            .ToList();
        var lowRecords = signedRecords
            .Where(item => GetGeneratedAmountCapacityTier(Math.Abs(item.TradeMoney ?? 0)) == ReferenceCapacityTier.Low)
            .ToList();
        var excessCount = (int)Math.Ceiling(
            (lowRecords.Count - (MaximumLowRecordRatio * signedRecords.Count))
            / Math.Max(0.0001d, 1d - MaximumLowRecordRatio));
        if (excessCount <= 0)
        {
            return false;
        }

        var sourceCounts = GetExistingGeneratedReferenceIndexCounts(records);
        var rulesByIndex = request.References.ToDictionary(item => item.Index);
        var dayCounts = lowRecords
            .Where(item => item.AccountTime.HasValue)
            .GroupBy(item => item.AccountTime!.Value.Date)
            .ToDictionary(group => group.Key, group => group.Count());
        var removable = lowRecords
            .Where(item => !IsRequiredGeneratedRecord(item))
            .Select(item => new
            {
                Record = item,
                SourceIndex = item.ExtraFields.TryGetValue(SourceIndexField, out var indexText)
                    && ParseInt(indexText) is { } sourceIndex
                        ? sourceIndex
                        : -1
            })
            .Where(item => item.SourceIndex >= 0 && rulesByIndex.ContainsKey(item.SourceIndex))
            .OrderByDescending(item => item.Record.AccountTime.HasValue
                ? dayCounts.GetValueOrDefault(item.Record.AccountTime.Value.Date)
                : 0)
            .ThenByDescending(item => Math.Abs(item.Record.TradeMoney ?? 0))
            .ThenBy(_ => random.Next())
            .ToList();
        var selected = new List<FlowRecord>(excessCount);
        foreach (var candidate in removable)
        {
            var rule = rulesByIndex[candidate.SourceIndex];
            var minimumCoverage = GetFinalOptionalReferenceCoverageTarget(request.Bank, rule);
            if (sourceCounts.GetValueOrDefault(candidate.SourceIndex) <= minimumCoverage)
            {
                continue;
            }

            selected.Add(candidate.Record);
            sourceCounts[candidate.SourceIndex]--;
            if (selected.Count >= excessCount)
            {
                break;
            }
        }

        var changed = false;
        if (selected.Count > 0)
        {
            var originalRecords = records.ToList();
            var originalSnapshot = CaptureRecordAmountSnapshot(originalRecords);
            var transferAmount = RoundMoney(selected.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            foreach (var record in selected)
            {
                records.Remove(record);
            }

            var transferred = TransferSmallAmountTotalToLargeRules(
                records,
                request,
                start,
                end,
                openingBalance,
                random,
                isIncome,
                transferAmount);
            ApplyBalances(records, openingBalance);
            if (transferred < transferAmount - 0.009d
                || GetMinimumBalance(records, openingBalance) < -0.009d)
            {
                records.Clear();
                records.AddRange(originalRecords);
                RestoreRecordAmounts(originalSnapshot);
                ApplyBalances(records, openingBalance);
            }
            else
            {
                changed = true;
            }
        }

        var currentSigned = records
            .Where(IsReferenceGeneratedRecord)
            .Where(item => GetAmountSign(item.TradeMoney ?? 0) == sign)
            .ToList();
        var currentLowCount = currentSigned.Count(item =>
            GetGeneratedAmountCapacityTier(Math.Abs(item.TradeMoney ?? 0)) == ReferenceCapacityTier.Low);
        var additionsNeeded = Math.Max(
            0,
            (int)Math.Ceiling((currentLowCount / MaximumLowRecordRatio) - currentSigned.Count));
        if (additionsNeeded <= 0)
        {
            return changed;
        }

        var nonLowSlices = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .SelectMany(rule => new[] { ReferenceCapacityTier.Medium, ReferenceCapacityTier.High }
                .Select(tier => TryCreateCapacityTierAmountSlice(rule, tier, out var slice) ? slice : null))
            .Where(item => item is not null)
            .Cast<GenerateReferenceRule>()
            .OrderBy(item => GetRuleAmountBounds(item).Min)
            .ThenBy(_ => random.Next())
            .ToList();
        if (nonLowSlices.Count == 0)
        {
            return changed;
        }

        var scheduleState = NativeScheduleState.From(records);
        var amountUsage = AmountUsageTracker.From(records);
        for (var index = 0; index < additionsNeeded; index++)
        {
            var added = false;
            foreach (var rule in nonLowSlices.Skip(index % nonLowSlices.Count)
                         .Concat(nonLowSlices.Take(index % nonLowSlices.Count)))
            {
                if (!TryAddFinalReferenceCoverageRecord(
                        records,
                        request,
                        rule,
                        start,
                        end,
                        openingBalance,
                        random,
                        scheduleState,
                        amountUsage))
                {
                    continue;
                }

                amountUsage = AmountUsageTracker.From(records);
                changed = true;
                added = true;
                break;
            }

            if (!added)
            {
                break;
            }
        }

        return changed;
    }

    private static bool RebalanceLowReferenceNaturalSegmentTotals(
        List<FlowRecord> records,
        double openingBalance,
        Random random)
    {
        if (records.Count == 0 || records.Count >= 700)
        {
            return false;
        }

        var changed = false;
        var groups = records
            .Where(IsReferenceGeneratedRecord)
            .Where(item => !IsSmallDiscreteAmountRecord(item))
            .Where(item => !IsSystemInterestRecord(item))
            .Where(item => Math.Abs(item.TradeMoney ?? 0) > 0.009d)
            .GroupBy(item =>
            {
                var bounds = GetRecordAmountBounds(item);
                return (
                    SourceKey: GetGeneratedAmountDistributionKey(item),
                    Sign: GetAmountSign(item.TradeMoney ?? 0),
                    bounds.Min,
                    bounds.Max,
                    bounds.Unit);
            })
            .Where(group => group.Key.Sign != 0)
            .Where(group => group.Key.Max <= 1000d + 0.009d)
            .Where(group => group.Count() >= 4)
            .OrderByDescending(group => group.Count())
            .ToList();

        foreach (var group in groups)
        {
            var groupRecords = group.ToList();
            var bounds = new AmountBounds(group.Key.Min, group.Key.Max, group.Key.Unit);
            if (CreateRuleAmountSegments(bounds).Count <= 1)
            {
                continue;
            }

            var currentTotal = RoundMoney(groupRecords.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            var desiredTotal = RoundAmountToUnit(
                EstimateNaturalSegmentAverage(bounds) * groupRecords.Count,
                Math.Max(0.01d, bounds.Unit));
            var desiredMove = RoundMoney(desiredTotal - currentTotal);
            if (Math.Abs(desiredMove) < Math.Max(0.01d, bounds.Unit) - 0.009d)
            {
                continue;
            }

            var isIncome = group.Key.Sign > 0;
            var transferCandidates = GetLargeSignedTransferCandidates(records, isIncome)
                .Where(item => !groupRecords.Contains(item, ReferenceEqualityComparer.Instance))
                .ToList();
            if (transferCandidates.Count == 0)
            {
                continue;
            }

            var groupSnapshot = CaptureRecordAmountSnapshot(groupRecords);
            var transferSnapshot = CaptureRecordAmountSnapshot(transferCandidates);
            var transferBefore = RoundMoney(transferCandidates.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
            double moved;
            if (desiredMove > 0)
            {
                DecreaseSignedRecordsWithinBounds(
                    transferCandidates,
                    isIncome,
                    desiredMove,
                    allowDropOptional: false);
                var transferAfter = RoundMoney(transferCandidates.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
                moved = RoundMoney(Math.Max(0, transferBefore - transferAfter));
            }
            else
            {
                moved = IncreaseLargeSignedRecordsWithinBoundsAmount(
                    transferCandidates,
                    isIncome,
                    Math.Abs(desiredMove));
            }

            if (moved <= 0.009d)
            {
                RestoreRecordAmounts(groupSnapshot);
                RestoreRecordAmounts(transferSnapshot);
                continue;
            }

            var nextTarget = desiredMove > 0
                ? RoundMoney(currentTotal + moved)
                : RoundMoney(currentTotal - moved);
            var amounts = CreateBalancedReferenceAmounts(
                bounds,
                groupRecords.Count,
                nextTarget,
                random);
            if (amounts.Length != groupRecords.Count
                || Math.Abs(amounts.Sum() - nextTarget) >= Math.Max(0.01d, bounds.Unit) + 0.009d)
            {
                RestoreRecordAmounts(groupSnapshot);
                RestoreRecordAmounts(transferSnapshot);
                continue;
            }

            amounts = OrderSmallDiscreteAmountsForTimeline(amounts, group.Key.Sign, random);
            for (var index = 0; index < groupRecords.Count; index++)
            {
                ApplySignedAmount(groupRecords[index], group.Key.Sign, amounts[index]);
            }

            ApplyBalances(records, openingBalance);
            if (GetMinimumBalance(records, openingBalance) < -0.009d)
            {
                RestoreRecordAmounts(groupSnapshot);
                RestoreRecordAmounts(transferSnapshot);
                ApplyBalances(records, openingBalance);
                continue;
            }

            changed = true;
        }

        return changed;
    }

    private static bool NormalizeReferenceAmountDistribution(
        List<FlowRecord> records,
        Random random)
    {
        var changed = false;
        var groups = records
            .Where(IsReferenceGeneratedRecord)
            .Where(item => !IsSmallDiscreteAmountRecord(item))
            .Where(item => Math.Abs(item.TradeMoney ?? 0) > 0.009d)
            .Where(item => !IsSystemInterestRecord(item))
            .GroupBy(item =>
            {
                var bounds = GetRecordAmountBounds(item);
                return (
                    SourceKey: GetGeneratedAmountDistributionKey(item),
                    Sign: GetAmountSign(item.TradeMoney ?? 0),
                    bounds.Min,
                    bounds.Max,
                    bounds.Unit);
            })
            .Where(group => group.Key.Sign != 0)
            .ToList();

        foreach (var group in groups)
        {
            changed = NormalizeReferenceAmountGroup(group.ToList(), group.Key.Sign, random) || changed;
        }

        return changed;
    }

    private static bool IsReferenceGeneratedRecord(FlowRecord record)
    {
        return record.ExtraFields.TryGetValue(SourceKindField, out var sourceKind)
            && string.Equals(sourceKind, ReferenceSourceKind, StringComparison.Ordinal);
    }

    private static bool NormalizeReferenceAmountGroup(
        IReadOnlyList<FlowRecord> sourceRecords,
        int sign,
        Random random)
    {
        var records = sourceRecords
            .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
            .ThenBy(item => item.Index)
            .ToList();
        if (records.Count < 2)
        {
            return false;
        }

        var bounds = GetRecordAmountBounds(records[0]);
        var unit = Math.Max(0.01d, bounds.Unit);
        if (bounds.Max <= bounds.Min + unit - 0.0000001d)
        {
            return false;
        }

        var currentTotal = RoundMoney(records.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
        var amounts = CreateBalancedReferenceAmounts(bounds, records.Count, currentTotal, random);
        if (amounts.Length != records.Count)
        {
            return false;
        }

        amounts = amounts
            .OrderBy(_ => random.Next())
            .ToArray();

        var changed = false;
        for (var index = 0; index < records.Count; index++)
        {
            var nextAmount = ClampAmountToBounds(amounts[index], bounds);
            var currentAmount = RoundMoney(Math.Abs(records[index].TradeMoney ?? 0));
            if (Math.Abs(nextAmount - currentAmount) <= 0.009d)
            {
                continue;
            }

            ApplySignedAmount(records[index], sign, nextAmount);
            changed = true;
        }

        return changed;
    }

    private static double[] CreateBalancedReferenceAmounts(
        AmountBounds bounds,
        int count,
        double targetTotal,
        Random random)
    {
        if (count <= 0)
        {
            return [];
        }

        var segments = CreateRuleAmountDistributionSegments(bounds);
        if (segments.Count > 1)
        {
            return CreateSegmentBalancedReferenceAmounts(bounds, segments, count, targetTotal, random);
        }

        var values = EnumerateDiscreteAmounts(bounds, ReferenceAmountDistributionStepLimit)
            .OrderBy(item => item)
            .ToArray();
        if (values.Length >= 2)
        {
            return CreateBalancedDiscreteReferenceAmounts(values, bounds, count, targetTotal, random);
        }

        return CreateBalancedContinuousReferenceAmounts(bounds, count, targetTotal, random);
    }

    private static double[] CreateSegmentBalancedReferenceAmounts(
        AmountBounds bounds,
        IReadOnlyList<AmountSegment> segments,
        int count,
        double targetTotal,
        Random random)
    {
        var unit = Math.Max(0.01d, bounds.Unit);
        var target = RoundAmountToUnit(
            Math.Clamp(targetTotal, bounds.Min * count, bounds.Max * count),
            unit);
        var assignments = CreateBalancedSegmentAssignments(segments, count, target, random);
        var amounts = new double[count];

        foreach (var group in assignments
                     .Select((segment, index) => new { segment, index })
                     .GroupBy(item => item.segment.Index))
        {
            var segment = segments.First(item => item.Index == group.Key);
            var positions = group
                .Select(item => item.index)
                .OrderBy(_ => random.Next())
                .ToList();
            for (var position = 0; position < positions.Count; position++)
            {
                var ratio = positions.Count == 1
                    ? 0.5d
                    : (position + 1d) / (positions.Count + 1d);
                var segmentBounds = new AmountBounds(segment.Min, segment.Max, unit);
                var raw = segment.Min + ((segment.Max - segment.Min) * ratio);
                amounts[positions[position]] = ClampAmountToBounds(
                    RoundAmountToUnit(raw, unit),
                    segmentBounds);
            }
        }

        AdjustSegmentBalancedAmounts(amounts, assignments, target, unit, random);
        return amounts;
    }

    private static AmountSegment[] CreateBalancedSegmentAssignments(
        IReadOnlyList<AmountSegment> segments,
        int count,
        double targetTotal,
        Random random)
    {
        var assignments = new List<AmountSegment>(count);
        var segmentUsage = segments.ToDictionary(segment => segment.Index, _ => 0);
        for (var assignmentIndex = 0; assignmentIndex < count; assignmentIndex++)
        {
            var selectedSegment = segments
                .OrderBy(segment => segmentUsage[segment.Index])
                .ThenBy(_ => random.Next())
                .First();
            assignments.Add(selectedSegment);
            segmentUsage[selectedSegment.Index]++;
        }

        var maxAttempts = Math.Max(8, count * segments.Count * 2);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var minimumTotal = assignments.Sum(item => item.Min);
            var maximumTotal = assignments.Sum(item => item.Max);
            if (targetTotal >= minimumTotal - 0.009d && targetTotal <= maximumTotal + 0.009d)
            {
                break;
            }

            var moveUp = targetTotal > maximumTotal;
            var usage = assignments
                .GroupBy(item => item.Index)
                .ToDictionary(group => group.Key, group => group.Count());
            var candidate = assignments
                .Select((segment, index) => new { segment, index })
                .Where(item => moveUp ? item.segment.Index < segments.Count - 1 : item.segment.Index > 0)
                .OrderBy(item => usage.GetValueOrDefault(
                    moveUp ? item.segment.Index + 1 : item.segment.Index - 1))
                .ThenByDescending(item => usage.GetValueOrDefault(item.segment.Index))
                .ThenBy(_ => random.Next())
                .FirstOrDefault();
            if (candidate is null)
            {
                break;
            }

            assignments[candidate.index] = segments[candidate.segment.Index + (moveUp ? 1 : -1)];
        }

        return assignments
            .OrderBy(_ => random.Next())
            .ToArray();
    }

    private static int GetAmountDistributionThirdIndex(
        AmountSegment segment,
        double overallMinimum,
        double overallMaximum)
    {
        var range = overallMaximum - overallMinimum;
        if (range <= 0.0000001d)
        {
            return 1;
        }

        var midpoint = segment.Min + ((segment.Max - segment.Min) * 0.5d);
        var ratio = Math.Clamp((midpoint - overallMinimum) / range, 0d, 1d);
        return ratio < 1d / 3d
            ? 0
            : ratio < 2d / 3d ? 1 : 2;
    }

    private static void AdjustSegmentBalancedAmounts(
        double[] amounts,
        IReadOnlyList<AmountSegment> assignments,
        double targetTotal,
        double unit,
        Random random)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var diff = RoundMoney(targetTotal - amounts.Sum());
            if (Math.Abs(diff) < unit - 0.009d)
            {
                return;
            }

            var increase = diff > 0;
            var candidates = Enumerable.Range(0, amounts.Length)
                .Where(index => increase
                    ? amounts[index] < assignments[index].Max - 0.009d
                    : amounts[index] > assignments[index].Min + 0.009d)
                .OrderBy(_ => random.Next())
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var remaining = Math.Abs(diff);
            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                var index = candidates[candidateIndex];
                var segment = assignments[index];
                var room = increase ? segment.Max - amounts[index] : amounts[index] - segment.Min;
                var slotsLeft = candidates.Count - candidateIndex;
                var desired = RoundAmountToUnit(remaining / Math.Max(1, slotsLeft), unit);
                var adjustment = Math.Min(room, Math.Max(unit, desired));
                adjustment = FloorAmountToUnit(Math.Min(adjustment, remaining), unit);
                if (adjustment <= 0.009d)
                {
                    continue;
                }

                amounts[index] = RoundAmountToUnit(
                    increase ? amounts[index] + adjustment : amounts[index] - adjustment,
                    unit);
                remaining = RoundMoney(remaining - adjustment);
                if (remaining < unit - 0.009d)
                {
                    break;
                }
            }
        }
    }

    private static double[] CreateBalancedDiscreteReferenceAmounts(
        IReadOnlyList<double> values,
        AmountBounds bounds,
        int count,
        double targetTotal,
        Random random)
    {
        var unit = Math.Max(0.01d, bounds.Unit);
        var min = values[0];
        var max = values[^1];
        var target = RoundAmountToUnit(Math.Clamp(targetTotal, min * count, max * count), unit);
        var result = new List<double>(count);
        var usage = values.ToDictionary(item => item, _ => 0);
        var runningTotal = 0d;

        if (count >= values.Count)
        {
            foreach (var value in values)
            {
                AddBalancedReferenceAmount(result, usage, value);
                runningTotal = RoundMoney(runningTotal + value);
            }
        }
        else
        {
            for (var index = 0; index < count; index++)
            {
                var valueIndex = count == 1
                    ? values.Count / 2
                    : (int)Math.Round(index * (values.Count - 1d) / (count - 1d), MidpointRounding.AwayFromZero);
                var value = values[Math.Clamp(valueIndex, 0, values.Count - 1)];
                AddBalancedReferenceAmount(result, usage, value);
                runningTotal = RoundMoney(runningTotal + value);
            }
        }

        var range = Math.Max(unit, max - min);
        var edgeLimit = GetReferenceEdgeUsageLimit(count);
        while (result.Count < count)
        {
            var remainingCount = count - result.Count;
            var slotsAfterThis = remainingCount - 1;
            var remainingTotal = RoundMoney(target - runningTotal);
            var minAfter = min * slotsAfterThis;
            var maxAfter = max * slotsAfterThis;
            var averageNeeded = remainingTotal / remainingCount;
            var candidate = values
                .Where(value => value <= remainingTotal - minAfter + 0.009d)
                .Where(value => value >= remainingTotal - maxAfter - 0.009d)
                .OrderBy(value => GetReferenceCandidateScore(value, averageNeeded, usage[value], min, max, range, edgeLimit, random))
                .FirstOrDefault(double.NaN);
            if (double.IsNaN(candidate))
            {
                candidate = values
                    .OrderBy(value => usage[value])
                    .ThenBy(value => Math.Abs(value - averageNeeded))
                    .First();
            }

            AddBalancedReferenceAmount(result, usage, candidate);
            runningTotal = RoundMoney(runningTotal + candidate);
        }

        AdjustBalancedReferenceAmounts(result, bounds, target, random);
        return result.ToArray();
    }

    private static double[] CreateBalancedContinuousReferenceAmounts(
        AmountBounds bounds,
        int count,
        double targetTotal,
        Random random)
    {
        var unit = Math.Max(0.01d, bounds.Unit);
        var min = bounds.Min;
        var max = bounds.Max;
        var range = Math.Max(unit, max - min);
        var target = RoundAmountToUnit(Math.Clamp(targetTotal, min * count, max * count), unit);
        var bucketCount = Math.Clamp((int)Math.Round(Math.Sqrt(count) * 1.8d, MidpointRounding.AwayFromZero), 6, 18);
        var result = new List<double>(count);
        for (var index = 0; index < count; index++)
        {
            var bucket = index % bucketCount;
            var ratio = (bucket + 0.18d + (random.NextDouble() * 0.64d)) / bucketCount;
            var amount = RoundAmountToUnit(min + (range * ratio), unit);
            result.Add(ClampAmountToBounds(amount, bounds));
        }

        AdjustBalancedReferenceAmounts(result, bounds, target, random);
        return result.ToArray();
    }

    private static void AddBalancedReferenceAmount(
        ICollection<double> result,
        IDictionary<double, int> usage,
        double value)
    {
        result.Add(value);
        usage[value] = usage.TryGetValue(value, out var count) ? count + 1 : 1;
    }

    private static double GetReferenceCandidateScore(
        double value,
        double averageNeeded,
        int usage,
        double min,
        double max,
        double range,
        int edgeLimit,
        Random random)
    {
        var edgePenalty = IsReferenceEdgeValue(value, min, max)
            ? usage >= edgeLimit ? range * 18d : range * 1.5d
            : 0d;
        var usagePenalty = usage * Math.Max(0.01d, range * 0.22d);
        return Math.Abs(value - averageNeeded)
            + usagePenalty
            + edgePenalty
            + (random.NextDouble() * Math.Max(0.01d, range * 0.015d));
    }

    private static void AdjustBalancedReferenceAmounts(
        List<double> amounts,
        AmountBounds bounds,
        double targetTotal,
        Random random)
    {
        if (amounts.Count == 0)
        {
            return;
        }

        var unit = Math.Max(0.01d, bounds.Unit);
        var min = bounds.Min;
        var max = bounds.Max;
        var edgeLimit = GetReferenceEdgeUsageLimit(amounts.Count);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var diff = RoundMoney(targetTotal - amounts.Sum());
            if (Math.Abs(diff) < unit - 0.009d)
            {
                FinishReferenceAmountBalancing(amounts, bounds, random);
                return;
            }

            var increase = diff > 0;
            var allowEdges = attempt >= 8;
            var minUsage = amounts.Count(item => Math.Abs(item - min) <= 0.009d);
            var maxUsage = amounts.Count(item => Math.Abs(item - max) <= 0.009d);
            var candidates = Enumerable.Range(0, amounts.Count)
                .Where(index => increase ? amounts[index] < max - 0.009d : amounts[index] > min + 0.009d)
                .Where(index => allowEdges || CanMoveReferenceAmountWithoutEdgePileup(
                    amounts[index],
                    increase,
                    unit,
                    min,
                    max,
                    minUsage,
                    maxUsage,
                    edgeLimit))
                .OrderBy(index => increase ? amounts[index] : -amounts[index])
                .ThenBy(_ => random.Next())
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var capacity = RoundMoney(candidates.Sum(index => increase ? max - amounts[index] : amounts[index] - min));
            if (capacity <= 0.009d)
            {
                FinishReferenceAmountBalancing(amounts, bounds, random);
                return;
            }

            var progressed = false;
            foreach (var index in candidates)
            {
                diff = RoundMoney(targetTotal - amounts.Sum());
                var remaining = Math.Abs(diff);
                if (remaining < unit - 0.009d)
                {
                    return;
                }

                var room = RoundMoney(increase ? max - amounts[index] : amounts[index] - min);
                if (room <= 0.009d)
                {
                    continue;
                }

                var share = remaining * (room / capacity);
                var adjustment = RoundAmountToUnit(Math.Min(room, Math.Max(unit, share)), unit);
                if (adjustment > remaining + 0.009d)
                {
                    adjustment = FloorAmountToUnit(remaining, unit);
                }

                if (adjustment <= 0.009d)
                {
                    continue;
                }

                var next = increase
                    ? ClampAmountToBounds(amounts[index] + adjustment, bounds)
                    : ClampAmountToBounds(amounts[index] - adjustment, bounds);
                if (Math.Abs(next - amounts[index]) <= 0.009d)
                {
                    continue;
                }

                amounts[index] = next;
                progressed = true;
            }

            if (!progressed && allowEdges)
            {
                FinishReferenceAmountBalancing(amounts, bounds, random);
                return;
            }
        }

        FinishReferenceAmountBalancing(amounts, bounds, random);
    }

    private static void FinishReferenceAmountBalancing(
        List<double> amounts,
        AmountBounds bounds,
        Random random)
    {
        BalanceReferenceEdgeUsage(amounts, bounds, random);
        BalanceReferenceValueUsage(amounts, bounds, random);
        BalanceReferenceEdgeUsage(amounts, bounds, random);
        EnsureReferenceEdgePresence(amounts, bounds, random);
    }

    private static void BalanceReferenceEdgeUsage(
        List<double> amounts,
        AmountBounds bounds,
        Random random)
    {
        if (amounts.Count < 3)
        {
            return;
        }

        var unit = Math.Max(0.01d, bounds.Unit);
        var min = bounds.Min;
        var max = bounds.Max;
        if (max <= min + unit - 0.0000001d)
        {
            return;
        }

        var edgeLimit = GetReferenceEdgeUsageLimit(amounts.Count);
        var maxAttempts = Math.Max(16, amounts.Count * 4);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var maxIndexes = Enumerable.Range(0, amounts.Count)
                .Where(index => Math.Abs(amounts[index] - max) <= 0.009d)
                .OrderBy(_ => random.Next())
                .ToList();
            if (maxIndexes.Count > edgeLimit)
            {
                var targetIndex = maxIndexes[0];
                var compensateIndex = Enumerable.Range(0, amounts.Count)
                    .Where(index => index != targetIndex)
                    .Where(index => amounts[index] <= max - (unit * 2d) + 0.009d)
                    .OrderBy(index => amounts[index])
                    .ThenBy(_ => random.Next())
                    .FirstOrDefault(-1);
                if (compensateIndex >= 0)
                {
                    amounts[targetIndex] = ClampAmountToBounds(amounts[targetIndex] - unit, bounds);
                    amounts[compensateIndex] = ClampAmountToBounds(amounts[compensateIndex] + unit, bounds);
                    continue;
                }
            }

            var minIndexes = Enumerable.Range(0, amounts.Count)
                .Where(index => Math.Abs(amounts[index] - min) <= 0.009d)
                .OrderBy(_ => random.Next())
                .ToList();
            if (minIndexes.Count > edgeLimit)
            {
                var targetIndex = minIndexes[0];
                var compensateIndex = Enumerable.Range(0, amounts.Count)
                    .Where(index => index != targetIndex)
                    .Where(index => amounts[index] >= min + (unit * 2d) - 0.009d)
                    .OrderByDescending(index => amounts[index])
                    .ThenBy(_ => random.Next())
                    .FirstOrDefault(-1);
                if (compensateIndex >= 0)
                {
                    amounts[targetIndex] = ClampAmountToBounds(amounts[targetIndex] + unit, bounds);
                    amounts[compensateIndex] = ClampAmountToBounds(amounts[compensateIndex] - unit, bounds);
                    continue;
                }
            }

            return;
        }
    }

    private static void BalanceReferenceValueUsage(
        List<double> amounts,
        AmountBounds bounds,
        Random random)
    {
        if (amounts.Count < 4)
        {
            return;
        }

        var values = EnumerateDiscreteAmounts(bounds, ReferenceAmountDistributionStepLimit)
            .OrderBy(item => item)
            .ToArray();
        if (values.Length < 4 || amounts.Count < values.Length)
        {
            return;
        }

        var unit = Math.Max(0.01d, bounds.Unit);
        var min = values[0];
        var max = values[^1];
        var averagePerValue = amounts.Count / (double)values.Length;
        var usageLimit = Math.Max(
            GetReferenceEdgeUsageLimit(amounts.Count),
            (int)Math.Ceiling(averagePerValue * 1.6d));
        var valueSet = values.ToHashSet();
        var maxAttempts = Math.Max(32, amounts.Count * 10);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var usage = amounts
                .Select(item => RoundAmountToUnit(item, unit))
                .GroupBy(item => item)
                .ToDictionary(group => group.Key, group => group.Count());
            var overused = usage
                .Where(item => item.Value > usageLimit)
                .OrderByDescending(item => item.Value - usageLimit)
                .ThenByDescending(item => Math.Abs(item.Key - ((min + max) / 2d)))
                .FirstOrDefault();
            if (overused.Value <= usageLimit)
            {
                return;
            }

            var overValue = overused.Key;
            var decreaseOverValue = overValue >= (min + max) / 2d;
            var nextOverValue = decreaseOverValue
                ? values
                    .Where(value => value < overValue - 0.009d)
                    .Where(value => usage.GetValueOrDefault(value) < usageLimit)
                    .OrderByDescending(value => value)
                    .FirstOrDefault(double.NaN)
                : values
                    .Where(value => value > overValue + 0.009d)
                    .Where(value => usage.GetValueOrDefault(value) < usageLimit)
                    .OrderBy(value => value)
                    .FirstOrDefault(double.NaN);
            if (double.IsNaN(nextOverValue) || !valueSet.Contains(nextOverValue))
            {
                usage[overValue] = usageLimit;
                continue;
            }

            var delta = RoundMoney(Math.Abs(overValue - nextOverValue));
            if (delta <= 0.009d)
            {
                usage[overValue] = usageLimit;
                continue;
            }

            var overIndex = Enumerable.Range(0, amounts.Count)
                .Where(index => Math.Abs(RoundAmountToUnit(amounts[index], unit) - overValue) <= 0.009d)
                .OrderBy(_ => random.Next())
                .FirstOrDefault(-1);
            if (overIndex < 0)
            {
                return;
            }

            var compensateIndex = Enumerable.Range(0, amounts.Count)
                .Where(index => index != overIndex)
                .Where(index =>
                {
                    var current = RoundAmountToUnit(amounts[index], unit);
                    if (decreaseOverValue
                        && Math.Abs(current - min) <= 0.009d
                        && usage.GetValueOrDefault(min) <= GetReferenceEdgeUsageLimit(amounts.Count))
                    {
                        return false;
                    }

                    if (!decreaseOverValue
                        && Math.Abs(current - max) <= 0.009d
                        && usage.GetValueOrDefault(max) <= GetReferenceEdgeUsageLimit(amounts.Count))
                    {
                        return false;
                    }

                    var next = RoundAmountToUnit(decreaseOverValue ? current + delta : current - delta, unit);
                    return valueSet.Contains(next)
                        && usage.GetValueOrDefault(next) < usageLimit
                        && (decreaseOverValue ? current <= max - delta + 0.009d : current >= min + delta - 0.009d);
                })
                .OrderBy(index => decreaseOverValue ? amounts[index] : -amounts[index])
                .ThenBy(_ => random.Next())
                .FirstOrDefault(-1);
            if (compensateIndex < 0)
            {
                return;
            }

            amounts[overIndex] = ClampAmountToBounds(nextOverValue, bounds);
            amounts[compensateIndex] = ClampAmountToBounds(
                amounts[compensateIndex] + (decreaseOverValue ? delta : -delta),
                bounds);
        }
    }

    private static void EnsureReferenceEdgePresence(
        List<double> amounts,
        AmountBounds bounds,
        Random random)
    {
        var values = EnumerateDiscreteAmounts(bounds, ReferenceAmountDistributionStepLimit)
            .OrderBy(item => item)
            .ToArray();
        if (values.Length < 4 || amounts.Count < values.Length)
        {
            return;
        }

        EnsureReferenceSingleEdgePresence(amounts, bounds, values[0], values[^1], ensureMax: false, random);
        EnsureReferenceSingleEdgePresence(amounts, bounds, values[0], values[^1], ensureMax: true, random);
    }

    private static void EnsureReferenceSingleEdgePresence(
        List<double> amounts,
        AmountBounds bounds,
        double min,
        double max,
        bool ensureMax,
        Random random)
    {
        var unit = Math.Max(0.01d, bounds.Unit);
        var edge = ensureMax ? max : min;
        if (amounts.Any(item => Math.Abs(item - edge) <= 0.009d))
        {
            return;
        }

        var edgeIndex = Enumerable.Range(0, amounts.Count)
            .Where(index => ensureMax ? amounts[index] <= max - unit + 0.009d : amounts[index] >= min + unit - 0.009d)
            .OrderBy(index => ensureMax ? Math.Abs(amounts[index] - (max - unit)) : Math.Abs(amounts[index] - (min + unit)))
            .ThenBy(_ => random.Next())
            .FirstOrDefault(-1);
        if (edgeIndex < 0)
        {
            return;
        }

        var delta = RoundMoney(Math.Abs(edge - amounts[edgeIndex]));
        if (delta <= 0.009d)
        {
            return;
        }

        var compensateIndex = Enumerable.Range(0, amounts.Count)
            .Where(index => index != edgeIndex)
            .Where(index => ensureMax ? amounts[index] >= min + delta - 0.009d : amounts[index] <= max - delta + 0.009d)
            .OrderBy(index => ensureMax ? -amounts[index] : amounts[index])
            .ThenBy(_ => random.Next())
            .FirstOrDefault(-1);
        if (compensateIndex < 0)
        {
            return;
        }

        amounts[edgeIndex] = edge;
        amounts[compensateIndex] = ClampAmountToBounds(
            amounts[compensateIndex] + (ensureMax ? -delta : delta),
            bounds);
    }

    private static int GetReferenceEdgeUsageLimit(int count)
    {
        return Math.Max(1, (int)Math.Ceiling(count * ReferenceAmountEdgeUsageRatio));
    }

    private static bool IsReferenceEdgeValue(double value, double min, double max)
    {
        return Math.Abs(value - min) <= 0.009d || Math.Abs(value - max) <= 0.009d;
    }

    private static bool CanMoveReferenceAmountWithoutEdgePileup(
        double amount,
        bool increase,
        double unit,
        double min,
        double max,
        int minUsage,
        int maxUsage,
        int edgeUsageLimit)
    {
        var next = increase ? amount + unit : amount - unit;
        if (increase && next >= max - 0.009d && maxUsage >= edgeUsageLimit)
        {
            return false;
        }

        if (!increase && next <= min + 0.009d && minUsage >= edgeUsageLimit)
        {
            return false;
        }

        return true;
    }

    private static bool EnsureSmallDiscreteAmountGroupCoverage(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        List<FlowRecord> groupRecords,
        int sign,
        Random random)
    {
        if (groupRecords.Count == 0 || sign <= 0)
        {
            return false;
        }

        var bounds = GetRecordAmountBounds(groupRecords[0]);
        var values = EnumerateDiscreteAmounts(bounds, SmallDiscreteAmountStepLimit)
            .OrderByDescending(item => item)
            .ToList();
        if (values.Count == 0 || groupRecords.Count >= values.Count)
        {
            return false;
        }

        var missingValues = values.Skip(groupRecords.Count).ToList();
        var requiredIncome = RoundMoney(missingValues.Sum());
        if (requiredIncome <= 0.009d || GetAvailableIncomeRoom(records, request) < requiredIncome - 0.009d)
        {
            return false;
        }

        var template = groupRecords
            .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
            .FirstOrDefault(item => ResolveRecordSourceRule(request, item) is not null);
        if (template is null || ResolveRecordSourceRule(request, template) is not FlowRuleBase rule)
        {
            return false;
        }

        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start)
        {
            (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        }

        var ruleColumns = rule is GenerateConstRule ? request.Bank.ConstColumns : request.Bank.ReferenceColumns;
        var scheduleState = NativeScheduleState.From(records);
        foreach (var amount in missingValues)
        {
            var accountTime = PickNativeDistributedTime(start, end, rule, isIncome: true, random, scheduleState);
            var record = CreateRecordFromRule(request, rule, ruleColumns, accountTime, amount);
            record.ExtraFields[RequiredOccurrenceField] = "false";
            records.Add(record);
            groupRecords.Add(record);
            scheduleState.Register(record);
        }

        return true;
    }

    private static bool NormalizeSmallDiscreteAmountGroup(
        IReadOnlyList<FlowRecord> sourceRecords,
        int sign,
        Random random)
    {
        var records = sourceRecords
            .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
            .ThenBy(item => item.Index)
            .ToList();
        if (records.Count < 2)
        {
            return false;
        }

        var bounds = GetRecordAmountBounds(records[0]);
        if (!IsSmallDiscreteAmountBounds(bounds))
        {
            return false;
        }

        var values = EnumerateDiscreteAmounts(bounds, SmallDiscreteAmountStepLimit);
        if (values.Count < 2)
        {
            return false;
        }

        var currentTotal = RoundMoney(records.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
        var amounts = CreateBalancedSmallDiscreteAmounts(values, bounds, records.Count, currentTotal, random);
        if (amounts.Length != records.Count)
        {
            return false;
        }

        amounts = OrderSmallDiscreteAmountsForTimeline(amounts, sign, random);

        var changed = false;
        for (var index = 0; index < records.Count; index++)
        {
            var nextAmount = ClampAmountToBounds(amounts[index], bounds);
            var currentAmount = RoundMoney(Math.Abs(records[index].TradeMoney ?? 0));
            if (Math.Abs(nextAmount - currentAmount) <= 0.009d)
            {
                continue;
            }

            ApplySignedAmount(records[index], sign, nextAmount);
            changed = true;
        }

        return changed;
    }

    private static double[] CreateBalancedSmallDiscreteAmounts(
        IReadOnlyList<double> sourceValues,
        AmountBounds bounds,
        int count,
        double currentTotal,
        Random random)
    {
        if (count <= 0)
        {
            return [];
        }

        var values = sourceValues
            .OrderBy(item => item)
            .ToArray();
        if (values.Length == 0)
        {
            return [];
        }

        if (values.Length == 1)
        {
            return Enumerable.Repeat(values[0], count).ToArray();
        }

        var min = values[0];
        var max = values[^1];
        var unit = Math.Max(1d, bounds.Unit);
        var target = RoundAmountToUnit(Math.Clamp(currentTotal, min * count, max * count), unit);
        var result = new List<double>(count);
        var seedSum = values.Sum();
        var remainingCount = count - values.Length;
        var canSeedCoverage = remainingCount >= 0
            && target >= seedSum + (min * remainingCount) - 0.009d
            && target <= seedSum + (max * remainingCount) + 0.009d;
        if (canSeedCoverage)
        {
            result.AddRange(values);
        }

        var fillCount = count - result.Count;
        var fillTotal = RoundMoney(target - result.Sum());
        if (fillCount > 0)
        {
            result.AddRange(CreateSmallDiscreteQuantileAmounts(values, bounds, fillCount, fillTotal, random));
        }

        AdjustSmallDiscreteAmountsToTarget(result, values, bounds, target, random);
        BalanceSmallDiscreteValueUsage(result, values, bounds, target, random);
        AdjustSmallDiscreteAmountsToTarget(result, values, bounds, target, random);

        return result.ToArray();
    }

    private static IEnumerable<double> CreateSmallDiscreteQuantileAmounts(
        IReadOnlyList<double> values,
        AmountBounds bounds,
        int count,
        double targetTotal,
        Random random)
    {
        if (count <= 0)
        {
            yield break;
        }

        var min = values[0];
        var max = values[^1];
        var unit = Math.Max(1d, bounds.Unit);
        var range = Math.Max(unit, max - min);
        var targetAverage = Math.Clamp(targetTotal / count, min, max);
        var targetRatio = Math.Clamp((targetAverage - min) / range, 0.02d, 0.98d);
        var exponent = targetRatio >= 0.5d
            ? Math.Max(0.08d, (1d / targetRatio) - 1d)
            : Math.Max(0.08d, targetRatio / (1d - targetRatio));
        var offset = random.NextDouble();

        for (var index = 0; index < count; index++)
        {
            var u = ((index + 0.5d + offset) % count) / count;
            u = Math.Clamp(u, 0.000001d, 0.999999d);
            var ratio = targetRatio >= 0.5d
                ? Math.Pow(u, exponent)
                : 1d - Math.Pow(1d - u, exponent);
            var amount = ClampAmountToBounds(min + (range * ratio), bounds);
            yield return SnapToNearestDiscreteValue(amount, values, unit);
        }
    }

    private static double SnapToNearestDiscreteValue(double amount, IReadOnlyList<double> values, double unit)
    {
        if (values.Count == 0)
        {
            return amount;
        }

        var min = values[0];
        var index = (int)Math.Round((amount - min) / Math.Max(0.01d, unit), MidpointRounding.AwayFromZero);
        index = Math.Clamp(index, 0, values.Count - 1);
        return values[index];
    }

    private static void AdjustSmallDiscreteAmountsToTarget(
        List<double> amounts,
        IReadOnlyList<double> values,
        AmountBounds bounds,
        double targetTotal,
        Random random)
    {
        if (amounts.Count == 0)
        {
            return;
        }

        var unit = Math.Max(1d, bounds.Unit);
        var valueSet = values.ToHashSet();
        var attempts = Math.Min(60000, Math.Max(200, amounts.Count * 12));
        var currentTotal = RoundMoney(amounts.Sum());
        var usage = amounts
            .GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var diff = RoundMoney(targetTotal - currentTotal);
            if (Math.Abs(diff) < unit - 0.009d)
            {
                return;
            }

            var increase = diff > 0;
            var index = Enumerable.Range(0, amounts.Count)
                .Select(item =>
                {
                    var current = amounts[item];
                    var next = RoundAmountToUnit(current + (increase ? unit : -unit), unit);
                    return new
                    {
                        Index = item,
                        Current = current,
                        Next = next,
                        CanMove = valueSet.Contains(next)
                    };
                })
                .Where(item => item.CanMove)
                .OrderBy(item => usage.GetValueOrDefault(item.Next))
                .ThenByDescending(item => usage.GetValueOrDefault(item.Current))
                .ThenBy(item => IsSmallDiscreteEdgeValue(item.Next, values[0], values[^1]) ? 1 : 0)
                .ThenBy(_ => random.Next())
                .Select(item => item.Index)
                .FirstOrDefault(-1);
            if (index < 0)
            {
                return;
            }

            var before = amounts[index];
            var after = RoundAmountToUnit(before + (increase ? unit : -unit), unit);
            amounts[index] = after;
            usage[before] = Math.Max(0, usage.GetValueOrDefault(before) - 1);
            usage[after] = usage.GetValueOrDefault(after) + 1;
            currentTotal = RoundMoney(currentTotal + (increase ? unit : -unit));
        }
    }

    private static void BalanceSmallDiscreteValueUsage(
        List<double> amounts,
        IReadOnlyList<double> values,
        AmountBounds bounds,
        double targetTotal,
        Random random)
    {
        if (amounts.Count < 4 || values.Count < 4)
        {
            return;
        }

        var unit = Math.Max(1d, bounds.Unit);
        var min = values[0];
        var max = values[^1];
        var averageRatio = Math.Clamp(((targetTotal / amounts.Count) - min) / Math.Max(unit, max - min), 0d, 1d);
        var skewAllowance = Math.Abs(averageRatio - 0.5d) * 0.42d;
        var usageLimit = Math.Max(
            GetSmallDiscreteEdgeUsageLimit(amounts.Count),
            (int)Math.Ceiling(amounts.Count * (0.08d + skewAllowance)));
        var valueSet = values.ToHashSet();
        var attempts = Math.Min(60000, Math.Max(200, amounts.Count * 10));
        var usage = amounts
            .GroupBy(item => item)
            .ToDictionary(group => group.Key, group => group.Count());

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var overused = usage
                .Where(item => item.Value > usageLimit)
                .OrderByDescending(item => item.Value - usageLimit)
                .FirstOrDefault();
            if (overused.Value <= usageLimit)
            {
                return;
            }

            var overValue = overused.Key;
            var moveDown = overValue >= (min + max) / 2d;
            var nextOverValue = RoundAmountToUnit(overValue + (moveDown ? -unit : unit), unit);
            if (!valueSet.Contains(nextOverValue))
            {
                return;
            }

            var compensateIndex = -1;
            var bestUsage = int.MaxValue;
            for (var index = 0; index < amounts.Count; index++)
            {
                if (Math.Abs(amounts[index] - overValue) <= 0.009d)
                {
                    continue;
                }

                var next = RoundAmountToUnit(amounts[index] + (moveDown ? unit : -unit), unit);
                if (!valueSet.Contains(next))
                {
                    continue;
                }

                var candidateUsage = usage.GetValueOrDefault(next);
                if (candidateUsage < bestUsage || (candidateUsage == bestUsage && random.Next(2) == 0))
                {
                    bestUsage = candidateUsage;
                    compensateIndex = index;
                }
            }

            var overIndex = -1;
            var overMatches = 0;
            for (var index = 0; index < amounts.Count; index++)
            {
                if (Math.Abs(amounts[index] - overValue) > 0.009d)
                {
                    continue;
                }

                overMatches++;
                if (random.Next(overMatches) == 0)
                {
                    overIndex = index;
                }
            }

            if (overIndex < 0 || compensateIndex < 0)
            {
                return;
            }

            var compensateBefore = amounts[compensateIndex];
            var compensateAfter = RoundAmountToUnit(compensateBefore + (moveDown ? unit : -unit), unit);
            amounts[overIndex] = nextOverValue;
            amounts[compensateIndex] = compensateAfter;
            usage[overValue] = Math.Max(0, usage.GetValueOrDefault(overValue) - 1);
            usage[nextOverValue] = usage.GetValueOrDefault(nextOverValue) + 1;
            usage[compensateBefore] = Math.Max(0, usage.GetValueOrDefault(compensateBefore) - 1);
            usage[compensateAfter] = usage.GetValueOrDefault(compensateAfter) + 1;
        }
    }

    private static double[] OrderSmallDiscreteAmountsForTimeline(
        IEnumerable<double> amounts,
        int sign,
        Random random)
    {
        var ordered = sign < 0
            ? amounts.OrderBy(item => item).ToArray()
            : amounts.OrderByDescending(item => item).ToArray();
        if (ordered.Length < 8)
        {
            return ordered;
        }

        var blockSize = Math.Clamp((int)Math.Round(Math.Sqrt(ordered.Length), MidpointRounding.AwayFromZero), 4, 16);
        for (var start = 0; start < ordered.Length; start += blockSize)
        {
            var length = Math.Min(blockSize, ordered.Length - start);
            var block = ordered
                .Skip(start)
                .Take(length)
                .OrderBy(_ => random.Next())
                .ToArray();
            Array.Copy(block, 0, ordered, start, length);
        }

        return ordered;
    }

    private static bool NormalizeSmallDiscreteExpenseAmountGroup(
        IReadOnlyList<FlowRecord> records,
        int sign,
        IReadOnlyList<double> values,
        AmountBounds bounds,
        Random random)
    {
        if (records.Count < 2 || values.Count < 2)
        {
            return false;
        }

        var currentTotal = RoundMoney(records.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
        var amounts = CreateBalancedSmallDiscreteExpenseAmounts(values, bounds, records.Count, currentTotal, random);
        if (amounts.Length != records.Count)
        {
            return false;
        }

        var changed = false;
        for (var index = 0; index < records.Count; index++)
        {
            var nextAmount = ClampAmountToBounds(amounts[index], bounds);
            var currentAmount = RoundMoney(Math.Abs(records[index].TradeMoney ?? 0));
            if (Math.Abs(nextAmount - currentAmount) <= 0.009d)
            {
                continue;
            }

            ApplySignedAmount(records[index], sign, nextAmount);
            changed = true;
        }

        return changed;
    }

    private static double[] CreateBalancedSmallDiscreteExpenseAmounts(
        IReadOnlyList<double> sourceValues,
        AmountBounds bounds,
        int count,
        double currentTotal,
        Random random)
    {
        if (count <= 0)
        {
            return [];
        }

        var values = sourceValues
            .OrderBy(item => item)
            .ToArray();
        if (values.Length == 0)
        {
            return [];
        }

        if (values.Length == 1)
        {
            return Enumerable.Repeat(values[0], count).ToArray();
        }

        var min = values[0];
        var max = values[^1];
        var range = Math.Max(bounds.Unit, max - min);
        var currentAverage = count > 0 ? currentTotal / count : min;
        var balancedAverageMin = min + (range * SmallDiscreteAverageMinRatio);
        var balancedAverageMax = min + (range * SmallDiscreteAverageMaxRatio);
        var targetAverage = Math.Clamp(currentAverage, balancedAverageMin, balancedAverageMax);
        var targetTotal = RoundAmountToUnit(targetAverage * count, Math.Max(1d, bounds.Unit));

        var result = new List<double>(count);
        var usage = values.ToDictionary(item => item, _ => 0);
        if (count >= values.Length)
        {
            foreach (var value in values)
            {
                result.Add(value);
                usage[value]++;
            }
        }
        else
        {
            for (var index = 0; index < count; index++)
            {
                var valueIndex = count == 1
                    ? values.Length / 2
                    : (int)Math.Round(index * (values.Length - 1d) / (count - 1d), MidpointRounding.AwayFromZero);
                var value = values[Math.Clamp(valueIndex, 0, values.Length - 1)];
                result.Add(value);
                usage[value]++;
            }
        }

        var remainingCount = count - result.Count;
        var remainingTotal = RoundMoney(targetTotal - result.Sum());
        var edgeUsageLimit = GetSmallDiscreteEdgeUsageLimit(count);
        while (remainingCount > 0)
        {
            var slotsAfterThis = remainingCount - 1;
            var minAfter = min * slotsAfterThis;
            var maxAfter = max * slotsAfterThis;
            var averageNeeded = remainingTotal / remainingCount;
            var usagePenalty = Math.Max(1d, range * 0.18d);
            var candidate = double.NaN;
            var bestScore = double.MaxValue;
            foreach (var value in values)
            {
                if (value > remainingTotal - minAfter + 0.009d
                    || value < remainingTotal - maxAfter - 0.009d
                    || IsSmallDiscreteEdgeValue(value, min, max) && usage[value] >= edgeUsageLimit)
                {
                    continue;
                }

                var score = Math.Abs(value - averageNeeded)
                    + (usage[value] * usagePenalty)
                    + (random.NextDouble() * 0.05d);
                if (score >= bestScore)
                {
                    continue;
                }

                candidate = value;
                bestScore = score;
            }

            if (double.IsNaN(candidate))
            {
                var bestUsage = int.MaxValue;
                var bestDistance = double.MaxValue;
                foreach (var value in values)
                {
                    if (IsSmallDiscreteEdgeValue(value, min, max) && usage[value] >= edgeUsageLimit)
                    {
                        continue;
                    }

                    var valueUsage = usage[value];
                    var distance = Math.Abs(value - averageNeeded);
                    if (valueUsage > bestUsage
                        || valueUsage == bestUsage && distance >= bestDistance)
                    {
                        continue;
                    }

                    candidate = value;
                    bestUsage = valueUsage;
                    bestDistance = distance;
                }
            }

            if (double.IsNaN(candidate))
            {
                var bestUsage = int.MaxValue;
                var bestDistance = double.MaxValue;
                foreach (var value in values)
                {
                    var valueUsage = usage[value];
                    var distance = Math.Abs(value - averageNeeded);
                    if (valueUsage > bestUsage
                        || valueUsage == bestUsage && distance >= bestDistance)
                    {
                        continue;
                    }

                    candidate = value;
                    bestUsage = valueUsage;
                    bestDistance = distance;
                }
            }

            result.Add(candidate);
            usage[candidate]++;
            remainingTotal = RoundMoney(remainingTotal - candidate);
            remainingCount--;
        }

        AdjustBalancedSmallDiscreteExpenseAmounts(result, values, bounds, targetTotal, random);

        return result
            .OrderBy(_ => random.Next())
            .ToArray();
    }

    private static void AdjustBalancedSmallDiscreteExpenseAmounts(
        List<double> amounts,
        IReadOnlyList<double> values,
        AmountBounds bounds,
        double targetTotal,
        Random random)
    {
        var unit = Math.Max(1d, bounds.Unit);
        var min = values[0];
        var max = values[^1];
        var edgeUsageLimit = GetSmallDiscreteEdgeUsageLimit(amounts.Count);
        var runningTotal = RoundMoney(amounts.Sum());
        var minUsage = 0;
        var maxUsage = 0;
        foreach (var amount in amounts)
        {
            if (Math.Abs(amount - min) <= 0.009d)
            {
                minUsage++;
            }

            if (Math.Abs(amount - max) <= 0.009d)
            {
                maxUsage++;
            }
        }

        var attempts = Math.Min(12000, Math.Max(100, amounts.Count * 8));
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var diff = RoundMoney(targetTotal - runningTotal);
            if (Math.Abs(diff) < unit - 0.009d)
            {
                return;
            }

            var increase = diff > 0;
            var indexToMove = -1;
            var bestAmount = increase ? double.MaxValue : double.MinValue;
            var bestTieBreaker = int.MaxValue;
            for (var index = 0; index < amounts.Count; index++)
            {
                var amount = amounts[index];
                if (increase ? amount >= max - 0.009d : amount <= min + 0.009d)
                {
                    continue;
                }

                if (!CanMoveSmallDiscreteAmountWithoutEdgePileup(
                        amount,
                        increase,
                        unit,
                        min,
                        max,
                        minUsage,
                        maxUsage,
                        edgeUsageLimit))
                {
                    continue;
                }

                var tieBreaker = random.Next();
                if ((increase && amount > bestAmount)
                    || (!increase && amount < bestAmount)
                    || Math.Abs(amount - bestAmount) <= 0.009d && tieBreaker >= bestTieBreaker)
                {
                    continue;
                }

                indexToMove = index;
                bestAmount = amount;
                bestTieBreaker = tieBreaker;
            }

            if (indexToMove < 0)
            {
                return;
            }

            var next = increase
                ? ClampAmountToBounds(amounts[indexToMove] + unit, bounds)
                : ClampAmountToBounds(amounts[indexToMove] - unit, bounds);
            if (Math.Abs(next - amounts[indexToMove]) <= 0.009d)
            {
                return;
            }

            var previous = amounts[indexToMove];
            if (Math.Abs(previous - min) <= 0.009d)
            {
                minUsage = Math.Max(0, minUsage - 1);
            }

            if (Math.Abs(previous - max) <= 0.009d)
            {
                maxUsage = Math.Max(0, maxUsage - 1);
            }

            amounts[indexToMove] = next;
            if (Math.Abs(next - min) <= 0.009d)
            {
                minUsage++;
            }

            if (Math.Abs(next - max) <= 0.009d)
            {
                maxUsage++;
            }

            runningTotal = RoundMoney(runningTotal + next - previous);
        }
    }

    private static int GetSmallDiscreteEdgeUsageLimit(int count)
    {
        return Math.Max(1, (int)Math.Ceiling(count * 0.02d));
    }

    private static bool IsSmallDiscreteEdgeValue(double value, double min, double max)
    {
        return Math.Abs(value - min) <= 0.009d || Math.Abs(value - max) <= 0.009d;
    }

    private static bool CanMoveSmallDiscreteAmountWithoutEdgePileup(
        double amount,
        bool increase,
        double unit,
        double min,
        double max,
        int minUsage,
        int maxUsage,
        int edgeUsageLimit)
    {
        var next = increase ? amount + unit : amount - unit;
        if (increase && next >= max - 0.009d && maxUsage >= edgeUsageLimit)
        {
            return false;
        }

        if (!increase && next <= min + 0.009d && minUsage >= edgeUsageLimit)
        {
            return false;
        }

        return true;
    }

    private static List<double> EnumerateDiscreteAmounts(AmountBounds bounds, int limit)
    {
        var result = new List<double>();
        var unit = Math.Max(0.01d, bounds.Unit);
        var count = CountDiscreteAmountSteps(bounds, limit);
        if (count <= 0 || count > limit)
        {
            return result;
        }

        for (var index = 0; index < count; index++)
        {
            result.Add(ClampAmountToBounds(bounds.Min + (index * unit), bounds));
        }

        return result
            .Distinct()
            .ToList();
    }

    private static double PickDiscreteProfileAmount(IReadOnlyList<double> values, Random random)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        if (values.Count <= 3)
        {
            return values[random.Next(values.Count)];
        }

        var lowCount = Math.Clamp((int)Math.Round(values.Count * 0.40d, MidpointRounding.AwayFromZero), 1, values.Count - 2);
        var highCount = Math.Clamp((int)Math.Ceiling(values.Count * 0.25d), 1, values.Count - lowCount - 1);
        var middleStart = lowCount;
        var highStart = Math.Clamp(values.Count - highCount, middleStart + 1, values.Count - 1);
        var bucket = random.NextDouble();
        if (bucket < 0.40d)
        {
            return values[random.Next(0, lowCount)];
        }

        if (bucket < 0.80d)
        {
            return values[random.Next(middleStart, highStart)];
        }

        return values[random.Next(highStart, values.Count)];
    }

    private static double MakeAmountDistinctWithinBounds(
        double amount,
        AmountBounds bounds,
        IEnumerable<FlowRecord> existingRecords,
        bool isIncome,
        Random random)
    {
        amount = ClampAmountToBounds(amount, bounds);
        var unit = Math.Max(0.01d, bounds.Unit);
        var totalSteps = Math.Floor((bounds.Max - bounds.Min + 0.0000001d) / unit) + 1d;
        if (totalSteps <= 1d)
        {
            return amount;
        }

        var usedAmounts = existingRecords
            .Where(item => isIncome ? item.TradeMoney > 0.009d : item.TradeMoney < -0.009d)
            .Select(item => RoundAmountToUnit(Math.Abs(item.TradeMoney ?? 0), unit))
            .ToHashSet();
        var preferFractional = false;
        if ((!usedAmounts.Contains(amount) && !preferFractional) || usedAmounts.Count >= totalSteps)
        {
            return amount;
        }

        if (TryPickDistinctAmount(
                amount,
                bounds,
                usedAmounts,
                preferredDelta: 0,
                preferFractional,
                out var distinctAmount))
        {
            return distinctAmount;
        }

        return amount;
    }

    private static bool ShouldPreferLowerOptionalIncome(IReadOnlyList<GenerateReferenceRule> rules, bool isIncome)
    {
        if (!isIncome || rules.Count == 0)
        {
            return false;
        }

        var smallest = rules.Select(item => GetRuleAmountBounds(item).Min).DefaultIfEmpty(0).Min();
        var largest = rules.Select(item => GetRuleAmountBounds(item).Max).DefaultIfEmpty(0).Max();
        return smallest >= 1000d && largest > smallest * 4d;
    }

    private static int GetNativeRepeatCount(string? value, Random random)
    {
        var tokens = ParseIntTokens(value)
            .Where(item => item > 0)
            .Distinct()
            .OrderBy(item => item)
            .ToList();
        if (tokens.Count == 0)
        {
            return 1;
        }

        if (tokens.Count == 1)
        {
            return Math.Max(1, tokens[0]);
        }

        return random.Next(tokens[0], tokens[^1] + 1);
    }

    private static DateTime NormalizeNativeFixedDay(DateTime day, FlowRuleBase rule, DateTime end)
    {
        var result = day.Date;
        while (result <= end.Date && rule.TradeWeekend == false && result.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            result = result.AddDays(1);
        }

        return result;
    }

    private static DateTime PickNativeDistributedTime(
        DateTime start,
        DateTime end,
        FlowRuleBase rule,
        bool isIncome,
        Random random,
        NativeScheduleState scheduleState)
    {
        var candidates = scheduleState.GetCandidateDates(start, end, rule);
        if (candidates.Count == 0)
        {
            return PickTime(start, end, rule, random);
        }

        var sequence = scheduleState.NextSequence();
        var desired = candidates.Count == 0 ? 0 : Math.Abs((sequence * 7 + 3) % candidates.Count);
        var selected = candidates
            .Select((date, index) => new
            {
                Date = date,
                Index = index,
                Same = scheduleState.GetSameDirectionCount(date, isIncome),
                NeighborSame = scheduleState.GetNeighborSameDirectionCount(date, isIncome),
                Total = scheduleState.GetTotalCount(date)
            })
            .OrderBy(item => item.Same)
            .ThenBy(item => item.NeighborSame)
            .ThenBy(item => item.Total)
            .ThenBy(item => Math.Abs(item.Index - desired))
            .ThenBy(_ => random.Next())
            .First()
            .Date;

        return PickNativeTimeOnDate(selected, rule, random, scheduleState, sequence);
    }

    private static DateTime? PickNativeDistributedTimeAtOrAfter(
        DateTime start,
        DateTime end,
        FlowRuleBase rule,
        bool isIncome,
        Random random,
        NativeScheduleState scheduleState)
    {
        var normalizedEnd = NormalizeEndDate(end);
        if (start > normalizedEnd)
        {
            return null;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = PickNativeDistributedTime(start, normalizedEnd, rule, isIncome, random, scheduleState);
            if (candidate >= start && candidate <= normalizedEnd)
            {
                return candidate;
            }
        }

        var startHour = Math.Clamp(rule.StartDay ?? 9, 0, 23);
        var endHour = Math.Clamp(rule.EndDay ?? 17, 0, 23);
        if (endHour < startHour)
        {
            (startHour, endHour) = (endHour, startHour);
        }

        foreach (var date in EnumerateNativeCandidateDates(start, normalizedEnd, rule))
        {
            var earliest = date.Date.AddHours(startHour);
            if (date.Date == start.Date && earliest < start)
            {
                earliest = TrimToSecond(start).AddSeconds(1);
            }

            var latest = date.Date.AddHours(endHour + 1).AddTicks(-1);
            if (latest > normalizedEnd)
            {
                latest = normalizedEnd;
            }

            if (earliest > latest)
            {
                continue;
            }

            var availableSeconds = Math.Max(0, (int)Math.Floor((latest - earliest).TotalSeconds));
            for (var attempt = 0; attempt < 240; attempt++)
            {
                var offset = availableSeconds == 0 ? 0 : random.Next(0, availableSeconds + 1);
                var candidate = earliest.AddSeconds(offset);
                if (scheduleState.IsTimeUsed(candidate))
                {
                    continue;
                }

                scheduleState.MarkTime(candidate);
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<DateTime> EnumerateNativeCandidateDates(DateTime start, DateTime end, FlowRuleBase rule)
    {
        var normalizedEnd = NormalizeEndDate(end);
        foreach (var date in EnumerateDates(start.Date, normalizedEnd.Date))
        {
            if (date < start.Date || date > normalizedEnd.Date)
            {
                continue;
            }

            if (rule.TradeWeekend == false && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            yield return date;
        }
    }

    private static DateTime PickNativeTimeOnDate(
        DateTime date,
        FlowRuleBase rule,
        Random random,
        NativeScheduleState scheduleState,
        int sequence)
    {
        var startHour = Math.Clamp(rule.StartDay ?? 9, 0, 23);
        var endHour = Math.Clamp(rule.EndDay ?? 17, 0, 23);
        if (endHour < startHour)
        {
            (startHour, endHour) = (endHour, startHour);
        }

        var hourSpan = Math.Max(1, endHour - startHour + 1);
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var hour = startHour + Math.Abs((sequence + attempt + random.Next(0, hourSpan)) % hourSpan);
            var minute = Math.Abs(((sequence * 11) + (attempt * 7) + random.Next(0, 60)) % 60);
            var second = Math.Abs(((sequence * 17) + (attempt * 13) + random.Next(0, 60)) % 60);
            var time = date.Date.AddHours(hour).AddMinutes(minute).AddSeconds(second);
            if (!scheduleState.IsTimeUsed(time))
            {
                scheduleState.MarkTime(time);
                return time;
            }
        }

        var fallback = date.Date.AddHours(startHour).AddMinutes(sequence % 60).AddSeconds((sequence / 60) % 60);
        scheduleState.MarkTime(fallback);
        return fallback;
    }

    private static DateTime TrimToSecond(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Kind);
    }

    private static MonthlyAmountPlan CreateMonthlyAmountPlan(
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double targetIncome,
        double targetExpense,
        Random random)
    {
        var months = EnumerateMonths(start, end).ToList();
        var selectedRules = request.References.Where(item => item.IsCheck).ToList();
        var incomeRules = selectedRules.Where(IsIncomeRuleSafe).ToList();
        var expenseRules = selectedRules.Where(item => !IsIncomeRuleSafe(item)).ToList();

        var incomeTargets = CreateMonthlyTargets(request.Config, months, targetIncome, true, random, incomeRules);
        var expenseTargets = CreateMonthlyTargets(request.Config, months, targetExpense, false, random, expenseRules);
        if (request.Config.SelectIndex != 2)
        {
            AmplifyMonthlyBalanceSwing(incomeTargets, expenseTargets, random);
            PreventFirstMonthIncomeSpike(incomeTargets, targetIncome, random);
            BalanceMonthlyIncomeAcrossTimeline(incomeTargets, targetIncome, random);
            SpreadMonthlyExpenseTargets(expenseTargets, incomeTargets, targetExpense, expenseRules);
        }

        return new MonthlyAmountPlan
        {
            Months = months,
            IncomeTargets = incomeTargets,
            ExpenseTargets = expenseTargets
        };
    }

    private static void GenerateReferenceRecords(
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        ICollection<FlowRecord> records,
        MonthlyAmountPlan monthlyPlan)
    {
        var selectedRules = request.References.Where(item => item.IsCheck).ToList();
        if (selectedRules.Count == 0)
        {
            return;
        }

        var months = monthlyPlan.Months;
        if (months.Count == 0)
        {
            return;
        }

        var incomeTargets = monthlyPlan.IncomeTargets;
        var expenseTargets = monthlyPlan.ExpenseTargets;
        var incomeRules = selectedRules.Where(IsIncomeRuleSafe).ToList();
        var expenseRules = selectedRules.Where(item => !IsIncomeRuleSafe(item)).ToList();

        for (var monthIndex = 0; monthIndex < months.Count; monthIndex++)
        {
            var month = months[monthIndex];
            var existingIncome = SumIncome(records.Where(item => IsRecordInRange(item, month.Start, month.End)));
            var existingExpense = SumExpense(records.Where(item => IsRecordInRange(item, month.Start, month.End)));

            GenerateReferenceRecordsForMonth(
                request,
                month.Start,
                month.End,
                incomeRules,
                RoundMoney(incomeTargets[monthIndex] - existingIncome),
                true,
                random,
                records);

            GenerateReferenceRecordsForMonth(
                request,
                month.Start,
                month.End,
                expenseRules,
                RoundMoney(expenseTargets[monthIndex] - existingExpense),
                false,
                random,
                records);
        }
    }

    private static void GenerateReferenceRecordsForMonth(
        FlowAutoGenerationRequest request,
        DateTime monthStart,
        DateTime monthEnd,
        IReadOnlyList<GenerateReferenceRule> rules,
        double targetAmount,
        bool isIncome,
        Random random,
        ICollection<FlowRecord> records)
    {
        targetAmount = RoundMoney(Math.Max(0, targetAmount));
        if (rules.Count == 0)
        {
            return;
        }

        var runningTotal = 0d;
        foreach (var rule in rules)
        {
            var requiredCount = GetRequiredMonthlyCount(rule);
            for (var requiredIndex = 0; requiredIndex < requiredCount; requiredIndex++)
            {
                var amount = Math.Abs(CreateSignedAmount(rule, random));
                if (amount <= 0.009d)
                {
                    continue;
                }

                runningTotal = RoundMoney(runningTotal + amount);
                records.Add(CreateRecordFromRule(
                    request,
                    rule,
                    request.Bank.ReferenceColumns,
                    PickTime(monthStart, monthEnd, rule, random),
                    isIncome ? amount : -amount));
            }
        }

        var optionalRules = rules
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .ToList();
        var optionalTarget = RoundMoney(Math.Max(0, targetAmount - runningTotal));
        if (optionalRules.Count == 0 || optionalTarget <= 0.009d)
        {
            return;
        }

        var optionalRuleOrder = optionalRules
            .OrderBy(_ => random.Next())
            .ToList();
        var targetCount = CalculateReferenceTargetCount(optionalRules, optionalTarget, random);
        runningTotal = 0d;
        for (var index = 0; index < targetCount; index++)
        {
            var remaining = RoundMoney(optionalTarget - runningTotal);
            var rule = PickCycledReferenceRule(optionalRuleOrder, index, remaining, random);
            var amount = Math.Abs(CreateSignedAmount(rule, random));
            if (remaining <= 0.009d)
            {
                break;
            }

            var bounds = GetRuleAmountBounds(rule);
            if (remaining < bounds.Min - 0.009d && runningTotal > 0)
            {
                break;
            }

            if (index == targetCount - 1 || amount > remaining)
            {
                amount = ClampAmountToBounds(remaining, bounds);
            }

            if (amount <= 0.009d)
            {
                continue;
            }

            runningTotal = RoundMoney(runningTotal + amount);
            records.Add(CreateRecordFromRule(
                request,
                rule,
                request.Bank.ReferenceColumns,
                PickTime(monthStart, monthEnd, rule, random),
                isIncome ? amount : -amount));
        }
    }

    private static int CalculateReferenceTargetCount(IReadOnlyList<GenerateReferenceRule> rules, double targetAmount, Random random)
    {
        var configuredCount = rules.Sum(item => Math.Max(0, item.PercentMonth ?? 1));
        if (configuredCount <= 0)
        {
            configuredCount = 1;
        }

        var averageAmount = EstimateAverageAmount(rules);
        var amountCount = averageAmount <= 0
            ? configuredCount
            : (int)Math.Ceiling(targetAmount / averageAmount);
        var variedConfiguredCount = (int)Math.Round(configuredCount * CreateCountFactor(random), MidpointRounding.AwayFromZero);
        var targetCount = Math.Max(1, Math.Max(variedConfiguredCount, amountCount));
        return Math.Clamp(targetCount, 1, Math.Max(configuredCount * 4, rules.Count));
    }

    private static double EstimateAverageAmount(IEnumerable<GenerateReferenceRule> rules)
    {
        var averages = rules
            .Select(item =>
            {
                var bounds = GetRuleAmountBounds(item);

                return (bounds.Min + bounds.Max) / 2d;
            })
            .Where(item => item > 0)
            .ToList();

        return averages.Count == 0 ? 0 : averages.Average();
    }

    private static double CreateCountFactor(Random random)
    {
        return 0.55d + (random.NextDouble() * 0.9d);
    }

    private static GenerateReferenceRule PickWeightedReferenceRule(IReadOnlyList<GenerateReferenceRule> rules, Random random)
    {
        var totalWeight = rules.Sum(item => Math.Max(1, item.PercentMonth ?? 1));
        var cursor = random.NextDouble() * totalWeight;
        foreach (var rule in rules)
        {
            cursor -= Math.Max(1, rule.PercentMonth ?? 1);
            if (cursor <= 0)
            {
                return rule;
            }
        }

        return rules[^1];
    }

    private static GenerateReferenceRule PickWeightedReferenceRule(
        IReadOnlyList<GenerateReferenceRule> rules,
        double remainingAmount,
        Random random)
    {
        var affordableRules = rules
            .Where(item => GetRuleAmountBounds(item).Min <= remainingAmount + 0.009d)
            .ToList();

        return PickWeightedReferenceRule(affordableRules.Count > 0 ? affordableRules : rules, random);
    }

    private static GenerateReferenceRule PickCycledReferenceRule(
        IReadOnlyList<GenerateReferenceRule> rules,
        int index,
        double remainingAmount,
        Random random)
    {
        if (rules.Count == 0)
        {
            throw new ArgumentException("At least one rule is required.", nameof(rules));
        }

        var preferred = rules[index % rules.Count];
        if (GetRuleAmountBounds(preferred).Min <= remainingAmount + 0.009d)
        {
            return preferred;
        }

        var affordableRules = rules
            .Where(item => GetRuleAmountBounds(item).Min <= remainingAmount + 0.009d)
            .ToList();
        if (affordableRules.Count == 0)
        {
            return PickWeightedReferenceRule(rules, random);
        }

        return affordableRules[index % affordableRules.Count];
    }

    private static void GenerateConstRecords(
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        ICollection<FlowRecord> records)
    {
        foreach (var rule in request.ConstItems.Where(item => item.IsCheck))
        {
            var repeatCount = Math.Max(1, ParseInt(rule.ReCnt) ?? 1);
            foreach (var day in ResolveFixedDays(rule, start, end))
            {
                for (var index = 0; index < repeatCount; index++)
                {
                    var amount = CreateSignedAmount(rule, random);
                    if (Math.Abs(amount) < 0.009d)
                    {
                        continue;
                    }

                    records.Add(CreateRecordFromRule(
                        request,
                        rule,
                        request.Bank.ConstColumns,
                        PickTime(day.Date, day.Date.AddDays(1).AddTicks(-1), rule, random).AddMinutes(index),
                        amount));
                }
            }
        }
    }

    private static void GenerateInterestRecords(
        FlowAutoGenerationRequest request,
        double openingBalance,
        DateTime start,
        DateTime end,
        Random random,
        ICollection<FlowRecord> records)
    {
        var setting = request.InterestSetting;
        if (setting is null)
        {
            return;
        }

        var day = ParseInt(setting.SettlementDay);
        var ratePercent = ParseDouble(setting.RatePercent);
        if (day is null || ratePercent is null || ratePercent <= 0)
        {
            return;
        }

        var months = ParseIntTokens(setting.Months)
            .Where(item => item is >= 1 and <= 12)
            .Distinct()
            .ToHashSet();
        if (months.Count == 0)
        {
            return;
        }

        var startHour = Math.Clamp(ParseInt(setting.StartTime) ?? 0, 0, 23);
        var endHour = Math.Clamp(ParseInt(setting.EndTime) ?? 23, 0, 23);
        if (endHour < startHour)
        {
            (startHour, endHour) = (endHour, startHour);
        }

        foreach (var month in EnumerateMonths(start, end))
        {
            if (!months.Contains(month.Start.Month))
            {
                continue;
            }

            var settlementDay = Math.Min(day.Value, DateTime.DaysInMonth(month.Start.Year, month.Start.Month));
            var time = new DateTime(month.Start.Year, month.Start.Month, settlementDay, random.Next(startHour, endHour + 1), random.Next(0, 60), random.Next(0, 60));
            if (time < start || time > end)
            {
                continue;
            }

            var interest = CalculateDailyProductInterest(records, openingBalance, start, time, ratePercent.Value);
            records.Add(CreateInterestRecord(request, setting, time, interest, InterestRowKind, InterestText, records));

            if (ShouldAppendInterestTaxRecord(request.Bank))
            {
                records.Add(CreateInterestRecord(request, setting, time, 0, InterestTaxRowKind, InterestTaxText, records));
            }
        }
    }

    private static FlowRecord CreateInterestRecord(
        FlowAutoGenerationRequest request,
        BankInterestSetting setting,
        DateTime accountTime,
        double amount,
        string rowKind,
        string brief,
        IEnumerable<FlowRecord> existingRecords)
    {
        var record = CreateBaseRecord(request, accountTime, Math.Max(0, amount));
        record.MoveFlag = false;
        record.ProductBrief = brief;
        record.Remark = PersonalCurrentInterestRemark;
        record.SerialNum = rowKind == InterestTaxRowKind ? "0000000002" : "0000000001";
        record.LogNum = "0000000001";
        record.ExtraFields[AmountUnitField] = "0.01";
        record.ExtraFields[SystemRowKindField] = rowKind;

        ApplyInterestRecordDefaults(request, record, rowKind, existingRecords);
        if (string.IsNullOrWhiteSpace(record.Remark) || record.Remark == InterestText)
        {
            record.Remark = PersonalCurrentInterestRemark;
        }

        if (string.IsNullOrWhiteSpace(record.LogNum))
        {
            record.LogNum = "0000000001";
        }

        ClearInterestDisplayFields(record);
        ApplyUserAccountToRecord(request, record);
        ApplyInterestFields(record, setting);
        ApplyInterestRowKindLabels(record, rowKind);

        return record;
    }

    private static void ClearInterestDisplayFields(FlowRecord record)
    {
        record.ProductName = string.Empty;
        record.ProductCode = string.Empty;
        record.ProductBrief = string.Empty;
        record.ProductType = string.Empty;
        record.SerialNum = string.Empty;
        record.Operator = string.Empty;
        record.OperatorNum = string.Empty;
        record.OppositeAccount = string.Empty;
        record.OppositeUsername = string.Empty;
        record.OppositeBank = string.Empty;
        record.BranchNum = string.Empty;
        record.Usage = string.Empty;
        record.AppNum = string.Empty;
        record.SequenceNum = string.Empty;
        record.Currency = string.Empty;
        record.CashCheck = string.Empty;
        record.TradeCode = string.Empty;
        record.TradeCurrency = string.Empty;
        record.Remark = string.Empty;
        record.DepositTerm = string.Empty;
        record.AgreedTerm = string.Empty;
        record.NoticeType = string.Empty;
        record.AreaNum = string.Empty;
        record.NetNum = string.Empty;
        record.InterfacePage = string.Empty;
        record.TradePlace = string.Empty;
        record.TradeChannel = string.Empty;
        record.TradeChannelEn = string.Empty;
        record.TradeExplain = string.Empty;
        record.AccountNum = string.Empty;
        record.SubAccountNum = string.Empty;
        record.VoucherType = string.Empty;
        record.VoucherNum = string.Empty;
        record.LogNum = string.Empty;
        record.MerchantName = string.Empty;
        record.TerminalNum = string.Empty;
        record.HandleStatus = string.Empty;
        record.Year = string.Empty;
        record.CreditType = string.Empty;
        record.ReceiptNum = string.Empty;
    }

    private sealed record InterestRecordFieldProfile(
        string AppNum,
        string SequenceNum,
        string CashCheck,
        string DepositTerm,
        string AgreedTerm,
        string NoticeType,
        string AreaNum,
        string NetNum,
        string BranchNum,
        string Operator,
        string OperatorNum,
        string InterfacePage,
        string TradeChannel,
        string TradeCurrency,
        string Currency,
        string TradeCode);

    private static void ApplyInterestRecordDefaults(
        FlowAutoGenerationRequest request,
        FlowRecord record,
        string rowKind,
        IEnumerable<FlowRecord> existingRecords)
    {
        var profile = CreateInterestRecordFieldProfile(existingRecords);
        var isIcbc = request.Bank.Name.Contains("工行", StringComparison.Ordinal)
            || request.Bank.Name.Contains("工商", StringComparison.Ordinal);

        record.Currency = FirstNonEmpty(record.Currency, profile.Currency, request.BankUser.Currency, "RMB");
        record.TradeCurrency = FirstNonEmpty(record.TradeCurrency, profile.TradeCurrency, record.Currency, "RMB");
        record.AppNum = FirstNonEmpty(record.AppNum, profile.AppNum, isIcbc ? "1" : string.Empty);
        record.SequenceNum = FirstNonEmpty(record.SequenceNum, profile.SequenceNum, isIcbc ? "0" : string.Empty);
        record.CashCheck = FirstNonEmpty(record.CashCheck, profile.CashCheck, isIcbc ? "钞" : "转账");
        record.DepositTerm = FirstNonEmpty(record.DepositTerm, profile.DepositTerm, isIcbc ? "000" : string.Empty);
        record.AgreedTerm = FirstNonEmpty(record.AgreedTerm, profile.AgreedTerm, isIcbc ? "不转存" : string.Empty);
        record.NoticeType = FirstNonEmpty(record.NoticeType, profile.NoticeType, isIcbc ? "0" : string.Empty);
        record.AreaNum = FirstNonEmpty(record.AreaNum, profile.AreaNum);
        record.NetNum = FirstNonEmpty(record.NetNum, profile.NetNum);
        record.BranchNum = FirstNonEmpty(record.BranchNum, profile.BranchNum);
        record.Operator = FirstNonEmpty(record.Operator, profile.Operator);
        record.OperatorNum = FirstNonEmpty(record.OperatorNum, profile.OperatorNum);
        record.InterfacePage = FirstNonEmpty(record.InterfacePage, profile.InterfacePage);
        record.TradeChannel = FirstNonEmpty(record.TradeChannel, profile.TradeChannel, request.Bank.Name == "支付宝" ? "电子商务" : "柜面");
        record.TradeCode = FirstNonEmpty(record.TradeCode, profile.TradeCode);
        record.ProductName = FirstNonEmpty(record.ProductName, rowKind == InterestTaxRowKind ? "利息税" : "结息");
        record.ProductType = FirstNonEmpty(record.ProductType, "活期");
        record.Usage = FirstNonEmpty(record.Usage, rowKind == InterestTaxRowKind ? "利息税" : "结息");
        record.TradeExplain = FirstNonEmpty(record.TradeExplain, rowKind == InterestTaxRowKind ? "利息税" : "结息");
        record.Remark = FirstNonEmpty(record.Remark, rowKind == InterestTaxRowKind ? "利息税" : "个人活期结息");
    }

    private static InterestRecordFieldProfile CreateInterestRecordFieldProfile(IEnumerable<FlowRecord> records)
    {
        var normalRecords = records
            .Where(item => !IsSystemInterestRecord(item))
            .ToList();

        return new InterestRecordFieldProfile(
            CommonValue(normalRecords, item => item.AppNum),
            CommonValue(normalRecords, item => item.SequenceNum),
            CommonValue(normalRecords, item => item.CashCheck),
            CommonValue(normalRecords, item => item.DepositTerm),
            CommonValue(normalRecords, item => item.AgreedTerm),
            CommonValue(normalRecords, item => item.NoticeType),
            CommonValue(normalRecords, item => item.AreaNum),
            CommonValue(normalRecords, item => item.NetNum),
            CommonValue(normalRecords, item => item.BranchNum),
            CommonValue(normalRecords, item => item.Operator),
            CommonValue(normalRecords, item => item.OperatorNum),
            CommonValue(normalRecords, item => item.InterfacePage),
            CommonValue(normalRecords, item => item.TradeChannel),
            CommonValue(normalRecords, item => item.TradeCurrency),
            CommonValue(normalRecords, item => item.Currency),
            CommonValue(normalRecords, item => item.TradeCode));
    }

    private static string CommonValue(IEnumerable<FlowRecord> records, Func<FlowRecord, string?> selector)
    {
        var values = records
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();

        return values.Count == 1 ? values[0] : string.Empty;
    }

    private static double CalculateDailyProductInterest(
        IEnumerable<FlowRecord> records,
        double openingBalance,
        DateTime start,
        DateTime settlementTime,
        double ratePercent)
    {
        var startDate = start.Date;
        var settlementDate = settlementTime.Date;
        if (settlementDate <= startDate)
        {
            return 0;
        }

        var dayAmounts = records
            .Where(item => item.AccountTime.HasValue)
            .Where(item => item.AccountTime!.Value.Date >= startDate && item.AccountTime.Value.Date <= settlementDate)
            .GroupBy(item => item.AccountTime!.Value.Date)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.TradeMoney ?? 0));

        var orderedDays = dayAmounts
            .OrderBy(item => item.Key)
            .ToList();

        var previousDate = startDate;
        var balance = openingBalance;
        var dailyProduct = 0d;
        foreach (var current in orderedDays)
        {
            if (current.Key > previousDate)
            {
                dailyProduct += Math.Max(0, balance) * (current.Key - previousDate).Days;
            }

            balance += current.Value;
            previousDate = current.Key;
        }

        if (settlementDate > previousDate)
        {
            dailyProduct += Math.Max(0, balance) * (settlementDate - previousDate).Days;
        }

        return RoundMoney(dailyProduct * ratePercent / 36500d);
    }

    private static void RecalculateInterestRecords(
        List<FlowRecord> records,
        double openingBalance,
        DateTime start,
        BankInterestSetting? setting)
    {
        var ratePercent = ParseDouble(setting?.RatePercent);
        if (ratePercent is null || ratePercent <= 0)
        {
            return;
        }

        var interestRows = records
            .Where(item => item.ExtraFields.TryGetValue(SystemRowKindField, out var value) && value == InterestRowKind)
            .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
            .ToList();
        if (interestRows.Count == 0)
        {
            return;
        }

        foreach (var systemRow in records.Where(IsSystemInterestRecord))
        {
            SetSystemInterestAmount(systemRow, 0);
        }

        foreach (var interestRow in interestRows)
        {
            if (!interestRow.AccountTime.HasValue)
            {
                continue;
            }

            var interest = CalculateDailyProductInterest(records, openingBalance, start, interestRow.AccountTime.Value, ratePercent.Value);
            SetSystemInterestAmount(interestRow, interest);
        }
    }

    private static void SetSystemInterestAmount(FlowRecord record, double amount)
    {
        amount = RoundMoney(Math.Max(0, amount));
        record.TradeMoney = amount;
        record.CreditAmount = amount > 0 ? amount : null;
        record.DebitAmount = null;
        record.IncomeAttribute = amount >= 0 ? "收入" : "支出";
        record.IncomeFlag = amount >= 0 ? "C" : "D";
    }

    private static bool ShouldAppendInterestTaxRecord(Bank bank)
    {
        if (IsAgriculturalBank(bank))
        {
            return true;
        }

        return bank.Name.Contains("农行", StringComparison.Ordinal)
            || bank.Name.Contains("农业", StringComparison.Ordinal);
    }

    private static FlowRecord CreateRecordFromRule(
        FlowAutoGenerationRequest request,
        FlowRuleBase rule,
        IReadOnlyList<ColumnDefinition> ruleColumns,
        DateTime accountTime,
        double amount)
    {
        var amountBounds = GetRuleAmountBounds(rule);
        var amountSign = amount < -0.009d ? -1d : 1d;
        amount = amountSign * ClampAmountToBounds(Math.Abs(amount), amountBounds);
        var record = CreateBaseRecord(request, accountTime, amount);
        CopyMatchingProperties(rule, record);
        CopyByMatchingColumnNames(rule, ruleColumns, request.Bank.FlowColumns, record);
        ApplyRuleColumnAliases(request.Bank, ruleColumns, rule, record);

        record.AccountTime = accountTime;
        record.TradeMoney = amount;
        record.ExtraFields[AmountUnitField] = FormatInvariant(GetAmountUnit(rule.FloutLength));
        record.ExtraFields[AmountMinField] = FormatInvariant(amountBounds.Min);
        record.ExtraFields[AmountMaxField] = FormatInvariant(amountBounds.Max);
        record.ExtraFields[SourceKindField] = rule is GenerateConstRule ? ConstSourceKind : ReferenceSourceKind;
        record.ExtraFields[SourceIndexField] = rule.Index.ToString(CultureInfo.InvariantCulture);
        record.ExtraFields[RequiredOccurrenceField] = IsRequiredRule(rule) ? "true" : "false";
        if (IsSmallDiscreteAmountRule(request.Bank, rule, amountBounds))
        {
            record.ExtraFields[SmallDiscreteAmountField] = bool.TrueString;
        }

        if (IsAgriculturalSmallExpenseRule(request.Bank, rule))
        {
            record.ExtraFields[AgriculturalSmallExpenseField] = bool.TrueString;
        }

        if (IsAgriculturalBank(request.Bank) && amountBounds.Unit < 1d - 0.0000001d)
        {
            record.ExtraFields[AvoidSyntheticFractionField] = bool.TrueString;
        }

        record.IncomeAttribute = amount >= 0 ? "收入" : "支出";
        record.CreditAmount = amount > 0 ? amount : null;
        record.DebitAmount = amount < 0 ? Math.Abs(amount) : null;
        record.IncomeFlag = amount >= 0 ? "C" : "D";

        if (string.IsNullOrWhiteSpace(record.ProductBrief))
        {
            record.ProductBrief = FirstNonEmpty(rule.ProductBrief, rule.Remark, rule.IncomeType, request.Bank.Name);
        }

        if (string.IsNullOrWhiteSpace(record.CashCheck))
        {
            record.CashCheck = "转账";
        }

        if (string.IsNullOrWhiteSpace(record.Currency))
        {
            record.Currency = FirstNonEmpty(rule.Currency, request.BankUser.Currency, "RMB");
        }

        if (string.IsNullOrWhiteSpace(record.TradeCurrency))
        {
            record.TradeCurrency = FirstNonEmpty(rule.TradeCurrency, record.Currency, "RMB");
        }

        ApplyBankSpecificGeneratedRecordFallbacks(request.Bank, rule, record);

        ApplyUserAccountToRecord(request, record);

        ApplyRecordColumnAliasBackfill(request.Bank, record);
        ApplyAutoGeneratedSystemFields(request, record);
        return record;
    }

    private static void ApplyBankSpecificGeneratedRecordFallbacks(Bank bank, FlowRuleBase rule, FlowRecord record)
    {
        if (!bank.Name.Contains("建行", StringComparison.Ordinal)
            && !bank.Name.Contains("建设", StringComparison.Ordinal))
        {
            return;
        }

        var tradePlace = FirstNonEmpty(
            record.TradePlace,
            rule.TradePlace,
            record.TradeExplain,
            rule.TradeExplain,
            record.MerchantName,
            rule.MerchantName,
            record.ProductBrief,
            rule.ProductBrief,
            record.OppositeBank,
            rule.OppositeBank);

        if (string.IsNullOrWhiteSpace(record.TradePlace))
        {
            record.TradePlace = tradePlace;
        }

        if (string.IsNullOrWhiteSpace(record.Remark))
        {
            record.Remark = FirstNonEmpty(
                rule.Remark,
                tradePlace,
                record.Usage,
                rule.Usage,
                record.ProductBrief,
                rule.ProductBrief);
        }
    }

    private static FlowRecord CreateBaseRecord(FlowAutoGenerationRequest request, DateTime accountTime, double amount)
    {
        return new FlowRecord
        {
            BankId = request.Bank.Id,
            BankUserId = request.BankUser.Id,
            AccountTime = accountTime,
            TradeMoney = RoundMoney(amount),
            IncomeAttribute = amount >= 0 ? "收入" : "支出",
            Account = ResolveFlowAccountValue(request),
            Currency = FirstNonEmpty(request.BankUser.Currency, "RMB"),
            TradeCurrency = FirstNonEmpty(request.BankUser.Currency, "RMB"),
            CreditAmount = amount > 0 ? RoundMoney(amount) : null,
            DebitAmount = amount < 0 ? RoundMoney(Math.Abs(amount)) : null,
            IncomeFlag = amount >= 0 ? "C" : "D"
        };
    }

    private static void ApplyUserAccountToRecord(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var accountValue = ResolveFlowAccountValue(request);
        if (!string.IsNullOrWhiteSpace(accountValue))
        {
            record.Account = accountValue;
        }
    }

    private static string ResolveFlowAccountValue(FlowAutoGenerationRequest request)
    {
        var accountColumn = request.Bank.FlowColumns
            .Where(item => string.Equals(item.Field, nameof(FlowRecord.Account), StringComparison.Ordinal))
            .FirstOrDefault(item => IsCardNumberName(item.Name) || IsAccountNumberName(item.Name));
        var preferCard = accountColumn is not null && IsCardNumberName(accountColumn.Name) && !IsAccountNumberName(accountColumn.Name);
        return ResolveBankUserAccountValue(request.Bank, request.BankUser, preferCard);
    }

    private static string ResolveBankUserAccountValue(Bank bank, BankUser user, bool preferCard)
    {
        return preferCard
            ? FirstNonEmpty(user.CardNo, FindBankUserColumnValue(bank, user, IsCardNumberName), user.AccountNo, FindBankUserColumnValue(bank, user, IsAccountNumberName))
            : FirstNonEmpty(user.AccountNo, FindBankUserColumnValue(bank, user, IsAccountNumberName), user.CardNo, FindBankUserColumnValue(bank, user, IsCardNumberName));
    }

    private static string FindBankUserColumnValue(Bank bank, BankUser user, Func<string?, bool> predicate)
    {
        foreach (var column in bank.Columns.Where(item => predicate(item.Name) && !string.IsNullOrWhiteSpace(item.Field)))
        {
            var value = GetBankUserStringValue(user, column.Field!);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string GetBankUserStringValue(BankUser user, string field)
    {
        if (TryGetIndexerField(field, out var indexerField))
        {
            return user[indexerField];
        }

        var property = typeof(BankUser).GetProperty(field, BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
        {
            return user[field];
        }

        return property.GetValue(user) switch
        {
            null => string.Empty,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var value => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
        };
    }

    private static bool IsCardNumberName(string? name)
    {
        var value = NormalizeName(name ?? string.Empty);
        return value is "\u5361\u53f7" or "\u501f\u8bb0\u5361\u53f7" or "\u6253\u5370\u5361\u53f7" or "\u4e3b\u5361\u5361\u53f7"
            || (value.Contains("\u5361\u53f7", StringComparison.Ordinal) && !value.Contains("\u8d26\u53f7", StringComparison.Ordinal) && !value.Contains("\u5e10\u53f7", StringComparison.Ordinal));
    }

    private static bool IsAccountNumberName(string? name)
    {
        var value = NormalizeName(name ?? string.Empty);
        return value is "\u8d26\u53f7" or "\u5e10\u53f7" or "\u8d26\u53f7\u5361\u53f7" or "\u5361\u53f7\u8d26\u6237"
            or "\u5ba2\u6237\u8d26\u53f7" or "\u6237\u53e3\u53f7" or "\u8d26\u6237\u8d26\u53f7" or "\u8d26\u6237\u53f7"
            or "\u5ba2\u6237\u8d26\u53e3" or "\u5ba2\u6237\u6237\u53e3" or "\u652f\u4ed8\u5b9d\u8d26\u6237" or "\u5fae\u4fe1\u53f7";
    }

    private static void NormalizeIncomeTotal(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        MonthlyAmountPlan monthlyPlan)
    {
        var targetIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney));
        NormalizeSignedRecordsByMonth(records, request, start, end, random, targetIncome, true, "鏀跺叆琛ヨ冻", monthlyPlan.Months, monthlyPlan.IncomeTargets);

    }

    private static void NormalizeExpenseTotal(
        List<FlowRecord> records,
        double targetExpense,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        MonthlyAmountPlan monthlyPlan)
    {
        targetExpense = RoundMoney(Math.Max(0, targetExpense));
        NormalizeSignedRecordsByMonth(records, request, start, end, random, targetExpense, false, "支出补足", monthlyPlan.Months, monthlyPlan.ExpenseTargets);

    }

    private static void NormalizeSignedRecordsByMonth(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        double targetTotal,
        bool isIncome,
        string balancingBrief,
        IReadOnlyList<(DateTime Start, DateTime End)> plannedMonths,
        IReadOnlyList<double> plannedTargets)
    {
        targetTotal = RoundMoney(Math.Max(0, targetTotal));
        var fixedSystemTotal = SumSignedSystemRecords(records, isIncome);
        var adjustableTargetTotal = RoundMoney(Math.Max(0, targetTotal - fixedSystemTotal));
        var signedRecords = GetSignedRecords(records, isIncome);
        if (adjustableTargetTotal <= 0)
        {
            RebalanceSignedRecords(signedRecords, 0);
            return;
        }

        var hasPlannedTargets = plannedMonths.Count == plannedTargets.Count
            && Math.Abs(RoundMoney(plannedTargets.Sum()) - targetTotal) <= 0.009d;
        var months = hasPlannedTargets
            ? plannedMonths
            : EnumerateMonths(start, end).ToList();
        if (months.Count == 0)
        {
            if (signedRecords.Count > 0)
            {
                RebalanceSignedRecords(signedRecords, adjustableTargetTotal);
            }

            return;
        }

        var monthlyTargets = hasPlannedTargets
            ? plannedTargets.ToArray()
            : CreateMonthlyTargets(request.Config, months, targetTotal, isIncome, random);
        for (var index = 0; index < months.Count; index++)
        {
            var month = months[index];
            var fixedMonthTotal = SumSignedSystemRecords(
                records.Where(item => IsRecordInRange(item, month.Start, month.End)),
                isIncome);
            var target = RoundMoney(Math.Max(0, monthlyTargets[index] - fixedMonthTotal));
            var monthRecords = GetSignedRecords(records, isIncome)
                .Where(item => IsRecordInRange(item, month.Start, month.End))
                .ToList();

            if (monthRecords.Count == 0)
            {
                continue;
            }

            var requiredMonthRecords = monthRecords
                .Where(IsRequiredGeneratedRecord)
                .ToList();
            if (requiredMonthRecords.Count > 0)
            {
                var requiredMonthTotal = RoundMoney(requiredMonthRecords.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
                if (target <= requiredMonthTotal + 0.009d)
                {
                    var optionalMonthRecords = monthRecords
                        .Where(item => !IsRequiredGeneratedRecord(item))
                        .ToList();
                    if (optionalMonthRecords.Count > 0)
                    {
                        RebalanceSignedRecords(optionalMonthRecords, 0);
                    }

                    continue;
                }
            }

            if (target <= 0)
            {
                RebalanceSignedRecords(monthRecords, 0);
                continue;
            }

            RebalanceSignedRecords(monthRecords, target);
        }

        CorrectSignedTotal(records, request, start, end, random, adjustableTargetTotal, isIncome, balancingBrief);
    }

    private static List<FlowRecord> GetSignedRecords(IEnumerable<FlowRecord> records, bool isIncome)
    {
        return records
            .Where(item => !IsSystemInterestRecord(item))
            .Where(item => isIncome ? item.TradeMoney > 0 : item.TradeMoney < 0)
            .ToList();
    }

    private static double SumSignedSystemRecords(IEnumerable<FlowRecord> records, bool isIncome)
    {
        return RoundMoney(records
            .Where(IsSystemInterestRecord)
            .Where(item => isIncome ? item.TradeMoney > 0 : item.TradeMoney < 0)
            .Sum(item => Math.Abs(item.TradeMoney ?? 0)));
    }

    private static bool IsSystemInterestRecord(FlowRecord record)
    {
        return record.ExtraFields.TryGetValue(SystemRowKindField, out var value)
            && (value == InterestRowKind || value == InterestTaxRowKind);
    }

    private static bool IsInterestTaxRecord(FlowRecord record)
    {
        return record.ExtraFields.TryGetValue(SystemRowKindField, out var value)
            && value == InterestTaxRowKind;
    }

    private static bool IsInterestRecord(FlowRecord record)
    {
        return record.ExtraFields.TryGetValue(SystemRowKindField, out var value)
            && value == InterestRowKind;
    }

    private static void PruneZeroAmountRecords(List<FlowRecord> records)
    {
        records.RemoveAll(item => !IsSystemInterestRecord(item)
            && Math.Abs(RoundMoney(item.TradeMoney ?? 0)) <= 0.009d);
    }

    private static bool HasPairedInterestRecord(IEnumerable<FlowRecord> records, FlowRecord taxRecord)
    {
        if (!taxRecord.AccountTime.HasValue)
        {
            return false;
        }

        return records.Any(item =>
            !ReferenceEquals(item, taxRecord)
            && IsInterestRecord(item)
            && item.AccountTime.HasValue
            && item.AccountTime.Value == taxRecord.AccountTime.Value);
    }

    private static bool IsRecordInRange(FlowRecord record, DateTime start, DateTime end)
    {
        var accountTime = record.AccountTime;
        return accountTime.HasValue && accountTime.Value >= start && accountTime.Value <= end;
    }

    private static void AmplifyMonthlyBalanceSwing(
        double[] incomeTargets,
        double[] expenseTargets,
        Random random)
    {
        var monthCount = Math.Min(incomeTargets.Length, expenseTargets.Length);
        if (monthCount < 3
            || incomeTargets.Sum() <= 0.009d
            || expenseTargets.Sum() <= 0.009d)
        {
            return;
        }

        var period = monthCount <= 6
            ? monthCount
            : random.Next(4, Math.Min(8, monthCount) + 1);
        var phase = random.NextDouble() * period;
        var waveOrder = Enumerable.Range(0, monthCount)
            .Select(index =>
            {
                var wave = Math.Sin(((index + phase) / period) * Math.PI * 2d);
                var noise = (random.NextDouble() - 0.5d) * 0.22d;
                return new { Index = index, Score = wave + noise };
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(_ => random.Next())
            .Select(item => item.Index)
            .ToList();

        var incomeValues = incomeTargets.Take(monthCount)
            .OrderByDescending(item => item)
            .ToArray();
        var expenseValues = expenseTargets.Take(monthCount)
            .OrderByDescending(item => item)
            .ToArray();

        var reorderedIncome = incomeTargets.ToArray();
        var reorderedExpense = expenseTargets.ToArray();
        for (var rank = 0; rank < waveOrder.Count; rank++)
        {
            var monthIndex = waveOrder[rank];
            reorderedIncome[monthIndex] = incomeValues[rank];
            reorderedExpense[monthIndex] = expenseValues[rank];
        }

        for (var index = 0; index < monthCount; index++)
        {
            incomeTargets[index] = reorderedIncome[index];
            expenseTargets[index] = reorderedExpense[index];
        }
    }

    private static void PreventFirstMonthIncomeSpike(
        double[] incomeTargets,
        double targetIncome,
        Random random)
    {
        var monthCount = incomeTargets.Length;
        if (monthCount < 4 || targetIncome <= 0.009d)
        {
            return;
        }

        var total = RoundMoney(incomeTargets.Sum());
        if (total <= 0.009d || incomeTargets[0] <= 0.009d)
        {
            return;
        }

        var average = total / monthCount;
        var otherMax = incomeTargets
            .Skip(1)
            .DefaultIfEmpty(0)
            .Max();
        var first = incomeTargets[0];
        var isObviousFirstPeak = first > average * 1.75d + 0.009d
            && first > otherMax * 1.45d + 0.009d;
        if (!isObviousFirstPeak)
        {
            return;
        }

        var absoluteShareCap = monthCount <= 6 ? 0.34d : 0.26d;
        var softCap = Math.Max(average * (1.20d + random.NextDouble() * 0.30d), otherMax * (1.08d + random.NextDouble() * 0.18d));
        softCap = Math.Min(softCap, total * absoluteShareCap);
        softCap = RoundMoney(Math.Max(average * 0.88d, softCap));
        var excess = RoundMoney(first - softCap);
        if (excess <= 0.009d)
        {
            return;
        }

        incomeTargets[0] = RoundMoney(first - excess);
        var receiverIndexes = Enumerable.Range(1, monthCount - 1)
            .OrderBy(index => incomeTargets[index])
            .ThenBy(_ => random.Next())
            .ToList();
        foreach (var index in receiverIndexes)
        {
            if (excess <= 0.009d)
            {
                break;
            }

            var localCap = Math.Max(average * (1.35d + random.NextDouble() * 0.55d), incomeTargets[index] + average * 0.35d);
            var room = RoundMoney(Math.Max(0, localCap - incomeTargets[index]));
            if (room <= 0.009d)
            {
                continue;
            }

            var add = RoundMoney(Math.Min(room, excess));
            incomeTargets[index] = RoundMoney(incomeTargets[index] + add);
            excess = RoundMoney(excess - add);
        }

        if (excess > 0.009d)
        {
            var index = receiverIndexes
                .OrderBy(item => incomeTargets[item])
                .ThenBy(_ => random.Next())
                .FirstOrDefault(1);
            incomeTargets[index] = RoundMoney(incomeTargets[index] + excess);
        }

        NormalizeMonthlyTargetsInPlace(incomeTargets, targetIncome);
    }

    private static void BalanceMonthlyIncomeAcrossTimeline(
        double[] incomeTargets,
        double targetIncome,
        Random random)
    {
        if (incomeTargets.Length < 6 || targetIncome <= 0.009d)
        {
            return;
        }

        var total = RoundMoney(incomeTargets.Sum());
        if (total <= 0.009d)
        {
            return;
        }

        var splitIndex = incomeTargets.Length / 2;
        var firstHalfTotal = RoundMoney(incomeTargets.Take(splitIndex).Sum());
        var firstHalfShare = firstHalfTotal / total;
        if (firstHalfShare is >= 0.36d and <= 0.64d)
        {
            return;
        }

        var desiredFirstHalfShare = 0.44d + (random.NextDouble() * 0.12d);
        var desiredFirstHalfTotal = RoundMoney(total * desiredFirstHalfShare);
        var moveToFirstHalf = firstHalfTotal < desiredFirstHalfTotal;
        var transfer = RoundMoney(Math.Abs(desiredFirstHalfTotal - firstHalfTotal));
        if (transfer <= 0.009d)
        {
            return;
        }

        var firstHalfIndexes = Enumerable.Range(0, splitIndex).ToArray();
        var secondHalfIndexes = Enumerable.Range(splitIndex, incomeTargets.Length - splitIndex).ToArray();
        var donorIndexes = (moveToFirstHalf ? secondHalfIndexes : firstHalfIndexes)
            .OrderByDescending(index => incomeTargets[index])
            .ThenBy(_ => random.Next())
            .ToList();
        var receiverIndexes = (moveToFirstHalf ? firstHalfIndexes : secondHalfIndexes)
            .OrderBy(index => incomeTargets[index])
            .ThenBy(_ => random.Next())
            .ToList();
        var floor = Math.Max(0, (total / incomeTargets.Length) * 0.03d);
        var removed = 0d;
        foreach (var index in donorIndexes)
        {
            var room = RoundMoney(Math.Max(0, incomeTargets[index] - floor));
            var amount = RoundMoney(Math.Min(room, transfer - removed));
            if (amount <= 0.009d)
            {
                continue;
            }

            incomeTargets[index] = RoundMoney(incomeTargets[index] - amount);
            removed = RoundMoney(removed + amount);
            if (removed >= transfer - 0.009d)
            {
                break;
            }
        }

        if (removed <= 0.009d || receiverIndexes.Count == 0)
        {
            return;
        }

        var remaining = removed;
        for (var index = 0; index < receiverIndexes.Count; index++)
        {
            var slotsLeft = receiverIndexes.Count - index;
            var amount = index == receiverIndexes.Count - 1
                ? remaining
                : RoundMoney(remaining / Math.Max(1, slotsLeft));
            incomeTargets[receiverIndexes[index]] = RoundMoney(incomeTargets[receiverIndexes[index]] + amount);
            remaining = RoundMoney(remaining - amount);
        }

        NormalizeMonthlyTargetsInPlace(incomeTargets, targetIncome);
    }

    private static void NormalizeMonthlyTargetsInPlace(double[] targets, double targetTotal)
    {
        if (targets.Length == 0)
        {
            return;
        }

        var normalized = NormalizeRawTargets(targets, targetTotal);
        Array.Copy(normalized, targets, targets.Length);
    }

    private static void SpreadMonthlyExpenseTargets(
        double[] expenseTargets,
        IReadOnlyList<double> incomeTargets,
        double targetExpense,
        IReadOnlyList<GenerateReferenceRule> expenseRules)
    {
        var monthCount = expenseTargets.Length;
        targetExpense = RoundMoney(Math.Max(0, targetExpense));
        if (monthCount < 3 || targetExpense <= 0.009d)
        {
            return;
        }

        var average = targetExpense / monthCount;
        var incomeTotal = incomeTargets.Sum(item => Math.Max(0, item));
        var spendRatio = incomeTotal > 0.009d
            ? Math.Clamp(targetExpense / incomeTotal, 0.08d, 1.25d)
            : 1d;
        var smallestRuleAmount = expenseRules
            .Select(rule => GetRuleAmountBounds(rule).Min)
            .Where(value => value > 0.009d)
            .DefaultIfEmpty(0)
            .Min();
        var floor = Math.Max(average * 0.38d, smallestRuleAmount > 0 ? Math.Min(smallestRuleAmount, average * 0.55d) : 0);
        if (floor * monthCount > targetExpense * 0.92d)
        {
            floor = average * 0.22d;
        }

        var incomeAverage = incomeTargets.Count > 0
            ? Math.Max(0.01d, incomeTargets.Sum() / incomeTargets.Count)
            : average;
        var floors = new double[monthCount];
        var caps = new double[monthCount];
        var values = new double[monthCount];
        for (var index = 0; index < monthCount; index++)
        {
            var incomeRatio = incomeTargets.Count > index
                ? Math.Clamp(incomeTargets[index] / incomeAverage, 0.35d, 2.2d)
                : 1d;
            floors[index] = RoundMoney(floor);
            caps[index] = RoundMoney(Math.Max(floors[index], average * (1.28d + Math.Min(0.62d, incomeRatio * 0.28d))));

            var previousIncome = index > 0 && incomeTargets.Count > index - 1 ? Math.Max(0, incomeTargets[index - 1]) : 0;
            var currentIncome = incomeTargets.Count > index ? Math.Max(0, incomeTargets[index]) : incomeAverage;
            var nextIncome = index + 1 < incomeTargets.Count ? Math.Max(0, incomeTargets[index + 1]) : 0;
            var localIncome = (previousIncome * 0.18d) + (currentIncome * 0.64d) + (nextIncome * 0.18d);
            var followIncomeTarget = localIncome * spendRatio;
            var blendedTarget = (followIncomeTarget * 0.72d) + (average * 0.22d) + (expenseTargets[index] * 0.06d);
            values[index] = RoundMoney(Math.Clamp(blendedTarget, floors[index], caps[index]));
        }

        var capTotal = caps.Sum();
        if (capTotal < targetExpense - 0.009d && capTotal > 0.009d)
        {
            var capScale = (targetExpense / capTotal) * 1.02d;
            for (var index = 0; index < monthCount; index++)
            {
                caps[index] = RoundMoney(Math.Max(floors[index], caps[index] * capScale));
            }
        }

        RebalanceMonthlyTargetsWithinBounds(values, targetExpense, floors, caps);
        PullExpenseTargetsForwardByIncomeCurve(values, incomeTargets, targetExpense, floors, caps);
        for (var index = 0; index < monthCount; index++)
        {
            expenseTargets[index] = values[index];
        }
    }

    private static void PullExpenseTargetsForwardByIncomeCurve(
        double[] values,
        IReadOnlyList<double> incomeTargets,
        double targetExpense,
        IReadOnlyList<double> floors,
        IReadOnlyList<double> caps)
    {
        var monthCount = values.Length;
        var incomeTotal = incomeTargets.Sum(item => Math.Max(0, item));
        if (monthCount < 3 || incomeTotal <= 0.009d || targetExpense <= 0.009d)
        {
            return;
        }

        var average = targetExpense / monthCount;
        var allowedLag = Math.Max(average * 0.35d, targetExpense * 0.012d);
        var cumulativeIncome = 0d;
        var cumulativeExpense = 0d;
        for (var index = 0; index < monthCount - 1; index++)
        {
            cumulativeIncome = RoundMoney(cumulativeIncome + Math.Max(0, incomeTargets.Count > index ? incomeTargets[index] : 0));
            cumulativeExpense = RoundMoney(cumulativeExpense + values[index]);
            var expectedExpense = RoundMoney(targetExpense * (cumulativeIncome / incomeTotal));
            var deficit = RoundMoney(expectedExpense - allowedLag - cumulativeExpense);
            if (deficit <= 0.009d)
            {
                continue;
            }

            var addRoom = RoundMoney(caps[index] - values[index]);
            if (addRoom <= 0.009d)
            {
                continue;
            }

            var needed = Math.Min(deficit, addRoom);
            var moved = 0d;
            for (var futureIndex = monthCount - 1; futureIndex > index && needed > 0.009d; futureIndex--)
            {
                var reducible = RoundMoney(values[futureIndex] - floors[futureIndex]);
                if (reducible <= 0.009d)
                {
                    continue;
                }

                var take = Math.Min(reducible, needed);
                values[futureIndex] = RoundMoney(values[futureIndex] - take);
                needed = RoundMoney(needed - take);
                moved = RoundMoney(moved + take);
            }

            if (moved <= 0.009d)
            {
                continue;
            }

            values[index] = RoundMoney(values[index] + moved);
            cumulativeExpense = RoundMoney(cumulativeExpense + moved);
        }
    }

    private static void RebalanceMonthlyTargetsWithinBounds(
        double[] values,
        double targetTotal,
        IReadOnlyList<double> floors,
        IReadOnlyList<double> caps)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var diff = RoundMoney(targetTotal - values.Sum());
            if (Math.Abs(diff) <= 0.009d)
            {
                return;
            }

            var progressed = false;
            var indexes = Enumerable.Range(0, values.Length)
                .OrderBy(index => diff > 0 ? values[index] : -values[index])
                .ToList();
            foreach (var index in indexes)
            {
                if (Math.Abs(diff) <= 0.009d)
                {
                    break;
                }

                if (diff > 0)
                {
                    var room = RoundMoney(caps[index] - values[index]);
                    if (room <= 0.009d)
                    {
                        continue;
                    }

                    var add = Math.Min(room, diff);
                    values[index] = RoundMoney(values[index] + add);
                    diff = RoundMoney(diff - add);
                    progressed = true;
                }
                else
                {
                    var room = RoundMoney(values[index] - floors[index]);
                    if (room <= 0.009d)
                    {
                        continue;
                    }

                    var reduce = Math.Min(room, Math.Abs(diff));
                    values[index] = RoundMoney(values[index] - reduce);
                    diff = RoundMoney(diff + reduce);
                    progressed = true;
                }
            }

            if (!progressed)
            {
                var normalized = NormalizeRawTargets(values, targetTotal);
                Array.Copy(normalized, values, values.Length);
                return;
            }
        }
    }

    private static double[] CreateMonthlyTargets(
        FlowGenerationConfig config,
        IReadOnlyList<(DateTime Start, DateTime End)> months,
        double targetTotal,
        bool isIncome,
        Random random,
        IReadOnlyList<GenerateReferenceRule>? rules = null)
    {
        targetTotal = RoundMoney(Math.Max(0, targetTotal));
        if (months.Count == 0 || targetTotal <= 0)
        {
            return new double[months.Count];
        }

        var rawTargets = config.SelectIndex switch
        {
            2 => CreateMonthDetailRawTargets(config, months, isIncome),
            1 => CreateRangeRawTargets(config, months.Count, targetTotal, isIncome, true, random, rules),
            _ => CreateRangeRawTargets(config, months.Count, targetTotal, isIncome, false, random, rules)
        };

        if (rawTargets.Sum() <= 0.009d)
        {
            rawTargets = CreateDefaultVolatileRawTargets(months.Count, targetTotal, isIncome, random, rules);
        }

        var targets = NormalizeRawTargets(rawTargets, targetTotal);
        return ApplyLargeIncomeMonthlyFloor(targets, targetTotal, isIncome, rules);
    }

    private static double[] ApplyLargeIncomeMonthlyFloor(
        double[] targets,
        double targetTotal,
        bool isIncome,
        IReadOnlyList<GenerateReferenceRule>? rules)
    {
        if (!isIncome || targets.Length == 0 || rules is null || rules.Count == 0)
        {
            return targets;
        }

        var smallest = rules.Select(item => GetRuleAmountBounds(item).Min).DefaultIfEmpty(0).Min();
        var largest = rules.Select(item => GetRuleAmountBounds(item).Max).DefaultIfEmpty(0).Max();
        if (smallest < 1000d || largest <= smallest * 4d || targetTotal < smallest * targets.Length)
        {
            return targets;
        }

        var result = targets.ToArray();
        var deficit = 0d;
        for (var index = 0; index < result.Length; index++)
        {
            if (result[index] >= smallest - 0.009d)
            {
                continue;
            }

            deficit = RoundMoney(deficit + smallest - result[index]);
            result[index] = smallest;
        }

        if (deficit <= 0.009d)
        {
            return result;
        }

        foreach (var index in result
                     .Select((value, itemIndex) => new { value, itemIndex })
                     .Where(item => item.value > smallest + 0.009d)
                     .OrderByDescending(item => item.value)
                     .Select(item => item.itemIndex))
        {
            var room = RoundMoney(result[index] - smallest);
            var reduction = Math.Min(room, deficit);
            result[index] = RoundMoney(result[index] - reduction);
            deficit = RoundMoney(deficit - reduction);
            if (deficit <= 0.009d)
            {
                break;
            }
        }

        return deficit <= 0.009d ? result : targets;
    }

    private static double[] CreateMonthDetailRawTargets(
        FlowGenerationConfig config,
        IReadOnlyList<(DateTime Start, DateTime End)> months,
        bool isIncome)
    {
        var rawTargets = new double[months.Count];
        foreach (var detail in config.MonthGenData)
        {
            var detailStart = detail.StartTime.Date;
            var detailEnd = NormalizeEndDate(detail.EndTime);
            var amount = Math.Max(0, isIncome ? detail.InMoney : detail.OutMoney);
            if (amount <= 0)
            {
                continue;
            }

            for (var index = 0; index < months.Count; index++)
            {
                var month = months[index];
                if (month.End >= detailStart && month.Start <= detailEnd)
                {
                    rawTargets[index] = RoundMoney(rawTargets[index] + amount);
                }
            }
        }

        return rawTargets;
    }

    private static double[] CreateRangeRawTargets(
        FlowGenerationConfig config,
        int monthCount,
        double targetTotal,
        bool isIncome,
        bool useSecondMonthlyRange,
        Random random,
        IReadOnlyList<GenerateReferenceRule>? rules)
    {
        var average = targetTotal / monthCount;
        var min = isIncome
            ? useSecondMonthlyRange ? config.MinInMoneyMonth2 : config.MinInMoneyMonth1
            : useSecondMonthlyRange ? config.MinOutMoneyMonth2 : config.MinOutMoneyMonth1;
        var max = isIncome
            ? useSecondMonthlyRange ? config.MaxInMoneyMonth2 : config.MaxInMoneyMonth1
            : useSecondMonthlyRange ? config.MaxOutMoneyMonth2 : config.MaxOutMoneyMonth1;

        if (min <= 0 && max <= 0)
        {
            return CreateDefaultVolatileRawTargets(monthCount, targetTotal, isIncome, random, rules);
        }

        if (max < min)
        {
            (min, max) = (max, min);
        }

        if (!useSecondMonthlyRange && Math.Abs(max - min) <= 0.009d)
        {
            min = Math.Max(0.01d, average * 0.08d);
            max = Math.Max(min, average * 3.2d);
        }

        return CreateSpikyRawTargets(monthCount, targetTotal, min, max, isIncome, random, rules);
    }

    private static double[] CreateDefaultVolatileRawTargets(
        int monthCount,
        double targetTotal,
        bool isIncome,
        Random random,
        IReadOnlyList<GenerateReferenceRule>? rules = null)
    {
        var average = targetTotal / monthCount;
        var min = Math.Max(0.01d, average * 0.05d);
        var max = Math.Max(min, average * 3.8d);
        return CreateSpikyRawTargets(monthCount, targetTotal, min, max, isIncome, random, rules);
    }

    private static double[] CreateSpikyRawTargets(
        int monthCount,
        double targetTotal,
        double min,
        double max,
        bool isIncome,
        Random random,
        IReadOnlyList<GenerateReferenceRule>? rules = null)
    {
        if (monthCount <= 0)
        {
            return [];
        }

        min = Math.Max(0.01d, min);
        max = Math.Max(min, max);
        var targets = Enumerable.Range(0, monthCount)
            .Select(_ => CreateVolatileAmount(min, max, random) * CreateMonthlySpikeFactor(isIncome, random))
            .ToArray();

        if (monthCount >= 4)
        {
            foreach (var index in PickDistinctIndexes(monthCount, Math.Max(1, monthCount / 5), random))
            {
                targets[index] *= isIncome
                    ? 3.25d + random.NextDouble() * 2.85d
                    : 1.65d + random.NextDouble() * 1.25d;
            }

            foreach (var index in PickDistinctIndexes(monthCount, Math.Max(1, monthCount / 4), random))
            {
                targets[index] *= isIncome
                    ? 0.01d + random.NextDouble() * 0.14d
                    : 0.35d + random.NextDouble() * 0.45d;
            }
        }

        ApplySparseActivityProfile(targets, targetTotal, isIncome, rules, random);

        if (targets.Sum() <= 0.009d)
        {
            var average = targetTotal / monthCount;
            Array.Fill(targets, average);
        }

        return targets;
    }

    private static void ApplySparseActivityProfile(
        double[] targets,
        double targetTotal,
        bool isIncome,
        IReadOnlyList<GenerateReferenceRule>? rules,
        Random random)
    {
        var activeMonthCount = CalculateActiveMonthCount(targets.Length, targetTotal, isIncome, rules);
        if (activeMonthCount >= targets.Length)
        {
            return;
        }

        var activeIndexes = PickDistinctIndexes(targets.Length, activeMonthCount, random)
            .ToHashSet();
        var activeFloor = rules is { Count: > 0 }
            ? EstimateWeightedTypicalMinimum(rules)
            : 0;
        for (var index = 0; index < targets.Length; index++)
        {
            if (!activeIndexes.Contains(index))
            {
                targets[index] = 0;
            }
            else if (activeFloor > 0)
            {
                targets[index] = Math.Max(targets[index], activeFloor);
            }
        }
    }

    private static int CalculateActiveMonthCount(
        int monthCount,
        double targetTotal,
        bool isIncome,
        IReadOnlyList<GenerateReferenceRule>? rules)
    {
        if (monthCount <= 1 || targetTotal <= 0.009d || rules is null || rules.Count == 0)
        {
            return monthCount;
        }

        if (!isIncome)
        {
            return monthCount;
        }

        var typicalMinimum = EstimateWeightedTypicalMinimum(rules);
        if (typicalMinimum <= 0)
        {
            return monthCount;
        }

        var averageMonthly = targetTotal / monthCount;
        var lumpyThreshold = isIncome ? 1.35d : 1.65d;
        if (typicalMinimum <= averageMonthly * lumpyThreshold)
        {
            return monthCount;
        }

        var activeCount = Math.Max(1, (int)Math.Floor(targetTotal / typicalMinimum));

        var maxActiveRatio = isIncome ? 0.72d : 0.84d;
        var maxActiveCount = Math.Max(1, (int)Math.Ceiling(monthCount * maxActiveRatio));
        activeCount = Math.Min(activeCount, maxActiveCount);

        return Math.Clamp(activeCount, 1, monthCount);
    }

    private static double EstimateWeightedTypicalMinimum(IReadOnlyList<GenerateReferenceRule> rules)
    {
        var weightedValues = rules
            .Select(item => new
            {
                Value = GetRuleAmountBounds(item).Min,
                Weight = Math.Max(1, item.PercentMonth ?? 1)
            })
            .OrderBy(item => item.Value)
            .ToList();
        if (weightedValues.Count == 0)
        {
            return 0;
        }

        var totalWeight = weightedValues.Sum(item => item.Weight);
        var midpoint = totalWeight * 0.6d;
        var cursor = 0d;
        foreach (var item in weightedValues)
        {
            cursor += item.Weight;
            if (cursor >= midpoint)
            {
                return item.Value;
            }
        }

        return weightedValues[^1].Value;
    }

    private static IEnumerable<int> PickDistinctIndexes(int count, int take, Random random)
    {
        return Enumerable.Range(0, count)
            .OrderBy(_ => random.Next())
            .Take(Math.Min(count, take));
    }

    private static double CreateVolatileAmount(double min, double max, Random random)
    {
        if (max <= min)
        {
            return Math.Max(0, min);
        }

        var ratio = random.NextDouble();
        if (random.NextDouble() < 0.28d)
        {
            ratio = 1d - Math.Pow(random.NextDouble(), 2d);
        }
        else
        {
            ratio = Math.Pow(ratio, 0.72d);
        }

        return min + ((max - min) * ratio);
    }

    private static double CreateMonthlySpikeFactor(bool isIncome, Random random)
    {
        var ratio = random.NextDouble();
        if (!isIncome)
        {
            if (ratio < 0.12d)
            {
                return 0.35d + random.NextDouble() * 0.35d;
            }

            if (ratio < 0.86d)
            {
                return 0.75d + random.NextDouble() * 0.85d;
            }

            return 1.65d + random.NextDouble() * 1.1d;
        }

        if (ratio < 0.28d)
        {
            return 0.015d + random.NextDouble() * 0.18d;
        }

        if (ratio < 0.78d)
        {
            return 0.35d + random.NextDouble() * 1.1d;
        }

        return 2.9d + random.NextDouble() * 3.4d;
    }

    private static double[] NormalizeRawTargets(IReadOnlyList<double> rawTargets, double targetTotal)
    {
        var targets = new double[rawTargets.Count];
        if (rawTargets.Count == 0)
        {
            return targets;
        }

        var rawTotal = rawTargets.Sum(item => Math.Max(0, item));
        if (rawTotal <= 0)
        {
            rawTotal = rawTargets.Count;
            rawTargets = Enumerable.Repeat(1d, rawTargets.Count).ToArray();
        }

        var runningTotal = 0d;
        for (var index = 0; index < rawTargets.Count; index++)
        {
            var target = index == rawTargets.Count - 1
                ? RoundMoney(targetTotal - runningTotal)
                : RoundMoney(targetTotal * (Math.Max(0, rawTargets[index]) / rawTotal));

            if (target < 0)
            {
                target = 0;
            }

            targets[index] = target;
            runningTotal = RoundMoney(runningTotal + target);
        }

        var diff = RoundMoney(targetTotal - targets.Sum());
        if (Math.Abs(diff) > 0.009d)
        {
            var targetIndex = targets
                .Select((value, index) => new { value, index })
                .OrderByDescending(item => item.value)
                .First().index;
            targets[targetIndex] = RoundMoney(Math.Max(0, targets[targetIndex] + diff));
        }

        return targets;
    }

    private static void CorrectSignedTotal(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        double targetTotal,
        bool isIncome,
        string balancingBrief)
    {
        var signedRecords = GetSignedRecords(records, isIncome)
            .OrderByDescending(item => item.AccountTime ?? DateTime.MinValue)
            .ToList();
        var currentTotal = RoundMoney(signedRecords.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
        var diff = RoundMoney(targetTotal - currentTotal);
        if (Math.Abs(diff) <= 0.009d)
        {
            return;
        }

        if (signedRecords.Count == 0)
        {
            return;
        }

        RebalanceSignedRecords(signedRecords, targetTotal);
    }

    private static void RebalanceSignedRecords(IReadOnlyList<FlowRecord> signedRecords, double targetTotal)
    {
        if (signedRecords.Count == 0)
        {
            return;
        }

        targetTotal = RoundMoney(Math.Max(0, targetTotal));
        var sign = signedRecords.Any(item => item.TradeMoney < 0) ? -1d : 1d;
        var amounts = new double[signedRecords.Count];
        var mins = new double[signedRecords.Count];
        var maxs = new double[signedRecords.Count];
        var hardMaxs = new double[signedRecords.Count];
        var units = new double[signedRecords.Count];
        var activeIndexes = Enumerable.Range(0, signedRecords.Count).ToList();
        var preserveLowCapacityRecords = signedRecords.Any(item => !IsLowCapacityReferenceRecord(item));

        for (var index = 0; index < signedRecords.Count; index++)
        {
            var record = signedRecords[index];
            var bounds = GetRecordAmountBounds(record);
            var currentAmount = ClampAmountToBounds(Math.Abs(record.TradeMoney ?? 0), bounds);
            units[index] = bounds.Unit;
            if (preserveLowCapacityRecords && IsLowCapacityReferenceRecord(record))
            {
                mins[index] = currentAmount;
                hardMaxs[index] = currentAmount;
                maxs[index] = currentAmount;
            }
            else
            {
                mins[index] = bounds.Min;
                hardMaxs[index] = Math.Max(bounds.Min, bounds.Max);
                maxs[index] = GetPreferredAmountUpperBound(record, bounds);
            }

            amounts[index] = currentAmount;
        }

        RemoveOptionalRecordsForTarget(signedRecords, amounts, mins, maxs, activeIndexes, targetTotal);
        ExpandPreferredMaximumsForTarget(signedRecords, maxs, hardMaxs, units, activeIndexes, targetTotal);

        if (activeIndexes.Count == 0)
        {
            foreach (var record in signedRecords)
            {
                ApplySignedAmount(record, sign, 0);
            }

            return;
        }

        var minTotal = RoundMoney(activeIndexes.Sum(index => mins[index]));
        var maxTotal = RoundMoney(activeIndexes.Sum(index => maxs[index]));
        var boundedTarget = Math.Clamp(targetTotal, minTotal, maxTotal);
        foreach (var index in activeIndexes.ToList())
        {
            amounts[index] = ClampAmountToBounds(amounts[index], new AmountBounds(mins[index], maxs[index], units[index]));
        }

        var currentTotal = RoundMoney(activeIndexes.Sum(index => amounts[index]));
        if (currentTotal > boundedTarget + 0.009d)
        {
            MoveAmountsTowardTarget(signedRecords, amounts, mins, maxs, units, activeIndexes, boundedTarget, decrease: true);
        }
        else if (currentTotal < boundedTarget - 0.009d)
        {
            MoveAmountsTowardTarget(signedRecords, amounts, mins, maxs, units, activeIndexes, boundedTarget, decrease: false);
        }

        AdjustRoundedAmountsToTarget(signedRecords, amounts, boundedTarget, mins, maxs, activeIndexes);

        for (var index = 0; index < signedRecords.Count; index++)
        {
            ApplySignedAmount(signedRecords[index], sign, amounts[index]);
        }
    }

    private static void ExpandPreferredMaximumsForTarget(
        IReadOnlyList<FlowRecord> signedRecords,
        double[] maxs,
        IReadOnlyList<double> hardMaxs,
        IReadOnlyList<double> units,
        IReadOnlyList<int> activeIndexes,
        double targetTotal)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var currentMaxTotal = RoundMoney(activeIndexes.Sum(index => maxs[index]));
            var remaining = RoundMoney(targetTotal - currentMaxTotal);
            if (remaining <= 0.009d)
            {
                return;
            }

            var candidates = activeIndexes
                .Where(index => hardMaxs[index] > maxs[index] + 0.009d)
                .OrderBy(index => IsRequiredGeneratedRecord(signedRecords[index]) ? 1 : 0)
                .ThenBy(index => IsSmallDiscreteAmountRecord(signedRecords[index]) ? 1 : 0)
                .ThenByDescending(index => RoundMoney(hardMaxs[index] - maxs[index]))
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var capacity = RoundMoney(candidates.Sum(index => hardMaxs[index] - maxs[index]));
            if (capacity <= 0.009d)
            {
                return;
            }

            var progressed = false;
            foreach (var index in candidates)
            {
                if (remaining <= 0.009d)
                {
                    break;
                }

                var unit = Math.Max(0.01d, units[index]);
                var room = RoundMoney(hardMaxs[index] - maxs[index]);
                if (room <= 0.009d || unit > remaining + 0.009d)
                {
                    continue;
                }

                var share = remaining * (room / capacity);
                var add = RoundAmountToUnit(Math.Min(room, share), unit);
                if (add > remaining + 0.009d)
                {
                    add = FloorAmountToUnit(remaining, unit);
                }

                if (add <= 0.009d)
                {
                    add = Math.Min(unit, room);
                }

                if (add <= 0.009d || add > remaining + 0.009d)
                {
                    continue;
                }

                maxs[index] = RoundAmountToUnit(Math.Min(hardMaxs[index], maxs[index] + add), unit);
                remaining = RoundMoney(targetTotal - activeIndexes.Sum(item => maxs[item]));
                progressed = true;
            }

            if (!progressed)
            {
                return;
            }
        }
    }

    private static void RemoveOptionalRecordsForTarget(
        IReadOnlyList<FlowRecord> signedRecords,
        double[] amounts,
        IReadOnlyList<double> mins,
        IReadOnlyList<double> maxs,
        List<int> activeIndexes,
        double targetTotal)
    {
        while (activeIndexes.Count > 0 && activeIndexes.Sum(index => mins[index]) > targetTotal + 0.009d)
        {
            var removableIndex = activeIndexes
                .Where(index => !IsRequiredGeneratedRecord(signedRecords[index]))
                .Where(index => CanRemoveOptionalRecordForTarget(signedRecords, activeIndexes, index))
                .OrderByDescending(index => amounts[index])
                .ThenByDescending(index => mins[index])
                .FirstOrDefault(-1);
            if (removableIndex < 0)
            {
                break;
            }

            amounts[removableIndex] = 0;
            activeIndexes.Remove(removableIndex);
        }

        for (var attempt = 0; attempt < signedRecords.Count; attempt++)
        {
            var currentTotal = RoundMoney(activeIndexes.Sum(index => amounts[index]));
            var excess = RoundMoney(currentTotal - targetTotal);
            if (excess <= 0.009d)
            {
                return;
            }

            var removableIndex = activeIndexes
                .Where(index => !IsRequiredGeneratedRecord(signedRecords[index]))
                .Where(index => CanRemoveOptionalRecordForTarget(signedRecords, activeIndexes, index))
                .OrderByDescending(index => amounts[index])
                .ThenByDescending(index => mins[index])
                .Where(index =>
                {
                    var nextActiveIndexes = activeIndexes.Where(item => item != index).ToList();
                    if (nextActiveIndexes.Count == 0)
                    {
                        return true;
                    }

                    var nextMinTotal = RoundMoney(nextActiveIndexes.Sum(item => mins[item]));
                    var nextMaxTotal = RoundMoney(nextActiveIndexes.Sum(item => maxs[item]));
                    if (targetTotal < nextMinTotal - 0.009d || targetTotal > nextMaxTotal + 0.009d)
                    {
                        return false;
                    }

                    var amount = amounts[index];
                    var nextExcess = Math.Abs(RoundMoney(currentTotal - amount - targetTotal));
                    var currentExcess = Math.Abs(excess);
                    return amount <= excess * 1.15d
                        || excess > targetTotal * 0.25d
                        || nextExcess < currentExcess;
                })
                .DefaultIfEmpty(-1)
                .First();
            if (removableIndex < 0)
            {
                return;
            }

            amounts[removableIndex] = 0;
            activeIndexes.Remove(removableIndex);
        }
    }

    private static bool CanRemoveOptionalRecordForTarget(
        IReadOnlyList<FlowRecord> signedRecords,
        IReadOnlyList<int> activeIndexes,
        int candidateIndex)
    {
        var record = signedRecords[candidateIndex];
        if ((record.TradeMoney ?? 0) <= 0.009d || !record.AccountTime.HasValue)
        {
            return true;
        }

        var bounds = GetRecordAmountBounds(record);
        if (bounds.Min < 1000d || bounds.Max <= bounds.Min * 4d)
        {
            return true;
        }

        var monthKey = (Year: record.AccountTime.Value.Year, Month: record.AccountTime.Value.Month);
        var sameMonthIncomeCount = activeIndexes.Count(index =>
        {
            var item = signedRecords[index];
            return (item.TradeMoney ?? 0) > 0.009d
                && item.AccountTime.HasValue
                && item.AccountTime.Value.Year == monthKey.Year
                && item.AccountTime.Value.Month == monthKey.Month;
        });

        return sameMonthIncomeCount > 3;
    }

    private static void MoveAmountsTowardTarget(
        IReadOnlyList<FlowRecord> records,
        double[] amounts,
        IReadOnlyList<double> mins,
        IReadOnlyList<double> maxs,
        IReadOnlyList<double> units,
        IReadOnlyList<int> activeIndexes,
        double targetTotal,
        bool decrease)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var currentTotal = RoundMoney(activeIndexes.Sum(index => amounts[index]));
            var remaining = decrease
                ? RoundMoney(currentTotal - targetTotal)
                : RoundMoney(targetTotal - currentTotal);
            if (remaining <= 0.009d)
            {
                return;
            }

            var candidates = activeIndexes
                .Where(index => decrease
                    ? amounts[index] > mins[index] + 0.009d
                    : amounts[index] < maxs[index] - 0.009d)
                .OrderBy(index => IsRequiredGeneratedRecord(records[index]) ? 1 : 0)
                .ThenByDescending(index => decrease
                    ? RoundMoney(amounts[index] - mins[index])
                    : RoundMoney(maxs[index] - amounts[index]))
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var capacity = RoundMoney(candidates.Sum(index => decrease
                ? amounts[index] - mins[index]
                : maxs[index] - amounts[index]));
            if (capacity <= 0.009d)
            {
                return;
            }

            var progressed = false;
            foreach (var index in candidates)
            {
                if (remaining <= 0.009d)
                {
                    break;
                }

                var unit = Math.Max(0.01d, units[index]);
                var room = RoundMoney(decrease ? amounts[index] - mins[index] : maxs[index] - amounts[index]);
                if (room <= 0.009d || unit > remaining + 0.009d)
                {
                    continue;
                }

                var share = remaining * (room / capacity);
                var adjustment = RoundAmountToUnit(Math.Min(room, share), unit);
                if (adjustment > remaining + 0.009d)
                {
                    adjustment = FloorAmountToUnit(remaining, unit);
                }

                if (adjustment <= 0.009d)
                {
                    adjustment = Math.Min(unit, room);
                }

                if (adjustment <= 0.009d || adjustment > remaining + 0.009d)
                {
                    continue;
                }

                amounts[index] = decrease
                    ? RoundAmountToUnit(Math.Max(mins[index], amounts[index] - adjustment), unit)
                    : RoundAmountToUnit(Math.Min(maxs[index], amounts[index] + adjustment), unit);
                remaining = decrease
                    ? RoundMoney(activeIndexes.Sum(item => amounts[item]) - targetTotal)
                    : RoundMoney(targetTotal - activeIndexes.Sum(item => amounts[item]));
                progressed = true;
            }

            if (!progressed)
            {
                return;
            }
        }
    }

    private static void ApplySignedAmount(FlowRecord record, double sign, double absoluteAmount)
    {
        var amount = RoundMoney(Math.Max(0, absoluteAmount));
        if (amount > 0.009d && record.ExtraFields.ContainsKey(AmountUnitField))
        {
            amount = ClampAmountToBounds(amount, GetRecordAmountBounds(record));
        }

        record.TradeMoney = sign > 0 ? amount : -amount;
        record.IncomeAttribute = sign > 0 ? "收入" : "支出";
        record.CreditAmount = sign > 0 && amount > 0 ? amount : null;
        record.DebitAmount = sign < 0 && amount > 0 ? amount : null;
        record.IncomeFlag = sign > 0 ? "C" : "D";
    }

    private static bool EnsureDistinctSignedAmounts(List<FlowRecord> records, Random? random = null)
    {
        var changed = false;
        changed = EnsureDistinctSignedAmounts(records, isIncome: true) || changed;
        changed = EnsureDistinctSignedAmounts(records, isIncome: false) || changed;
        return changed;
    }

    private static bool EnsureDistinctSignedAmounts(List<FlowRecord> records, bool isIncome)
    {
        var signedRecords = records
            .Where(item => isIncome ? item.TradeMoney > 0.009d : item.TradeMoney < -0.009d)
            .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
            .ThenBy(item => item.Index)
            .ToList();
        if (signedRecords.Count < 2)
        {
            return false;
        }

        var sign = isIncome ? 1d : -1d;
        var usedAmounts = new HashSet<double>();
        var netDelta = 0d;
        var changed = false;
        foreach (var record in signedRecords)
        {
            var amount = RoundMoney(Math.Abs(record.TradeMoney ?? 0));
            if (amount <= 0.009d)
            {
                continue;
            }

            var bounds = GetRecordAmountBounds(record);
            var preferFractional = false;
            if (!usedAmounts.Contains(amount) && !preferFractional)
            {
                usedAmounts.Add(amount);
                continue;
            }

            if (IsSystemInterestRecord(record))
            {
                continue;
            }

            if (TryPickDistinctAmount(
                    amount,
                    bounds,
                    usedAmounts,
                    RoundMoney(-netDelta),
                    preferFractional,
                    out var distinctAmount))
            {
                ApplySignedAmount(record, sign, distinctAmount);
                usedAmounts.Add(distinctAmount);
                netDelta = RoundMoney(netDelta + distinctAmount - amount);
                changed = true;
            }
            else if (!usedAmounts.Contains(amount))
            {
                usedAmounts.Add(amount);
            }
        }

        return changed;
    }

    private static bool TryPickDistinctAmount(
        double amount,
        AmountBounds bounds,
        ISet<double> usedAmounts,
        double preferredDelta,
        bool preferFractional,
        out double distinctAmount)
    {
        distinctAmount = amount;
        var unit = Math.Max(0.01d, bounds.Unit);
        var totalSteps = Math.Floor((bounds.Max - bounds.Min + 0.0000001d) / unit) + 1d;
        if (totalSteps <= 1d || usedAmounts.Count >= totalSteps)
        {
            return false;
        }

        double? bestAmount = null;
        var bestScore = double.MaxValue;
        var bestDistance = double.MaxValue;

        void Consider(double candidate, bool allowWholeAmount)
        {
            candidate = ClampAmountToBounds(candidate, bounds);
            if (candidate <= 0.009d || usedAmounts.Contains(candidate))
            {
                return;
            }

            if (!allowWholeAmount && IsWholeAmount(candidate))
            {
                return;
            }

            if (!allowWholeAmount && HasUnnaturalFractionTail(candidate))
            {
                return;
            }

            var delta = RoundMoney(candidate - amount);
            var stylePenalty = GetDistinctAmountStylePenalty(candidate, bounds, preferFractional);
            var score = Math.Abs(RoundMoney(delta - preferredDelta)) + stylePenalty;
            var distance = Math.Abs(delta) + stylePenalty;
            if (score < bestScore - 0.0000001d
                || (Math.Abs(score - bestScore) <= 0.0000001d && distance < bestDistance - 0.0000001d))
            {
                bestAmount = candidate;
                bestScore = score;
                bestDistance = distance;
            }
        }

        var preferNonWhole = preferFractional && HasNonWholeAmountCandidate(bounds, usedAmounts);
        for (var pass = 0; pass < (preferNonWhole ? 2 : 1) && !bestAmount.HasValue; pass++)
        {
            var allowWholeAmount = pass > 0 || !preferNonWhole;
            var maxNearestSteps = (int)Math.Min(4096d, totalSteps);
            for (var step = 1; step < maxNearestSteps; step++)
            {
                Consider(amount - (step * unit), allowWholeAmount);
                Consider(amount + (step * unit), allowWholeAmount);
                if (bestAmount.HasValue && bestScore <= 0.009d)
                {
                    break;
                }
            }

            if (!bestAmount.HasValue)
            {
                var maxScanSteps = (int)Math.Min(4096d, totalSteps);
                for (var offset = 0; offset < maxScanSteps; offset++)
                {
                    Consider(bounds.Min + (offset * unit), allowWholeAmount);
                    if (bestAmount.HasValue && bestScore <= 0.009d)
                    {
                        break;
                    }
                }
            }
        }

        if (bestAmount.HasValue)
        {
            distinctAmount = bestAmount.Value;
            return true;
        }

        return false;
    }

    private static bool ShouldPreferFractionalAmount(AmountBounds bounds)
    {
        return bounds.Unit < 1d - 0.0000001d
            && bounds.Max >= bounds.Min + bounds.Unit - 0.0000001d;
    }

    private static bool IsWholeAmount(double amount)
    {
        return Math.Abs(RoundMoney(amount) - Math.Round(amount, 0, MidpointRounding.AwayFromZero)) <= 0.009d;
    }

    private static double GetDistinctAmountStylePenalty(double amount, AmountBounds bounds, bool preferFractional)
    {
        if (bounds.Unit >= 1d - 0.0000001d)
        {
            return 0;
        }

        if (IsWholeAmount(amount))
        {
            return preferFractional ? 0.25d : 0d;
        }

        if (HasUnnaturalFractionTail(amount))
        {
            return 12d;
        }

        var cents = GetCentTail(amount);
        if (cents is 10 or 20 or 30 or 40 or 50 or 60 or 70 or 80)
        {
            return 0.02d;
        }

        if (cents % 5 == 0 && cents < 90)
        {
            return 0.35d;
        }

        return 0.75d;
    }

    private static bool HasUnnaturalFractionTail(double amount)
    {
        var cents = GetCentTail(amount);
        return cents is 90 or >= 97;
    }

    private static int GetCentTail(double amount)
    {
        var cents = (int)Math.Round(Math.Abs(RoundMoney(amount)) * 100d, MidpointRounding.AwayFromZero) % 100;
        return cents;
    }

    private static bool HasNonWholeAmountCandidate(AmountBounds bounds, ISet<double> usedAmounts)
    {
        if (!ShouldPreferFractionalAmount(bounds))
        {
            return false;
        }

        var unit = Math.Max(0.01d, bounds.Unit);
        var totalSteps = (int)Math.Min(4096d, Math.Floor((bounds.Max - bounds.Min + 0.0000001d) / unit) + 1d);
        for (var offset = 0; offset < totalSteps; offset++)
        {
            var candidate = ClampAmountToBounds(bounds.Min + (offset * unit), bounds);
            if (!IsWholeAmount(candidate) && !usedAmounts.Contains(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static void AdjustRoundedAmountsToTarget(
        IReadOnlyList<FlowRecord> records,
        double[] amounts,
        double targetTotal,
        IReadOnlyList<double> mins,
        IReadOnlyList<double> maxs,
        IReadOnlyCollection<int> activeIndexes)
    {
        var diff = RoundMoney(targetTotal - amounts.Sum());
        if (Math.Abs(diff) <= 0.009d)
        {
            return;
        }

        var orderedIndexes = activeIndexes
            .OrderBy(index => IsRequiredGeneratedRecord(records[index]) ? 1 : 0)
            .ThenBy(index => GetRecordAmountUnit(records[index]))
            .ThenByDescending(index => amounts[index])
            .ToList();

        for (var attempt = 0; attempt < 10 && Math.Abs(diff) > 0.009d; attempt++)
        {
            var progressed = false;
            foreach (var index in orderedIndexes)
            {
                var unit = GetRecordAmountUnit(records[index]);
                if (unit <= 0 || unit > Math.Abs(diff) + 0.009d)
                {
                    continue;
                }

                var steps = Math.Floor(Math.Abs(diff) / unit + 0.0000001d);
                if (steps <= 0)
                {
                    continue;
                }

                var adjustment = RoundMoney(steps * unit);
                if (diff < 0)
                {
                    adjustment = Math.Min(adjustment, RoundMoney(amounts[index] - mins[index]));
                    if (adjustment <= 0.009d)
                    {
                        continue;
                    }

                    amounts[index] = RoundAmountToUnit(amounts[index] - adjustment, unit);
                }
                else
                {
                    adjustment = Math.Min(adjustment, RoundMoney(maxs[index] - amounts[index]));
                    if (adjustment <= 0.009d)
                    {
                        continue;
                    }

                    amounts[index] = RoundAmountToUnit(amounts[index] + adjustment, unit);
                }

                diff = RoundMoney(targetTotal - amounts.Sum());
                progressed = true;
                if (Math.Abs(diff) <= 0.009d)
                {
                    return;
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        // Leave any remaining diff unresolved when no record can move without breaking its bounds.
    }

    private static void BringFinalBalanceWithinTolerance(List<FlowRecord> records, FlowAutoGenerationRequest request, double openingBalance)
    {
        if (records.Count == 0)
        {
            return;
        }

        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start)
        {
            (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        }

        var random = new Random(CreateRunSeed(request, start, end, records.Count ^ 0x5F3759DF));
        var maxAttempts = IsWechatBank(request.Bank) ? 2 : 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ApplyBalances(records, openingBalance);
            if (EnforceConfiguredTotals(records, request))
            {
                PruneZeroAmountRecords(records);
                continue;
            }

            var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
            var targetBalance = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
            if (finalBalance < targetBalance - FinalBalanceTolerance)
            {
                var reduction = RoundMoney(targetBalance - FinalBalanceTolerance - finalBalance);
                if (!IncreaseFinalBalanceWithinRules(records, request, start, end, random, reduction))
                {
                    break;
                }

                PruneZeroAmountRecords(records);
                continue;
            }

            if (finalBalance > targetBalance + FinalBalanceTolerance)
            {
                var increase = RoundMoney(finalBalance - (targetBalance + FinalBalanceTolerance));
                if (!DecreaseFinalBalanceWithinRules(records, request, start, end, random, increase))
                {
                    break;
                }

                PruneZeroAmountRecords(records);
                continue;
            }

            break;
        }
    }

    private static bool ForceHighVolumeFinalBalanceWithinTolerance(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        if (records.Count == 0)
        {
            return false;
        }

        var changed = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ApplyBalances(records, openingBalance);
            var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
            var targetBalance = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
            if (finalBalance < targetBalance - FinalBalanceTolerance)
            {
                var amount = RoundMoney(targetBalance - FinalBalanceTolerance - finalBalance);
                if (!IncreaseFinalBalanceWithinRules(records, request, start, end, random, amount))
                {
                    break;
                }

                changed = true;
                PruneZeroAmountRecords(records);
                continue;
            }

            if (finalBalance > targetBalance + FinalBalanceTolerance)
            {
                var amount = RoundMoney(finalBalance - targetBalance - FinalBalanceTolerance);
                if (!DecreaseFinalBalanceWithinRules(records, request, start, end, random, amount))
                {
                    break;
                }

                changed = true;
                PruneZeroAmountRecords(records);
                continue;
            }

            break;
        }

        if (changed)
        {
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static bool IncreaseFinalBalanceWithinRules(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        double amount)
    {
        var remaining = RoundMoney(amount);
        var netBefore = CalculateNetAmount(records);
        DecreaseSignedRecordsWithinBounds(records, isIncome: false, remaining, allowDropOptional: true);
        remaining = RoundMoney(amount - (CalculateNetAmount(records) - netBefore));
        if (remaining <= 0.009d)
        {
            return true;
        }

        var availableIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney - SumIncome(records)));
        if (availableIncome > 0.009d)
        {
            netBefore = CalculateNetAmount(records);
            IncreaseSignedRecordsWithinBounds(records, isIncome: true, Math.Min(remaining, availableIncome));
            remaining = RoundMoney(remaining - (CalculateNetAmount(records) - netBefore));
            availableIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney - SumIncome(records)));
        }

        if (remaining <= 0.009d)
        {
            return true;
        }

        if (availableIncome <= 0.009d)
        {
            return false;
        }

        netBefore = CalculateNetAmount(records);
        AddReferenceAdjustmentRecords(
            records,
            request,
            start,
            end,
            random,
            isIncome: true,
            targetAmount: Math.Min(remaining, availableIncome));
        remaining = RoundMoney(remaining - (CalculateNetAmount(records) - netBefore));

        return remaining <= 0.009d;
    }

    private static bool DecreaseFinalBalanceWithinRules(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        double amount)
    {
        var remaining = RoundMoney(Math.Min(amount, GetAvailableExpenseRoom(records)));
        if (remaining <= 0.009d)
        {
            return false;
        }

        var netBefore = CalculateNetAmount(records);
        IncreaseSignedRecordsWithinBounds(records, isIncome: false, remaining);
        remaining = RoundMoney(amount - (netBefore - CalculateNetAmount(records)));
        if (remaining <= 0.009d)
        {
            return true;
        }

        remaining = RoundMoney(Math.Min(remaining, GetAvailableExpenseRoom(records)));
        if (remaining <= 0.009d)
        {
            return false;
        }

        netBefore = CalculateNetAmount(records);
        DecreaseSignedRecordsWithinBounds(records, isIncome: true, remaining, allowDropOptional: true);
        remaining = RoundMoney(remaining - (netBefore - CalculateNetAmount(records)));
        if (remaining <= 0.009d)
        {
            return true;
        }

        netBefore = CalculateNetAmount(records);
        AddReferenceAdjustmentRecords(
            records,
            request,
            start,
            end,
            random,
            isIncome: false,
            targetAmount: remaining);
        remaining = RoundMoney(remaining - (netBefore - CalculateNetAmount(records)));

        return remaining <= 0.009d;
    }

    private static double CalculateNetAmount(IEnumerable<FlowRecord> records)
    {
        return RoundMoney(SumIncome(records) - SumExpense(records));
    }

    private static void AddReferenceAdjustmentRecords(
        ICollection<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        bool isIncome,
        double targetAmount)
    {
        var remaining = RoundMoney(Math.Max(0, targetAmount));
        if (remaining <= 0.009d)
        {
            return;
        }

        remaining = LimitAdjustmentAmountByTotals(records, request, isIncome, remaining);
        if (remaining <= 0.009d)
        {
            return;
        }

        var orderedRules = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .OrderBy(item => IsAgriculturalSmallExpenseRule(request.Bank, item) ? 1 : 0)
            .ThenByDescending(item => GetRuleAmountBounds(item).Max)
            .ThenBy(_ => random.Next())
            .ToList();
        var rules = SelectNativeAmountFillRules(orderedRules);
        if (rules.Count == 0)
        {
            return;
        }

        var largestRuleAmount = rules
            .Select(item => GetRuleAmountBounds(item).Max)
            .DefaultIfEmpty(1d)
            .Max();
        var maxAttempts = Math.Clamp(
            (int)Math.Ceiling(remaining / Math.Max(1d, largestRuleAmount)) + (rules.Count * 4),
            rules.Count,
            IsWechatBank(request.Bank) ? 160 : 1000);
        for (var index = 0; index < maxAttempts && remaining > 0.009d; index++)
        {
            remaining = LimitAdjustmentAmountByTotals(records, request, isIncome, remaining);
            if (remaining <= 0.009d)
            {
                break;
            }

            var rule = rules[index % rules.Count];
            var bounds = GetRuleAmountBounds(rule);
            var maxAcceptable = remaining;
            if (bounds.Min > maxAcceptable + 0.009d)
            {
                continue;
            }

            var amount = Math.Min(remaining, bounds.Max);
            if (amount < bounds.Min - 0.009d)
            {
                continue;
            }

            var effectiveBounds = new AmountBounds(bounds.Min, Math.Min(bounds.Max, maxAcceptable), bounds.Unit);
            amount = ClampAmountToBounds(amount, effectiveBounds);
            amount = MoveAmountAwayFromEdges(amount, effectiveBounds, random);
            if (amount <= 0.009d || amount > maxAcceptable + 0.009d)
            {
                continue;
            }

            records.Add(CreateRecordFromRule(
                request,
                rule,
                request.Bank.ReferenceColumns,
                PickAdjustmentTime(
                    records,
                    request.OpeningBalanceOverride ?? request.Config.OpeningBalance,
                    start,
                    end,
                    rule,
                    random,
                    index,
                    isIncome ? amount : -amount),
                isIncome ? amount : -amount));
            remaining = RoundMoney(remaining - amount);
        }
    }

    private static DateTime PickAdjustmentTime(
        IEnumerable<FlowRecord> records,
        double openingBalance,
        DateTime start,
        DateTime end,
        FlowRuleBase rule,
        Random random,
        int sequence,
        double signedAmount)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var sign = signedAmount >= 0 ? 1 : -1;
        var existing = records
            .Where(item => item.AccountTime.HasValue)
            .ToList();
        var sameSignCounts = existing
            .Where(item => GetAmountSign(item.TradeMoney ?? 0) == sign)
            .GroupBy(item => item.AccountTime!.Value.Date)
            .ToDictionary(group => group.Key, group => group.Count());
        var totalCounts = existing
            .GroupBy(item => item.AccountTime!.Value.Date)
            .ToDictionary(group => group.Key, group => group.Count());

        var dates = EnumerateDates(start.Date, NormalizeEndDate(end).Date)
            .Where(date => rule.TradeWeekend != false || date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            .ToList();
        if (dates.Count == 0)
        {
            return PickTime(start, end, rule, random);
        }

        var desiredOffset = Math.Abs((sequence * 7 + 3) % dates.Count);
        var amount = Math.Abs(signedAmount);
        var orderedExisting = existing
            .OrderBy(item => item.AccountTime)
            .ThenBy(item => item.SerialNum)
            .ToList();
        var runningBalance = RoundMoney(openingBalance);
        var recordIndex = 0;
        var candidates = new List<AdjustmentTimeCandidate>(dates.Count);
        for (var index = 0; index < dates.Count; index++)
        {
            var date = dates[index];
            var dayEnd = date.AddDays(1).AddTicks(-1);
            while (recordIndex < orderedExisting.Count
                && orderedExisting[recordIndex].AccountTime <= dayEnd)
            {
                runningBalance = RoundMoney(runningBalance + (orderedExisting[recordIndex].TradeMoney ?? 0));
                recordIndex++;
            }

            candidates.Add(new AdjustmentTimeCandidate(
                date,
                index,
                sameSignCounts.GetValueOrDefault(date),
                totalCounts.GetValueOrDefault(date),
                runningBalance));
        }

        foreach (var candidate in candidates
                     .OrderByDescending(item => sign > 0 || item.BalanceBefore >= amount)
                     .ThenBy(item => item.SameSignCount)
                     .ThenBy(item => item.TotalCount)
                     .ThenBy(item => Math.Abs(item.Index - desiredOffset))
                     .ThenBy(item => item.Date))
        {
            var dayStart = MaxDateTime(start, candidate.Date);
            var dayEnd = MinDateTime(end, candidate.Date.AddDays(1).AddTicks(-1));
            if (dayStart > dayEnd)
            {
                continue;
            }

            return PickTime(dayStart, dayEnd, rule, random);
        }

        return PickTime(start, end, rule, random);
    }

    private sealed record AdjustmentTimeCandidate(
        DateTime Date,
        int Index,
        int SameSignCount,
        int TotalCount,
        double BalanceBefore);

    private static int GetAmountSign(double amount)
    {
        if (amount > 0.009d)
        {
            return 1;
        }

        return amount < -0.009d ? -1 : 0;
    }

    private static IEnumerable<DateTime> EnumerateDates(DateTime start, DateTime end)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static DateTime MinDateTime(DateTime left, DateTime right)
    {
        return left <= right ? left : right;
    }

    private static DateTime MaxDateTime(DateTime left, DateTime right)
    {
        return left >= right ? left : right;
    }

    private static bool DecreaseSignedRecordsWithinBounds(
        IEnumerable<FlowRecord> records,
        bool isIncome,
        double amount,
        bool allowDropOptional)
    {
        var remaining = RoundMoney(amount);
        var candidates = GetSignedRecords(records, isIncome)
            .OrderBy(item => IsRequiredGeneratedRecord(item) ? 1 : 0)
            .ThenBy(item => IsSmallDiscreteAmountRecord(item) ? 1 : 0)
            .ThenByDescending(item => Math.Abs(item.TradeMoney ?? 0))
            .ThenByDescending(item => item.AccountTime ?? DateTime.MinValue)
            .ToList();

        foreach (var record in candidates)
        {
            if (remaining <= 0.009d)
            {
                break;
            }

            var absolute = Math.Abs(record.TradeMoney ?? 0);
            if (absolute <= 0)
            {
                continue;
            }

            var sign = isIncome ? 1d : -1d;
            var bounds = GetRecordAmountBounds(record);
            var isRequired = IsRequiredGeneratedRecord(record);
            if (!isRequired && allowDropOptional)
            {
                var canDropRecord = !isIncome || CanDropOptionalIncomeRecord(candidates, record);
                var partialNext = RoundMoney(absolute - remaining);
                if (partialNext >= bounds.Min - 0.009d)
                {
                    var nextAmount = ClampAmountToBounds(partialNext, bounds);
                    var actualReduction = RoundMoney(absolute - nextAmount);
                    if (actualReduction > 0.009d)
                    {
                        ApplySignedAmount(record, sign, nextAmount);
                        remaining = RoundMoney(remaining - actualReduction);
                    }

                    continue;
                }

                if (canDropRecord)
                {
                    ApplySignedAmount(record, sign, 0);
                    remaining = RoundMoney(remaining - absolute);
                    continue;
                }

                var reducibleToMin = RoundMoney(absolute - bounds.Min);
                if (reducibleToMin <= 0.009d)
                {
                    continue;
                }

                ApplySignedAmount(record, sign, bounds.Min);
                remaining = RoundMoney(remaining - reducibleToMin);
                continue;
            }

            var reducible = RoundMoney(absolute - bounds.Min);
            if (reducible <= 0.009d)
            {
                continue;
            }

            var reduction = Math.Min(reducible, remaining);
            var adjustedAmount = ClampAmountToBounds(absolute - reduction, bounds);
            var actualRequiredReduction = RoundMoney(absolute - adjustedAmount);
            if (actualRequiredReduction <= 0.009d)
            {
                continue;
            }

            ApplySignedAmount(record, sign, adjustedAmount);
            remaining = RoundMoney(remaining - actualRequiredReduction);
        }

        return remaining <= 0.009d;
    }

    private static bool CanDropOptionalIncomeRecord(IEnumerable<FlowRecord> records, FlowRecord candidate)
    {
        if (!candidate.AccountTime.HasValue)
        {
            return true;
        }

        var bounds = GetRecordAmountBounds(candidate);
        if (bounds.Min < 1000d || bounds.Max <= bounds.Min * 4d)
        {
            return true;
        }

        var month = candidate.AccountTime.Value;
        var sameMonthCount = records.Count(item =>
            (item.TradeMoney ?? 0) > 0.009d
            && item.AccountTime.HasValue
            && item.AccountTime.Value.Year == month.Year
            && item.AccountTime.Value.Month == month.Month);
        return sameMonthCount > 3;
    }

    private static bool IncreaseSignedRecordsWithinBounds(IEnumerable<FlowRecord> records, bool isIncome, double amount)
    {
        var remaining = RoundMoney(amount);
        var sign = isIncome ? 1d : -1d;
        var signedRecords = GetSignedRecords(records, isIncome).ToList();
        var hasNonSmallRecords = signedRecords.Any(item =>
            !IsSmallDiscreteAmountRecord(item)
            && !IsLowCapacityReferenceRecord(item));
        var orderedRecords = signedRecords
            .Where(item => !hasNonSmallRecords
                || !IsSmallDiscreteAmountRecord(item) && !IsLowCapacityReferenceRecord(item))
            .OrderBy(item => IsRequiredGeneratedRecord(item) ? 1 : 0)
            .ThenBy(item => IsAgriculturalSmallExpenseRecord(item) ? 1 : 0)
            .ThenBy(item => GetRecordAmountUnit(item))
            .ThenByDescending(item => item.AccountTime ?? DateTime.MinValue)
            .ToList();

        for (var pass = 0; pass < 2 && remaining > 0.009d; pass++)
        {
            foreach (var record in orderedRecords)
            {
                if (remaining <= 0.009d)
                {
                    break;
                }

                var absolute = Math.Abs(record.TradeMoney ?? 0);
                var bounds = GetRecordAmountBounds(record);
                var upper = pass == 0
                    ? GetPreferredAmountUpperBound(record, bounds)
                    : bounds.Max;
                var room = RoundMoney(upper - absolute);
                if (room <= 0.009d)
                {
                    continue;
                }

                var unit = Math.Max(0.01d, bounds.Unit);
                var increase = Math.Min(room, remaining);
                if (increase > 0.009d)
                {
                    increase = RoundAmountToUnit(increase, unit);
                    if (increase > remaining + 0.009d)
                    {
                        increase = FloorAmountToUnit(remaining, unit);
                    }
                }

                if (increase <= 0.009d)
                {
                    continue;
                }

                ApplySignedAmount(record, sign, ClampAmountToBounds(absolute + increase, bounds));
                remaining = RoundMoney(remaining - increase);
            }
        }

        return remaining <= 0.009d;
    }

    private static void ApplyBalances(List<FlowRecord> records, double openingBalance)
    {
        if (!AreRecordsInBalanceOrder(records))
        {
            records.Sort(CompareRecordsForBalance);
        }

        var balance = RoundMoney(openingBalance);
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var amount = RoundMoney(record.TradeMoney ?? 0);
            balance = RoundMoney(balance + amount);
            record.Index = index + 1;
            record.TradeMoney = amount;
            record.Balance = balance;
            record.BalanceAmount = balance;
            record.IncomeAttribute = amount >= 0 ? "收入" : "支出";
            record.CreditAmount = amount > 0 ? amount : null;
            record.DebitAmount = amount < 0 ? Math.Abs(amount) : null;
            record.IncomeFlag = amount >= 0 ? "C" : "D";
        }
    }

    private static bool AreRecordsInBalanceOrder(IReadOnlyList<FlowRecord> records)
    {
        for (var index = 1; index < records.Count; index++)
        {
            if (CompareRecordsForBalance(records[index - 1], records[index]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareRecordsForBalance(FlowRecord left, FlowRecord right)
    {
        var result = Nullable.Compare(left.AccountTime, right.AccountTime);
        return result != 0 ? result : string.CompareOrdinal(left.SerialNum, right.SerialNum);
    }

    private static double CalculateTargetExpense(double openingBalance, IEnumerable<FlowRecord> records, double lastMoney)
    {
        return RoundMoney(openingBalance + SumIncome(records) - CalculateFinalBalanceTarget(openingBalance, lastMoney));
    }

    private static double CalculateFinalBalanceTarget(double openingBalance, double lastMoney)
    {
        return RoundMoney(openingBalance + lastMoney);
    }

    private static bool IsFinalBalanceOutsideTolerance(double finalBalance, double openingBalance, double lastMoney)
    {
        return Math.Abs(RoundMoney(finalBalance - CalculateFinalBalanceTarget(openingBalance, lastMoney))) > FinalBalanceTolerance + 0.009d;
    }

    private static bool IsWechatBank(Bank bank)
    {
        return bank.Name.Contains("\u5fae\u4fe1", StringComparison.Ordinal);
    }

    private static bool IsAgriculturalBank(Bank bank)
    {
        return bank.Name.Contains("\u519c\u884c", StringComparison.Ordinal)
            || bank.Name.Contains("\u519c\u4e1a", StringComparison.Ordinal)
            || bank.Name.Contains("鍐滆", StringComparison.Ordinal);
    }

    private static bool UseHighVolumeNativePostProcessing(Bank bank, int recordCount)
    {
        return recordCount >= 5000
            || recordCount >= 700 && (IsAgriculturalBank(bank) || IsWechatBank(bank));
    }

    private static bool UseVeryHighVolumeNativeFastPath(int recordCount)
    {
        return recordCount >= 10000;
    }

    private static FlowAutoGenerationResult FinalizeVeryHighVolumeNativeResult(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        double openingBalance,
        DateTime start,
        DateTime end,
        Random random)
    {
        var perfTrace = FlowGenerationPerfTrace.Start();
        PruneZeroAmountRecords(records);
        SmoothNativeSchedule(records, request, start, end, random);
        perfTrace.Mark("fast schedule", records);
        ApplyBalances(records, openingBalance);
        EnforceConfiguredTotals(records, request);
        ApplyBalances(records, openingBalance);
        perfTrace.Mark("fast totals", records);

        RepairVeryHighVolumeNegativeBalances(records, openingBalance);
        perfTrace.Mark("fast nonnegative", records);
        ReducePositiveFinalBalanceSafely(records, request, openingBalance);
        AddSafeExpenseRecordsForPositiveFinalBalanceBatch(records, request, start, end, openingBalance, random);
        ApplyBalances(records, openingBalance);
        perfTrace.Mark("fast restore", records);
        ForceHighVolumeFinalBalanceWithinTolerance(records, request, start, end, openingBalance, random);
        EnforceConfiguredTotals(records, request);
        ApplyBalances(records, openingBalance);
        if (GetMinimumBalance(records, openingBalance) < -0.009d)
        {
            RepairVeryHighVolumeNegativeBalances(records, openingBalance);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            ApplyBalances(records, openingBalance);
        }

        perfTrace.Mark("fast tolerance", records);

        var incomeTotal = SumIncome(records);
        var expenseTotal = SumExpense(records);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimumBalance = GetMinimumBalance(records, openingBalance);
        if (incomeTotal > Math.Max(0, request.Config.AllInMoney) + 0.009d
            || expenseTotal > incomeTotal + 0.009d
            || minimumBalance < -0.009d
            || IsFinalBalanceOutsideTolerance(finalBalance, openingBalance, request.Config.LastMoney))
        {
            ForceCloseExternalFinalBalance(request, records);
            ApplyBalances(records, openingBalance);
            if (GetMinimumBalance(records, openingBalance) < -0.009d)
            {
                RepairVeryHighVolumeNegativeBalances(records, openingBalance);
            }

            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            AddSafeExpenseRecordsForPositiveFinalBalanceBatch(records, request, start, end, openingBalance, random);
            ApplyBalances(records, openingBalance);
            perfTrace.Mark("fast force close", records);
            incomeTotal = SumIncome(records);
            expenseTotal = SumExpense(records);
            finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
            minimumBalance = GetMinimumBalance(records, openingBalance);
        }

        RebalanceVeryHighVolumeLowCapacityExpense(
            records,
            request,
            start,
            end,
            openingBalance,
            random);
        ApplyBalances(records, openingBalance);
        incomeTotal = SumIncome(records);
        expenseTotal = SumExpense(records);
        finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        minimumBalance = GetMinimumBalance(records, openingBalance);

        var requiredOpeningBalance = minimumBalance < -0.009d
            ? RoundMoney(openingBalance - minimumBalance)
            : openingBalance;
        return new FlowAutoGenerationResult
        {
            Records = records,
            OpeningBalance = openingBalance,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            FinalBalance = RoundMoney(finalBalance),
            MinimumBalance = RoundMoney(minimumBalance),
            RequiresOpeningBalanceCorrection = minimumBalance < -0.009d
                || IsFinalBalanceOutsideTolerance(finalBalance, openingBalance, request.Config.LastMoney),
            RequiredOpeningBalance = RoundMoney(Math.Max(0, requiredOpeningBalance))
        };
    }

    private static bool RebalanceVeryHighVolumeLowCapacityExpense(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        var incomeTotal = SumIncome(records);
        var targetExpense = RoundMoney(Math.Min(incomeTotal, Math.Max(0, incomeTotal - request.Config.LastMoney)));
        var lowCapacityLimit = RoundMoney(targetExpense * MaximumLowCapacityTierPoolRatio);
        var lowCapacityRecords = records
            .Where(item => item.TradeMoney < -0.009d)
            .Where(IsGeneratedLowAmountReferenceRecord)
            .OrderBy(item => IsRequiredGeneratedRecord(item) ? 1 : 0)
            .ThenByDescending(item => Math.Abs(item.TradeMoney ?? 0))
            .ToList();
        var lowCapacityTotal = RoundMoney(lowCapacityRecords.Sum(item => Math.Abs(item.TradeMoney ?? 0)));
        var excess = RoundMoney(lowCapacityTotal - lowCapacityLimit);
        if (excess <= 0.009d)
        {
            return false;
        }

        var beforeExpense = SumExpense(records);
        DecreaseSignedRecordsWithinBounds(
            lowCapacityRecords,
            isIncome: false,
            excess,
            allowDropOptional: true);
        PruneZeroAmountRecords(records);
        ApplyBalances(records, openingBalance);
        var reduced = RoundMoney(beforeExpense - SumExpense(records));
        if (reduced <= 0.009d)
        {
            return false;
        }

        AddSafeExpenseRecordsForPositiveFinalBalanceBatch(
            records,
            request,
            start,
            end,
            openingBalance,
            random);
        ReducePositiveFinalBalanceSafely(records, request, openingBalance);
        ApplyBalances(records, openingBalance);
        return true;
    }

    private static bool RepairVeryHighVolumeNegativeBalances(
        List<FlowRecord> records,
        double openingBalance)
    {
        if (records.Count == 0)
        {
            return false;
        }

        ApplyBalances(records, openingBalance);
        var changed = false;
        var runningBalance = RoundMoney(openingBalance);
        var adjustableExpenses = new PriorityQueue<FlowRecord, double>();
        foreach (var record in records)
        {
            var amount = RoundMoney(record.TradeMoney ?? 0);
            runningBalance = RoundMoney(runningBalance + amount);
            if (amount < -0.009d && !IsSystemInterestRecord(record))
            {
                var reducible = GetExpenseReducibleAmount(record);
                if (reducible > 0.009d)
                {
                    adjustableExpenses.Enqueue(record, -reducible);
                }
            }

            while (runningBalance < 1d - 0.009d
                   && adjustableExpenses.TryDequeue(out var candidate, out _))
            {
                var absolute = Math.Abs(candidate.TradeMoney ?? 0);
                var reducible = GetExpenseReducibleAmount(candidate);
                if (reducible <= 0.009d)
                {
                    continue;
                }

                var needed = RoundMoney(1d - runningBalance);
                var bounds = GetRecordAmountBounds(candidate);
                var desired = absolute - Math.Min(reducible, needed);
                double nextAmount;
                if (desired >= bounds.Min - 0.009d)
                {
                    nextAmount = ClampAmountToBounds(
                        FloorAmountToUnit(desired, Math.Max(0.01d, bounds.Unit)),
                        bounds);
                }
                else
                {
                    nextAmount = IsRequiredGeneratedRecord(candidate) ? bounds.Min : 0d;
                }

                var actualReduction = RoundMoney(absolute - nextAmount);
                if (actualReduction <= 0.009d)
                {
                    continue;
                }

                ApplySignedAmount(candidate, -1d, nextAmount);
                runningBalance = RoundMoney(runningBalance + actualReduction);
                changed = true;

                var remainingReducible = GetExpenseReducibleAmount(candidate);
                if (remainingReducible > 0.009d)
                {
                    adjustableExpenses.Enqueue(candidate, -remainingReducible);
                }
            }
        }

        if (changed)
        {
            PruneZeroAmountRecords(records);
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static bool AddSafeExpenseRecordsForPositiveFinalBalanceBatch(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        if (records.Count == 0)
        {
            return false;
        }

        ApplyBalances(records, openingBalance);
        var targetBalance = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var balanceDiff = RoundMoney(finalBalance - targetBalance);
        if (balanceDiff <= FinalBalanceTolerance)
        {
            return false;
        }

        if (TryClosePositiveFinalBalanceByNonLowExpenseExchange(
                records,
                openingBalance,
                targetBalance))
        {
            ApplyBalances(records, openingBalance);
            finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
            balanceDiff = RoundMoney(finalBalance - targetBalance);
            if (balanceDiff <= FinalBalanceTolerance)
            {
                return true;
            }
        }

        var incomeTotal = SumIncome(records);
        var targetExpense = RoundMoney(Math.Min(incomeTotal, Math.Max(0, incomeTotal - request.Config.LastMoney)));
        var expenseRoom = RoundMoney(Math.Max(0, targetExpense - SumExpense(records)));
        var requestedAmount = RoundMoney(Math.Min(balanceDiff, expenseRoom));
        if (requestedAmount <= FinalBalanceTolerance)
        {
            return false;
        }

        MoveLateOptionalIncomeBeforeFinalExpenseWindow(
            records,
            request,
            end,
            requestedAmount,
            random);
        ApplyBalances(records, openingBalance);

        var suffixMinimumBalances = new double[records.Count];
        var suffixMinimum = double.MaxValue;
        for (var index = records.Count - 1; index >= 0; index--)
        {
            suffixMinimum = Math.Min(suffixMinimum, records[index].Balance ?? openingBalance);
            suffixMinimumBalances[index] = suffixMinimum;
        }

        var minimumRequiredAddition = RoundMoney(requestedAmount - FinalBalanceTolerance);
        var safeIndex = Enumerable.Range(0, records.Count)
            .FirstOrDefault(index => records[index].AccountTime.HasValue
                && suffixMinimumBalances[index] >= minimumRequiredAddition + 1d, -1);
        if (safeIndex < 0)
        {
            return false;
        }

        var effectiveStart = MaxDateTime(start, records[safeIndex].AccountTime!.Value);
        if (effectiveStart > end)
        {
            return false;
        }

        var rules = GetNativeFillReferenceRules(request, isIncome: false, includeRequiredRules: false, random);
        if (rules.Count == 0)
        {
            return false;
        }

        var scheduleState = NativeScheduleState.From(records);
        var added = new List<FlowRecord>();
        var remaining = requestedAmount;
        var safeCapacity = RoundMoney(suffixMinimumBalances[safeIndex] - 1d);
        var usedSafeCapacity = 0d;
        var rulesByTier = rules
            .GroupBy(GetReferenceCapacityTier)
            .ToDictionary(group => group.Key, group => group.ToList());
        var tierCursors = Enum.GetValues<ReferenceCapacityTier>()
            .ToDictionary(tier => tier, _ => 0);
        var consecutiveFailures = 0;
        var failureLimit = Math.Max(32, rules.Count * 6);
        var lowCapacityLimit = RoundMoney(targetExpense * MaximumLowCapacityTierPoolRatio);
        var currentLowCapacityExpense = RoundMoney(records
            .Where(item => item.TradeMoney < -0.009d)
            .Where(IsGeneratedLowAmountReferenceRecord)
            .Sum(item => Math.Abs(item.TradeMoney ?? 0)));
        var lowCapacityRoom = RoundMoney(Math.Max(0, lowCapacityLimit - currentLowCapacityExpense));
        var referenceExpenseCount = records.Count(item =>
            item.TradeMoney < -0.009d && IsGeneratedReferenceRecord(item));
        var lowReferenceExpenseCount = records.Count(item =>
            item.TradeMoney < -0.009d
            && IsGeneratedReferenceRecord(item)
            && GetGeneratedAmountCapacityTier(Math.Abs(item.TradeMoney ?? 0)) == ReferenceCapacityTier.Low);
        while (remaining > FinalBalanceTolerance && consecutiveFailures < failureLimit)
        {
            var selectedTier = Enum.GetValues<ReferenceCapacityTier>()
                .OrderByDescending(tier => tier)
                .FirstOrDefault(tier => rulesByTier.TryGetValue(tier, out var tierRules)
                    && tierRules.Any(rule => GetRuleAmountBounds(rule).Min <= remaining + FinalBalanceTolerance + 0.009d),
                    (ReferenceCapacityTier)(-1));
            if ((int)selectedTier < 0 || !rulesByTier.TryGetValue(selectedTier, out var selectedRules))
            {
                break;
            }

            var cursor = tierCursors[selectedTier];
            var rule = selectedRules[cursor % selectedRules.Count];
            tierCursors[selectedTier] = cursor + 1;
            var bounds = GetRuleAmountBounds(rule);
            if (remaining + FinalBalanceTolerance < bounds.Min - 0.009d)
            {
                consecutiveFailures++;
                continue;
            }

            var remainingSafeCapacity = RoundMoney(Math.Max(0, safeCapacity - usedSafeCapacity));
            var upper = Math.Min(Math.Min(bounds.Max, remaining), remainingSafeCapacity);
            if (upper < bounds.Min - 0.009d)
            {
                consecutiveFailures++;
                continue;
            }

            var lower = Math.Max(bounds.Min, upper >= bounds.Max - 0.009d
                ? bounds.Min + ((bounds.Max - bounds.Min) * 0.65d)
                : bounds.Min);
            var rawAmount = upper < bounds.Max - 0.009d
                ? upper
                : lower + (random.NextDouble() * Math.Max(0, upper - lower));
            var amount = ClampAmountToBounds(rawAmount, bounds);
            if (amount <= 0.009d
                || amount > remaining + FinalBalanceTolerance + 0.009d
                || amount > remainingSafeCapacity + 0.009d)
            {
                consecutiveFailures++;
                continue;
            }

            if (GetGeneratedAmountCapacityTier(amount) == ReferenceCapacityTier.Low)
            {
                if (lowCapacityRoom < amount - 0.009d
                    || (lowReferenceExpenseCount + 1d) / Math.Max(1d, referenceExpenseCount + 1d)
                    > MaximumLowRecordRatio + 0.0001d)
                {
                    consecutiveFailures++;
                    continue;
                }

                lowCapacityRoom = RoundMoney(lowCapacityRoom - amount);
            }

            var accountTime = PickNativeDistributedTimeAtOrAfter(
                effectiveStart,
                end,
                rule,
                isIncome: false,
                random,
                scheduleState);
            if (!accountTime.HasValue)
            {
                consecutiveFailures++;
                continue;
            }

            var record = CreateRecordFromRule(
                request,
                rule,
                request.Bank.ReferenceColumns,
                accountTime.Value,
                -amount);
            record.ExtraFields[RequiredOccurrenceField] = "false";
            records.Add(record);
            added.Add(record);
            scheduleState.Register(record);
            referenceExpenseCount++;
            if (GetGeneratedAmountCapacityTier(amount) == ReferenceCapacityTier.Low)
            {
                lowReferenceExpenseCount++;
            }
            remaining = RoundMoney(remaining - amount);
            usedSafeCapacity = RoundMoney(usedSafeCapacity + amount);
            consecutiveFailures = 0;
        }

        if (added.Count == 0)
        {
            return false;
        }

        ApplyBalances(records, openingBalance);
        if (GetMinimumBalance(records, openingBalance) < -0.009d)
        {
            foreach (var record in added)
            {
                records.Remove(record);
            }

            ApplyBalances(records, openingBalance);
            return false;
        }

        return true;
    }

    private static bool TryClosePositiveFinalBalanceByNonLowExpenseExchange(
        List<FlowRecord> records,
        double openingBalance,
        double targetBalance)
    {
        ApplyBalances(records, openingBalance);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var balanceDiff = RoundMoney(finalBalance - targetBalance);
        if (balanceDiff <= FinalBalanceTolerance)
        {
            return false;
        }

        var recordIndexes = new Dictionary<FlowRecord, int>(ReferenceEqualityComparer.Instance);
        var suffixMinimumBalances = new double[records.Count];
        var suffixMinimum = double.MaxValue;
        for (var index = records.Count - 1; index >= 0; index--)
        {
            recordIndexes[records[index]] = index;
            suffixMinimum = Math.Min(suffixMinimum, records[index].Balance ?? openingBalance);
            suffixMinimumBalances[index] = suffixMinimum;
        }

        var candidates = records
            .Where(item => item.TradeMoney < -0.009d)
            .Where(item => !IsSystemInterestRecord(item))
            .Where(item => !IsGeneratedLowAmountReferenceRecord(item))
            .Where(item => item.AccountTime.HasValue)
            .OrderBy(item => IsRequiredGeneratedRecord(item) ? 1 : 0)
            .ThenBy(item => item.AccountTime)
            .ToList();
        foreach (var donor in candidates)
        {
            var donorAmount = Math.Abs(donor.TradeMoney ?? 0);
            var donorBounds = GetRecordAmountBounds(donor);
            var donorUnit = Math.Max(0.01d, donorBounds.Unit);
            var donorFloor = Math.Max(donorBounds.Min, CeilAmountToUnit(1000d + donorUnit, donorUnit));
            var donorRoom = RoundMoney(donorAmount - donorFloor);
            if (donorRoom <= 0.009d)
            {
                continue;
            }

            foreach (var receiver in candidates)
            {
                if (ReferenceEquals(receiver, donor)
                    || receiver.AccountTime <= donor.AccountTime)
                {
                    continue;
                }

                var receiverAmount = Math.Abs(receiver.TradeMoney ?? 0);
                var receiverBounds = GetRecordAmountBounds(receiver);
                var receiverRoom = RoundMoney(receiverBounds.Max - receiverAmount);
                if (receiverRoom < balanceDiff - FinalBalanceTolerance - 0.009d)
                {
                    continue;
                }

                var maximumDonorReduction = Math.Min(
                    donorRoom,
                    Math.Max(0, receiverRoom - Math.Max(0, balanceDiff - FinalBalanceTolerance)));
                var reductionTargets = new[]
                {
                    maximumDonorReduction,
                    maximumDonorReduction * 0.75d,
                    maximumDonorReduction * 0.50d,
                    maximumDonorReduction * 0.25d,
                    0d
                };
                foreach (var reductionTarget in reductionTargets)
                {
                    var nextDonorAmount = ClampAmountToBounds(
                        Math.Max(donorFloor, donorAmount - reductionTarget),
                        donorBounds);
                    if (nextDonorAmount < donorFloor - 0.009d)
                    {
                        continue;
                    }

                    var actualDonorReduction = RoundMoney(donorAmount - nextDonorAmount);
                    var nextReceiverAmount = ClampAmountToBounds(
                        receiverAmount + actualDonorReduction + balanceDiff,
                        receiverBounds);
                    var actualReceiverIncrease = RoundMoney(nextReceiverAmount - receiverAmount);
                    var netExpenseIncrease = RoundMoney(actualReceiverIncrease - actualDonorReduction);
                    if (netExpenseIncrease <= 0.009d
                        || Math.Abs(balanceDiff - netExpenseIncrease) > FinalBalanceTolerance + 0.009d)
                    {
                        continue;
                    }

                    var receiverIndex = recordIndexes[receiver];
                    if (suffixMinimumBalances[receiverIndex] < netExpenseIncrease - 0.009d)
                    {
                        continue;
                    }

                    ApplySignedAmount(donor, -1d, nextDonorAmount);
                    ApplySignedAmount(receiver, -1d, nextReceiverAmount);
                    ApplyBalances(records, openingBalance);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MoveLateOptionalIncomeBeforeFinalExpenseWindow(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime end,
        double amountNeeded,
        Random random)
    {
        var nonLowExpenseRules = request.References
            .Where(item => item.IsCheck && !IsIncomeRuleSafe(item))
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .Where(item => GetRuleAmountBounds(item).Max > 1000d + 0.009d)
            .ToList();
        if (nonLowExpenseRules.Count == 0)
        {
            return false;
        }

        var latestExpenseHour = nonLowExpenseRules
            .Select(item => Math.Clamp(item.EndDay ?? 17, 0, 23))
            .Max();
        var targetDate = NormalizeEndDate(end).Date;
        var expenseCutoff = targetDate.AddHours(latestExpenseHour + 1).AddTicks(-1);
        var candidates = records
            .Where(item => (item.TradeMoney ?? 0) > 0.009d)
            .Where(item => item.AccountTime.HasValue
                && item.AccountTime.Value.Date == targetDate
                && item.AccountTime.Value > expenseCutoff)
            .Where(item => !IsSystemInterestRecord(item))
            .Select(item => new
            {
                Record = item,
                Rule = ResolveRecordSourceRule(request, item)
            })
            .Where(item => item.Rule is not null && IsIncomeRuleSafe(item.Rule))
            .OrderByDescending(item => item.Record.TradeMoney ?? 0)
            .ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        var scheduleState = NativeScheduleState.From(records);
        var movedAmount = 0d;
        var changed = false;
        foreach (var candidate in candidates)
        {
            var rule = candidate.Rule!;
            var startHour = Math.Clamp(rule.StartDay ?? 9, 0, 23);
            var endHour = Math.Min(Math.Clamp(rule.EndDay ?? 17, 0, 23), latestExpenseHour);
            if (endHour < startHour)
            {
                continue;
            }

            var earliest = targetDate.AddHours(startHour);
            var latest = targetDate.AddHours(endHour + 1).AddTicks(-1);
            var availableSeconds = Math.Max(0, (int)Math.Floor((latest - earliest).TotalSeconds));
            DateTime? nextTime = null;
            for (var attempt = 0; attempt < 240; attempt++)
            {
                var offset = availableSeconds == 0 ? 0 : random.Next(0, availableSeconds + 1);
                var proposed = earliest.AddSeconds(offset);
                if (!scheduleState.IsTimeUsed(proposed))
                {
                    nextTime = proposed;
                    break;
                }
            }

            if (!nextTime.HasValue)
            {
                continue;
            }

            var originalTime = candidate.Record.AccountTime!.Value;
            candidate.Record.AccountTime = nextTime.Value;
            scheduleState.Move(candidate.Record, originalTime);
            movedAmount = RoundMoney(movedAmount + (candidate.Record.TradeMoney ?? 0));
            changed = true;
            if (movedAmount >= amountNeeded - 0.009d)
            {
                break;
            }
        }

        return changed;
    }

    private static bool IsAgriculturalSmallExpenseRule(Bank bank, FlowRuleBase rule)
    {
        if (!IsAgriculturalBank(bank) || IsIncomeRuleSafe(rule))
        {
            return false;
        }

        var bounds = GetRuleAmountBounds(rule);
        return bounds.Min < 300d && bounds.Max <= 300d;
    }

    private static bool IsAgriculturalSmallExpenseRecord(FlowRecord record)
    {
        return record.ExtraFields.TryGetValue(AgriculturalSmallExpenseField, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static bool ShouldAvoidSyntheticFraction(FlowRecord record)
    {
        return record.ExtraFields.TryGetValue(AvoidSyntheticFractionField, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static FlowAutoGenerationResult BuildResult(
        List<FlowRecord> records,
        double openingBalance,
        double lastMoney,
        bool requiresCorrection,
        double requiredOpeningBalance)
    {
        PruneZeroAmountRecords(records);
        EnsureDistinctSignedAmounts(records);
        ApplyBalances(records, openingBalance);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimumBalance = records.Select(item => item.Balance ?? openingBalance).Append(openingBalance).Min();

        return new FlowAutoGenerationResult
        {
            Records = records,
            OpeningBalance = openingBalance,
            IncomeTotal = SumIncome(records),
            ExpenseTotal = SumExpense(records),
            FinalBalance = RoundMoney(finalBalance),
            MinimumBalance = RoundMoney(minimumBalance),
            RequiresOpeningBalanceCorrection = requiresCorrection || IsFinalBalanceOutsideTolerance(finalBalance, openingBalance, lastMoney),
            RequiredOpeningBalance = RoundMoney(Math.Max(0, requiredOpeningBalance))
        };
    }

    private static FlowAutoGenerationResult CreateEmptyResult(double openingBalance, double lastMoney)
    {
        return new FlowAutoGenerationResult
        {
            Records = [],
            OpeningBalance = openingBalance,
            IncomeTotal = 0,
            ExpenseTotal = 0,
            FinalBalance = openingBalance,
            MinimumBalance = openingBalance,
            RequiresOpeningBalanceCorrection = IsFinalBalanceOutsideTolerance(openingBalance, openingBalance, lastMoney),
            RequiredOpeningBalance = openingBalance
        };
    }

    private static IEnumerable<(DateTime Start, DateTime End)> EnumerateMonths(DateTime start, DateTime end)
    {
        var cursor = new DateTime(start.Year, start.Month, 1);
        var last = new DateTime(end.Year, end.Month, 1);
        while (cursor <= last)
        {
            var monthStart = cursor == new DateTime(start.Year, start.Month, 1) ? start : cursor;
            var monthEnd = new DateTime(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month), 23, 59, 59);
            if (monthEnd > end)
            {
                monthEnd = end;
            }

            yield return (monthStart, monthEnd);
            cursor = cursor.AddMonths(1);
        }
    }

    private static IEnumerable<DateTime> ResolveFixedDays(GenerateConstRule rule, DateTime start, DateTime end)
    {
        var parsedDates = ParseDateTokens(rule.FixDay).ToList();
        if (parsedDates.Count > 0)
        {
            return parsedDates
                .Select(item => item.Date)
                .Where(item => item >= start.Date && item <= end.Date)
                .Distinct()
                .OrderBy(item => item)
                .ToList();
        }

        var days = ParseIntTokens(rule.FixDay)
            .Where(item => item is >= 1 and <= 31)
            .Distinct()
            .DefaultIfEmpty(start.Day)
            .ToList();

        var result = new List<DateTime>();
        foreach (var month in EnumerateMonths(start, end))
        {
            foreach (var day in days)
            {
                var fixedDay = Math.Min(day, DateTime.DaysInMonth(month.Start.Year, month.Start.Month));
                var date = new DateTime(month.Start.Year, month.Start.Month, fixedDay);
                if (date >= start.Date && date <= end.Date)
                {
                    result.Add(date);
                }
            }
        }

        return result
            .Distinct()
            .OrderBy(item => item)
            .ToList();
    }

    private static DateTime PickTime(DateTime start, DateTime end, FlowRuleBase? rule, Random random)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var startHour = Math.Clamp(rule?.StartDay ?? 9, 0, 23);
        var endHour = Math.Clamp(rule?.EndDay ?? 17, 0, 23);
        if (endHour < startHour)
        {
            (startHour, endHour) = (endHour, startHour);
        }

        var days = Math.Max(0, (end.Date - start.Date).Days);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var date = start.Date.AddDays(random.Next(0, days + 1));
            if (rule?.TradeWeekend == false && (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                continue;
            }

            var time = date
                .AddHours(random.Next(startHour, endHour + 1))
                .AddMinutes(random.Next(0, 60))
                .AddSeconds(random.Next(0, 60));

            if (time < start)
            {
                time = start.AddSeconds(random.Next(0, 120));
            }

            if (time > end)
            {
                time = end.AddSeconds(-random.Next(0, 120));
            }

            return time;
        }

        return start.Date
            .AddHours(startHour)
            .AddMinutes(random.Next(0, 60))
            .AddSeconds(random.Next(0, 60));
    }

    private readonly record struct AmountBounds(double Min, double Max, double Unit);

    private readonly record struct AmountSegment(double Min, double Max, int Index);

    private sealed class CapacityTierGenerationPlan(
        ReferenceCapacityTier tier,
        IReadOnlyList<GenerateReferenceRule> rules,
        int count,
        IReadOnlyList<DailyNaturalConsumptionSlot> dailySlots,
        double minimumTotal,
        double expectedTotal,
        double maximumTotal)
    {
        public ReferenceCapacityTier Tier { get; } = tier;
        public IReadOnlyList<GenerateReferenceRule> Rules { get; } = rules;
        public int Count { get; } = count;
        public IReadOnlyList<DailyNaturalConsumptionSlot> DailySlots { get; } = dailySlots;
        public int RegularCount => Math.Max(0, Count - DailySlots.Count);
        public double DailyMinimumTotal => RoundMoney(DailySlots.Sum(item => item.Minimum));
        public double DailyExpectedTotal => RoundMoney(DailySlots.Sum(item => item.Expected));
        public double MinimumTotal { get; } = minimumTotal;
        public double ExpectedTotal { get; } = expectedTotal;
        public double MaximumTotal { get; } = maximumTotal;
        public double Budget { get; set; }
    }

    private sealed class DailyNaturalConsumptionSlot(
        DateTime start,
        DateTime end,
        IReadOnlyList<GenerateReferenceRule> rules)
    {
        public DateTime Start { get; } = start;
        public DateTime End { get; } = end;
        public IReadOnlyList<GenerateReferenceRule> Rules { get; } = rules;
        public double Minimum { get; } = rules.Min(rule => GetRuleAmountBounds(rule).Min);
        public double Maximum { get; } = rules.Max(rule => GetRuleAmountBounds(rule).Max);
        public double Expected { get; } = rules.Average(rule => EstimateNaturalSegmentAverage(GetRuleAmountBounds(rule)));
    }

    private sealed record CapacityTierGenerationResult(
        double Spent,
        IReadOnlyList<FlowRecord> Records);

    private sealed record CapacityTierRuleGroup(
        ReferenceCapacityTier Tier,
        double Weight,
        IReadOnlyList<GenerateReferenceRule> Rules);

    private enum ReferenceCapacityTier
    {
        Low,
        Medium,
        High
    }

    private static FlowAutoGenerationRequest ExcludeUngeneratableReferenceRules(FlowAutoGenerationRequest request)
    {
        List<GenerateReferenceRule>? filteredReferences = null;
        for (var index = 0; index < request.References.Count; index++)
        {
            var rule = request.References[index];
            if (!rule.IsCheck || TryGetRuleAmountBounds(rule, out _))
            {
                filteredReferences?.Add(rule);
                continue;
            }

            filteredReferences ??= request.References.Take(index).ToList();
        }

        if (filteredReferences is null)
        {
            return request;
        }

        return new FlowAutoGenerationRequest
        {
            Bank = request.Bank,
            BankUser = request.BankUser,
            Config = request.Config,
            References = filteredReferences,
            ConstItems = request.ConstItems,
            InterestSetting = request.InterestSetting,
            OpeningBalanceOverride = request.OpeningBalanceOverride
        };
    }

    private static AmountBounds GetRuleAmountBounds(FlowRuleBase rule)
    {
        if (TryGetRuleAmountBounds(rule, out var bounds))
        {
            return bounds;
        }

        throw new InvalidOperationException(
            $"参照明细 ID {rule.Index} 的金额范围 {rule.MinMoney:0.##}~{rule.MaxMoney:0.##} " +
            $"与小数位 {rule.FloutLength} 冲突，范围内没有符合位数规则的金额。");
    }

    private static bool TryGetRuleAmountBounds(FlowRuleBase rule, out AmountBounds bounds)
    {
        var unit = GetAmountUnit(rule.FloutLength);
        var min = Math.Max(0.01d, rule.MinMoney ?? 10d);
        var max = Math.Max(min, rule.MaxMoney ?? min);
        if (max < min)
        {
            (min, max) = (max, min);
        }

        var roundedMin = CeilAmountToUnit(min, unit);
        var roundedMax = FloorAmountToUnit(max, unit);
        if (roundedMin <= 0.009d)
        {
            roundedMin = Math.Max(0.01d, unit);
        }

        if (roundedMax < roundedMin)
        {
            bounds = default;
            return false;
        }

        if (RequiresNonZeroPrecisionDigit(unit))
        {
            var firstValid = FindValidAmountAtOrAbove(roundedMin, roundedMax, unit);
            var lastValid = FindValidAmountAtOrBelow(roundedMax, roundedMin, unit);
            if (!firstValid.HasValue || !lastValid.HasValue || firstValid.Value > lastValid.Value + 0.009d)
            {
                bounds = default;
                return false;
            }

            roundedMin = firstValid.Value;
            roundedMax = lastValid.Value;
        }

        bounds = new AmountBounds(RoundMoney(roundedMin), RoundMoney(roundedMax), unit);
        return true;
    }

    private static AmountBounds GetRecordAmountBounds(FlowRecord record)
    {
        var unit = GetRecordAmountUnit(record);
        var min = record.ExtraFields.TryGetValue(AmountMinField, out var minValue) && TryParseDouble(minValue, out var parsedMin)
            ? parsedMin
            : 0d;
        var max = record.ExtraFields.TryGetValue(AmountMaxField, out var maxValue) && TryParseDouble(maxValue, out var parsedMax)
            ? parsedMax
            : double.MaxValue;

        min = Math.Max(0, RoundAmountToUnit(min, unit));
        max = double.IsInfinity(max) || max >= double.MaxValue / 2
            ? double.MaxValue
            : Math.Max(min, RoundAmountToUnit(max, unit));
        return new AmountBounds(min, max, unit);
    }

    private static double ClampAmountToBounds(double amount, AmountBounds bounds)
    {
        if (amount <= 0.009d)
        {
            return 0;
        }

        var bounded = Math.Clamp(amount, bounds.Min, bounds.Max);
        bounded = RoundAmountToUnit(bounded, bounds.Unit);
        if (bounded < bounds.Min - 0.009d)
        {
            bounded = bounds.Min;
        }
        else if (bounded > bounds.Max + 0.009d)
        {
            bounded = bounds.Max;
        }

        if (!HasRequiredNonZeroPrecisionDigit(bounded, bounds.Unit))
        {
            var lower = FindValidAmountAtOrBelow(bounded, bounds.Min, bounds.Unit);
            var upper = FindValidAmountAtOrAbove(bounded, bounds.Max, bounds.Unit);
            bounded = (lower, upper) switch
            {
                ({ } lowerValue, { } upperValue) =>
                    bounded - lowerValue <= upperValue - bounded ? lowerValue : upperValue,
                ({ } lowerValue, null) => lowerValue,
                (null, { } upperValue) => upperValue,
                _ => throw new InvalidOperationException(
                    $"金额范围 {bounds.Min:0.##}~{bounds.Max:0.##} 内没有符合位数规则的金额。")
            };
        }

        return RoundMoney(bounded);
    }

    private static bool RequiresNonZeroPrecisionDigit(double unit)
    {
        return unit >= 10d - 0.0000001d;
    }

    private static bool HasRequiredNonZeroPrecisionDigit(double amount, double unit)
    {
        if (!RequiresNonZeroPrecisionDigit(unit))
        {
            return true;
        }

        var precisionPlaceValue = Math.Floor((Math.Abs(amount) + 0.0000001d) / unit);
        var digit = (int)(precisionPlaceValue % 10d);
        return digit is >= 1 and <= 9;
    }

    private static double? FindValidAmountAtOrAbove(double start, double maximum, double unit)
    {
        var candidate = CeilAmountToUnit(start, unit);
        for (var offset = 0; offset <= 10 && candidate <= maximum + 0.009d; offset++, candidate = RoundMoney(candidate + unit))
        {
            if (HasRequiredNonZeroPrecisionDigit(candidate, unit))
            {
                return candidate;
            }
        }

        return null;
    }

    private static double? FindValidAmountAtOrBelow(double start, double minimum, double unit)
    {
        var candidate = FloorAmountToUnit(start, unit);
        for (var offset = 0; offset <= 10 && candidate >= minimum - 0.009d; offset++, candidate = RoundMoney(candidate - unit))
        {
            if (HasRequiredNonZeroPrecisionDigit(candidate, unit))
            {
                return candidate;
            }
        }

        return null;
    }

    private static double MoveAmountAwayFromEdges(double amount, AmountBounds bounds, Random random)
    {
        amount = ClampAmountToBounds(amount, bounds);
        var unit = Math.Max(0.01d, bounds.Unit);
        var range = bounds.Max - bounds.Min;
        if (amount <= 0.009d || range < unit * 6d)
        {
            return amount;
        }

        if (amount <= bounds.Min + 0.009d)
        {
            return ClampAmountToBounds(bounds.Min + range * (0.12d + random.NextDouble() * 0.16d), bounds);
        }

        if (amount >= bounds.Max - 0.009d)
        {
            return ClampAmountToBounds(bounds.Min + range * (0.72d + random.NextDouble() * 0.16d), bounds);
        }

        return amount;
    }

    private static double GetPreferredAmountUpperBound(FlowRecord record, AmountBounds bounds)
    {
        if (double.IsInfinity(bounds.Max)
            || bounds.Max >= double.MaxValue / 2)
        {
            return bounds.Max;
        }

        var unit = Math.Max(0.01d, bounds.Unit);
        var range = bounds.Max - bounds.Min;
        if (range <= 0 || range < unit * 8d)
        {
            return bounds.Max;
        }

        var ratio = IsWideAmountRange(bounds) ? 0.84d : 0.90d;
        var preferred = ClampAmountToBounds(bounds.Min + (range * ratio), bounds);
        return preferred >= bounds.Max - unit - 0.009d ? bounds.Max : preferred;
    }

    private static int GetRequiredMonthlyCount(GenerateReferenceRule rule)
    {
        return Math.Max(0, rule.PercentMonth ?? 0);
    }

    private static bool IsRequiredRule(FlowRuleBase rule)
    {
        return rule is GenerateConstRule
            || (rule is GenerateReferenceRule referenceRule && GetRequiredMonthlyCount(referenceRule) > 0);
    }

    private static bool IsRequiredGeneratedRecord(FlowRecord record)
    {
        return record.ExtraFields.TryGetValue(RequiredOccurrenceField, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static double CeilAmountToUnit(double value, double unit)
    {
        if (unit <= 0)
        {
            return RoundMoney(value);
        }

        return RoundMoney(Math.Ceiling((value - 0.0000001d) / unit) * unit);
    }

    private static double FloorAmountToUnit(double value, double unit)
    {
        if (unit <= 0)
        {
            return RoundMoney(value);
        }

        return RoundMoney(Math.Floor((value + 0.0000001d) / unit) * unit);
    }

    private static double CreateSignedAmount(FlowRuleBase rule, Random random)
    {
        var bounds = GetRuleAmountBounds(rule);
        var amount = bounds.Min + (random.NextDouble() * (bounds.Max - bounds.Min));
        amount = ClampAmountToBounds(amount, bounds);
        amount = MoveAmountAwayFromEdges(amount, bounds, random);
        return IsIncomeRuleSafe(rule) ? amount : -amount;
    }

    private static bool IsIncomeRuleSafe(FlowRuleBase rule)
    {
        var primary = NormalizeRuleText(rule.IncomeAttribute);
        if (ContainsAny(primary, "支出", "出账", "出帳", "借方", "扣款", "付款", "消费", "转出", "轉出", "汇出", "匯出", "鏀嚭"))
        {
            return false;
        }

        if (ContainsAny(primary, "收入", "进账", "進賬", "入账", "入帳", "贷方", "貸方", "收款", "转入", "轉入", "汇入", "匯入", "存入", "鏀跺叆", "杩涜处"))
        {
            return true;
        }

        if ((rule.CreditAmount ?? 0) > 0 && (rule.DebitAmount ?? 0) <= 0)
        {
            return true;
        }

        if ((rule.DebitAmount ?? 0) > 0 && (rule.CreditAmount ?? 0) <= 0)
        {
            return false;
        }

        var secondary = NormalizeRuleText(string.Join(
            " ",
            rule.IncomeType,
            rule.ProductName,
            rule.ProductBrief,
            rule.ProductType,
            rule.Remark,
            rule.Usage,
            rule.TradeExplain));
        if (ContainsAny(secondary, "他行汇入", "他行匯入", "入账", "入帳", "进账", "進賬", "收款", "转入", "轉入", "汇入", "匯入", "存入"))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeRuleText(string? value)
    {
        return string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));
    }

    private static bool ContainsAny(string value, params string[] tokens)
    {
        return tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsIncomeRule(FlowRuleBase rule)
    {
        var value = rule.IncomeAttribute ?? string.Empty;
        return value.Contains("收入", StringComparison.Ordinal)
            || value.Contains("进账", StringComparison.Ordinal)
            || (value.Contains('入') && !value.Contains("支出", StringComparison.Ordinal));
    }

    private static void CopyMatchingProperties(FlowRuleBase rule, FlowRecord record)
    {
        var recordType = typeof(FlowRecord);
        foreach (var property in rule.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (IgnoredRuleProperties.Contains(property.Name)
                || !property.CanRead
                || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var targetProperty = recordType.GetProperty(property.Name, BindingFlags.Instance | BindingFlags.Public);
            if (targetProperty is null
                || !targetProperty.CanWrite
                || targetProperty.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var value = property.GetValue(rule);
            if (value is null)
            {
                continue;
            }

            SetConvertedValue(record, targetProperty, value);
        }
    }

    private static void CopyByMatchingColumnNames(
        FlowRuleBase rule,
        IReadOnlyList<ColumnDefinition> ruleColumns,
        IReadOnlyList<ColumnDefinition> flowColumns,
        FlowRecord record)
    {
        var ruleColumnsByName = ruleColumns
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Field))
            .GroupBy(item => NormalizeName(item.Name!), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var flowColumn in flowColumns.Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Field)))
        {
            if (!ruleColumnsByName.TryGetValue(NormalizeName(flowColumn.Name!), out var sourceColumn))
            {
                continue;
            }

            var value = GetRuleValue(rule, sourceColumn.Field!);
            if (value is null || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.CurrentCulture)))
            {
                continue;
            }

            SetRecordValue(record, flowColumn.Field!, value);
        }
    }

    private static void ApplyRuleColumnAliases(
        Bank bank,
        IReadOnlyList<ColumnDefinition> ruleColumns,
        FlowRuleBase rule,
        FlowRecord record)
    {
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "摘要", "交易摘要", "商品说明"), rule.ProductBrief, record.ProductBrief), "摘要", "交易摘要", "商品说明");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "备注", "附言", "留言", "转账附言", "APP备注"), rule.Remark, record.Remark), "备注", "附言", "留言", "转账附言", "APP备注");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "流水号", "交易流水号", "交易流水", "交易单号", "交易报文号", "核心流水号"), rule.SerialNum, record.SerialNum), "流水号", "交易流水号", "交易流水", "交易单号", "交易报文号", "核心流水号");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "资金渠道", "交易渠道", "渠道"), rule.TradeChannel, record.TradeChannel), "资金渠道", "交易渠道", "渠道");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "交易说明", "注释", "交易描述"), rule.TradeExplain, record.TradeExplain), "交易说明", "注释", "交易描述");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "交易方式", "现转标志", "钞汇", "冲补帐"), rule.CashCheck, record.CashCheck), "交易方式", "现转标志", "钞汇", "冲补帐");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "交易类型", "交易名称", "交易种类", "业务类型"), rule.ProductName, record.ProductName, rule.ProductBrief, record.ProductBrief), "交易类型", "交易名称", "交易种类", "业务类型");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "收支其他", "支付宝分类", "交易分类", "APP交易分类"), rule.IncomeType, rule.Usage, record.Usage, record.IncomeAttribute), "收支其他", "支付宝分类", "交易分类", "APP交易分类");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "交易对方", "对方户名", "户名", "对方名称", "对方姓名", "对方账号名称"), rule.OppositeUsername, record.OppositeUsername), "交易对方", "对方户名", "户名", "对方名称", "对方姓名", "对方账号名称");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "对方账号", "对方账户", "对手账号", "交易对手账号", "对方卡号账号", "对方帐号"), rule.OppositeAccount, record.OppositeAccount), "对方账号", "对方账户", "对手账号", "交易对手账号", "对方卡号账号", "对方帐号");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "对方开户行", "对方银行", "对方行名"), rule.OppositeBank, record.OppositeBank), "对方开户行", "对方银行", "对方行名");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "商家订单号", "商户单号", "商户名称"), rule.MerchantName, record.MerchantName), "商家订单号", "商户单号", "商户名称");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "地点", "交易地点", "交易场所", "交易网点", "交易行所"), rule.TradePlace, record.TradePlace), "地点", "交易地点", "交易场所", "交易网点", "交易行所");
        ApplyColumnValue(bank, record, FirstNonEmpty(GetRuleColumnText(rule, ruleColumns, "用途", "交易用途"), rule.Usage, record.Usage), "用途", "交易用途");
    }

    private static void ApplyRecordColumnAliasBackfill(Bank bank, FlowRecord record)
    {
        ApplyColumnValue(bank, record, record.ProductBrief, "摘要", "交易摘要", "商品说明");
        ApplyColumnValue(bank, record, record.Remark, "备注", "附言", "留言", "转账附言", "APP备注");
        ApplyColumnValue(bank, record, record.SerialNum, "流水号", "交易流水号", "交易流水", "交易单号", "交易报文号", "核心流水号");
        ApplyColumnValue(bank, record, record.TradeChannel, "资金渠道", "交易渠道", "渠道");
        ApplyColumnValue(bank, record, record.TradeExplain, "交易说明", "注释", "交易描述");
        ApplyColumnValue(bank, record, record.CashCheck, "交易方式", "现转标志", "钞汇", "冲补帐");
        ApplyColumnValue(bank, record, record.ProductName, "交易类型", "交易名称", "交易种类", "业务类型");
        ApplyColumnValue(bank, record, record.Usage, "收支其他", "支付宝分类", "交易分类", "APP交易分类", "用途", "交易用途");
        ApplyColumnValue(bank, record, record.OppositeUsername, "交易对方", "对方户名", "户名", "对方名称", "对方姓名", "对方账号名称");
        ApplyColumnValue(bank, record, record.OppositeAccount, "对方账号", "对方账户", "对手账号", "交易对手账号", "对方卡号账号", "对方帐号");
        ApplyColumnValue(bank, record, record.OppositeBank, "对方开户行", "对方银行", "对方行名");
        ApplyColumnValue(bank, record, record.MerchantName, "商家订单号", "商户单号", "商户名称");
        ApplyColumnValue(bank, record, record.TradePlace, "地点", "交易地点", "交易场所", "交易网点", "交易行所");
    }

    private static void ApplyColumnValue(Bank bank, FlowRecord record, string? value, params string[] columnNames)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalizedNames = columnNames
            .Select(NormalizeName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var column in bank.FlowColumns.Where(item =>
                     !string.IsNullOrWhiteSpace(item.Name)
                     && !string.IsNullOrWhiteSpace(item.Field)
                     && normalizedNames.Contains(NormalizeName(item.Name!))))
        {
            if (!string.IsNullOrWhiteSpace(GetRecordStringValue(record, column.Field!)))
            {
                continue;
            }

            SetRecordValue(record, column.Field!, value);
        }
    }

    private static string? GetRuleColumnText(
        FlowRuleBase rule,
        IReadOnlyList<ColumnDefinition> ruleColumns,
        params string[] columnNames)
    {
        var normalizedNames = columnNames
            .Select(NormalizeName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var column in ruleColumns.Where(item =>
                     !string.IsNullOrWhiteSpace(item.Name)
                     && !string.IsNullOrWhiteSpace(item.Field)
                     && normalizedNames.Contains(NormalizeName(item.Name!))))
        {
            var value = GetRuleValue(rule, column.Field!);
            var text = Convert.ToString(value, CultureInfo.CurrentCulture);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static void ApplyInterestFields(FlowRecord record, BankInterestSetting setting)
    {
        foreach (var field in setting.Fields.Where(item => !string.IsNullOrWhiteSpace(item.Field) && !string.IsNullOrWhiteSpace(item.Value)))
        {
            if (IsProtectedInterestSettingField(field.Field))
            {
                continue;
            }

            SetRecordValue(record, field.Field, field.Value);
        }

        if (ShouldApplyLegacyInterestFieldFallback(record))
        {
            record.ProductBrief = InterestText;
        }
    }

    private static void ApplyInterestRowKindLabels(FlowRecord record, string rowKind)
    {
        if (rowKind != InterestTaxRowKind)
        {
            return;
        }

        record.ProductName = ConvertInterestLabelToTax(record.ProductName);
        record.ProductBrief = ConvertInterestLabelToTax(record.ProductBrief);
        record.ProductType = ConvertInterestLabelToTax(record.ProductType);
        record.Usage = ConvertInterestLabelToTax(record.Usage);
        record.TradeExplain = ConvertInterestLabelToTax(record.TradeExplain);
        record.Remark = ConvertInterestLabelToTax(record.Remark);
    }

    private static string ConvertInterestLabelToTax(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim();
        if (text.Contains(InterestTaxText, StringComparison.Ordinal))
        {
            return text;
        }

        if (text.Contains(InterestText, StringComparison.Ordinal))
        {
            return text.Replace(InterestText, InterestTaxText, StringComparison.Ordinal);
        }

        if (string.Equals(text, "\u5229\u606f", StringComparison.Ordinal))
        {
            return InterestTaxText;
        }

        return text;
    }

    private static bool IsProtectedInterestSettingField(string field)
    {
        if (InterestSettingProtectedFields.Contains(field))
        {
            return true;
        }

        return TryGetIndexerField(field, out var indexerField)
            && InterestSettingProtectedFields.Contains(indexerField);
    }

    private static bool ShouldApplyLegacyInterestFieldFallback(FlowRecord record)
    {
        return false;
    }

    private static void ApplyAutoGeneratedSystemFields(FlowAutoGenerationRequest request, FlowRecord record)
    {
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.SerialNum), () => CreateGeneratedSerialNumber(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.MerchantName), () => CreateGeneratedMerchantNumber(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.VoucherNum), () => CreateGeneratedVoucherNumber(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.ReceiptNum), () => CreateGeneratedReceiptNumber(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.Operator), () => CreateGeneratedOperator(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.OperatorNum), () => CreateGeneratedOperatorNumber(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.NetNum), () => CreateGeneratedNetNumber(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.AppNum), () => CreateGeneratedAppNumber(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.SequenceNum), () => CreateGeneratedSequenceNumber(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.AccountNum), () => CreateGeneratedAccountSequence(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.SubAccountNum), () => CreateGeneratedSubAccountSequence(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.TradeCode), () => CreateGeneratedTradeCode(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.AreaNum), () => CreateGeneratedAreaNumber(request, record));
        ApplyGeneratedField(request.Bank, record, nameof(FlowRecord.Year), () => CreateGeneratedYear(record));

        if (HasFlowRecordField(request.Bank, nameof(FlowRecord.LogNum))
            && string.IsNullOrWhiteSpace(record.LogNum))
        {
            record.LogNum = CreateGeneratedLogNumber(request.Bank, record);
        }

        if (HasFlowRecordField(request.Bank, nameof(FlowRecord.Remark)))
        {
            var generatedPostscript = CreateGeneratedPostscript(request.Bank, record);
            if (generatedPostscript is not null)
            {
                record.Remark = generatedPostscript;
            }
        }
    }

    private static void ApplyGeneratedField(Bank bank, FlowRecord record, string field, Func<string?> createValue)
    {
        if (!HasFlowRecordField(bank, field) || !string.IsNullOrWhiteSpace(GetRecordStringValue(record, field)))
        {
            return;
        }

        var value = createValue();
        if (!string.IsNullOrWhiteSpace(value))
        {
            SetRecordValue(record, field, value);
        }
    }

    private static bool HasFlowRecordField(Bank bank, string field)
    {
        return bank.FlowColumns.Any(item => string.Equals(item.Field, field, StringComparison.Ordinal));
    }

    private static bool HasFlowColumnName(Bank bank, string field, params string[] names)
    {
        return bank.FlowColumns.Any(item =>
            string.Equals(item.Field, field, StringComparison.Ordinal)
            && names.Any(name => string.Equals(item.Name, name, StringComparison.Ordinal)));
    }

    private static string GetRecordStringValue(FlowRecord record, string field)
    {
        if (TryGetIndexerField(field, out var indexerField))
        {
            return record[indexerField];
        }

        var property = typeof(FlowRecord).GetProperty(field, BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
        {
            return record[field];
        }

        return property.GetValue(record) switch
        {
            null => string.Empty,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var value => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
        };
    }

    private static string? CreateGeneratedSerialNumber(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var time = record.AccountTime ?? DateTime.Today;
        var seed = CreateRecordSeed(bank, record, 41);

        if (bank.Name.Contains("微信", StringComparison.Ordinal))
        {
            return CreateGeneratedWechatTradeNumber(record, time, seed);
        }

        if (bank.Name.Contains("支付宝", StringComparison.Ordinal))
        {
            return RandomDigits(seed, 8);
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.SerialNum), "交易单号"))
        {
            return $"{time:yyyyMMdd}{RandomDigits(seed, 14)}";
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.SerialNum), "交易流水号", "交易流水", "流水号", "核心流水号"))
        {
            return RandomDigits(seed, 8);
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.SerialNum), "柜员流水", "柜员流水号", "机构柜员流水"))
        {
            return RandomDigits(seed, 9);
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.SerialNum), "交易报文号"))
        {
            return $"{time:yyyyMMddHHmmss}{RandomDigits(seed, 8)}";
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.SerialNum), ExternalSystemFlowColumnName))
        {
            return IsConsumerExpenseRecord(record) ? CreateExternalSystemFlowNumber(record, seed) : null;
        }

        return RandomDigits(seed, 8);
    }

    private static string CreateGeneratedWechatTradeNumber(FlowRecord record, DateTime time, int seed)
    {
        return GetWechatTradeKind(record) switch
        {
            WechatTradeKind.ScanOrPayment => $"1000{RandomDigits(seed, 6)}{time:yyMMdd}{RandomDigits(seed + 17, 18)}",
            WechatTradeKind.Transfer => $"1000{RandomDigits(seed, 6)}{time:yyyyMMdd}{RandomDigits(seed + 17, 13)}",
            WechatTradeKind.GroupRedPacket => $"1000{RandomDigits(seed, 6)}{time:yyMMdd}{RandomDigits(seed + 17, 20)}",
            WechatTradeKind.RedPacket => $"1000{RandomDigits(seed, 6)}{RandomDigits(seed + 17, 21)}",
            _ => $"4200{RandomDigits(seed, 6)}{time:yyyyMMdd}{RandomDigits(seed + 23, 10)}"
        };
    }

    private static string? CreateGeneratedMerchantNumber(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var time = record.AccountTime ?? DateTime.Today;
        var seed = CreateRecordSeed(bank, record, 47);

        if (bank.Name.Contains("微信", StringComparison.Ordinal))
        {
            return GetWechatTradeKind(record) switch
            {
                WechatTradeKind.ScanOrPayment => $"1000{RandomDigits(seed, 6)}{time:yyyyMMdd}{RandomDigits(seed + 19, 12)}",
                WechatTradeKind.Transfer => "/",
                WechatTradeKind.GroupRedPacket => $"1000{RandomDigits(seed, 6)}{time:yyyyMMdd}{RandomDigits(seed + 19, 13)}",
                WechatTradeKind.RedPacket => $"1000{RandomDigits(seed, 6)}{RandomDigits(seed + 19, 21)}",
                _ => $"{time:yyyyMMdd}{RandomDigits(seed, 22)}"
            };
        }

        if (bank.Name.Contains("支付宝", StringComparison.Ordinal))
        {
            return RandomDigits(seed, Math.Abs(seed % 3) == 0 ? 6 : 4);
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.MerchantName), "商户名称"))
        {
            return FirstNonEmpty(record.OppositeUsername, record.TradePlace, record.ProductBrief);
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.MerchantName), "商家订单号", "商户单号"))
        {
            return $"{time:yyyyMMdd}{RandomDigits(seed, 8)}";
        }

        return null;
    }

    private static WechatTradeKind GetWechatTradeKind(FlowRecord record)
    {
        var text = BuildWechatScenarioText(record);

        if (text.Contains("扫二维码", StringComparison.Ordinal)
            || text.Contains("二维码", StringComparison.Ordinal)
            || text.Contains("扫码", StringComparison.Ordinal)
            || text.Contains("面对面收款", StringComparison.Ordinal)
            || text.Contains("微信支付", StringComparison.Ordinal))
        {
            return WechatTradeKind.ScanOrPayment;
        }

        if (text.Contains("群红包", StringComparison.Ordinal))
        {
            return WechatTradeKind.GroupRedPacket;
        }

        if (text.Contains("红包", StringComparison.Ordinal))
        {
            return WechatTradeKind.RedPacket;
        }

        if (text.Contains("转账", StringComparison.Ordinal)
            || text.Contains("提现", StringComparison.Ordinal)
            || text.Contains("零钱充值", StringComparison.Ordinal))
        {
            return WechatTradeKind.Transfer;
        }

        return WechatTradeKind.Merchant;
    }

    private static string BuildWechatScenarioText(FlowRecord record)
    {
        var values = new[]
        {
            record.ProductName,
            record.ProductBrief,
            record.ProductType,
            record.Usage,
            record.TradeExplain,
            record.Remark,
            record.TradePlace,
            record.TradeChannel,
            record.CashCheck,
            record.OppositeUsername,
            record.OppositeBank
        };

        return string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string CreateExternalSystemFlowNumber(FlowRecord record, int seed)
    {
        var time = record.AccountTime ?? DateTime.Today;
        return $"{time:yyyyMMddHHmmss}{RandomDigits(seed, 16)}";
    }

    private static bool IsConsumerExpenseRecord(FlowRecord record)
    {
        if ((record.TradeMoney ?? 0) >= -0.009d)
        {
            return false;
        }

        var nonConsumerText = BuildNonConsumerClassificationText(record);
        if (ContainsAny(nonConsumerText, NonConsumerExpenseKeywords))
        {
            return false;
        }

        var consumerText = BuildConsumerClassificationText(record);
        return ContainsAny(consumerText, ConsumerExpenseKeywords);
    }

    private static string BuildConsumerClassificationText(FlowRecord record)
    {
        return string.Join(" ", new[]
        {
            record.ProductName,
            record.ProductBrief,
            record.ProductType,
            record.Usage,
            record.TradeExplain,
            record.Remark,
            record.TradePlace,
            record.TradeChannel,
            record.OppositeUsername,
            record.MerchantName,
            record.InterfacePage
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildNonConsumerClassificationText(FlowRecord record)
    {
        return string.Join(" ", new[]
        {
            record.ProductName,
            record.ProductBrief,
            record.ProductType,
            record.Usage,
            record.TradeExplain,
            record.Remark,
            record.TradeChannel,
            record.OppositeUsername,
            record.MerchantName
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private enum WechatTradeKind
    {
        Merchant,
        ScanOrPayment,
        Transfer,
        RedPacket,
        GroupRedPacket
    }

    private static string? CreateGeneratedVoucherNumber(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var time = record.AccountTime ?? DateTime.Today;
        var seed = CreateRecordSeed(bank, record, 53);

        if (bank.Name.Contains("中信", StringComparison.Ordinal))
        {
            return $"ECPS{time:yyyyMMddHHmmss}{RandomDigits(seed, 6)}";
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.VoucherNum), ExternalSystemFlowColumnName))
        {
            return IsConsumerExpenseRecord(record) ? CreateExternalSystemFlowNumber(record, seed) : null;
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.VoucherNum), "凭证号", "凭证号码", "凭证", "凭证序号", "凭证号码业务", "票据号", "传票号"))
        {
            return RandomDigits(seed, 8);
        }

        return null;
    }

    private static string? CreateGeneratedReceiptNumber(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var time = record.AccountTime ?? DateTime.Today;
        var seed = CreateRecordSeed(bank, record, 59);

        if (HasFlowColumnName(bank, nameof(FlowRecord.ReceiptNum), "全局路由号", "回单编号"))
        {
            return $"{time:yyyyMMddHHmmss}{RandomDigits(seed, 8)}";
        }

        return null;
    }

    private static string? CreateGeneratedOperator(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var seed = CreateRecordSeed(bank, record, 61);

        if (HasFlowColumnName(bank, nameof(FlowRecord.Operator), "柜员", "业务柜员", "交易柜员", "操作员", "交易操作员"))
        {
            return RandomDigits(seed, 6);
        }

        return null;
    }

    private static string? CreateGeneratedOperatorNumber(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var seed = CreateRecordSeed(bank, record, 67);

        if (HasFlowColumnName(bank, nameof(FlowRecord.OperatorNum), "柜员交易号"))
        {
            return RandomDigits(seed, 9);
        }

        if (HasFlowColumnName(bank, nameof(FlowRecord.OperatorNum), "柜员号", "交易柜员号", "操作员编号"))
        {
            return RandomDigits(seed, 6);
        }

        return null;
    }

    private static string? CreateGeneratedNetNumber(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var seed = CreateRecordSeed(bank, record, 71);

        if (HasFlowColumnName(bank, nameof(FlowRecord.NetNum), "网点号", "机构号", "机构码", "交易机构号", "交易机构", "机构"))
        {
            return RandomDigits(seed, 6);
        }

        return null;
    }

    private static string? CreateGeneratedAppNumber(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var seed = CreateRecordSeed(bank, record, 73);

        if (HasFlowColumnName(bank, nameof(FlowRecord.AppNum), "应用号"))
        {
            return RandomDigits(seed, 5);
        }

        return null;
    }

    private static string? CreateGeneratedSequenceNumber(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var seed = CreateRecordSeed(bank, record, 79);

        if (HasFlowColumnName(bank, nameof(FlowRecord.SequenceNum), "序号", "流水序号"))
        {
            return RandomDigits(seed, 3);
        }

        return null;
    }

    private static string? CreateGeneratedAccountSequence(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;

        if (HasFlowColumnName(bank, nameof(FlowRecord.AccountNum), "账户序号", "账号序号"))
        {
            return "000";
        }

        return null;
    }

    private static string? CreateGeneratedSubAccountSequence(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;

        if (HasFlowColumnName(bank, nameof(FlowRecord.SubAccountNum), "分户序号", "子账户序号"))
        {
            return "0000";
        }

        return null;
    }

    private static string? CreateGeneratedTradeCode(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var seed = CreateRecordSeed(bank, record, 83);

        if (HasFlowColumnName(bank, nameof(FlowRecord.TradeCode), "交易代码", "交易码"))
        {
            return RandomDigits(seed, 3);
        }

        return null;
    }

    private static string? CreateGeneratedAreaNumber(FlowAutoGenerationRequest request, FlowRecord record)
    {
        var bank = request.Bank;
        var seed = CreateRecordSeed(bank, record, 89);

        if (HasFlowColumnName(bank, nameof(FlowRecord.AreaNum), "地区号"))
        {
            return RandomDigits(seed, 4);
        }

        return null;
    }

    private static string CreateGeneratedYear(FlowRecord record)
    {
        return (record.AccountTime ?? DateTime.Today).ToString("yyyy", CultureInfo.InvariantCulture);
    }

    private static string CreateGeneratedLogNumber(Bank bank, FlowRecord record)
    {
        var time = record.AccountTime ?? DateTime.Today;
        var seed = CreateRecordSeed(bank, record, 17);
        var digits = RandomDigits(seed, 9);
        if (time < new DateTime(2024, 3, 15))
        {
            return digits;
        }

        if (time < new DateTime(2024, 8, 15))
        {
            return $"U{digits}";
        }

        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var index = Math.Abs(seed % letters.Length);
        return $"{letters[index]}{digits}";
    }

    private static string? CreateGeneratedPostscript(Bank bank, FlowRecord record)
    {
        if (!bank.Name.Contains("农行", StringComparison.Ordinal)
            && !bank.Name.Contains("农业", StringComparison.Ordinal))
        {
            return null;
        }

        var originalPostscript = record.Remark?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(originalPostscript))
        {
            return string.Empty;
        }

        if (HasGeneratedAgriculturalPostscriptPrefix(originalPostscript))
        {
            return originalPostscript;
        }

        var time = record.AccountTime ?? DateTime.Today;
        var seed = CreateRecordSeed(bank, record, 31);
        var brief = record.ProductBrief?.Trim() ?? string.Empty;
        var channel = record.TradeChannel?.Trim() ?? string.Empty;

        if (brief.Contains("抖音", StringComparison.Ordinal)
            || originalPostscript.Contains("抖音", StringComparison.Ordinal))
        {
            return $"NA{time:yyyyMMdd}{RandomDigits(seed, 23)}{originalPostscript}";
        }

        if (brief.Contains("代付", StringComparison.Ordinal)
            && originalPostscript.Contains("零钱提现", StringComparison.Ordinal))
        {
            return $"NG{time:yyyyMMdd}{RandomDigits(seed, 23)}{originalPostscript}";
        }

        if (IsAgriculturalUaPostscriptRecord(record, originalPostscript, brief, channel))
        {
            return $"UA{time:MMdd}{RandomDigits(seed, 12)}{originalPostscript}";
        }

        return originalPostscript;
    }

    private static bool IsAgriculturalUaPostscriptRecord(FlowRecord record, string postscriptText, string brief, string channel)
    {
        var text = string.Join(" ", new[]
        {
            brief,
            postscriptText,
            channel,
            record.ProductName,
            record.ProductType,
            record.TradeExplain,
            record.OppositeUsername,
            record.OppositeBank,
            record.Usage,
            record.InterfacePage
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return AgriculturalUaPostscriptKeywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
    }

    private static bool HasGeneratedAgriculturalPostscriptPrefix(string value)
    {
        return HasPrefixWithDigitCount(value, "UA", 16)
            || HasPrefixWithDigitCount(value, "NA", 31)
            || HasPrefixWithDigitCount(value, "NG", 31);
    }

    private static bool HasPrefixWithDigitCount(string value, string prefix, int digitCount)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length < prefix.Length + digitCount)
        {
            return false;
        }

        for (var i = prefix.Length; i < prefix.Length + digitCount; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int CreateRecordSeed(Bank bank, FlowRecord record, int salt)
    {
        unchecked
        {
            return HashCode.Combine(
                bank.Id,
                record.AccountTime?.Ticks ?? 0,
                record.TradeMoney ?? 0,
                record.SerialNum,
                record.ProductBrief,
                record.OppositeAccount,
                salt);
        }
    }

    private static object? GetRuleValue(FlowRuleBase rule, string field)
    {
        if (TryGetIndexerField(field, out var indexerField))
        {
            return rule[indexerField];
        }

        var property = rule.GetType().GetProperty(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
        if (property is null)
        {
            return rule[field];
        }

        var value = property.GetValue(rule);
        if (value is null)
        {
            return rule[field];
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return rule[field];
        }

        return value;
    }

    private static void SetRecordValue(FlowRecord record, string field, object value)
    {
        if (TryGetIndexerField(field, out var indexerField))
        {
            record[indexerField] = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            return;
        }

        var property = typeof(FlowRecord).GetProperty(field, BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanWrite)
        {
            record[field] = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            return;
        }

        SetConvertedValue(record, property, value);
    }

    private static void SetConvertedValue(object target, PropertyInfo property, object value)
    {
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        try
        {
            if (targetType == typeof(string))
            {
                property.SetValue(target, Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty);
            }
            else if (targetType == typeof(double) && TryParseDouble(value, out var number))
            {
                property.SetValue(target, number);
            }
            else if (targetType == typeof(int) && TryParseDouble(value, out var intNumber))
            {
                property.SetValue(target, Convert.ToInt32(Math.Round(intNumber, MidpointRounding.AwayFromZero)));
            }
            else if (targetType == typeof(long) && TryParseDouble(value, out var longNumber))
            {
                property.SetValue(target, Convert.ToInt64(Math.Round(longNumber, MidpointRounding.AwayFromZero)));
            }
            else if (targetType == typeof(bool))
            {
                if (value is bool boolean)
                {
                    property.SetValue(target, boolean);
                }
                else if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed))
                {
                    property.SetValue(target, parsed);
                }
            }
            else if (targetType.IsInstanceOfType(value))
            {
                property.SetValue(target, value);
            }
        }
        catch
        {
            // Invalid imported cell values should not break generation.
        }
    }

    private static double CalculateBalanceBefore(IEnumerable<FlowRecord> records, double openingBalance, DateTime time)
    {
        var balance = openingBalance;
        foreach (var item in records
                     .Where(item => item.AccountTime < time)
                     .OrderBy(item => item.AccountTime))
        {
            balance = RoundMoney(balance + (item.TradeMoney ?? 0));
        }

        return balance;
    }

    private static IEnumerable<int> ParseIntTokens(string? value)
    {
        return SplitTokens(value)
            .Select(ParseInt)
            .Where(item => item.HasValue)
            .Select(item => item!.Value);
    }

    private static IEnumerable<DateTime> ParseDateTokens(string? value)
    {
        foreach (var token in SplitTokens(value))
        {
            if (DateTime.TryParse(token, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)
                || DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                yield return date;
            }
        }
    }

    private static IEnumerable<string> SplitTokens(string? value)
    {
        return (value ?? string.Empty)
            .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int? ParseInt(string? value)
    {
        if (int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var number)
            || int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static double? ParseDouble(string? value)
    {
        return TryParseDouble(value ?? string.Empty, out var number) ? number : null;
    }

    private static bool TryParseDouble(object value, out double number)
    {
        var text = Convert.ToString(value, CultureInfo.CurrentCulture)?.Replace(",", string.Empty).Trim() ?? string.Empty;
        return double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out number)
            || double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out number);
    }

    private static bool TryGetIndexerField(string field, out string indexerField)
    {
        if (field.Length >= 2 && field.StartsWith('[') && field.EndsWith(']'))
        {
            indexerField = field[1..^1];
            return true;
        }

        indexerField = string.Empty;
        return false;
    }

    private static string NormalizeName(string value)
    {
        return string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));
    }

    private static DateTime NormalizeEndDate(DateTime end)
    {
        return end.Date.AddDays(1).AddTicks(-1);
    }

    private static double SumIncome(IEnumerable<FlowRecord> records)
    {
        return RoundMoney(records.Where(item => item.TradeMoney > 0).Sum(item => item.TradeMoney ?? 0));
    }

    private static double SumExpense(IEnumerable<FlowRecord> records)
    {
        return RoundMoney(records.Where(item => item.TradeMoney < 0).Sum(item => Math.Abs(item.TradeMoney ?? 0)));
    }

    private static double RoundAmountByFloutLength(double value, int? floutLength)
    {
        return RoundAmountToUnit(value, GetAmountUnit(floutLength));
    }

    private static double GetAmountUnit(int? floutLength)
    {
        var precision = floutLength ?? 0;
        if (precision == 0)
        {
            return 1d;
        }

        if (precision < 0)
        {
            return Math.Pow(10d, Math.Min(Math.Abs(precision), 8));
        }

        return Math.Pow(10d, -Math.Min(precision, 8));
    }

    private static double GetRecordAmountUnit(FlowRecord record)
    {
        if (record.ExtraFields.TryGetValue(AmountUnitField, out var value)
            && TryParseDouble(value, out var unit)
            && unit > 0)
        {
            return unit;
        }

        return 0.01d;
    }

    private static double RoundAmountToUnit(double value, double unit)
    {
        if (unit <= 0)
        {
            return RoundMoney(value);
        }

        return RoundMoney(Math.Round(value / unit, MidpointRounding.AwayFromZero) * unit);
    }

    private static string FormatInvariant(double value)
    {
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static double RoundMoney(double value)
    {
        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return Math.Abs(rounded) <= 0.0000001d ? 0 : rounded;
    }

    private static int CreateSeed(FlowAutoGenerationRequest request, DateTime start, DateTime end)
    {
        return HashCode.Combine(
            request.Bank.Id,
            request.BankUser.Id,
            start.Date,
            end.Date,
            request.Config.SelectIndex,
            request.Config.AllInMoney,
            request.Config.LastMoney);
    }

    private static int CreateRunSeed(FlowAutoGenerationRequest request, DateTime start, DateTime end, int salt = 0)
    {
        return HashCode.Combine(
            CreateSeed(request, start, end),
            Environment.TickCount,
            DateTime.UtcNow.Ticks,
            Random.Shared.Next(),
            salt);
    }

    private static string RandomDigits(int seed, int length)
    {
        var random = new Random(seed);
        return string.Concat(Enumerable.Range(0, length).Select(_ => random.Next(0, 10).ToString()));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
