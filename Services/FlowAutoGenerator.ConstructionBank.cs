using System.Globalization;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private const string CcbStratumMin = "__CcbStratumMin";
    private const string CcbStratumMax = "__CcbStratumMax";
    private const string CcbWindowStart = "__CcbWindowStart";
    private const string CcbWindowEnd = "__CcbWindowEnd";
    private const string CcbFixedDate = "__CcbFixedDate";
    private const string CcbPeriodIncome = "__CcbPeriodIncome";
    private const string CcbPeriodExpense = "__CcbPeriodExpense";
    private const string CcbScheduledTime = "__CcbScheduledTime";

    private sealed record CcbWindow(DateTime Start, DateTime End, bool Fixed = false, DateTime? ScheduledDate = null);
    private sealed record CcbPeriod(DateTime Start, DateTime End, double Income, double Expense);

    private sealed class CcbRulePlan(FlowRuleBase rule, IReadOnlyList<ColumnDefinition> columns)
    {
        public FlowRuleBase Rule { get; } = rule;
        public IReadOnlyList<ColumnDefinition> Columns { get; } = columns;
        public CcbAmountRange Range { get; } = new(GetRuleAmountBounds(rule));
        public List<CcbWindow> Windows { get; } = [];
        public bool Optional => Rule is GenerateReferenceRule reference && GetRequiredMonthlyCount(reference) == 0;
        public CcbWindow? OptionalWindow { get; set; }
        public int Count { get; set; }
    }

    /// <summary>Ranks all values accepted by the existing precision contract, without a sampled value pool.</summary>
    private sealed class CcbAmountRange
    {
        public AmountBounds Bounds { get; }
        private readonly long first;
        private readonly long last;
        private readonly bool nonzeroDigit;
        private readonly bool excludeLowCentTail;
        private readonly long offset;
        public long Count { get; }
        public double Mean => (Value(0) + Value(Count - 1)) / 2d;

        public CcbAmountRange(AmountBounds bounds)
        {
            Bounds = bounds;
            if (!double.IsFinite(bounds.Min) || !double.IsFinite(bounds.Max)
                || bounds.Max / bounds.Unit >= long.MaxValue / 10d)
                throw new InvalidOperationException("建行金额范围过大或无效，请检查最小金额、最大金额和小数位。");
            first = checked((long)Math.Round(bounds.Min / bounds.Unit));
            last = checked((long)Math.Round(bounds.Max / bounds.Unit));
            nonzeroDigit = RequiresNonZeroPrecisionDigit(bounds);
            excludeLowCentTail = bounds.Precision == 2;
            offset = Through(first - 1);
            Count = Through(last) - offset;
            if (Count <= 0) throw new InvalidOperationException("建行金额区间内没有符合小数位规则的金额。");
        }

        private long Through(long step)
        {
            if (!excludeLowCentTail) return nonzeroDigit ? step - step / 10 : step;
            var tail = step % 100;
            return step / 100 * 81 + (tail < 11 ? 0 : (tail / 10 - 1) * 9 + tail % 10);
        }
        public double Value(long rank)
        {
            rank = Math.Clamp(rank, 0, Count - 1);
            var position = offset + rank + 1;
            var index = position - 1;
            var step = excludeLowCentTail
                ? index / 81 * 100 + (index % 81 / 9 + 1) * 10 + index % 9 + 1
                : nonzeroDigit ? index / 9 * 10 + index % 9 + 1 : first + rank;
            return RoundMoney(step * Bounds.Unit);
        }

        public long RankAtOrBelow(double value)
        {
            var step = Math.Clamp((long)Math.Floor((value + 0.0000001d) / Bounds.Unit), first, last);
            return Math.Clamp(Through(step) - offset - 1, 0, Count - 1);
        }
    }

    private sealed class CcbSample(CcbRulePlan plan, long lower, long upper, long selected)
    {
        public CcbRulePlan Plan { get; } = plan;
        public long Lower { get; } = lower;
        public long Upper { get; } = upper;
        public long Selected { get; set; } = selected;
        public double Amount => Plan.Range.Value(Selected);
        public FlowRecord? Row { get; set; }
    }

    private static FlowAutoGenerationResult GenerateConstructionBankResult(
        FlowAutoGenerationRequest request, DateTime start, DateTime end, double openingBalance, Random random)
    {
        if (!double.IsFinite(request.Config.AllInMoney) || request.Config.AllInMoney < 0
            || !double.IsFinite(request.Config.LastMoney) || !double.IsFinite(openingBalance))
            throw new InvalidOperationException("建行生成金额配置无效。");

        var plans = CreateCcbRequiredPlans(request, start, end, random);
        var periods = CreateCcbPeriods(request, start, end, random);
        var records = new List<FlowRecord>();
        var state = NativeScheduleState.From(records);
        foreach (var period in periods)
        foreach (var income in new[] { true, false })
        {
            var periodPlans = plans.Where(plan => IsIncomeRuleSafe(plan.Rule) == income)
                .Select(plan => CopyCcbPlanForPeriod(plan, period)).ToList();
            var target = income ? period.Income : period.Expense;
            GenerateCcbDirection(request, periodPlans, period, target, income, random, records, state);
        }

        return FinalizeCcbResult(request, records, periods, start, end, openingBalance, random, repairNegative: true);
    }

    private static List<CcbRulePlan> CreateCcbRequiredPlans(
        FlowAutoGenerationRequest request, DateTime start, DateTime end, Random random)
    {
        var plans = new List<CcbRulePlan>();
        foreach (var rule in request.ConstItems.Where(rule => rule.IsCheck && !string.IsNullOrWhiteSpace(rule.FixDay)))
        {
            var plan = new CcbRulePlan(rule, request.Bank.ConstColumns);
            var option = FixedDateRuleParser.Parse(rule.FixDay);
            if (!option.IsValid) throw new InvalidOperationException($"建行固定日期项目第{rule.Index}行日期格式无效。");
            var repeat = GetNativeRepeatCount(rule.ReCnt, random);
            // Fixed-date additions deliberately ignore both reference date checkboxes.
            if (option.Kind == FixedDateRuleKind.FixedDay)
            {
                foreach (var month in EnumerateMonths(start, end))
                {
                    var date = new DateTime(month.Start.Year, month.Start.Month,
                        Math.Min(option.StartDay, DateTime.DaysInMonth(month.Start.Year, month.Start.Month)));
                    if (date < start.Date || date > end.Date) continue;
                    for (var i = 0; i < repeat; i++) plan.Windows.Add(new CcbWindow(date, NormalizeEndDate(date), true));
                }
            }
            else if (option.Kind == FixedDateRuleKind.Daily)
            {
                foreach (var date in EnumerateDates(start.Date, end.Date))
                for (var i = 0; i < repeat; i++) plan.Windows.Add(new CcbWindow(date, NormalizeEndDate(date), true));
            }
            else
            {
                var windows = option.Kind == FixedDateRuleKind.Global
                    ? new[] { (Start: start, End: end) }.ToList() : EnumerateMonths(start, end).ToList();
                foreach (var window in windows)
                {
                    var from = window.Start;
                    var to = window.End;
                    if (option.Kind == FixedDateRuleKind.DayRange)
                    {
                        var days = DateTime.DaysInMonth(from.Year, from.Month);
                        from = new[] { from, new DateTime(from.Year, from.Month, Math.Min(option.StartDay, days)) }.Max();
                        to = new[] { to, NormalizeEndDate(new DateTime(to.Year, to.Month, Math.Min(option.EndDay, days))) }.Min();
                        if (from > to) continue;
                    }
                    var dates = GetCcbDates(rule, from, to);
                    if (dates.Count == 0) throw new InvalidOperationException($"建行固定日期项目第{rule.Index}行没有可用交易时段。");
                    var selected = SpreadNativeFixedOccurrences(dates, repeat, new NativeScheduleState(),
                        new Dictionary<DateTime, int>(), random);
                    foreach (var date in selected) plan.Windows.Add(new CcbWindow(from, to, ScheduledDate: date));
                }
            }
            plan.Count = plan.Windows.Count;
            plans.Add(plan);
        }
        foreach (var rule in request.References.Where(rule => rule.IsCheck))
        {
            var plan = new CcbRulePlan(rule, request.Bank.ReferenceColumns);
            var required = GetRequiredMonthlyCount(rule);
            if (required > 0)
            {
                foreach (var month in EnumerateMonths(start, end))
                {
                    EnsureCcbDates(rule, month.Start, month.End);
                    for (var i = 0; i < required; i++) plan.Windows.Add(new CcbWindow(month.Start, month.End));
                }
            }
            else plan.OptionalWindow = new CcbWindow(start, end);
            plan.Count = plan.Windows.Count;
            plans.Add(plan);
        }
        if (plans.Sum(plan => (long)plan.Count) > MaximumFixedRuleRecordCount)
            throw new InvalidOperationException("建行固定项目和每月必现次数超过单次生成上限。");
        return plans;
    }

    private static CcbRulePlan CopyCcbPlanForPeriod(CcbRulePlan source, CcbPeriod period)
    {
        var result = new CcbRulePlan(source.Rule, source.Columns);
        foreach (var window in source.Windows)
        {
            if (window.ScheduledDate is { } scheduled
                && (scheduled < period.Start.Date || scheduled > period.End.Date)) continue;
            var from = window.Start < period.Start ? period.Start : window.Start;
            var to = window.End > period.End ? period.End : window.End;
            if (from <= to) result.Windows.Add(new CcbWindow(from, to, window.Fixed, window.ScheduledDate));
        }
        result.Count = result.Windows.Count;
        if (source.Optional) result.OptionalWindow = new CcbWindow(period.Start, period.End);
        return result;
    }

    private static List<CcbPeriod> CreateCcbPeriods(FlowAutoGenerationRequest request, DateTime start, DateTime end, Random random)
    {
        var income = RoundMoney(request.Config.AllInMoney);
        var expense = RoundMoney(Math.Max(0, income - request.Config.LastMoney));
        if (request.Config.SelectIndex == 0) return [new CcbPeriod(start, end, income, expense)];
        var months = EnumerateMonths(start, end).ToList();
        double[] incomes;
        double[] expenses;
        if (request.Config.SelectIndex == 2)
        {
            incomes = CreateMonthDetailRawTargets(request.Config, months, true);
            expenses = CreateMonthDetailRawTargets(request.Config, months, false);
            if (Math.Abs(incomes.Sum() - income) > 0.011d || Math.Abs(expenses.Sum() - expense) > 0.011d)
                throw new InvalidOperationException("建行按月明细的收入/支出合计与总进账及最后卡上金额不一致，请先调整配置。");
        }
        else
        {
            incomes = AllocateCcbMonthRange(income, months.Count, request.Config.MinInMoneyMonth2,
                request.Config.MaxInMoneyMonth2, GetCcbBudgetUnit(request, true), random);
            expenses = AllocateCcbMonthRange(expense, months.Count, request.Config.MinOutMoneyMonth2,
                request.Config.MaxOutMoneyMonth2, GetCcbBudgetUnit(request, false), random);
        }
        return months.Select((month, i) => new CcbPeriod(month.Start, month.End, incomes[i], expenses[i])).ToList();
    }

    private static double GetCcbBudgetUnit(FlowAutoGenerationRequest request, bool income) =>
        request.References.Cast<FlowRuleBase>().Concat(request.ConstItems.Where(rule => !string.IsNullOrWhiteSpace(rule.FixDay)))
            .Where(rule => rule.IsCheck && IsIncomeRuleSafe(rule) == income)
            .Select(rule => Math.Max(0.01d, GetRuleAmountBounds(rule).Unit)).DefaultIfEmpty(0.01d).Min();

    private static double[] AllocateCcbMonthRange(double target, int count, double min, double max, double unit, Random random)
    {
        var targetSteps = checked((long)Math.Round(target / unit));
        if (Math.Abs(RoundMoney(targetSteps * unit) - target) > AcceptedFinalBalanceTolerance + 0.009d)
            throw new InvalidOperationException("建行总金额与勾选项目的金额精度不兼容，请调整总额或小数位。");
        if (min <= 0 && max <= 0)
        {
            var values = Enumerable.Repeat(targetSteps / count, count).ToArray();
            foreach (var i in Enumerable.Range(0, count).OrderBy(_ => random.Next()).Take((int)(targetSteps % count))) values[i]++;
            var budgets = values.Select(value => RoundMoney(value * unit)).ToArray();
            budgets[^1] = RoundMoney(budgets[^1] + target - budgets.Sum());
            return budgets;
        }
        if (max < min) (min, max) = (max, min);
        var minimum = checked((long)Math.Ceiling(min / unit - 0.0000001d));
        var maximum = checked((long)Math.Floor(max / unit + 0.0000001d));
        if (minimum > maximum || targetSteps < minimum * count || targetSteps > maximum * count)
            throw new InvalidOperationException("建行月收支金额范围无法满足配置总额，请调整月范围或总额。");
        var amounts = Enumerable.Range(0, count).Select(_ => random.NextInt64(minimum, maximum + 1)).ToArray();
        var difference = targetSteps - amounts.Sum();
        foreach (var i in Enumerable.Range(0, count).OrderBy(_ => random.Next()))
        {
            var change = Math.Clamp(difference, minimum - amounts[i], maximum - amounts[i]);
            amounts[i] += change;
            difference = targetSteps - amounts.Sum();
        }
        var result = amounts.Select(value => RoundMoney(value * unit)).ToArray();
        var remainder = RoundMoney(target - result.Sum());
        foreach (var i in Enumerable.Range(0, count).OrderBy(_ => random.Next()))
        {
            var change = Math.Clamp(remainder, min - result[i], max - result[i]);
            result[i] = RoundMoney(result[i] + change);
            remainder = RoundMoney(target - result.Sum());
        }
        return result;
    }

    private static void GenerateCcbDirection(FlowAutoGenerationRequest request, List<CcbRulePlan> plans,
        CcbPeriod period, double target, bool income, Random random, List<FlowRecord> records, NativeScheduleState state)
    {
        var mandatoryMin = plans.Where(plan => !plan.Optional).Sum(plan => plan.Count * plan.Range.Bounds.Min);
        if (mandatoryMin > target + 0.011d)
            throw new InvalidOperationException($"建行{(income ? "收入" : "支出")}固定日期/每月必现项目最低需要{mandatoryMin:N2}元，超过本期预算{target:N2}元。");
        var optional = plans.Where(plan => plan.Optional
            && GetCcbDates(plan.Rule, period.Start, period.End).Count > 0).ToList();
        if (target > mandatoryMin + 0.009d)
        {
            // Similar ranges share a budget family, so eleven transfer counterparties do
            // not give transfers eleven times the budget of a single consumption range.
            var families = optional.GroupBy(plan => (plan.Rule.MinMoney, plan.Rule.MaxMoney)).ToList();
            var coverageMin = optional.Sum(plan => plan.Range.Bounds.Min);
            if (mandatoryMin + coverageMin > target + 0.011d)
                throw new InvalidOperationException("建行预算不足以覆盖全部勾选参照，请减少勾选项或提高预算。");
            foreach (var plan in optional) plan.Count = 1;
            var remaining = Math.Max(0, target - plans.Sum(plan => plan.Count * plan.Range.Mean));
            // Vary the optional budget mix, rather than biasing amount samples or
            // changing required occurrences. Equal ranges remain in one family.
            var weights = families.Select(_ => 0.8d + random.NextDouble() * 0.4d).ToArray();
            var weightTotal = weights.Sum();
            for (var familyIndex = 0; familyIndex < families.Count; familyIndex++)
            {
                var family = families[familyIndex];
                var members = family.OrderBy(_ => random.Next()).ToArray();
                var budget = members.Sum(plan => plan.Range.Mean) + remaining * weights[familyIndex] / weightTotal;
                var estimate = Math.Round(budget / members.Average(plan => plan.Range.Mean));
                if (estimate > MaximumFixedRuleRecordCount)
                    throw new InvalidOperationException("建行预计生成笔数超过单次上限，请调整金额范围或总额。");
                var count = Math.Max(members.Length, (int)estimate);
                for (var i = members.Length; i < count; i++) members[i % members.Length].Count++;
            }
            // Resolve count rounding using whole occurrences before touching amounts.
            // In particular, do not ask cent-precision consumption rows to absorb a
            // multi-thousand-yuan discrepancy left by rounding transfer row counts.
            for (var attempt = 0; attempt < 128; attempt++)
            {
                var expected = plans.Sum(plan => plan.Count * plan.Range.Mean);
                var difference = Math.Abs(target - expected);
                var choices = optional.SelectMany(plan => new[] { -1, 1 }
                    .Where(change => plan.Count + change >= 1)
                    .Select(change => new { Plan = plan, Change = change,
                        Error = Math.Abs(target - expected - change * plan.Range.Mean) }))
                    .Where(choice => choice.Error < difference - 0.000001d)
                    .OrderBy(choice => choice.Error).ThenBy(_ => random.Next()).ToList();
                if (choices.Count == 0) break;
                choices[0].Plan.Count += choices[0].Change;
            }
            // With one optional family, normalized budget weights cannot vary.
            // Try a neighbouring sample size; the existing range/precision solver
            // retains it only when the configured budget is attainable.
            if (families.Count == 1)
            {
                var change = random.Next(-1, 2);
                var candidates = optional.Where(plan => plan.Range.Count > 1 && plan.Count + change >= 1)
                    .OrderBy(_ => random.Next());
                foreach (var plan in candidates)
                {
                    if (plans.Sum(item => (long)item.Count) + records.Count + change > MaximumFixedRuleRecordCount) break;
                    var min = plans.Sum(item => item.Count * item.Range.Bounds.Min) + change * plan.Range.Bounds.Min;
                    var max = plans.Sum(item => item.Count * item.Range.Bounds.Max) + change * plan.Range.Bounds.Max;
                    if (target < min - 0.011d || target > max + 0.011d) continue;
                    plan.Count += change;
                    break;
                }
            }
        }

        List<CcbSample> samples = [];
        for (var attempt = 0; attempt < 64; attempt++)
        {
            if (plans.Sum(plan => (long)plan.Count) + records.Count > MaximumFixedRuleRecordCount)
                throw new InvalidOperationException("建行生成笔数超过单次上限，请调整金额范围或总额。");
            samples = plans.SelectMany(plan => CreateCcbUniformSamples(plan, random)).ToList();
            var attainableTarget = NearestCcbBudgetTarget(samples, target);
            var minimum = samples.Sum(sample => sample.Plan.Range.Value(sample.Lower));
            var maximum = samples.Sum(sample => sample.Plan.Range.Value(sample.Upper));
            if ((samples.Count > 0 || target <= 0.009d)
                && attainableTarget >= minimum - 0.011d && attainableTarget <= maximum + 0.011d
                && BalanceCcbSamples(samples, target, random)) break;
            var increase = attainableTarget > maximum + 0.009d;
            var candidates = optional.Where(plan => increase || plan.Count > 1).ToList();
            // Distribution bands are a sampling preference, not another user
            // restriction. Fixed monthly counts can leave a feasible budget outside
            // those bands; close a small flexible tail inside the original ranges.
            if ((candidates.Count == 0 || attempt == 63) && BalanceCcbFlexibleTail(samples, target, random)) break;
            if (candidates.Count == 0)
                throw new InvalidOperationException($"建行{period.Start:yyyy-MM}{(income ? "收入" : "支出")}预算与完整区间均匀取值及固定次数无法同时满足（目标{target:F2}，当前{samples.Sum(sample => sample.Amount):F2}，原始容量{samples.Sum(sample => sample.Plan.Range.Value(0)):F2}～{samples.Sum(sample => sample.Plan.Range.Value(sample.Plan.Range.Count - 1)):F2}）。");
            var expected = plans.Sum(plan => plan.Count * plan.Range.Mean);
            var selected = candidates.OrderBy(plan => Math.Abs(target - (expected + (increase ? 1 : -1) * plan.Range.Mean)))
                .ThenBy(_ => random.Next()).First();
            selected.Count += increase ? 1 : -1;
            if (attempt == 63) throw new InvalidOperationException("建行无法在保留完整金额分布的条件下满足预算，请调整金额范围或次数。");
        }
        foreach (var plan in plans)
        {
            var amounts = samples.Where(sample => ReferenceEquals(sample.Plan, plan)).OrderBy(_ => random.Next()).ToArray();
            var dates = plan.Optional ? GetCcbDates(plan.Rule, period.Start, period.End) : [];
            for (var i = 0; i < amounts.Length; i++)
            {
                var window = plan.Optional ? plan.OptionalWindow! : plan.Windows[i];
                var allowed = plan.Optional ? dates : GetCcbDates(plan.Rule, window.Start, window.End);
                if (allowed.Count == 0) throw new InvalidOperationException($"建行第{plan.Rule.Index}行在指定日期范围没有可交易日期。");
                var date = window.ScheduledDate ?? allowed.OrderBy(day => state.GetTotalCount(day)).ThenBy(_ => random.Next()).First();
                var time = PickCcbTime(date, window, plan.Rule, random, state);
                var sample = amounts[i];
                var row = CreateRecordFromRule(request, plan.Rule, plan.Columns, time, income ? sample.Amount : -sample.Amount);
                row.ExtraFields[CcbStratumMin] = sample.Plan.Range.Value(sample.Lower).ToString("R", CultureInfo.InvariantCulture);
                row.ExtraFields[CcbStratumMax] = sample.Plan.Range.Value(sample.Upper).ToString("R", CultureInfo.InvariantCulture);
                row.ExtraFields[CcbWindowStart] = window.Start.ToString("O", CultureInfo.InvariantCulture);
                row.ExtraFields[CcbWindowEnd] = window.End.ToString("O", CultureInfo.InvariantCulture);
                row.ExtraFields[CcbFixedDate] = window.Fixed.ToString();
                row.ExtraFields[CcbPeriodIncome] = period.Income.ToString("R", CultureInfo.InvariantCulture);
                row.ExtraFields[CcbPeriodExpense] = period.Expense.ToString("R", CultureInfo.InvariantCulture);
                row.ExtraFields[CcbScheduledTime] = time.ToString("O", CultureInfo.InvariantCulture);
                sample.Row = row;
                records.Add(row);
                state.Register(row);
            }
        }
    }

    private static List<CcbSample> CreateCcbUniformSamples(CcbRulePlan plan, Random random)
    {
        var samples = new List<CcbSample>(plan.Count);
        var fullCycles = plan.Count / plan.Range.Count;
        for (var cycle = 0L; cycle < fullCycles; cycle++)
        for (var rank = 0L; rank < plan.Range.Count; rank++) samples.Add(new CcbSample(plan, rank, rank, rank));
        var remaining = (int)(plan.Count % plan.Range.Count);
        if (plan.Range.Count > ReferenceAmountDistributionStepLimit && remaining > 0)
        {
            // Keep monetary bands uniform even when precision removes a whole
            // cent interval. Pure rank bands can drift across the original
            // equal-width boundaries after excluding .01 through .09.
            var bands = Math.Min(10, remaining);
            var monetary = new (long Lower, long Upper)[bands];
            var useMonetary = plan.Range.Bounds.Precision == 2;
            var rawMin = Math.Max(0, plan.Rule.MinMoney ?? plan.Range.Bounds.Min);
            var rawMax = plan.Rule.MaxMoney ?? plan.Range.Bounds.Max;
            for (var band = 0; useMonetary && band < bands; band++)
            {
                var lower = band == 0 ? 0 : plan.Range.RankAtOrBelow(rawMin + (rawMax - rawMin) * band / bands - 0.00001d) + 1;
                var upper = band == bands - 1 ? plan.Range.Count - 1 : plan.Range.RankAtOrBelow(rawMin + (rawMax - rawMin) * (band + 1) / bands - 0.00001d);
                monetary[band] = (lower, upper);
                if (upper - lower + 1 < remaining / bands + (band < remaining % bands ? 1 : 0)) useMonetary = false;
            }
            var used = new HashSet<long>();
            foreach (var band in Enumerable.Range(0, bands).OrderBy(_ => random.Next()))
            {
                var count = remaining / bands + (band < remaining % bands ? 1 : 0);
                var lower = useMonetary ? monetary[band].Lower : band * plan.Range.Count / bands;
                var upper = useMonetary ? monetary[band].Upper : (band + 1L) * plan.Range.Count / bands - 1;
                for (var i = 0; i < count; i++)
                {
                    // Preserve small edge strata inside the first/last monetary
                    // bands. Budget closing must not move every low-end sample
                    // above the bottom of an otherwise uniform wide range.
                    var edge = useMonetary && remaining >= 20 && i == 0;
                    var edgeWidth = Math.Max(1, (upper - lower + 1) / 10);
                    var sampleLower = edge && band == bands - 1 ? upper - edgeWidth + 1 : lower;
                    var sampleUpper = edge && band == 0 ? lower + edgeWidth - 1 : upper;
                    long rank;
                    do { rank = random.NextInt64(sampleLower, sampleUpper + 1); } while (!used.Add(rank));
                    samples.Add(new CcbSample(plan, sampleLower, sampleUpper, rank));
                }
            }
            return samples;
        }
        // Rotate leftover strata around the legal-value ring. Always beginning
        // at rank zero fixes their total range and can force one sample count,
        // even though neighbouring counts have equally balanced value coverage.
        var phase = remaining > 0 ? random.NextInt64(plan.Range.Count) : 0;
        for (var i = 0; i < remaining; i++)
        {
            var lower = (i * plan.Range.Count / remaining + phase) % plan.Range.Count;
            var upper = ((i + 1L) * plan.Range.Count / remaining - 1 + phase) % plan.Range.Count;
            if (lower <= upper)
                samples.Add(new CcbSample(plan, lower, upper, random.NextInt64(lower, upper + 1)));
            else
            {
                var selected = (lower + random.NextInt64(plan.Range.Count - lower + upper + 1)) % plan.Range.Count;
                samples.Add(new CcbSample(plan, selected >= lower ? lower : 0,
                    selected >= lower ? plan.Range.Count - 1 : upper, selected));
            }
        }
        return samples;
    }

    private static bool BalanceCcbSamples(List<CcbSample> samples, double target, Random random)
    {
        target = NearestCcbBudgetTarget(samples, target);
        if (target < samples.Sum(sample => sample.Plan.Range.Value(sample.Lower)) - 0.009d
            || target > samples.Sum(sample => sample.Plan.Range.Value(sample.Upper)) + 0.009d) return false;
        var total = RoundMoney(samples.Sum(sample => sample.Amount));
        for (var pass = 0; pass < 8 && Math.Abs(target - total) > 0.009d; pass++)
        {
            var changed = false;
            var initialDelta = RoundMoney(target - total);
            var capacity = samples.Sum(sample => initialDelta > 0
                ? sample.Plan.Range.Value(sample.Upper) - sample.Amount
                : sample.Amount - sample.Plan.Range.Value(sample.Lower));
            foreach (var sample in samples.OrderBy(sample => pass < 3 ? 0 : sample.Plan.Range.Bounds.Unit).ThenBy(_ => random.Next()))
            {
                var delta = RoundMoney(target - total);
                if (Math.Abs(delta) <= 0.009d) return true;
                var before = sample.Amount;
                var room = initialDelta > 0 ? sample.Plan.Range.Value(sample.Upper) - before
                    : before - sample.Plan.Range.Value(sample.Lower);
                var share = pass < 3 && capacity > 0.009d ? initialDelta * room / capacity : delta;
                var rank = Math.Clamp(sample.Plan.Range.RankAtOrBelow(before + share), sample.Lower, sample.Upper);
                var best = new[] { rank, Math.Min(sample.Upper, rank + 1) }
                    .OrderBy(candidate => Math.Abs(share - (sample.Plan.Range.Value(candidate) - before))).First();
                var change = RoundMoney(sample.Plan.Range.Value(best) - before);
                if (Math.Abs(delta - change) >= Math.Abs(delta) - 0.000001d) continue;
                sample.Selected = best;
                total = RoundMoney(total + change);
                changed = true;
            }
            // A sub-cent proportional share may round to zero for every row.
            // Still run the closing passes, which can assign the remaining cent
            // to a row without changing its assigned distribution band.
            if (!changed && pass >= 3) break;
        }
        return Math.Abs(target - total) <= 0.009d || CloseCcbResidual(samples, RoundMoney(target - total));
    }

    private static bool CloseCcbResidual(List<CcbSample> samples, double delta)
    {
        if (CloseCcbSamplePair(samples, delta)) return true;
        var movable = samples.Where(sample => sample.Lower < sample.Upper).ToList();
        if (movable.Count < 3) return false;
        var finestUnit = movable.Min(sample => sample.Plan.Range.Bounds.Unit);
        foreach (var primary in movable.OrderBy(sample => sample.Plan.Range.Bounds.Unit <= finestUnit)
                     .ThenBy(sample => sample.Plan.Range.Bounds.Unit))
        {
            var others = movable.Where(sample => !ReferenceEquals(sample, primary)).ToList();
            foreach (var rank in CcbClosingNeighborRanks(primary))
            {
                if (rank < primary.Lower || rank > primary.Upper) continue;
                var change = RoundMoney(primary.Plan.Range.Value(rank) - primary.Amount);
                // Some totals require a three-row move. Look beyond the old
                // two-rank neighbourhood to cross the new forbidden interval.
                if (!CloseCcbSamplePair(others, RoundMoney(delta - change))) continue;
                primary.Selected = rank;
                return true;
            }
        }
        return false;
    }

    private static double NearestCcbBudgetTarget(List<CcbSample> samples, double target)
    {
        if (samples.Count == 0) return 0;
        var fixedAmount = samples.Where(sample => sample.Plan.Range.Count == 1).Sum(sample => sample.Amount);
        var variable = samples.Where(sample => sample.Plan.Range.Count > 1).ToList();
        var unit = variable.Select(sample => sample.Plan.Range.Bounds.Unit).DefaultIfEmpty(0.01d).Min();
        var rounded = variable.Count == 0 ? RoundMoney(fixedAmount)
            : RoundMoney(fixedAmount + Math.Round((target - fixedAmount) / unit, MidpointRounding.AwayFromZero) * unit);
        // Keep configured precision, and use only the nearest attainable total.
        // Never use rounding to exceed the application's existing balance tolerance.
        return Math.Abs(rounded - target) <= AcceptedFinalBalanceTolerance + 0.009d ? rounded : target;
    }

    private static bool BalanceCcbFlexibleTail(List<CcbSample> samples, double target, Random random)
    {
        target = NearestCcbBudgetTarget(samples, target);
        var lower = samples.Sum(sample => sample.Plan.Range.Value(0));
        var upper = samples.Sum(sample => sample.Plan.Range.Value(sample.Plan.Range.Count - 1));
        if (target < lower - 0.009d || target > upper + 0.009d) return false;
        var relaxed = samples.Select(sample => new CcbSample(sample.Plan, sample.Lower, sample.Upper, sample.Selected)
            { Row = sample.Row }).ToList();
        var increase = target > relaxed.Sum(sample => sample.Amount);
        var candidates = Enumerable.Range(0, relaxed.Count).Select(index => new
        {
            Index = index,
            Room = increase ? relaxed[index].Plan.Range.Value(relaxed[index].Plan.Range.Count - 1) - relaxed[index].Amount
                : relaxed[index].Amount - relaxed[index].Plan.Range.Value(0),
            Extension = relaxed[index].Plan.Range.Value(relaxed[index].Lower) - relaxed[index].Plan.Range.Value(0)
                + relaxed[index].Plan.Range.Value(relaxed[index].Plan.Range.Count - 1) - relaxed[index].Plan.Range.Value(relaxed[index].Upper)
        }).Where(candidate => candidate.Extension > 0.009d)
            .OrderByDescending(candidate => candidate.Room).ToList();
        foreach (var candidate in candidates)
        {
            var sample = relaxed[candidate.Index];
            relaxed[candidate.Index] = new CcbSample(sample.Plan, 0, sample.Plan.Range.Count - 1, sample.Selected) { Row = sample.Row };
            if (!BalanceCcbSamples(relaxed, target, random)) continue;
            samples.Clear();
            samples.AddRange(relaxed);
            return true;
        }
        return false;
    }

    private static bool CloseCcbSamplePair(List<CcbSample> samples, double delta)
    {
        var needed = (long)Math.Round(delta * 100);
        var moves = new Dictionary<long, List<(CcbSample Sample, long Rank)>>();
        foreach (var sample in samples)
        {
            var before = sample.Amount;
            var targetRank = sample.Plan.Range.RankAtOrBelow(before + delta);
            var ranks = CcbClosingNeighborRanks(sample).Concat(new[] { targetRank, targetRank + 1 });
            foreach (var rank in ranks.Distinct().Where(rank => rank >= sample.Lower && rank <= sample.Upper))
            {
                var change = (long)Math.Round((sample.Plan.Range.Value(rank) - before) * 100);
                if (change == 0) continue;
                if (change == needed) { sample.Selected = rank; return true; }
                if (moves.TryGetValue(needed - change, out var complements))
                {
                    foreach (var complement in complements.Where(move => !ReferenceEquals(move.Sample, sample)))
                    {
                        sample.Selected = rank;
                        complement.Sample.Selected = complement.Rank;
                        return true;
                    }
                }
                if (!moves.TryGetValue(change, out var candidates)) moves[change] = candidates = [];
                if (candidates.Count < 2 && candidates.All(move => !ReferenceEquals(move.Sample, sample)))
                    candidates.Add((sample, rank));
            }
        }
        // A coarse move may need a larger counter-move in a finer row. For
        // example, -200 and +150 close a -50 residue when single-row greedy
        // moves are blocked by a lower bound or a prohibited zero precision digit.
        var partners = samples.Where(sample => sample.Lower < sample.Upper)
            .OrderBy(sample => sample.Plan.Range.Bounds.Unit).ToList();
        foreach (var mover in partners.OrderByDescending(sample => sample.Plan.Range.Bounds.Unit))
        {
            foreach (var rank in CcbClosingNeighborRanks(mover))
            {
                if (rank < mover.Lower || rank > mover.Upper) continue;
                var change = RoundMoney(mover.Plan.Range.Value(rank) - mover.Amount);
                foreach (var partner in partners)
                {
                    if (ReferenceEquals(mover, partner)) continue;
                    var desired = RoundMoney(partner.Amount + delta - change);
                    if (desired < partner.Plan.Range.Value(partner.Lower) - 0.009d
                        || desired > partner.Plan.Range.Value(partner.Upper) + 0.009d) continue;
                    var partnerRank = partner.Plan.Range.RankAtOrBelow(desired);
                    if (Math.Abs(partner.Plan.Range.Value(partnerRank) - desired) > 0.009d) continue;
                    mover.Selected = rank;
                    partner.Selected = partnerRank;
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<long> CcbClosingNeighborRanks(CcbSample sample)
    {
        var radius = sample.Plan.Range.Bounds.Precision == 2 ? 12 : 2;
        for (var distance = 1; distance <= radius; distance++)
        {
            if (sample.Selected - distance >= sample.Lower) yield return sample.Selected - distance;
            if (sample.Selected + distance <= sample.Upper) yield return sample.Selected + distance;
        }
    }

    private static List<DateTime> GetCcbDates(FlowRuleBase rule, DateTime start, DateTime end)
    {
        return EnumerateDates(start.Date, end.Date)
            .Where(date => rule is GenerateConstRule || ConstructionBankGenerationPolicy.IsReferenceDateAllowed(date, rule))
            .Where(date => CcbTimeWindow(date, new CcbWindow(start, end), rule) is not null).ToList();
    }

    private static void EnsureCcbDates(FlowRuleBase rule, DateTime start, DateTime end)
    {
        if (GetCcbDates(rule, start, end).Count == 0)
            throw new InvalidOperationException($"建行参照明细第{rule.Index}行在{start:yyyy年MM月}没有符合勾选日期及交易时段的日期，无法满足每月出现次数。");
    }

    private static (DateTime Start, DateTime End)? CcbTimeWindow(DateTime date, CcbWindow window, FlowRuleBase rule)
    {
        var from = Math.Clamp(rule.StartDay ?? 9, 0, 23);
        var to = Math.Clamp(rule.EndDay ?? 17, 0, 23);
        if (to < from) (from, to) = (to, from);
        var start = new[] { date.Date.AddHours(from), window.Start }.Max();
        var end = new[] { date.Date.AddHours(to).AddMinutes(59).AddSeconds(59), window.End }.Min();
        return start <= end ? (start, end) : null;
    }

    private static DateTime PickCcbTime(DateTime date, CcbWindow window, FlowRuleBase rule, Random random, NativeScheduleState state)
    {
        var bounds = CcbTimeWindow(date, window, rule)
            ?? throw new InvalidOperationException($"建行第{rule.Index}行没有符合配置的交易时段。");
        var seconds = (int)(bounds.End - bounds.Start).TotalSeconds;
        var offset = random.Next(seconds + 1);
        for (var i = 0; i <= seconds; i++)
        {
            var time = bounds.Start.AddSeconds((offset + i) % (seconds + 1));
            if (!state.IsTimeUsed(time)) { state.MarkTime(time); return time; }
        }
        throw new InvalidOperationException("建行指定日期的交易时段已满，请扩大时段或减少次数。");
    }

    private static FlowAutoGenerationResult CorrectCcbResult(FlowAutoGenerationRequest request, FlowAutoGenerationResult source, bool repairNegative)
    {
        var start = request.Config.StartTime.Date;
        var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start) (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        var random = new Random(CreateRunSeed(request, start, end, 0xCCB));
        var records = source.Records.Select(row => row.Clone()).ToList();
        var periods = CreateCcbPeriods(request, start, end, random);
        if (request.Config.SelectIndex == 1)
        {
            periods = periods.Select(period =>
            {
                var row = records.FirstOrDefault(row => IsRecordInRange(row, period.Start, period.End)
                    && row.ExtraFields.ContainsKey(CcbPeriodIncome));
                return row is null ? period : period with
                {
                    Income = ParseCcbDouble(row, CcbPeriodIncome, period.Income),
                    Expense = ParseCcbDouble(row, CcbPeriodExpense, period.Expense)
                };
            }).ToList();
        }
        return FinalizeCcbResult(request, records, periods,
            start, end, source.OpeningBalance, random, repairNegative);
    }

    private static FlowAutoGenerationResult FinalizeCcbResult(FlowAutoGenerationRequest request,
        List<FlowRecord> records, List<CcbPeriod> periods, DateTime start, DateTime end,
        double openingBalance, Random random, bool repairNegative)
    {
        for (var pass = 0; pass < 16; pass++)
        {
            RecalculateInterestRecords(records, request, openingBalance, start, end, random);
            foreach (var period in periods)
            foreach (var income in new[] { true, false })
            {
                var rows = records.Where(row => IsRecordInRange(row, period.Start, period.End)
                    && (income ? row.TradeMoney > 0 : row.TradeMoney < 0)).ToList();
                var systemAmount = rows.Where(IsSystemInterestRecord).Sum(row => Math.Abs(row.TradeMoney ?? 0));
                // First close income at its legal precision, then compensate its
                // rounding residue through expense so the requested net is preserved.
                var incomeResidue = income ? 0d : SumIncome(records.Where(row => IsRecordInRange(row, period.Start, period.End))) - period.Income;
                var target = RoundMoney((income ? period.Income : period.Expense + incomeResidue) - systemAmount);
                var samples = new List<CcbSample>();
                foreach (var row in rows.Where(row => !IsSystemInterestRecord(row)))
                {
                    var rule = ResolveRecordSourceRule(request, row)
                        ?? throw new InvalidOperationException("建行修正缺少原始参照标识，不能安全改写流水。");
                    var plan = new CcbRulePlan(rule, []);
                    var min = ParseCcbDouble(row, CcbStratumMin, Math.Abs(row.TradeMoney ?? 0));
                    var max = ParseCcbDouble(row, CcbStratumMax, Math.Abs(row.TradeMoney ?? 0));
                    var sample = new CcbSample(plan, plan.Range.RankAtOrBelow(min), plan.Range.RankAtOrBelow(max),
                        plan.Range.RankAtOrBelow(Math.Abs(row.TradeMoney ?? 0))) { Row = row };
                    samples.Add(sample);
                }
                if (!BalanceCcbSamples(samples, target, random) && !BalanceCcbFlexibleTail(samples, target, random))
                    throw new InvalidOperationException("建行利息/余额修正与完整区间分布及金额精度冲突，请调整预算或增加可用参照。");
                foreach (var sample in samples)
                {
                    ApplySignedAmount(sample.Row!, income ? 1 : -1, sample.Amount);
                    sample.Row!.ExtraFields[CcbStratumMin] = sample.Plan.Range.Value(sample.Lower).ToString("R", CultureInfo.InvariantCulture);
                    sample.Row.ExtraFields[CcbStratumMax] = sample.Plan.Range.Value(sample.Upper).ToString("R", CultureInfo.InvariantCulture);
                }
            }
            ApplyBalances(records, openingBalance);
            if (repairNegative && GetMinimumBalance(records, openingBalance) < -0.009d)
                RepairCcbNegativeTimes(request, records, start, end, openingBalance, random);
            RecalculateInterestRecords(records, request, openingBalance, start, end, random);
            ApplyBalances(records, openingBalance);
            if (CcbBudgetsSettled(records, periods, request)) break;
            if (pass == 15) throw new InvalidOperationException("建行利息与总收支未能收敛到配置金额，请调整配置。");
        }
        ValidateCcbRows(request, records, start, end);
        return CreateWechatNaturalBalanceResult(records, openingBalance, request.Config.LastMoney);
    }

    private static bool CcbBudgetsSettled(List<FlowRecord> records, List<CcbPeriod> periods, FlowAutoGenerationRequest request)
    {
        foreach (var period in periods)
        {
            var periodRows = records.Where(row => IsRecordInRange(row, period.Start, period.End)).ToList();
            var incomeResidue = SumIncome(periodRows) - period.Income;
            foreach (var income in new[] { true, false })
            {
                var rows = periodRows.Where(row => income ? row.TradeMoney > 0 : row.TradeMoney < 0).ToList();
                var samples = rows.Where(row => !IsSystemInterestRecord(row)).Select(row =>
                {
                    var rule = ResolveRecordSourceRule(request, row)!;
                    return new CcbSample(new CcbRulePlan(rule, []), 0, 0, 0);
                }).ToList();
                var systemAmount = rows.Where(IsSystemInterestRecord).Sum(row => Math.Abs(row.TradeMoney ?? 0));
                var target = (income ? period.Income : period.Expense + incomeResidue) - systemAmount;
                var actual = rows.Where(row => !IsSystemInterestRecord(row)).Sum(row => Math.Abs(row.TradeMoney ?? 0));
                if (Math.Abs(actual - NearestCcbBudgetTarget(samples, target)) > 0.009d) return false;
            }
        }
        return true;
    }

    private static double ParseCcbDouble(FlowRecord row, string key, double fallback) =>
        double.TryParse(row.ExtraFields.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static CcbWindow GetCcbRecordWindow(FlowRecord row, DateTime start, DateTime end)
    {
        return new CcbWindow(
            DateTime.TryParse(row.ExtraFields.GetValueOrDefault(CcbWindowStart), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var from) ? from : start,
            DateTime.TryParse(row.ExtraFields.GetValueOrDefault(CcbWindowEnd), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var to) ? to : end,
            bool.TryParse(row.ExtraFields.GetValueOrDefault(CcbFixedDate), out var fixedDate) && fixedDate);
    }

    private sealed record CcbRepairRow(FlowRecord Row, FlowRuleBase Rule, CcbWindow Window,
        IReadOnlyList<DateTime> Dates, int DailyLimit = 0);

    private sealed record CcbRepairMove(CcbRepairRow Item, DateTime Original, DateTime Destination, int DensityPenalty = 0)
    {
        public bool SameMonth => Original.Year == Destination.Year && Original.Month == Destination.Month;
        public double Distance => Math.Abs((Original - Destination).TotalDays);
    }

    private static DateTime OriginalCcbTime(FlowRecord row) =>
        DateTime.TryParse(row.ExtraFields.GetValueOrDefault(CcbScheduledTime), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var time) ? time : row.AccountTime!.Value;

    private static void RepairCcbNegativeTimes(FlowAutoGenerationRequest request, List<FlowRecord> records,
        DateTime start, DateTime end, double openingBalance, Random random)
    {
        var dateCache = new Dictionary<(FlowRuleBase Rule, DateTime Start, DateTime End), List<DateTime>>();
        var movable = new List<CcbRepairRow>();
        foreach (var row in records.Where(row => !IsSystemInterestRecord(row) && row.AccountTime.HasValue))
        {
            var rule = ResolveRecordSourceRule(request, row);
            var window = GetCcbRecordWindow(row, start, end);
            if (rule is null || window.Fixed) continue;
            var key = (rule, window.Start, window.End);
            if (!dateCache.TryGetValue(key, out var dates))
                dateCache[key] = dates = GetCcbDates(rule, window.Start, window.End);
            movable.Add(new CcbRepairRow(row, rule, window, dates));
        }
        movable = movable.GroupBy(item => (item.Rule, item.Window.Start, item.Window.End)).SelectMany(group =>
        {
            var density = (int)Math.Ceiling((double)group.Count() / Math.Max(1, group.First().Dates.Count));
            var originalPeak = group.GroupBy(item => OriginalCcbTime(item.Row).Date).Max(day => day.Count());
            return group.Select(item => item with { DailyLimit = Math.Max(density, originalPeak) + 1 });
        }).ToList();
        var directionLimits = new Dictionary<bool, int>();
        foreach (var income in new[] { true, false })
        {
            var rows = records.Where(row => !IsSystemInterestRecord(row) && row.AccountTime.HasValue
                && (income ? row.TradeMoney > 0 : row.TradeMoney < 0)).ToList();
            var days = movable.Where(item => (item.Row.TradeMoney > 0) == income)
                .SelectMany(item => item.Dates).Distinct().Count();
            var density = (int)Math.Ceiling((double)rows.Count / Math.Max(1, days));
            var originalPeak = rows.GroupBy(row => OriginalCcbTime(row).Date).Select(day => day.Count()).DefaultIfEmpty(0).Max();
            directionLimits[income] = Math.Max(density, originalPeak) + 1;
        }
        var state = NativeScheduleState.From(records);
        var sourceCounts = CcbSourceDayCounts(movable);
        var originalMonthCounts = movable.GroupBy(item => (item.Rule, OriginalCcbTime(item.Row).Year, OriginalCcbTime(item.Row).Month))
            .ToDictionary(group => group.Key, group => group.Count());
        var sourceMonthCounts = CcbSourceMonthCounts(movable);
        for (var pass = 0; pass < Math.Min(4000, records.Count * 2); pass++)
        {
            ApplyBalances(records, openingBalance);
            var negative = records.FirstOrDefault(row => row.Balance < -0.009d);
            if (negative?.AccountTime is not DateTime cutoff) return;
            var deficit = -(negative.Balance ?? 0);
            var funding = records.FirstOrDefault(row => row.AccountTime > cutoff && row.TradeMoney > 0
                && !IsSystemInterestRecord(row))?.AccountTime;
            CcbRepairMove? move = null;
            var exchanged = false;
            for (var expenseOverflow = 0; expenseOverflow <= 1; expenseOverflow++)
            {
                var moves = new List<CcbRepairMove>();
                foreach (var item in movable)
                {
                    var original = item.Row.AccountTime!.Value;
                    var income = item.Row.TradeMoney > 0;
                    if (expenseOverflow > 0 && income || (income ? original <= cutoff : original > cutoff)) continue;
                    var from = income ? item.Window.Start : new[] { cutoff.AddSeconds(1), funding?.AddSeconds(1) ?? cutoff.AddSeconds(1), item.Window.Start }.Max();
                    var to = income ? new[] { cutoff.AddSeconds(-1), item.Window.End }.Min() : item.Window.End;
                    if (from > to) continue;
                    // Income concentration remains constrained. Expense density is
                    // a preference: it cannot make a legal cash-flow repair fail.
                    foreach (var date in income ? item.Dates.Reverse() : item.Dates)
                    {
                        if (date < from.Date || date > to.Date) continue;
                        var penalty = date == original.Date ? 0 : Math.Max(0, sourceCounts.GetValueOrDefault((item.Rule, date)) + 1 - item.DailyLimit)
                            + Math.Max(0, state.GetSameDirectionCount(date, income) + 1 - directionLimits[income]);
                        if (expenseOverflow == 0 && penalty > 0) continue;
                        if (income && (date.Year != original.Year || date.Month != original.Month)
                            && sourceMonthCounts.GetValueOrDefault((item.Rule, date.Year, date.Month))
                                >= originalMonthCounts.GetValueOrDefault((item.Rule, date.Year, date.Month)) + 1) continue;
                        if (!TryCcbRepairTime(date, from, to, item.Rule, income, state, random, out var time)) continue;
                        moves.Add(new CcbRepairMove(item, original, time, penalty));
                        if (expenseOverflow == 0) break;
                    }
                }
                move = moves.OrderBy(candidate => !candidate.SameMonth).ThenBy(candidate => candidate.DensityPenalty)
                    .ThenBy(candidate => candidate.Distance)
                    .ThenBy(candidate => Math.Abs(candidate.Item.Row.TradeMoney ?? 0) + 0.009d < deficit)
                    .ThenBy(candidate => Math.Abs(Math.Abs(candidate.Item.Row.TradeMoney ?? 0) - deficit)).FirstOrDefault();
                if ((move is null || !move.SameMonth) && TryCcbRepairExchange(movable, cutoff, deficit, move))
                {
                    // Same-source exchanges preserve all occupied slots and counts.
                    exchanged = true;
                    break;
                }
                if (move is not null) break;
            }
            if (exchanged) continue;
            if (move is null) break;
            // Advancing income or delaying expense only improves intervening
            // balances. No amount, occurrence count, or configured window changes.
            move.Item.Row.AccountTime = move.Destination;
            state.Move(move.Item.Row, move.Original);
            var oldKey = (move.Item.Rule, move.Original.Date);
            sourceCounts[oldKey] = sourceCounts.GetValueOrDefault(oldKey) - 1;
            var newKey = (move.Item.Rule, move.Destination.Date);
            sourceCounts[newKey] = sourceCounts.GetValueOrDefault(newKey) + 1;
            var oldMonth = (move.Item.Rule, move.Original.Year, move.Original.Month);
            var newMonth = (move.Item.Rule, move.Destination.Year, move.Destination.Month);
            sourceMonthCounts[oldMonth] = sourceMonthCounts.GetValueOrDefault(oldMonth) - 1;
            sourceMonthCounts[newMonth] = sourceMonthCounts.GetValueOrDefault(newMonth) + 1;
        }
        ApplyBalances(records, openingBalance);
    }

    private static Dictionary<(FlowRuleBase Rule, DateTime Date), int> CcbSourceDayCounts(List<CcbRepairRow> rows) =>
        rows.GroupBy(item => (item.Rule, item.Row.AccountTime!.Value.Date)).ToDictionary(group => group.Key, group => group.Count());

    private static Dictionary<(FlowRuleBase Rule, int Year, int Month), int> CcbSourceMonthCounts(List<CcbRepairRow> rows) =>
        rows.GroupBy(item => (item.Rule, item.Row.AccountTime!.Value.Year, item.Row.AccountTime.Value.Month))
            .ToDictionary(group => group.Key, group => group.Count());

    private static bool TryCcbRepairTime(DateTime date, DateTime from, DateTime to, FlowRuleBase rule,
        bool income, NativeScheduleState state, Random random, out DateTime time)
    {
        time = default;
        if (CcbTimeWindow(date, new CcbWindow(from, to), rule) is not { } bounds) return false;
        var seconds = (int)(bounds.End - bounds.Start).TotalSeconds;
        var offset = random.Next(Math.Min(900, seconds) + 1);
        for (var i = 0; i <= seconds; i++)
        {
            var gap = (offset + i) % (seconds + 1);
            var candidate = income ? bounds.End.AddSeconds(-gap) : bounds.Start.AddSeconds(gap);
            if (state.IsTimeUsed(candidate)) continue;
            time = candidate;
            return true;
        }
        return false;
    }

    private static bool TryCcbRepairExchange(List<CcbRepairRow> rows, DateTime cutoff, double deficit, CcbRepairMove? alternative)
    {
        (CcbRepairRow Early, CcbRepairRow Late, bool CrossMonth, double Distance, double Gain)? best = null;
        foreach (var group in rows.GroupBy(item => (item.Rule, item.Window.Start, item.Window.End)))
        foreach (var early in group.Where(item => item.Row.AccountTime <= cutoff))
        foreach (var late in group.Where(item => item.Row.AccountTime > cutoff))
        {
            var first = early.Row.AccountTime!.Value;
            var last = late.Row.AccountTime!.Value;
            var income = early.Row.TradeMoney > 0;
            var gain = income ? CcbRepairAmount(late.Row) - CcbRepairAmount(early.Row) : CcbRepairAmount(early.Row) - CcbRepairAmount(late.Row);
            if (gain <= 0.009d || last < early.Window.Start || last > early.Window.End
                || first < late.Window.Start || first > late.Window.End) continue;
            var crossMonth = first.Year != last.Year || first.Month != last.Month;
            var distance = (last - first).TotalDays;
            if (alternative is not null && (crossMonth && alternative.SameMonth
                || crossMonth == !alternative.SameMonth && distance > alternative.Distance)) continue;
            // Do not replace a useful direct move with thousands of tiny swaps.
            // A preferred exchange must fund at least as much of this gap as the
            // direct move; when no direct move exists, take the largest useful gain.
            if (alternative is not null && gain + 0.009d < Math.Min(deficit, CcbRepairAmount(alternative.Item.Row))) continue;
            var partial = gain + 0.009d < deficit;
            if (best is { } current && (crossMonth, partial, partial ? -gain : 0d, distance, Math.Abs(gain - deficit))
                .CompareTo((current.CrossMonth, current.Gain + 0.009d < deficit,
                    current.Gain + 0.009d < deficit ? -current.Gain : 0d, current.Distance, Math.Abs(current.Gain - deficit))) >= 0) continue;
            best = (early, late, crossMonth, distance, gain);
        }
        if (best is not { } selected) return false;
        (selected.Early.Row.AccountTime, selected.Late.Row.AccountTime) = (selected.Late.Row.AccountTime, selected.Early.Row.AccountTime);
        return true;
    }

    private static double CcbRepairAmount(FlowRecord row) => Math.Abs(row.TradeMoney ?? 0);

    private static void ValidateCcbRows(FlowAutoGenerationRequest request, List<FlowRecord> records, DateTime start, DateTime end)
    {
        foreach (var row in records.Where(row => !IsSystemInterestRecord(row)))
        {
            var rule = ResolveRecordSourceRule(request, row)
                ?? throw new InvalidOperationException("建行生成流水缺少原始参照标识。");
            var time = row.AccountTime ?? throw new InvalidOperationException("建行流水缺少交易时间。");
            var window = GetCcbRecordWindow(row, start, end);
            var amount = Math.Abs(row.TradeMoney ?? 0);
            var bounds = GetRuleAmountBounds(rule);
            if (time < start || time > end || time < window.Start || time > window.End
                || CcbTimeWindow(time.Date, window, rule) is not { } allowed || time < allowed.Start || time > allowed.End
                || rule is GenerateReferenceRule && !ConstructionBankGenerationPolicy.IsReferenceDateAllowed(time, rule)
                || amount < bounds.Min - 0.009d || amount > bounds.Max + 0.009d
                || Math.Abs(amount / bounds.Unit - Math.Round(amount / bounds.Unit)) > 0.00001d
                || !HasRequiredNonZeroPrecisionDigit(amount, bounds))
                throw new InvalidOperationException($"建行第{rule.Index}行生成结果违反原始日期、时段或金额精度限制。");
        }
    }
}
