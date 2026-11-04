using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("34类银行三种模式均拦截不同金额的相同尾数", TestAllBanks),
    ("整数穿插不能绕过两位小数尾数检查", TestIntegerBridge),
    ("收入支出使用共同的尾数相邻检查", TestBothDirections),
    ("固定金额结息导入及已保存数据保留原值", TestProtected),
    ("固定日期可变金额也执行尾数规则，固定日期固定金额保留", TestFixedDateSources),
    ("182笔同尾数在原区间内均衡，合计日期及期初不改", TestConcentration),
    ("同月独立明细期间的收支预算不可串用", TestPeriodBudgets),
    ("结息保持原值且真实利息重算一致", TestInterest),
    ("所有余额修正出口再次检查且正常结果幂等", TestCorrectionExits),
    ("建行实际数值配置连续30次无相邻尾数重复及11集中", TestReportedCcb),
    ("无解尾数配置明确定位冲突且不修改输入", TestImpossible),
    ("少量孤立低尾数允许原样通过，不主动凑满比例", TestIsolatedLowTail),
    ("34类银行三种模式均阻止不同低尾数连续出现", TestDifferentLowTails),
    ("低尾数不能用整数或相反方向的流水隔开检查", TestLowIntegerBridge),
    ("集中低尾数压低至约2%且不改总额日期和原始输入", TestLowTailFrequency),
    ("新生成低尾数降频不借用已保存流水金额", TestLowTailPersisted)
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} strict-tail tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
static void Near(double a, double b) => Check(Math.Abs(a - b) < .011, $"expected={a}, actual={b}");
static int Tail(FlowRecord row) => (int)Math.Round(Math.Abs(row.TradeMoney ?? 0) * 100, MidpointRounding.AwayFromZero) % 100;
static GenerateReferenceRule Rule(int index = 1, bool income = false, double min = 5, double max = 300, int precision = 2) => new()
{ Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出", MinMoney = min, MaxMoney = max,
    FloutLength = precision, StartDay = 8, EndDay = 22, TradeHoliday = true, TradeWeekend = true };
static FlowAutoGenerationRequest Request(IReadOnlyList<GenerateReferenceRule> rules, long id = 8, string name = "建行", bool interest = false, double opening = 10000)
{
    var bank = new Bank { Id = id, Name = name, Type = id < 100 ? BankTypes.Personal : BankTypes.Corporate };
    foreach (var method in new[] { "ConfigureFlowGenerationColumns", "ConfigureFlowRecordColumns" })
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [bank]);
    return new FlowAutoGenerationRequest { Bank = bank, BankUser = new BankUser { Id = 9000 + id, BankId = id, AccountNo = "9000000000000000", AutoCalculateInterest = interest },
        Config = new FlowGenerationConfig { StartTime = new DateTime(2026, 1, 1), EndTime = new DateTime(2026, 3, 31), OpeningBalance = opening, AllInMoney = 1000 },
        References = rules, ConstItems = [], InterestSetting = BankInterestSettingDefaults.CreateDefault(bank) };
}
static FlowRecord Row(FlowAutoGenerationRequest request, FlowRuleBase rule, int position, double money, DateTime? time = null) =>
    (FlowRecord)typeof(FlowAutoGenerator).GetMethod("CreateRecordFromRule", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [request, rule, rule is GenerateConstRule ? request.Bank.ConstColumns : request.Bank.ReferenceColumns,
            time ?? new DateTime(2026, 1, 5, 9, 0, 0).AddSeconds(position), money])!;
static FlowAutoGenerationResult Repair(FlowAutoGenerationRequest request, params FlowRecord[] rows)
{
    var before = JsonSerializer.Serialize(rows); var input = JsonSerializer.Serialize(request);
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, request.Config.OpeningBalance);
    Check(before == JsonSerializer.Serialize(rows) && input == JsonSerializer.Serialize(request), "不能改写输入");
    Near(request.Config.OpeningBalance, result.OpeningBalance); Check(result.MinimumBalance >= -.009, "不能制造负余额");
    AssertTails(result); return result;
}
static bool Eligible(FlowRecord row) => row.Id == 0 && !row.IsDocumentImported && !FlowGeneratedRowKinds.IsSystemInterest(row)
    && row.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision") == "2"
    && row.ExtraFields.GetValueOrDefault("__GeneratedAmountMin") != row.ExtraFields.GetValueOrDefault("__GeneratedAmountMax");
static void AssertTails(FlowAutoGenerationResult result)
{
    var rows = result.Records.Where(Eligible).ToArray();
    Check(rows.Zip(rows.Skip(1)).All(pair => Tail(pair.First) != Tail(pair.Second)), "两位小数序列存在连续相同尾数: " + string.Join(',', rows.Select(row => row.TradeMoney)));
    Check(rows.All(row => Tail(row) is >= 1 and <= 99 && Tail(row) % 10 != 0), "仍需遵守两位非零末位规则");
    Check(rows.Zip(rows.Skip(1)).All(pair => Tail(pair.First) > 9 || Tail(pair.Second) > 9), "不同低尾数也不能连续出现");
}
static void TestAllBanks()
{
    var count = 0;
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    foreach (var pair in (IReadOnlyDictionary<string, long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
    foreach (var mode in new[] { 0, 1, 2 })
    {
        var rule = Rule(); var request = Request([rule], pair.Value, pair.Key); request.Config.SelectIndex = mode;
        var rows = new[] { 10.11, 44.11, 140.11, 25.11 }.Select((money, i) => Row(request, rule, i, -money)).ToArray();
        var result = Repair(request, rows); Near(219.44, result.ExpenseTotal);
        Check(result.Records.Select(row => row.AccountTime).SequenceEqual(rows.Select(row => row.AccountTime)), "同日可解时不要改时间");
        count++;
    }
    Check(count == 102, "所有银行三种模式均应覆盖");
}
static void TestIntegerBridge()
{
    var rule = Rule(); var integer = Rule(2, precision: 0); var request = Request([rule, integer]);
    var result = Repair(request, Row(request, rule, 0, -10.11), Row(request, integer, 1, -30), Row(request, rule, 2, -44.11));
    Near(84.22, result.ExpenseTotal); Check(result.Records[1].TradeMoney == -30, "整数不参与尾数分配");
}
static void TestBothDirections()
{
    var income = Rule(1, true); var expense = Rule(2); var request = Request([income, expense]);
    var result = Repair(request, Row(request, income, 0, 100.11), Row(request, expense, 1, -40.11),
        Row(request, expense, 2, -60.23), Row(request, income, 3, 200.37));
    Near(300.48, result.IncomeTotal); Near(100.34, result.ExpenseTotal);
}
static void TestProtected()
{
    var variable = Rule(); var fixedRule = Rule(2, min: 20.11, max: 20.11); var request = Request([variable, fixedRule]);
    var a = Row(request, variable, 0, -10.11); var b = Row(request, variable, 1, -44.11);
    var imported = Row(request, variable, 2, -15.01); imported.IsDocumentImported = true; imported.TradeMoney = -15.01;
    var persisted = Row(request, variable, 3, -80.11); persisted.Id = 500;
    var fixedRow = Row(request, fixedRule, 4, -20.11);
    var result = Repair(request, a, b, imported, persisted, fixedRow);
    Check(result.Records.Single(row => row.IsDocumentImported).TradeMoney == -15.01, "导入原值不能改变");
    Check(result.Records.Single(row => row.Id == 500).TradeMoney == -80.11, "已有普通流水不能改变");
    Check(result.Records.Single(row => row.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex") == "2").TradeMoney == -20.11, "固定小数不能改变");
}
static void TestConcentration()
{
    var rule = Rule(max: 500); var request = Request([rule], opening: 100000);
    var rows = Enumerable.Range(0, 182).Select(i => Row(request, rule, i, -(20 + i % 100 + .11))).ToArray();
    var result = Repair(request, rows); Near(rows.Sum(row => -row.TradeMoney ?? 0), result.ExpenseTotal);
    var usage = result.Records.Where(Eligible).GroupBy(Tail).Select(group => group.Count()).ToArray();
    Check(usage.Max() <= 6, "不能把11集中改成另一个集中尾数，最高频次=" + usage.Max());
    Check(result.Records.Select(row => row.AccountTime).SequenceEqual(rows.Select(row => row.AccountTime)), "只需分配尾数时日期不能改变");
}
static void TestFixedDateSources()
{
    var basic = Request([]);
    var variable = new GenerateConstRule { Index = 1, IsCheck = true, IncomeAttribute = "支出", MinMoney = 5, MaxMoney = 300,
        FloutLength = 2, FixDay = "5", StartDay = 8, EndDay = 22 };
    var fixedRule = new GenerateConstRule { Index = 2, IsCheck = true, IncomeAttribute = "支出", MinMoney = 20.11, MaxMoney = 20.11,
        FloutLength = 2, FixDay = "5", StartDay = 8, EndDay = 22 };
    var request = new FlowAutoGenerationRequest { Bank = basic.Bank, BankUser = basic.BankUser, Config = basic.Config,
        References = [], ConstItems = [variable, fixedRule], InterestSetting = basic.InterestSetting };
    var rows = new[] { 10.11, 44.11, 140.11, 25.11 }.Select((amount, i) => Row(request, variable, i, -amount))
        .Concat([Row(request, fixedRule, 4, -20.11), Row(request, fixedRule, 5, -20.11)]).ToArray();
    var result = Repair(request, rows); Near(259.66, result.ExpenseTotal);
    Check(result.Records.Select(row => row.AccountTime).SequenceEqual(rows.Select(row => row.AccountTime)), "固定日期项目不能改时间");
    Check(result.Records.Where(row => row.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex") == "2")
        .All(row => row.TradeMoney == -20.11), "固定金额不能改");
}
static void TestPeriodBudgets()
{
    var rule = Rule(); var request = Request([rule]); request.Config.SelectIndex = 2;
    foreach (var (start, end) in new[] { (1, 15), (16, 31) })
        request.Config.MonthGenData.Add(new MonthGenerateRule { StartTime = new DateTime(2026, 1, start), EndTime = new DateTime(2026, 1, end), OutMoney = 219.44 });
    var rows = new[] { 5, 20 }.SelectMany(day => new[] { 10.11, 44.11, 140.11, 25.11 }
        .Select((amount, i) => Row(request, rule, i, -amount, new DateTime(2026, 1, day, 9, 0, i)))).ToArray();
    var result = Repair(request, rows);
    foreach (var period in request.Config.MonthGenData)
        Near(219.44, -result.Records.Where(row => row.AccountTime >= period.StartTime && row.AccountTime < period.EndTime.Date.AddDays(1)).Sum(row => row.TradeMoney ?? 0));
}
static void TestInterest()
{
    var rule = Rule(); var request = Request([rule], interest: true);
    var result = Repair(request, new[] { 10.11, 44.11, 140.11, 25.11 }.Select((amount, i) => Row(request, rule, i, -amount)).ToArray());
    var copy = result.Records.Select(row => row.Clone()).ToList();
    BankInterestCalculationService.RecalculatePreservingImported(request.Bank, request.BankUser, request.InterestSetting, copy,
        result.OpeningBalance, request.Config.StartTime, request.Config.EndTime.Date.AddDays(1).AddSeconds(-1));
    Near(result.IncomeTotal, copy.Where(row => row.TradeMoney > 0).Sum(row => row.TradeMoney ?? 0));
}
static void TestCorrectionExits()
{
    var rule = Rule(); var request = Request([rule], 3, "个人农商", interest: true);
    request.Config.OpeningBalance = 100000;
    var result = Repair(request, Enumerable.Range(0, 182).Select(i => Row(request, rule, i, -(20 + i % 100 + .11))).ToArray());
    string Ordinary(FlowAutoGenerationResult value) => JsonSerializer.Serialize(value.Records.Where(row => !FlowGeneratedRowKinds.IsSystemInterest(row))
        .Select(row => new { row.AccountTime, row.TradeMoney, row.SerialNum, row.ExtraFields }));
    var generator = new FlowAutoGenerator(); var before = Ordinary(result);
    foreach (var corrected in new[] { generator.ApplyNegativeBalanceCorrection(request, result), generator.ApplyFinalBalanceCorrection(request, result),
                 generator.ApplyOpeningBalanceCorrection(request, result, result.OpeningBalance),
                 generator.ApplyOpeningBalanceCorrection(request, result, result.OpeningBalance + 5000) })
    { AssertTails(corrected); Check(before == Ordinary(corrected), "有效结果重复修正或仅改期初时普通金额应幂等"); }
}
static void TestReportedCcb()
{
    var snapshot = JsonSerializer.Deserialize<FlowGenerationSnapshot>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/ccb-reported-numeric.json")))!;
    var basic = Request(snapshot.References, interest: true, opening: snapshot.Config.OpeningBalance);
    var request = new FlowAutoGenerationRequest { Bank = basic.Bank, BankUser = basic.BankUser, Config = snapshot.Config, References = snapshot.References,
        ConstItems = [], InterestSetting = basic.InterestSetting };
    var previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED"); var max11 = 0; var maxUsage = 0;
    try
    {
        for (var seed = 1001; seed <= 1030; seed++)
        {
            Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString(CultureInfo.InvariantCulture));
            var result = new FlowAutoGenerator().Generate(request); AssertTails(result);
            Near(request.Config.OpeningBalance, result.OpeningBalance); Near(request.Config.OpeningBalance + request.Config.LastMoney, result.FinalBalance);
            Check(result.MinimumBalance >= -.009, "不能制造负余额");
            var p2 = result.Records.Where(Eligible).ToArray(); Check(p2.Length == 182, "每月必现参照次数不变");
            max11 = Math.Max(max11, p2.Count(row => Tail(row) == 11)); maxUsage = Math.Max(maxUsage, p2.GroupBy(Tail).Max(group => group.Count()));
            Check(p2.GroupBy(Tail).Max(group => group.Count()) <= 9,
                $"实际配置仍有超过5%的单尾数集中：seed={seed}, tail={p2.GroupBy(Tail).OrderByDescending(group => group.Count()).First().Key}, max={p2.GroupBy(Tail).Max(group => group.Count())}");
        }
    }
    finally { Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous); }
    Console.WriteLine($"  reported CCB x30: max .11 count={max11}, max individual tail count={maxUsage}");
}
static void TestImpossible()
{
    var rule = Rule(min: 10.11, max: 10.12); var request = Request([rule]); request.Config.SelectIndex = 1;
    try { Repair(request, Row(request, rule, 0, -10.11), Row(request, rule, 1, -10.11)); }
    catch (InvalidOperationException error) { Check(error.Message.Contains("尾数"), "需要定位到尾数冲突"); return; }
    throw new Exception("不能把不可行重复尾数当成功输出");
}

static void TestIsolatedLowTail()
{
    var rule = Rule(); var request = Request([rule]);
    var rows = new[] { Row(request, rule, 0, -10.03), Row(request, rule, 1, -44.27), Row(request, rule, 2, -140.58) };
    var amounts = rows.Select(row => row.TradeMoney).ToArray();
    var result = Repair(request, rows);
    Check(result.Records.Select(row => row.TradeMoney).SequenceEqual(amounts), "孤立且少量的低尾数不应再被禁用或强制调整");
}
static void TestDifferentLowTails()
{
    var cases = 0;
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    foreach (var pair in (IReadOnlyDictionary<string, long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
    foreach (var mode in new[] { 0, 1, 2 })
    {
        var rule = Rule(); var request = Request([rule], pair.Value, pair.Key); request.Config.SelectIndex = mode;
        var rows = new[] { 10.03, 44.07, 140.28, 25.56 }.Select((money, i) => Row(request, rule, i, -money)).ToArray();
        var times = rows.Select(row => row.AccountTime).ToArray();
        var result = Repair(request, rows); Near(219.94, result.ExpenseTotal);
        Check(result.Records.Select(row => row.AccountTime).SequenceEqual(times), "同日金额有解时不能改日期");
        cases++;
    }
    Check(cases == 102, "应覆盖34类银行三种模式");
}
static void TestLowIntegerBridge()
{
    var income = Rule(1, true); var expense = Rule(2); var integer = Rule(3, precision: 0);
    var request = Request([income, expense, integer]);
    var result = Repair(request, Row(request, income, 0, 10.03), Row(request, integer, 1, -30),
        Row(request, expense, 2, -44.07), Row(request, expense, 3, -40.51));
    Check(result.Records[1].TradeMoney == -30, "整数不应被低尾数分配修改");
    Near(-104.55, result.FinalBalance - result.OpeningBalance);
}
static void TestLowTailFrequency()
{
    foreach (var fixedDate in new[] { false, true })
    {
        var rule = Rule();
        var basic = Request([rule], opening: 100000);
        FlowRuleBase sourceRule = fixedDate ? new GenerateConstRule { Index = 1, IsCheck = true, IncomeAttribute = "支出", MinMoney = 5, MaxMoney = 300,
            FloutLength = 2, FixDay = "5", StartDay = 8, EndDay = 22 } : rule;
        var request = new FlowAutoGenerationRequest { Bank = basic.Bank, BankUser = basic.BankUser, Config = basic.Config,
            References = fixedDate ? [] : [rule], ConstItems = fixedDate ? [(GenerateConstRule)sourceRule] : [], InterestSetting = basic.InterestSetting };
        var rows = Enumerable.Range(0, 200).Select(i => Row(request, sourceRule, i,
            -(20 + i % 100 + (i % 5 < 2 ? (i % 5 == 0 ? .03 : .07) : (11 + i % 8 * 10) / 100d)))).ToArray();
        var times = rows.Select(row => row.AccountTime).ToArray();
        var total = rows.Sum(row => -row.TradeMoney!.Value);
        var result = Repair(request, rows);
        Check(result.Records.Count(row => Tail(row) is >= 1 and <= 9) <= 4, "200条可变小数流水低尾数应不超过4条");
        Near(total, result.ExpenseTotal);
        Check(result.Records.Select(row => row.AccountTime).SequenceEqual(times), "降低低尾数频率不能挪动日期");
        var corrected = new FlowAutoGenerator().ApplyNegativeBalanceCorrection(request, result);
        AssertTails(corrected);
        Check(result.Records.Select(row => row.TradeMoney).SequenceEqual(corrected.Records.Select(row => row.TradeMoney)), "已合规结果重复修正应幂等");
    }
}
static void TestLowTailPersisted()
{
    var rule = Rule(); var request = Request([rule]);
    var persisted = Row(request, rule, 0, -20.03); persisted.Id = 42;
    var original = JsonSerializer.Serialize(persisted);
    var rows = new[] { persisted, Row(request, rule, 1, -24.07), Row(request, rule, 2, -25.03),
        Row(request, rule, 3, -30.27), Row(request, rule, 4, -40.58) };
    var result = Repair(request, rows);
    var saved = result.Records.Single(row => row.Id == 42);
    Check(saved.TradeMoney == persisted.TradeMoney && saved.AccountTime == persisted.AccountTime
        && saved.SerialNum == persisted.SerialNum && saved.VoucherNum == persisted.VoucherNum,
        "不能借用已保存记录完成新生成流水的降频");
    Check(JsonSerializer.Serialize(persisted) == original, "不能改写原已保存对象");
}
