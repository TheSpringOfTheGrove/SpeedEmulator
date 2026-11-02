using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var fixtures = JsonSerializer.Deserialize<List<Fixture>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reported-configs.json")))!;
var tests = new List<(string Name, Action Run)>
{
    ("34类银行及三个模式精确隔离", TestScope),
    ("预算尾差由可变参照收入吸收，固定金额不变", TestIncomeCapacity),
    ("原有字段和显式流水号经生成与三种修正后保留", TestFields),
    ("新增银行自然结果均可通过界面校验", () => TestAcceptance(fixtures)),
    ("两位小数保持非零末位，结息导入原值保留", TestImported),
    ("周末节假日筛选不会重抽补满，固定日期按原规则", TestDates),
    ("支付宝自动流水号保持最终交易日期且收入不被搬到首日", TestAlipayDates)
};
var summaries = new List<object>();
foreach (var fixture in fixtures) tests.Add(($"{fixture.Bank.Name}原配置30次生成及余额修正", () => TestFixture(fixture, summaries)));
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + exception); }
}
Directory.CreateDirectory("tmp/natural-rollout-20261101");
File.WriteAllText("tmp/natural-rollout-20261101/matrix-verification.json", JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{tests.Count - failed}/{tests.Count} natural-generation tests passed.");
return failed == 0 ? 0 : 1;

static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
static void Near(double a, double b) => Check(Math.Abs(a - b) <= .009, $"expected={a}, actual={b}");
static string Kind(FlowRecord row) => row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind", "");
static bool Ordinary(FlowRecord row) => !FlowGeneratedRowKinds.IsSystemInterest(row);
static string Snapshot(FlowAutoGenerationResult result) => JsonSerializer.Serialize(result.Records.Where(Ordinary).Select(row => new
{ row.Id, row.BankId, row.BankUserId, row.AccountTime, row.TradeMoney, row.SerialNum, row.ProductBrief, row.OppositeAccount, row.OppositeBank, row.ExtraFields }));
static FlowAutoGenerationRequest Request(Fixture fixture, bool interest = false)
{
    var bank = new Bank { Id = fixture.Bank.Id, Name = fixture.Bank.Name, Type = fixture.Bank.Type };
    foreach (var name in new[] { "ConfigureFlowGenerationColumns", "ConfigureFlowRecordColumns" })
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [bank]);
    return new FlowAutoGenerationRequest
    {
        Bank = bank, BankUser = new BankUser { Id = 9000 + bank.Id, BankId = bank.Id, AccountNo = "9000000000000000", CardNo = "9000000000000000", AutoCalculateInterest = interest },
        Config = fixture.Config.Clone(), References = fixture.References.Select(rule => rule.Clone()).ToList(),
        ConstItems = fixture.ConstItems.Select(rule => rule.Clone()).ToList(), InterestSetting = BankInterestSettingDefaults.CreateDefault(bank)
    };
}
static void Validate(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
{
    Check(result.MinimumBalance >= -.009 && !result.RequiresOpeningBalanceCorrection, "补初后仍有负余额");
    var balance = result.OpeningBalance;
    foreach (var row in result.Records)
    {
        balance = Math.Round(balance + (row.TradeMoney ?? 0), 2, MidpointRounding.AwayFromZero); Near(balance, row.Balance!.Value);
        if (!Ordinary(row) || row.IsDocumentImported) continue;
        var amount = Math.Abs(row.TradeMoney ?? 0);
        if (Kind(row) is not ("Reference" or "Const")) continue;
        double Field(string key) => double.Parse(row.ExtraFields[key], CultureInfo.InvariantCulture);
        var min = Field("__GeneratedAmountMin"); var max = Field("__GeneratedAmountMax"); var unit = Field("__GeneratedAmountUnit");
        Check(amount >= min - .000001 && amount <= max + .000001, "突破来源区间");
        Check(Math.Abs(amount / unit - Math.Round(amount / unit)) < .000001, "精度被改变");
        if (unit < 1 || unit >= 10) Check((long)Math.Round(amount / unit) % 10 != 0, "有效金额位不能为0");
        Check(row.AccountTime!.Value.Date >= request.Config.StartTime.Date && row.AccountTime.Value.Date <= request.Config.EndTime.Date, "超出整体日期范围");
        if (Kind(row) == "Reference")
        {
            var rule = request.References.Single(rule => rule.Index.ToString() == row.ExtraFields["__GeneratedSourceIndex"]);
            Check(ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(row.AccountTime.Value, rule), "违反周末节假日限制");
        }
    }
    Near(balance, result.FinalBalance);
    var tails = result.Records.Where(row => Ordinary(row) && !row.IsDocumentImported
        && row.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision") == "2"
        && row.ExtraFields.GetValueOrDefault("__GeneratedAmountMin") != row.ExtraFields.GetValueOrDefault("__GeneratedAmountMax"))
        .Select(row => (int)Math.Round(Math.Abs(row.TradeMoney ?? 0) * 100) % 100).ToArray();
    Check(tails.Zip(tails.Skip(1)).All(pair => pair.First > 9 || pair.Second > 9), "低尾数不能连续出现");
    // A single required income in a month may have no equal-total cent partner.
    // Check rarity in the entire ordinary ledger, keeping hard monthly amounts.
    var ordinaryCount = result.Records.Count(row => Ordinary(row) && !row.IsDocumentImported);
    Check(tails.Count(tail => tail is >= 1 and <= 9) <= Math.Max(1, (int)Math.Ceiling(ordinaryCount * .02)),
        $"低尾数不应在普通流水中大量出现: bank={request.Bank.Name}, low={tails.Count(tail=>tail is >=1 and <=9)}, total={ordinaryCount}");
}
static void TestFixture(Fixture fixture, List<object> summaries)
{
    var counts = new List<int>(); var expenses = new List<double>(); var lower = 0; var variable = 0; var corrections = 0;
    for (var seed = 1001; seed <= 1030; seed++)
    {
        using var scope = new SeedScope(seed); var request = Request(fixture); request.Config.OpeningBalance = 100000000; // Funded replay; insufficient-fund cases have a separate suite.
        var input = JsonSerializer.Serialize(request);
        var generator = new FlowAutoGenerator(); var result = generator.Generate(request); Validate(request, result);
        Check(result.IncomeTotal <= request.Config.AllInMoney + .009 && result.IncomeTotal >= request.Config.AllInMoney * .97, "普通进账应接近配置预算");
        foreach (var row in result.Records.Where(row => row.TradeMoney < 0 && Kind(row) == "Reference"))
        {
            var min = double.Parse(row.ExtraFields["__GeneratedAmountMin"], CultureInfo.InvariantCulture);
            var max = double.Parse(row.ExtraFields["__GeneratedAmountMax"], CultureInfo.InvariantCulture);
            if (max <= min + .000001) continue;
            variable++; if ((Math.Abs(row.TradeMoney!.Value) - min) / (max - min) <= .1) lower++;
        }
        var snapshot = Snapshot(result);
        // One full correction cycle per seed; inspect the actual returned ledger, not just its totals.
        foreach (var corrected in new[] { generator.ApplyNegativeBalanceCorrection(request, result), generator.ApplyFinalBalanceCorrection(request, result),
                     generator.ApplyOpeningBalanceCorrection(request, result, result.OpeningBalance + 5000) })
        {
            Validate(request, corrected); Check(snapshot == Snapshot(corrected), "余额修正改写了普通金额、日期或编号"); corrections++;
        }
        Check(input == JsonSerializer.Serialize(request), "调用方配置被修改");
        counts.Add(result.Records.Count); expenses.Add(result.ExpenseTotal);
    }
    var ratio = counts.Average() / fixture.VendorRows.Average();
    Check(ratio is > .80 and < 1.20, $"条数仍明显偏离原真诚样本：{counts.Average():F1}/{fixture.VendorRows.Average():F1}");
    Check(counts.Distinct().Count() > 3, "条数不应固定");
    Check(variable == 0 || (double)lower / variable < .4, "可变区间支出仍大量集中下限");
    summaries.Add(new { BankId = fixture.Bank.Id, Generations = counts.Count, Corrections = corrections,
        MinRows = counts.Min(), MaxRows = counts.Max(), MeanRows = counts.Average(), VendorRows = fixture.VendorRows,
        MeanExpense = expenses.Average(), Lower10Ratio = variable == 0 ? 0 : (double)lower / variable });
}
static void TestScope()
{
    var personal = new long[] { 1, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 15, 16, 17 }; var corporate = new long[] { 104, 107, 113 };
    foreach (var id in Enumerable.Range(1, 18).Select(i => (long)i).Concat(Enumerable.Range(101, 16).Select(i => (long)i)))
    foreach (var type in new[] { BankTypes.Personal, BankTypes.Corporate })
    foreach (var mode in new[] { 0, 1, 2 })
        Check(ReferenceNaturalGenerationPolicy.AppliesTo(new Bank { Id = id, Name = "测试银行", Type = type }, new FlowGenerationConfig { SelectIndex = mode })
            == (mode == 0 && (type == BankTypes.Personal ? personal.Contains(id) : corporate.Contains(id))), "新增分支泄漏至其他类型/模式");
}
static Fixture Small() => new()
{
    Bank = new Bank { Id = 16, Name = "招行", Type = BankTypes.Personal },
    Config = new FlowGenerationConfig { StartTime = new DateTime(2026, 1, 1), EndTime = new DateTime(2026, 1, 31), AllInMoney = 30000, LastMoney = 10000 },
    References = [new GenerateReferenceRule { Index = 1, IsCheck = true, IncomeAttribute = "收入", MinMoney = 5000, MaxMoney = 60000,
        FloutLength = 0, PercentMonth = 2, TradeHoliday = true, TradeWeekend = true, StartDay = 9, EndDay = 20 }]
};
static void TestIncomeCapacity()
{
    for (var seed = 1001; seed <= 1030; seed++)
    {
        using var scope = new SeedScope(seed); var request = Request(Small()); var result = new FlowAutoGenerator().Generate(request);
        Check(result.IncomeTotal is >= 29990 and <= 30000, "没有廉价参照时不能丢掉几千元收入预算"); Validate(request, result);
    }
    var fixture = Small(); fixture.ConstItems.Add(new GenerateConstRule { Index = 2, IsCheck = true, IncomeAttribute = "收入", MinMoney = 1000, MaxMoney = 1000, FixDay = "15", StartDay = 9, EndDay = 20 });
    var r = new FlowAutoGenerator().Generate(Request(fixture)); Check(r.Records.Single(row => Kind(row) == "Const").TradeMoney == 1000, "固定收入不能用于凑尾差");
}
static void TestFields()
{
    var fixture = Small(); var rule = fixture.References[0]; rule.SerialNum = "SYNTHETIC-SERIAL"; rule.ProductBrief = "测试摘要";
    rule.OppositeAccount = "TEST-COUNTERPARTY"; rule.OppositeBank = "测试开户行";
    var request = Request(fixture); var generator = new FlowAutoGenerator(); var result = generator.Generate(request);
    foreach (var row in result.Records)
        Check(row.SerialNum == rule.SerialNum && row.ProductBrief == rule.ProductBrief && row.OppositeAccount == rule.OppositeAccount && row.OppositeBank == rule.OppositeBank, "字段映射被改变");
    Check(Snapshot(result) == Snapshot(generator.ApplyFinalBalanceCorrection(request, result)), "修正改变了原字段");
}
static void TestAcceptance(List<Fixture> fixtures)
{
    foreach (var fixture in fixtures)
    {
        using var scope = new SeedScope(1001); var request = Request(fixture, interest: true); var result = new FlowAutoGenerator().Generate(request);
        var type = typeof(SpeedEmulator.ViewModels.FlowGenerationViewModel); var vm = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("<Bank>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, request.Bank);
        type.GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, request.Config.Clone());
        object?[] args = [result, request.BankUser, request.InterestSetting, null];
        Check((bool)type.GetMethod("IsGenerationResultAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!, fixture.Bank.Name + ": " + args[3]);
    }
}
static void TestImported()
{
    var fixture = Small(); var request = Request(fixture); var rows = new List<FlowRecord>
    {
        new() { BankId = 16, BankUserId = request.BankUser.Id, AccountTime = new DateTime(2026, 1, 1), TradeMoney = -10.01, IsDocumentImported = true },
        new() { BankId = 16, BankUserId = request.BankUser.Id, AccountTime = new DateTime(2026, 1, 21), TradeMoney = .05 }
    };
    FlowGeneratedRowKinds.SetKind(rows[1], FlowGeneratedRowKinds.Interest);
    rows[1].IsDocumentImported = true;
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0);
    Check(result.Records.Any(row => row.IsDocumentImported && row.TradeMoney == -10.01), "导入金额不能被修改");
    Check(result.Records.Any(row => FlowGeneratedRowKinds.IsInterest(row) && row.TradeMoney == .05), "导入结息不能被修改");
}
static void TestDates()
{
    var fixture = Small(); fixture.Config.AllInMoney = 0; var rule = fixture.References[0];
    rule.IncomeAttribute = "支出"; rule.MinMoney = 5; rule.MaxMoney = 300; rule.FloutLength = 2; rule.PercentMonth = 200;
    fixture.Config.EndTime = new DateTime(2026, 1, 4); rule.TradeHoliday = false; rule.TradeWeekend = false;
    using var scope = new SeedScope(99); var request = Request(fixture); var result = new FlowAutoGenerator().Generate(request);
    Check(result.Records.Count == 0, "无允许日期时不能捏造参照流水");
    rule.TradeHoliday = true; rule.TradeWeekend = true; fixture.Config.OpeningBalance = 1000000; request = Request(fixture); result = new FlowAutoGenerator().Generate(request); Validate(request, result);
    Check(result.Records.Count is > 5 and < 60, "四天窗口不能补满整月200次");
}
static void TestAlipayDates()
{
    var fixture = Small(); fixture.Bank.Id = 1; fixture.Bank.Name = "支付宝";
    fixture.Config.StartTime = new DateTime(2026, 1, 1); fixture.Config.EndTime = new DateTime(2026, 12, 31);
    var income = fixture.References[0]; income.PercentMonth = 0; income.ProductBrief = "转账";
    income.MinMoney = 500; income.MaxMoney = 2000;
    var firstDay = 0;
    for (var seed = 1001; seed <= 1010; seed++)
    {
        using var scope = new SeedScope(seed); var request = Request(fixture); var generator = new FlowAutoGenerator(); var result = generator.Generate(request);
        foreach (var row in result.Records)
        {
            Check(row.SerialNum.StartsWith(row.AccountTime!.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)), "流水号日期未同步");
            if (row.AccountTime.Value.Date == fixture.Config.StartTime.Date) firstDay++;
        }
        Check(Snapshot(result) == Snapshot(generator.ApplyFinalBalanceCorrection(request, result)), "修正不应再搬动日期或编号");
    }
    Check(firstDay < 5, "新增入口不能每批强行搬一笔收入到第一天");
}

sealed class Fixture
{
    public Bank Bank { get; init; } = new(); public FlowGenerationConfig Config { get; init; } = new();
    public List<GenerateReferenceRule> References { get; init; } = []; public List<GenerateConstRule> ConstItems { get; init; } = [];
    public List<int> VendorRows { get; init; } = [];
}
sealed class SeedScope : IDisposable
{
    readonly string? previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    public SeedScope(int seed) => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString(CultureInfo.InvariantCulture));
    public void Dispose() => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous);
}
