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
    private static readonly string[] TokenSeparators = [",", ";", ":", "，", "；", "：", "|", "、", " "];

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

        GenerateReferenceRecords(request, start, end, random, records);
        GenerateConstRecords(request, start, end, random, records);

        if (request.BankUser.AutoCalculateInterest)
        {
            GenerateInterestRecords(request, openingBalance, start, end, random, records);
        }

        if (records.Count == 0)
        {
            return CreateEmptyResult(openingBalance, request.Config.LastMoney);
        }

        NormalizeIncomeTotal(records, request, start, end, random);
        var targetExpense = CalculateTargetExpense(openingBalance, records, request.Config.LastMoney);
        if (targetExpense < 0)
        {
            return BuildResult(records, openingBalance, request.Config.LastMoney, true, request.Config.LastMoney - SumIncome(records));
        }

        NormalizeExpenseTotal(records, targetExpense, request, start, end, random);
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

        if (Math.Abs(finalBalance - request.Config.LastMoney) > 0.009d)
        {
            requiredOpeningBalance = Math.Max(
                requiredOpeningBalance,
                RoundMoney(expenseTotal + request.Config.LastMoney - incomeTotal));
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

        ApplyBalances(records, openingBalance);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var finalDelta = RoundMoney(finalBalance - request.Config.LastMoney);

        if (Math.Abs(finalDelta) > 0.009d)
        {
            if (finalDelta > 0)
            {
                records.Add(CreateBalancingRecord(
                    request,
                    PickFinalBalancingTime(records, request.Config),
                    -finalDelta,
                    "余额修正"));
            }
            else if (!ReduceExpenseForFinalBalance(records, Math.Abs(finalDelta)))
            {
                records.Add(CreateBalancingRecord(
                    request,
                    PickFinalBalancingTime(records, request.Config),
                    Math.Abs(finalDelta),
                    "余额修正"));
            }
        }

        ApplyBalances(records, openingBalance);

        var incomeTotal = SumIncome(records);
        var expenseTotal = SumExpense(records);
        finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimumBalance = records.Select(item => item.Balance ?? openingBalance).Append(openingBalance).Min();
        var requiresCorrection = minimumBalance < -0.009d || Math.Abs(finalBalance - request.Config.LastMoney) > 0.009d;
        var requiredOpeningBalance = requiresCorrection
            ? RoundMoney(Math.Max(openingBalance - minimumBalance, expenseTotal + request.Config.LastMoney - incomeTotal))
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

    private static void GenerateReferenceRecords(
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random,
        ICollection<FlowRecord> records)
    {
        var selectedRules = request.References.Where(item => item.IsCheck).ToList();
        if (selectedRules.Count == 0)
        {
            return;
        }

        foreach (var month in EnumerateMonths(start, end))
        {
            foreach (var rule in selectedRules)
            {
                var count = Math.Max(0, rule.PercentMonth ?? 1);
                for (var index = 0; index < count; index++)
                {
                    var amount = CreateSignedAmount(rule, random);
                    if (Math.Abs(amount) < 0.009d)
                    {
                        continue;
                    }

                    records.Add(CreateRecordFromRule(
                        request,
                        rule,
                        request.Bank.ReferenceColumns,
                        PickTime(month.Start, month.End, rule, random),
                        amount));
                }
            }
        }
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
                        PickTime(day, day, rule, random).AddMinutes(index),
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

            var balanceBeforeInterest = CalculateBalanceBefore(records, openingBalance, time);
            var interest = RoundMoney(Math.Max(0, balanceBeforeInterest) * (ratePercent.Value / 100d));
            if (interest <= 0)
            {
                continue;
            }

            var record = CreateBaseRecord(request, time, interest);
            record.ProductBrief = "结息";
            record.Remark = "结息";
            record.CashCheck = "转账";
            record.TradeChannel = request.Bank.Name == "支付宝" ? "电子商务" : "柜面";
            ApplyInterestFields(record, setting);
            records.Add(record);
        }
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

        record.AccountTime = accountTime;
        record.TradeMoney = amount;
        record.ExtraFields[AmountUnitField] = FormatInvariant(GetAmountUnit(rule.FloutLength));
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

        if (string.IsNullOrWhiteSpace(record.Account))
        {
            record.Account = request.BankUser.AccountNo;
        }

        if (string.IsNullOrWhiteSpace(record.SerialNum))
        {
            record.SerialNum = RandomDigits(accountTime.Millisecond + accountTime.Second + record.GetHashCode(), 8);
        }

        return record;
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
            Account = request.BankUser.AccountNo,
            Currency = FirstNonEmpty(request.BankUser.Currency, "RMB"),
            TradeCurrency = FirstNonEmpty(request.BankUser.Currency, "RMB"),
            CreditAmount = amount > 0 ? RoundMoney(amount) : null,
            DebitAmount = amount < 0 ? RoundMoney(Math.Abs(amount)) : null,
            IncomeFlag = amount >= 0 ? "C" : "D"
        };
    }

    private static void NormalizeIncomeTotal(
        List<FlowRecord> records,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random)
    {
        var targetIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney));
        var incomeRecords = records.Where(item => item.TradeMoney > 0).ToList();
        var currentIncome = SumIncome(records);

        if (targetIncome <= 0)
        {
            RebalanceSignedRecords(incomeRecords, 0);
            return;
        }

        if (incomeRecords.Count > 0)
        {
            RebalanceSignedRecords(incomeRecords, targetIncome);
            return;
        }

        records.Add(CreateBalancingRecord(request, PickTime(start, end, null, random), targetIncome, "收入补足"));
    }

    private static void NormalizeExpenseTotal(
        List<FlowRecord> records,
        double targetExpense,
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        Random random)
    {
        targetExpense = RoundMoney(Math.Max(0, targetExpense));
        var expenseRecords = records.Where(item => item.TradeMoney < 0).ToList();
        var currentExpense = SumExpense(records);

        if (targetExpense <= 0)
        {
            RebalanceSignedRecords(expenseRecords, 0);
            return;
        }

        if (expenseRecords.Count > 0)
        {
            RebalanceSignedRecords(expenseRecords, targetExpense);
            return;
        }

        records.Add(CreateBalancingRecord(request, PickTime(start, end, null, random), -targetExpense, "支出补足"));
    }

    private static void RebalanceSignedRecords(IReadOnlyList<FlowRecord> signedRecords, double targetTotal)
    {
        if (signedRecords.Count == 0)
        {
            return;
        }

        targetTotal = RoundMoney(Math.Max(0, targetTotal));
        var sign = signedRecords.Any(item => item.TradeMoney < 0) ? -1d : 1d;
        var currentTotal = signedRecords.Sum(item => Math.Abs(item.TradeMoney ?? 0));
        var amounts = new double[signedRecords.Count];
        var runningTotal = 0d;

        for (var index = 0; index < signedRecords.Count; index++)
        {
            var record = signedRecords[index];
            var absolute = Math.Abs(record.TradeMoney ?? 0);
            var unit = GetRecordAmountUnit(record);
            var amount = index == signedRecords.Count - 1
                ? RoundAmountToUnit(targetTotal - runningTotal, unit)
                : currentTotal <= 0
                    ? RoundAmountToUnit(targetTotal / signedRecords.Count, unit)
                    : RoundAmountToUnit(targetTotal * (absolute / currentTotal), unit);

            if (amount < 0)
            {
                amount = 0;
            }

            runningTotal = RoundMoney(runningTotal + amount);
            amounts[index] = amount;
        }

        AdjustRoundedAmountsToTarget(signedRecords, amounts, targetTotal);

        for (var index = 0; index < signedRecords.Count; index++)
        {
            var record = signedRecords[index];
            var amount = Math.Max(0, amounts[index]);
            record.TradeMoney = sign > 0 ? amount : -amount;
            record.IncomeAttribute = sign > 0 ? "收入" : "支出";
            record.CreditAmount = record.TradeMoney > 0 ? record.TradeMoney : null;
            record.DebitAmount = record.TradeMoney < 0 ? Math.Abs(record.TradeMoney.Value) : null;
            record.IncomeFlag = sign > 0 ? "C" : "D";
        }
    }

    private static void AdjustRoundedAmountsToTarget(IReadOnlyList<FlowRecord> records, double[] amounts, double targetTotal)
    {
        var diff = RoundMoney(targetTotal - amounts.Sum());
        if (Math.Abs(diff) <= 0.009d)
        {
            return;
        }

        var orderedIndexes = Enumerable.Range(0, records.Count)
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
                    adjustment = Math.Min(adjustment, amounts[index]);
                    amounts[index] = RoundAmountToUnit(amounts[index] - adjustment, unit);
                }
                else
                {
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

        if (Math.Abs(diff) > 0.009d)
        {
            var fallbackIndex = orderedIndexes.First();
            amounts[fallbackIndex] = RoundMoney(amounts[fallbackIndex] + diff);
        }
    }

    private static FlowRecord CreateBalancingRecord(FlowAutoGenerationRequest request, DateTime accountTime, double amount, string brief)
    {
        var record = CreateBaseRecord(request, accountTime, amount);
        record.ProductBrief = brief;
        record.Remark = brief;
        record.CashCheck = "转账";
        record.TradeChannel = request.Bank.Name == "支付宝" ? "电子商务" : "柜面";
        record.SerialNum = RandomDigits(accountTime.DayOfYear + accountTime.Second, 8);
        record.ExtraFields[AmountUnitField] = "0.01";
        return record;
    }

    private static DateTime PickFinalBalancingTime(IEnumerable<FlowRecord> records, FlowGenerationConfig config)
    {
        var end = NormalizeEndDate(config.EndTime);
        var lastTime = records
            .Select(item => item.AccountTime)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .DefaultIfEmpty(config.StartTime.Date)
            .Max();

        var candidate = lastTime.AddMinutes(1);
        if (candidate > end)
        {
            candidate = end;
        }

        return candidate;
    }

    private static bool ReduceExpenseForFinalBalance(IEnumerable<FlowRecord> records, double amount)
    {
        var remaining = RoundMoney(amount);
        foreach (var record in records
                     .Where(item => item.TradeMoney < 0)
                     .OrderByDescending(item => item.AccountTime ?? DateTime.MinValue))
        {
            if (remaining <= 0)
            {
                break;
            }

            var absolute = Math.Abs(record.TradeMoney ?? 0);
            if (absolute <= 0)
            {
                continue;
            }

            var reduction = Math.Min(absolute, remaining);
            var nextAbsolute = RoundMoney(absolute - reduction);
            record.TradeMoney = nextAbsolute <= 0 ? 0 : -nextAbsolute;
            record.DebitAmount = record.TradeMoney < 0 ? Math.Abs(record.TradeMoney.Value) : null;
            record.CreditAmount = null;
            remaining = RoundMoney(remaining - reduction);
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
        return RoundMoney(openingBalance + SumIncome(records) - lastMoney);
    }

    private static FlowAutoGenerationResult BuildResult(
        List<FlowRecord> records,
        double openingBalance,
        double lastMoney,
        bool requiresCorrection,
        double requiredOpeningBalance)
    {
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
            RequiresOpeningBalanceCorrection = requiresCorrection || Math.Abs(finalBalance - lastMoney) > 0.009d,
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
            RequiresOpeningBalanceCorrection = Math.Abs(openingBalance - lastMoney) > 0.009d,
            RequiredOpeningBalance = lastMoney
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

    private static double CreateSignedAmount(FlowRuleBase rule, Random random)
    {
        var min = Math.Max(0.01d, rule.MinMoney ?? 10d);
        var max = Math.Max(min, rule.MaxMoney ?? min);
        if (max < min)
        {
            (min, max) = (max, min);
        }

        var amount = min + (random.NextDouble() * (max - min));
        amount = RoundAmountByFloutLength(amount, rule.FloutLength);
        return IsIncomeRule(rule) ? amount : -amount;
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

    private static object? GetRuleValue(FlowRuleBase rule, string field)
    {
        if (TryGetIndexerField(field, out var indexerField))
        {
            return rule[indexerField];
        }

        var property = rule.GetType().GetProperty(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
        return property?.GetValue(rule);
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
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
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
