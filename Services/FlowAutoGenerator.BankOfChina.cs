using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private static FlowAutoGenerationResult WithBankOfChinaDiagnostics(
        FlowAutoGenerationRequest request, string operation, Func<FlowAutoGenerationResult> generate)
    {
        if (!BankOfChinaGenerationBudget.AppliesTo(request.Bank)) return generate();
        using var scope = BankOfChinaGenerationDiagnostics.Begin(operation, request);
        try
        {
            var result = generate();
            BankOfChinaGenerationDiagnostics.Result(result);
            return result;
        }
        catch (Exception exception)
        {
            BankOfChinaGenerationDiagnostics.Failure(exception);
            throw;
        }
    }

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

        var occurrences = CreateBankOfChinaReferenceOccurrences(request, plan, schedule, random);
        var requiredIncomeMinimum = RoundMoney(occurrences.Where(item => IsIncomeRuleSafe(item.Rule))
            .Sum(item => GetRuleAmountBounds(item.Rule).Min));
        var requiredExpenseMinimum = RoundMoney(occurrences.Where(item => !IsIncomeRuleSafe(item.Rule))
            .Sum(item => GetRuleAmountBounds(item.Rule).Min));
        if (requiredIncomeMinimum > incomeCap + 0.009d)
            throw new ReferenceBudgetConflictException("中行每月必现收入项目的最低金额已超过总预算允许的10%浮动范围");
        GenerateNativeConstRecords(request, start, end, RoundMoney(incomeCap - requiredIncomeMinimum), random, records, schedule, usage);
        var constExpenseCap = RoundMoney(BankOfChinaGenerationBudget.Bounds(expenseTarget).Max - requiredExpenseMinimum);
        if (SumExpense(records) > constExpenseCap + 0.009d)
            AdjustBankOfChinaSignedTotal(records, isIncome: false, Math.Max(0, constExpenseCap));
        GenerateBankOfChinaRequiredReferences(request, occurrences, incomeCap, random, records, schedule, usage);
        BankOfChinaGenerationDiagnostics.Stage("after-required-references", records);
        EnsureNativeOptionalReferenceRuleCoverage(request, plan, incomeCap, random, records, schedule, usage);
        GenerateNativeOptionalReferenceRecords(request, plan, incomeCap, random, records, schedule, usage);
        EnsureNativeOptionalReferenceRuleCoverage(request, plan, incomeCap, random, records, schedule, usage);
        CompleteNativeConfiguredTotals(request, plan, start, end, incomeTarget, random, records, schedule, usage);
        BankOfChinaGenerationDiagnostics.Stage("before-closing", records);
        foreach (var group in occurrences.GroupBy(item => (item.Rule.Index, item.Start, item.End)))
        {
            var actual = records.Count(row => IsGeneratedReferenceRecord(row)
                && row.ExtraFields.GetValueOrDefault(SourceIndexField) == group.Key.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && IsRecordInRange(row, group.Key.Start, group.Key.End));
            if (actual != group.Count())
                throw new InvalidOperationException($"中行参照明细第{group.Key.Index}行在{group.Key.Start:yyyy年MM月}未保留计划次数");
        }

        // Do not run the legacy amount-shaping/closing passes: they can delete income
        // to achieve the final balance and then accept a much smaller statement.
        return FinalizeBankOfChinaResult(request, records, start, end, openingBalance, random, repairNegative: false);
    }

    private sealed record BankOfChinaReferenceOccurrence(GenerateReferenceRule Rule, DateTime Start, DateTime End, DateTime? Date);

    private static List<BankOfChinaReferenceOccurrence> CreateBankOfChinaReferenceOccurrences(
        FlowAutoGenerationRequest request, MonthlyAmountPlan plan, NativeScheduleState schedule, Random random)
    {
        var result = new List<BankOfChinaReferenceOccurrence>();
        var required = request.References.Where(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0).ToArray();
        foreach (var month in plan.Months)
        foreach (var rule in required)
        {
            var dates = schedule.GetCandidateDates(month.Start, month.End, rule);
            if (dates.Count == 0)
            {
                // No occurrence is planned, reserved or validated for a month
                // that cannot legally host this reference. Keep other months.
                BankOfChinaGenerationDiagnostics.SkippedReferenceMonth(rule.Index, month.Start, month.End);
                continue;
            }
            var days = DateTime.DaysInMonth(month.Start.Year, month.Start.Month);
            var partial = request.Config.SelectIndex == 0 && (month.Start.Day != 1 || month.End.Day != days);
            var allowed = partial ? dates.Select(date => date.Date).ToHashSet() : null;
            for (var attempt = 0; attempt < GetRequiredMonthlyCount(rule); attempt++)
            {
                DateTime? date = null;
                if (partial)
                {
                    date = new DateTime(month.Start.Year, month.Start.Month, 1).AddDays(random.Next(days));
                    if (!allowed!.Contains(date.Value)) continue;
                }
                result.Add(new BankOfChinaReferenceOccurrence(rule, month.Start, month.End, date));
                if (result.Count > MaximumFixedRuleRecordCount)
                    throw new InvalidOperationException($"中行每月参照项目超过单次上限{MaximumFixedRuleRecordCount:N0}笔");
            }
        }
        return result;
    }

    private static void GenerateBankOfChinaRequiredReferences(
        FlowAutoGenerationRequest request, IReadOnlyList<BankOfChinaReferenceOccurrence> occurrences,
        double incomeCap, Random random, List<FlowRecord> records, NativeScheduleState schedule, AmountUsageTracker usage)
    {
        if (occurrences.Count + records.Count > MaximumFixedRuleRecordCount)
            throw new InvalidOperationException($"中行固定及每月必现项目超过单次上限{MaximumFixedRuleRecordCount:N0}笔");
        var incomeMinimum = RoundMoney(occurrences.Where(item => IsIncomeRuleSafe(item.Rule))
            .Sum(item => GetRuleAmountBounds(item.Rule).Min));
        var expenseMinimum = RoundMoney(occurrences.Where(item => !IsIncomeRuleSafe(item.Rule))
            .Sum(item => GetRuleAmountBounds(item.Rule).Min));
        var expenseCap = BankOfChinaGenerationBudget.Bounds(BankOfChinaGenerationBudget.ExpenseTarget(request.Config)).Max;
        if (incomeMinimum + SumIncome(records) > incomeCap + 0.009d
            || expenseMinimum + SumExpense(records) > expenseCap + 0.009d)
            throw new ReferenceBudgetConflictException("中行固定及每月必现项目的最低金额已超过总预算允许的10%浮动范围");

        foreach (var occurrence in occurrences)
        {
            var rule = occurrence.Rule;
            var income = IsIncomeRuleSafe(rule);
            var bounds = GetRuleAmountBounds(rule);
            var futureMinimum = RoundMoney((income ? incomeMinimum : expenseMinimum) - bounds.Min);
            var current = income ? SumIncome(records) : SumExpense(records);
            var available = RoundMoney((income ? incomeCap : expenseCap) - current - futureMinimum);
            if (available < bounds.Min - 0.009d)
                throw new ReferenceBudgetConflictException("中行每月必现项目无法在总预算±10%内满足配置次数");
            // Soft month allocations must not collapse an otherwise feasible range.
            var cap = Math.Min(available, bounds.Max);
            if (!TryCreateNativeUniqueAmount(rule, cap, random, preferLower: true, records, income, usage, out var amount))
                amount = bounds.Min;
            var time = occurrence.Date is { } day
                ? PickNativeTimeOnDate(day, rule, random, schedule, schedule.NextSequence())
                : PickNativeDistributedTime(occurrence.Start, occurrence.End, rule, income, random, schedule);
            var row = CreateRecordFromRule(request, rule, request.Bank.ReferenceColumns, time, income ? amount : -amount);
            records.Add(row); schedule.Register(row); usage.Register(row);
            if (income) incomeMinimum = futureMinimum; else expenseMinimum = futureMinimum;
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
        var runSeed = CreateRunSeed(request, start, end, records.Count ^ 0x61B0C);
        BankOfChinaGenerationDiagnostics.Seed(runSeed);
        var random = new Random(runSeed);
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
            BankOfChinaGenerationDiagnostics.Stage("before-closing-pass", records);
            var income = SumIncome(records);
            if (income < incomeBounds.Min - 0.009d || income > incomeBounds.Max + 0.009d)
            {
                AdjustBankOfChinaSignedTotal(records, isIncome: true, Math.Clamp(income, incomeBounds.Min, incomeBounds.Max));
                MoveBankOfChinaIncomeInsideBand(records, incomeBounds.Min, incomeBounds.Max);
            }

            // Debit budget follows actual gross income (including settlement interest).
            // If the percentage band limits it, the original final-balance tolerance applies.
            var desiredExpense = Math.Clamp(SumIncome(records) - request.Config.LastMoney, expenseBounds.Min, expenseBounds.Max);
            AdjustBankOfChinaSignedTotal(records, isIncome: false, desiredExpense);
            ApplyBalances(records, openingBalance);
            if (repairNegative && GetMinimumBalance(records, openingBalance) < -0.009d)
            {
                RepairBankOfChinaNegativeBalances(request, records, start, end, openingBalance, random);
                BankOfChinaGenerationDiagnostics.Stage("after-negative-balance-repair", records);
            }

            // Time/amount repair changes the daily ledger, so recompute settlement before
            // deciding whether this candidate is stable. Never close by deleting income.
            RecalculateInterestRecords(records, request, openingBalance, start, end, random);
            ApplyBalances(records, openingBalance);
            BankOfChinaGenerationDiagnostics.Stage("after-settlement", records);
            if (signature == CalculateBalanceInputSignature(records)) break;
        }

        var result = CreateBankOfChinaBalanceResult(records, openingBalance, request.Config.LastMoney);
        if (!BankOfChinaGenerationBudget.TryValidate(request.Bank, request.Config, result, out var reason))
            throw new ReferenceBudgetConflictException(reason);
        if (IsFinalBalanceOutsideTolerance(result.FinalBalance, openingBalance, request.Config.LastMoney))
            throw new ReferenceBudgetConflictException(
                $"中行无法在保留流水、金额范围和总收支±10%的条件下满足期末余额：" +
                $"目标 {CalculateFinalBalanceTarget(openingBalance, request.Config.LastMoney):N2}，实际 {result.FinalBalance:N2}");
        // The shared cashflow planner handles a remaining deficit without an
        // interactive confirmation and funds only unavoidable hard constraints.
        return result;
    }

    private static bool AdjustBankOfChinaSignedTotal(List<FlowRecord> records, bool isIncome, double target)
    {
        var current = isIncome ? SumIncome(records) : SumExpense(records);
        var initial = current;
        var changed = false;
        var increasing = target > current;
        var candidates = OrderAmountRepairCandidates(records.Where(row => !IsSystemInterestRecord(row))
                .Where(row => isIncome ? row.TradeMoney > 0 : row.TradeMoney < 0))
            .OrderBy(row => GetRecordAmountBounds(row).Unit).ToList();
        // Settle the bulk with coarser reference amounts first. Consuming the
        // cent-precision capacity first can pin almost every small payment to
        // its minimum, leaving no budget for the final chronological tail pass.
        // Never overshoot: finer rows only need to cover the remaining residue.
        foreach (var row in candidates.Where(IsGeneratedReferenceRecord).Where(row => !row.IsDocumentImported)
                     .Where(row => GetRecordAmountBounds(row).Unit >= 0.1d - 0.0000001d)
                     .OrderByDescending(row => GetRecordAmountBounds(row).Unit))
        {
            var remaining = RoundMoney(target - current);
            if (Math.Abs(remaining) <= 0.009d)
            {
                BankOfChinaGenerationDiagnostics.Closing("coarse-complete", records, isIncome, target, initial, current);
                return changed;
            }
            var before = Math.Abs(row.TradeMoney ?? 0);
            var bounds = GetRecordAmountBounds(row);
            var step = FloorAmountToUnit(Math.Abs(remaining), bounds.Unit);
            if (step < bounds.Unit - 0.0000001d) continue;
            var next = increasing
                ? FindValidAmountAtOrBelow(Math.Min(bounds.Max, before + step), before + bounds.Unit, bounds)
                : FindValidAmountAtOrAbove(Math.Max(bounds.Min, before - step), before - bounds.Unit, bounds);
            if (!next.HasValue) continue;
            var delta = RoundMoney(next.Value - before);
            if ((delta > 0) != increasing || Math.Abs(delta) <= 0.009d || Math.Abs(delta) > Math.Abs(remaining) + 0.000001d) continue;
            ApplySignedAmount(row, isIncome ? 1 : -1, next.Value);
            current = RoundMoney(current + delta);
            changed = true;
        }
        BankOfChinaGenerationDiagnostics.Closing("coarse-residue", records, isIncome, target, initial, current);
        // Prefer a legal non-low tail before settlement is calculated. Unlike
        // a post-settlement edit this does not need to undo rounded interest.
        // Commit only a complete plan; otherwise retain the original capacity
        // and closing algorithm, including fixed-date amounts and exact totals.
        var preferred = new List<(FlowRecord Row, double Amount)>();
        var preferredTotal = current;
        foreach (var row in candidates.Where(IsGeneratedReferenceRecord).Where(row => !row.IsDocumentImported))
        {
            var remaining = RoundMoney(target - preferredTotal);
            if (Math.Abs(remaining) <= 0.009d) break;
            var bounds = GetRecordAmountBounds(row);
            if (bounds.Precision != 2 || Math.Abs(bounds.Unit - 0.01d) > 0.0000001d || bounds.Max <= bounds.Min) continue;
            var before = Math.Abs(row.TradeMoney ?? 0);
            var next = ClampAmountToBounds(Math.Clamp(before + remaining, bounds.Min, bounds.Max), bounds);
            var tail = (int)Math.Round(next * 100d, MidpointRounding.AwayFromZero) % 100;
            if (tail is >= 1 and <= 9 || !increasing && tail == 11 && Math.Floor(next) == Math.Floor(bounds.Min))
            {
                var ticks = (ulong)(row.AccountTime?.Ticks ?? row.Index);
                var bucket = (int)((ticks ^ (ticks >> 17) ^ (uint)row.Index * 0x9E3779B9u) % 72);
                var preferredTail = 11 + bucket / 9 * 10 + bucket % 9;
                next = RoundMoney(Math.Floor(next) - (increasing ? 1 : 0) + preferredTail / 100d);
            }
            if (next < bounds.Min - 0.000001d || next > bounds.Max + 0.000001d) continue;
            var delta = RoundMoney(next - before);
            if ((delta > 0) != increasing || Math.Abs(delta) <= 0.009d
                || Math.Abs(target - (preferredTotal + delta)) >= Math.Abs(remaining) - 0.000001d) continue;
            preferred.Add((row, next)); preferredTotal = RoundMoney(preferredTotal + delta);
        }
        if (preferred.Count > 0 && Math.Abs(preferredTotal - target) <= 0.009d)
        {
            foreach (var item in preferred) ApplySignedAmount(item.Row, isIncome ? 1 : -1, item.Amount);
            BankOfChinaGenerationDiagnostics.Closing("fractional-preferred", records, isIncome, target, initial, preferredTotal);
            return true;
        }
        BankOfChinaGenerationDiagnostics.Closing("fractional-fallback", records, isIncome, target, initial, current);
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
        BankOfChinaGenerationDiagnostics.Closing("closing-complete", records, isIncome, target, initial, current);
        return changed;
    }

    private static void MoveBankOfChinaIncomeInsideBand(List<FlowRecord> records, double minimum, double maximum)
    {
        var income = SumIncome(records);
        if (income >= minimum - 0.009d && income <= maximum + 0.009d) return;
        var increasing = income < minimum;
        foreach (var row in OrderAmountRepairCandidates(records.Where(row => row.TradeMoney > 0)
                         .Where(IsGeneratedReferenceRecord).Where(row => !row.IsDocumentImported))
                     .OrderBy(row => GetRecordAmountBounds(row).Unit))
        {
            var before = row.TradeMoney!.Value;
            var bounds = GetRecordAmountBounds(row);
            var difference = increasing ? minimum - income : income - maximum;
            // Gross income includes cents of interest, but a transfer can still
            // have a 100/1000-yuan denomination. Enter the existing permitted
            // band at the next legal denomination, rather than demanding its
            // generally unattainable exact cent boundary.
            var next = increasing
                ? FindValidAmountAtOrAbove(before + difference, bounds.Max, bounds)
                : FindValidAmountAtOrBelow(before - difference, bounds.Min, bounds);
            if (!next.HasValue) continue;
            var total = RoundMoney(income + next.Value - before);
            if (total < minimum - 0.009d || total > maximum + 0.009d) continue;
            ApplySignedAmount(row, 1, next.Value);
            BankOfChinaGenerationDiagnostics.Closing("income-band-denomination", records, true,
                Math.Clamp(income, minimum, maximum), income, total);
            return;
        }
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
