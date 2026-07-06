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
    private const string ReferenceSourceKind = "Reference";
    private const string ConstSourceKind = "Const";
    private const string SystemRowKindField = "__GeneratedSystemRowKind";
    private const string InterestRowKind = "Interest";
    private const string InterestTaxRowKind = "InterestTax";
    private const double FinalBalanceTolerance = 10000d;
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
        var random = new Random(CreateSeed(request, start, end));
        var records = new List<FlowRecord>();
        var targetIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney));
        var finalBalanceTarget = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
        var plannedExpense = RoundMoney(Math.Max(0, openingBalance + targetIncome - finalBalanceTarget));
        var monthlyPlan = CreateMonthlyAmountPlan(request, start, end, targetIncome, plannedExpense, random);

        GenerateConstRecords(request, start, end, random, records);
        GenerateReferenceRecords(request, start, end, random, records, monthlyPlan);

        if (request.BankUser.AutoCalculateInterest)
        {
            GenerateInterestRecords(request, openingBalance, start, end, random, records);
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
        }

        if (records.Count == 0)
        {
            return CreateEmptyResult(openingBalance, request.Config.LastMoney);
        }

        NormalizeIncomeTotal(records, request, start, end, random, monthlyPlan);
        if (request.BankUser.AutoCalculateInterest)
        {
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
            NormalizeIncomeTotal(records, request, start, end, random, monthlyPlan);
        }

        var targetExpense = CalculateTargetExpense(openingBalance, records, request.Config.LastMoney);
        if (targetExpense < 0)
        {
            return BuildResult(records, openingBalance, request.Config.LastMoney, true, openingBalance);
        }

        NormalizeExpenseTotal(records, targetExpense, request, start, end, random, monthlyPlan);
        if (request.BankUser.AutoCalculateInterest)
        {
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
            targetExpense = CalculateTargetExpense(openingBalance, records, request.Config.LastMoney);
            NormalizeExpenseTotal(records, targetExpense, request, start, end, random, monthlyPlan);
        }

        PruneZeroAmountRecords(records);
        ApplyBalances(records, openingBalance);
        BringFinalBalanceWithinTolerance(records, request, openingBalance);
        PruneZeroAmountRecords(records);
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
        ApplyBalances(records, openingBalance);
        BringFinalBalanceWithinTolerance(records, request, openingBalance);

        PruneZeroAmountRecords(records);
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
        var random = new Random(CreateSeed(request, start, end));

        if (request.BankUser.AutoCalculateInterest)
        {
            GenerateInterestRecords(request, openingBalance, start, end, random, records);
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
        }

        PruneZeroAmountRecords(records);
        ApplyBalances(records, openingBalance);
        BringFinalBalanceWithinTolerance(records, request, openingBalance);

        if (request.BankUser.AutoCalculateInterest)
        {
            RecalculateInterestRecords(records, openingBalance, start, request.InterestSetting);
        }

        PruneZeroAmountRecords(records);
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
        var random = new Random(CreateSeed(request, start, end) ^ records.Count ^ 0x6C8E9CF5);
        for (var attempt = 0; attempt < 18; attempt++)
        {
            PruneZeroAmountRecords(records);
            ApplyBalances(records, openingBalance);

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
        ApplyBalances(records, openingBalance);
    }

    private static bool TryFindFirstNegativeBalance(
        IEnumerable<FlowRecord> records,
        double openingBalance,
        out DateTime firstNegativeTime,
        out double minimumBalance)
    {
        firstNegativeTime = default;
        minimumBalance = RoundMoney(openingBalance);
        foreach (var record in records
                     .OrderBy(item => item.AccountTime ?? DateTime.MinValue)
                     .ThenBy(item => item.Index))
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

        var candidates = request.References
            .Where(item => item.IsCheck && IsIncomeRuleSafe(item) == isIncome)
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
        while (remaining > FinalBalanceTolerance && sequence < 2000)
        {
            var candidate = PickBalanceAdjustmentRule(candidates, remaining);
            if (candidate is null)
            {
                break;
            }

            var bounds = GetRuleAmountBounds(candidate.Rule);
            var amount = remaining >= bounds.Min
                ? Math.Min(remaining, bounds.Max)
                : bounds.Min;
            amount = ClampAmountToBounds(amount, bounds);
            if (amount <= 0.009d)
            {
                break;
            }

            var accountTime = PickAdjustmentTime(start, end, candidate.Rule, random);
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

        return NormalizeRawTargets(rawTargets, targetTotal);
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
            MoveAmountsTowardTarget(amounts, mins, maxs, units, activeIndexes, boundedTarget, decrease: true);
        }
        else if (currentTotal < boundedTarget - 0.009d)
        {
            MoveAmountsTowardTarget(amounts, mins, maxs, units, activeIndexes, boundedTarget, decrease: false);
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

    private static void MoveAmountsTowardTarget(
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
                .OrderByDescending(index => decrease
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
        record.TradeMoney = sign > 0 ? amount : -amount;
        record.IncomeAttribute = sign > 0 ? "收入" : "支出";
        record.CreditAmount = sign > 0 && amount > 0 ? amount : null;
        record.DebitAmount = sign < 0 && amount > 0 ? amount : null;
        record.IncomeFlag = sign > 0 ? "C" : "D";
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
            .OrderBy(index => GetRecordAmountUnit(records[index]))
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

        var random = new Random(CreateSeed(request, start, end) ^ records.Count ^ 0x5F3759DF);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ApplyBalances(records, openingBalance);
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
        var remaining = RoundMoney(amount);
        var netBefore = CalculateNetAmount(records);
        IncreaseSignedRecordsWithinBounds(records, isIncome: false, remaining);
        remaining = RoundMoney(amount - (netBefore - CalculateNetAmount(records)));
        if (remaining <= 0.009d)
        {
            return true;
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
            1000);
        for (var index = 0; index < maxAttempts && remaining > 0.009d; index++)
        {
            var rule = rules[index % rules.Count];
            var bounds = GetRuleAmountBounds(rule);
            var maxAcceptable = isIncome
                ? remaining
                : RoundMoney(remaining + (FinalBalanceTolerance * 2));
            if (bounds.Min > maxAcceptable + 0.009d)
            {
                continue;
            }

            var amount = Math.Min(remaining, bounds.Max);
            if (amount < bounds.Min - 0.009d)
            {
                if (isIncome)
                {
                    continue;
                }

                amount = bounds.Min;
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
                PickAdjustmentTime(start, end, rule, random),
                isIncome ? amount : -amount));
            remaining = RoundMoney(remaining - amount);
        }
    }

    private static DateTime PickAdjustmentTime(DateTime start, DateTime end, FlowRuleBase rule, Random random)
    {
        var windowStart = end.Date.AddDays(-2);
        if (windowStart < start)
        {
            windowStart = start;
        }

        return PickTime(windowStart, end, rule, random);
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
                var partialNext = RoundMoney(absolute - remaining);
                if (partialNext >= bounds.Min - 0.009d)
                {
                    ApplySignedAmount(record, sign, ClampAmountToBounds(partialNext, bounds));
                    remaining = 0;
                    break;
                }

                ApplySignedAmount(record, sign, 0);
                remaining = RoundMoney(remaining - absolute);
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

    private static bool IncreaseSignedRecordsWithinBounds(IEnumerable<FlowRecord> records, bool isIncome, double amount)
    {
        var remaining = RoundMoney(amount);
        var sign = isIncome ? 1d : -1d;
        foreach (var record in GetSignedRecords(records, isIncome)
                     .OrderBy(item => GetRecordAmountUnit(item))
                     .ThenByDescending(item => IsRequiredGeneratedRecord(item))
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
        records.Sort((left, right) =>
        {
            var result = Nullable.Compare(left.AccountTime, right.AccountTime);
            return result != 0 ? result : string.CompareOrdinal(left.SerialNum, right.SerialNum);
        });

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

    private static FlowAutoGenerationResult BuildResult(
        List<FlowRecord> records,
        double openingBalance,
        double lastMoney,
        bool requiresCorrection,
        double requiredOpeningBalance)
    {
        PruneZeroAmountRecords(records);
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
        return Math.Max(0, rule.PercentMonth ?? 1);
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
