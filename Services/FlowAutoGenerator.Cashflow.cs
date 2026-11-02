using System.Globalization;
using System.Reflection;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private sealed record CashflowWindow(FlowRuleBase? Rule, DateTime Start, DateTime End, bool Mutable);
    private static readonly string[] CashflowDerivedFields =
    [nameof(FlowRecord.SerialNum), nameof(FlowRecord.VoucherNum), nameof(FlowRecord.LogNum), nameof(FlowRecord.MerchantName),
        nameof(FlowRecord.ReceiptNum), nameof(FlowRecord.SequenceNum), nameof(FlowRecord.Remark), nameof(FlowRecord.Usage), nameof(FlowRecord.Year)];

    /// <summary>Keep the requested opening whenever feasible. Only hard obligations can require extra funding.</summary>
    private static double PlanCashflowBeforeOpeningFunding(FlowAutoGenerationRequest request, List<FlowRecord> records,
        double opening, DateTime start, DateTime end, Random random)
    {
        // Settlement is recalculated at the requested opening before this pass.
        // Deferring debits/advancing credits only improves the daily cash ledger,
        // so those already-earned settlements are a conservative funding source.
        var ordinary = records.ToList();
        var windows = ordinary.ToDictionary(row => row, row => ResolveCashflowWindow(request, row, start, end));
        // Explicit month budgets are hard configuration constraints. Preserve
        // their totals and seek feasible within-month times before any funding.
        bool Soft(FlowRecord row) => request.Config.SelectIndex == 0 && row.TradeMoney < 0 && windows[row].Mutable
            && windows[row].Rule is GenerateReferenceRule rule
            && GetRuleAmountBounds(rule).Max > GetRuleAmountBounds(rule).Min + .000001;
        var soft = ordinary.Where(Soft).ToHashSet();
        var hard = ordinary.Where(row => !soft.Contains(row)).ToList();
        var used = ordinary.Where(row => row.AccountTime.HasValue).Select(row => row.AccountTime!.Value).ToHashSet();
        // These receipts already have dates from their generation plan. A small
        // ending-reserve shortage does not make them unassigned: retain that
        // plan and advance only receipts needed by an actual unpaid obligation.
        var deferred = new HashSet<FlowRecord>();
        var raised = opening;

        // Hard-date payments and fixed amounts retain their obligations. First
        // defer flexible dates/advance eligible existing income, then fund only
        // the remaining unavoidable shortfall. Ordinary variable debits are absent.
        for (var pass = 0; pass < hard.Count * 2 + 8; pass++)
        {
            ApplyBalances(hard, raised);
            var negative = hard.FirstOrDefault(row => row.Balance < -.009d);
            if (negative is null) break;
            var window = windows[negative];
            if (window.Mutable && window.Rule is GenerateReferenceRule && deferred.Add(negative)
                && TryCashflowTime(request, window, negative.AccountTime!.Value.AddSeconds(1), window.End,
                    random, used, latest: true, out var later))
            {
                MoveCashflowRow(request, negative, later, used); continue;
            }
            if (TryAdvanceCashflowIncome(request, hard, windows, negative.AccountTime!.Value, random, used, out _, out _)) continue;
            if (TryUseRemainingCashflowIncomeBudget(request, records, hard, windows, negative.AccountTime.Value,
                    -negative.Balance!.Value)) continue;
            if (window.Mutable && window.Rule is GenerateConstRule rule)
            {
                var bounds = GetRuleAmountBounds(rule);
                var available = RoundMoney((negative.Balance ?? 0) + Math.Abs(negative.TradeMoney ?? 0));
                if (bounds.Max > bounds.Min + .000001 && available >= bounds.Min - .000001
                    && TrySampleReferenceNaturalRangeAmount(rule, available, random, out var amount))
                {
                    var oldAmount = negative.TradeMoney!.Value;
                    ApplySignedAmount(negative, -1, amount);
                    RefreshCashflowDerivedFields(request, negative, negative.AccountTime.Value, oldAmount);
                    continue;
                }
            }
            raised = RoundMoney(raised - negative.Balance!.Value);
        }
        ApplyBalances(hard, raised);
        var hardMinimum = GetMinimumBalance(hard, raised);
        if (hardMinimum < -.009d) raised = RoundMoney(raised - hardMinimum);

        var ledger = new CashflowLedger(raised);
        foreach (var row in hard) if (row.AccountTime is { } time) ledger.Add(time, row.TradeMoney ?? 0);
        var finalHard = raised + hard.Sum(row => row.TradeMoney ?? 0);
        var totalFundsInsufficient = raised + records.Sum(row => row.TradeMoney ?? 0) < -.009d;
        // RCB fixed withdrawals may already make the requested ending impossible.
        // A partial reserve would then lock every remaining yuan and erase all
        // ordinary spending, although that cash is still legally available.
        var canReserveEnding = !PersonalRuralCommercialGenerationPolicy.AppliesTo(request.Bank, request.Config)
            || finalHard >= request.Config.LastMoney - .009d;
        var plannedEndingShortage = request.Config.SelectIndex == 0 && soft.Any(row => row.ExtraFields.ContainsKey(BudgetWindowStart))
            && raised + records.Sum(row => row.TradeMoney ?? 0) < request.Config.LastMoney - .009d;
        var reserve = (totalFundsInsufficient || plannedEndingShortage) && canReserveEnding
            ? Math.Min(Math.Max(0, request.Config.LastMoney), Math.Max(0, finalHard)) : 0;
        if (reserve > 0) ledger.Add(end.AddSeconds(1), -reserve);

        var softBudget = Math.Max(0, RoundMoney(finalHard - reserve));
        var trim = soft.Sum(row => Math.Abs(row.TradeMoney ?? 0)) > softBudget + .009d;
        var monthBudgets = trim ? CreateAccruedMonthBudgets(start, end, softBudget, random) : null;
        if (trim)
            foreach (var row in soft)
            {
                // Ordinary reductions retain each original month. Carry unused
                // monthly budget forward instead of borrowing later months' cash.
                BindRecordToBudgetMonth(row, start, end);
                windows[row] = ResolveCashflowWindow(request, row, start, end);
            }
        if (trim && CreateSparseMonthBudgets(request, records, soft, softBudget, start, end, random, used) is { } sparseBudgets)
            monthBudgets = sparseBudgets;
        var dayUsage = hard.Where(row => row.AccountTime.HasValue).GroupBy(row => row.AccountTime!.Value.Date)
            .ToDictionary(group => group.Key, group => group.Count());
        var coverage = ReserveMonthlyOrdinaryCoverage(request, soft, windows, ledger, softBudget, start, end, random, used, dayUsage, monthBudgets, hard);
        var coverageCosts = coverage.GroupBy(row => BudgetMonth(row.AccountTime!.Value))
            .ToDictionary(group => group.Key, group => group.Sum(row => Math.Abs(row.TradeMoney!.Value)));
        var spent = 0d;
        var ordered = soft.Where(row => !coverage.Contains(row)).GroupBy(row => BudgetMonth(row.AccountTime!.Value)).OrderBy(group => group.Key)
            .SelectMany(group => group.OrderBy(row => trim && (GetRecordAmountBounds(row).Min + GetRecordAmountBounds(row).Max) / 2d
                    <= softBudget / Math.Max(1, monthBudgets!.Count) * .05d ? 0 : 1).ThenBy(_ => random.Next())).ToArray();
        foreach (var row in ordered)
        {
            var amount = Math.Abs(row.TradeMoney ?? 0); var window = windows[row];
            var preferred = row.AccountTime!.Value;
            var reservedThroughMonth = coverageCosts.Where(pair => pair.Key <= BudgetMonth(preferred)).Sum(pair => pair.Value);
            var allowance = trim ? Math.Max(0, RoundMoney(monthBudgets![BudgetMonth(preferred)] - reservedThroughMonth - spent)) : double.MaxValue;
            if (amount <= allowance + .009d && ledger.Headroom(preferred) >= amount - .009d)
            {
                ledger.Add(preferred, -amount); spent = RoundMoney(spent + amount);
                dayUsage[preferred.Date] = dayUsage.GetValueOrDefault(preferred.Date) + 1; continue;
            }
            var time = default(DateTime);
            var placed = amount <= allowance + .009d
                && TryPlaceCashflowDebit(request, ledger, window, preferred, amount, random, used, dayUsage, out time);
            if (!placed && amount <= allowance + .009d
                && TryFundCashflowWindow(request, hard, windows, ledger, window, amount, random, used, dayUsage))
                placed = TryPlaceCashflowDebit(request, ledger, window, window.Start, amount, random, used, dayUsage, out time);
            if (!placed)
            {
                var available = TryCashflowTime(request, window, window.Start, window.End, random, used, true, out var lastAllowed)
                    ? ledger.Headroom(lastAllowed) : 0;
                available = Math.Min(available, allowance);
                var bounds = GetRecordAmountBounds(row);
                // Preserve full-range draws while reducing occurrences. Only a
                // final-period tail (or a genuine timing-only shortage) is redrawn.
                if ((!trim || BudgetMonth(preferred) == BudgetMonth(end))
                    && available >= bounds.Min - .009d && window.Rule is { } rule
                    && TrySampleReferenceNaturalRangeAmount(rule, available, random, out var smaller)
                    && TryPlaceCashflowDebit(request, ledger, window, preferred, smaller, random, used, dayUsage, out time))
                {
                    var oldAmount = row.TradeMoney!.Value;
                    ApplySignedAmount(row, -1, smaller);
                    RefreshCashflowDerivedFields(request, row, preferred, oldAmount);
                    amount = smaller; placed = true;
                }
            }
            if (!placed) { records.Remove(row); used.Remove(preferred); continue; }
            MoveCashflowRow(request, row, time, used); ledger.Add(time, -amount);
            spent = RoundMoney(spent + amount); dayUsage[time.Date] = dayUsage.GetValueOrDefault(time.Date) + 1;
        }
        ApplyBalances(records, raised);
        return raised;
    }

    private static CashflowWindow ResolveCashflowWindow(FlowAutoGenerationRequest request, FlowRecord row, DateTime start, DateTime end)
    {
        var rule = ResolveRecordSourceRule(request, row);
        var mutable = !row.IsDocumentImported && row.Id == 0 && rule is not null && row.AccountTime.HasValue;
        if (!mutable) return new CashflowWindow(rule, row.AccountTime ?? start, row.AccountTime ?? end, false);
        var time = row.AccountTime!.Value;
        if (rule is GenerateConstRule) { start = time.Date; end = NormalizeEndDate(time); }
        else if (rule is GenerateReferenceRule reference && (GetRequiredMonthlyCount(reference) > 0 || request.Config.SelectIndex != 0))
        {
            start = MaxDateTime(start, new DateTime(time.Year, time.Month, 1));
            end = MinDateTime(end, NormalizeEndDate(new DateTime(time.Year, time.Month, DateTime.DaysInMonth(time.Year, time.Month))));
        }
        if (row.ExtraFields.TryGetValue(CcbWindowStart, out var from) && DateTime.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedStart))
            start = MaxDateTime(start, parsedStart);
        if (row.ExtraFields.TryGetValue(CcbWindowEnd, out var to) && DateTime.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedEnd))
            end = MinDateTime(end, parsedEnd);
        if (row.ExtraFields.TryGetValue(BudgetWindowStart, out var budgetFrom) && DateTime.TryParse(budgetFrom, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var budgetStart))
            start = MaxDateTime(start, budgetStart);
        if (row.ExtraFields.TryGetValue(BudgetWindowEnd, out var budgetTo) && DateTime.TryParse(budgetTo, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var budgetEnd))
            end = MinDateTime(end, budgetEnd);
        return new CashflowWindow(rule, start, end, true);
    }

    private static bool TryAdvanceCashflowIncome(FlowAutoGenerationRequest request, List<FlowRecord> rows,
        Dictionary<FlowRecord, CashflowWindow> windows, DateTime before, Random random, HashSet<DateTime> used,
        out FlowRecord? moved, out DateTime original)
    {
        var usage = rows.Where(row => row.AccountTime.HasValue).GroupBy(row => row.AccountTime!.Value.Date)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var row in rows.Where(row => row.TradeMoney > 0 && row.AccountTime >= before && windows[row].Mutable)
                     .OrderBy(row => row.AccountTime).ThenByDescending(row => row.TradeMoney))
        {
            var window = windows[row];
            var latest = MinDateTime(window.End, before.AddSeconds(-1));
            var time = default(DateTime); var found = false;
            // Search the nearest trading week first. Do not pull a receipt all
            // the way back to the statement's first month to cover a local gap.
            while (latest >= window.Start)
            {
                var nearby = MaxDateTime(window.Start, latest.Date.AddDays(-Math.Min(6, (latest.Date - window.Start.Date).Days)));
                if (TryBalancedCashflowTime(request, window, nearby, latest, random, used, usage, out time)) { found = true; break; }
                if (nearby <= window.Start) break;
                latest = nearby.AddTicks(-1);
            }
            if (!found) continue;
            original = row.AccountTime!.Value; MoveCashflowRow(request, row, time, used); moved = row; return true;
        }
        // A source/month is part of the transaction contract. Never replace a
        // future monthly income with another reference just to fund an early debit.
        moved = null; original = default; return false;
    }

    private static bool TryFundCashflowWindow(FlowAutoGenerationRequest request, List<FlowRecord> income,
        Dictionary<FlowRecord, CashflowWindow> windows, CashflowLedger ledger, CashflowWindow window,
        double amount, Random random, HashSet<DateTime> used, Dictionary<DateTime, int> usage)
    {
        if (!TryCashflowTime(request, window, window.Start, window.End, random, used, true, out var deadline)) return false;
        for (var attempt = 0; ledger.Headroom(deadline) < amount - .009d && attempt < income.Count; attempt++)
        {
            if (!TryAdvanceCashflowIncome(request, income, windows, deadline.AddSeconds(1), random, used, out var moved, out var original)) break;
            ledger.Add(original, -moved!.TradeMoney!.Value); ledger.Add(moved.AccountTime!.Value, moved.TradeMoney.Value);
            usage[original.Date] = Math.Max(0, usage.GetValueOrDefault(original.Date) - 1);
            usage[moved.AccountTime.Value.Date] = usage.GetValueOrDefault(moved.AccountTime.Value.Date) + 1;
        }
        return ledger.Headroom(deadline) >= amount - .009d;
    }

    private static bool TryUseRemainingCashflowIncomeBudget(FlowAutoGenerationRequest request, List<FlowRecord> allRows,
        List<FlowRecord> hard, Dictionary<FlowRecord, CashflowWindow> windows, DateTime before, double needed)
    {
        if (request.Config.SelectIndex != 0) return false;
        var ordinaryBudget = ReferenceNaturalGenerationPolicy.AppliesTo(request.Bank, request.Config);
        var remaining = RoundMoney(request.Config.AllInMoney - allRows.Where(row => row.TradeMoney > 0 && (!ordinaryBudget || !IsSystemInterestRecord(row)))
            .Sum(row => row.TradeMoney ?? 0));
        if (remaining <= .009d) return false;
        foreach (var row in hard.Where(row => row.TradeMoney > 0 && row.AccountTime < before && windows[row].Mutable
                     && windows[row].Rule is GenerateReferenceRule))
        {
            var bounds = GetRecordAmountBounds(row); var previous = row.TradeMoney!.Value;
            var maximum = Math.Min(bounds.Max, previous + remaining);
            var amount = FindValidAmountAtOrAbove(previous + Math.Min(remaining, needed), maximum, bounds)
                ?? FindValidAmountAtOrBelow(maximum, previous + bounds.Unit, bounds);
            if (!amount.HasValue || amount <= previous + .009d) continue;
            ApplySignedAmount(row, 1, amount.Value);
            RefreshCashflowDerivedFields(request, row, row.AccountTime!.Value, previous);
            return true;
        }
        return false;
    }

    private static bool TryPlaceCashflowDebit(FlowAutoGenerationRequest request, CashflowLedger ledger,
        CashflowWindow window, DateTime preferred, double amount, Random random, HashSet<DateTime> used,
        IReadOnlyDictionary<DateTime, int> dayUsage, out DateTime time)
    {
        time = default;
        if (!TryCashflowTime(request, window, window.Start, window.End, random, used, true, out var lastAllowed)
            || ledger.Headroom(lastAllowed) < amount - .009d) return false;
        var first = Math.Max(window.Start.Ticks / TimeSpan.TicksPerSecond, preferred.Ticks / TimeSpan.TicksPerSecond);
        var last = lastAllowed.Ticks / TimeSpan.TicksPerSecond;
        if (first > last) return false;
        while (first < last)
        {
            var middle = first + (last - first) / 2;
            if (ledger.Headroom(new DateTime(middle * TimeSpan.TicksPerSecond)) >= amount - .009d) last = middle;
            else first = middle + 1;
        }
        return TryBalancedCashflowTime(request, window, new DateTime(first * TimeSpan.TicksPerSecond), window.End, random, used, dayUsage, out time)
            && ledger.Headroom(time) >= amount - .009d;
    }

    private static bool TryCashflowTime(FlowAutoGenerationRequest request, CashflowWindow window, DateTime start,
        DateTime end, Random random, HashSet<DateTime> used, bool latest, out DateTime time)
    {
        time = default; start = MaxDateTime(start, window.Start); end = MinDateTime(end, window.End);
        if (!window.Mutable || window.Rule is not { } rule || start > end) return false;
        var fromHour = Math.Clamp(rule.StartDay ?? 9, 0, 23); var toHour = Math.Clamp(rule.EndDay ?? 17, 0, 23);
        if (toHour < fromHour) (fromHour, toHour) = (toHour, fromHour);
        for (var day = latest ? end.Date : start.Date; day >= start.Date && day <= end.Date; day = day.AddDays(latest ? -1 : 1))
        {
            if (rule is GenerateReferenceRule && !(ConstructionBankGenerationPolicy.AppliesTo(request.Bank)
                    ? ConstructionBankGenerationPolicy.IsReferenceDateAllowed(day, rule)
                    : ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day, rule))) continue;
            var lower = MaxDateTime(start, day.AddHours(fromHour));
            var upper = MinDateTime(end, day.AddHours(toHour).AddMinutes(59).AddSeconds(59));
            if (lower > upper) continue;
            var first = (long)Math.Ceiling((double)lower.Ticks / TimeSpan.TicksPerSecond);
            var last = upper.Ticks / TimeSpan.TicksPerSecond;
            for (var attempt = 0; attempt < 32 && first <= last; attempt++)
            {
                var second = latest ? Math.Max(first, last - attempt) : random.NextInt64(first, last + 1);
                var candidate = new DateTime(second * TimeSpan.TicksPerSecond);
                if (used.Contains(candidate)) continue;
                time = candidate; return true;
            }
        }
        return false;
    }

    private static void MoveCashflowRow(FlowAutoGenerationRequest request, FlowRecord row, DateTime time, HashSet<DateTime> used)
    {
        var old = row.AccountTime!.Value; used.Remove(old); row.AccountTime = time; used.Add(time);
        RefreshCashflowDerivedFields(request, row, old, row.TradeMoney ?? 0);
    }

    private static void RefreshCashflowDerivedFields(FlowAutoGenerationRequest request, FlowRecord row, DateTime oldTime, double oldAmount)
    {
        if (row.IsDocumentImported || ResolveRecordSourceRule(request, row) is not { } rule) return;
        if (AlipayGeneratedNumberRules.AppliesTo(request.Bank)) { AlipayGeneratedNumberRules.Apply(request.Bank, row); return; }
        var columns = rule is GenerateConstRule ? request.Bank.ConstColumns : request.Bank.ReferenceColumns;
        var old = CreateRecordFromRule(request, rule, columns, oldTime, oldAmount);
        var fresh = CreateRecordFromRule(request, rule, columns, row.AccountTime!.Value, row.TradeMoney ?? 0);
        foreach (var name in CashflowDerivedFields)
        {
            var property = typeof(FlowRecord).GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!;
            // User edits/provided identifiers retain ownership; refresh generated values only.
            if (Equals(property.GetValue(row), property.GetValue(old))) property.SetValue(row, property.GetValue(fresh));
        }
    }

    // Ordered event tree. Cached subtree sums/minima support suffix cash capacity
    // and online debit placement in O(log n), without scanning an entire statement.
    private sealed class CashflowLedger(double opening)
    {
        private sealed class Node(long key, double delta)
        {
            public long Key = key; public double Delta = delta, Sum = delta, Minimum = Math.Min(0, delta);
            public ulong Priority = Mix((ulong)key); public Node? Left, Right;
        }
        private Node? root;
        public void Fund(double amount) => opening = RoundMoney(opening + amount);
        public void Add(DateTime time, double delta) => root = Insert(root, time.Ticks, delta);
        public double Headroom(DateTime time) => RoundMoney(opening + Before(root, time.Ticks) + Suffix(root, time.Ticks).Minimum);
        private static ulong Mix(ulong value) { unchecked { value ^= value >> 30; value *= 0xbf58476d1ce4e5b9; value ^= value >> 27; value *= 0x94d049bb133111eb; return value ^ value >> 31; } }
        private static double Sum(Node? node) => node?.Sum ?? 0;
        private static double Min(Node? node) => node?.Minimum ?? 0;
        private static void Update(Node node)
        {
            node.Sum = Sum(node.Left) + node.Delta + Sum(node.Right);
            node.Minimum = Math.Min(Min(node.Left), Sum(node.Left) + node.Delta + Min(node.Right));
        }
        private static Node Insert(Node? node, long key, double delta)
        {
            if (node is null) return new Node(key, delta);
            if (key == node.Key) node.Delta += delta;
            else if (key < node.Key)
            {
                node.Left = Insert(node.Left, key, delta);
                if (node.Left.Priority < node.Priority) { var top = node.Left; node.Left = top.Right; top.Right = node; Update(node); node = top; }
            }
            else
            {
                node.Right = Insert(node.Right, key, delta);
                if (node.Right.Priority < node.Priority) { var top = node.Right; node.Right = top.Left; top.Left = node; Update(node); node = top; }
            }
            Update(node); return node;
        }
        private static double Before(Node? node, long key) => node is null ? 0 : node.Key >= key ? Before(node.Left, key)
            : Sum(node.Left) + node.Delta + Before(node.Right, key);
        private static (double Sum, double Minimum) Suffix(Node? node, long key)
        {
            if (node is null) return (0, 0);
            if (node.Key < key) return Suffix(node.Right, key);
            var left = Suffix(node.Left, key);
            return (left.Sum + node.Delta + Sum(node.Right), Math.Min(left.Minimum, left.Sum + node.Delta + Min(node.Right)));
        }
    }
}
