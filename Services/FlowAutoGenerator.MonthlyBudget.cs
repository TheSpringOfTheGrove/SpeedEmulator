using System.Globalization;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private const string BudgetWindowStart = "__GeneratedBudgetWindowStart";
    private const string BudgetWindowEnd = "__GeneratedBudgetWindowEnd";
    private const string BudgetCoverageCandidate = "__GeneratedBudgetCoverageCandidate";

    private static DateTime BudgetMonth(DateTime time) => new(time.Year, time.Month, 1);

    private static Dictionary<DateTime, double>? CreateSparseMonthBudgets(FlowAutoGenerationRequest request,
        List<FlowRecord> records, HashSet<FlowRecord> soft, double budget, DateTime start, DateTime end,
        Random random, HashSet<DateTime> used)
    {
        var months = EnumerateMonths(start, end).ToArray();
        if (months.Length < 3 || soft.Count == 0) return null;
        var affordable = soft.Select(row => (Row: row, Bounds: GetRecordAmountBounds(row)))
            .Select(item => (item.Row, item.Bounds, Minimum: FindValidAmountAtOrAbove(item.Bounds.Min, Math.Min(item.Bounds.Max, budget), item.Bounds)))
            .Where(item => item.Minimum.HasValue).ToArray();
        if (affordable.Length == 0) return null;
        var minimum = affordable.Min(item => item.Minimum!.Value);
        var typicalMinimumDraw = affordable.GroupBy(item => BudgetMonth(item.Row.AccountTime!.Value))
            .Average(group => group.Min(item => (item.Minimum!.Value + Math.Min(item.Bounds.Max, budget)) / 2d));
        if (minimum <= budget / months.Length + .009d && typicalMinimumDraw <= budget / months.Length + .009d) return null;
        // When natural full-range draws cannot be sustained monthly, reserve a
        // few feasible events across separate portions of the full period. Do
        // not redraw every month near its minimum or postpone all large events.
        var mean = affordable.Average(item => (item.Minimum!.Value + Math.Min(item.Bounds.Max, budget)) / 2d);
        var groups = affordable.GroupBy(item => BudgetMonth(item.Row.AccountTime!.Value)).OrderBy(group => group.Key).ToArray();
        var count = Math.Clamp((int)Math.Round(budget / Math.Max(minimum, mean)), 1,
            Math.Min(groups.Length, Math.Max(1, (int)Math.Floor(budget / minimum + .000001))));
        var selected = new HashSet<FlowRecord>(); var remaining = budget;
        for (var index = 0; index < count; index++)
        {
            var first = index * groups.Length / count; var last = (index + 1) * groups.Length / count;
            var options = groups.Skip(first).Take(last - first).OrderBy(_ => random.Next());
            foreach (var group in options)
            {
                var maximum = RoundMoney(remaining - minimum * (count - index - 1));
                var candidates = group.Where(item => item.Minimum <= maximum + .000001).OrderBy(_ => random.Next()).ToArray();
                if (candidates.Length == 0) continue;
                var chosen = candidates[0]; var row = chosen.Row;
                var amount = Math.Abs(row.TradeMoney!.Value);
                if (index == count - 1)
                    amount = FindValidAmountAtOrBelow(Math.Min(chosen.Bounds.Max, maximum), chosen.Minimum!.Value, chosen.Bounds) ?? amount;
                else if (amount > maximum + .009d && ResolveRecordSourceRule(request, row) is { } rule
                    && TrySampleReferenceNaturalRangeAmount(rule, maximum, random, out var sampled)) amount = sampled;
                if (amount > maximum + .009d) continue;
                var previous = row.TradeMoney!.Value;
                ApplySignedAmount(row, -1, amount);
                RefreshCashflowDerivedFields(request, row, row.AccountTime!.Value, previous);
                selected.Add(row); remaining = RoundMoney(remaining - amount); break;
            }
        }
        if (selected.Count == 0) return null;
        foreach (var row in soft.Where(row => !selected.Contains(row)).ToArray())
        {
            records.Remove(row); soft.Remove(row); used.Remove(row.AccountTime!.Value);
        }
        var accrued = 0d; var result = new Dictionary<DateTime, double>();
        foreach (var month in months)
        {
            var key = BudgetMonth(month.Start);
            accrued = RoundMoney(accrued + selected.Where(row => BudgetMonth(row.AccountTime!.Value) == key).Sum(row => -row.TradeMoney!.Value));
            result[key] = accrued;
        }
        return result;
    }

    private static bool TryGenerateMonthlyBudgetedNaturalResult(FlowAutoGenerationRequest request, DateTime start, DateTime end,
        double opening, Random random, int seed, out FlowAutoGenerationResult? result)
    {
        result = null;
        if (!request.References.Any(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0
                && GetRuleAmountBounds(rule).Max > GetRuleAmountBounds(rule).Min + .000001)) return false;
        var draft = new List<FlowRecord>(); var state = new NativeScheduleState();
        var fixedUsage = new Dictionary<DateTime, int>();
        foreach (var rule in request.ConstItems.Where(rule => rule.IsCheck && !string.IsNullOrWhiteSpace(rule.FixDay)))
        foreach (var day in ResolveNativeFixedOccurrences(rule, start, end, random, state, fixedUsage))
        {
            if (!TrySampleReferenceNaturalRangeAmount(rule, double.MaxValue, random, out var amount)) continue;
            AddReferenceNaturalRecord(request, rule, day, amount, random, draft, state);
            fixedUsage[day.Date] = fixedUsage.GetValueOrDefault(day.Date) + 1;
        }
        // Preserve the established full-month date draw and calendar filtering.
        // Draw incomes without consuming an annual quota in reference-list order.
        foreach (var rule in request.References.Where(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0))
        foreach (var month in EnumerateMonths(start, end))
        for (var attempt = 0; attempt < GetRequiredMonthlyCount(rule); attempt++)
        {
            var day = BudgetMonth(month.Start).AddDays(random.Next(DateTime.DaysInMonth(month.Start.Year, month.Start.Month)));
            if (day < month.Start.Date || day > month.End.Date || !ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day, rule)) continue;
            if (TrySampleReferenceNaturalRangeAmount(rule, double.MaxValue, random, out var amount))
                AddReferenceNaturalRecord(request, rule, day, amount, random, draft, state);
        }
        var balance = opening; var minimum = balance;
        foreach (var row in draft.OrderBy(row => row.AccountTime)) { balance += row.TradeMoney ?? 0; minimum = Math.Min(minimum, balance); }
        // A fully funded replay already retains its natural dates and draws.
        // Do not change its established sampling sequence for quota cosmetics.
        if (opening > .009d && opening >= SumExpense(draft) - .009d) return false;
        if (SumIncome(draft) <= request.Config.AllInMoney + .009d
            && SumExpense(draft) <= opening + request.Config.AllInMoney - Math.Max(0, request.Config.LastMoney) + .009d
            && minimum >= -.009d) return false;

        TraceReferenceNaturalGeneration(request, "monthly-budget-full-sample", draft, seed, opening);
        var variableIncome = draft.Where(row => row.TradeMoney > 0 && IsGeneratedReferenceRecord(row)
            && GetRecordAmountBounds(row).Max > GetRecordAmountBounds(row).Min + .000001).ToArray();
        var rows = draft.Except(variableIncome).ToList();
        var incomePool = request.References.Where(rule => rule.IsCheck && IsIncomeRuleSafe(rule) && GetRequiredMonthlyCount(rule) == 0).ToArray();
        var incomeUnit = request.References.Where(rule => rule.IsCheck && IsIncomeRuleSafe(rule))
            .Select(rule => GetRuleAmountBounds(rule).Unit).DefaultIfEmpty(.01d).Min();
        var target = RoundMoney(Math.Max(SumIncome(rows), request.Config.AllInMoney
            - random.NextDouble() * Math.Min(request.Config.AllInMoney * .1d, incomeUnit * 9d)));
        var variableBudget = Math.Max(0, RoundMoney(target - SumIncome(rows)));
        var limits = CreateAccruedMonthBudgets(start, end, variableBudget, random);
        var spent = 0d;
        foreach (var month in EnumerateMonths(start, end))
        {
            var key = BudgetMonth(month.Start); var monthIncome = new List<FlowRecord>();
            var candidates = variableIncome.Where(row => BudgetMonth(row.AccountTime!.Value) == key).OrderBy(_ => random.Next()).ToList();
            foreach (var row in candidates.ToArray())
            {
                var available = Math.Max(0, RoundMoney(limits[key] - spent));
                if (row.TradeMoney > available + .009d) continue;
                rows.Add(row); monthIncome.Add(row); candidates.Remove(row); spent = RoundMoney(spent + row.TradeMoney!.Value);
            }
            var optional = incomePool.Where(rule => EnumerateDates(month.Start.Date, month.End.Date)
                .Any(day => ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day, rule))).ToArray();
            var attempts = 0;
            while (spent < limits[key] - .009d && attempts++ < MaximumFixedRuleRecordCount)
            {
                var available = Math.Max(0, RoundMoney(limits[key] - spent));
                var affordable = optional.Where(rule => GetRuleAmountBounds(rule).Min <= available + .000001).ToArray();
                var required = candidates.Where(row => GetRecordAmountBounds(row).Min <= available + .000001).ToArray();
                FlowRuleBase? rule = affordable.Length > 0 ? affordable[random.Next(affordable.Length)]
                    : required.Length > 0 ? ResolveRecordSourceRule(request, required[random.Next(required.Length)]) : null;
                // A high-minimum source may afford only a few payments for the
                // whole year. Allow one legal draw, then carry its budget debt
                // forward rather than fill every month with artificial minima.
                if (rule is null && monthIncome.Count == 0 && spent < limits[key])
                {
                    var sparse = optional.Cast<FlowRuleBase>().Concat(candidates.Select(row => ResolveRecordSourceRule(request, row)!))
                        .Where(source => GetRuleAmountBounds(source).Min > variableBudget / Math.Max(1, limits.Count)
                            && GetRuleAmountBounds(source).Min <= variableBudget - spent + .000001).ToArray();
                    if (sparse.Length > 0) { rule = sparse[random.Next(sparse.Length)]; available = RoundMoney(variableBudget - spent); }
                }
                if (rule is null || !TrySampleReferenceNaturalRangeAmount(rule, available, random, out var amount)) break;
                FlowRecord? row;
                if (rule is GenerateReferenceRule reference && GetRequiredMonthlyCount(reference) > 0)
                {
                    row = candidates.FirstOrDefault(candidate => ReferenceEquals(ResolveRecordSourceRule(request, candidate), rule));
                    if (row is null) break;
                    candidates.Remove(row); var previousAmount = row.TradeMoney!.Value;
                    ApplySignedAmount(row, 1, amount);
                    RefreshCashflowDerivedFields(request, row, row.AccountTime!.Value, previousAmount);
                }
                else row = CreateBudgetedNaturalRecord(request, rule, month.Start, month.End, amount, random, rows);
                if (row is null) break;
                rows.Add(row); monthIncome.Add(row); spent = RoundMoney(spent + amount);
            }
            // Absorb a small precision tail into this month's existing income;
            // never move a later month's income or change its reference identity.
            var remainder = Math.Max(0, RoundMoney(limits[key] - spent));
            foreach (var row in monthIncome.OrderBy(_ => random.Next()))
            {
                if (remainder < incomeUnit - .000001) break;
                var bounds = GetRecordAmountBounds(row); var before = row.TradeMoney!.Value;
                var after = FindValidAmountAtOrBelow(Math.Min(bounds.Max, before + remainder), before, bounds);
                if (!after.HasValue || after <= before + .009d) continue;
                ApplySignedAmount(row, 1, after.Value);
                RefreshCashflowDerivedFields(request, row, row.AccountTime!.Value, before);
                spent = RoundMoney(spent + after.Value - before);
                remainder = Math.Max(0, RoundMoney(limits[key] - spent));
            }
            foreach (var row in monthIncome) BindRecordToBudgetMonth(row, start, end);
            // One early legal receipt supplies the month's cash. Remaining
            // receipts use the least crowded legal days, not month-end seconds.
            var used = rows.Where(row => row.AccountTime.HasValue).Select(row => row.AccountTime!.Value).ToHashSet();
            var usage = rows.Where(row => row.AccountTime.HasValue).GroupBy(row => row.AccountTime!.Value.Date)
                .ToDictionary(group => group.Key, group => group.Count());
            var first = true;
            foreach (var row in monthIncome.OrderByDescending(row => row.TradeMoney))
            {
                var window = ResolveCashflowWindow(request, row, start, end); var previous = row.AccountTime!.Value;
                used.Remove(previous); usage[previous.Date] = Math.Max(0, usage.GetValueOrDefault(previous.Date) - 1);
                if (TryBalancedCashflowTime(request, window, month.Start, month.End, random, used, usage, out var time, earliestDay: first))
                {
                    MoveCashflowRow(request, row, time, used); usage[time.Date] = usage.GetValueOrDefault(time.Date) + 1; first = false;
                }
                else { used.Add(previous); usage[previous.Date] = usage.GetValueOrDefault(previous.Date) + 1; }
            }
        }
        // A partial last month can have no successful monthly income attempts.
        // Its small unreachable remainder may use existing legal income capacity;
        // source identity, occurrence counts and all assigned months stay intact.
        var beforeTail = rows.Where(row => row.TradeMoney > 0).ToDictionary(row => row, row => row.TradeMoney!.Value);
        CompleteReferenceNaturalOptionalDirection(rows, target, true, includeRequiredIncome: true);
        foreach (var entry in beforeTail.Where(entry => Math.Abs(entry.Key.TradeMoney!.Value - entry.Value) > .009d))
            RefreshCashflowDerivedFields(request, entry.Key, entry.Key.AccountTime!.Value, entry.Value);

        // Optional expenses are also assigned to monthly windows before the
        // cashflow pass. Do not fill an annual tail with hundreds of tiny rows.
        var expenseTarget = Math.Max(0, RoundMoney(SumIncome(rows) - request.Config.LastMoney));
        var expenseLimits = CreateAccruedMonthBudgets(start, end, expenseTarget, random);
        var expensePool = request.References.Where(rule => rule.IsCheck && !IsIncomeRuleSafe(rule) && GetRequiredMonthlyCount(rule) == 0).ToArray();
        foreach (var month in EnumerateMonths(start, end))
        {
            var cumulative = RoundMoney(rows.Where(row => row.TradeMoney < 0 && row.AccountTime <= month.End).Sum(row => -row.TradeMoney!.Value));
            var available = Math.Max(0, RoundMoney(expenseLimits[BudgetMonth(month.Start)] - cumulative));
            var key = BudgetMonth(month.Start);
            var needsCoverage = !rows.Any(row => row.TradeMoney < 0 && IsGeneratedReferenceRecord(row)
                    && GetRecordAmountBounds(row).Max > GetRecordAmountBounds(row).Min + .000001
                    && BudgetMonth(row.AccountTime!.Value) == key);
            if (needsCoverage)
            {
                // A month's required attempts may all be filtered out by the
                // calendar. Only zero-count optional sources can supply coverage;
                // never retry or inflate a required reference's monthly count.
                var previousLimit = expenseLimits.GetValueOrDefault(key.AddMonths(-1));
                available = Math.Max(available, RoundMoney(expenseLimits[key] - previousLimit));
            }
            var pool = expensePool.Where(rule => EnumerateDates(month.Start.Date, month.End.Date)
                .Any(day => ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day, rule))).ToArray();
            var count = 0;
            while (available > .009d && count++ < 64)
            {
                var affordable = pool.Where(rule => GetRuleAmountBounds(rule).Min <= available + .000001).ToArray();
                if (affordable.Length == 0) break;
                var rule = affordable[random.Next(affordable.Length)];
                if (!TrySampleReferenceNaturalRangeAmount(rule, available, random, out var amount)) break;
                var row = CreateBudgetedNaturalRecord(request, rule, month.Start, month.End, -amount, random, rows);
                if (row is null) break;
                if (needsCoverage) { row.ExtraFields[BudgetCoverageCandidate] = "1"; needsCoverage = false; }
                rows.Add(row); available = RoundMoney(available - amount);
            }
        }
        foreach (var row in rows) BindRecordToBudgetMonth(row, start, end);
        TraceReferenceNaturalGeneration(request, "monthly-budget-before-cashflow", rows, seed, opening);
        result = EnsureAutomaticOpeningBalance(request, CreateAutomaticBalanceResult(rows, opening), recalculateInterest: true);
        if (FillMonthlyBudgetExpenseCapacity(request, start, end, expenseTarget, random, result))
            result = EnsureAutomaticOpeningBalance(request, CreateAutomaticBalanceResult(result.Records, result.OpeningBalance), recalculateInterest: true);
        TraceReferenceNaturalGeneration(request, "monthly-budget-complete", result.Records, seed, result.OpeningBalance);
        return true;
    }

    private static HashSet<FlowRecord> ReserveMonthlyOrdinaryCoverage(FlowAutoGenerationRequest request,
        HashSet<FlowRecord> soft, Dictionary<FlowRecord, CashflowWindow> windows, CashflowLedger ledger,
        double budget, DateTime start, DateTime end, Random random, HashSet<DateTime> used,
        Dictionary<DateTime, int> dayUsage, IReadOnlyDictionary<DateTime, double>? monthBudgets, List<FlowRecord> income)
    {
        var selected = new HashSet<FlowRecord>();
        if (soft.Count == 0 || monthBudgets is null && !soft.Any(row => row.ExtraFields.ContainsKey(BudgetWindowStart))) return selected;
        var groups = soft.GroupBy(row => BudgetMonth(row.AccountTime!.Value)).OrderBy(group => group.Key).ToArray();
        var limits = monthBudgets ?? CreateAccruedMonthBudgets(start, end, budget, random);
        var minimums = groups.Select(group => group.Select(row => GetRecordAmountBounds(row))
            .Select(bounds => FindValidAmountAtOrAbove(bounds.Min, bounds.Max, bounds)).Where(amount => amount.HasValue)
            .Select(amount => amount!.Value).DefaultIfEmpty(double.MaxValue).Min()).ToArray();
        var reserved = 0d;
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index];
            var futureMinimum = minimums.Skip(index + 1).Where(minimum => minimum <= budget + .009d).Sum();
            var remaining = Math.Max(0, RoundMoney(budget - reserved - futureMinimum));
            var share = Math.Max(0, RoundMoney(limits[group.Key] - limits.GetValueOrDefault(group.Key.AddMonths(-1))));
            var allowance = Math.Min(remaining, Math.Max(share, minimums[index]));
            foreach (var row in group.OrderBy(row => (GetRecordAmountBounds(row).Min + GetRecordAmountBounds(row).Max) / 2d).ThenBy(_ => random.Next()))
            {
                var bounds = GetRecordAmountBounds(row); var window = windows[row]; var preferred = row.AccountTime!.Value;
                var amount = Math.Abs(row.TradeMoney!.Value);
                if (bounds.Min <= allowance + .009d && (amount <= allowance + .009d || row.ExtraFields.GetValueOrDefault(BudgetCoverageCandidate) == "1"))
                    TryFundCashflowWindow(request, income, windows, ledger, window, Math.Min(amount, allowance), random, used, dayUsage);
                var capacity = TryCashflowTime(request, window, window.Start, window.End, random, used, true, out var last)
                    ? Math.Min(allowance, ledger.Headroom(last)) : 0;
                if (bounds.Min > capacity + .000001) continue;
                if (amount > capacity + .009d && row.ExtraFields.GetValueOrDefault(BudgetCoverageCandidate) == "1" && window.Rule is { } rule
                    && TrySampleReferenceNaturalRangeAmount(rule, capacity, random, out var sampled)) amount = sampled;
                if (amount > capacity + .009d) continue;
                var time = preferred;
                if (ledger.Headroom(preferred) < amount - .009d
                    && !TryPlaceCashflowDebit(request, ledger, window, preferred, amount, random, used, dayUsage, out time)) continue;
                var previous = row.TradeMoney!.Value;
                if (Math.Abs(previous + amount) > .009d)
                {
                    ApplySignedAmount(row, -1, amount);
                    RefreshCashflowDerivedFields(request, row, preferred, previous);
                }
                MoveCashflowRow(request, row, time, used); ledger.Add(time, -amount); selected.Add(row);
                reserved = RoundMoney(reserved + amount); dayUsage[time.Date] = dayUsage.GetValueOrDefault(time.Date) + 1;
                break;
            }
        }
        return selected;
    }

    private static bool FillMonthlyBudgetExpenseCapacity(FlowAutoGenerationRequest request, DateTime start, DateTime end,
        double target, Random random, FlowAutoGenerationResult result)
    {
        if (SumExpense(result.Records) >= target - .009d) return false;
        var pool = request.References.Where(rule => rule.IsCheck && !IsIncomeRuleSafe(rule) && GetRequiredMonthlyCount(rule) == 0).ToArray();
        if (pool.Length == 0) return false;
        var limits = CreateAccruedMonthBudgets(start, end, target, random);
        var ledger = new CashflowLedger(result.OpeningBalance);
        foreach (var row in result.Records) if (row.AccountTime is { } time) ledger.Add(time, row.TradeMoney ?? 0);
        ledger.Add(end.AddSeconds(1), -Math.Max(0, request.Config.LastMoney));
        var used = result.Records.Where(row => row.AccountTime.HasValue).Select(row => row.AccountTime!.Value).ToHashSet();
        var usage = result.Records.Where(row => row.AccountTime.HasValue).GroupBy(row => row.AccountTime!.Value.Date)
            .ToDictionary(group => group.Key, group => group.Count());
        var changed = false;
        foreach (var month in EnumerateMonths(start, end))
        {
            var cumulative = result.Records.Where(row => row.TradeMoney < 0 && row.AccountTime <= month.End).Sum(row => -row.TradeMoney!.Value);
            var remaining = Math.Min(RoundMoney(target - SumExpense(result.Records)), RoundMoney(limits[BudgetMonth(month.Start)] - cumulative));
            var attempts = 0;
            while (remaining > .009d && attempts++ < 64)
            {
                var candidates = pool.Select(rule =>
                {
                    var window = new CashflowWindow(rule, month.Start, month.End, true);
                    var capacity = TryCashflowTime(request, window, month.Start, month.End, random, used, true, out var last)
                        ? Math.Min(remaining, ledger.Headroom(last)) : 0;
                    return (Rule: rule, Window: window, Capacity: capacity);
                }).Where(candidate => GetRuleAmountBounds(candidate.Rule).Min <= candidate.Capacity + .000001).ToArray();
                if (candidates.Length == 0) break;
                var candidate = candidates[random.Next(candidates.Length)];
                if (!TrySampleReferenceNaturalRangeAmount(candidate.Rule, candidate.Capacity, random, out var amount)
                    || !TryPlaceCashflowDebit(request, ledger, candidate.Window, month.Start, amount, random, used, usage, out var time)) break;
                var row = CreateRecordFromRule(request, candidate.Rule, request.Bank.ReferenceColumns, time, -amount);
                BindRecordToBudgetMonth(row, start, end); result.Records.Add(row); used.Add(time);
                ledger.Add(time, -amount); usage[time.Date] = usage.GetValueOrDefault(time.Date) + 1;
                remaining = RoundMoney(remaining - amount); changed = true;
            }
        }
        var tail = RoundMoney(target - SumExpense(result.Records));
        foreach (var row in result.Records.Where(row => row.TradeMoney < 0 && IsGeneratedReferenceRecord(row)
                     && !IsRequiredGeneratedRecord(row)).OrderByDescending(row => row.AccountTime))
        {
            if (tail <= .009d) break;
            var before = Math.Abs(row.TradeMoney!.Value); var bounds = GetRecordAmountBounds(row);
            var capacity = Math.Max(0, ledger.Headroom(row.AccountTime!.Value));
            var after = FindValidAmountAtOrBelow(Math.Min(bounds.Max, before + Math.Min(tail, capacity)), before, bounds);
            if (!after.HasValue || after <= before + .009d) continue;
            var previous = row.TradeMoney!.Value;
            ApplySignedAmount(row, -1, after.Value);
            RefreshCashflowDerivedFields(request, row, row.AccountTime.Value, previous);
            var delta = RoundMoney(after.Value - before);
            ledger.Add(row.AccountTime.Value, -delta); tail = RoundMoney(tail - delta); changed = true;
        }
        return changed;
    }

    private static FlowRecord? CreateBudgetedNaturalRecord(FlowAutoGenerationRequest request, FlowRuleBase rule,
        DateTime start, DateTime end, double amount, Random random, List<FlowRecord> records)
    {
        if (records.Count >= MaximumFixedRuleRecordCount) throw new InvalidOperationException($"{request.Bank.Name}生成条数超过上限");
        var usage = records.Where(row => row.AccountTime.HasValue).GroupBy(row => row.AccountTime!.Value.Date)
            .ToDictionary(group => group.Key, group => group.Count());
        var used = records.Where(row => row.AccountTime.HasValue).Select(row => row.AccountTime!.Value).ToHashSet();
        if (!TryBalancedCashflowTime(request, new CashflowWindow(rule, start, end, true), start, end, random, used, usage, out var time)) return null;
        var row = CreateRecordFromRule(request, rule, request.Bank.ReferenceColumns, time, amount);
        BindRecordToBudgetMonth(row, start, end); return row;
    }

    private static Dictionary<DateTime, double> CreateAccruedMonthBudgets(DateTime start, DateTime end, double amount, Random random)
    {
        var months = EnumerateMonths(start, end).ToArray();
        var weights = months.Select(month => ((month.End.Date - month.Start.Date).Days + 1d)
            / DateTime.DaysInMonth(month.Start.Year, month.Start.Month) * (.85d + random.NextDouble() * .3d)).ToArray();
        var total = weights.Sum(); var cumulative = 0d;
        var result = new Dictionary<DateTime, double>();
        for (var index = 0; index < months.Length; index++)
        {
            cumulative += weights[index];
            result[BudgetMonth(months[index].Start)] = index == months.Length - 1 ? RoundMoney(amount)
                : RoundMoney(amount * cumulative / total);
        }
        return result;
    }

    private static void BindRecordToBudgetMonth(FlowRecord row, DateTime start, DateTime end)
    {
        if (row.AccountTime is not { } time || row.Id != 0 || row.IsDocumentImported || !IsGeneratedReferenceRecord(row)) return;
        var month = BudgetMonth(time);
        row.ExtraFields[BudgetWindowStart] = MaxDateTime(start, month).ToString("O", CultureInfo.InvariantCulture);
        row.ExtraFields[BudgetWindowEnd] = MinDateTime(end, month.AddMonths(1).AddTicks(-1)).ToString("O", CultureInfo.InvariantCulture);
    }

    private static bool TryBalancedCashflowTime(FlowAutoGenerationRequest request, CashflowWindow window, DateTime start,
        DateTime end, Random random, HashSet<DateTime> used, IReadOnlyDictionary<DateTime, int>? dayUsage,
        out DateTime time, bool earliestDay = false)
    {
        time = default; start = MaxDateTime(start, window.Start); end = MinDateTime(end, window.End);
        if (!window.Mutable || window.Rule is not { } rule || start > end) return false;
        var from = Math.Clamp(rule.StartDay ?? 9, 0, 23); var to = Math.Clamp(rule.EndDay ?? 17, 0, 23);
        if (to < from) (from, to) = (to, from);
        var days = EnumerateDates(start.Date, end.Date).Where(day => rule is not GenerateReferenceRule
            || (ConstructionBankGenerationPolicy.AppliesTo(request.Bank)
                ? ConstructionBankGenerationPolicy.IsReferenceDateAllowed(day, rule)
                : ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day, rule)));
        days = earliestDay ? days.OrderBy(day => day) : days.OrderBy(day => dayUsage?.GetValueOrDefault(day.Date) ?? 0).ThenBy(_ => random.Next());
        foreach (var day in days)
        {
            var lower = MaxDateTime(start, day.AddHours(from));
            var upper = MinDateTime(end, day.AddHours(to).AddMinutes(59).AddSeconds(59));
            if (lower > upper) continue;
            var first = (long)Math.Ceiling((double)lower.Ticks / TimeSpan.TicksPerSecond);
            var last = upper.Ticks / TimeSpan.TicksPerSecond;
            for (var attempt = 0; attempt < 64 && first <= last; attempt++)
            {
                var candidate = new DateTime(random.NextInt64(first, last + 1) * TimeSpan.TicksPerSecond);
                if (used.Contains(candidate)) continue;
                time = candidate; return true;
            }
        }
        return false;
    }
}
