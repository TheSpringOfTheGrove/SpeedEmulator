using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("34类银行按最终相邻绝对金额检查，收支合计不变", TestAllBanks),
    ("跨收入支出比较绝对值，并允许固定金额连续出现", TestAbsolute),
    ("每月一次的可变金额仍跨月份打散，次数及日期不改", TestMonthlyOccurrence),
    ("固定金额、精度后仅一个合法值、导入及已保存数据保留", TestImmutable),
    ("整数、一位两位小数、负精度均遵守原范围", TestPrecision),
    ("显式月份及明细期间预算均保持原值", TestBudgets),
    ("余额不能因等额调配变负，期初不自动增加", TestBalance),
    ("调整后的支付宝金额编码与实际金额一致", TestNumbers),
    ("已有结息保持原值且符合重算结果", TestInterest),
    ("窄区间金额被预算锁定时交换合法时间，原金额及结息不变", TestTimePermutation),
    ("总额模式全部命中区间顶端时等额减少一收一支，净额及期初不改", TestNetNeutralFallback),
    ("真实Generate与三种修正均启用且重复执行幂等", TestGenerate),
    ("农商期末无法达到时不再冻结全部可用普通支出", TestRcbReserve),
    ("可实现的期末预留及其它银行预留逻辑保持", TestReserveScope),
    ("无重复时不改金额多重集及调用方对象", TestNoChange),
    ("数学无解的普通重复不得悄悄通过校验", TestImpossible)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + exception); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} adjacent-amount tests passed.");
return failed == 0 ? 0 : 1;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Near(double a, double b) => Check(Math.Abs(a - b) < .009, $"expected={a}, actual={b}");
static GenerateReferenceRule Rule(int index, bool income = false, double min = 10, double max = 200, int precision = 0, int monthly = 0) => new()
{
    Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出", MinMoney = min, MaxMoney = max,
    FloutLength = precision, PercentMonth = monthly, StartDay = 8, EndDay = 22, TradeHoliday = true, TradeWeekend = true
};
static FlowAutoGenerationRequest Request(IReadOnlyList<GenerateReferenceRule> rules, long id = 3, bool interest = false, double opening = 10000)
{
    var bank = new Bank { Id = id, Name = id == 3 ? "个人农商" : id == 1 ? "支付宝" : "模拟银行", Type = id < 100 ? BankTypes.Personal : BankTypes.Corporate };
    foreach (var name in new[] { "ConfigureFlowGenerationColumns", "ConfigureFlowRecordColumns" })
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [bank]);
    return new FlowAutoGenerationRequest { Bank = bank, BankUser = new BankUser { Id = 9000 + id, BankId = id, AccountNo = "9000000000000000", AutoCalculateInterest = interest },
        Config = new FlowGenerationConfig { StartTime = new DateTime(2026, 1, 1), EndTime = new DateTime(2026, 3, 31), OpeningBalance = opening, AllInMoney = 1000 },
        References = rules, ConstItems = [], InterestSetting = BankInterestSettingDefaults.CreateDefault(bank) };
}
static FlowRecord Row(FlowAutoGenerationRequest request, FlowRuleBase rule, int day, double money, int month = 1) =>
    (FlowRecord)typeof(FlowAutoGenerator).GetMethod("CreateRecordFromRule", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [request, rule, request.Bank.ReferenceColumns, new DateTime(2026, month, day, 12, 0, 0), money])!;
static FlowAutoGenerationResult Repair(FlowAutoGenerationRequest request, params FlowRecord[] rows)
{
    var before = JsonSerializer.Serialize(rows); var input = JsonSerializer.Serialize(request);
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, request.Config.OpeningBalance);
    Check(before == JsonSerializer.Serialize(rows) && input == JsonSerializer.Serialize(request), "调用方数据不能改变");
    Check(result.MinimumBalance >= -.009, "修正不能产生负余额"); return result;
}
static void Distinct(FlowAutoGenerationResult result)
{
    var values = result.Records.Select(row => Math.Abs(row.TradeMoney ?? 0)).ToArray();
    Check(values.Zip(values.Skip(1)).All(pair => Math.Abs(pair.First - pair.Second) > .009), "还有相邻金额重复: " + string.Join(',', values));
}
static void TestAllBanks()
{
    var count = 0;
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    foreach (var pair in (IReadOnlyDictionary<string, long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
    {
        var rule = Rule(1); var request = Request([rule], pair.Value);
        var result = Repair(request, Row(request, rule, 1, -100), Row(request, rule, 2, -100), Row(request, rule, 3, -120));
        Distinct(result); Near(320, result.ExpenseTotal); Near(10000, result.OpeningBalance); count++;
    }
    Check(count == 34, "需覆盖所有银行");
}
static void TestAbsolute()
{
    var income = Rule(1, true); var expense = Rule(2); var request = Request([income, expense]);
    var result = Repair(request, Row(request, income, 1, 100), Row(request, expense, 2, -100), Row(request, expense, 3, -120));
    Distinct(result); Near(100, result.IncomeTotal); Near(220, result.ExpenseTotal);
    var fixedRule = Rule(3, false, 3000, 3000); request = Request([fixedRule]);
    result = Repair(request, Row(request, fixedRule, 1, -3000), Row(request, fixedRule, 2, -3000));
    Check(result.Records.All(row => row.TradeMoney == -3000), "固定重复允许保留");
}
static void TestMonthlyOccurrence()
{
    foreach (var income in new[] { false, true })
    {
        var rule = Rule(1, income, 100, 900, monthly: 1); var request = Request([rule]);
        var sign = income ? 1 : -1;
        var rows = Enumerable.Range(1, 3).Select(month => Row(request, rule, 15, sign * 500, month)).ToArray();
        var result = Repair(request, rows); Distinct(result);
        Check(result.Records.Select(row => row.AccountTime).SequenceEqual(rows.Select(row => row.AccountTime)), "月度次数和日期不能改变");
        Near(1500, income ? result.IncomeTotal : result.ExpenseTotal);
    }
}
static void TestImmutable()
{
    var fixedRule = Rule(1, false, 100, 100, 2); var narrow = Rule(2, false, 99.9, 100.1, 0);
    var variable = Rule(3); var request = Request([fixedRule, narrow, variable]);
    var a = Row(request, fixedRule, 1, -100); var b = Row(request, fixedRule, 2, -100);
    var c = Row(request, variable, 3, -100); c.IsDocumentImported = true;
    var d = Row(request, variable, 4, -100); d.Id = 99;
    var result = Repair(request, a, b, c, d, Row(request, narrow, 5, -100)); Check(result.Records.All(row => row.TradeMoney == -100), "不可调整的原值必须保留");
}
static void TestPrecision()
{
    foreach (var precision in new[] { -2, 0, 1, 2 })
    {
        var amount = precision switch { -2 => 1100, 1 => 100.1, 2 => 100.11, _ => 100 };
        var rule = Rule(1, false, precision == -2 ? 100 : 10, precision == -2 ? 3000 : 200, precision);
        var request = Request([rule]); var result = Repair(request, Row(request, rule, 1, -amount), Row(request, rule, 2, -amount));
        Distinct(result); Near(amount * 2, result.ExpenseTotal);
        foreach (var row in result.Records)
        {
            var value = Math.Abs(row.TradeMoney!.Value); Check(value >= rule.MinMoney && value <= rule.MaxMoney, "超出原范围");
            var cents = (long)Math.Round(value * 100); Check(cents % 100 is not (>= 1 and <= 9), "禁止小数尾数");
            if (precision == -2) Check(cents % 10000 == 0 && cents / 10000 % 10 != 0, "百位精度不能改变");
        }
    }
}
static void TestBudgets()
{
    foreach (var mode in new[] { 1, 2 })
    {
        var rule = Rule(1); var request = Request([rule]); request.Config.SelectIndex = mode;
        var rows = new[] { Row(request, rule, 1, -100), Row(request, rule, 2, -100), Row(request, rule, 1, -100, 2), Row(request, rule, 2, -100, 2) };
        var result = Repair(request, rows); Distinct(result);
        foreach (var group in result.Records.GroupBy(row => row.AccountTime!.Value.Month)) Near(200, -group.Sum(row => row.TradeMoney ?? 0));
    }
}
static void TestBalance()
{
    var rule = Rule(1, false, 50, 150); var request = Request([rule], opening: 200);
    var result = Repair(request, Row(request, rule, 1, -100), Row(request, rule, 2, -100)); Distinct(result); Near(200, result.OpeningBalance); Near(0, result.FinalBalance);
}
static void TestNumbers()
{
    var rule = Rule(1); rule.ProductBrief = "花呗"; var request = Request([rule], 1);
    var result = Repair(request, Row(request, rule, 1, -100), Row(request, rule, 2, -100)); Distinct(result);
    foreach (var row in result.Records) Check(row.SerialNum.StartsWith(row.AccountTime!.Value.ToString("yyyyMMdd")), "日期编码错误");
}
static void TestInterest()
{
    var rule = Rule(1, false, 10, 200, 2); var request = Request([rule], interest: true);
    var a = Row(request, rule, 1, -100.11); var b = Row(request, rule, 1, -100.11); b.AccountTime = b.AccountTime!.Value.AddHours(1);
    var result = Repair(request, a, b); Distinct(result);
    var rows = result.Records.Select(row => row.Clone()).ToList();
    BankInterestCalculationService.RecalculatePreservingImported(request.Bank, request.BankUser, request.InterestSetting, rows,
        result.OpeningBalance, request.Config.StartTime, request.Config.EndTime.Date.AddDays(1).AddSeconds(-1));
    Near(result.IncomeTotal, rows.Where(row => row.TradeMoney > 0).Sum(row => row.TradeMoney ?? 0));
}
static void TestGenerate()
{
    var expense = Rule(1, false, 10, 40, 0, 80); var income = Rule(2, true, 200, 1000);
    var request = Request([expense, income]); request.Config.AllInMoney = 1000;
    var generator = new FlowAutoGenerator(); var previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    try
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString(CultureInfo.InvariantCulture));
            var result = generator.Generate(request); Distinct(result); var before = JsonSerializer.Serialize(result.Records);
            foreach (var corrected in new[] { generator.ApplyNegativeBalanceCorrection(request, result), generator.ApplyFinalBalanceCorrection(request, result), generator.ApplyOpeningBalanceCorrection(request, result, result.OpeningBalance) })
            { Distinct(corrected); Check(before == JsonSerializer.Serialize(corrected.Records), "修正应幂等"); }
        }
    }
    finally { Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous); }
}
static void TestTimePermutation()
{
    foreach (var id in new[] { 1L, 3L })
    {
        var income = Rule(1, true, 1.91, 2.19, 2); var expense = Rule(2, false, .91, 1.19, 2);
        var request = Request([income, expense], id, interest: id == 3); request.Config.SelectIndex = 1;
        var a = Row(request, income, 1, 2.19); var b = Row(request, income, 1, 2.19); b.AccountTime = b.AccountTime!.Value.AddHours(1);
        var c = Row(request, expense, 1, -.98); c.AccountTime = c.AccountTime!.Value.AddHours(2);
        var result = Repair(request, a, b, c); Distinct(result);
        var ordinary = result.Records.Where(row => !FlowGeneratedRowKinds.IsSystemInterest(row)).ToArray();
        Check(ordinary.Select(row => row.TradeMoney).Order().SequenceEqual(new double?[] { -.98, 2.19, 2.19 }), "不能改变合法金额多重集");
        Check(ordinary.All(row => row.AccountTime!.Value.Date == a.AccountTime!.Value.Date), "同日可解时不要跨日移动");
        Near(.98, result.ExpenseTotal); Check(result.MinimumBalance >= 0, "时间交换不能制造负余额");
    }
}
static void TestNetNeutralFallback()
{
    var income = Rule(1, true, 5000, 30000, -1); var expense = Rule(2, false, 5000, 30000, -1);
    var request = Request([income, expense], 14, opening: 0); request.Config.AllInMoney = 59980;
    var result = Repair(request, Row(request, income, 1, 29990), Row(request, income, 2, 29990),
        Row(request, expense, 3, -29990), Row(request, expense, 4, -29990));
    Distinct(result); Near(0, result.OpeningBalance); Near(0, result.FinalBalance);
    Near(59970, result.IncomeTotal); Near(59970, result.ExpenseTotal);
    Check(result.Records.Select(row => row.AccountTime!.Value.Day).SequenceEqual(new[] { 1, 2, 3, 4 }), "极端兜底不改日期");
}
static void TestRcbReserve()
{
    var income = Rule(1, true, 100, 100); var fixedExpense = Rule(2, false, 80, 80); var expense = Rule(3, false, 10, 30);
    var request = Request([income, fixedExpense, expense], opening: 0); request.Config.LastMoney = 50;
    var result = Repair(request, Row(request, income, 1, 100), Row(request, fixedExpense, 2, -80), Row(request, expense, 3, -30));
    Near(0, result.OpeningBalance); Check(result.Records.Any(row => row.TradeMoney is <= -10 and >= -20), "余款20元不能全部锁死");
    Check(result.Records.Any(row => row.TradeMoney == -80), "固定项目必须保留");
}
static void TestReserveScope()
{
    foreach (var bank in new[] { 3L, 4L })
    {
        var income = Rule(1, true, 200, 200); var expense = Rule(2, false, 10, 100); var request = Request([income, expense], bank, opening: 0);
        request.Config.LastMoney = 100; var result = Repair(request, Row(request, income, 1, 200), Row(request, expense, 2, -100), Row(request, expense, 3, -100), Row(request, expense, 4, -100));
        Check(result.FinalBalance >= 100, "可满足时仍需预留期末");
    }
}
static void TestNoChange()
{
    var rule = Rule(1); var request = Request([rule]);
    var result = Repair(request, Row(request, rule, 1, -100), Row(request, rule, 2, -110));
    Check(result.Records.Select(row => row.TradeMoney).SequenceEqual(new double?[] { -100, -110 }), "无冲突不应处理");
}
static void TestImpossible()
{
    var rule = Rule(1, false, 100, 101); var request = Request([rule]); request.Config.SelectIndex = 1;
    try { Repair(request, Row(request, rule, 1, -100), Row(request, rule, 2, -100)); }
    catch (InvalidOperationException error) { Check(error.Message.Contains("相邻金额"), "必须解释冲突原因"); return; }
    throw new Exception("无解配置不允许返回重复的普通流水");
}
