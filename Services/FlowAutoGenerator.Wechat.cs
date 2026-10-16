using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    // A memory-safety limit, not a daily density or an income/expense count target.
    private const int MaximumWechatNaturalRecordCount = 200000;

    private static FlowAutoGenerationResult GenerateWechatNaturalResult(
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        double openingBalance,
        Random random)
    {
        var records = new List<FlowRecord>();
        var scheduleState = new NativeScheduleState();
        var months = EnumerateMonths(start, end).ToList();
        var incomeTarget = RoundMoney(Math.Max(0, request.Config.AllInMoney));
        var expenseTarget = RoundMoney(Math.Max(0, incomeTarget - request.Config.LastMoney));
        var trace = FlowGenerationPerfTrace.Start();

        GenerateWechatRequiredRecords(request, start, end, months, incomeTarget, expenseTarget,
            random, records, scheduleState);
        trace.Mark("wechat natural required", records);
        GenerateWechatOptionalDirection(request, months, incomeTarget, true, random, records, scheduleState);
        CompleteWechatDirectionBudget(request, months, incomeTarget, true, random, records, scheduleState);
        ValidateWechatDirectionBudget(records, incomeTarget, true);
        trace.Mark("wechat natural income", records);

        // Couple money totals, never row counts. Any unavoidable rounding in income also
        // shifts the expense budget so that opening balance + LastMoney stays the target.
        expenseTarget = RoundMoney(Math.Max(0, SumIncome(records) - request.Config.LastMoney));
        var expenseMonths = IsWechatAutomaticMonthlyRange(request.Config, isIncome: false)
            ? CreateWechatAutomaticExpenseMonths(request, months, records, scheduleState, openingBalance, random)
            : null;
        GenerateWechatOptionalDirection(request, months, expenseTarget, false, random, records, scheduleState, expenseMonths);
        CompleteWechatDirectionBudget(request, months, expenseTarget, false, random, records, scheduleState);
        ValidateWechatDirectionBudget(records, expenseTarget, false);
        ApplyBalances(records, openingBalance);
        trace.Mark("wechat natural final", records);
        return CreateWechatNaturalBalanceResult(records, openingBalance, request.Config.LastMoney);
    }

    private static FlowAutoGenerationResult CreateWechatNaturalBalanceResult(
        List<FlowRecord> records, double openingBalance, double lastMoney)
    {
        ApplyBalances(records, openingBalance);
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimumBalance = records.Select(record => record.Balance ?? openingBalance)
            .Append(openingBalance).Min();
        // Keep the existing UI's negative-balance confirmation/correction contract.
        // Do not trim rows or silently replace the configured opening balance here.
        return new FlowAutoGenerationResult
        {
            Records = records,
            OpeningBalance = openingBalance,
            IncomeTotal = SumIncome(records),
            ExpenseTotal = SumExpense(records),
            FinalBalance = RoundMoney(finalBalance),
            MinimumBalance = RoundMoney(minimumBalance),
            RequiresOpeningBalanceCorrection = minimumBalance < -0.009d
                || IsFinalBalanceOutsideTolerance(finalBalance, openingBalance, lastMoney),
            RequiredOpeningBalance = RoundMoney(Math.Max(0, openingBalance - Math.Min(0, minimumBalance)))
        };
    }

    private static void GenerateWechatRequiredRecords(
        FlowAutoGenerationRequest request,
        DateTime start,
        DateTime end,
        IReadOnlyList<(DateTime Start, DateTime End)> months,
        double incomeTarget,
        double expenseTarget,
        Random random,
        List<FlowRecord> records,
        NativeScheduleState state)
    {
        var plannedDayUsage = new Dictionary<DateTime, int>();
        var occurrences = new List<(FlowRuleBase Rule, DateTime Start, DateTime End, DateTime? FixedDate)>();
        foreach (var rule in request.ConstItems.Where(rule => rule.IsCheck && !string.IsNullOrWhiteSpace(rule.FixDay)))
        {
            foreach (var date in ResolveNativeFixedOccurrences(rule, start, end, random, state, plannedDayUsage))
            {
                occurrences.Add((rule, date.Date, NormalizeEndDate(date), date.Date));
                plannedDayUsage[date.Date] = plannedDayUsage.GetValueOrDefault(date.Date) + 1;
                EnsureWechatNaturalCapacity(occurrences.Count);
            }
        }

        foreach (var rule in request.References.Where(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0))
        {
            foreach (var month in months)
            {
                if (state.GetCandidateDates(month.Start, month.End, rule).Count == 0)
                {
                    throw new InvalidOperationException($"微信参照明细第 {rule.Index} 行在 {month.Start:yyyy-MM} 没有符合交易日期限制的日期，无法满足每月出现次数。");
                }

                for (var index = 0; index < GetRequiredMonthlyCount(rule); index++)
                {
                    occurrences.Add((rule, month.Start, month.End, null));
                    EnsureWechatNaturalCapacity(occurrences.Count);
                }
            }
        }

        var remainingIncomeMinimum = RoundMoney(occurrences.Where(item => IsIncomeRuleSafe(item.Rule))
            .Sum(item => GetRuleAmountBounds(item.Rule).Min));
        var remainingExpenseMinimum = RoundMoney(occurrences.Where(item => !IsIncomeRuleSafe(item.Rule))
            .Sum(item => GetRuleAmountBounds(item.Rule).Min));
        if (remainingIncomeMinimum > incomeTarget + 0.009d || remainingExpenseMinimum > expenseTarget + 0.009d)
        {
            throw new InvalidOperationException(
                $"微信固定日期及每月必现项目的最低收入/支出为 {remainingIncomeMinimum:N2}/{remainingExpenseMinimum:N2} 元，" +
                $"超过目标 {incomeTarget:N2}/{expenseTarget:N2} 元，请调整必现次数或金额范围。");
        }

        var income = 0d;
        var expense = 0d;
        foreach (var occurrence in occurrences)
        {
            var rule = occurrence.Rule;
            var isIncome = IsIncomeRuleSafe(rule);
            var bounds = GetRuleAmountBounds(rule);
            var futureMinimum = RoundMoney((isIncome ? remainingIncomeMinimum : remainingExpenseMinimum) - bounds.Min);
            var budget = RoundMoney((isIncome ? incomeTarget - income : expenseTarget - expense) - futureMinimum);
            var amount = DrawWechatNaturalAmount(bounds, budget, random);
            var dates = state.GetCandidateDates(occurrence.Start, occurrence.End, rule);
            var date = occurrence.FixedDate ?? dates[random.Next(dates.Count)];
            AddWechatNaturalRecord(request, rule, date, amount, random, records, state);
            if (isIncome)
            {
                income = RoundMoney(income + amount);
                remainingIncomeMinimum = futureMinimum;
            }
            else
            {
                expense = RoundMoney(expense + amount);
                remainingExpenseMinimum = futureMinimum;
            }
        }
    }

    private static void GenerateWechatOptionalDirection(
        FlowAutoGenerationRequest request,
        IReadOnlyList<(DateTime Start, DateTime End)> months,
        double target,
        bool isIncome,
        Random random,
        List<FlowRecord> records,
        NativeScheduleState state,
        IReadOnlyList<double>? plannedTargets = null)
    {
        var rules = request.References.Where(rule => rule.IsCheck
                && IsIncomeRuleSafe(rule) == isIncome && GetRequiredMonthlyCount(rule) == 0)
            .ToArray();
        if (rules.Length == 0 || months.Count == 0)
        {
            return;
        }

        var pools = months.Select(month => rules.Select(rule => new WechatNaturalRule(
                rule, GetRuleAmountBounds(rule), state.GetCandidateDates(month.Start, month.End, rule)))
            .Where(item => item.Dates.Count > 0).ToArray()).ToArray();
        var monthTargets = plannedTargets ?? CreateWechatNaturalMonthlyTargets(request.Config, months, target, isIncome, random);
        var existing = months.Select(month => isIncome
            ? SumIncome(records.Where(record => IsRecordInRange(record, month.Start, month.End)))
            : SumExpense(records.Where(record => IsRecordInRange(record, month.Start, month.End)))).ToArray();
        var remainingTotal = RoundMoney(Math.Max(0, target - existing.Sum()));
        var weights = monthTargets.Select((amount, index) => pools[index].Length == 0
            ? 0 : Math.Max(0, amount - existing[index])).ToArray();
        if (weights.Sum() <= 0.009d)
        {
            weights = months.Select((month, index) => pools[index].Length == 0
                ? 0d : GetActiveDayCount(month)).ToArray();
        }
        if (weights.Sum() <= 0.009d)
        {
            return;
        }

        var budgets = NormalizeRawTargets(weights, remainingTotal);
        var carriedRemainder = 0d;
        for (var monthIndex = 0; monthIndex < months.Count; monthIndex++)
        {
            var remaining = RoundMoney(budgets[monthIndex] + carriedRemainder);
            var monthTarget = RoundMoney(existing[monthIndex] + remaining);
            var pool = pools[monthIndex];
            var largestMinimum = pool.Select(item => item.Bounds.Min).DefaultIfEmpty(double.MaxValue).Max();
            // Equal weight per selected rule, not per amount tier. At the budget
            // tail, adjust existing amounts first instead of over-selecting the
            // cheapest rules and creating an artificial cluster of small expenses.
            while (remaining >= largestMinimum - 0.009d)
            {
                var selected = pool[random.Next(pool.Length)];
                var amount = DrawWechatNaturalAmount(selected.Bounds, remaining, random);
                var date = selected.Dates[random.Next(selected.Dates.Count)];
                AddWechatNaturalRecord(request, selected.Rule, date, amount, random, records, state);
                remaining = RoundMoney(remaining - amount);
            }
            var month = months[monthIndex];
            var monthRecords = records.Where(record => IsRecordInRange(record, month.Start, month.End)).ToList();
            AdjustWechatDirectionAmounts(monthRecords, monthTarget, isIncome, random);
            var actual = isIncome ? SumIncome(monthRecords) : SumExpense(monthRecords);
            remaining = RoundMoney(Math.Max(0, monthTarget - actual));
            // A small budget may never afford the largest rule's minimum, or all
            // existing rows may already be at their maximum. Fill only this
            // unabsorbed tail with affordable rules, rather than silently skipping it.
            while (remaining > 0.009d)
            {
                var affordable = pool.Where(item => item.Bounds.Min <= remaining + 0.009d).ToArray();
                if (affordable.Length == 0)
                {
                    break;
                }
                var selected = affordable[random.Next(affordable.Length)];
                var amount = DrawWechatNaturalAmount(selected.Bounds, remaining, random);
                var date = selected.Dates[random.Next(selected.Dates.Count)];
                AddWechatNaturalRecord(request, selected.Rule, date, amount, random, records, state);
                monthRecords.Add(records[^1]);
                remaining = RoundMoney(remaining - amount);
            }
            AdjustWechatDirectionAmounts(monthRecords, monthTarget, isIncome, random);
            actual = isIncome ? SumIncome(monthRecords) : SumExpense(monthRecords);
            carriedRemainder = RoundMoney(Math.Max(0, monthTarget - actual));
        }
    }

    private static double[] CreateWechatNaturalMonthlyTargets(
        FlowGenerationConfig config,
        IReadOnlyList<(DateTime Start, DateTime End)> months,
        double target,
        bool isIncome,
        Random random)
    {
        // In total-income mode the Calculate button fills min=max with the
        // arithmetic monthly average. It is not a request for identical months.
        // Explicit monthly ranges/details keep their existing allocation semantics.
        if (months.Count > 1 && IsWechatAutomaticMonthlyRange(config, isIncome))
        {
            var factors = months.Select(_ => 0.80d + random.NextDouble() * 0.40d).ToArray();
            var fullMonths = Enumerable.Range(0, months.Count)
                .Where(index => GetActiveDayCount(months[index]) == DateTime.DaysInMonth(months[index].Start.Year, months[index].Start.Month))
                .OrderBy(_ => random.Next()).ToArray();
            if (fullMonths.Length >= 4)
            {
                factors[fullMonths[0]] = 1.45d + random.NextDouble() * 0.20d;
                factors[fullMonths[1]] = 0.65d + random.NextDouble() * 0.13d;
            }
            var volatileWeights = months.Select((month, index) => factors[index]
                * GetActiveDayCount(month) / DateTime.DaysInMonth(month.Start.Year, month.Start.Month)).ToArray();
            return NormalizeRawTargets(volatileWeights, target);
        }

        if (config.SelectIndex == 2)
        {
            var detailTargets = CreateMonthDetailRawTargets(config, months, isIncome);
            if (detailTargets.Sum() > 0.009d)
            {
                return NormalizeRawTargets(detailTargets, target);
            }
        }

        var secondRange = config.SelectIndex == 1;
        var min = isIncome
            ? secondRange ? config.MinInMoneyMonth2 : config.MinInMoneyMonth1
            : secondRange ? config.MinOutMoneyMonth2 : config.MinOutMoneyMonth1;
        var max = isIncome
            ? secondRange ? config.MaxInMoneyMonth2 : config.MaxInMoneyMonth1
            : secondRange ? config.MaxOutMoneyMonth2 : config.MaxOutMoneyMonth1;
        if (max < min)
        {
            (min, max) = (max, min);
        }
        var weights = months.Select(month => max > 0.009d
            ? Math.Max(0, min + random.NextDouble() * (max - min))
            : (double)GetActiveDayCount(month)).ToArray();
        return NormalizeRawTargets(weights, target);
    }

    private static bool IsWechatAutomaticMonthlyRange(FlowGenerationConfig config, bool isIncome)
    {
        if (config.SelectIndex != 0) return false;
        var min = isIncome ? config.MinInMoneyMonth1 : config.MinOutMoneyMonth1;
        var max = isIncome ? config.MaxInMoneyMonth1 : config.MaxOutMoneyMonth1;
        return min <= 0.009d && max <= 0.009d || Math.Abs(max - min) <= 0.009d;
    }

    private static double[]? CreateWechatAutomaticExpenseMonths(
        FlowAutoGenerationRequest request,
        IReadOnlyList<(DateTime Start, DateTime End)> months,
        IReadOnlyList<FlowRecord> records,
        NativeScheduleState state,
        double openingBalance,
        Random random)
    {
        if (months.Count == 0) return null;
        var finalBalance = CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney);
        if (finalBalance < -0.009d) return null;
        var income = months.Select(month => SumIncome(records.Where(record => IsRecordInRange(record, month.Start, month.End)))).ToArray();
        var requiredExpense = months.Select(month => SumExpense(records.Where(record => IsRecordInRange(record, month.Start, month.End)))).ToArray();
        var optionalRules = request.References.Where(rule => rule.IsCheck && !IsIncomeRuleSafe(rule)
            && GetRequiredMonthlyCount(rule) == 0).ToArray();
        var canAddExpense = months.Select(month => optionalRules.Any(rule => state.GetCandidateDates(month.Start, month.End, rule).Count > 0)).ToArray();

        // Reserve cash for future required expenses and the requested final balance.
        // Months with no eligible optional expense rule cannot spend extra money.
        var lowerClosing = new double[months.Count];
        var upperClosing = new double[months.Count];
        lowerClosing[^1] = upperClosing[^1] = finalBalance;
        for (var index = months.Count - 2; index >= 0; index--)
        {
            var next = index + 1;
            var shift = RoundMoney(requiredExpense[next] - income[next]);
            lowerClosing[index] = RoundMoney(Math.Max(0, lowerClosing[next] + shift));
            upperClosing[index] = canAddExpense[next] ? double.MaxValue : upperClosing[next] + shift;
            if (upperClosing[index] < lowerClosing[index] - 0.009d) return null;
        }

        var averageIncome = income.Sum() / months.Count;
        var previousClosing = openingBalance;
        var targets = new double[months.Count];
        for (var index = 0; index < months.Count; index++)
        {
            var available = RoundMoney(previousClosing + income[index] - requiredExpense[index]);
            var minimum = lowerClosing[index];
            var maximum = Math.Min(upperClosing[index], available);
            if (maximum < minimum - 0.009d) return null;
            double closing;
            if (!canAddExpense[index])
            {
                closing = available;
                if (closing > maximum + 0.009d) return null;
            }
            else
            {
                // Income and expense track the month's activity, but their gap is
                // a cash-balance walk, not a fixed monthly ratio or equal net amount.
                var desired = averageIncome * (0.03d + random.NextDouble() * 0.15d);
                var step = Math.Max(1d, income[index] * (0.06d + random.NextDouble() * 0.06d));
                desired = Math.Clamp(desired, Math.Max(0, previousClosing - step), previousClosing + step);
                closing = RoundMoney(Math.Clamp(desired, minimum, Math.Max(minimum, maximum)));
            }
            targets[index] = RoundMoney(previousClosing + income[index] - closing);
            previousClosing = closing;
        }
        return targets;
    }

    private static double DrawWechatNaturalAmount(AmountBounds bounds, double budget, Random random)
    {
        var maximum = FindValidAmountAtOrBelow(Math.Min(bounds.Max, budget), bounds.Min, bounds);
        if (!maximum.HasValue)
        {
            throw new InvalidOperationException("微信金额预算不足，无法满足配置的最小金额及小数位。");
        }
        var first = (long)Math.Round(bounds.Min / bounds.Unit);
        var last = (long)Math.Round(maximum.Value / bounds.Unit);
        // Rejection sampling preserves equal probability among valid precision values.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var amount = RoundMoney(random.NextInt64(first, last + 1) * bounds.Unit);
            if (HasRequiredNonZeroPrecisionDigit(amount, bounds))
            {
                return amount;
            }
        }
        return bounds.Min;
    }

    private static void AddWechatNaturalRecord(
        FlowAutoGenerationRequest request, FlowRuleBase rule, DateTime date, double amount,
        Random random, List<FlowRecord> records, NativeScheduleState state)
    {
        EnsureWechatNaturalCapacity(records.Count + 1);
        var time = PickNativeTimeOnDate(date, rule, random, state, state.NextSequence());
        var record = CreateRecordFromRule(request, rule,
            rule is GenerateConstRule ? request.Bank.ConstColumns : request.Bank.ReferenceColumns,
            time, IsIncomeRuleSafe(rule) ? amount : -amount);
        records.Add(record);
        state.Register(record);
    }

    private static void AdjustWechatDirectionAmounts(IReadOnlyList<FlowRecord> records, double target, bool isIncome, Random random)
    {
        var difference = RoundMoney(target - (isIncome ? SumIncome(records) : SumExpense(records)));
        foreach (var record in records.Where(record => (record.TradeMoney > 0) == isIncome)
                     .OrderBy(record => GetRecordAmountBounds(record).Unit).ThenBy(_ => random.Next()))
        {
            if (Math.Abs(difference) <= 0.009d)
            {
                break;
            }
            var bounds = GetRecordAmountBounds(record);
            var current = Math.Abs(record.TradeMoney ?? 0);
            var next = difference > 0
                ? FindValidAmountAtOrBelow(Math.Min(bounds.Max, current + difference), current + bounds.Unit, bounds)
                : FindValidAmountAtOrAbove(Math.Max(bounds.Min, current + difference), current - bounds.Unit, bounds);
            if (!next.HasValue)
            {
                continue;
            }
            record.TradeMoney = isIncome ? next.Value : -next.Value;
            difference = RoundMoney(difference - (next.Value - current));
        }
    }

    private static void CompleteWechatDirectionBudget(
        FlowAutoGenerationRequest request,
        IReadOnlyList<(DateTime Start, DateTime End)> months,
        double target,
        bool isIncome,
        Random random,
        List<FlowRecord> records,
        NativeScheduleState state)
    {
        AdjustWechatDirectionAmounts(records, target, isIncome, random);
        var remaining = RoundMoney(target - (isIncome ? SumIncome(records) : SumExpense(records)));
        if (remaining <= AcceptedFinalBalanceTolerance + 0.009d)
        {
            return;
        }

        // Example: a fixed 5,000 denomination and a 14,000 budget cannot make
        // 14,000 exactly, but 15,000 is within the existing 1,000 tolerance.
        // Use only an optional rule; never create a synthetic out-of-range row.
        var candidates = request.References.Where(rule => rule.IsCheck
                && IsIncomeRuleSafe(rule) == isIncome && GetRequiredMonthlyCount(rule) == 0)
            .Select(rule => new WechatNaturalRule(rule, GetRuleAmountBounds(rule),
                months.SelectMany(month => state.GetCandidateDates(month.Start, month.End, rule)).ToArray()))
            .Where(item => item.Dates.Count > 0).ToArray();
        while (remaining > AcceptedFinalBalanceTolerance + 0.009d)
        {
            var affordable = candidates.Where(item => item.Bounds.Min <= remaining + AcceptedFinalBalanceTolerance + 0.009d).ToArray();
            if (affordable.Length == 0)
            {
                break;
            }
            var selected = affordable[random.Next(affordable.Length)];
            var amount = FindValidAmountAtOrBelow(Math.Min(selected.Bounds.Max, remaining), selected.Bounds.Min, selected.Bounds)
                ?? selected.Bounds.Min;
            var date = selected.Dates[random.Next(selected.Dates.Count)];
            AddWechatNaturalRecord(request, selected.Rule, date, amount, random, records, state);
            remaining = RoundMoney(remaining - amount);
        }
        AdjustWechatDirectionAmounts(records, target, isIncome, random);
    }

    private static void ValidateWechatDirectionBudget(List<FlowRecord> records, double target, bool isIncome)
    {
        var actual = isIncome ? SumIncome(records) : SumExpense(records);
        if (Math.Abs(RoundMoney(target - actual)) > AcceptedFinalBalanceTolerance + 0.009d)
        {
            throw new InvalidOperationException(
                $"微信{(isIncome ? "收入" : "支出")}目标 {target:N2} 元，配置范围内只能生成 {actual:N2} 元。" +
                "请检查该方向是否有可选参照明细、可交易日期，或必现项目的金额容量是否足够。");
        }
    }

    private static void EnsureWechatNaturalCapacity(int count)
    {
        if (count > MaximumWechatNaturalRecordCount)
        {
            throw new InvalidOperationException($"微信按当前金额范围需要超过 {MaximumWechatNaturalRecordCount:N0} 条流水，超过单次内存安全上限，请缩短日期范围或调整金额配置。");
        }
    }

    private static bool TryRepairWechatNegativeBalanceTimes(
        FlowAutoGenerationRequest request, List<FlowRecord> records, double openingBalance,
        DateTime start, DateTime end, Random random)
    {
        var state = NativeScheduleState.From(records);
        var rules = request.References.GroupBy(rule => rule.Index).ToDictionary(group => group.Key, group => group.First());
        for (var pass = 0; pass < 256; pass++)
        {
            ApplyBalances(records, openingBalance);
            var firstNegative = records.FirstOrDefault(record => record.Balance < -0.009d);
            if (firstNegative?.AccountTime is not { } firstNegativeTime)
            {
                return GetMinimumBalance(records, openingBalance) >= -0.009d;
            }
            var monthStart = MaxDateTime(start, new DateTime(firstNegativeTime.Year, firstNegativeTime.Month, 1));
            var monthEnd = MinDateTime(end, NormalizeEndDate(new DateTime(firstNegativeTime.Year, firstNegativeTime.Month,
                DateTime.DaysInMonth(firstNegativeTime.Year, firstNegativeTime.Month))));
            var monthRecords = records.Where(record => IsRecordInRange(record, monthStart, monthEnd)).ToList();
            var needed = RoundMoney(-monthRecords.Min(record => record.Balance ?? openingBalance) + 1);
            var moved = false;
            foreach (var record in monthRecords.Where(record => record.AccountTime > firstNegativeTime && record.TradeMoney > 0)
                         .OrderByDescending(record => record.TradeMoney).Take(512))
            {
                if (!TryGetWechatReferenceRule(record, rules, out var rule)
                    || !TryPickWechatRepairTime(state, rule, monthStart, firstNegativeTime.AddSeconds(-1), random, out var time))
                {
                    continue;
                }
                var previous = record.AccountTime!.Value;
                record.AccountTime = time;
                state.Move(record, previous);
                RefreshWechatMovedSystemNumbers(request, rule, record);
                needed = RoundMoney(needed - (record.TradeMoney ?? 0));
                moved = true;
                if (needed <= 0.009d) break;
            }
            if (moved) continue;

            // If the income window cannot precede the debit, defer a reference
            // expense within its own month. Fixed-date items are never moved.
            foreach (var record in monthRecords.Where(record => record.AccountTime <= firstNegativeTime && record.TradeMoney < 0)
                         .OrderBy(record => record.TradeMoney))
            {
                if (!TryGetWechatReferenceRule(record, rules, out var rule)
                    || !TryPickWechatRepairTime(state, rule, firstNegativeTime.AddSeconds(1), monthEnd, random, out var time))
                {
                    continue;
                }
                var previous = record.AccountTime!.Value;
                record.AccountTime = time;
                state.Move(record, previous);
                RefreshWechatMovedSystemNumbers(request, rule, record);
                moved = true;
                break;
            }
            if (!moved) break;
        }
        ApplyBalances(records, openingBalance);
        return GetMinimumBalance(records, openingBalance) >= -0.009d;
    }

    private static bool TryGetWechatReferenceRule(
        FlowRecord record, IReadOnlyDictionary<int, GenerateReferenceRule> rules, out GenerateReferenceRule rule)
    {
        rule = null!;
        return IsGeneratedReferenceRecord(record)
            && record.ExtraFields.TryGetValue(SourceIndexField, out var sourceIndex)
            && int.TryParse(sourceIndex, out var index)
            && rules.TryGetValue(index, out rule!);
    }

    private static void RefreshWechatMovedSystemNumbers(
        FlowAutoGenerationRequest request, GenerateReferenceRule rule, FlowRecord record)
    {
        var sign = record.TradeMoney < 0 ? -1 : 1;
        var template = RuleRecordTemplates.GetOrAdd((rule, request.Bank, request.BankUser.Id, sign),
            _ => CreateRecordTemplate(request, rule, request.Bank.ReferenceColumns, GetRuleAmountBounds(rule), sign));
        if (!string.IsNullOrWhiteSpace(template.SerialNum) && !string.IsNullOrWhiteSpace(template.MerchantName)) return;
        var refreshed = CreateRecordFromRule(request, rule, request.Bank.ReferenceColumns,
            record.AccountTime!.Value, record.TradeMoney ?? 0);
        // Only regenerate system-filled numbers containing a date; user-provided
        // values and formulas must keep their original source binding.
        if (string.IsNullOrWhiteSpace(template.SerialNum)) record.SerialNum = refreshed.SerialNum;
        if (string.IsNullOrWhiteSpace(template.MerchantName)) record.MerchantName = refreshed.MerchantName;
    }

    private static bool TryPickWechatRepairTime(
        NativeScheduleState state, GenerateReferenceRule rule, DateTime start, DateTime end,
        Random random, out DateTime time)
    {
        time = default;
        if (start > end) return false;
        var startHour = Math.Clamp(rule.StartDay ?? 9, 0, 23);
        var endHour = Math.Clamp(rule.EndDay ?? 17, 0, 23);
        if (endHour < startHour) (startHour, endHour) = (endHour, startHour);
        foreach (var date in state.GetCandidateDates(start, end, rule)
                     .OrderBy(date => state.GetTotalCount(date)).ThenBy(_ => random.Next()))
        {
            var earliest = MaxDateTime(start, date.Date.AddHours(startHour));
            var latest = MinDateTime(end, date.Date.AddHours(endHour + 1).AddSeconds(-1));
            if (earliest > latest) continue;
            var seconds = (int)(latest - earliest).TotalSeconds;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var candidate = earliest.AddSeconds(random.Next(seconds + 1));
                if (state.IsTimeUsed(candidate)) continue;
                time = candidate;
                return true;
            }
        }
        return false;
    }

    private sealed record WechatNaturalRule(GenerateReferenceRule Rule, AmountBounds Bounds, IReadOnlyList<DateTime> Dates);
}
