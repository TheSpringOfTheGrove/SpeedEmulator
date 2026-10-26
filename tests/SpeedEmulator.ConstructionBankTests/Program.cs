using System.Globalization;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

if (args.Contains("--saved-config", StringComparer.Ordinal))
{
    TestSavedConfiguration();
    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("个人建行专用入口及四种参照日期组合", TestScopeAndCalendar),
    ("截图同结构20条参照：全区间、独立分布、日期、固定工资及预算", TestReportedRules),
    ("整数收入与整百转账遇到小数预算，含结息及修正仍可生成", TestMixedPrecisionReported),
    ("按月范围及按月明细也处理整数收入的小数预算", TestMixedPrecisionMonths),
    ("固定小数项目参与尾差基准，不破坏其金额", TestFixedFractionOffset),
    ("采样分层不阻止原始范围内可行的必现预算", TestFlexibleTail),
    ("离散金额完整一轮先覆盖各合法值再重复", TestDiscreteUniformity),
    ("两条同名扫码参照保持各自范围及小于10元金额", TestOverlappingRules),
    ("固定日期项目忽略两个开关且每月次数不变", TestFixedDates),
    ("五种固定日期模式在按月生成时次数和日期正确", TestFixedModes),
    ("没有合法日期的每月必现项目明确报错", TestUnavailableDates),
    ("按月明细的金额、日期和次数不被重分配", TestMonthlyDetails),
    ("按月收支范围执行用户设置且修正后每月金额不漂移", TestMonthlyRanges),
    ("负余额修正只改合法时间并保留金额条数", TestNegativeCorrection),
    ("大额总进账的负余额修正不把收入堆到首日", TestIncomeDateConcentration),
    ("日期密度已满时同参照时间对调修正且不改金额", TestRepairExchange),
    ("利息重算后收支和末余额仍满足预算", TestInterest),
    ("固定次数与均匀金额预算不兼容时明确报错", TestImpossibleBudget),
    ("相同种子可复现、不同种子有变化且不改配置", TestReproducibility)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} CCB tests passed.");
return failed == 0 ? 0 : 1;

static Bank Bank() => new() { Id = 8, Name = "建行", Type = BankTypes.Personal };
static GenerateReferenceRule Rule(int index, double min, double max, int precision, bool income = false) => new()
{
    Index = index, BankId = 8, IsCheck = true, IncomeAttribute = income ? "收入" : "支出",
    MinMoney = min, MaxMoney = max, FloutLength = precision, StartDay = 9, EndDay = 20,
    TradeWeekend = true, TradeHoliday = true, ProductBrief = "消费", TradePlace = "商户" + index
};
static FlowGenerationConfig Config(double income, double last = 1000) => new()
{
    StartTime = new DateTime(2024, 9, 20), EndTime = new DateTime(2026, 9, 23),
    OpeningBalance = 3518.25, AllInMoney = income, LastMoney = last
};
static FlowAutoGenerationRequest Request(FlowGenerationConfig config, IEnumerable<GenerateReferenceRule> rules,
    IEnumerable<GenerateConstRule>? constants = null, bool interest = false) => new()
{
    Bank = Bank(), BankUser = new BankUser { Id = 88, BankId = 8, BankName = "建行", AutoCalculateInterest = interest },
    Config = config, References = rules.ToArray(), ConstItems = constants?.ToArray() ?? [],
    InterestSetting = BankInterestSettingDefaults.CreateDefault(Bank())
};
static FlowAutoGenerationResult Generate(FlowAutoGenerationRequest request, int seed = 1301)
{
    var previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    try
    {
        Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString());
        return new FlowAutoGenerator().Generate(request);
    }
    finally { Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous); }
}
static string Kind(FlowRecord row) => row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind") ?? "";
static int Index(FlowRecord row) => int.Parse(row.ExtraFields["__GeneratedSourceIndex"], CultureInfo.InvariantCulture);
static List<FlowRecord> Source(FlowAutoGenerationResult result, int index, string kind = "Reference") =>
    result.Records.Where(row => Kind(row) == kind && Index(row) == index).ToList();
static double Amount(FlowRecord row) => Math.Abs(row.TradeMoney ?? 0);
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Near(double expected, double actual, double tolerance = 0.01) =>
    Check(Math.Abs(expected - actual) <= tolerance, $"expected={expected:F2}, actual={actual:F2}");
static void Throws(Action action, string text)
{
    try { action(); } catch (InvalidOperationException exception) when (exception.Message.Contains(text)) { return; }
    throw new Exception("应明确报告: " + text);
}

static void Validate(FlowAutoGenerationRequest request, FlowAutoGenerationResult result,
    double incomeTolerance = 0.01, double expenseTolerance = 0.01)
{
    Near(request.Config.AllInMoney, result.IncomeTotal, incomeTolerance);
    Near(request.Config.AllInMoney - request.Config.LastMoney, result.ExpenseTotal, expenseTolerance);
    Near(result.OpeningBalance + request.Config.LastMoney, result.FinalBalance);
    foreach (var row in result.Records.Where(row => !FlowGeneratedRowKinds.IsSystemInterest(row)))
    {
        var rule = Kind(row) == "Const" ? (FlowRuleBase)request.ConstItems.Single(rule => rule.Index == Index(row))
            : request.References.Single(rule => rule.Index == Index(row));
        var time = row.AccountTime!.Value;
        Check(time.Date >= request.Config.StartTime.Date && time.Date <= request.Config.EndTime.Date, "日期越界");
        Check(time.Hour >= rule.StartDay && time.Hour <= rule.EndDay, "时段越界");
        if (rule is GenerateReferenceRule)
            Check(ConstructionBankGenerationPolicy.IsReferenceDateAllowed(time, rule), "参照落在未勾选日期");
        Check(Amount(row) >= rule.MinMoney - 0.009 && Amount(row) <= rule.MaxMoney + 0.009, "金额越界");
        var precision = rule.FloutLength ?? 0;
        var unit = precision < 0 ? Math.Pow(10, -precision) : Math.Pow(10, -precision);
        Near(Math.Round(Amount(row) / unit), Amount(row) / unit, 0.00001);
        if (precision != 0) Check((long)Math.Round(Amount(row) / unit) % 10 != 0, "既有指定位非零要求改变");
    }
}

static void TestScopeAndCalendar()
{
    Check(ConstructionBankGenerationPolicy.AppliesTo(Bank()), "个人建行应启用");
    var catalogCount = 0;
    var matchedCount = 0;
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    {
        var bankIds = (IReadOnlyDictionary<string, long>)typeof(SpeedEmulator.ViewModels.MainViewModel)
            .GetField(field, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var pair in bankIds)
        {
            var bank = new Bank { Id = pair.Value, Name = pair.Key,
                Type = field.Contains("Personal", StringComparison.Ordinal) ? BankTypes.Personal : BankTypes.Corporate };
            var expected = bank.Type == BankTypes.Personal && bank.Name == "建行";
            Check(ConstructionBankGenerationPolicy.AppliesTo(bank) == expected, "全行目录入口误命中: " + bank.Name);
            catalogCount++;
            if (ConstructionBankGenerationPolicy.AppliesTo(bank)) matchedCount++;
        }
    }
    Check(matchedCount == 1, "全行目录只能命中个人建行");
    Console.WriteLine($"  isolation: {catalogCount} bank categories, {matchedCount} personal CCB match, {catalogCount - matchedCount} bypasses");
    foreach (var name in new[] { "微信", "支付宝", "农行", "招行", "交行", "中行", "建行对公", "个人农商" })
        Check(!ConstructionBankGenerationPolicy.AppliesTo(new Bank { Id = 8, Name = name, Type = BankTypes.Personal }), "不能按ID误命中: " + name);
    Check(!ConstructionBankGenerationPolicy.AppliesTo(new Bank { Name = "建行", Type = BankTypes.Corporate }), "建行对公不启用");
    var weekday = new DateTime(2025, 10, 9);
    var holiday = new DateTime(2025, 10, 2);
    var weekend = new DateTime(2025, 10, 18);
    foreach (var flags in new[] { (false, false), (true, false), (false, true), (true, true) })
    {
        var rule = Rule(1, 5, 50, 1); rule.TradeWeekend = flags.Item1; rule.TradeHoliday = flags.Item2;
        Check(ConstructionBankGenerationPolicy.IsReferenceDateAllowed(weekday, rule) == (!flags.Item1 && !flags.Item2), "普通工作日组合");
        Check(ConstructionBankGenerationPolicy.IsReferenceDateAllowed(holiday, rule) == flags.Item2, "节假日组合");
        Check(ConstructionBankGenerationPolicy.IsReferenceDateAllowed(weekend, rule) == flags.Item1, "周末组合");
    }
    Check(ConstructionBankGenerationPolicy.IsHoliday(new DateTime(2024, 2, 12)), "2024春节");
    Check(ConstructionBankGenerationPolicy.IsHoliday(new DateTime(2025, 10, 8)), "2025国庆中秋");
    Check(ConstructionBankGenerationPolicy.IsHoliday(new DateTime(2026, 2, 23)), "2026春节");
    Throws(() => ConstructionBankGenerationPolicy.IsHoliday(new DateTime(2030, 1, 1)), "尚未收录");
}

static FlowAutoGenerationRequest Reported(bool interest = false)
{
    var rules = new List<GenerateReferenceRule> { Rule(1, 5, 300, 2), Rule(2, 5, 300, 2), Rule(3, 50, 300, 2) };
    rules[2].PercentMonth = 1;
    for (var i = 4; i <= 14; i++)
    {
        var rule = Rule(i, 5000, 10000, i % 2 == 0 ? -2 : -3);
        rule.TradeHoliday = false; rule.EndDay = 18; rules.Add(rule);
    }
    rules.AddRange([Rule(15, 50, 1000, -1), Rule(16, 500, 8000, -2), Rule(17, 10, 200, -1),
        Rule(18, 5, 50, 1), Rule(19, 10, 300, 1), Rule(20, 5, 300, 2)]);
    var salary = new GenerateConstRule
    {
        Index = 1, IsCheck = true, IncomeAttribute = "收入", MinMoney = 8300, MaxMoney = 8700,
        FloutLength = 2, FixDay = "15", StartDay = 15, EndDay = 17,
        TradeHoliday = true, TradeWeekend = true, ProductBrief = "工资"
    };
    return Request(Config(203850.36, 5000), rules, [salary], interest);
}

static void TestReportedRules()
{
    var below10 = 0;
    var runs = Environment.GetEnvironmentVariable("SPEEDEMULATOR_CCB_STRESS") == "1" ? 100 : 5;
    for (var seed = 1301; seed < 1301 + runs; seed++)
    {
        var request = Reported(); var result = Generate(request, seed); Validate(request, result);
        Check(result.MinimumBalance >= -0.009, $"固定工资配置应完成负余额修正: seed={seed}, minimum={result.MinimumBalance:F2}");
        Check(Source(result, 1, "Const").Count == 24 && Source(result, 1, "Const").All(row => row.AccountTime!.Value.Day == 15), "24笔工资日期");
        Check(Source(result, 3).Count == 25, "生活缴费每月一笔");
        foreach (var rule in request.References)
            Check(Source(result, rule.Index).Count > 0, "漏掉勾选参照: " + rule.Index);
        foreach (var index in new[] { 1, 2, 20 })
        {
            var rows = Source(result, index);
            Check(rows.Any(row => Amount(row) > 250), "5~300不能压成30以下");
            Check(rows.Any(row => Amount(row) < 50), "下段覆盖");
            below10 += rows.Count(row => Amount(row) < 10);
            var histogram = new int[10];
            foreach (var row in rows) histogram[Math.Clamp((int)((Amount(row) - 5) / 29.5), 0, 9)]++;
            Check(histogram.Max() - histogram.Min() <= 2, "5~300等宽区间笔数不均衡");
        }
        Check(Source(result, 18).Any(row => Amount(row) < 10) && Source(result, 18).Any(row => Amount(row) > 40), "5~50完整上下段");
        var red = Source(result, 17);
        Check(red.Select(Amount).Distinct().Count() >= 15, "红包不能只剩10/20");
        var counts = red.GroupBy(Amount).Select(group => group.Count()).ToArray();
        Check(counts.Max() - counts.Min() <= 1, "红包合法值应均衡轮换");
        if (seed < 1306)
            Console.WriteLine($"  seed={seed} rows={result.Records.Count} red={red.Count}/{red.Select(Amount).Distinct().Count()} values below10={result.Records.Count(row => row.TradeMoney < 0 && Amount(row) < 10)} minBalance={result.MinimumBalance:F2}");
    }
    Check(below10 > 0, "5~300全局小于10元区间不能消失");
    Console.WriteLine($"  fixed salary: {runs} seeds succeeded");
}

static void TestDiscreteUniformity()
{
    var request = Request(Config(19000, 0), [Rule(1, 10, 200, -1), Rule(2, 100, 300, 2, true)]);
    var result = Generate(request); Validate(request, result);
    var rows = Source(result, 1);
    Check(rows.Select(Amount).Distinct().Count() == 18, "18个符合现有精度规则的红包金额均需出现");
    var counts = rows.GroupBy(Amount).Select(group => group.Count()).ToArray();
    Check(counts.Max() - counts.Min() <= 1, "离散合法值次数差不能超过1");
}

static FlowAutoGenerationRequest MixedPrecisionReported(bool interest)
{
    var options = new (double Min, double Max, int Precision, int Monthly, int Start, int End)[]
    {
        (5, 80, 0, 3, 9, 22), (2, 50, 0, 8, 8, 22), (200, 1000, 0, 1, 8, 22),
        (100, 200, 1, 2, 9, 22), (500, 3000, 0, 1, 9, 22), (2, 60, 0, 2, 9, 20),
        (2, 80, 0, 3, 9, 20), (150, 800, 2, 1, 9, 22), (2, 80, 0, 2, 9, 22),
        (100, 200, 1, 1, 19, 21), (20, 200, 0, 2, 9, 20), (5, 60, 0, 2, 9, 22),
        (2, 20, 0, 2, 9, 22), (8, 100, 2, 1, 9, 20), (5, 40, 2, 1, 9, 22),
        (2, 60, 0, 1, 9, 22), (5, 60, 0, 1, 8, 22), (12, 20, 0, 1, 7, 8),
        (15, 80, 0, 1, 9, 20), (3000, 30000, -2, 0, 9, 17), (3000, 30000, -2, 0, 9, 17)
    };
    var rules = options.Select((option, i) =>
    {
        var rule = Rule(i + 1, option.Min, option.Max, option.Precision, i is 4 or 20);
        rule.PercentMonth = option.Monthly; rule.StartDay = option.Start; rule.EndDay = option.End;
        return rule;
    }).ToArray();
    return Request(Config(203850.36, 5000), rules, interest: interest);
}

static void TestMixedPrecisionReported()
{
    var runs = Environment.GetEnvironmentVariable("SPEEDEMULATOR_CCB_STRESS") == "1" ? 100 : 5;
    foreach (var interest in new[] { false, true })
    for (var i = 0; i < runs; i++)
    {
        var request = MixedPrecisionReported(interest); var result = Generate(request, 41026 + i);
        Validate(request, result, 0.51, 0.51);
        foreach (var rule in request.References.Where(rule => rule.PercentMonth > 0))
        foreach (var month in result.Records.Where(row => row.AccountTime.HasValue).Select(row => row.AccountTime!.Value.ToString("yyyy-MM")).Distinct())
            Check(Source(result, rule.Index).Count(row => row.AccountTime!.Value.ToString("yyyy-MM") == month) == rule.PercentMonth,
                "新截图每月必现次数不得变动");
        Check(result.MinimumBalance >= -0.009, "新截图不能产生负余额");
        if (i == 0)
        {
            Console.WriteLine($"  mixed interest={interest} rows={result.Records.Count} income={result.IncomeTotal:F2} expense={result.ExpenseTotal:F2} final={result.FinalBalance:F2}");
            Validate(request, new FlowAutoGenerator().ApplyFinalBalanceCorrection(request, result), 0.51, 0.51);
        }
    }
    Console.WriteLine($"  mixed precision: {runs * 2} runs succeeded (interest on/off)");
}

static void TestMixedPrecisionMonths()
{
    foreach (var mode in new[] { 1, 2 })
    {
        var request = MixedPrecisionReported(false); var config = request.Config;
        config.SelectIndex = mode;
        config.MinInMoneyMonth2 = config.MinOutMoneyMonth2 = 5000;
        config.MaxInMoneyMonth2 = config.MaxOutMoneyMonth2 = 10000;
        if (mode == 2)
        {
            for (var i = 0; i < 25; i++)
            {
                var month = new DateTime(2024, 9, 1).AddMonths(i);
                config.MonthGenData.Add(new MonthGenerateRule
                {
                    StartTime = month, EndTime = month.AddMonths(1).AddDays(-1),
                    InMoney = i == 24 ? 8154.12 : 8154.01,
                    OutMoney = i == 24 ? 7954.12 : 7954.01
                });
            }
        }
        var result = Generate(request); Validate(request, result, 0.51, 0.51);
        Validate(request, new FlowAutoGenerator().ApplyFinalBalanceCorrection(request, result), 0.51, 0.51);
    }
}

static void TestFixedFractionOffset()
{
    var config = Config(1015.36, 500);
    config.StartTime = new DateTime(2025, 10, 1); config.EndTime = new DateTime(2025, 10, 31);
    var fixedRow = new GenerateConstRule
    {
        Index = 1, IsCheck = true, IncomeAttribute = "收入", MinMoney = 15.15, MaxMoney = 15.15,
        FloutLength = 2, FixDay = "15", StartDay = 9, EndDay = 17
    };
    var request = Request(config, [Rule(1, 100, 200, 0, true), Rule(2, 5, 50, 2)], [fixedRow]);
    var result = Generate(request); Validate(request, result, 0.51, 0.51);
    Near(1015.15, result.IncomeTotal);
    Near(15.15, Amount(Source(result, 1, "Const").Single()));
}

static void TestFlexibleTail()
{
    var config = Config(1000, 968);
    config.StartTime = new DateTime(2025, 10, 1); config.EndTime = new DateTime(2025, 10, 31);
    var expense = Rule(1, 5, 10, 0); expense.PercentMonth = 5;
    var request = Request(config, [expense, Rule(2, 100, 300, 2, true)]);
    var result = Generate(request); Validate(request, result);
    Check(Source(result, 1).Count == 5, "不能增删必现笔数解决采样约束");
    Near(32, Source(result, 1).Sum(Amount));
    Validate(request, new FlowAutoGenerator().ApplyFinalBalanceCorrection(request, result));
}

static void TestOverlappingRules()
{
    var a = Rule(1, 5, 50, 1); var b = Rule(2, 5, 300, 2);
    a.TradePlace = b.TradePlace = "扫码付款"; a.OppositeAccount = b.OppositeAccount = "1000107101";
    var request = Request(Config(120000, 1000), [a, b, Rule(3, 1000, 2000, 2, true)]);
    var result = Generate(request); Validate(request, result);
    foreach (var index in new[] { 1, 2 }) Check(Source(result, index).Any(row => Amount(row) < 10), "同名参照独立低段");
    Check(Source(result, 1).All(row => Amount(row) <= 50), "小区间不能借用大区间金额");
    Check(Source(result, 2).Any(row => Amount(row) > 250), "大区间高段必须存在");
}

static void TestFixedDates()
{
    var config = Config(1000, 1000); config.StartTime = config.EndTime = new DateTime(2025, 10, 9);
    var fixedRow = new GenerateConstRule { Index = 1, IsCheck = true, IncomeAttribute = "收入", MinMoney = 1000,
        MaxMoney = 1000, FloutLength = 0, FixDay = "9", StartDay = 15, EndDay = 17, TradeHoliday = true, TradeWeekend = true };
    var request = Request(config, [], [fixedRow]);
    var result = Generate(request); Validate(request, result);
    Check(result.Records.Single().AccountTime!.Value.Date == new DateTime(2025, 10, 9), "固定日期项目不能被周末/节假日限制挪日");
}

static void TestUnavailableDates()
{
    var config = Config(100, 0); config.StartTime = config.EndTime = new DateTime(2025, 10, 9);
    var rule = Rule(1, 5, 50, 1); rule.PercentMonth = 1;
    Throws(() => Generate(Request(config, [rule])), "没有符合");
}

static void TestFixedModes()
{
    var config = Config(144, 144); config.StartTime = new DateTime(2025, 1, 1); config.EndTime = new DateTime(2025, 2, 28);
    config.SelectIndex = 2;
    config.MonthGenData.Add(new MonthGenerateRule { StartTime = config.StartTime, EndTime = new DateTime(2025, 1, 31), InMoney = 75, OutMoney = 0 });
    config.MonthGenData.Add(new MonthGenerateRule { StartTime = new DateTime(2025, 2, 1), EndTime = config.EndTime, InMoney = 69, OutMoney = 0 });
    var rules = new[] { ("+", "59", 59), ("*", "3", 6), ("=", "1", 59), ("5-8", "4", 8), ("15", "6", 12) };
    var constants = rules.Select((item, i) => new GenerateConstRule
    {
        Index = i + 1, IsCheck = true, IncomeAttribute = "收入", MinMoney = 1, MaxMoney = 1,
        FloutLength = 0, FixDay = item.Item1, ReCnt = item.Item2, StartDay = 9, EndDay = 10,
        TradeWeekend = true, TradeHoliday = true
    }).ToArray();
    var request = Request(config, [], constants);
    var result = Generate(request); Validate(request, result);
    for (var i = 0; i < rules.Length; i++) Check(Source(result, i + 1, "Const").Count == rules[i].Item3, "固定模式次数改变: " + rules[i].Item1);
    Check(Source(result, 4, "Const").All(row => row.AccountTime!.Value.Day is >= 5 and <= 8), "日期区间模式越界");
    Check(Source(result, 5, "Const").All(row => row.AccountTime!.Value.Day == 15), "固定15日越界");
}

static void TestMonthlyDetails()
{
    var config = Config(4000, 1000); config.SelectIndex = 2;
    config.StartTime = new DateTime(2025, 1, 1); config.EndTime = new DateTime(2025, 2, 28);
    config.MonthGenData.Add(new MonthGenerateRule { StartTime = config.StartTime, EndTime = new DateTime(2025, 1, 31), InMoney = 2000, OutMoney = 1000 });
    config.MonthGenData.Add(new MonthGenerateRule { StartTime = new DateTime(2025, 2, 1), EndTime = config.EndTime, InMoney = 2000, OutMoney = 2000 });
    var request = Request(config, [Rule(1, 5, 50, 1), Rule(2, 100, 300, 2, true)]);
    var result = Generate(request); Validate(request, result);
    Near(2000, result.Records.Where(row => row.AccountTime!.Value.Month == 1 && row.TradeMoney > 0).Sum(row => row.TradeMoney ?? 0));
    Near(1000, -result.Records.Where(row => row.AccountTime!.Value.Month == 1 && row.TradeMoney < 0).Sum(row => row.TradeMoney ?? 0));
    Near(2000, -result.Records.Where(row => row.AccountTime!.Value.Month == 2 && row.TradeMoney < 0).Sum(row => row.TradeMoney ?? 0));
}

static void TestNegativeCorrection()
{
    var request = Request(Config(10000, 1000), [Rule(1, 5, 50, 1), Rule(2, 200, 300, 2, true)]);
    var original = Generate(request);
    foreach (var row in original.Records) row.AccountTime = row.TradeMoney < 0 ? new DateTime(2025, 1, 4, 9, 0, 0) : new DateTime(2025, 10, 18, 9, 0, 0);
    var amounts = original.Records.Select(row => row.TradeMoney).ToArray();
    var corrected = new FlowAutoGenerator().ApplyNegativeBalanceCorrection(request, original);
    Validate(request, corrected);
    Check(corrected.MinimumBalance >= -0.009, "应在合法日期内修正负余额");
    Check(corrected.Records.Count == original.Records.Count, "不能靠增删条数修正");
    Check(corrected.Records.Select(Amount).Order().SequenceEqual(original.Records.Select(Amount).Order()), "修正不能破坏金额分布");
    Check(amounts.SequenceEqual(original.Records.Select(row => row.TradeMoney)), "不修改源结果");
}

static void TestIncomeDateConcentration()
{
    var runs = Environment.GetEnvironmentVariable("SPEEDEMULATOR_CCB_STRESS") == "1" ? 100 : 5;
    for (var seed = 41026; seed < 41026 + runs; seed++)
    {
        var request = MixedPrecisionReported(true); request.Config.AllInMoney = 2003850.36;
        var result = Generate(request, seed); Validate(request, result, 0.51, 0.51);
        Check(result.MinimumBalance >= -0.009, "大额配置仍须消除负余额");
        var income = result.Records.Where(row => row.TradeMoney > 0 && !FlowGeneratedRowKinds.IsSystemInterest(row)).ToList();
        var dayCounts = income.GroupBy(row => row.AccountTime!.Value.Date).Select(group => group.Count()).ToArray();
        Check(dayCounts.Max() <= 2, $"同日普通收入不应由修正堆积: seed={seed}, max={dayCounts.Max()}");
        var months = income.GroupBy(row => row.AccountTime!.Value.ToString("yyyy-MM")).ToList();
        Check(months.Count >= 22, "收入仍应覆盖大部分配置月份");
        Check(months.Max(group => group.Sum(Amount)) / result.IncomeTotal < 0.15,
            $"不能把多个月的收入提前到一个月: seed={seed}, share={months.Max(group => group.Sum(Amount)) / result.IncomeTotal:P1}");
        foreach (var rule in request.References.Where(rule => rule.PercentMonth > 0))
        for (var i = 0; i < 25; i++)
        {
            var month = new DateTime(2024, 9, 1).AddMonths(i);
            Check(Source(result, rule.Index).Count(row => row.AccountTime!.Value.Year == month.Year
                && row.AccountTime.Value.Month == month.Month) == rule.PercentMonth, "集中度修正不能破坏月次数");
        }
        if (seed == 41026)
        {
            var firstDate = result.Records.Min(row => row.AccountTime!.Value.Date);
            var firstIncome = income.Where(row => row.AccountTime!.Value.Date == firstDate).ToList();
            var largestAdvance = income.Max(row => (DateTime.Parse(row.ExtraFields["__CcbScheduledTime"], CultureInfo.InvariantCulture) - row.AccountTime!.Value).TotalDays);
            Console.WriteLine($"  concentration rows={result.Records.Count} maxIncomePerDay={dayCounts.Max()} firstDayIncomeCount={firstIncome.Count} firstDayIncome={firstIncome.Sum(Amount):F2} maxIncomeAdvanceDays={largestAdvance:F1} minBalance={result.MinimumBalance:F2}");
            var before = result.Records.Select(row => row.Clone()).ToArray();
            var corrected = new FlowAutoGenerator().ApplyNegativeBalanceCorrection(request, result);
            Validate(request, corrected, 0.51, 0.51);
            Check(corrected.Records.Count == before.Length, "二次修正不能增删条数");
            Check(before.Select(Amount).Order().SequenceEqual(corrected.Records.Select(Amount).Order()), "稳定结果修正不改金额");
        }
    }
    Console.WriteLine($"  concentration: {runs} seeds succeeded");
}

static void TestRepairExchange()
{
    var config = Config(240, 40); config.OpeningBalance = 0;
    config.StartTime = new DateTime(2025, 10, 18); config.EndTime = new DateTime(2025, 10, 19);
    var income = Rule(1, 10, 110, 0, true); income.PercentMonth = 4; income.EndDay = 10;
    var fixedExpense = new GenerateConstRule
    {
        Index = 1, IsCheck = true, IncomeAttribute = "支出", MinMoney = 200, MaxMoney = 200,
        FloutLength = 0, FixDay = "18", StartDay = 15, EndDay = 15
    };
    var request = Request(config, [income], [fixedExpense]);
    var rows = new List<FlowRecord>();
    foreach (var (amount, time) in new[]
    {
        (10d, new DateTime(2025, 10, 18, 9, 0, 0)), (20d, new DateTime(2025, 10, 18, 9, 10, 0)),
        (100d, new DateTime(2025, 10, 19, 9, 0, 0)), (110d, new DateTime(2025, 10, 19, 9, 10, 0)),
        (-200d, new DateTime(2025, 10, 18, 15, 0, 0))
    })
    {
        rows.Add(new FlowRecord
        {
            AccountTime = time, TradeMoney = amount,
            ExtraFields = new Dictionary<string, string>
            {
                ["__GeneratedSourceKind"] = amount > 0 ? "Reference" : "Const", ["__GeneratedSourceIndex"] = "1",
                ["__CcbStratumMin"] = amount > 0 ? "10" : "200", ["__CcbStratumMax"] = amount > 0 ? "110" : "200",
                ["__CcbWindowStart"] = config.StartTime.ToString("O"),
                ["__CcbWindowEnd"] = (amount > 0 ? config.EndTime : config.StartTime).AddDays(1).AddSeconds(-1).ToString("O"),
                ["__CcbFixedDate"] = (amount < 0).ToString(), ["__CcbScheduledTime"] = time.ToString("O")
            }
        });
    }
    var source = new FlowAutoGenerationResult
    {
        Records = rows, OpeningBalance = 0, IncomeTotal = 240, ExpenseTotal = 200, FinalBalance = 40,
        MinimumBalance = -170, RequiresOpeningBalanceCorrection = true, RequiredOpeningBalance = 170
    };
    var before = rows.Select(row => (row.AccountTime, row.TradeMoney)).ToArray();
    var result = new FlowAutoGenerator().ApplyNegativeBalanceCorrection(request, source); Validate(request, result);
    Check(result.MinimumBalance >= -0.009, "对调应消除固定支出造成的负余额");
    Check(result.Records.Count == 5 && result.Records.Select(row => row.TradeMoney).Order()
        .SequenceEqual(rows.Select(row => row.TradeMoney).Order()), "不能靠改金额和条数代替对调");
    Check(Source(result, 1).Count(row => row.AccountTime!.Value.Day == 18) == 3, "不能把第四笔收入挤到已满日期");
    Check(Source(result, 1, "Const").Single().AccountTime == new DateTime(2025, 10, 18, 15, 0, 0), "固定支出时间不动");
    Check(result.Records.Select(row => row.AccountTime).Distinct().Count() == 5, "对调不能制造重叠时间");
    Check(before.SequenceEqual(rows.Select(row => (row.AccountTime, row.TradeMoney))), "不修改源流水");
}

static void TestMonthlyRanges()
{
    var config = Config(4000, 1000); config.SelectIndex = 1;
    config.StartTime = new DateTime(2025, 1, 1); config.EndTime = new DateTime(2025, 2, 28);
    config.MinInMoneyMonth2 = 1500; config.MaxInMoneyMonth2 = 2500;
    config.MinOutMoneyMonth2 = 1000; config.MaxOutMoneyMonth2 = 2000;
    var request = Request(config, [Rule(1, 5, 50, 1), Rule(2, 100, 300, 2, true)]);
    var result = Generate(request); Validate(request, result);
    var corrected = new FlowAutoGenerator().ApplyFinalBalanceCorrection(request, result);
    foreach (var month in new[] { 1, 2 })
    {
        var income = result.Records.Where(row => row.AccountTime!.Value.Month == month && row.TradeMoney > 0).Sum(row => row.TradeMoney ?? 0);
        var expense = -result.Records.Where(row => row.AccountTime!.Value.Month == month && row.TradeMoney < 0).Sum(row => row.TradeMoney ?? 0);
        Check(income is >= 1500 and <= 2500 && expense is >= 1000 and <= 2000, "月度范围");
        Near(income, corrected.Records.Where(row => row.AccountTime!.Value.Month == month && row.TradeMoney > 0).Sum(row => row.TradeMoney ?? 0));
        Near(expense, -corrected.Records.Where(row => row.AccountTime!.Value.Month == month && row.TradeMoney < 0).Sum(row => row.TradeMoney ?? 0));
    }
}

static void TestInterest()
{
    var request = Reported(interest: true); var result = Generate(request); Validate(request, result);
    Check(result.Records.Any(FlowGeneratedRowKinds.IsInterest), "应保留银行系统结息");
    var copy = result.Records.Select(row => row.Clone()).ToList();
    BankInterestCalculationService.Recalculate(request.Bank, request.BankUser, request.InterestSetting,
        copy, result.OpeningBalance, request.Config.StartTime, request.Config.EndTime);
    Near(copy.Where(FlowGeneratedRowKinds.IsInterest).Sum(row => row.TradeMoney ?? 0),
        result.Records.Where(FlowGeneratedRowKinds.IsInterest).Sum(row => row.TradeMoney ?? 0));
}

static void TestImpossibleBudget()
{
    var request = Reported(); request.Config.AllInMoney = 1000;
    Throws(() => Generate(request), "最低需要");
}

static void TestReproducibility()
{
    var request = Reported(); var before = JsonSerializer.Serialize(request);
    var a = Generate(request, 1301); var b = Generate(request, 1301); var c = Generate(request, 1302);
    Check(a.Records.Select(row => (row.AccountTime, row.TradeMoney)).SequenceEqual(b.Records.Select(row => (row.AccountTime, row.TradeMoney))), "同种子应复现");
    Check(!a.Records.Select(row => (row.AccountTime, row.TradeMoney)).SequenceEqual(c.Records.Select(row => (row.AccountTime, row.TradeMoney))), "不同种子不能固定取值");
    Check(before == JsonSerializer.Serialize(request), "不能改写用户参照或配置");
}

static void TestSavedConfiguration()
{
    var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeedEmulator");
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var snapshots = JsonSerializer.Deserialize<Dictionary<string, FlowGenerationSnapshot>>(
        System.IO.File.ReadAllText(System.IO.Path.Combine(root, "flow-generation-config.json")), options)!;
    var snapshot = snapshots["8:bank"];
    var users = JsonSerializer.Deserialize<List<BankUser>>(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "bank-users.json")), options)!;
    var user = users.Single(user => user.BankId == 8 && user.Id == 1549);
    var settings = JsonSerializer.Deserialize<Dictionary<string, BankInterestSetting>>(
        System.IO.File.ReadAllText(System.IO.Path.Combine(root, "bank-interest-settings.json")), options)!;
    var bank = Bank();
    foreach (var method in new[] { "ConfigureFlowGenerationColumns", "ConfigureFlowRecordColumns" })
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(method,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [bank]);
    var request = new FlowAutoGenerationRequest
    {
        Bank = bank, BankUser = user, Config = snapshot.Config, References = snapshot.References,
        ConstItems = snapshot.ConstItems, InterestSetting = settings.GetValueOrDefault("8") ?? BankInterestSettingDefaults.CreateDefault(bank)
    };
    var before = JsonSerializer.Serialize(request);
    var result = Generate(request, 41026); Validate(request, result, 0.51, 0.51);
    Check(before == JsonSerializer.Serialize(request), "实际配置不能被改写");
    Console.WriteLine($"ACTUAL_CONFIG rows={result.Records.Count} income={result.IncomeTotal:F2} expense={result.ExpenseTotal:F2} ending={result.FinalBalance:F2} minimum={result.MinimumBalance:F2}");
    var ordinaryIncome = result.Records.Where(row => row.TradeMoney > 0 && !FlowGeneratedRowKinds.IsSystemInterest(row)).ToList();
    var firstDate = result.Records.Min(row => row.AccountTime!.Value.Date);
    Console.WriteLine($"DATE_AUDIT maxIncomePerDay={ordinaryIncome.GroupBy(row => row.AccountTime!.Value.Date).Max(group => group.Count())} firstDayIncomeCount={ordinaryIncome.Count(row => row.AccountTime!.Value.Date == firstDate)} maxMonthIncomeShare={ordinaryIncome.GroupBy(row => row.AccountTime!.Value.ToString("yyyy-MM")).Max(group => group.Sum(Amount)) / result.IncomeTotal:P1}");
    foreach (var index in new[] { 1, 2, 3, 17, 18, 19, 20 })
    {
        var rows = Source(result, index);
        Console.WriteLine($"rule={index} count={rows.Count} min={rows.Min(Amount):F2} max={rows.Max(Amount):F2} below10={rows.Count(row => Amount(row) < 10)} distinct={rows.Select(Amount).Distinct().Count()}");
    }
    Console.WriteLine("READ_ONLY_AUDIT: no repository save, no app restart, no user data written.");
}
