using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("总资金足够只调整普通支出日期，期初不改", TestTiming),
    ("固定日支出可由普通收入提前覆盖，期初不改", TestAdvanceIncome),
    ("不能把后月收入更换参照提前，固定硬缺口仅补最小期初", TestAlternativeIncome),
    ("入账预算尚有尾差时先补齐合法收入，不为随机尾差改期初", TestIncomeTail),
    ("普通支出总额超过预算时调整笔数金额，不抬期初", TestVariableBudget),
    ("未来固定项目先预留，普通消费不能挤占后再补期初", TestReservation),
    ("固定日期固定金额确实无解时仅补最小缺口", TestHardException),
    ("固定日可变金额优先合法重抽，避免补期初", TestVariableConst),
    ("只允许在原月份及交易时段内调度", TestWindows),
    ("建行专属周末日期限制保留", TestCcbDates),
    ("支付宝日期号码同步，显式字段及调用方原数据保留", TestIdentifiers),
    ("农行附言日期随合法调度同步，原附言不丢失", TestPostscript),
    ("导入金额日期保留，只为不可调整的硬缺口补初", TestImported),
    ("34类银行统一先排时间，普通资金不足不自动补初", TestAllBanks),
    ("实际生成30次不因随机先支出而修改期初，重复修正幂等", TestGenerated),
    ("带结息的调度结果按原期初重新计息", TestInterest)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + exception); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} cashflow tests passed.");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < .009, $"expected={expected}, actual={actual}");
static GenerateReferenceRule Ref(int index, bool income, double min = 1, double max = 1000, int monthly = 0) => new()
{ Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出", MinMoney = min, MaxMoney = max, PercentMonth = monthly,
    FloutLength = 0, StartDay = 9, EndDay = 20, TradeHoliday = true, TradeWeekend = true };
static GenerateConstRule Fixed(int index, bool income, double min, double max, string day) => new()
{ Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出", MinMoney = min, MaxMoney = max, FixDay = day,
    FloutLength = 0, StartDay = 9, EndDay = 20, TradeHoliday = true, TradeWeekend = true };
static FlowAutoGenerationRequest Request(IReadOnlyList<GenerateReferenceRule> refs, IReadOnlyList<GenerateConstRule>? fixedItems = null,
    long bankId = 4, string name = "工行", bool interest = false, double opening = 0)
{
    var bank = new Bank { Id = bankId, Name = name, Type = bankId < 100 ? BankTypes.Personal : BankTypes.Corporate };
    foreach (var method in new[] { "ConfigureFlowGenerationColumns", "ConfigureFlowRecordColumns" })
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [bank]);
    return new FlowAutoGenerationRequest { Bank = bank, BankUser = new BankUser { Id = 9000 + bankId, BankId = bankId, AccountNo = "9000000000000000", AutoCalculateInterest = interest },
        Config = new FlowGenerationConfig { StartTime = new DateTime(2026, 1, 1), EndTime = new DateTime(2026, 1, 31), AllInMoney = 1000, LastMoney = 0, OpeningBalance = opening },
        References = refs, ConstItems = fixedItems ?? [], InterestSetting = BankInterestSettingDefaults.CreateDefault(bank) };
}
static FlowRecord Row(FlowAutoGenerationRequest request, FlowRuleBase rule, int day, double money, int hour = 10) =>
    (FlowRecord)typeof(FlowAutoGenerator).GetMethod("CreateRecordFromRule", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [request, rule, rule is GenerateConstRule ? request.Bank.ConstColumns : request.Bank.ReferenceColumns, new DateTime(2026, 1, day, hour, 0, 0), money])!;
static FlowAutoGenerationResult Repair(FlowAutoGenerationRequest request, params FlowRecord[] rows)
{
    var before = JsonSerializer.Serialize(rows); var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, request.Config.OpeningBalance);
    Check(before == JsonSerializer.Serialize(rows), "不能修改调用方流水"); Validate(result); return result;
}
static void Validate(FlowAutoGenerationResult result)
{
    Check(result.MinimumBalance >= -.009 && !result.RequiresOpeningBalanceCorrection, "生成后仍有负余额");
    var balance = result.OpeningBalance;
    foreach (var row in result.Records) { balance = Math.Round(balance + (row.TradeMoney ?? 0), 2, MidpointRounding.AwayFromZero); Near(balance, row.Balance!.Value); }
    Near(balance, result.FinalBalance);
}
static void TestTiming()
{
    var expense = Ref(1, false); var income = Fixed(2, true, 200, 200, "15"); var request = Request([expense], [income]);
    var result = Repair(request, Row(request, expense, 2, -100), Row(request, income, 15, 200));
    Near(0, result.OpeningBalance); Near(100, result.ExpenseTotal); Check(result.Records.Count == 2, "有可用日期不应删流水");
    Check(result.Records.Single(r => r.TradeMoney < 0).AccountTime >= result.Records.Single(r => r.TradeMoney > 0).AccountTime, "应延后支出");
}
static void TestAdvanceIncome()
{
    var income = Ref(1, true); var expense = Fixed(2, false, 100, 100, "2"); var request = Request([income], [expense]);
    var result = Repair(request, Row(request, expense, 2, -100), Row(request, income, 15, 200));
    Near(0, result.OpeningBalance); Check(result.Records.Single(r => r.TradeMoney > 0).AccountTime < new DateTime(2026, 1, 2, 10, 0, 0), "只提前必要的可变收入");
    Check(result.Records.Single(r => r.TradeMoney < 0).AccountTime == new DateTime(2026, 1, 2, 10, 0, 0), "固定日期不得变化");
}
static void TestVariableBudget()
{
    var income = Fixed(1, true, 1000, 1000, "1"); var expense = Ref(2, false, 100, 500); var request = Request([expense], [income]); request.Config.LastMoney = 100;
    var rows = Enumerable.Range(2, 20).Select(day => Row(request, expense, day, -500)).Append(Row(request, income, 1, 1000)).ToArray();
    var result = Repair(request, rows); Near(0, result.OpeningBalance);
    Check(result.Records.Count < rows.Length && result.ExpenseTotal <= 900 && result.FinalBalance >= 100, "普通支出必须服从可用资金，不强凑次数");
    Check(result.Records.Where(r => r.TradeMoney < 0).All(r => -r.TradeMoney is >= 100 and <= 500), "不突破来源范围");
}
static void TestAlternativeIncome()
{
    var later = Ref(1, true, 1000, 30000, 1); var optional = Ref(2, true, 10000, 50000);
    var expense = Fixed(3, false, 1000, 1000, "2"); var request = Request([later, optional], [expense]);
    request.Config.EndTime = new DateTime(2026, 2, 28);
    var income = Row(request, later, 15, 20000); income.AccountTime = new DateTime(2026, 2, 15, 10, 0, 0);
    var result = Repair(request, Row(request, expense, 2, -1000), income);
    Near(1000, result.OpeningBalance); Near(20000, result.IncomeTotal);
    var retained = result.Records.Single(r => r.TradeMoney > 0);
    Check(retained.ExtraFields["__GeneratedSourceIndex"] == "1" && retained.AccountTime!.Value.Month == 2,
        "后月按月收入必须保留来源及月份，不能更换可选参照搬到前月");
}
static void TestIncomeTail()
{
    var income = Ref(1, true, 500, 2000); var expense = Fixed(2, false, 1000, 1000, "2");
    var request = Request([income], [expense]);
    var result = Repair(request, Row(request, income, 1, 995), Row(request, expense, 2, -1000));
    Near(0, result.OpeningBalance); Near(1000, result.IncomeTotal);
}
static void TestReservation()
{
    var income = Fixed(1, true, 100, 100, "1"); var fixedExpense = Fixed(2, false, 100, 100, "20"); var expense = Ref(3, false, 10, 100);
    var request = Request([expense], [income, fixedExpense]); var result = Repair(request, Row(request, income, 1, 100), Row(request, expense, 5, -80), Row(request, fixedExpense, 20, -100));
    Near(0, result.OpeningBalance); Near(100, result.ExpenseTotal); Check(result.Records.Count == 2, "必须为未来固定付款预留现金");
}
static void TestHardException()
{
    var income = Fixed(1, true, 1000, 1000, "15"); var expense = Fixed(2, false, 500, 500, "2"); var request = Request([], [income, expense]);
    var result = Repair(request, Row(request, income, 15, 1000), Row(request, expense, 2, -500)); Near(500, result.OpeningBalance);
    Check(result.Records.Single(r => r.TradeMoney < 0).AccountTime!.Value.Day == 2 && result.Records.Count == 2, "固定项目不能改变");
    var again = new FlowAutoGenerator().ApplyNegativeBalanceCorrection(request, result); Near(500, again.OpeningBalance);
}
static void TestVariableConst()
{
    var income = Fixed(1, true, 100, 100, "1"); var expense = Fixed(2, false, 50, 200, "2"); var request = Request([], [income, expense]);
    var result = Repair(request, Row(request, income, 1, 100), Row(request, expense, 2, -180)); Near(0, result.OpeningBalance);
    Check(result.Records.Single(r => r.TradeMoney < 0).TradeMoney is >= -100 and <= -50, "固定日可变金额先合法重抽");
}
static void TestWindows()
{
    var income = Ref(1, true, 100, 1000, 1); income.StartDay = 12; income.EndDay = 16;
    var expense = Fixed(2, false, 100, 100, "1"); var request = Request([income], [expense]);
    var result = Repair(request, Row(request, expense, 1, -100, 9), Row(request, income, 15, 200, 12)); Near(100, result.OpeningBalance);
    Check(result.Records.Single(r => r.TradeMoney > 0).AccountTime!.Value.Hour >= 12, "不能伪造许可时段之前的收入");
}
static void TestCcbDates()
{
    var income = Fixed(1, true, 200, 200, "5"); var expense = Ref(2, false); expense.TradeWeekend = true; expense.TradeHoliday = false;
    var request = Request([expense], [income], 8, "建行"); var result = Repair(request, Row(request, expense, 4, -100), Row(request, income, 5, 200));
    Near(0, result.OpeningBalance); Check(result.Records.Single(r => r.TradeMoney < 0).AccountTime!.Value.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday, "建行仍只能落允许日期");
}
static void TestIdentifiers()
{
    var income = Fixed(1, true, 200, 200, "15"); var expense = Ref(2, false); expense.ProductBrief = "普通消费";
    var request = Request([expense], [income], 1, "支付宝"); var result = Repair(request, Row(request, expense, 2, -100), Row(request, income, 15, 200)); Near(0, result.OpeningBalance);
    foreach (var row in result.Records) Check(row.SerialNum.StartsWith(row.AccountTime!.Value.ToString("yyyyMMdd")), "日期号码不一致");
    expense.SerialNum = "SYNTHETIC-USER-SERIAL"; expense.MerchantName = "SYNTHETIC-MERCHANT";
    request = Request([expense], [income], 1, "支付宝");
    result = Repair(request, Row(request, expense, 2, -100), Row(request, income, 15, 200));
    Check(result.Records.Single(r => r.TradeMoney < 0).SerialNum == expense.SerialNum, "用户指定号码不得覆盖");
}
static void TestImported()
{
    var request = Request([]); var imported = new FlowRecord { IsDocumentImported = true, BankId = 4, BankUserId = request.BankUser.Id, AccountTime = new DateTime(2026, 1, 1), TradeMoney = -18.01, SerialNum = "original" };
    var result = Repair(request, imported); Near(18.01, result.OpeningBalance);
    Check(result.Records.Single().TradeMoney == -18.01 && result.Records.Single().SerialNum == "original" && result.Records.Single().AccountTime == imported.AccountTime, "导入原值保留");
}
static void TestPostscript()
{
    var income = Fixed(1, true, 1000, 1000, "15"); var expense = Ref(2, false);
    expense.ProductBrief = "微信支付"; expense.ProductType = "消费"; expense.Remark = "模拟原附言";
    var request = Request([expense], [income], 11, "农行");
    var result = Repair(request, Row(request, expense, 2, -100), Row(request, income, 15, 1000)); Near(0, result.OpeningBalance);
    var debit = result.Records.Single(row => row.TradeMoney < 0);
    Check(debit.Remark.StartsWith("NA" + debit.AccountTime!.Value.ToString("yyyyMMdd")), "附言仍包含旧日期");
    Check(debit.Remark.EndsWith("模拟原附言"), "原附言不得丢失");
}
static void TestAllBanks()
{
    var count = 0;
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    foreach (var pair in (IReadOnlyDictionary<string, long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
    {
        var income = Fixed(1, true, 200, 200, "15"); var expense = Ref(2, false); var request = Request([expense], [income], pair.Value, pair.Key);
        var result = Repair(request, Row(request, expense, 2, -100), Row(request, income, 15, 200)); Near(0, result.OpeningBalance); count++;
    }
    Check(count == 34, "必须覆盖全部银行");
}
static void TestGenerated()
{
    var income = Ref(1, true, 100, 1000); var expense = Ref(2, false, 10, 200, 20); var request = Request([income, expense]); request.Config.AllInMoney = 10000; request.Config.LastMoney = 1000;
    var previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    try
    {
        for (var seed = 1001; seed <= 1030; seed++)
        {
            Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString(CultureInfo.InvariantCulture));
            var generator = new FlowAutoGenerator(); var result = generator.Generate(request); Validate(result); Near(0, result.OpeningBalance);
            var snapshot = JsonSerializer.Serialize(result.Records); var corrected = generator.ApplyNegativeBalanceCorrection(request, result);
            Near(0, corrected.OpeningBalance); Check(snapshot == JsonSerializer.Serialize(corrected.Records), "无负余额时重复修正应幂等");
        }
    }
    finally { Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous); }
}
static void TestInterest()
{
    var income = Fixed(1, true, 1000, 1000, "15"); var expense = Ref(2, false, 10, 1000); var request = Request([expense], [income], interest: true);
    request.Config.EndTime = new DateTime(2026, 3, 31);
    var result = Repair(request, Row(request, expense, 2, -100), Row(request, income, 15, 1000)); Near(0, result.OpeningBalance);
    var rows = result.Records.Select(r => r.Clone()).ToList();
    BankInterestCalculationService.RecalculatePreservingImported(request.Bank, request.BankUser, request.InterestSetting, rows, 0, request.Config.StartTime, request.Config.EndTime);
    Near(result.IncomeTotal, rows.Where(r => r.TradeMoney > 0).Sum(r => r.TradeMoney ?? 0));
}
