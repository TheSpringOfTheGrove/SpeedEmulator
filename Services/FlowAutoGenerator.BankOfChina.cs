using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private static FlowAutoGenerationResult GenerateBankOfChinaResult(
        FlowAutoGenerationRequest request, DateTime start, DateTime end, double openingBalance, Random random)
    {
        if (!double.IsFinite(request.Config.AllInMoney) || request.Config.AllInMoney < 0
            || !double.IsFinite(request.Config.LastMoney) || !double.IsFinite(openingBalance))
            throw new InvalidOperationException("中行生成金额配置无效");

        var incomeTarget = RoundMoney(request.Config.AllInMoney);
        var incomeCap = BankOfChinaGenerationBudget.Bounds(incomeTarget).Max;
        var expenseTarget = BankOfChinaGenerationBudget.ExpenseTarget(request.Config);
        if (incomeTarget == 0 || expenseTarget == 0)
        {
            request = new FlowAutoGenerationRequest
            {
                Bank = request.Bank, BankUser = request.BankUser, Config = request.Config,
                ConstItems = request.ConstItems, InterestSetting = request.InterestSetting,
                OpeningBalanceOverride = request.OpeningBalanceOverride,
                References = request.References.Where(rule => GetRequiredMonthlyCount(rule) > 0
                    || (IsIncomeRuleSafe(rule) ? incomeTarget : expenseTarget) > 0).ToList()
            };
        }
        var plan = CreateMonthlyAmountPlan(request, start, end, incomeTarget, expenseTarget, random);
        var records = new List<FlowRecord>();
        var schedule = NativeScheduleState.From(records);
        var usage = AmountUsageTracker.From(records);

        var requiredIncomeMinimum = RoundMoney(request.References.Where(rule => rule.IsCheck && IsIncomeRuleSafe(rule))
            .Sum(rule => GetRuleAmountBounds(rule).Min * GetRequiredMonthlyCount(rule) * plan.Months.Count));
        var requiredExpenseMinimum = RoundMoney(request.References.Where(rule => rule.IsCheck && !IsIncomeRuleSafe(rule))
            .Sum(rule => GetRuleAmountBounds(rule).Min * GetRequiredMonthlyCount(rule) * plan.Months.Count));
        if (requiredIncomeMinimum > incomeCap + 0.009d)
            throw new InvalidOperationException("中行每月必现收入项目的最低金额已超过总预算允许的10%浮动范围");
        GenerateNativeConstRecords(request, start, end, RoundMoney(incomeCap - requiredIncomeMinimum), random, records, schedule, usage);
        var constExpenseCap = RoundMoney(BankOfChinaGenerationBudget.Bounds(expenseTarget).Max - requiredExpenseMinimum);
        if (SumExpense(records) > constExpenseCap + 0.009d)
            AdjustBankOfChinaSignedTotal(records, isIncome: false, Math.Max(0, constExpenseCap));
        GenerateBankOfChinaRequiredReferences(request, plan, incomeCap, random, records, schedule, usage);
        EnsureNativeOptionalReferenceRuleCoverage(request, plan, incomeCap, random, records, schedule, usage);
        GenerateNativeOptionalReferenceRecords(request, plan, incomeCap, random, records, schedule, usage);
        EnsureNativeOptionalReferenceRuleCoverage(request, plan, incomeCap, random, records, schedule, usage);
        CompleteNativeConfiguredTotals(request, plan, start, end, incomeTarget, random, records, schedule, usage);
        foreach (var rule in request.References.Where(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0))
        foreach (var month in plan.Months)
        {
            var actual = records.Count(row => IsGeneratedReferenceRecord(row)
                && row.ExtraFields.GetValueOrDefault(SourceIndexField) == rule.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && IsRecordInRange(row, month.Start, month.End));
            if (actual != GetRequiredMonthlyCount(rule))
                throw new InvalidOperationException($"中行参照明细第{rule.Index}行在{month.Start:yyyy年MM月}无法满足每月出现次数，" +
                    "请检查总预算、金额范围和可交易日期");
        }

        // Do not run the legacy amount-shaping/closing passes: they can delete income
        // to achieve the final balance and then accept a much smaller statement.
        return FinalizeBankOfChinaResult(request, records, start, end, openingBalance, random, repairNegative: false);
    }

    private static void GenerateBankOfChinaRequiredReferences(
        FlowAutoGenerationRequest request, MonthlyAmountPlan plan, double incomeCap, Random random,
        List<FlowRecord> records, NativeScheduleState schedule, AmountUsageTracker usage)
    {
        var required = request.References.Where(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0).ToList();
        var count = required.Sum(rule => (long)GetRequiredMonthlyCount(rule) * plan.Months.Count);
        if (count + records.Count > MaximumFixedRuleRecordCount)
            throw new InvalidOperationException($"中行固定及每月必现项目超过单次上限{MaximumFixedRuleRecordCount:N0}笔");
        var incomeMinimum = RoundMoney(required.Where(IsIncomeRuleSafe)
            .Sum(rule => GetRuleAmountBounds(rule).Min * GetRequiredMonthlyCount(rule) * plan.Months.Count));
        var expenseMinimum = RoundMoney(required.Where(rule => !IsIncomeRuleSafe(rule))
            .Sum(rule => GetRuleAmountBounds(rule).Min * GetRequiredMonthlyCount(rule) * plan.Months.Count));
        var expenseCap = BankOfChinaGenerationBudget.Bounds(BankOfChinaGenerationBudget.ExpenseTarget(request.Config)).Max;
        if (incomeMinimum + SumIncome(records) > incomeCap + 0.009d
            || expenseMinimum + SumExpense(records) > expenseCap + 0.009d)
            throw new InvalidOperationException("中行固定及每月必现项目的最低金额已超过总预算允许的10%浮动范围");

        for (var monthIndex = 0; monthIndex < plan.Months.Count; monthIndex++)
        {
            var month = plan.Months[monthIndex];
            foreach (var rule in required)
            {
                if (schedule.GetCandidateDates(month.Start, month.End, rule).Count == 0)
                    throw new InvalidOperationException($"中行参照明细第{rule.Index}行在{month.Start:yyyy年MM月}没有可交易日期");
                var income = IsIncomeRuleSafe(rule);
                var bounds = GetRuleAmountBounds(rule);
                for (var occurrence = 0; occurrence < GetRequiredMonthlyCount(rule); occurrence++)
                {
                    var futureMinimum = RoundMoney((income ? incomeMinimum : expenseMinimum) - bounds.Min);
                    var current = income ? SumIncome(records) : SumExpense(records);
                    var available = RoundMoney((income ? incomeCap : expenseCap) - current - futureMinimum);
                    if (available < bounds.Min - 0.009d)
                        throw new InvalidOperationException("中行每月必现项目无法在总预算±10%内满足配置次数");
                    var monthCurrent = income ? SumIncome(records.Where(row => IsRecordInRange(row, month.Start, month.End)))
                        : SumExpense(records.Where(row => IsRecordInRange(row, month.Start, month.End)));
                    var monthTarget = income ? plan.IncomeTargets[monthIndex] : plan.ExpenseTargets[monthIndex];
                    // Monthly allocation is a soft budget; it cannot erase an explicit occurrence.
                    var cap = Math.Min(available, Math.Min(bounds.Max, Math.Max(bounds.Min, monthTarget - monthCurrent)));
                    if (!TryCreateNativeUniqueAmount(rule, cap, random, preferLower: true, records, income, usage, out var amount))
                        amount = bounds.Min;
                    var time = PickNativeDistributedTime(month.Start, month.End, rule, income, random, schedule);
                    var row = CreateRecordFromRule(request, rule, request.Bank.ReferenceColumns, time, income ? amount : -amount);
                    records.Add(row); schedule.Register(row); usage.Register(row);
                    if (income) incomeMinimum = futureMinimum; else expenseMinimum = futureMinimum;
                }
            }
        }
    }

    private static FlowAutoGenerationResult CorrectBankOfChinaResult(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source, bool repairNegative)
    {
        request = ExcludeUngeneratableReferenceRules(request);
        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start) (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        var records = source.Records.Select(row => row.Clone()).ToList();
        var random = new Random(CreateRunSeed(request, start, end, records.Count ^ 0x61B0C));
        return FinalizeBankOfChinaResult(request, records, start, end, source.OpeningBalance, random, repairNegative);
    }

    private static FlowAutoGenerationResult FinalizeBankOfChinaResult(
        FlowAutoGenerationRequest request, List<FlowRecord> records, DateTime start, DateTime end,
        double openingBalance, Random random, bool repairNegative)
    {
        var incomeBounds = BankOfChinaGenerationBudget.Bounds(request.Config.AllInMoney);
        var expenseBounds = BankOfChinaGenerationBudget.Bounds(BankOfChinaGenerationBudget.ExpenseTarget(request.Config));
        for (var pass = 0; pass < 12; pass++)
        {
            RecalculateInterestRecords(records, request, openingBalance, start, end, random);
            ApplyBalances(records, openingBalance);
            var signature = CalculateBalanceInputSignature(records);
            var income = SumIncome(records);
            if (income < incomeBounds.Min - 0.009d || income > incomeBounds.Max + 0.009d)
                AdjustBankOfChinaSignedTotal(records, isIncome: true, Math.Clamp(income, incomeBounds.Min, incomeBounds.Max));

            // Debit budget follows actual gross income (including settlement interest).
            // If the percentage band limits it, the original final-balance tolerance applies.
            var desiredExpense = Math.Clamp(SumIncome(records) - request.Config.LastMoney, expenseBounds.Min, expenseBounds.Max);
            AdjustBankOfChinaSignedTotal(records, isIncome: false, desiredExpense);
            ApplyBalances(records, openingBalance);
            if (repairNegative && GetMinimumBalance(records, openingBalance) < -0.009d)
                RepairBankOfChinaNegativeBalances(request, records, start, end, openingBalance, random);

            // Time/amount repair changes the daily ledger, so recompute settlement before
            // deciding whether this candidate is stable. Never close by deleting income.
            RecalculateInterestRecords(records, request, openingBalance, start, end, random);
            ApplyBalances(records, openingBalance);
            if (signature == CalculateBalanceInputSignature(records)) break;
        }

        var result = CreateBankOfChinaBalanceResult(records, openingBalance, request.Config.LastMoney);
        if (!BankOfChinaGenerationBudget.TryValidate(request.Bank, request.Config, result, out var reason))
            throw new InvalidOperationException(reason);
        if (IsFinalBalanceOutsideTolerance(result.FinalBalance, openingBalance, request.Config.LastMoney))
            throw new InvalidOperationException(
                $"中行无法在保留流水、金额范围和总收支±10%的条件下满足期末余额：" +
                $"目标 {CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney):N2}，实际 {result.FinalBalance:N2}");
        // A still-negative result is deliberately returned for the existing Yes/No
        // confirmation/repair validator. The caller must not silently waive this check.
        return result;
    }

    private static bool AdjustBankOfChinaSignedTotal(List<FlowRecord> records, bool isIncome, double target)
    {
        var current = isIncome ? SumIncome(records) : SumExpense(records);
        var changed = false;
        var increasing = target > current;
        var candidates = records.Where(row => !IsSystemInterestRecord(row))
            .Where(row => isIncome ? row.TradeMoney > 0 : row.TradeMoney < 0)
            .OrderBy(row => GetRecordAmountBounds(row).Unit)
            .ThenByDescending(row => row.AccountTime).ToList();
        foreach (var row in candidates)
        {
            var remaining = RoundMoney(target - current);
            if (Math.Abs(remaining) <= 0.009d) break;
            var before = Math.Abs(row.TradeMoney ?? 0);
            var bounds = GetRecordAmountBounds(row);
            // The shared clamp deliberately treats non-positive input as "delete row".
            // BOC closing must instead stop at the source minimum and keep the row.
            var next = ClampAmountToBounds(Math.Clamp(before + remaining, bounds.Min, bounds.Max), bounds);
            var delta = RoundMoney(next - before);
            if ((delta > 0) != increasing || Math.Abs(delta) <= 0.009d
                || Math.Abs(target - (current + delta)) >= Math.Abs(remaining) - 0.000001d) continue;
            ApplySignedAmount(row, isIncome ? 1 : -1, next);
            current = RoundMoney(current + delta);
            changed = true;
        }
        return changed;
    }

    private static void RepairBankOfChinaNegativeBalances(
        FlowAutoGenerationRequest request, List<FlowRecord> records, DateTime start, DateTime end,
        double openingBalance, Random random)
    {
        var state = NativeScheduleState.From(records);
        for (var pass = 0; pass < Math.Min(4000, records.Count * 2); pass++)
        {
            ApplyBalances(records, openingBalance);
            var firstNegative = records.FirstOrDefault(row => row.Balance < -0.009d);
            if (firstNegative?.AccountTime is not { } negativeTime) break;
            var changed = false;
            foreach (var row in records.Where(row => row.AccountTime > negativeTime && row.TradeMoney > 0)
                         .Where(IsGeneratedReferenceRecord).OrderBy(row => row.AccountTime)
                         .ThenByDescending(row => row.TradeMoney).ToList())
            {
                if (ResolveRecordSourceRule(request, row) is not GenerateReferenceRule rule) continue;
                var earliest = GetRequiredMonthlyCount(rule) > 0
                    ? MaxDateTime(start, new DateTime(row.AccountTime!.Value.Year, row.AccountTime.Value.Month, 1)) : start;
                if (!TryPickBankOfChinaRepairTime(state, rule, earliest, negativeTime.AddSeconds(-1),
                        random, latestFirst: true, out var time)) continue;
                var previous = row.AccountTime!.Value;
                row.AccountTime = time;
                state.Move(row, previous);
                changed = true;
                break;
            }
            if (changed) continue;

            var nextIncomeTime = records.Where(row => row.AccountTime > negativeTime && row.TradeMoney > 0)
                .Select(row => row.AccountTime!.Value).FirstOrDefault();
            foreach (var row in records.Where(row => row.AccountTime <= negativeTime && row.TradeMoney < 0)
                         .Where(IsGeneratedReferenceRecord).OrderBy(row => row.TradeMoney).ToList())
            {
                if (ResolveRecordSourceRule(request, row) is not GenerateReferenceRule rule) continue;
                var latest = GetRequiredMonthlyCount(rule) > 0
                    ? MinDateTime(end, NormalizeEndDate(new DateTime(row.AccountTime!.Value.Year, row.AccountTime.Value.Month,
                        DateTime.DaysInMonth(row.AccountTime.Value.Year, row.AccountTime.Value.Month)))) : end;
                var earliest = MaxDateTime(negativeTime.AddSeconds(1), nextIncomeTime);
                if (!TryPickBankOfChinaRepairTime(state, rule, earliest, latest,
                        random, latestFirst: false, out var time)) continue;
                var previous = row.AccountTime!.Value;
                row.AccountTime = time;
                state.Move(row, previous);
                changed = true;
                break;
            }
            if (changed) continue;

            // Fixed dates and monthly required occurrences stay in place. Equal amount
            // transfers are the fallback and preserve both direction totals and counts.
            var needed = RoundMoney(Math.Abs(firstNegative.Balance ?? 0) + 1);
            if (!TryTransferBankOfChinaAmount(records, negativeTime, needed, isIncome: false)
                && !TryTransferBankOfChinaAmount(records, negativeTime, needed, isIncome: true)) break;
        }
        ApplyBalances(records, openingBalance);
    }

    private static bool TryPickBankOfChinaRepairTime(
        NativeScheduleState state, GenerateReferenceRule rule, DateTime start, DateTime end,
        Random random, bool latestFirst, out DateTime time)
    {
        time = default;
        if (start > end) return false;
        var startHour = Math.Clamp(rule.StartDay ?? 9, 0, 23);
        var endHour = Math.Clamp(rule.EndDay ?? 17, 0, 23);
        if (endHour < startHour) (startHour, endHour) = (endHour, startHour);
        var dates = state.GetCandidateDates(start, end, rule);
        foreach (var date in latestFirst ? dates.OrderByDescending(date => date) : dates.OrderBy(date => date))
        {
            var earliest = MaxDateTime(start, date.Date.AddHours(startHour));
            var latest = MinDateTime(end, date.Date.AddHours(endHour + 1).AddSeconds(-1));
            if (earliest > latest) continue;
            var seconds = (int)(latest - earliest).TotalSeconds;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var candidate = earliest.AddSeconds(random.Next(seconds + 1));
                if (state.IsTimeUsed(candidate)) continue;
                time = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool TryTransferBankOfChinaAmount(
        List<FlowRecord> records, DateTime negativeTime, double needed, bool isIncome)
    {
        var signed = records.Where(row => !IsSystemInterestRecord(row))
            .Where(row => isIncome ? row.TradeMoney > 0 : row.TradeMoney < 0).ToList();
        var donors = signed.Where(row => isIncome ? row.AccountTime > negativeTime : row.AccountTime <= negativeTime)
            .OrderByDescending(row => Math.Abs(row.TradeMoney ?? 0) - GetRecordAmountBounds(row).Min).ToList();
        var receivers = signed.Where(row => isIncome ? row.AccountTime <= negativeTime : row.AccountTime > negativeTime)
            .OrderByDescending(row => GetRecordAmountBounds(row).Max - Math.Abs(row.TradeMoney ?? 0)).ToList();
        foreach (var donor in donors)
        foreach (var receiver in receivers)
        {
            var donorBefore = Math.Abs(donor.TradeMoney ?? 0);
            var receiverBefore = Math.Abs(receiver.TradeMoney ?? 0);
            var donorBounds = GetRecordAmountBounds(donor);
            var receiverBounds = GetRecordAmountBounds(receiver);
            var unit = Math.Max(donorBounds.Unit, receiverBounds.Unit);
            var room = Math.Min(donorBefore - donorBounds.Min, receiverBounds.Max - receiverBefore);
            var step = Math.Min(CeilAmountToUnit(needed, unit), FloorAmountToUnit(room, unit));
            for (var attempt = 0; attempt < 10 && step > 0.009d; attempt++, step = RoundMoney(step - unit))
            {
                var donorNext = ClampAmountToBounds(donorBefore - step, donorBounds);
                var receiverNext = ClampAmountToBounds(receiverBefore + step, receiverBounds);
                var decrease = RoundMoney(donorBefore - donorNext);
                var increase = RoundMoney(receiverNext - receiverBefore);
                if (decrease <= 0.009d || Math.Abs(decrease - increase) > 0.009d) continue;
                ApplySignedAmount(donor, isIncome ? 1 : -1, donorNext);
                ApplySignedAmount(receiver, isIncome ? 1 : -1, receiverNext);
                return true;
            }
        }
        return false;
    }

    private static FlowAutoGenerationResult CreateBankOfChinaBalanceResult(
        List<FlowRecord> records, double openingBalance, double lastMoney)
    {
        ApplyBalances(records, openingBalance);
        var final = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimum = GetMinimumBalance(records, openingBalance);
        return new FlowAutoGenerationResult
        {
            Records = records, OpeningBalance = openingBalance, IncomeTotal = SumIncome(records), ExpenseTotal = SumExpense(records),
            FinalBalance = RoundMoney(final), MinimumBalance = minimum,
            RequiresOpeningBalanceCorrection = minimum < -0.009d || IsFinalBalanceOutsideTolerance(final, openingBalance, lastMoney),
            RequiredOpeningBalance = RoundMoney(minimum < -0.009d ? openingBalance - minimum : openingBalance)
        };
    }
}
