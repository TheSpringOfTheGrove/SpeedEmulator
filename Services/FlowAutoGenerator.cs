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
    private const string ReferenceSourceKind = "Reference";
    private const string ConstSourceKind = "Const";
    private const string SystemRowKindField = "__GeneratedSystemRowKind";
    private const string InterestRowKind = "Interest";
    private const string InterestTaxRowKind = "InterestTax";
    private const double FinalBalanceTolerance = 1000d;
    private const string ExternalSystemFlowColumnName = "\u5916\u90e8\u7cfb\u7edf\u6d41\u6c34";
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
        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start)
        {
            (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        }

        var openingBalance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
        var random = new Random(CreateRunSeed(request, start, end));
        var records = new List<FlowRecord>();
        var scheduleState = NativeScheduleState.From(records);
        var targetIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney));
        var finalBalanceTarget = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
        var plannedExpense = RoundMoney(Math.Min(targetIncome, Math.Max(0, targetIncome - request.Config.LastMoney)));
        var monthlyPlan = CreateMonthlyAmountPlan(request, start, end, targetIncome, plannedExpense, random);

        GenerateNativeConstRecords(request, start, end, targetIncome, random, records, scheduleState);
        GenerateNativeRequiredReferenceRecords(request, monthlyPlan, targetIncome, random, records, scheduleState);
        GenerateNativeOptionalReferenceRecords(request, monthlyPlan, targetIncome, random, records, scheduleState);
        CompleteNativeConfiguredTotals(request, monthlyPlan, start, end, targetIncome, random, records, scheduleState);

        if (request.BankUser.AutoCalculateInterest)
        {
            GenerateInterestRecords(request, openingBalance, start, end, random, records);
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
        }

        if (records.Count == 0)
        {
            return CreateEmptyResult(openingBalance, request.Config.LastMoney);
        }

        ReconcileNativeGeneratedRecords(records, request, openingBalance, start, end, random);
        if (request.BankUser.AutoCalculateInterest)
        {
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
            ReconcileNativeGeneratedRecords(records, request, openingBalance, start, end, random);
        }

        PruneZeroAmountRecords(records);
        ApplyBalances(records, openingBalance);
        SmoothNativeSchedule(records, request, start, end, random);
        ReconcileNativeGeneratedRecords(records, request, openingBalance, start, end, random);
        BalanceNativeIncomeExpenseRhythm(records, request, start, end, openingBalance, random, requireNonNegativeBalance: false);
        if (!IsWechatBank(request.Bank))
        {
            EnsureLargeIncomeMonthlyPresence(records, request, start, end, openingBalance, random, requireNonNegativeBalance: false);
        }

        ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
        ReducePositiveFinalBalanceSafely(records, request, openingBalance);
        AddSafeExpenseRecordsForPositiveFinalBalance(records, request, start, end, openingBalance, random);
        ReducePositiveFinalBalanceSafely(records, request, openingBalance);
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

        ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
        ReducePositiveFinalBalanceSafely(records, request, openingBalance);
        // SplitNativeMaxAmountRecords(records, request, start, end, openingBalance, random, requireNonNegativeBalance: true);
        BalanceNativeIncomeExpenseRhythm(records, request, start, end, openingBalance, random, requireNonNegativeBalance: true);
        if (!IsWechatBank(request.Bank))
        {
            EnsureLargeIncomeMonthlyPresence(records, request, start, end, openingBalance, random, requireNonNegativeBalance: false);
            ForceNativeNonNegativeBalances(records, request, openingBalance, start, end, random);
            ReducePositiveFinalBalanceSafely(records, request, openingBalance);
            // SplitNativeMaxAmountRecords(records, request, start, end, openingBalance, random, requireNonNegativeBalance: true);
        }
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

    public FlowAutoGenerationResult ApplyNegativeBalanceCorrection(
        FlowAutoGenerationRequest request,
        FlowAutoGenerationResult source)
    {
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

        for (var attempt = 0; attempt < 3; attempt++)
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
        RestoreFinalBalanceAfterDistinctAmounts(records, request, openingBalance);
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
        var maxAttempts = IsWechatBank(request.Bank) ? 3 : 8;
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
                scheduleState);
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
        var maxAttempts = IsWechatBank(request.Bank) ? 8 : 36;
        for (var attempt = 0; attempt < Math.Min(maxAttempts, Math.Max(1, records.Count)); attempt++)
        {
            ApplyBalances(records, openingBalance);
            if (!TryFindFirstNegativeBalance(records, openingBalance, out _, out var minimumBalance)
                || minimumBalance >= -0.009d)
            {
                break;
            }

            var passChanged = MoveNativeIncomeBeforeNegativeBalances(records, request, openingBalance, start, end, random);
            ApplyBalances(records, openingBalance);

            passChanged = MoveNativeRecordsToAvoidNegativeBalances(records, request, openingBalance, start, end, random) || passChanged;
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
        if (IsWechatBank(request.Bank) && records.Count > 1000)
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
        if (IsWechatBank(request.Bank) && records.Count > 1000)
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
        for (var index = records.Count - 1; index >= 0 && remaining > FinalBalanceTolerance; index--)
        {
            var record = records[index];
            if (record.TradeMoney >= -0.009d || IsSystemInterestRecord(record))
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
            if (actualIncrease <= 0.009d || actualIncrease > allowed + 0.009d)
            {
                continue;
            }

            ApplySignedAmount(record, -1d, nextAmount);
            remaining = RoundMoney(remaining - actualIncrease);
            suffixReduction = RoundMoney(suffixReduction + actualIncrease);
            changed = true;
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
                    changed = true;
                    continue;
                }
            }

            records.Remove(record);
            ApplyBalances(records, openingBalance);
        }

        return changed;
    }

    private static void SmoothNativeSchedule(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random)
    {
        DeduplicateNativeTimes(records, request, start, end, random);
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
        var maxPasses = isWechat ? 4 : 10;
        var maxSegmentsPerPass = isWechat ? 32 : 96;
        var maxIncomeBreaksPerRun = isWechat ? 3 : 24;
        var maxTotalMoves = isWechat ? 96 : 360;
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
            var ordered = records
                .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
                .ThenBy(item => item.Index)
                .ToList();
            var expenseRuns = FindExpenseRuns(ordered, maxConsecutiveExpenses)
                .OrderByDescending(run => run.Records.Count)
                .Take(maxSegmentsPerPass)
                .ToList();
            if (expenseRuns.Count == 0)
            {
                break;
            }

            var dayCounts = BuildDayCounts(records, start, end);
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
                            requireNonNegativeBalance)
                        || TrySplitIncomeToDate(
                            records,
                            request,
                            targetDate,
                            dayCounts,
                            openingBalance,
                            rhythmRandom,
                            requireNonNegativeBalance))
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
                        requireNonNegativeBalance))
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
        bool requireNonNegativeBalance)
    {
        var candidates = records
            .Where(item => (item.TradeMoney ?? 0) > 0.009d)
            .Where(IsMovableGeneratedReferenceRecord)
            .Select(item => new
            {
                Record = item,
                Rule = ResolveRecordSourceRule(request, item),
                SourceDate = item.AccountTime!.Value.Date,
                Amount = Math.Abs(item.TradeMoney ?? 0)
            })
            .Where(item => item.Rule is not null)
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
                    candidate.Rule!,
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
        bool requireNonNegativeBalance)
    {
        var candidates = records
            .Where(item => (item.TradeMoney ?? 0) > 0.009d)
            .Where(IsSplittableGeneratedIncomeRecord)
            .Select(item => new
            {
                Record = item,
                Rule = ResolveRecordSourceRule(request, item),
                SourceDate = item.AccountTime!.Value.Date,
                Amount = Math.Abs(item.TradeMoney ?? 0),
                Bounds = GetRecordAmountBounds(item)
            })
            .Where(item => item.Rule is not null)
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
                    candidate.Rule!,
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
        bool requireNonNegativeBalance)
    {
        var runCandidates = run.Records
            .Where(IsMovableGeneratedReferenceRecord)
            .Select(item => new
            {
                Record = item,
                Rule = ResolveRecordSourceRule(request, item),
                SourceDate = item.AccountTime!.Value.Date,
                Amount = Math.Abs(item.TradeMoney ?? 0)
            })
            .Where(item => item.Rule is not null)
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
                        candidate.Rule!,
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
        bool requireNonNegativeBalance)
    {
        if (!record.AccountTime.HasValue)
        {
            return false;
        }

        var originalTime = record.AccountTime.Value;
        var sourceDate = originalTime.Date;
        var state = NativeScheduleState.From(records.Where(item => !ReferenceEquals(item, record)));
        record.AccountTime = PickNativeTimeOnDate(targetDate, rule, random, state, state.NextSequence());
        ApplyBalances(records, openingBalance);
        if (requireNonNegativeBalance && GetMinimumBalance(records, openingBalance) < -0.009d)
        {
            record.AccountTime = originalTime;
            ApplyBalances(records, openingBalance);
            return false;
        }

        if (sourceDate != targetDate)
        {
            dayCounts[sourceDate] = Math.Max(0, dayCounts.GetValueOrDefault(sourceDate) - 1);
            dayCounts[targetDate] = dayCounts.GetValueOrDefault(targetDate) + 1;
        }

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
            && Math.Abs(record.TradeMoney ?? 0) > 0.009d
            && IsGeneratedReferenceRecord(record);
    }

    private static int CountSameDirectionOnDate(IEnumerable<FlowRecord> records, DateTime date, bool isIncome)
    {
        return records.Count(item => item.AccountTime.HasValue
            && item.AccountTime.Value.Date == date.Date
            && (isIncome ? (item.TradeMoney ?? 0) > 0.009d : (item.TradeMoney ?? 0) < -0.009d));
    }

    private static void DeduplicateNativeTimes(
        IEnumerable<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random)
    {
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

            state.Register(record);
        }
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
        var candidates = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .Select(item => new BalanceAdjustmentRule((FlowRuleBase)item, request.Bank.ReferenceColumns))
            .Concat(allowConstRules
                ? request.ConstItems
                    .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
                    .Select(item => new BalanceAdjustmentRule((FlowRuleBase)item, request.Bank.ConstColumns))
                : [])
            .OrderBy(item => IsRequiredRule(item.Rule) ? 1 : 0)
            .ThenBy(item => GetRuleAmountBounds(item.Rule).Min)
            .ThenByDescending(item => GetRuleAmountBounds(item.Rule).Max)
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

    private static void GenerateNativeConstRecords(
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double incomeCap,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState)
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

                    if (!TryCreateNativeUniqueAmount(rule, isIncome ? availableIncome : double.MaxValue, random, preferLower: false, records, isIncome, out var amount))
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
        NativeScheduleState scheduleState)
    {
        var requiredRules = request.References
            .Where(item => item.IsCheck && GetRequiredMonthlyCount(item) > 0)
            .ToList();
        if (requiredRules.Count == 0)
        {
            return;
        }

        for (var monthIndex = 0; monthIndex < monthlyPlan.Months.Count; monthIndex++)
        {
            var month = monthlyPlan.Months[monthIndex];
            foreach (var rule in requiredRules)
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
                    var available = IsWideAmountRange(bounds)
                        ? Math.Min(availableTotal, bounds.Max)
                        : Math.Min(availableTotal, remainingMonthTarget > 0 ? Math.Max(remainingMonthTarget, bounds.Min) : availableTotal);

                    if (!TryCreateNativeUniqueAmount(rule, available, random, preferLower: false, records, isIncome, out var amount))
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
        NativeScheduleState scheduleState)
    {
        var optionalIncomeRules = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) && GetRequiredMonthlyCount(item) <= 0)
            .OrderBy(item => GetRuleAmountBounds(item).Min)
            .ThenBy(_ => random.Next())
            .ToList();
        var optionalExpenseRules = request.References
            .Where(item => item.IsCheck && !IsIncomeRuleSafe(item) && GetRequiredMonthlyCount(item) <= 0)
            .OrderBy(item => GetRuleAmountBounds(item).Min)
            .ThenBy(_ => random.Next())
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
                scheduleState);

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
                scheduleState);
        }
    }

    private static void CompleteNativeConfiguredTotals(
        FlowAutoGenerationRequest request,
        MonthlyAmountPlan monthlyPlan,
        DateTime start,
        DateTime end,
        double targetIncome,
        Random random,
        ICollection<FlowRecord> records,
        NativeScheduleState scheduleState)
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
            scheduleState);

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
            scheduleState);
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
        NativeScheduleState scheduleState)
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

            IncreaseSignedRecordsWithinBounds(records, isIncome, remaining);
            remaining = RoundMoney(targetTotal - GetNativeSignedTotal(records, isIncome));
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
                scheduleState);

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

        return rules
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
        NativeScheduleState scheduleState)
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
                scheduleState);
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
            scheduleState);
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
        NativeScheduleState scheduleState)
    {
        var availableRules = rules
            .Where(item => GetRequiredMonthlyCount(item) <= 0)
            .ToList();
        var remaining = RoundMoney(Math.Max(0, targetAmount));
        if (availableRules.Count == 0 || remaining <= 0.009d)
        {
            return;
        }

        var targetCount = CalculateNativeOptionalCount(availableRules, monthStart, monthEnd, remaining, isIncome);
        var created = 0;
        var cursor = 0;
        var guard = Math.Max(availableRules.Count * Math.Max(1, targetCount) * 3, 24);
        var incomeTotal = isIncome ? SumIncome(records) : 0d;
        var preferLowerIncome = ShouldPreferLowerOptionalIncome(availableRules, isIncome);
        while (remaining > 0.009d && created < targetCount && cursor < guard)
        {
            var rule = availableRules[cursor % availableRules.Count];
            cursor++;

            if (isIncome)
            {
                remaining = Math.Min(remaining, RoundMoney(Math.Max(0, incomeCap - incomeTotal)));
                if (remaining <= 0.009d)
                {
                    break;
                }
            }

            var slotsLeft = Math.Max(1, targetCount - created);
            var budget = CalculateNativePerRecordBudget(rule, remaining, slotsLeft, random);
            if (!TryCreateNativeUniqueAmount(rule, budget, random, preferLower: preferLowerIncome, records, isIncome, out var amount))
            {
                continue;
            }

            if (amount > remaining + 0.009d)
            {
                continue;
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
            scheduleState.Register(record);
            remaining = RoundMoney(remaining - amount);
            if (isIncome)
            {
                incomeTotal = RoundMoney(incomeTotal + amount);
            }

            created++;
        }
    }

    private static int CalculateNativeOptionalCount(
        IReadOnlyList<GenerateReferenceRule> rules,
        DateTime monthStart,
        DateTime monthEnd,
        double targetAmount,
        bool isIncome)
    {
        if (targetAmount <= 0.009d || rules.Count == 0)
        {
            return 0;
        }

        var average = Math.Max(0.01d, EstimateAverageAmount(rules));

        var largest = rules.Select(item => GetRuleAmountBounds(item).Max).DefaultIfEmpty(average).Max();
        var dayCount = Math.Max(1, (NormalizeEndDate(monthEnd).Date - monthStart.Date).Days + 1);
        var byAverage = (int)Math.Ceiling(targetAmount / average);
        var byLargest = (int)Math.Ceiling(targetAmount / Math.Max(0.01d, largest));
        var softDailyLimit = dayCount <= 3 ? 12 : dayCount <= 10 ? 8 : 5;
        var maxCount = Math.Max(byLargest, dayCount * softDailyLimit);
        return Math.Clamp(Math.Max(byAverage, byLargest), 1, Math.Max(1, maxCount));
    }

    private static double CalculateNativePerRecordBudget(
        FlowRuleBase rule,
        double remaining,
        int slotsLeft,
        Random random)
    {
        var bounds = GetRuleAmountBounds(rule);
        if (remaining <= bounds.Min + 0.009d || slotsLeft <= 1)
        {
            return remaining;
        }

        if (IsWideAmountRange(bounds))
        {
            return Math.Min(remaining, bounds.Max);
        }

        var target = remaining / slotsLeft;
        var variedTarget = target * (0.72d + random.NextDouble() * 0.72d);
        return Math.Min(remaining, Math.Max(bounds.Min, Math.Min(bounds.Max, variedTarget)));
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
        if (ShouldPreferFractionalAmount(bounds) && IsWholeAmount(amount))
        {
            amount = PreferNonWholeAmount(amount, new AmountBounds(bounds.Min, max, bounds.Unit), random);
        }

        return amount >= bounds.Min - 0.009d && amount <= max + 0.009d;
    }

    private static bool TryCreateNativeUniqueAmount(
        FlowRuleBase rule,
        double budget,
        Random random,
        bool preferLower,
        IEnumerable<FlowRecord> existingRecords,
        bool isIncome,
        out double amount)
    {
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

    private static double CreateAmountBucketRatio(Random random)
    {
        var bucket = random.NextDouble();
        if (bucket < 0.15d)
        {
            return random.NextDouble() * 0.22d;
        }

        if (bucket < 0.85d)
        {
            return 0.35d + (random.NextDouble() * 0.25d);
        }

        return 0.67d + (random.NextDouble() * 0.33d);
    }

    private static bool IsWideAmountRange(AmountBounds bounds)
    {
        return bounds.Min >= 1000d && bounds.Max >= bounds.Min * 4d;
    }

    private static double PreferNonWholeAmount(double amount, AmountBounds bounds, Random random)
    {
        if (!ShouldPreferFractionalAmount(bounds) || !IsWholeAmount(amount))
        {
            return amount;
        }

        var unit = Math.Max(0.01d, bounds.Unit);
        var lower = ClampAmountToBounds(amount - unit, bounds);
        var upper = ClampAmountToBounds(amount + unit, bounds);
        var lowerValid = !IsWholeAmount(lower) && lower >= bounds.Min - 0.009d;
        var upperValid = !IsWholeAmount(upper) && upper <= bounds.Max + 0.009d;
        if (lowerValid && upperValid)
        {
            return random.Next(0, 2) == 0 ? lower : upper;
        }

        if (upperValid)
        {
            return upper;
        }

        return lowerValid ? lower : amount;
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
        var preferFractional = ShouldPreferFractionalAmount(bounds) && IsWholeAmount(amount);
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

        return new MonthlyAmountPlan
        {
            Months = months,
            IncomeTargets = CreateMonthlyTargets(request.Config, months, targetIncome, true, random, incomeRules),
            ExpenseTargets = CreateMonthlyTargets(request.Config, months, targetExpense, false, random, expenseRules)
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
            records.Add(CreateInterestRecord(request, setting, time, interest, InterestRowKind, "结息", records));

            if (ShouldAppendInterestTaxRecord(request.Bank))
            {
                records.Add(CreateInterestRecord(request, setting, time, 0, InterestTaxRowKind, "利息税", records));
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
        record.Remark = "个人活期结息";
        record.SerialNum = rowKind == InterestTaxRowKind ? "0000000002" : "0000000001";
        record.LogNum = "0000000001";
        record.ExtraFields[AmountUnitField] = "0.01";
        record.ExtraFields[SystemRowKindField] = rowKind;

        ApplyInterestFields(record, setting);
        record.ProductBrief = brief;
        ApplyInterestRecordDefaults(request, record, rowKind, existingRecords);
        if (string.IsNullOrWhiteSpace(record.Remark) || record.Remark == "结息")
        {
            record.Remark = "个人活期结息";
        }

        if (string.IsNullOrWhiteSpace(record.LogNum))
        {
            record.LogNum = "0000000001";
        }

        ApplyUserAccountToRecord(request, record);
        ApplyRecordColumnAliasBackfill(request.Bank, record);
        ApplyAutoGeneratedSystemFields(request, record);
        return record;
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
        var settlementDate = settlementTime.Date;
        var dayAmounts = records
            .Where(item => item.AccountTime.HasValue && item.AccountTime.Value.Date <= settlementDate)
            .GroupBy(item => item.AccountTime!.Value.Date)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => item.TradeMoney ?? 0));

        if (!dayAmounts.ContainsKey(settlementDate))
        {
            dayAmounts[settlementDate] = 0;
        }

        var orderedDays = dayAmounts
            .OrderBy(item => item.Key)
            .ToList();
        if (orderedDays.Count == 0)
        {
            return 0;
        }

        var firstDate = orderedDays[0].Key;
        var productStart = start.Date < firstDate ? start.Date : firstDate;
        var dailyProduct = openingBalance * (firstDate - productStart).Days;
        var balance = openingBalance + orderedDays[0].Value;

        for (var index = 1; index < orderedDays.Count; index++)
        {
            var current = orderedDays[index];
            var previous = orderedDays[index - 1];
            dailyProduct += balance * (current.Key - previous.Key).Days;
            balance += current.Value;
            if (current.Key >= settlementDate)
            {
                return RoundMoney(Math.Max(0, dailyProduct) * ratePercent / 36500d);
            }
        }

        return 0;
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
        var record = CreateBaseRecord(request, accountTime, amount);
        CopyMatchingProperties(rule, record);
        CopyByMatchingColumnNames(rule, ruleColumns, request.Bank.FlowColumns, record);
        ApplyRuleColumnAliases(request.Bank, ruleColumns, rule, record);

        record.AccountTime = accountTime;
        record.TradeMoney = amount;
        record.ExtraFields[AmountUnitField] = FormatInvariant(GetAmountUnit(rule.FloutLength));
        var amountBounds = GetRuleAmountBounds(rule);
        record.ExtraFields[AmountMinField] = FormatInvariant(amountBounds.Min);
        record.ExtraFields[AmountMaxField] = FormatInvariant(amountBounds.Max);
        record.ExtraFields[SourceKindField] = rule is GenerateConstRule ? ConstSourceKind : ReferenceSourceKind;
        record.ExtraFields[SourceIndexField] = rule.Index.ToString(CultureInfo.InvariantCulture);
        record.ExtraFields[RequiredOccurrenceField] = IsRequiredRule(rule) ? "true" : "false";
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

    private static void PruneZeroAmountRecords(List<FlowRecord> records)
    {
        records.RemoveAll(item => !IsInterestTaxRecord(item)
            && Math.Abs(RoundMoney(item.TradeMoney ?? 0)) <= 0.009d);
    }

    private static bool IsRecordInRange(FlowRecord record, DateTime start, DateTime end)
    {
        var accountTime = record.AccountTime;
        return accountTime.HasValue && accountTime.Value >= start && accountTime.Value <= end;
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
            min = Math.Max(0.01d, average * 0.35d);
            max = Math.Max(min, average * 1.9d);
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
        var min = Math.Max(0.01d, average * 0.12d);
        var max = Math.Max(min, average * 2.8d);
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
                    ? 2.25d + random.NextDouble() * 1.75d
                    : 2.7d + random.NextDouble() * 2.2d;
            }

            foreach (var index in PickDistinctIndexes(monthCount, Math.Max(1, monthCount / 4), random))
            {
                targets[index] *= 0.08d + random.NextDouble() * 0.28d;
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
        if (ratio < 0.28d)
        {
            return 0.10d + random.NextDouble() * 0.35d;
        }

        if (ratio < 0.78d)
        {
            return 0.55d + random.NextDouble() * 0.85d;
        }

        return isIncome
            ? 1.8d + random.NextDouble() * 2.1d
            : 2.1d + random.NextDouble() * 2.4d;
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
        var units = new double[signedRecords.Count];
        var activeIndexes = Enumerable.Range(0, signedRecords.Count).ToList();

        for (var index = 0; index < signedRecords.Count; index++)
        {
            var record = signedRecords[index];
            var bounds = GetRecordAmountBounds(record);
            units[index] = bounds.Unit;
            mins[index] = bounds.Min;
            maxs[index] = Math.Max(bounds.Min, bounds.Max);
            amounts[index] = ClampAmountToBounds(Math.Abs(record.TradeMoney ?? 0), bounds);
        }

        RemoveOptionalRecordsForTarget(signedRecords, amounts, mins, maxs, activeIndexes, targetTotal);

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
            var preferFractional = ShouldPreferFractionalAmount(bounds) && IsWholeAmount(amount);
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

            var delta = RoundMoney(candidate - amount);
            var score = Math.Abs(RoundMoney(delta - preferredDelta));
            var distance = Math.Abs(delta);
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

        var rules = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
            .OrderBy(item => GetRequiredMonthlyCount(item) > 0 ? 1 : 0)
            .ThenBy(_ => random.Next())
            .ToList();
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

            amount = ClampAmountToBounds(amount, bounds);
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
                    ApplySignedAmount(record, sign, ClampAmountToBounds(partialNext, bounds));
                    remaining = 0;
                    break;
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
            ApplySignedAmount(record, sign, ClampAmountToBounds(absolute - reduction, bounds));
            remaining = RoundMoney(remaining - reduction);
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
        foreach (var record in GetSignedRecords(records, isIncome)
                     .OrderBy(item => IsRequiredGeneratedRecord(item) ? 1 : 0)
                     .ThenBy(item => GetRecordAmountUnit(item))
                     .ThenByDescending(item => item.AccountTime ?? DateTime.MinValue))
        {
            if (remaining <= 0.009d)
            {
                break;
            }

            var absolute = Math.Abs(record.TradeMoney ?? 0);
            var bounds = GetRecordAmountBounds(record);
            var room = RoundMoney(bounds.Max - absolute);
            if (room <= 0.009d)
            {
                continue;
            }

            var increase = Math.Min(room, remaining);
            ApplySignedAmount(record, sign, ClampAmountToBounds(absolute + increase, bounds));
            remaining = RoundMoney(remaining - increase);
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

    private static AmountBounds GetRuleAmountBounds(FlowRuleBase rule)
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
            roundedMax = roundedMin;
        }

        return new AmountBounds(RoundMoney(roundedMin), RoundMoney(roundedMax), unit);
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

        return RoundMoney(bounded);
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
            SetRecordValue(record, field.Field, field.Value);
        }

        if (string.IsNullOrWhiteSpace(record.ProductBrief))
        {
            record.ProductBrief = "结息";
        }
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
