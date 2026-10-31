using SpeedEmulator.Models;
using SpeedEmulator.Services;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("只针对个人中行，微信及其他银行不启用", TestScope),
    ("收入支出允许上下10%，1%不是最小必须偏差", TestBudgetBands),
    ("截图同类配置多随机种子不缩水，负余额可修正", TestReportedConfiguration),
    ("收入上下浮动时支出联动，不删收入凑余额", TestLinkedClosing),
    ("修正保持固定日期和每月必现次数", TestRequiredOccurrences),
    ("时间不可移动时等额配对修正，不改变总额条数", TestPairedCorrection),
    ("固定项目无法修正时保留负余额供原校验拦截", TestUnrepairableNegative),
    ("只有固定项目且预算明显不足时明确拒绝", TestInsufficientCapacity),
    ("零支出目标不强行添加可选支出", TestZeroExpense),
    ("整数粗粒度不能突破金额范围或10%预算", TestCoarseAmounts),
    ("固定项目先为每月必现收支预留最低预算", TestRequiredBudgetReservation),
    ("每月必现没有可交易日期时不捏造流水", TestUnavailableRequiredDates),
    ("非零期初及覆盖值在修正后保持不变", TestOpeningBalance),
    ("混合精度十个月多种子生成和修正不再集中11尾数", TestMixedPrecisionClosing),
    ("残月必现项目不受零月预算挤压到小数下限", TestPartialMonthRequiredAmounts),
    ("含结息收入按百元取值时进入允许区间而非强求分界金额", TestRepresentableIncomeBand),
    ("中行阶段日志记录种子及集中检测，其他银行不写入", TestGenerationDiagnostics)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void TestScope()
{
    Check(BankOfChinaGenerationBudget.AppliesTo(Bank()), "个人中行启用");
    Check(!BankOfChinaGenerationBudget.AppliesTo(new Bank { Id = 18, Name = "中行对公", Type = BankTypes.Corporate }), "对公不能启用");
    foreach (var name in new[] { "微信", "支付宝", "建行", "农行", "邮政", "工行" })
        Check(!BankOfChinaGenerationBudget.AppliesTo(new Bank { Id = 2, Name = name, Type = BankTypes.Personal }), "其他银行不能启用");
}

static void TestBudgetBands()
{
    var config = Config(100000, 10000);
    foreach (var totals in new[] { (90000d, 81000d), (110000d, 99000d), (100000d, 90000d), (101000d, 90900d) })
        Check(BankOfChinaGenerationBudget.TryValidate(Bank(), config, Result([], totals.Item1, totals.Item2), out _), "±10%及精确目标都应接受");
    foreach (var totals in new[] { (89999d, 81000d), (110001d, 99000d), (100000d, 80999d), (100000d, 99001d), (76995.68d, 66995.68d) })
        Check(!BankOfChinaGenerationBudget.TryValidate(Bank(), config, Result([], totals.Item1, totals.Item2), out _), "越界或严重缩水应拒绝");
    Check(!BankOfChinaGenerationBudget.TryValidate(Bank(), config, Result([], double.NaN, 90000), out _), "非有限金额应拒绝");
    Check(!BankOfChinaGenerationBudget.TryValidate(Bank(), Config(0, 0), Result([], 0.01, 0), out _), "零目标不能用比例放宽");
}

static void TestReportedConfiguration()
{
    var rules = new List<GenerateReferenceRule>();
    Add(rules, true, 1, 300, 5000, -1);
    Add(rules, true, 1, 1000, 3000, -3);
    Add(rules, true, 2, 1000, 3000, -2);
    Add(rules, true, 2, 1000, 3000, -1);
    Add(rules, true, 2, 1000, 3000, 0);
    Add(rules, true, 4, 1000, 5000, -3);
    Add(rules, true, 7, 1000, 5000, -2);
    Add(rules, true, 7, 1000, 5000, -1);
    Add(rules, true, 1, 1000, 5000, 0);
    Add(rules, true, 4, 1000, 10000, -1);
    Add(rules, false, 3, 2, 100, 0);
    Add(rules, false, 9, 5, 300, 0);
    Add(rules, false, 1, 5, 300, 2);
    Add(rules, false, 1, 5, 1000, 0);
    Add(rules, false, 1, 20, 600, 0);
    Add(rules, false, 1, 50, 300, -1);
    Add(rules, false, 1, 80, 500, -1);
    Add(rules, false, 1, 100, 500, -1);
    Add(rules, false, 1, 100, 600, -1);
    Add(rules, false, 1, 1000, 2000, 0);
    Add(rules, false, 5, 1000, 5000, -2);
    Add(rules, false, 3, 1000, 10000, -3);
    Add(rules, false, 4, 1000, 10000, -2);
    Add(rules, false, 5, 1000, 10000, -1);
    Add(rules, false, 2, 1000, 10000, 0);
    Add(rules, false, 1, 5000, 10000, -3);
    var constants = new[] { Fixed(1, "-", "10"), Fixed(2, "+", "50"), Fixed(3, "*", "5"), Fixed(4, "=", "1") };
    var config = Config(702376.36, 10000);
    config.StartTime = new DateTime(2025, 9, 10);
    config.EndTime = new DateTime(2026, 9, 10);
    config.MinInMoneyMonth1 = config.MaxInMoneyMonth1 = 54028.95;
    config.MinOutMoneyMonth1 = config.MaxOutMoneyMonth1 = 53259.72;
    var request = Request(config, rules, constants, interest: true);
    var generator = new FlowAutoGenerator();
    for (var seed = 1001; seed <= 1010; seed++)
    {
        Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString());
        var result = generator.Generate(request);
        Validate(request, result);
        Check(result.Records.Count(row => FlowGeneratedRowKinds.IsInterest(row)) == 4, "全年4条结息");
        Check(result.Records.Any(row => row.TradeMoney > 0 && Kind(row) == "Reference"), "普通收入必须保留");
        Check(Source(result, "Const", 1).Count == 130 && Source(result, "Const", 2).Count == 50
            && Source(result, "Const", 3).Count == 65 && Source(result, "Const", 4).Count == 262, "四种固定项目次数不能缩水");
        if (result.MinimumBalance < -0.009)
        {
            var fixedBefore = FixedTimes(result);
            var count = result.Records.Count;
            var ordinaryIncome = OrdinaryTotal(result, true);
            var sourceAmounts = result.Records.Select(row => row.TradeMoney).ToArray();
            var corrected = generator.ApplyNegativeBalanceCorrection(request, result);
            Validate(request, corrected);
            Check(corrected.MinimumBalance >= -0.009, "同类配置修正后不得留下负余额");
            Check(corrected.Records.Count == count, "修正不得增删流水");
            Check(FixedTimes(corrected).SequenceEqual(fixedBefore), "固定项目日期不能移动");
            Near(ordinaryIncome, OrdinaryTotal(corrected, true), 0.01);
            Check(sourceAmounts.SequenceEqual(result.Records.Select(row => row.TradeMoney)), "不污染源结果");
            var recomputed = corrected.Records.Select(row => row.Clone()).ToList();
            BankInterestCalculationService.Recalculate(request.Bank, request.BankUser, request.InterestSetting,
                recomputed, corrected.OpeningBalance, config.StartTime, config.EndTime);
            Near(recomputed.Where(FlowGeneratedRowKinds.IsInterest).Sum(row => row.TradeMoney ?? 0),
                corrected.Records.Where(FlowGeneratedRowKinds.IsInterest).Sum(row => row.TradeMoney ?? 0), 0.01);
        }
    }
}

static void TestLinkedClosing()
{
    foreach (var income in new[] { 91000d, 109000d })
    {
        var config = Config(100000, 10000);
        var request = Request(config, []);
        var rows = new List<FlowRecord>
        {
            Row(new DateTime(2026, 1, 1, 9, 0, 0), income, 90000, 110000),
            Row(new DateTime(2026, 1, 2, 9, 0, 0), -90000, 80000, 100000)
        };
        var result = new FlowAutoGenerator().ApplyFinalBalanceCorrection(request, Result(rows, income, 90000));
        Validate(request, result);
        Near(income, result.IncomeTotal, 0.01);
        Near(income - 10000, result.ExpenseTotal, 0.01);
        Check(result.Records.Count == 2, "不删收入、不增删行");
    }
}

static void TestRequiredOccurrences()
{
    var config = Config(20000, 2000);
    config.EndTime = new DateTime(2026, 2, 28);
    var rules = new[] { Rule(1, true, 100, 1000, 0, 2), Rule(2, true, 100, 1000), Rule(3, false, 10, 500) };
    var constants = new[] { Fixed(1, "15", "2") };
    var request = Request(config, rules, constants);
    var generator = new FlowAutoGenerator();
    var before = generator.Generate(request);
    var after = generator.ApplyNegativeBalanceCorrection(request, before);
    Validate(request, after);
    foreach (var month in new[] { 1, 2 })
        Check(Source(after, "Reference", 1).Count(row => row.AccountTime!.Value.Month == month) == 2, "月必现次数不能跨月移动");
    Check(FixedTimes(before).SequenceEqual(FixedTimes(after)), "指定日固定流水不得移动");
}

static void TestPairedCorrection()
{
    var config = Config(100, 10);
    var rows = new List<FlowRecord>
    {
        Row(new DateTime(2026, 1, 1, 9, 0, 0), 50, 50, 50),
        Row(new DateTime(2026, 1, 1, 10, 0, 0), -60, 10, 80),
        Row(new DateTime(2026, 1, 2, 9, 0, 0), 50, 50, 50),
        Row(new DateTime(2026, 1, 2, 10, 0, 0), -30, 10, 80)
    };
    var request = Request(config, []);
    var result = new FlowAutoGenerator().ApplyNegativeBalanceCorrection(request, Result(rows, 100, 90));
    Validate(request, result);
    Check(result.MinimumBalance >= -0.009, "等额转移支出能修正早期负余额");
    Near(100, result.IncomeTotal, 0.01); Near(90, result.ExpenseTotal, 0.01);
    Check(result.Records.Select(row => row.AccountTime).SequenceEqual(rows.Select(row => row.AccountTime)), "不移动固定日期");
    Check(result.Records.Count == 4, "不增删行");
}

static void TestUnrepairableNegative()
{
    var config = Config(100, 10);
    var rows = new List<FlowRecord>
    {
        Row(new DateTime(2026, 1, 1, 9, 0, 0), -90, 90, 90),
        Row(new DateTime(2026, 1, 2, 9, 0, 0), 100, 100, 100)
    };
    var result = new FlowAutoGenerator().ApplyNegativeBalanceCorrection(Request(config, []), Result(rows, 100, 90));
    Check(result.MinimumBalance < 0 && result.RequiresOpeningBalanceCorrection, "不能取消负余额拦截或伪造修正成功");
    Check(result.Records.Count == 2 && result.OpeningBalance == 0, "不能补造记录或抬高期初余额");
}

static void TestInsufficientCapacity()
{
    var config = Config(100000, 10000);
    var constant = Fixed(1, "+", "2");
    constant.MinMoney = constant.MaxMoney = 10;
    try { new FlowAutoGenerator().Generate(Request(config, [], [constant])); }
    catch (InvalidOperationException exception)
    {
        Check(exception.Message.Contains("10%"), "预算不足应说明偏差而不是缩水放行"); return;
    }
    throw new Exception("不能保存20元收入冒充10万元目标");
}

static void TestZeroExpense()
{
    var request = Request(Config(1000, 1000), [Rule(1, true, 10, 100), Rule(2, false, 10, 100)]);
    var result = new FlowAutoGenerator().Generate(request);
    Validate(request, result); Near(0, result.ExpenseTotal, 0.001);
}

static void TestCoarseAmounts()
{
    var request = Request(Config(5050, 1000), [Rule(1, true, 1000, 3000, -3), Rule(2, false, 1000, 3000, -3)]);
    var result = new FlowAutoGenerator().Generate(request);
    Validate(request, result);
    Check(result.Records.All(row => Math.Abs(row.TradeMoney ?? 0) is >= 1000 and <= 3000), "粗粒度不能越界");
}

static void TestRequiredBudgetReservation()
{
    foreach (var requiredIncome in new[] { true, false })
    {
        var constant = Fixed(1, "+", "10");
        constant.IncomeAttribute = requiredIncome ? "收入" : "支出";
        constant.MinMoney = 100;
        var required = Rule(1, requiredIncome, 1000, 1000, 0, 8);
        var optional = Rule(2, !requiredIncome, 10, 1000);
        var request = Request(Config(10000, 1000), [required, optional], [constant]);
        var result = new FlowAutoGenerator().Generate(request);
        Validate(request, result);
        Check(Source(result, "Reference", 1).Count == 8, "必现收支次数都要保留");
        Check(Source(result, "Const", 1).Count == 10, "固定项目次数都要保留");
    }
}

static void TestUnavailableRequiredDates()
{
    var config = Config(1000, 100);
    config.StartTime = new DateTime(2026, 1, 3);
    config.EndTime = new DateTime(2026, 1, 4);
    var required = Rule(1, true, 10, 100, 0, 2);
    required.TradeWeekend = false;
    try { new FlowAutoGenerator().Generate(Request(config, [required, Rule(2, false, 10, 100)])); }
    catch (InvalidOperationException exception)
    {
        Check(exception.Message.Contains("没有可交易日期"), "明确提示日期约束冲突"); return;
    }
    throw new Exception("不能跳过必现次数或捏造周末交易");
}

static void TestOpeningBalance()
{
    var config = Config(20000, 2000);
    config.OpeningBalance = 3000;
    config.StartTime = new DateTime(2026, 3, 1);
    config.EndTime = new DateTime(2026, 3, 31);
    var rules = new[] { Rule(1, true, 100, 1000), Rule(2, false, 10, 500) };
    var basic = Request(config, rules, interest: true);
    var request = new FlowAutoGenerationRequest
    {
        Bank = basic.Bank, BankUser = basic.BankUser, Config = config, References = rules, ConstItems = [],
        InterestSetting = basic.InterestSetting, OpeningBalanceOverride = 600
    };
    var generator = new FlowAutoGenerator();
    var result = generator.Generate(request);
    var corrected = generator.ApplyNegativeBalanceCorrection(request, result);
    Validate(request, corrected);
    Near(600, result.OpeningBalance, 0.001); Near(600, corrected.OpeningBalance, 0.001);
    Near(3000, config.OpeningBalance, 0.001); Near(2600, corrected.FinalBalance, 1000);
    Check(corrected.MinimumBalance >= -0.009, "正期初仍应按原值修正");
}

static void TestMixedPrecisionClosing()
{
    var rules = new List<GenerateReferenceRule> { Rule(1, false, 3, 60, 0, 2), Rule(2, false, 5000, 30000, -2),
        Rule(3, false, 3, 50, 0, 2) };
    Add(rules, true, 20, 5000, 30000, -2);
    foreach (var item in new[] { (10d,80d,0,3), (10d,80d,0,2), (10d,80d,2,3), (2d,200d,0,2),
                 (10d,80d,-1,1), (20d,100d,2,3), (20d,100d,2,2), (15d,80d,2,2), (15d,50d,2,1),
                 (5d,100d,0,3), (30d,150d,2,1), (100d,300d,-1,2), (30d,100d,2,1), (20d,300d,1,1), (30d,150d,2,1) })
        rules.Add(Rule(rules.Count + 1, false, item.Item1, item.Item2, item.Item3, item.Item4));
    Add(rules, false, 19, 5000, 30000, -2);
    var config = Config(158000, 1000);
    config.StartTime = new DateTime(2025, 4, 1); config.EndTime = new DateTime(2026, 1, 1);
    config.OpeningBalance = 15874.39;
    config.MinInMoneyMonth1 = config.MaxInMoneyMonth1 = config.MinInMoneyMonth2 = config.MaxInMoneyMonth2 = 15800;
    config.MinOutMoneyMonth1 = config.MaxOutMoneyMonth1 = config.MinOutMoneyMonth2 = config.MaxOutMoneyMonth2 = 15700;
    var request = Request(config, rules, interest: true);
    var previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    var input = JsonSerializer.Serialize(request);
    try
    {
        foreach (var seed in Enumerable.Range(8101, 40).Append(-525913056))
        {
            Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString(CultureInfo.InvariantCulture));
            var generator = new FlowAutoGenerator();
            var result = generator.Generate(request);
            Validate(request, result); CheckTails(result);
            foreach (var rule in rules.Where(rule => rule.PercentMonth > 0))
            foreach (var month in result.Records.Select(row => row.AccountTime!.Value.ToString("yyyy-MM")).Distinct())
                Check(Source(result, "Reference", rule.Index).Count(row => row.AccountTime!.Value.ToString("yyyy-MM") == month) == rule.PercentMonth,
                    "每月必现次数不能改变");
            var corrected = generator.ApplyNegativeBalanceCorrection(request, result);
            Validate(request, corrected); CheckTails(corrected);
            Check(corrected.MinimumBalance >= -.009, "负余额修正必须可完成");
            var final = generator.ApplyFinalBalanceCorrection(request, corrected);
            Validate(request, final); CheckTails(final);
            var opening = generator.ApplyOpeningBalanceCorrection(request, final, final.OpeningBalance + 5000);
            Validate(request, opening); CheckTails(opening);
            Near(final.OpeningBalance + 5000, opening.OpeningBalance, .000001);
            Check(opening.Records.Count == final.Records.Count, "期初余额修正不能增删流水");
        }
        Check(JsonSerializer.Serialize(request) == input, "不能改写用户参照和金额配置");
    }
    finally { Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous); }

    static void CheckTails(FlowAutoGenerationResult result)
    {
        var p2 = result.Records.Where(row => Kind(row) == "Reference" && row.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision") == "2").ToArray();
        Check(p2.Length == 140, "140条必现两位小数流水不能减少");
        var tails = p2.Select(row => (int)Math.Round(Math.Abs(row.TradeMoney!.Value) * 100, MidpointRounding.AwayFromZero) % 100).ToArray();
        Check(tails.All(tail => tail >= 11 && tail % 10 != 0), "必须保留原合法小数约束");
        Check(tails.GroupBy(tail => tail).Max(group => group.Count()) <= 5, "不能再成批集中为同一个尾数");
        foreach (var day in p2.GroupBy(row => row.AccountTime!.Value.Date))
        {
            var maximum = day.GroupBy(row => (int)Math.Round(Math.Abs(row.TradeMoney!.Value) * 100) % 100).Max(group => group.Count());
            Check(maximum <= 2, $"{day.Key:yyyy-MM-dd} 单日同尾数集中{maximum}条，不能只检查全局比例");
        }
    }
}

static void TestPartialMonthRequiredAmounts()
{
    var config = Config(158000, 1000);
    config.StartTime = new DateTime(2025, 4, 1); config.EndTime = new DateTime(2026, 1, 1);
    config.OpeningBalance = 35874.39;
    var request = Request(config, [Rule(1, false, 2, 80, 0, 18), Rule(2, false, 20, 100, 2, 14)]);
    var end = config.EndTime.AddDays(1).AddTicks(-1);
    var plan = Invoke("CreateMonthlyAmountPlan", request, config.StartTime, end, 158000d, 157000d, new Random(123))!;
    var targets = (IList<double>)plan.GetType().GetProperty("ExpenseTargets")!.GetValue(plan)!;
    targets[^1] = 0; // A soft allocation cannot turn every mandatory payment into its minimum.
    var rows = new List<FlowRecord>();
    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    var state = typeof(FlowAutoGenerator).GetNestedType("NativeScheduleState", BindingFlags.NonPublic)!
        .GetMethod("From", flags)!.Invoke(null, [rows])!;
    var usage = typeof(FlowAutoGenerator).GetNestedType("AmountUsageTracker", BindingFlags.NonPublic)!
        .GetMethod("From", flags)!.Invoke(null, [rows])!;
    Invoke("GenerateBankOfChinaRequiredReferences", request, plan, 173800d, new Random(456), rows, state, usage);
    var lastDay = rows.Where(row => row.AccountTime!.Value.Date == config.EndTime && row.ExtraFields["__GeneratedAmountPrecision"] == "2").ToArray();
    Check(lastDay.Length == 14, "残月仍需保留配置要求的14次出现，不能通过删流水规避");
    var tails = lastDay.Select(row => (int)Math.Round(Math.Abs(row.TradeMoney!.Value) * 100) % 100).ToArray();
    Check(tails.Distinct().Count() >= 12 && tails.Count(tail => tail == 11) <= 1, "零软预算不能把残月必现流水压成一批.11");
    Check(lastDay.All(row => Math.Abs(row.TradeMoney!.Value) is >= 20 and <= 100), "仍须遵守原金额范围");
}

static void TestRepresentableIncomeBand()
{
    foreach (var item in new[] { (2100d, 20.12, 1900d, 2100d), (1700d, 80.12, 1800d, 2000d) })
    {
        var coarse = Row(new DateTime(2026, 1, 5), item.Item1, 1100, 2900);
        coarse.ExtraFields["__GeneratedSourceKind"] = "Reference";
        coarse.ExtraFields["__GeneratedAmountUnit"] = "100";
        coarse.ExtraFields["__GeneratedAmountPrecision"] = "-2";
        // An existing settlement amount is immutable; only the reference transfer can move.
        var settlement = new FlowRecord { AccountTime = new DateTime(2026, 1, 21), TradeMoney = item.Item2,
            ExtraFields = new() { ["__GeneratedSourceKind"] = "Interest" } };
        var rows = new List<FlowRecord> { coarse, settlement };
        Invoke("MoveBankOfChinaIncomeInsideBand", rows, item.Item3, item.Item4);
        var income = rows.Sum(row => row.TradeMoney!.Value);
        Check(income >= item.Item3 && income <= item.Item4, "应选择区间内可实现的百元收入");
        Near(item.Item2, settlement.TradeMoney!.Value, .000001);
        Check((int)Math.Round(coarse.TradeMoney!.Value) % 100 == 0, "不能破坏百元精度");
    }
}

static void TestGenerationDiagnostics()
{
    var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpeedEmulator-boc-diagnostics-" + Guid.NewGuid().ToString("N"));
    var previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_BOC_LOG_DIR");
    try
    {
        Environment.SetEnvironmentVariable("SPEEDEMULATOR_BOC_LOG_DIR", directory);
        var request = Request(Config(10000, 1000), [Rule(1, true, 100, 1000), Rule(2, false, 10, 100, 2)]);
        var generator = new FlowAutoGenerator();
        var result = generator.Generate(request);
        generator.ApplyNegativeBalanceCorrection(request, result);
        var path = System.IO.Path.Combine(directory, "generation-boc.log");
        var lines = System.IO.File.ReadAllLines(path);
        var stages = lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("stage").GetString()).ToArray();
        Check(stages.Contains("seed") && stages.Contains("before-closing") && stages.Contains("closing") && stages.Contains("result"),
            "日志必须包含实际种子、收口阶段和最终尾数统计");
        var alien = new FlowAutoGenerationRequest { Bank = new Bank { Id = 19, Name = "工行", Type = BankTypes.Personal },
            BankUser = request.BankUser, Config = request.Config, References = request.References, ConstItems = [] };
        generator.Generate(alien);
        Check(System.IO.File.ReadAllLines(path).Length == lines.Length, "其他银行不得进入中行日志分支");
        var diagnostic = typeof(FlowAutoGenerator).Assembly.GetType("SpeedEmulator.Services.BankOfChinaGenerationDiagnostics")!;
        var rows = Enumerable.Range(1, 30).Select(i => { var row = Row(new DateTime(2026, 1, 1).AddHours(i), -20.11, 20.11, 80.99);
            row.ExtraFields["__GeneratedSourceKind"] = "Reference"; row.ExtraFields["__GeneratedAmountPrecision"] = "2"; return row; }).ToList();
        var statistics = diagnostic.GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [rows])!;
        Check((bool)statistics.GetType().GetProperty("Concentrated")!.GetValue(statistics)!, "集中结果即使尾数合法也必须被标记");
        var legal = Enumerable.Range(11, 89).Where(tail => tail % 10 != 0).ToArray();
        var dailyRows = Enumerable.Range(0, 140).Select(i =>
        {
            var row = Row(i < 3 ? new DateTime(2026, 1, 1, 9, i, 0) : new DateTime(2025, 4, 1).AddDays(i),
                -(20 + (i < 3 ? 11 : legal[i % legal.Length]) / 100d), 10.11, 80.99);
            row.ExtraFields["__GeneratedSourceKind"] = "Reference"; row.ExtraFields["__GeneratedAmountPrecision"] = "2"; return row;
        }).ToList();
        statistics = diagnostic.GetMethod("Analyze", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [dailyRows])!;
        Check((int)statistics.GetType().GetProperty("MaximumTailCount")!.GetValue(statistics)! <= 5,
            "此样本的全局尾数统计应正常");
        Check((int)statistics.GetType().GetProperty("MaximumTailOnSameDay")!.GetValue(statistics)! == 3
            && (bool)statistics.GetType().GetProperty("Concentrated")!.GetValue(statistics)!, "全局正常也必须发现单日集中");
    }
    finally { Environment.SetEnvironmentVariable("SPEEDEMULATOR_BOC_LOG_DIR", previous); }
}

static object? Invoke(string name, params object[] args)
{
    try { return typeof(FlowAutoGenerator).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args); }
    catch (TargetInvocationException exception) { throw exception.InnerException!; }
}

static Bank Bank() => new() { Id = 18, Name = "中行", Type = BankTypes.Personal };
static FlowGenerationConfig Config(double income, double last) => new()
{
    StartTime = new DateTime(2026, 1, 1), EndTime = new DateTime(2026, 1, 31), AllInMoney = income,
    LastMoney = last, OpeningBalance = 0, MinInMoneyMonth1 = 0, MaxInMoneyMonth1 = 0,
    MinOutMoneyMonth1 = 0, MaxOutMoneyMonth1 = 0
};
static FlowAutoGenerationRequest Request(FlowGenerationConfig config, IReadOnlyList<GenerateReferenceRule> rules,
    IReadOnlyList<GenerateConstRule>? constants = null, bool interest = false) => new()
{
    Bank = Bank(), BankUser = new BankUser { Id = 99, BankId = 18, BankName = "中行", AutoCalculateInterest = interest },
    Config = config, References = rules, ConstItems = constants ?? [], InterestSetting = BankInterestSettingDefaults.CreateDefault(Bank())
};
static GenerateReferenceRule Rule(int index, bool income, double min, double max, int precision = 0, int monthly = 0) => new()
{
    Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出", MinMoney = min, MaxMoney = max,
    FloutLength = precision, PercentMonth = monthly, StartDay = 9, EndDay = 21, TradeHoliday = true, TradeWeekend = true
};
static GenerateConstRule Fixed(int index, string day, string count) => new()
{
    Index = index, IsCheck = true, IncomeAttribute = "收入", MinMoney = 10, MaxMoney = 1000,
    FloutLength = 0, StartDay = 6, EndDay = 18, TradeHoliday = false, TradeWeekend = false, FixDay = day, ReCnt = count
};
static void Add(List<GenerateReferenceRule> rules, bool income, int count, double min, double max, int precision)
{
    for (var i = 0; i < count; i++) rules.Add(Rule(rules.Count + 1, income, min, max, precision));
}
static FlowRecord Row(DateTime time, double amount, double min, double max) => new()
{
    AccountTime = time, TradeMoney = amount,
    ExtraFields = new() { ["__GeneratedAmountMin"] = min.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["__GeneratedAmountMax"] = max.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["__GeneratedAmountUnit"] = "1", ["__GeneratedAmountPrecision"] = "0", ["__GeneratedRequiredOccurrence"] = "true" }
};
static FlowAutoGenerationResult Result(List<FlowRecord> rows, double income, double expense) => new()
{
    Records = rows, OpeningBalance = 0, IncomeTotal = income, ExpenseTotal = expense, FinalBalance = income - expense,
    MinimumBalance = -1, RequiresOpeningBalanceCorrection = true, RequiredOpeningBalance = 1
};
static string Kind(FlowRecord row) => row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind", "");
static List<FlowRecord> Source(FlowAutoGenerationResult result, string kind, int index) => result.Records
    .Where(row => Kind(row) == kind && row.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex") == index.ToString()).ToList();
static DateTime[] FixedTimes(FlowAutoGenerationResult result) => result.Records.Where(row => Kind(row) == "Const")
    .Select(row => row.AccountTime!.Value).OrderBy(time => time).ToArray();
static double OrdinaryTotal(FlowAutoGenerationResult result, bool income) => Math.Round(result.Records
    .Where(row => !FlowGeneratedRowKinds.IsSystemInterest(row) && (income ? row.TradeMoney > 0 : row.TradeMoney < 0))
    .Sum(row => Math.Abs(row.TradeMoney ?? 0)), 2);
static void Validate(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
{
    Check(BankOfChinaGenerationBudget.TryValidate(request.Bank, request.Config, result, out var reason), reason);
    Near(result.OpeningBalance + request.Config.LastMoney, result.FinalBalance, 1000);
    var balance = result.OpeningBalance;
    DateTime? previous = null;
    foreach (var row in result.Records)
    {
        Check(previous is null || row.AccountTime >= previous, "时间有序"); previous = row.AccountTime;
        balance = Math.Round(balance + (row.TradeMoney ?? 0), 2, MidpointRounding.AwayFromZero);
        Near(balance, row.Balance!.Value, 0.01);
        if (FlowGeneratedRowKinds.IsSystemInterest(row)) continue;
        var kind = Kind(row);
        if (kind.Length == 0) continue;
        var index = int.Parse(row.ExtraFields["__GeneratedSourceIndex"]);
        FlowRuleBase rule = kind == "Const" ? request.ConstItems.Single(rule => rule.Index == index)
            : request.References.Single(rule => rule.Index == index);
        var amount = Math.Abs(row.TradeMoney ?? 0);
        Check(amount >= rule.MinMoney - 0.009 && amount <= rule.MaxMoney + 0.009,
            $"不得突破来源规则金额范围：{kind}/{index}，金额{amount}，范围{rule.MinMoney}～{rule.MaxMoney}，" +
            $"元数据{row.ExtraFields.GetValueOrDefault("__GeneratedAmountMin")}～{row.ExtraFields.GetValueOrDefault("__GeneratedAmountMax")}");
        var unit = Math.Pow(10, -(rule.FloutLength ?? 0));
        Near(Math.Round(amount / unit), amount / unit, 0.000001);
        if (rule.FloutLength != 0) Check((long)Math.Round(amount / unit) % 10 != 0, "指定金额位数不能变成0");
        Check(row.AccountTime >= request.Config.StartTime.Date && row.AccountTime < request.Config.EndTime.Date.AddDays(1), "不越日期范围");
        Check(row.AccountTime!.Value.Hour >= rule.StartDay && row.AccountTime.Value.Hour <= rule.EndDay, "不越交易时段");
        if (rule.TradeWeekend == false) Check(row.AccountTime.Value.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday, "周末限制保留");
    }
    Near(balance, result.FinalBalance, 0.01);
}
static void Near(double expected, double actual, double tolerance) => Check(Math.Abs(expected - actual) <= tolerance + 0.000001,
    $"expected={expected}, actual={actual}, tolerance={tolerance}");
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
