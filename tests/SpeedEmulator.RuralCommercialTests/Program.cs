using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("仅个人农商总进账模式新增启用，对公及其他模式保持原路径", TestScope),
    ("82条参照的真实配置恢复完整金额区间和接近真诚的条数", TestReported),
    ("末尾两天按整月抽日期后筛选，条数动态不补满", TestPartialMonth),
    ("周末节假日勾选代表允许，不勾选抽中后跳过", TestDates),
    ("三种余额修正保留金额日期编号及输入，结息仍按现有利率", TestCorrections),
    ("固定日期和固定整数金额沿用原有规则", TestFixed),
    ("保留农商字段及显式流水号，倒置金额范围仍兼容", TestFields),
    ("自然金额及自动补初结果通过界面验收", TestAcceptance)
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + exception); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} rural-commercial tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
static void Near(double a, double b) => Check(Math.Abs(a - b) <= .009, $"expected={a}, actual={b}");
static string Kind(FlowRecord row) => row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind", "");
static string Source(FlowRecord row) => row.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex", "");
static FlowAutoGenerationRequest Request(bool interest = false)
{
    // Numeric/date settings only: no customer names, accounts, or counterparties.
    var snapshot = JsonSerializer.Deserialize<FlowGenerationSnapshot>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "reported-config.json")))!;
    snapshot.Config.OpeningBalance = 20000000; // Funded replay verifies the original full-range distribution independently of shortage handling.
    return Build(snapshot.Config, snapshot.References, interest: interest);
}
static FlowAutoGenerationRequest Build(FlowGenerationConfig config, IReadOnlyList<GenerateReferenceRule> rules,
    IReadOnlyList<GenerateConstRule>? fixedItems = null, bool interest = false)
{
    var bank = new Bank { Id = 3, Name = "个人农商", Type = BankTypes.Personal };
    foreach (var method in new[] { "ConfigureFlowGenerationColumns", "ConfigureFlowRecordColumns" })
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [bank]);
    return new FlowAutoGenerationRequest
    {
        Bank = bank, BankUser = new BankUser { Id = 9003, BankId = 3, AccountNo = "9000000000000003", AutoCalculateInterest = interest },
        Config = config, References = rules, ConstItems = fixedItems ?? [],
        InterestSetting = BankInterestSettingDefaults.CreateDefault(bank)
    };
}
static GenerateReferenceRule Rule(int index = 1, int attempts = 300) => new()
{
    BankId = 3, Index = index, IsCheck = true, IncomeAttribute = "支出", MinMoney = 5, MaxMoney = 300,
    FloutLength = 2, PercentMonth = attempts, StartDay = 9, EndDay = 20, TradeHoliday = true, TradeWeekend = true
};
static FlowGenerationConfig January() => new() { SelectIndex = 0, StartTime = new DateTime(2026, 1, 1), EndTime = new DateTime(2026, 1, 31), OpeningBalance = 1000000 };
static IDisposable Seed(int seed) => new EnvironmentScope(seed);
static string Ordinary(FlowAutoGenerationResult result) => JsonSerializer.Serialize(result.Records
    .Where(row => !FlowGeneratedRowKinds.IsSystemInterest(row)).Select(row => new
    {
        row.AccountTime, row.TradeMoney, row.SerialNum, row.BankId, row.BankUserId, row.ProductBrief,
        row.OppositeAccount, row.OppositeUsername, row.OppositeBank, row.ExtraFields
    }));
static void Validate(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
{
    Check(result.MinimumBalance >= -.009 && !result.RequiresOpeningBalanceCorrection, "补初后不能有负余额");
    var balance = result.OpeningBalance;
    foreach (var row in result.Records)
    {
        balance = Math.Round(balance + (row.TradeMoney ?? 0), 2, MidpointRounding.AwayFromZero);
        Near(balance, row.Balance!.Value);
        if (FlowGeneratedRowKinds.IsSystemInterest(row) || row.IsDocumentImported) continue;
        var amount = Math.Abs(row.TradeMoney ?? 0);
        var min = double.Parse(row.ExtraFields["__GeneratedAmountMin"], CultureInfo.InvariantCulture);
        var max = double.Parse(row.ExtraFields["__GeneratedAmountMax"], CultureInfo.InvariantCulture);
        var unit = double.Parse(row.ExtraFields["__GeneratedAmountUnit"], CultureInfo.InvariantCulture);
        Check(amount >= min - .000001 && amount <= max + .000001, "来源金额范围不可突破");
        Check(Math.Abs(Math.Round(amount / unit) - amount / unit) < .000001, "精度不可突破");
        Check((int)Math.Round(amount * 100, MidpointRounding.AwayFromZero) % 100 is not (>= 1 and <= 9), "不得出现.01至.09");
        if (unit < 1 || unit >= 10) Check((long)Math.Round(amount / unit) % 10 != 0, "有效金额位不能为零");
        if (Kind(row) == "Reference")
        {
            var rule = request.References.Single(rule => rule.Index.ToString() == Source(row));
            Check(ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(row.AccountTime!.Value, rule), "交易日限制不可突破");
            Check(row.AccountTime.Value.Date >= request.Config.StartTime.Date && row.AccountTime.Value.Date <= request.Config.EndTime.Date, "日期不能超出整体区间");
        }
    }
    Near(balance, result.FinalBalance);
}
static void TestScope()
{
    var config = new FlowGenerationConfig { SelectIndex = 0 };
    foreach (var id in Enumerable.Range(1, 24).Select(i => (long)i).Concat(Enumerable.Range(101, 10).Select(i => (long)i)))
    foreach (var mode in new[] { 0, 1, 2 })
    {
        config.SelectIndex = mode;
        var bank = new Bank { Id = id, Name = id == 101 ? "对公农商" : "测试银行", Type = id < 100 ? BankTypes.Personal : BankTypes.Corporate };
        Check(PersonalRuralCommercialGenerationPolicy.AppliesTo(bank, config) == (id == 3 && mode == 0), "个人农商范围不能泄漏");
        var natural = bank.Type == BankTypes.Personal && id is 1 or 3 or 4 or 5 or 6 or 7 or 9 or 10 or 11 or 12 or 13 or 15 or 16 or 17
            || bank.Type == BankTypes.Corporate && id is 104 or 107 or 113;
        Check(ReferenceNaturalGenerationPolicy.AppliesTo(bank, config) == (natural && mode == 0), "仅经复现验证的银行总进账模式启用新分支");
    }
    config.SelectIndex = 0;
    Check(!PersonalRuralCommercialGenerationPolicy.AppliesTo(new Bank { Id = 101, Name = "个人农商", Type = BankTypes.Corporate }, config), "银行类型必须先隔离");
    Check(!IcbcGenerationPolicy.AppliesTo(Request().Bank, config), "不能扩大工行自身策略范围");
}
static void TestReported()
{
    var counts = new HashSet<int>();
    for (var seed = 1001; seed <= 1030; seed++)
    {
        using var scope = Seed(seed); var request = Request(); var before = JsonSerializer.Serialize(request);
        var result = new FlowAutoGenerator().Generate(request); Validate(request, result);
        var expense = result.Records.Where(row => row.TradeMoney < 0).ToArray();
        Check(request.References.Count == 82, "必须重放完整82条参照");
        Check(result.Records.Count is >= 1250 and <= 1290, $"条数应接近真诚1268条，实际{result.Records.Count}");
        Check(result.IncomeTotal is >= 29900 and <= 30000, "普通收入接近总进账但不超额");
        Check(result.ExpenseTotal is > 7000000 and < 13000000, "大额支出不能再被压成区间下限");
        Check(expense.Count(row => row.TradeMoney >= -20) < expense.Length * .25, "不能再有七成支出集中20元内");
        var large = expense.Where(row => Source(row) == "63").Select(row => -row.TradeMoney!.Value).ToArray();
        Check(large.Length >= 72 && large.Average() is > 80000 and < 140000, "2万至20万元必须覆盖完整区间");
        Check(large.Max() > 150000, "大额区间上部必须有样本");
        Check(result.OpeningBalance == request.Config.OpeningBalance, "资金足够时应保留完整金额分布且不改期初");
        Check(before == JsonSerializer.Serialize(request), "生成不能修改调用方配置");
        counts.Add(result.Records.Count);
    }
    Check(counts.Count > 5, "不同随机种子应产生动态条数");
}
static void TestPartialMonth()
{
    using var scope = Seed(1001); var request = Request(); var result = new FlowAutoGenerator().Generate(request);
    var expenses = result.Records.Where(row => row.TradeMoney < 0).ToArray();
    Check(expenses.Where(row => row.AccountTime!.Value < new DateTime(2026, 6, 1)).GroupBy(row => row.AccountTime!.Value.ToString("yyyy-MM"))
        .All(group => group.Count() == 104), "完整月份保留104次支出尝试");
    Check(expenses.Count(row => row.AccountTime!.Value >= new DateTime(2026, 6, 1)) < 25, "末尾两天不能补满整月104笔");
}
static void TestDates()
{
    var rule = Rule(); using var scope = Seed(88);
    var all = new FlowAutoGenerator().Generate(Build(January(), [rule]));
    Check(all.Records.Count == 300, "全部日期允许时保留所有尝试");
    Check(all.Records.Any(row => row.AccountTime!.Value.DayOfWeek == DayOfWeek.Sunday), "勾选后允许周末");
    Check(all.Records.Any(row => row.AccountTime!.Value.Day == 5), "勾选不能排除工作日");
    rule.TradeWeekend = false; rule.TradeHoliday = false;
    var filtered = new FlowAutoGenerator().Generate(Build(January(), [rule]));
    Check(filtered.Records.Count is > 100 and < 290, "禁止日期抽中后跳过，不能补抽");
    Check(filtered.Records.All(row => ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(row.AccountTime!.Value, rule)), "禁止周末假日不生成");
    Check(!ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(new DateTime(2026, 1, 1), rule), "元旦需过滤");
    var mean = all.Records.Average(row => Math.Abs(row.TradeMoney!.Value));
    Check(mean is > 130 and < 175, "5至300元应接近完整区间均值");
}
static void TestCorrections()
{
    using var scope = Seed(1001); var request = Request(true); var generator = new FlowAutoGenerator();
    var before = JsonSerializer.Serialize(request); var result = generator.Generate(request); var ordinary = Ordinary(result);
    Check(result.Records.Any(FlowGeneratedRowKinds.IsInterest), "启用计息后必须仍有结息");
    foreach (var corrected in new[] { generator.ApplyNegativeBalanceCorrection(request, result), generator.ApplyFinalBalanceCorrection(request, result), generator.ApplyOpeningBalanceCorrection(request, result, result.OpeningBalance + 5000) })
    {
        Validate(request, corrected); Check(ordinary == Ordinary(corrected), "修正不能再压回预算或改普通流水");
        var rows = corrected.Records.Select(row => row.Clone()).ToList();
        BankInterestCalculationService.RecalculatePreservingImported(request.Bank, request.BankUser, request.InterestSetting, rows,
            corrected.OpeningBalance, request.Config.StartTime.Date, request.Config.EndTime.Date.AddDays(1).AddSeconds(-1));
        Near(corrected.IncomeTotal, rows.Where(row => row.TradeMoney > 0).Sum(row => row.TradeMoney ?? 0));
    }
    Check(before == JsonSerializer.Serialize(request) && ordinary == Ordinary(result), "修正不能修改输入");
}
static void TestFixed()
{
    using var scope = Seed(10); var config = January();
    var request = Build(config, [], [new GenerateConstRule { BankId = 3, Index = 1, IsCheck = true, IncomeAttribute = "支出", MinMoney = 100, MaxMoney = 100,
        FloutLength = 2, FixDay = "15", StartDay = 9, EndDay = 20, TradeHoliday = false, TradeWeekend = false }]);
    var result = new FlowAutoGenerator().Generate(request); Validate(request, result);
    Check(result.Records.Count == 1 && result.Records[0].AccountTime!.Value.Day == 15 && result.Records[0].TradeMoney == -100,
        "保留固定日期15日，固定100元不强凑小数");
}
static void TestFields()
{
    using var scope = Seed(44); var rule = Rule(attempts: 1); rule.MinMoney = 10000; rule.MaxMoney = 5000; rule.FloutLength = 0;
    rule.ProductName = "测试产品"; rule.ProductBrief = "测试摘要"; rule.SerialNum = "SYNTHETIC-SERIAL";
    rule.Operator = "模拟柜员"; rule.BranchNum = "TEST-BRANCH"; rule.OppositeAccount = "TEST-ACCOUNT"; rule.Usage = "测试用途";
    var request = Build(January(), [rule]); var result = new FlowAutoGenerator().Generate(request); Validate(request, result);
    var row = result.Records.Single();
    Check(row.ProductName == rule.ProductName && row.ProductBrief == rule.ProductBrief && row.SerialNum == rule.SerialNum && row.Operator == rule.Operator
        && row.BranchNum == rule.BranchNum && row.OppositeAccount == rule.OppositeAccount && row.Usage == rule.Usage, "原有农商字段及显式流水号不可改变");
    rule.SerialNum = null; var generated = new FlowAutoGenerator().Generate(request).Records.Single();
    Check(!string.IsNullOrWhiteSpace(generated.SerialNum), "空流水号仍按农商已有规则生成");
}
static void TestAcceptance()
{
    using var scope = Seed(1001); var request = Request(true); var result = new FlowAutoGenerator().Generate(request);
    var type = typeof(SpeedEmulator.ViewModels.FlowGenerationViewModel);
    var vm = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
    type.GetField("<Bank>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, request.Bank);
    type.GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, request.Config.Clone());
    object?[] args = [result, request.BankUser, request.InterestSetting, null];
    Check((bool)type.GetMethod("IsGenerationResultAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!, "界面不能以旧期末目标拒绝结果: " + args[3]);
    Check(Math.Abs(result.FinalBalance - result.OpeningBalance - request.Config.LastMoney) > 1000000, "确实覆盖旧期末目标冲突场景");
}
sealed class EnvironmentScope : IDisposable
{
    readonly string? previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    public EnvironmentScope(int seed) => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString(CultureInfo.InvariantCulture));
    public void Dispose() => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous);
}
