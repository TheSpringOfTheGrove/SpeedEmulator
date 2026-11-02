using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("仅个人中信总进账模式启用，不扩展至对公或月度模式", TestScope),
    ("截图配置30次均有收支，收入约3万，固定支出21366，期初零", TestReported),
    ("固定整数即使设置两位小数仍保留21366", TestFixedPrecision),
    ("三种修正不再为凑一万期末而删掉支出或收入", TestCorrections),
    ("已有普通支出时不额外强塞固定可选支出", TestOtherExpenses),
    ("零支出目标不强行生成可选付款", TestZeroExpense),
    ("没有合法日期或总资金不足时不强行生成不可支付的可选支出", TestUnavailable),
    ("指定字段和编号沿用原规则，输入配置不改写", TestFields),
    ("其他银行不启用中信固定支出覆盖补充", TestIsolation),
    ("实际现金余额通过界面验收而不触发旧期末收口", TestAcceptance)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + exception); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} CITIC tests passed.");
return failed == 0 ? 0 : 1;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) <= .009, $"expected={expected}, actual={actual}");
static FlowAutoGenerationRequest Request(long bankId = 17, string bankName = "中信")
{
    var bank = new Bank { Id = bankId, Name = bankName, Type = BankTypes.Personal };
    foreach (var name in new[] { "ConfigureFlowGenerationColumns", "ConfigureFlowRecordColumns" })
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [bank]);
    GenerateReferenceRule Rule(int index, bool income, double min, double max, bool allowed) => new()
    {
        BankId = bankId, Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出", MinMoney = min, MaxMoney = max,
        FloutLength = 0, StartDay = income ? 16 : 7, EndDay = income ? 18 : 9, PercentMonth = null,
        TradeHoliday = allowed, TradeWeekend = allowed, ProductBrief = index == 1 ? "贷款本息" : index == 2 ? "网银互联跨行转账" : "补助"
    };
    return new FlowAutoGenerationRequest
    {
        Bank = bank, BankUser = new BankUser { Id = 9017, BankId = bankId, AccountNo = "9000000000000017", AutoCalculateInterest = false },
        Config = new FlowGenerationConfig { SelectIndex = 0, StartTime = new DateTime(2025, 1, 1), EndTime = new DateTime(2025, 12, 29),
            OpeningBalance = 0, AllInMoney = 30000, AllOutMoney = 20000, LastMoney = 10000,
            MinInMoneyMonth1 = 2500, MaxInMoneyMonth1 = 2500, MinOutMoneyMonth1 = 1666.67, MaxOutMoneyMonth1 = 1666.67,
            MinInMoneyMonth2 = 2500, MaxInMoneyMonth2 = 2500, MinOutMoneyMonth2 = 1666.67, MaxOutMoneyMonth2 = 1666.67 },
        References = [Rule(1, false, 21366, 21366, true), Rule(2, true, 5000, 12000, false), Rule(3, true, 450, 600, false),
            new GenerateReferenceRule { Index = 4, IsCheck = false, IncomeAttribute = "支出", MinMoney = 1000, MaxMoney = 1200, FloutLength = 2 }],
        ConstItems = [], InterestSetting = BankInterestSettingDefaults.CreateDefault(bank)
    };
}
static string Ordinary(FlowAutoGenerationResult result) => JsonSerializer.Serialize(result.Records.Select(row => new
{ row.AccountTime, row.TradeMoney, row.SerialNum, row.ReceiptNum, row.ProductBrief, row.OppositeAccount, row.Remark, row.ExtraFields }));
static void Validate(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
{
    Near(0, result.OpeningBalance); Near(21366, result.ExpenseTotal);
    Check(result.IncomeTotal is >= 29990 and <= 30000, "不能再把收入压成一万元");
    Near(result.IncomeTotal - 21366, result.FinalBalance);
    Check(result.MinimumBalance >= -.009 && !result.RequiresOpeningBalanceCorrection, "不能出现负余额或补期初要求");
    var balance = result.OpeningBalance;
    foreach (var row in result.Records)
    {
        balance = Math.Round(balance + (row.TradeMoney ?? 0), 2, MidpointRounding.AwayFromZero); Near(balance, row.Balance!.Value);
        var rule = request.References.Single(rule => rule.Index.ToString() == row.ExtraFields["__GeneratedSourceIndex"]);
        Check(rule.IsCheck, "未勾选明细不能生成");
        var amount = Math.Abs(row.TradeMoney ?? 0);
        Check(amount >= rule.MinMoney && amount <= rule.MaxMoney, "金额不能突破参照范围");
        Check(row.AccountTime!.Value.Hour >= rule.StartDay && row.AccountTime.Value.Hour <= rule.EndDay, "交易时段不能突破");
        Check(ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(row.AccountTime.Value, rule), "交易日期不能突破");
    }
}
static void TestScope()
{
    foreach (var id in new long[] { 1, 4, 11, 16, 17, 18, 103, 116 })
    foreach (var type in new[] { BankTypes.Personal, BankTypes.Corporate })
    foreach (var mode in new[] { 0, 1, 2 })
        Check(CiticGenerationPolicy.AppliesTo(new Bank { Id = id, Name = "测试银行", Type = type }, new FlowGenerationConfig { SelectIndex = mode })
            == (id == 17 && type == BankTypes.Personal && mode == 0), "中信范围泄漏");
    Check(!CiticGenerationPolicy.AppliesTo(new Bank { Id = 103, Name = "中信对公", Type = BankTypes.Corporate }, Request().Config), "对公必须保留原分支");
}
static void TestReported()
{
    var counts = new List<int>(); var generator = new FlowAutoGenerator();
    for (var seed = 1001; seed <= 1030; seed++)
    {
        using var scope = new SeedScope(seed); var request = Request(); var before = JsonSerializer.Serialize(request);
        var result = generator.Generate(request); Validate(request, result);
        Check(result.Records.Count is >= 5 and <= 30, "条数应接近真诚，不再固定两条");
        Check(result.Records.Count(row => row.TradeMoney < 0) == 1, "应保留一笔21366固定支出");
        Check(before == JsonSerializer.Serialize(request), "不能改写配置"); counts.Add(result.Records.Count);
    }
    Check(counts.Distinct().Count() > 5 && counts.Average() is > 7 and < 16, "多次生成条数应自然变化");
}
static void TestFixedPrecision()
{
    var request = Request(); request.References[0].FloutLength = 2;
    using var scope = new SeedScope(1001); Validate(request, new FlowAutoGenerator().Generate(request));
}
static void TestCorrections()
{
    using var scope = new SeedScope(1001); var request = Request(); var generator = new FlowAutoGenerator(); var result = generator.Generate(request);
    var before = Ordinary(result);
    foreach (var corrected in new[] { generator.ApplyNegativeBalanceCorrection(request, result), generator.ApplyFinalBalanceCorrection(request, result),
                 generator.ApplyOpeningBalanceCorrection(request, result, 0) })
    { Validate(request, corrected); Check(before == Ordinary(corrected), "重复修正不能删除收支或改写普通流水"); }
}
static void TestOtherExpenses()
{
    var request = Request(); request.References[3].IsCheck = true;
    using var scope = new SeedScope(1001); var result = new FlowAutoGenerator().Generate(request);
    Check(result.Records.Any(row => row.TradeMoney < 0), "有可行支出不能全部跳过");
    Check(result.Records.All(row => row.ExtraFields["__GeneratedSourceIndex"] != "1"), "已有普通支出时不额外强塞固定可选项目");
    Near(0, result.OpeningBalance);
}
static void TestZeroExpense()
{
    var request = Request(); request.Config.LastMoney = request.Config.AllInMoney;
    Check(new FlowAutoGenerator().Generate(request).Records.All(row => row.TradeMoney >= 0), "零支出目标不应注入支出");
}
static void TestUnavailable()
{
    var request = Request(); request.References[0].MinMoney = request.References[0].MaxMoney = 50000;
    var result = new FlowAutoGenerator().Generate(request); Near(0, result.OpeningBalance);
    Check(result.ExpenseTotal == 0, "不可支付的可选支出不强行触发补初");
    request = Request(); request.Config.StartTime = new DateTime(2026, 1, 1); request.Config.EndTime = new DateTime(2026, 1, 4);
    request.References[0].TradeHoliday = request.References[0].TradeWeekend = false;
    foreach (var rule in request.References.Skip(1)) rule.TradeHoliday = rule.TradeWeekend = true;
    result = new FlowAutoGenerator().Generate(request); Check(result.ExpenseTotal == 0, "没有合法日期不能伪造付款");
}
static void TestFields()
{
    var request = Request(); request.References[0].SerialNum = "SYNTHETIC-SERIAL"; request.References[0].ReceiptNum = "SYNTHETIC-RECEIPT";
    request.References[0].Remark = "模拟备注"; request.References[0].OppositeAccount = "TEST-COUNTERPARTY";
    var result = new FlowAutoGenerator().Generate(request); var debit = result.Records.Single(row => row.TradeMoney < 0);
    Check(debit.SerialNum == "SYNTHETIC-SERIAL" && debit.ReceiptNum == "SYNTHETIC-RECEIPT" && debit.Remark == "模拟备注"
        && debit.OppositeAccount == "TEST-COUNTERPARTY", "不能影响中信原字段、编号和备注映射");
}
static void TestIsolation()
{
    foreach (var bank in new[] { (4L, "工行"), (3L, "个人农商"), (16L, "招行") })
    {
        var request = Request(bank.Item1, bank.Item2); var result = new FlowAutoGenerator().Generate(request);
        Check(result.ExpenseTotal == 0, "中信补充支出规则不能改变其他银行");
    }
}
static void TestAcceptance()
{
    var request = Request(); var result = new FlowAutoGenerator().Generate(request);
    var type = typeof(SpeedEmulator.ViewModels.FlowGenerationViewModel); var vm = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
    type.GetField("<Bank>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, request.Bank);
    type.GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, request.Config.Clone());
    object?[] args = [result, request.BankUser, request.InterestSetting, null];
    Check((bool)type.GetMethod("IsGenerationResultAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!, "实际现金余额不应被旧期末目标拒绝: " + args[3]);
    Check(Math.Abs(result.FinalBalance - 10000) > 1000, "确实覆盖固定金额与期末目标无法同时满足的场景");
}
sealed class SeedScope : IDisposable
{
    readonly string? previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    public SeedScope(int seed) => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString(CultureInfo.InvariantCulture));
    public void Dispose() => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous);
}
