using SpeedEmulator.Models;
using SpeedEmulator.Services;

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
    ("非零期初及覆盖值在修正后保持不变", TestOpeningBalance)
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
