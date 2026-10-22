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
    ("微信所有支出生成商户订单号且不影响收入和其他银行", TestWechatExpenseMerchantNumber),
    ("邮政微信转账支出生成外部系统流水且不影响其他银行", TestPostalWechatTransferExternalSystemFlow),
    ("农行六类消费附言生成NA且不影响其他银行和既有规则", TestAgriculturalConsumerPostscript),
    ("微信负余额优先只改时间，保持金额条数及每月次数", TestNegativeTimeCorrection),
    ("微信总进账自动分配有月度起伏且首末月按有效天数", TestAutomaticMonthlyVariation),
    ("微信手动月金额范围不被自动起伏覆盖", TestExplicitMonthlyRange)
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
    Check(debit.SerialNum!.Substring(10, 8) == date && debit.MerchantName!.StartsWith(date),
        $"移动日期后系统自动生成的单号日期应同步: serial={debit.SerialNum}, merchant={debit.MerchantName}, date={date}");
}

static void TestWechatExpenseMerchantNumber()
{
    var apply = typeof(FlowAutoGenerator).GetMethod(
        "ApplyAutoGeneratedSystemFields",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var bank = Bank();
    bank.FlowColumns.Add(new ColumnDefinition { Name = "商户单号", Field = nameof(FlowRecord.MerchantName) });
    var request = new FlowAutoGenerationRequest
    {
        Bank = bank, BankUser = new BankUser { Id = 99, BankName = "微信" },
        Config = new(), References = [], ConstItems = []
    };
    var time = new DateTime(2026, 9, 1, 12, 42, 52);

    var transferExpense = new FlowRecord { AccountTime = time, TradeMoney = -270, ProductName = "转账", MerchantName = "/" };
    apply.Invoke(null, [request, transferExpense]);
    Check(transferExpense.MerchantName.Length == 30
        && transferExpense.MerchantName.StartsWith("20260901")
        && transferExpense.MerchantName.All(char.IsDigit),
        $"微信转账支出必须生成日期开头的30位商户订单号: {transferExpense.MerchantName}");

    var qrExpense = new FlowRecord { AccountTime = time, TradeMoney = -16, ProductName = "二维码收款" };
    apply.Invoke(null, [request, qrExpense]);
    Check(!string.IsNullOrWhiteSpace(qrExpense.MerchantName)
        && qrExpense.MerchantName != "/"
        && qrExpense.MerchantName.All(char.IsDigit),
        $"微信支出不能因二维码场景留下斜杠商户订单号: {qrExpense.MerchantName}");

    var configuredExpense = new FlowRecord
    {
        AccountTime = time, TradeMoney = -88, ProductName = "转账", MerchantName = "configured-expense-id"
    };
    apply.Invoke(null, [request, configuredExpense]);
    Check(configuredExpense.MerchantName == "configured-expense-id", "不能覆盖微信支出中明确配置的有效商户订单号");

    var transferIncome = new FlowRecord { AccountTime = time, TradeMoney = 270, ProductName = "转账" };
    apply.Invoke(null, [request, transferIncome]);
    Check(transferIncome.MerchantName == "/", "微信转账收入原有商户订单号规则必须保持不变");

    var otherBank = new Bank { Id = 900, Name = "测试其他银行", Type = BankTypes.Personal };
    otherBank.FlowColumns.Add(new ColumnDefinition { Name = "商户单号", Field = nameof(FlowRecord.MerchantName) });
    var otherRequest = new FlowAutoGenerationRequest
    {
        Bank = otherBank, BankUser = new BankUser { Id = 100, BankName = otherBank.Name },
        Config = new(), References = [], ConstItems = []
    };
    var otherExpense = new FlowRecord { AccountTime = time, TradeMoney = -270, ProductName = "转账", MerchantName = "/" };
    apply.Invoke(null, [otherRequest, otherExpense]);
    Check(otherExpense.MerchantName == "/", "微信支出规则不能改写其他银行的商户订单号");
}

static void TestPostalWechatTransferExternalSystemFlow()
{
    var apply = typeof(FlowAutoGenerator).GetMethod(
        "ApplyAutoGeneratedSystemFields",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var postalBank = new Bank { Id = 14, Name = "邮政", Type = BankTypes.Personal };
    postalBank.FlowColumns.Add(new ColumnDefinition { Name = "外部系统流水", Field = nameof(FlowRecord.VoucherNum) });
    var postalRequest = new FlowAutoGenerationRequest
    {
        Bank = postalBank, BankUser = new BankUser { Id = 114, BankName = postalBank.Name },
        Config = new(), References = [], ConstItems = []
    };
    var time = new DateTime(2026, 9, 1, 12, 42, 52);

    var wechatTransferExpense = new FlowRecord
    {
        AccountTime = time, TradeMoney = -270, ProductName = "转账", TradeChannel = "微信"
    };
    apply.Invoke(null, [postalRequest, wechatTransferExpense]);
    Check(wechatTransferExpense.VoucherNum.Length == 30
        && wechatTransferExpense.VoucherNum.StartsWith("20260901124252")
        && wechatTransferExpense.VoucherNum.All(char.IsDigit),
        $"邮政微信转账支出必须生成30位外部系统流水: {wechatTransferExpense.VoucherNum}");

    var configuredExpense = new FlowRecord
    {
        AccountTime = time, TradeMoney = -88, ProductName = "微信转账", VoucherNum = "configured-postal-flow"
    };
    apply.Invoke(null, [postalRequest, configuredExpense]);
    Check(configuredExpense.VoucherNum == "configured-postal-flow", "不能覆盖邮政规则中已配置的外部系统流水");

    var wechatTransferIncome = new FlowRecord { AccountTime = time, TradeMoney = 270, ProductName = "微信转账" };
    apply.Invoke(null, [postalRequest, wechatTransferIncome]);
    Check(string.IsNullOrWhiteSpace(wechatTransferIncome.VoucherNum), "邮政微信转账收入不能生成外部系统流水");

    var ordinaryTransferExpense = new FlowRecord { AccountTime = time, TradeMoney = -270, ProductName = "转账" };
    apply.Invoke(null, [postalRequest, ordinaryTransferExpense]);
    Check(string.IsNullOrWhiteSpace(ordinaryTransferExpense.VoucherNum), "邮政普通转账支出应保持原规则");

    var repaymentExpense = new FlowRecord
    {
        AccountTime = time, TradeMoney = -270, ProductName = "微信转账", Remark = "贷款还款"
    };
    apply.Invoke(null, [postalRequest, repaymentExpense]);
    Check(string.IsNullOrWhiteSpace(repaymentExpense.VoucherNum), "邮政微信转账中的还款类支出不能误生成");

    var merchantExpense = new FlowRecord { AccountTime = time, TradeMoney = -16, ProductName = "微信扫码支付" };
    apply.Invoke(null, [postalRequest, merchantExpense]);
    Check(merchantExpense.VoucherNum.Length == 30, "邮政原有消费类支出规则不能受影响");

    var otherBank = new Bank { Id = 914, Name = "测试其他银行", Type = BankTypes.Personal };
    otherBank.FlowColumns.Add(new ColumnDefinition { Name = "外部系统流水", Field = nameof(FlowRecord.VoucherNum) });
    var otherRequest = new FlowAutoGenerationRequest
    {
        Bank = otherBank, BankUser = new BankUser { Id = 115, BankName = otherBank.Name },
        Config = new(), References = [], ConstItems = []
    };
    var otherWechatTransferExpense = new FlowRecord
    {
        AccountTime = time, TradeMoney = -270, ProductName = "转账", TradeChannel = "微信"
    };
    apply.Invoke(null, [otherRequest, otherWechatTransferExpense]);
    Check(string.IsNullOrWhiteSpace(otherWechatTransferExpense.VoucherNum), "邮政微信转账规则不能影响其他银行");

    var corporateBank = new Bank { Id = 114, Name = "邮政对公", Type = BankTypes.Corporate };
    corporateBank.FlowColumns.Add(new ColumnDefinition { Name = "外部系统流水", Field = nameof(FlowRecord.VoucherNum) });
    var corporateRequest = new FlowAutoGenerationRequest
    {
        Bank = corporateBank, BankUser = new BankUser { Id = 116, BankName = corporateBank.Name },
        Config = new(), References = [], ConstItems = []
    };
    var corporateWechatTransferExpense = new FlowRecord
    {
        AccountTime = time, TradeMoney = -270, ProductName = "转账", TradeChannel = "微信"
    };
    apply.Invoke(null, [corporateRequest, corporateWechatTransferExpense]);
    Check(string.IsNullOrWhiteSpace(corporateWechatTransferExpense.VoucherNum), "个人邮政规则不能影响邮政对公");
}

static void TestAgriculturalConsumerPostscript()
{
    var apply = typeof(FlowAutoGenerator).GetMethod(
        "ApplyAutoGeneratedSystemFields",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var vendorBridge = typeof(ZhenchengFlowGenerationAdapter).GetNestedType(
        "VendorBridge",
        System.Reflection.BindingFlags.NonPublic)!;
    var createVendorPostscript = vendorBridge.GetMethod(
        "CreateAgriculturalPostscript",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var time = new DateTime(2026, 9, 1, 12, 42, 52);
    var bank = new Bank { Id = 11, Name = "农行", Type = BankTypes.Personal };
    bank.FlowColumns.Add(new ColumnDefinition { Name = "附言", Field = nameof(FlowRecord.Remark) });
    var request = new FlowAutoGenerationRequest
    {
        Bank = bank,
        BankUser = new BankUser { Id = 111, BankName = bank.Name },
        Config = new(),
        References = [],
        ConstItems = []
    };

    foreach (var summary in new[] { "微信支付", "抖音支付", "支付宝", "美团支付", "多多支付", "银联支付" })
    {
        var original = "原附言-" + summary;
        var row = new FlowRecord
        {
            AccountTime = time,
            ProductBrief = summary,
            CashCheck = "消费",
            Remark = original
        };
        apply.Invoke(null, [request, row]);
        CheckAgriculturalNa(row.Remark, time, original, "本地生成/" + summary);

        var vendorRow = new FlowRecord
        {
            AccountTime = time,
            ProductBrief = summary,
            CashCheck = "消费",
            Remark = original
        };
        var vendorValue = (string)createVendorPostscript.Invoke(null, [bank, vendorRow, original])!;
        CheckAgriculturalNa(vendorValue, time, original, "真诚兼容生成/" + summary);
    }

    var blankRemark = new FlowRecord
    {
        AccountTime = time,
        ProductBrief = "微信支付",
        CashCheck = "消费",
        Remark = string.Empty
    };
    apply.Invoke(null, [request, blankRemark]);
    CheckAgriculturalNa(blankRemark.Remark, time, string.Empty, "目标消费空附言");

    var nonConsumerCases = new[]
    {
        new FlowRecord { AccountTime = time, ProductBrief = "抖音支付", CashCheck = "转账", Remark = "抖音原附言" },
        new FlowRecord { AccountTime = time, ProductBrief = "美团支付", CashCheck = "转账", Remark = "美团原附言" },
        new FlowRecord { AccountTime = time, ProductBrief = "银联支付", CashCheck = "", Remark = "银联原附言" }
    };
    foreach (var row in nonConsumerCases)
    {
        var original = row.Remark;
        apply.Invoke(null, [request, row]);
        Check(!row.Remark.StartsWith("NA", StringComparison.Ordinal), $"非消费不能进入新增NA规则: {row.ProductBrief}/{row.CashCheck}/{row.Remark}");

        var vendorRow = new FlowRecord
        {
            AccountTime = row.AccountTime,
            ProductBrief = row.ProductBrief,
            CashCheck = row.CashCheck,
            Remark = original
        };
        var vendorValue = (string)createVendorPostscript.Invoke(null, [bank, vendorRow, original])!;
        Check(!vendorValue.StartsWith("NA", StringComparison.Ordinal), $"真诚兼容生成的非消费不能进入新增NA规则: {row.ProductBrief}/{row.CashCheck}/{vendorValue}");
    }

    foreach (var summary in new[] { "微信支付退款", "抖音支付退款", "美团支付订单", "银联支付退款" })
    {
        var row = new FlowRecord
        {
            AccountTime = time,
            ProductBrief = summary,
            CashCheck = "消费",
            Remark = "相似摘要原附言"
        };
        apply.Invoke(null, [request, row]);
        Check(!row.Remark.StartsWith("NA", StringComparison.Ordinal), $"相似摘要不能进入精确NA规则: {summary}/{row.Remark}");
    }

    var ua = new FlowRecord
    {
        AccountTime = time,
        ProductBrief = "财付通",
        CashCheck = "消费",
        Remark = "财付通原附言"
    };
    apply.Invoke(null, [request, ua]);
    CheckAgriculturalUa(ua.Remark, time, "财付通原附言", "财付通仍使用UA");

    var ng = new FlowRecord
    {
        AccountTime = time,
        ProductBrief = "代付",
        CashCheck = "转账",
        Remark = "零钱提现"
    };
    apply.Invoke(null, [request, ng]);
    CheckAgriculturalLongPrefix(ng.Remark, "NG", time, "零钱提现", "零钱提现仍使用NG");

    const string generatedUa = "UA0901123456789012已生成附言";
    var existingPrefix = new FlowRecord
    {
        AccountTime = time,
        ProductBrief = "微信支付",
        CashCheck = "消费",
        Remark = generatedUa
    };
    apply.Invoke(null, [request, existingPrefix]);
    Check(existingPrefix.Remark == generatedUa, "已有合法UA/NA/NG前缀不能被重复改写");

    var corporateBank = new Bank { Id = 105, Name = "农行对公", Type = BankTypes.Corporate };
    corporateBank.FlowColumns.Add(new ColumnDefinition { Name = "附言", Field = nameof(FlowRecord.Remark) });
    var corporateRequest = new FlowAutoGenerationRequest
    {
        Bank = corporateBank,
        BankUser = new BankUser { Id = 1105, BankName = corporateBank.Name },
        Config = new(),
        References = [],
        ConstItems = []
    };
    var corporateRow = new FlowRecord
    {
        AccountTime = time,
        ProductBrief = "微信支付",
        CashCheck = "消费",
        Remark = "对公原附言"
    };
    apply.Invoke(null, [corporateRequest, corporateRow]);
    CheckAgriculturalUa(corporateRow.Remark, time, "对公原附言", "农行对公保持原UA规则");

    foreach (var bankName in new[] { "微信", "支付宝", "招行", "工行", "建行", "交行", "邮政", "广发" })
    {
        var otherBank = new Bank { Id = 900, Name = bankName, Type = BankTypes.Personal };
        otherBank.FlowColumns.Add(new ColumnDefinition { Name = "附言", Field = nameof(FlowRecord.Remark) });
        var otherRequest = new FlowAutoGenerationRequest
        {
            Bank = otherBank,
            BankUser = new BankUser { Id = 1900, BankName = otherBank.Name },
            Config = new(),
            References = [],
            ConstItems = []
        };
        var original = "其他银行原附言-" + bankName;
        var row = new FlowRecord
        {
            AccountTime = time,
            ProductBrief = "微信支付",
            CashCheck = "消费",
            Remark = original
        };
        apply.Invoke(null, [otherRequest, row]);
        Check(row.Remark == original, $"新增农行规则不能改写{bankName}附言: {row.Remark}");
    }
}

static void CheckAgriculturalNa(string? value, DateTime time, string original, string label)
{
    CheckAgriculturalLongPrefix(value, "NA", time, original, label);
}

static void CheckAgriculturalLongPrefix(string? value, string prefix, DateTime time, string original, string label)
{
    var expectedStart = prefix + time.ToString("yyyyMMdd");
    Check(value is not null
        && value.StartsWith(expectedStart, StringComparison.Ordinal)
        && value.Length == expectedStart.Length + 23 + original.Length
        && value.AsSpan(expectedStart.Length, 23).ToArray().All(char.IsDigit)
        && value.EndsWith(original, StringComparison.Ordinal),
        $"{label}格式错误: {value}");
}

static void CheckAgriculturalUa(string? value, DateTime time, string original, string label)
{
    var expectedStart = "UA" + time.ToString("MMdd");
    Check(value is not null
        && value.StartsWith(expectedStart, StringComparison.Ordinal)
        && value.Length == expectedStart.Length + 12 + original.Length
        && value.AsSpan(expectedStart.Length, 12).ToArray().All(char.IsDigit)
        && value.EndsWith(original, StringComparison.Ordinal),
        $"{label}格式错误: {value}");
}

static void TestAutomaticMonthlyVariation()
{
    var config = Config(new DateTime(2025, 9, 18), new DateTime(2026, 9, 18), 5800000, 10000, 3000);
    config.MinInMoneyMonth1 = config.MaxInMoneyMonth1 = 446153.85;
    config.MinOutMoneyMonth1 = config.MaxOutMoneyMonth1 = 445384.62;
    var rules = new[] { Rule(1, true, 50, 300, 0), Rule(2, false, 1000, 50000, -2) };
    for (var run = 0; run < 3; run++)
    {
        var result = Generate(config, rules);
        var monthly = result.Records.GroupBy(record => new DateTime(record.AccountTime!.Value.Year, record.AccountTime.Value.Month, 1))
            .OrderBy(group => group.Key).Select(group => new
            {
                Month = group.Key,
                Income = group.Where(record => record.TradeMoney > 0).Sum(record => record.TradeMoney ?? 0),
                Expense = -group.Where(record => record.TradeMoney < 0).Sum(record => record.TradeMoney ?? 0),
                Closing = group.Last().Balance ?? 0
            }).ToArray();
        var fullMonths = monthly.Skip(1).SkipLast(1).ToArray();
        var median = fullMonths.Select(month => month.Income).Order().ElementAt(fullMonths.Length / 2);
        Check(fullMonths.Max(month => month.Income) - fullMonths.Min(month => month.Income) > median * 0.35,
            "完整月份不能继续均摊");
        Check(fullMonths.Max(month => month.Expense) - fullMonths.Min(month => month.Expense) > median * 0.30,
            "支出也必须跟随月度活动起伏");
        Check(monthly[0].Income < median && monthly[^1].Income < median, "首末不完整月份应按有效天数降低");
        Check(fullMonths.Any(month => month.Income < month.Expense) && fullMonths.Any(month => month.Income > month.Expense),
            "收支差额不能每月都机械地相同");
        Check(monthly.All(month => month.Closing >= -0.009), "自动分配不能制造月末资金缺口");
        Near(5800000, result.IncomeTotal, 1);
        Near(5790000, result.ExpenseTotal, 100);
        Near(13000, result.FinalBalance, 100);
        ValidateRows(result, rules);
        Console.WriteLine($"  monthly run={run + 1}, incomeMin={fullMonths.Min(month => month.Income):F0}, incomeMax={fullMonths.Max(month => month.Income):F0}, " +
            $"first={monthly[0].Income:F0}, last={monthly[^1].Income:F0}, final={result.FinalBalance:F2}");
        if (result.MinimumBalance < -0.009)
        {
            var corrected = new FlowAutoGenerator().ApplyNegativeBalanceCorrection(new FlowAutoGenerationRequest
            {
                Bank = Bank(), BankUser = new BankUser { Id = 99, BankName = "微信" },
                Config = config, References = rules, ConstItems = []
            }, result);
            Check(corrected.MinimumBalance >= -0.009, "起伏分配后的负余额仍应能修正");
            Check(corrected.Records.Count == result.Records.Count, "时间修正不应改动条数");
            foreach (var month in monthly)
            {
                var correctedMonth = corrected.Records.Where(record => record.AccountTime!.Value.Year == month.Month.Year
                    && record.AccountTime.Value.Month == month.Month.Month).ToArray();
                Near(month.Income, correctedMonth.Where(record => record.TradeMoney > 0).Sum(record => record.TradeMoney ?? 0), 0.01);
                Near(month.Expense, -correctedMonth.Where(record => record.TradeMoney < 0).Sum(record => record.TradeMoney ?? 0), 0.01);
            }
        }
    }
}

static void TestExplicitMonthlyRange()
{
    var config = Config(new DateTime(2025, 1, 1), new DateTime(2025, 3, 31), 30000, 0, 1000);
    config.SelectIndex = 1;
    config.MinInMoneyMonth2 = config.MaxInMoneyMonth2 = 10000;
    config.MinOutMoneyMonth2 = config.MaxOutMoneyMonth2 = 10000;
    var result = Generate(config, [Rule(1, true, 10, 100, 0), Rule(2, false, 10, 100, 0)]);
    foreach (var month in result.Records.GroupBy(record => record.AccountTime!.Value.Month))
    {
        Near(10000, month.Where(record => record.TradeMoney > 0).Sum(record => record.TradeMoney ?? 0), 1);
        Near(10000, -month.Where(record => record.TradeMoney < 0).Sum(record => record.TradeMoney ?? 0), 100);
    }
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
