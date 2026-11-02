using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private static readonly object ReferenceNaturalDiagnosticLock = new();

    private static FlowAutoGenerationResult GenerateReferenceNaturalResult(FlowAutoGenerationRequest request,
        DateTime start, DateTime end, double openingBalance, Random random, int runSeed)
    {
        if (!double.IsFinite(request.Config.AllInMoney) || request.Config.AllInMoney < 0
            || !double.IsFinite(request.Config.LastMoney))
            throw new InvalidOperationException($"{request.Bank.Name}生成金额配置无效");
        if (TryGenerateMonthlyBudgetedNaturalResult(request, start, end, openingBalance, new Random(runSeed), runSeed, out var budgeted))
            return budgeted!;
        var records = new List<FlowRecord>();
        var state = new NativeScheduleState();
        var fixedDayUsage = new Dictionary<DateTime, int>();
        foreach (var rule in request.ConstItems.Where(rule => rule.IsCheck && !string.IsNullOrWhiteSpace(rule.FixDay)))
        foreach (var day in ResolveNativeFixedOccurrences(rule, start, end, random, state, fixedDayUsage))
        {
            if (!TrySampleReferenceNaturalRangeAmount(rule, double.MaxValue, random, out var amount)) continue;
            AddReferenceNaturalRecord(request, rule, day, amount, random, records, state);
            fixedDayUsage[day.Date] = fixedDayUsage.GetValueOrDefault(day.Date) + 1;
        }
        GenerateReferenceNaturalAttempts(request, start, end, random, records, state);
        TraceReferenceNaturalGeneration(request, "after-date-attempts", records, runSeed, openingBalance);

        // Sample source ranges first. The shared cashflow planner later adjusts
        // variable occurrences to available funds while preserving hard projects.
        var incomeTarget = RoundMoney(request.Config.AllInMoney);
        var incomeUnit = request.References.Where(rule => rule.IsCheck && IsIncomeRuleSafe(rule) && GetRequiredMonthlyCount(rule) == 0)
            .Select(rule => GetRuleAmountBounds(rule).Unit).DefaultIfEmpty(.01d).Min();
        // Tail variation follows source precision: a million-yuan budget must
        // not lose ten percent merely to make the income look different.
        var incomeTailBand = Math.Min(incomeTarget * .1d, incomeUnit * 9d);
        var desiredIncome = RoundMoney(incomeTarget - random.NextDouble() * incomeTailBand);
        FillReferenceNaturalOptionalDirection(request, start, end, desiredIncome, true, random, records, state);
        CompleteReferenceNaturalOptionalDirection(records, desiredIncome, true);
        if (SumIncome(records) < incomeTarget - incomeTailBand - incomeUnit * 2d - .009d)
        {
            CompleteReferenceNaturalOptionalDirection(records, incomeTarget, true);
            FillReferenceNaturalOptionalDirection(request, start, end, incomeTarget, true, random, records, state);
            // Audited banks can have only coarse/minimum-heavy income sources.
            // Absorb an unreachable tail into an existing variable reference;
            // do not add small rows or alter a fixed-date/fixed-amount payment.
            // Retain the established ICBC/RCB sampling sequence and results.
            if (!IcbcGenerationPolicy.AppliesTo(request.Bank, request.Config)
                && !PersonalRuralCommercialGenerationPolicy.AppliesTo(request.Bank, request.Config))
                CompleteReferenceNaturalOptionalDirection(records, desiredIncome, true, includeRequiredIncome: true);
        }
        var expenseTarget = RoundMoney(Math.Max(0, SumIncome(records) - request.Config.LastMoney));
        FillReferenceNaturalOptionalDirection(request, start, end, expenseTarget, false, random, records, state);
        EnsureCiticFixedExpenseCoverage(request, start, end, openingBalance, random, records, state);
        DistributeReferenceNaturalCentTails(records, random, false);
        // Retain the initial ICBC/RCB funding-transfer convention. Other initial
        // samples keep their dates; the shared planner handles only actual cash
        // shortages and refreshes date-bearing identifiers when a move is needed.
        if (IcbcGenerationPolicy.AppliesTo(request.Bank, request.Config)
            || PersonalRuralCommercialGenerationPolicy.AppliesTo(request.Bank, request.Config))
        {
            foreach (var row in records.Where(row => row.TradeMoney > 0 && IsGeneratedReferenceRecord(row) && !IsRequiredGeneratedRecord(row))
                         .OrderByDescending(row => row.TradeMoney).Take(1))
            {
                if (ResolveRecordSourceRule(request, row) is not GenerateReferenceRule rule) continue;
                var days = ReferenceNaturalAllowedDates(state, start, end, rule);
                if (days.Count == 0) continue;
                var previous = row.AccountTime!.Value;
                row.AccountTime = PickNativeTimeOnDate(days[0], rule, random, state, state.NextSequence());
                state.Move(row, previous);
            }
        }
        var result = EnsureAutomaticOpeningBalance(request, CreateAutomaticBalanceResult(records, openingBalance), recalculateInterest: true);
        TraceReferenceNaturalGeneration(request, "after-natural-opening", result.Records, runSeed, result.OpeningBalance);
        return result;
    }

    private static void GenerateReferenceNaturalAttempts(FlowAutoGenerationRequest request, DateTime start, DateTime end,
        Random random, List<FlowRecord> records, NativeScheduleState state)
    {
        foreach (var rule in request.References.Where(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) > 0))
        foreach (var month in EnumerateMonths(start, end))
        for (var attempt = 0; attempt < GetRequiredMonthlyCount(rule); attempt++)
        {
            var day = new DateTime(month.Start.Year, month.Start.Month, 1)
                .AddDays(random.Next(DateTime.DaysInMonth(month.Start.Year, month.Start.Month)));
            if (day < month.Start.Date || day > month.End.Date || !ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day, rule)) continue;
            var available = IsIncomeRuleSafe(rule) ? request.Config.AllInMoney - SumIncome(records) : double.MaxValue;
            if (!TrySampleReferenceNaturalRangeAmount(rule, available, random, out var amount)) continue;
            AddReferenceNaturalRecord(request, rule, day, amount, random, records, state);
        }
    }

    private static void FillReferenceNaturalOptionalDirection(FlowAutoGenerationRequest request, DateTime start, DateTime end,
        double target, bool income, Random random, List<FlowRecord> records, NativeScheduleState state)
    {
        var pool = request.References.Where(rule => rule.IsCheck && GetRequiredMonthlyCount(rule) == 0 && IsIncomeRuleSafe(rule) == income)
            .Select(rule => (Rule: rule, Bounds: GetRuleAmountBounds(rule), Dates: ReferenceNaturalAllowedDates(state, start, end, rule)))
            .Where(item => item.Dates.Count > 0).ToArray();
        var total = income ? SumIncome(records) : SumExpense(records);
        var largestMinimum = pool.Select(item => item.Bounds.Min).DefaultIfEmpty(0).Max();
        var preserveEstablishedTail = IcbcGenerationPolicy.AppliesTo(request.Bank, request.Config)
            || PersonalRuralCommercialGenerationPolicy.AppliesTo(request.Bank, request.Config);
        while (total < target - .009d)
        {
            var remaining = RoundMoney(target - total);
            var affordable = pool.Where(item => item.Bounds.Min <= remaining + .000001d).ToArray();
            var expectedDraw = affordable.Select(item => (item.Bounds.Min + Math.Min(item.Bounds.Max, remaining)) / 2d)
                .DefaultIfEmpty(0).Average();
            if (!income && remaining < largestMinimum && (preserveEstablishedTail || expectedDraw <= 0 || remaining / expectedDraw > 64d))
            {
                // At the budget tail, use existing optional capacity instead
                // of adding hundreds of tiny rows. An expensive rule becoming
                // unaffordable alone must not exclude the remaining medium ranges.
                CompleteReferenceNaturalOptionalDirection(records, target, false);
                total = SumExpense(records); remaining = RoundMoney(target - total);
                if (remaining <= .009d) return;
                affordable = pool.Where(item => item.Bounds.Min <= remaining + .000001d).ToArray();
            }
            if (affordable.Length == 0) return;
            var selected = affordable[random.Next(affordable.Length)];
            if (!TrySampleReferenceNaturalRangeAmount(selected.Rule, remaining, random, out var amount)) return;
            AddReferenceNaturalRecord(request, selected.Rule, selected.Dates[random.Next(selected.Dates.Count)], amount, random, records, state);
            total = RoundMoney(total + amount);
        }
    }

    private static void CompleteReferenceNaturalOptionalDirection(List<FlowRecord> records, double target, bool income,
        bool includeRequiredIncome = false)
    {
        var remaining = RoundMoney(target - (income ? SumIncome(records) : SumExpense(records)));
        foreach (var row in records.Where(row => (income ? row.TradeMoney > 0 : row.TradeMoney < 0)
                     && IsGeneratedReferenceRecord(row) && (!IsRequiredGeneratedRecord(row) || income && includeRequiredIncome))
                     .OrderByDescending(row => GetRecordAmountBounds(row).Unit))
        {
            if (remaining <= .009d) break;
            var before = Math.Abs(row.TradeMoney ?? 0); var bounds = GetRecordAmountBounds(row);
            var after = FindValidAmountAtOrBelow(Math.Min(bounds.Max, before + remaining), before, bounds);
            if (!after.HasValue || after <= before + .009d) continue;
            ApplySignedAmount(row, income ? 1 : -1, after.Value);
            remaining = RoundMoney(remaining - after.Value + before);
        }
    }

    private static IReadOnlyList<DateTime> ReferenceNaturalAllowedDates(NativeScheduleState state, DateTime start, DateTime end, FlowRuleBase rule) =>
        state.GetCandidateDates(start, end, rule).Where(day => ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day, rule)).ToArray();

    private static void AddReferenceNaturalRecord(FlowAutoGenerationRequest request, FlowRuleBase rule, DateTime day, double amount,
        Random random, List<FlowRecord> records, NativeScheduleState state)
    {
        if (records.Count >= MaximumFixedRuleRecordCount) throw new InvalidOperationException($"{request.Bank.Name}生成条数超过上限");
        var time = PickNativeTimeOnDate(day, rule, random, state, state.NextSequence());
        var row = CreateRecordFromRule(request, rule, rule is GenerateConstRule ? request.Bank.ConstColumns : request.Bank.ReferenceColumns,
            time, IsIncomeRuleSafe(rule) ? amount : -amount);
        records.Add(row); state.Register(row);
    }

    private static bool TrySampleReferenceNaturalRangeAmount(FlowRuleBase rule, double available, Random random, out double amount)
    {
        var bounds = GetRuleAmountBounds(rule);
        var maximum = Math.Min(bounds.Max, available);
        amount = 0;
        if (maximum < bounds.Min - .000001d) return false;
        var first = (long)Math.Ceiling(bounds.Min / bounds.Unit - .000001d);
        var last = (long)Math.Floor(maximum / bounds.Unit + .000001d);
        if (last < first) return false;
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var candidate = RoundMoney(random.NextInt64(first, last + 1) * bounds.Unit);
            if (!IsReferenceNaturalLegalAmount(candidate, bounds)) continue;
            amount = candidate; return true;
        }
        var fallback = FindValidAmountAtOrAbove(bounds.Min, maximum, bounds);
        if (!fallback.HasValue) return false;
        amount = fallback.Value; return true;
    }

    private static void DistributeReferenceNaturalCentTails(List<FlowRecord> rows, Random random, bool income)
    {
        // Flooring an illegal .01-.09 fraction into the preceding .99 creates
        // another spike. Assign least-used legal tails before interest exists;
        // the full source ranges and selected transaction counts stay intact.
        var candidates = rows.Where(row => income ? row.TradeMoney > 0 : row.TradeMoney < 0)
            .Where(IsGeneratedReferenceRecord).Where(row =>
        {
            var bounds = GetRecordAmountBounds(row);
            return bounds.Precision == 2 && bounds.Unit == .01d && bounds.Max > bounds.Min;
        });
        foreach (var group in candidates.GroupBy(row => row.Account ?? string.Empty))
        {
            var usage = new int[100]; var recent = new Queue<int>();
            foreach (var row in group.OrderBy(row => row.AccountTime).ThenBy(row => row.Index))
            {
                var before = Math.Abs(row.TradeMoney ?? 0); var whole = Math.Floor(before); var bounds = GetRecordAmountBounds(row);
                var choices = new List<(int Tail, double Amount, int Tie)>();
                for (var tail = 11; tail <= 99; tail++)
                {
                    if (tail % 10 == 0) continue;
                    var amount = new[] { whole + tail / 100d, whole - 1 + tail / 100d, whole + 1 + tail / 100d }
                        .Select(RoundMoney).Where(value => IsReferenceNaturalLegalAmount(value, bounds))
                        .OrderBy(value => Math.Abs(value - before)).Select(value => (double?)value).FirstOrDefault();
                    if (amount.HasValue) choices.Add((tail, amount.Value, random.Next()));
                }
                if (choices.Count == 0) continue;
                var chosen = choices.OrderBy(choice => usage[choice.Tail]).ThenBy(choice => recent.Contains(choice.Tail) ? 1 : 0)
                    .ThenBy(choice => choice.Tie).First();
                ApplySignedAmount(row, income ? 1 : -1, chosen.Amount);
                usage[chosen.Tail]++; recent.Enqueue(chosen.Tail);
                if (recent.Count > 24) recent.Dequeue();
            }
        }
    }

    private static bool IsReferenceNaturalLegalAmount(double amount, AmountBounds bounds) => amount >= bounds.Min - .000001d
        && amount <= bounds.Max + .000001d && Math.Abs(RoundAmountToUnit(amount, bounds.Unit) - amount) < .000001d
        && HasRequiredNonZeroPrecisionDigit(amount, bounds);

    private static double ReferenceNaturalOrdinaryTotal(IEnumerable<FlowRecord> rows, bool income) => RoundMoney(rows
        .Where(row => !IsSystemInterestRecord(row) && (income ? row.TradeMoney > 0 : row.TradeMoney < 0))
        .Sum(row => Math.Abs(row.TradeMoney ?? 0)));

    private static void TraceReferenceNaturalGeneration(FlowAutoGenerationRequest request, string stage, List<FlowRecord> rows, int seed, double openingBalance)
    {
        if (System.Windows.Application.Current is null) return;
        try
        {
            var logName = PersonalRuralCommercialGenerationPolicy.AppliesTo(request.Bank, request.Config)
                ? "generation-personal-rcb.log" : IcbcGenerationPolicy.AppliesTo(request.Bank, request.Config)
                    ? "generation-icbc.log" : $"generation-natural-{request.Bank.Id}.log";
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeedEmulator", "logs", logName);
            var line = JsonSerializer.Serialize(new { time = DateTimeOffset.Now, stage, seed, UserId = request.BankUser.Id,
                BankId = request.Bank.Id,
                request.Config.StartTime, request.Config.EndTime, openingBalance, Target = request.Config.AllInMoney,
                RequestedOpening = request.OpeningBalanceOverride ?? request.Config.OpeningBalance,
                OpeningFunding = RoundMoney(Math.Max(0, openingBalance - (request.OpeningBalanceOverride ?? request.Config.OpeningBalance))),
                ReferencePriority = UsesReferencePriority(request.Config, rows),
                request.Config.LastMoney, Rows = rows.Count, OrdinaryIncome = ReferenceNaturalOrdinaryTotal(rows, true),
                Income = SumIncome(rows), Expense = SumExpense(rows), SmallExpenses = rows.Count(row => row.TradeMoney < 0 && row.TradeMoney >= -15),
                PeakDailyRows = rows.Where(row => !IsSystemInterestRecord(row) && row.AccountTime.HasValue)
                    .GroupBy(row => row.AccountTime!.Value.Date).Select(group => group.Count()).DefaultIfEmpty(0).Max(),
                Months = rows.Where(row => !IsSystemInterestRecord(row) && row.AccountTime.HasValue)
                    .GroupBy(row => row.AccountTime!.Value.ToString("yyyy-MM")).OrderBy(group => group.Key)
                    .Select(group => new { Month = group.Key, IncomeRows = group.Count(row => row.TradeMoney > 0),
                        VariableExpenseRows = group.Count(row => row.TradeMoney < 0 && IsGeneratedReferenceRecord(row)
                            && GetRecordAmountBounds(row).Max > GetRecordAmountBounds(row).Min + .000001), Rows = group.Count() }) });
            lock (ReferenceNaturalDiagnosticLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* Diagnostic I/O must not change a generated ledger. */ }
    }
}
