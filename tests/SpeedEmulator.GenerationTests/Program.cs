using System.Diagnostics;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("微信按规则自然形成大量小额收入、少量大额支出", TestNaturalDistribution),
    ("微信固定日期和每月必现次数保持不变", TestRequiredOccurrences),
    ("微信金额精度、交易时段和周末限制", TestConstraints),
    ("微信始终不生成结息", TestNoInterest),
    ("微信负余额继续交给原确认修正流程", TestNegativeBalanceContract),
    ("微信金额尾差不突破参照范围", TestRoundingTolerance),
    ("微信必现最低金额超过预算时明确报错", TestImpossibleRequiredBudget),
    ("微信按月明细金额分配", TestMonthlyDetail),
    ("微信支出占多数时不强制收入条数比例", TestExpenseDominant),
    ("微信无可交易日期的月份不捏造周末流水", TestUnavailableMonth),
    ("微信可在一千元容差内补齐大面额预算", TestLargeDenominationTolerance),
    ("微信小预算仍使用可负担的小额规则", TestSmallBudget),
    ("微信负余额优先只改时间，保持金额条数及每月次数", TestNegativeTimeCorrection)
};
var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void TestNaturalDistribution()
{
    // Synthetic rules matching the compared amount ranges, without customer data.
    var rules = new List<GenerateReferenceRule>();
    AddRules(rules, true, 7, 30, 300, -1);
    AddRules(rules, true, 2, 30, 300, 0);
    AddRules(rules, true, 85, 50, 150, -1);
    AddRules(rules, true, 2, 50, 150, -1, 1);
    AddRules(rules, true, 9, 50, 300, -2);
    AddRules(rules, true, 183, 50, 300, -1);
    AddRules(rules, true, 52, 50, 300, 0);
    AddRules(rules, true, 5, 300, 1000, -2);
    AddRules(rules, true, 25, 300, 1000, -1);
    AddRules(rules, true, 3, 1000, 10000, -2);
    AddRules(rules, true, 4, 1000, 20000, -2);
    AddRules(rules, true, 1, 1000, 20000, -2, 1);
    AddRules(rules, false, 1, 1000, 10000, -1);
    AddRules(rules, false, 2, 1000, 10000, 0);
    AddRules(rules, false, 2, 1000, 20000, -1);
    AddRules(rules, false, 5, 1000, 50000, -3);
    AddRules(rules, false, 6, 1000, 50000, -2);
    AddRules(rules, false, 3, 5000, 10000, -1);
    AddRules(rules, false, 19, 10000, 50000, -2);
    AddRules(rules, false, 1, 10000, 50000, -1);
    AddRules(rules, false, 3, 20000, 50000, -2);
    AddRules(rules, false, 2, 20000, 50000, -1);
    Check(rules.Count == 422, "测试规则应为 422 条");
    var config = Config(new DateTime(2025, 9, 18), new DateTime(2026, 9, 18), 5000000, 20000, 100000);
    config.MinInMoneyMonth1 = config.MaxInMoneyMonth1 = 384615.38;
    config.MinOutMoneyMonth1 = config.MaxOutMoneyMonth1 = 383076.92;
    var totals = new List<int>();
    for (var run = 0; run < 3; run++)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = Generate(config, rules);
        totals.Add(result.Records.Count);
        var income = result.Records.Where(record => record.TradeMoney > 0).ToList();
        var expense = result.Records.Where(record => record.TradeMoney < 0).ToList();
        Check(result.Records.Count is > 11000 and < 19000, "不能再被压成 8000 左右或硬凑 15000");
        Check(income.Count / (double)result.Records.Count > 0.95, "条数比例必须由配置形成，不能固定 55/45");
        Check(expense.Count is > 100 and < 400, "大额支出不能被拆成几千笔");
        Check(income.Count(record => record.TradeMoney <= 300) / (double)income.Count > 0.85, "大量小额规则应得到相应选择权重");
        Near(5000000, result.IncomeTotal, 100);
        Near(4980000, result.ExpenseTotal, 100);
        Near(120000, result.FinalBalance, 100);
        ValidateRows(result, rules);
        if (result.MinimumBalance < -0.009)
        {
            var corrected = new FlowAutoGenerator().ApplyNegativeBalanceCorrection(new FlowAutoGenerationRequest
            {
                Bank = Bank(), BankUser = new BankUser { Id = 99, BankName = "微信" },
                Config = config, References = rules, ConstItems = []
            }, result);
            Check(corrected.MinimumBalance >= -0.009, "密集微信流水修正后不能仍有负余额");
            Check(corrected.Records.Count == result.Records.Count, "仅调整时间可修正的场景不能改动条数");
            Near(result.IncomeTotal, corrected.IncomeTotal, 0.01);
            Near(result.ExpenseTotal, corrected.ExpenseTotal, 0.01);
            Near(result.OpeningBalance, corrected.OpeningBalance, 0.01);
            ValidateRows(corrected, rules);
        }
        Console.WriteLine($"  run={run + 1}, rows={result.Records.Count}, incomeRows={income.Count}, expenseRows={expense.Count}, " +
            $"meanIncome={income.Average(record => record.TradeMoney):F2}, meanExpense={expense.Average(record => -record.TradeMoney):F2}, " +
            $"final={result.FinalBalance:F2}, minimum={result.MinimumBalance:F2}, elapsedMs={stopwatch.ElapsedMilliseconds}");
    }
    Check(totals.Distinct().Count() > 1, "自然随机生成的条数不应固定");
}

static void TestRequiredOccurrences()
{
    var config = Config(new DateTime(2025, 1, 1), new DateTime(2025, 2, 28), 2000, 1000, 1000);
    var required = Rule(1, true, 10, 10, 0, 3);
    var optionalIncome = Rule(2, true, 10, 50, 0);
    var optionalExpense = Rule(3, false, 10, 50, 0);
    var fixedRule = new GenerateConstRule
    {
        Index = 4, IsCheck = true, IncomeAttribute = "收入", MinMoney = 20, MaxMoney = 20,
        FloutLength = 0, StartDay = 9, EndDay = 17, TradeHoliday = true, TradeWeekend = true,
        FixDay = "3-6", ReCnt = "2", Remark = "fixed-test"
    };
    var result = Generate(config, [required, optionalIncome, optionalExpense], [fixedRule]);
    var requiredRows = SourceRows(result, "Reference", 1);
    Check(requiredRows.Count == 6, "必现次数必须精确，不能参与随机填充增加数量");
    Check(requiredRows.GroupBy(record => record.AccountTime!.Value.Month).All(group => group.Count() == 3), "每月应有 3 笔");
    var fixedRows = SourceRows(result, "Const", 4);
    Check(fixedRows.Count == 4 && fixedRows.All(record => record.AccountTime!.Value.Day is >= 3 and <= 6), "固定日期规则不可被余额整理移动或删除");
    Near(2000, result.IncomeTotal, 0.01);
    Near(2000, result.FinalBalance, 0.01);
}

static void TestConstraints()
{
    var config = Config(new DateTime(2025, 1, 1), new DateTime(2025, 3, 31), 200000, 5000, 10000);
    var rules = new[] { Rule(1, true, 50, 300, -1), Rule(2, false, 1000, 20000, -2) };
    foreach (var rule in rules)
    {
        rule.TradeWeekend = false;
        rule.StartDay = 15;
        rule.EndDay = 16;
    }
    var result = Generate(config, rules);
    ValidateRows(result, rules);
    Check(result.Records.All(record => record.AccountTime!.Value.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday), "不能在禁用的周末生成");
    Check(result.Records.All(record => record.AccountTime!.Value.Hour is >= 15 and <= 16), "必须保留规则的交易时段");
    Near(15000, result.FinalBalance, 100);
}

static void TestNoInterest()
{
    var bank = Bank();
    var request = new FlowAutoGenerationRequest
    {
        Bank = bank,
        BankUser = new BankUser { Id = 99, BankName = "微信", AutoCalculateInterest = true },
        Config = Config(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), 10000, 1000, 100),
        References = [Rule(1, true, 50, 100, 0), Rule(2, false, 100, 500, 0)],
        ConstItems = [],
        InterestSetting = new BankInterestSetting()
    };
    var result = new FlowAutoGenerator().Generate(request);
    Check(result.Records.All(record => !FlowGeneratedRowKinds.IsInterest(record) && !FlowGeneratedRowKinds.IsInterestTax(record)), "微信不允许生成结息及利息税");
}

static void TestNegativeBalanceContract()
{
    var fixedRules = new[]
    {
        new GenerateConstRule { Index = 1, IsCheck = true, IncomeAttribute = "支出", MinMoney = 50, MaxMoney = 50, FloutLength = 0, FixDay = "1", ReCnt = "1", StartDay = 9, EndDay = 9, TradeWeekend = true },
        new GenerateConstRule { Index = 2, IsCheck = true, IncomeAttribute = "收入", MinMoney = 100, MaxMoney = 100, FloutLength = 0, FixDay = "2", ReCnt = "1", StartDay = 9, EndDay = 9, TradeWeekend = true }
    };
    var result = Generate(Config(new DateTime(2025, 1, 1), new DateTime(2025, 1, 3), 100, 50, 0), [], fixedRules);
    Check(result.Records.Count == 2, "不能为了负余额删掉固定项目");
    Near(0, result.OpeningBalance, 0.01);
    Near(-50, result.MinimumBalance, 0.01);
    Near(50, result.FinalBalance, 0.01);
    Check(result.RequiresOpeningBalanceCorrection, "负余额必须保留修正信号，触发原有 UI 提示");
}

static void TestRoundingTolerance()
{
    var result = Generate(Config(new DateTime(2025, 1, 1), new DateTime(2025, 1, 31), 10003.27, 202.18, 1000),
        [Rule(1, true, 100, 300, -2), Rule(2, false, 100, 500, -2)]);
    Check(result.Records.All(record => Math.Abs(record.TradeMoney!.Value) % 100 == 0), "不能为几分钱尾差捏造不符合精度的交易");
    Near(1202.18, result.FinalBalance, 100);
    Check(result.Records.All(record => Math.Abs(record.TradeMoney!.Value) >= 100), "不能添加超出范围的小额补差记录");
}

static void TestImpossibleRequiredBudget()
{
    try
    {
        Generate(Config(new DateTime(2025, 1, 1), new DateTime(2025, 1, 31), 100, 100, 0),
            [Rule(1, true, 100, 100, 0, 2)]);
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("最低收入/支出"))
    {
        return;
    }
    throw new Exception("必现最低金额超过预算时不能悄悄漏掉必现条数");
}

static void TestMonthlyDetail()
{
    var config = Config(new DateTime(2025, 1, 1), new DateTime(2025, 2, 28), 40000, 5000, 1000);
    config.SelectIndex = 2;
    config.MonthGenData.Add(new MonthGenerateRule { StartTime = new DateTime(2025, 1, 1), EndTime = new DateTime(2025, 1, 31), InMoney = 10000, OutMoney = 5000 });
    config.MonthGenData.Add(new MonthGenerateRule { StartTime = new DateTime(2025, 2, 1), EndTime = new DateTime(2025, 2, 28), InMoney = 30000, OutMoney = 30000 });
    var result = Generate(config, [Rule(1, true, 10, 100, 0), Rule(2, false, 10, 100, 0)]);
    Near(10000, result.Records.Where(record => record.AccountTime!.Value.Month == 1 && record.TradeMoney > 0).Sum(record => record.TradeMoney ?? 0), 100);
    Near(30000, result.Records.Where(record => record.AccountTime!.Value.Month == 2 && record.TradeMoney > 0).Sum(record => record.TradeMoney ?? 0), 100);
    Near(6000, result.FinalBalance, 0.01);
}

static void TestExpenseDominant()
{
    var rules = new List<GenerateReferenceRule>();
    AddRules(rules, true, 2, 10000, 50000, -2);
    AddRules(rules, false, 100, 50, 150, -1);
    var result = Generate(Config(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), 500000, 10000, 1000), rules);
    Check(result.Records.Count(record => record.TradeMoney < 0) / (double)result.Records.Count > 0.95, "反向配置下条数应自然以支出为主");
    Near(11000, result.FinalBalance, 100);
    ValidateRows(result, rules);
}

static void TestUnavailableMonth()
{
    // January 31 is Friday; the included February days are both on the weekend.
    var rules = new[] { Rule(1, true, 10, 100, 0), Rule(2, false, 10, 100, 0) };
    foreach (var rule in rules) rule.TradeWeekend = false;
    var result = Generate(Config(new DateTime(2025, 1, 31), new DateTime(2025, 2, 2), 10000, 1000, 1000), rules);
    Check(result.Records.All(record => record.AccountTime!.Value.Date == new DateTime(2025, 1, 31)), "预算只能分配到允许交易的日期");
    Near(2000, result.FinalBalance, 0.01);
}

static void TestLargeDenominationTolerance()
{
    var result = Generate(Config(new DateTime(2025, 1, 1), new DateTime(2025, 1, 31), 14000, 0, 10000),
        [Rule(1, true, 5000, 5000, 0), Rule(2, false, 5000, 5000, 0)]);
    Check(result.Records.Count == 6 && result.Records.All(record => Math.Abs(record.TradeMoney!.Value) == 5000), "应使用合法面额进入容差，不能生成 4000 或补差条目");
    Near(15000, result.IncomeTotal, 0.01);
    Near(10000, result.FinalBalance, 0.01);
}

static void TestSmallBudget()
{
    var rules = new[]
    {
        Rule(1, true, 50, 100, 0), Rule(2, true, 5000, 10000, 0),
        Rule(3, false, 50, 100, 0), Rule(4, false, 5000, 10000, 0)
    };
    var result = Generate(Config(new DateTime(2025, 1, 1), new DateTime(2025, 1, 31), 500, 100, 1000), rules);
    Near(500, result.IncomeTotal, 0.01);
    Near(400, result.ExpenseTotal, 0.01);
    Near(1100, result.FinalBalance, 0.01);
    ValidateRows(result, rules);
}

static void TestNegativeTimeCorrection()
{
    var config = Config(new DateTime(2025, 1, 1), new DateTime(2025, 1, 3), 100, 50, 0);
    var income = Rule(1, true, 100, 100, 0, 1);
    var expense = Rule(2, false, 50, 50, 0, 1);
    income.StartDay = income.EndDay = 10;
    expense.StartDay = expense.EndDay = 9;
    income.SerialNum = "configured-income-id";
    income.MerchantName = "configured-merchant";
    var bank = Bank();
    bank.FlowColumns.Add(new ColumnDefinition { Name = "交易单号", Field = nameof(FlowRecord.SerialNum) });
    bank.FlowColumns.Add(new ColumnDefinition { Name = "商户单号", Field = nameof(FlowRecord.MerchantName) });
    var request = new FlowAutoGenerationRequest
    {
        Bank = bank, BankUser = new BankUser { Id = 99, BankName = "微信" },
        Config = config, References = [income, expense], ConstItems = []
    };
    var generator = new FlowAutoGenerator();
    var result = generator.Generate(request);
    result.Records.Single(record => record.TradeMoney > 0).AccountTime = new DateTime(2025, 1, 2, 10, 0, 0);
    result.Records.Single(record => record.TradeMoney < 0).AccountTime = new DateTime(2025, 1, 1, 9, 0, 0);
    var corrected = generator.ApplyNegativeBalanceCorrection(request, result);
    Check(corrected.MinimumBalance >= 0, "修正后不能留下负余额导致拒绝保存");
    Check(corrected.Records.Count == 2 && SourceRows(corrected, "Reference", 1).Count == 1 && SourceRows(corrected, "Reference", 2).Count == 1, "不能变更条数或每月必现次数");
    Near(100, corrected.IncomeTotal, 0.01);
    Near(50, corrected.ExpenseTotal, 0.01);
    Near(0, corrected.OpeningBalance, 0.01);
    Near(50, corrected.FinalBalance, 0.01);
    Check(corrected.Records.All(record => record.AccountTime!.Value.Month == 1 && record.AccountTime <= new DateTime(2025, 1, 3, 23, 59, 59)), "时间修正必须在原月份和日期范围内");
    Check(corrected.Records.Single(record => record.TradeMoney > 0).AccountTime!.Value.Hour == 10, "收入必须保留可交易时段");
    Check(corrected.Records.Single(record => record.TradeMoney < 0).AccountTime!.Value.Hour == 9, "支出必须保留可交易时段");
    Check(corrected.Records.Single(record => record.TradeMoney > 0).SerialNum == "configured-income-id"
        && corrected.Records.Single(record => record.TradeMoney > 0).MerchantName == "configured-merchant", "不能覆盖参照中指定的单号");
    var debit = corrected.Records.Single(record => record.TradeMoney < 0);
    var date = debit.AccountTime!.Value.ToString("yyyyMMdd");
    Check(debit.SerialNum!.Substring(10, 8) == date && (debit.MerchantName == "/" || debit.MerchantName!.StartsWith(date)),
        $"移动日期后系统自动生成的单号日期应同步: serial={debit.SerialNum}, merchant={debit.MerchantName}, date={date}");
}

static FlowAutoGenerationResult Generate(FlowGenerationConfig config, IReadOnlyList<GenerateReferenceRule> rules, IReadOnlyList<GenerateConstRule>? constants = null)
{
    var bank = Bank();
    return new FlowAutoGenerator().Generate(new FlowAutoGenerationRequest
    {
        Bank = bank,
        BankUser = new BankUser { Id = 99, BankId = bank.Id, BankName = "微信", AccountName = "测试用户", AutoCalculateInterest = false },
        Config = config, References = rules, ConstItems = constants ?? []
    });
}

static Bank Bank() => new() { Id = 2, Name = "微信", Type = BankTypes.Personal };

static FlowGenerationConfig Config(DateTime start, DateTime end, double income, double last, double opening) => new()
{
    StartTime = start, EndTime = end, AllInMoney = income, LastMoney = last, OpeningBalance = opening,
    MinInMoneyMonth1 = 0, MaxInMoneyMonth1 = 0, MinOutMoneyMonth1 = 0, MaxOutMoneyMonth1 = 0,
    MinInMoneyMonth2 = 0, MaxInMoneyMonth2 = 0, MinOutMoneyMonth2 = 0, MaxOutMoneyMonth2 = 0
};

static GenerateReferenceRule Rule(int index, bool income, double min, double max, int precision, int monthly = 0) => new()
{
    Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出", MinMoney = min, MaxMoney = max,
    FloutLength = precision, PercentMonth = monthly, TradeHoliday = true, TradeWeekend = true,
    StartDay = 9, EndDay = 20, Remark = $"test-{index}"
};

static void AddRules(List<GenerateReferenceRule> rules, bool income, int count, double min, double max, int precision, int monthly = 0)
{
    for (var index = 0; index < count; index++) rules.Add(Rule(rules.Count + 1, income, min, max, precision, monthly));
}

static List<FlowRecord> SourceRows(FlowAutoGenerationResult result, string kind, int index) => result.Records
    .Where(record => record.ExtraFields["__GeneratedSourceKind"] == kind && record.ExtraFields["__GeneratedSourceIndex"] == index.ToString()).ToList();

static void ValidateRows(FlowAutoGenerationResult result, IReadOnlyList<GenerateReferenceRule> rules)
{
    var byIndex = rules.ToDictionary(rule => rule.Index);
    var balance = result.OpeningBalance;
    DateTime? previous = null;
    foreach (var record in result.Records)
    {
        var rule = byIndex[int.Parse(record.ExtraFields["__GeneratedSourceIndex"])];
        var amount = Math.Abs(record.TradeMoney!.Value);
        var unit = Math.Pow(10, -(rule.FloutLength ?? 0));
        Check(amount >= rule.MinMoney - 0.009 && amount <= rule.MaxMoney + 0.009, "金额必须在来源规则范围内");
        Near(Math.Round(amount / unit), amount / unit, 0.000001);
        if (rule.FloutLength != 0) Check((long)Math.Round(amount / unit) % 10 != 0, "指定精度位不能变成 0");
        Check(previous is null || record.AccountTime >= previous, "流水应按时间排序");
        previous = record.AccountTime;
        balance = Math.Round(balance + record.TradeMoney.Value, 2, MidpointRounding.AwayFromZero);
        Near(balance, record.Balance!.Value, 0.01);
    }
}

static void Near(double expected, double actual, double tolerance) => Check(Math.Abs(expected - actual) <= tolerance + 0.000001, $"expected={expected}, actual={actual}, tolerance={tolerance}");
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
