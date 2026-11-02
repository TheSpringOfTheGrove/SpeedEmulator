using System.Globalization;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private const string ReferencePriorityField = "__GeneratedReferencePriority";
    private const string ReferencePrioritySeedField = "__GeneratedReferencePrioritySeed";

    public static bool UsesReferencePriority(FlowGenerationConfig config, IEnumerable<FlowRecord> rows) =>
        config.SelectIndex == 0 && rows.Any(row => row.ExtraFields.GetValueOrDefault(ReferencePriorityField) == "1");

    private static FlowAutoGenerationResult CompleteReferencePriorityFunding(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
    {
        if (!UsesReferencePriority(request.Config, result.Records)) return result;
        var requested = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
        for (var pass = 0; pass < 8; pass++)
        {
            var balance = requested; var minimum = balance;
            foreach (var row in result.Records.Where(row => row.IsDocumentImported || !IsSystemInterestRecord(row)))
            {
                balance = RoundMoney(balance + (row.TradeMoney ?? 0)); minimum = Math.Min(minimum, balance);
            }
            var opening = minimum < -.009d ? RoundMoney(requested - minimum + 1d) : requested;
            if (Math.Abs(result.OpeningBalance - opening) <= .009d)
            {
                var seed = result.Records.Select(row => row.ExtraFields.GetValueOrDefault(ReferencePrioritySeedField)).FirstOrDefault(value => value is not null);
                TraceReferenceNaturalGeneration(request, "reference-priority-final", result.Records,
                    int.TryParse(seed, CultureInfo.InvariantCulture, out var parsedSeed) ? parsedSeed : 0, result.OpeningBalance);
                return StampGeneratedAmountPatterns(request, result);
            }
            // Tail/adjacency cleanup may have changed the ordinary net by a few
            // cents. Keep only the final ledger's actual funding requirement;
            // do not leave a stale surplus from the pre-normalization sample.
            result = EnsureAutomaticOpeningBalance(request, result, opening, recalculateInterest: true);
            result = CompleteGeneratedAdjacentAmounts(request, result);
        }
        throw new InvalidOperationException("参照流水资金与金额校验未能收敛");
    }

    private static bool PriorityDateAllowed(FlowAutoGenerationRequest request, DateTime day, FlowRuleBase rule) =>
        ConstructionBankGenerationPolicy.AppliesTo(request.Bank)
            ? ConstructionBankGenerationPolicy.IsReferenceDateAllowed(day, rule)
            : ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day, rule);

    private static bool TryGenerateReferencePriorityResult(FlowAutoGenerationRequest request, DateTime start, DateTime end,
        double opening, Random random, int seed, out FlowAutoGenerationResult? result)
    {
        result = null;
        if (request.Config.SelectIndex != 0 || !request.References.Any(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0)) return false;
        if (!double.IsFinite(request.Config.AllInMoney) || request.Config.AllInMoney < 0 || !double.IsFinite(opening)
            || !double.IsFinite(request.Config.LastMoney)) throw new InvalidOperationException("生成金额配置无效");
        var draft = new List<FlowRecord>(); var state = new NativeScheduleState(); var usage = new Dictionary<DateTime, int>();
        foreach (var rule in request.ConstItems.Where(rule => rule.IsCheck && !string.IsNullOrWhiteSpace(rule.FixDay)))
        {
            var calendarRule = rule.Clone(); calendarRule.TradeHoliday = true; calendarRule.TradeWeekend = true;
            foreach (var day in ResolveNativeFixedOccurrences(calendarRule, start, end, random, state, usage))
            {
                if (!TrySampleReferenceNaturalRangeAmount(rule, double.MaxValue, random, out var amount)) continue;
                AddReferenceNaturalRecord(request, rule, day, amount, random, draft, state);
                usage[day.Date] = usage.GetValueOrDefault(day.Date) + 1;
            }
        }
        // Retain the monthly date-attempt convention. Forbidden or out-of-period
        // days are skipped, rather than retried until the configured count is met.
        foreach (var rule in request.References.Where(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0))
        foreach (var month in EnumerateMonths(start, end))
        for (var attempt = 0; attempt < GetRequiredMonthlyCount(rule); attempt++)
        {
            var day = BudgetMonth(month.Start).AddDays(random.Next(DateTime.DaysInMonth(month.Start.Year, month.Start.Month)));
            if (day < month.Start.Date || day > month.End.Date || !PriorityDateAllowed(request, day, rule)) continue;
            if (TrySampleReferenceNaturalRangeAmount(rule, double.MaxValue, random, out var amount))
                AddReferenceNaturalRecord(request, rule, day, amount, random, draft, state);
        }
        var expenses = SumExpense(draft);
        // Preserve the established sampling path for already funded, healthy
        // configurations. This branch handles actual reference/budget conflicts.
        var incomeMinimum = draft.Where(row => row.TradeMoney > 0).Sum(row => GetRecordAmountBounds(row).Min);
        if (opening > .009d && opening >= expenses - .009d
            && (ReferenceNaturalGenerationPolicy.AppliesTo(request.Bank, request.Config) || incomeMinimum <= request.Config.AllInMoney + .009d)) return false;
        var variableMonthly = draft.Any(row => IsGeneratedReferenceRecord(row)
            && GetRecordAmountBounds(row).Max > GetRecordAmountBounds(row).Min + .000001);
        if (!variableMonthly || expenses <= opening + request.Config.AllInMoney - Math.Max(0, request.Config.LastMoney) + .009d
            && incomeMinimum <= request.Config.AllInMoney + .009d) return false;

        var variableIncome = draft.Where(row => row.TradeMoney > 0 && IsGeneratedReferenceRecord(row)
            && GetRecordAmountBounds(row).Max > GetRecordAmountBounds(row).Min + .000001).ToArray();
        var rows = draft.Except(variableIncome).ToList();
        var target = Math.Max(SumIncome(rows), RoundMoney(request.Config.AllInMoney));
        var variableTarget = Math.Max(0, RoundMoney(target - SumIncome(rows)));
        var incomeRules = request.References.Where(rule => rule.IsCheck && IsIncomeRuleSafe(rule)).ToArray();
        var optionalIncome = incomeRules.Where(rule => GetRequiredMonthlyCount(rule) == 0).ToArray();
        var minimumIncome = incomeRules.Select(rule => GetRuleAmountBounds(rule)).Select(bounds =>
            FindValidAmountAtOrAbove(bounds.Min, bounds.Max, bounds)).Where(amount => amount.HasValue)
            .Select(amount => amount!.Value).DefaultIfEmpty(double.MaxValue).Min();
        var limits = CreatePriorityIncomeLimits(start, end, variableTarget, draft, random);
        // Small annual targets may afford only one or two normal receipts.
        // Do not reserve impossible minima for all future months and pin six
        // receipts to 5,000. Larger budgets retain income coverage across months.
        var sparseIncome = minimumIncome > variableTarget / Math.Max(1, limits.Count) + .009d;
        var selectedIncome = new List<FlowRecord>(); var spent = 0d;
        foreach (var month in EnumerateMonths(start, end))
        {
            var allowance = Math.Max(0, RoundMoney((sparseIncome ? variableTarget : limits[BudgetMonth(month.Start)]) - spent));
            foreach (var row in variableIncome.Where(row => BudgetMonth(row.AccountTime!.Value) == BudgetMonth(month.Start)).OrderBy(row => row.AccountTime))
            {
                if (allowance <= .009d || ResolveRecordSourceRule(request, row) is not { } rule) break;
                var amount = row.TradeMoney!.Value;
                if (amount > allowance + .009d && !TrySampleReferenceNaturalRangeAmount(rule, allowance, random, out amount)) continue;
                var previous = row.TradeMoney!.Value; ApplySignedAmount(row, 1, amount);
                RefreshCashflowDerivedFields(request, row, row.AccountTime!.Value, previous);
                rows.Add(row); selectedIncome.Add(row); spent = RoundMoney(spent + amount); allowance = RoundMoney(allowance - amount);
            }
            FillPriorityOptionalMonth(request, month.Start, month.End, optionalIncome, allowance, true, random, rows, selectedIncome);
            spent = RoundMoney(selectedIncome.Sum(row => row.TradeMoney!.Value));
            var remaining = Math.Max(0, RoundMoney(variableTarget - spent));
            if (sparseIncome && remaining < minimumIncome - .000001)
            {
                TopUpPriorityIncome(request, selectedIncome, remaining);
                spent = RoundMoney(selectedIncome.Sum(row => row.TradeMoney!.Value));
            }
        }
        TopUpPriorityIncome(request, selectedIncome, RoundMoney(target - SumIncome(rows)));
        // If the target is below every legal minimum, use one lawful receipt.
        // A single indivisible tail may exceed the target; no invented small row.
        if (SumIncome(rows) <= .009d && target > .009d)
        {
            var options = incomeRules.Select(rule => (Rule: rule, Bounds: GetRuleAmountBounds(rule)))
                .Select(item => (item.Rule, Minimum: FindValidAmountAtOrAbove(item.Bounds.Min, item.Bounds.Max, item.Bounds)))
                .Where(item => item.Minimum.HasValue).OrderBy(item => item.Minimum).ToArray();
            foreach (var item in options)
            {
                var existing = variableIncome.FirstOrDefault(row => ReferenceEquals(ResolveRecordSourceRule(request, row), item.Rule));
                if (existing is not null)
                {
                    var previous = existing.TradeMoney!.Value; ApplySignedAmount(existing, 1, item.Minimum!.Value);
                    RefreshCashflowDerivedFields(request, existing, existing.AccountTime!.Value, previous); rows.Add(existing); break;
                }
                if (GetRequiredMonthlyCount(item.Rule) > 0) continue;
                var row = CreatePriorityOptionalRecord(request, item.Rule, start, end, item.Minimum!.Value, random, rows);
                if (row is null) continue; rows.Add(row); break;
            }
        }

        var expenseTarget = Math.Max(0, RoundMoney(SumIncome(rows) - request.Config.LastMoney));
        var expenseLimits = CreateAccruedMonthBudgets(start, end, expenseTarget, random);
        var optionalExpense = request.References.Where(rule => rule.IsCheck && !IsIncomeRuleSafe(rule) && GetRequiredMonthlyCount(rule) == 0).ToArray();
        foreach (var month in EnumerateMonths(start, end))
        {
            var existing = rows.Where(row => row.TradeMoney < 0 && row.AccountTime <= month.End).Sum(row => -row.TradeMoney!.Value);
            var allowance = Math.Max(0, RoundMoney(expenseLimits[BudgetMonth(month.Start)] - existing));
            FillPriorityOptionalMonth(request, month.Start, month.End, optionalExpense, allowance, false, random, rows, null);
        }
        foreach (var row in rows)
        {
            row.ExtraFields[ReferencePriorityField] = "1";
            BindRecordToBudgetMonth(row, start, end);
        }
        if (rows.Count > 0) rows[0].ExtraFields[ReferencePrioritySeedField] = seed.ToString(CultureInfo.InvariantCulture);
        TraceReferenceNaturalGeneration(request, "reference-priority-full-sample", rows, seed, opening);
        var funded = FundReferencePriorityCashflow(request, rows, opening, start, end, random);
        result = EnsureAutomaticOpeningBalance(request, CreateAutomaticBalanceResult(rows, funded), recalculateInterest: true);
        TraceReferenceNaturalGeneration(request, "reference-priority-complete", result.Records, seed, result.OpeningBalance);
        return true;
    }

    private static Dictionary<DateTime, double> CreatePriorityIncomeLimits(DateTime start, DateTime end, double target,
        List<FlowRecord> draft, Random random)
    {
        var months = EnumerateMonths(start, end).ToArray();
        var costs = months.Select(month => draft.Where(row => row.TradeMoney < 0 && row.AccountTime >= month.Start && row.AccountTime <= month.End)
            .Sum(row => -row.TradeMoney!.Value)).ToArray();
        if (costs.Sum() <= .009d) return CreateAccruedMonthBudgets(start, end, target, random);
        // Income shares follow the obligations they need to fund. Keep a small
        // calendar floor so a quiet month is not starved, without arbitrarily
        // capping a busy month's receipts and borrowing avoidable opening cash.
        var average = costs.Average();
        var weights = months.Select((month, index) => Math.Max(costs[index], average * .1d
            * ((month.End.Date - month.Start.Date).Days + 1d) / DateTime.DaysInMonth(month.Start.Year, month.Start.Month))).ToArray();
        var total = weights.Sum(); var accrued = 0d; var result = new Dictionary<DateTime, double>();
        for (var index = 0; index < months.Length; index++)
        {
            accrued += weights[index];
            result[BudgetMonth(months[index].Start)] = index == months.Length - 1 ? target : RoundMoney(target * accrued / total);
        }
        return result;
    }

    private static void TopUpPriorityIncome(FlowAutoGenerationRequest request, List<FlowRecord> rows, double remaining)
    {
        foreach (var row in rows.OrderByDescending(row => row.AccountTime))
        {
            if (remaining <= .009d) break;
            var before = row.TradeMoney!.Value; var bounds = GetRecordAmountBounds(row);
            var after = FindValidAmountAtOrBelow(Math.Min(bounds.Max, before + remaining), before, bounds);
            if (!after.HasValue || after <= before + .009d) continue;
            ApplySignedAmount(row, 1, after.Value); RefreshCashflowDerivedFields(request, row, row.AccountTime!.Value, before);
            remaining = RoundMoney(remaining - after.Value + before);
        }
    }

    private static void FillPriorityOptionalMonth(FlowAutoGenerationRequest request, DateTime start, DateTime end,
        IReadOnlyList<GenerateReferenceRule> pool, double allowance, bool income, Random random,
        List<FlowRecord> rows, List<FlowRecord>? selectedIncome)
    {
        var options = pool.Where(rule => EnumerateDates(start.Date, end.Date).Any(day => PriorityDateAllowed(request, day, rule))).ToArray();
        for (var attempt = 0; allowance > .009d && attempt < 64; attempt++)
        {
            var affordable = options.Where(rule => GetRuleAmountBounds(rule).Min <= allowance + .000001).ToArray();
            if (affordable.Length == 0) return;
            var rule = affordable[random.Next(affordable.Length)];
            if (!TrySampleReferenceNaturalRangeAmount(rule, allowance, random, out var amount)) return;
            var row = CreatePriorityOptionalRecord(request, rule, start, end, income ? amount : -amount, random, rows);
            if (row is null) return;
            rows.Add(row); selectedIncome?.Add(row); allowance = RoundMoney(allowance - amount);
        }
    }

    private static FlowRecord? CreatePriorityOptionalRecord(FlowAutoGenerationRequest request, GenerateReferenceRule rule,
        DateTime start, DateTime end, double amount, Random random, List<FlowRecord> rows)
    {
        if (rows.Count >= MaximumFixedRuleRecordCount) throw new InvalidOperationException("生成条数超过上限");
        var used = rows.Where(row => row.AccountTime.HasValue).Select(row => row.AccountTime!.Value).ToHashSet();
        var usage = rows.Where(row => row.AccountTime.HasValue).GroupBy(row => row.AccountTime!.Value.Date).ToDictionary(group => group.Key, group => group.Count());
        if (!TryBalancedCashflowTime(request, new CashflowWindow(rule, start, end, true), start, end, random, used, usage, out var time)) return null;
        return CreateRecordFromRule(request, rule, request.Bank.ReferenceColumns, time, amount);
    }

    private static double FundReferencePriorityCashflow(FlowAutoGenerationRequest request, List<FlowRecord> records,
        double opening, DateTime start, DateTime end, Random random)
    {
        // Generated settlements are recomputed after funding. Imported credits
        // retain their original value and can fund the cash ledger as usual.
        var ordinary = records.Where(row => row.IsDocumentImported || !IsSystemInterestRecord(row)).ToList();
        var windows = ordinary.ToDictionary(row => row, row => ResolveCashflowWindow(request, row, start, end));
        foreach (var row in ordinary.Where(row => row.TradeMoney > 0))
            windows[row] = PriorityOptionalFundingWindow(request, row, start, end);
        var jobs = ordinary.Where(row => row.TradeMoney < 0 && windows[row].Mutable).ToArray();
        var committed = ordinary.Except(jobs).ToList();
        var net = RoundMoney(opening + ordinary.Sum(row => row.TradeMoney ?? 0));
        var raised = net < -.009d ? RoundMoney(opening - net + 1d) : opening;
        if (raised < -.009d) raised = 1d;
        var used = ordinary.Where(row => row.AccountTime.HasValue).Select(row => row.AccountTime!.Value).ToHashSet();
        for (var pass = 0; pass <= committed.Count; pass++)
        {
            ApplyBalances(committed, raised);
            var negative = committed.FirstOrDefault(row => row.Balance < -.009d);
            if (negative is null) break;
            if (TryAdvanceCashflowIncome(request, committed, windows, negative.AccountTime!.Value, random, used, out var moved, out _))
            {
                BindRecordToBudgetMonth(moved!, start, end); continue;
            }
            raised = RoundMoney(raised - negative.Balance!.Value + 1d);
        }
        var ledger = new CashflowLedger(raised);
        foreach (var row in committed) if (row.AccountTime is { } time) ledger.Add(time, row.TradeMoney ?? 0);
        var usage = committed.Where(row => row.AccountTime.HasValue).GroupBy(row => row.AccountTime!.Value.Date).ToDictionary(group => group.Key, group => group.Count());
        foreach (var row in jobs.OrderBy(row => windows[row].Rule is GenerateReferenceRule rule && GetRequiredMonthlyCount(rule) == 0 ? 1 : 0)
                     .ThenBy(row => windows[row].End).ThenBy(row => row.AccountTime))
        {
            var preferred = row.AccountTime!.Value; var amount = Math.Abs(row.TradeMoney!.Value); var window = windows[row];
            var placed = TryPlacePriorityDebit(request, committed, windows, ledger, window, preferred, amount, start, end, random, used, usage, out var time);
            if (!placed)
            {
                var flexible = PriorityOptionalFundingWindow(request, row, start, end);
                if (flexible.Start != window.Start || flexible.End != window.End)
                {
                    window = flexible;
                    placed = TryPlacePriorityDebit(request, committed, windows, ledger, window, preferred, amount, start, end, random, used, usage, out time);
                }
            }
            if (!placed)
            {
                // Keeping this lawful occurrence is more important than keeping
                // an infeasible opening. Fund only the actual remaining gap.
                if (!TryCashflowTime(request, window, window.Start, window.End, random, used, true, out var deadline))
                    throw new InvalidOperationException("参照流水没有可用交易时段");
                var extra = RoundMoney(Math.Max(0, amount - ledger.Headroom(deadline)) + 1d);
                raised = RoundMoney(raised + extra); ledger.Fund(extra);
                placed = TryPlaceCashflowDebit(request, ledger, window, preferred, amount, random, used, usage, out time);
                if (!placed) throw new InvalidOperationException("参照流水补足资金后仍无法安排合法日期");
            }
            MoveCashflowRow(request, row, time, used); BindRecordToBudgetMonth(row, start, end);
            ledger.Add(time, -amount); committed.Add(row);
            usage[time.Date] = usage.GetValueOrDefault(time.Date) + 1;
        }
        ApplyBalances(records, raised); return raised;
    }

    private static CashflowWindow PriorityOptionalFundingWindow(FlowAutoGenerationRequest request, FlowRecord row, DateTime start, DateTime end)
    {
        if (ResolveRecordSourceRule(request, row) is not GenerateReferenceRule rule || GetRequiredMonthlyCount(rule) > 0)
            return ResolveCashflowWindow(request, row, start, end);
        // An optional row's planned month is a distribution hint, not a user
        // obligation. Try its original legal window before borrowing cash.
        var copy = row.Clone(); copy.ExtraFields.Remove(BudgetWindowStart); copy.ExtraFields.Remove(BudgetWindowEnd);
        return ResolveCashflowWindow(request, copy, start, end);
    }

    private static bool TryPlacePriorityDebit(FlowAutoGenerationRequest request, List<FlowRecord> committed,
        Dictionary<FlowRecord, CashflowWindow> windows, CashflowLedger ledger, CashflowWindow window,
        DateTime preferred, double amount, DateTime start, DateTime end, Random random,
        HashSet<DateTime> used, Dictionary<DateTime, int> usage, out DateTime time)
    {
        time = preferred;
        if (ledger.Headroom(preferred) >= amount - .009d
            || TryPlaceCashflowDebit(request, ledger, window, preferred, amount, random, used, usage, out time)) return true;
        for (var attempt = 0; attempt < committed.Count; attempt++)
        {
            if (!TryCashflowTime(request, window, window.Start, window.End, random, used, true, out var deadline)
                || !TryAdvanceCashflowIncome(request, committed, windows, deadline.AddSeconds(1), random, used, out var moved, out var original)) break;
            ledger.Add(original, -moved!.TradeMoney!.Value); ledger.Add(moved.AccountTime!.Value, moved.TradeMoney.Value);
            BindRecordToBudgetMonth(moved, start, end);
            usage[original.Date] = Math.Max(0, usage.GetValueOrDefault(original.Date) - 1);
            usage[moved.AccountTime.Value.Date] = usage.GetValueOrDefault(moved.AccountTime.Value.Date) + 1;
            if (ledger.Headroom(preferred) >= amount - .009d) { time = preferred; return true; }
            if (TryPlaceCashflowDebit(request, ledger, window, preferred, amount, random, used, usage, out time)) return true;
        }
        return false;
    }
}
