using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var fixture = JsonSerializer.Deserialize<FlowGenerationSnapshot>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "minsheng-config.json")))!;
var observed = JsonSerializer.Deserialize<List<ObservedRow>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vendor-amounts.json")))!;
var summaries = new List<object>();
var tests = new (string Name, Action Run)[]
{
    ("民生对公原配置100次成功，保留全区间及月尝试，不再六笔收入卡5000", () => Reported(fixture, summaries)),
    ("真诚同批金额：只补普通收支最大缺口加1元，随后按实际期初计息", () => ObservedFunding(fixture, observed)),
    ("34类银行相同低预算配置均可生成，编号来源及界面验收有效", () => AllBanks(fixture)),
    ("合法顺延就能付款时保留期初，跨月收入不换源", Timing),
    ("可选明细的内部月份提示不能导致不必要补初", OptionalTiming),
    ("期初本身为负时也按真实起始缺口处理", NegativeOpening),
    ("资金充足时保留期初且普通金额日期不变", () => Funded(fixture, observed)),
    ("固定项目保留日期金额，普通月支出不因缺钱删掉", FixedProjects),
    ("四种建行日期开关仍执行专属日期规则", CcbDates),
    ("三种修正不再强凑期末，金额日期编号及配置保持不变", () => Corrections(fixture)),
    ("变量卡在同一下限仍能合法去重，固定金额例外保留", BoundaryDuplicates),
    ("普通两位尾数与相邻金额严格检查，结息按调整后重算", CentPatterns),
    ("导入低尾数金额与结息原值保留", Imported),
    ("显式月度预算模式继续使用原路径", () => MonthlyModes(fixture)),
    ("已有充足资金的自然生成配置沿用原路径", Healthy)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
}
Directory.CreateDirectory("tmp/reference-priority-20261102");
File.WriteAllText("tmp/reference-priority-20261102/tests.json", JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{tests.Length - failed}/{tests.Length} reference-priority tests passed.");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < .009, $"expected={expected}, actual={actual}");
static double Meta(FlowRecord row, string key) => double.TryParse(row.ExtraFields.GetValueOrDefault(key), CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
static bool Ordinary(FlowRecord row) => !FlowGeneratedRowKinds.IsSystemInterest(row);
static bool Variable(FlowRecord row) => Ordinary(row) && !row.IsDocumentImported && row.Id == 0 && Meta(row, "__GeneratedAmountMax") > Meta(row, "__GeneratedAmountMin") + .000001;
static GenerateReferenceRule Rule(int index, bool income, double min, double max, int monthly = 0, int precision = 0) => new()
{ Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出", MinMoney = min, MaxMoney = max, PercentMonth = monthly, FloutLength = precision, StartDay = 9, EndDay = 20, TradeHoliday = true, TradeWeekend = true };
static FlowAutoGenerationRequest Build(Bank bank, FlowGenerationConfig config, IEnumerable<GenerateReferenceRule> rules, IEnumerable<GenerateConstRule>? fixedItems = null, bool interest = false)
{
    foreach (var name in new[] { "ConfigureFlowGenerationColumns", "ConfigureFlowRecordColumns" })
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [bank]);
    return new FlowAutoGenerationRequest { Bank = bank, BankUser = new BankUser { Id = 9000 + bank.Id, BankId = bank.Id, AccountNo = "9000000000000000", AutoCalculateInterest = interest },
        Config = config.Clone(), References = rules.Select(rule => rule.Clone()).ToArray(), ConstItems = fixedItems?.Select(rule => rule.Clone()).ToArray() ?? [], InterestSetting = BankInterestSettingDefaults.CreateDefault(bank) };
}
static FlowAutoGenerationRequest Request(FlowGenerationSnapshot fixture, bool interest = false) => Build(new Bank { Id = 102, Name = "民生对公", Type = BankTypes.Corporate }, fixture.Config, fixture.References, interest: interest);
static FlowRecord Row(FlowAutoGenerationRequest request, FlowRuleBase rule, DateTime time, double money)
{
    var row = (FlowRecord)typeof(FlowAutoGenerator).GetMethod("CreateRecordFromRule", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [request, rule, rule is GenerateConstRule ? request.Bank.ConstColumns : request.Bank.ReferenceColumns, time, money])!;
    row.ExtraFields["__GeneratedReferencePriority"] = "1"; return row;
}
static string Snapshot(FlowAutoGenerationResult result) => JsonSerializer.Serialize(result.Records.Where(Ordinary).Select(row => new
{ row.AccountTime, row.TradeMoney, row.SerialNum, row.VoucherNum, row.MerchantName, row.Remark, row.OppositeAccount, row.ExtraFields }));
static void Validate(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
{
    Check(result.MinimumBalance >= -.009 && !result.RequiresOpeningBalanceCorrection, "结果有负余额");
    var balance = result.OpeningBalance; var previous = double.NaN; var previousVariable = false; int? previousTail = null;
    foreach (var row in result.Records)
    {
        balance = Math.Round(balance + (row.TradeMoney ?? 0), 2, MidpointRounding.AwayFromZero); Near(balance, row.Balance!.Value);
        var amount = Math.Abs(row.TradeMoney ?? 0); var variable = Variable(row);
        if (variable)
        {
            Check(amount >= Meta(row, "__GeneratedAmountMin") - .000001 && amount <= Meta(row, "__GeneratedAmountMax") + .000001, "金额突破原区间");
            var unit = Meta(row, "__GeneratedAmountUnit"); Check(Math.Abs(amount / unit - Math.Round(amount / unit)) < .000001, "改变精度");
            var cents = (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero); var tail = (int)(cents % 100);
            Check(tail is not (>= 1 and <= 9), "普通金额出现禁用尾数");
            Check(!previousVariable || Math.Abs(previous - amount) > .009, "相邻绝对金额重复");
            if (Meta(row, "__GeneratedAmountPrecision") == 2 && unit < .1)
            {
                Check(tail != previousTail, "普通两位小数子序列尾数重复"); previousTail = tail;
            }
        }
        if (row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind") == "Reference" && !row.IsDocumentImported)
        {
            var rule = request.References.Single(rule => rule.Index.ToString() == row.ExtraFields["__GeneratedSourceIndex"]);
            Check(request.Bank.Id == 8 ? ConstructionBankGenerationPolicy.IsReferenceDateAllowed(row.AccountTime!.Value, rule)
                : ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(row.AccountTime!.Value, rule), "违反银行交易日限制");
        }
        if (row.ExtraFields.TryGetValue("__GeneratedBudgetWindowStart", out var from)) Check(row.AccountTime >= DateTime.Parse(from, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), "移出分配月份");
        if (row.ExtraFields.TryGetValue("__GeneratedBudgetWindowEnd", out var to)) Check(row.AccountTime <= DateTime.Parse(to, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), "移出分配月份");
        previous = amount; previousVariable = variable;
    }
    Near(balance, result.FinalBalance);
    if (BankInterestPolicy.ShouldCalculate(request.Bank, request.BankUser))
    {
        var check = result.Records.Select(row => row.Clone()).ToList();
        BankInterestCalculationService.RecalculatePreservingImported(request.Bank, request.BankUser, request.InterestSetting, check, result.OpeningBalance, request.Config.StartTime, request.Config.EndTime.Date.AddDays(1).AddTicks(-1));
        Near(result.IncomeTotal, check.Where(row => row.TradeMoney > 0).Sum(row => row.TradeMoney!.Value));
    }
}
static void Reported(FlowGenerationSnapshot fixture, List<object> summaries)
{
    var values = new List<double>(); var counts = new HashSet<int>(); var totalExpenses = 0;
    for (var seed = 1001; seed <= 1100; seed++)
    {
        using var scope = new SeedScope(seed); var request = Request(fixture, interest: true); var before = JsonSerializer.Serialize(request);
        var result = new FlowAutoGenerator().Generate(request); Validate(request, result); Check(FlowAutoGenerator.UsesReferencePriority(request.Config, result.Records), "未启用参照优先");
        var expenses = result.Records.Where(row => row.TradeMoney < 0 && Ordinary(row)).ToArray();
        var income = result.Records.Where(row => row.TradeMoney > 0 && Ordinary(row)).ToArray();
        Check(expenses.Length is >= 3 and <= 12 && result.ExpenseTotal > 20000, "月支出仍被预算压缩");
        Check(income.Length < 6 && income.Any(row => row.TradeMoney != 5000), "收入仍全卡最低5000");
        Check(result.OpeningBalance > 0, "真实缺口未补期初");
        Check(result.Records.Any(FlowGeneratedRowKinds.IsInterest), "未按实际期初计息");
        var ordinaryBalance = 0d; var minimum = 0d;
        foreach (var row in result.Records.Where(Ordinary)) { ordinaryBalance += row.TradeMoney ?? 0; minimum = Math.Min(minimum, ordinaryBalance); }
        Check(result.OpeningBalance >= -minimum - .009 && result.OpeningBalance <= -minimum + 1.01, "期初不是实际最大缺口加安全余量");
        Check(before == JsonSerializer.Serialize(request), "改写用户配置");
        values.AddRange(expenses.Select(row => -row.TradeMoney!.Value)); totalExpenses += expenses.Length; counts.Add(result.Records.Count);
        summaries.Add(new { Seed = seed, Rows = result.Records.Count, OrdinaryIncomeRows = income.Length, ExpenseRows = expenses.Length, result.IncomeTotal, result.ExpenseTotal, result.OpeningBalance, result.FinalBalance });
    }
    var rule = fixture.References.Single(rule => rule.Index == 6); var expected = 0d;
    for (var month = fixture.Config.StartTime.Date; month <= fixture.Config.EndTime; month = month.AddMonths(1))
        expected += Enumerable.Range(1, DateTime.DaysInMonth(month.Year, month.Month)).Count(day => ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(new DateTime(month.Year, month.Month, day), rule)) / (double)DateTime.DaysInMonth(month.Year, month.Month);
    Check(Math.Abs(totalExpenses / 100d - expected) < .8, "日期筛选后未保持每月一次尝试的统计数量");
    Check(values.Average() is > 29000 and < 36000 && values.Min() < 10000 && values.Max() > 55000, "原区间没有均匀覆盖");
    Check(counts.Count >= 5, "每次条数固定");
}
static void ObservedFunding(FlowGenerationSnapshot fixture, List<ObservedRow> observed)
{
    var request = Request(fixture, interest: true);
    var rows = observed.Where(row => row.Kind != "Interest").Select(row => Row(request, request.References.Single(rule => rule.Index == row.SourceRule), row.Time,
        row.Money == 30108.01 ? 30108.11 : row.Money)).ToArray();
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Validate(request, result);
    Near(303033.89, result.OpeningBalance); Near(338664, result.ExpenseTotal);
    Check(rows.Length == result.Records.Count(Ordinary), "原参照笔数改变");
}
static void AllBanks(FlowGenerationSnapshot fixture)
{
    var banks = new List<Bank>();
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    foreach (var pair in (IReadOnlyDictionary<string, long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
        banks.Add(new Bank { Id = pair.Value, Name = pair.Key, Type = field.Contains("Corporate") ? BankTypes.Corporate : BankTypes.Personal });
    foreach (var bank in banks)
    for (var seed = 1001; seed <= 1008; seed++)
    {
        using var scope = new SeedScope(seed); var request = Build(bank, fixture.Config, fixture.References, interest: true);
        var result = new FlowAutoGenerator().Generate(request); Validate(request, result);
        Check(result.ExpenseTotal > 20000 && result.OpeningBalance > 0, bank.Name + "仍被旧预算校验拦截");
        var type = typeof(SpeedEmulator.ViewModels.FlowGenerationViewModel); var vm = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("<Bank>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, request.Bank);
        type.GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, request.Config.Clone());
        object?[] args = [result, request.BankUser, request.InterestSetting, null];
        Check((bool)type.GetMethod("IsGenerationResultAccepted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args)!, bank.Name + "界面拒绝：" + args[3]);
        var corrected = new FlowAutoGenerator().ApplyFinalBalanceCorrection(request, result); Validate(request, corrected);
        Check(Snapshot(result) == Snapshot(corrected), bank.Name + "期末修正改写普通流水");
    }
    Check(banks.Count == 34, "未覆盖全部银行");
}
static void Timing()
{
    var fixedIncome = new GenerateConstRule { Index = 1, IsCheck = true, IncomeAttribute = "收入", MinMoney = 200, MaxMoney = 200, FixDay = "15", StartDay = 9, EndDay = 20 };
    var request = Build(new Bank { Id = 102, Name = "民生对公", Type = BankTypes.Corporate }, new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 2, 28) }, [Rule(2, false, 100, 500, 1)], [fixedIncome]);
    var rows = new[] { Row(request, request.ConstItems[0], new(2026, 1, 15, 10, 0, 0), 200), Row(request, request.ConstItems[0], new(2026, 2, 15, 10, 0, 0), 200),
        Row(request, request.References[0], new(2026, 1, 2, 10, 0, 0), -200), Row(request, request.References[0], new(2026, 2, 2, 10, 0, 0), -200) };
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Validate(request, result); Near(0, result.OpeningBalance);
    Check(result.Records.Count == 4 && result.ExpenseTotal == 400 && result.Records.Where(row => row.TradeMoney < 0).All(row => row.AccountTime!.Value.Day >= 15), "可合法顺延时不应补初或删流水");
}
static void Funded(FlowGenerationSnapshot fixture, List<ObservedRow> observed)
{
    var request = Request(fixture); var rows = observed.Where(row => row.Kind != "Interest").Select(row => Row(request, request.References.Single(rule => rule.Index == row.SourceRule), row.Time, row.Money == 30108.01 ? 30108.11 : row.Money)).ToArray();
    var before = JsonSerializer.Serialize(rows); var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 500000); Validate(request, result); Near(500000, result.OpeningBalance);
    Check(before == JsonSerializer.Serialize(rows), "修改调用方数据");
    Check(rows.Select(row => (row.AccountTime, row.TradeMoney)).SequenceEqual(result.Records.Select(row => (row.AccountTime, row.TradeMoney))), "资金足够仍改金额日期");
}
static void OptionalTiming()
{
    var income = new GenerateConstRule { Index = 1, IsCheck = true, IncomeAttribute = "收入", MinMoney = 500, MaxMoney = 500, FixDay = "2026-02-15", StartDay = 9, EndDay = 20 };
    var request = Build(new Bank { Id = 102, Name = "民生对公", Type = BankTypes.Corporate },
        new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 2, 28), AllInMoney = 500 }, [Rule(2, false, 100, 500)], [income]);
    var expense = Row(request, request.References[0], new(2026, 1, 5, 10, 0, 0), -300);
    expense.ExtraFields["__GeneratedBudgetWindowStart"] = "2026-01-01T00:00:00";
    expense.ExtraFields["__GeneratedBudgetWindowEnd"] = "2026-01-31T23:59:59";
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request,
        [expense, Row(request, request.ConstItems[0], new(2026, 2, 15, 9, 0, 0), 500)], 0);
    Validate(request, result); Near(0, result.OpeningBalance);
    Check(result.Records.Single(row => row.TradeMoney < 0).AccountTime >= new DateTime(2026, 2, 15), "可选支出应在合法全期窗口内顺延");
}
static void NegativeOpening()
{
    var request = Build(new Bank { Id = 102, Name = "民生对公", Type = BankTypes.Corporate },
        new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 1, 31) }, [Rule(1, true, 100, 1000)]);
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request,
        [Row(request, request.References[0], new(2026, 1, 2, 10, 0, 0), 200)], -100);
    Validate(request, result); Near(1, result.OpeningBalance);
}
static void FixedProjects()
{
    var fixedItem = new GenerateConstRule { Index = 3, IsCheck = true, IncomeAttribute = "支出", MinMoney = 777, MaxMoney = 777, FixDay = "15", StartDay = 9, EndDay = 20, TradeHoliday = false, TradeWeekend = false };
    var request = Build(new Bank { Id = 102, Name = "民生对公", Type = BankTypes.Corporate }, new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 3, 31), AllInMoney = 3000 }, [Rule(1, true, 1000, 6000, 1), Rule(2, false, 5000, 60000, 1)], [fixedItem]);
    var result = new FlowAutoGenerator().Generate(request); Validate(request, result);
    Check(result.Records.Count(row => row.TradeMoney == -777 && row.AccountTime!.Value.Day == 15) == 3, "固定日期项目未保留");
    Check(result.Records.Count(row => row.TradeMoney < -1000) == 3, "普通月支出被删掉");
}
static void CcbDates()
{
    foreach (var weekend in new[] { false, true }) foreach (var holiday in new[] { false, true })
    {
        var expense = Rule(2, false, 5000, 60000, 40); expense.TradeWeekend = weekend; expense.TradeHoliday = holiday;
        var income = Rule(1, true, 1000, 10000); income.TradeHoliday = holiday; income.TradeWeekend = weekend;
        var request = Build(new Bank { Id = 8, Name = "建行", Type = BankTypes.Personal }, new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 3, 31), AllInMoney = 30000 }, [income, expense]);
        var result = new FlowAutoGenerator().Generate(request); Validate(request, result); Check(result.Records.Count > 5, "合法日期有参照却未生成");
    }
}
static void Corrections(FlowGenerationSnapshot fixture)
{
    using var seed = new SeedScope(1001); var request = Request(fixture, true); var generator = new FlowAutoGenerator();
    var before = JsonSerializer.Serialize(request); var result = generator.Generate(request); var snapshot = Snapshot(result);
    foreach (var corrected in new[] { generator.ApplyNegativeBalanceCorrection(request, result), generator.ApplyFinalBalanceCorrection(request, result), generator.ApplyOpeningBalanceCorrection(request, result, result.OpeningBalance + 5000) })
    { Validate(request, corrected); Check(snapshot == Snapshot(corrected), "修正改写普通流水"); }
    Check(before == JsonSerializer.Serialize(request), "改写输入配置");
}
static void BoundaryDuplicates()
{
    var request = Build(new Bank { Id = 102, Name = "民生对公", Type = BankTypes.Corporate }, new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 12, 31), AllInMoney = 30000 }, [Rule(1, true, 5000, 60000, 1), Rule(2, false, 5000, 60000, 1)]);
    var rows = Enumerable.Range(1, 6).Select(month => Row(request, request.References[0], new(2026, month, 5, 10, 0, 0), 5000))
        .Concat(Enumerable.Range(7, 4).Select(month => Row(request, request.References[1], new(2026, month, 5, 10, 0, 0), -5000))).ToArray();
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Validate(request, result);
    Check(result.Records.Count == 10, "下限去重不应删除月次数");
}
static void CentPatterns()
{
    var request = Build(new Bank { Id = 102, Name = "民生对公", Type = BankTypes.Corporate }, new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 3, 31), AllInMoney = 50000 }, [Rule(1, true, 50000, 50000), Rule(2, false, 5, 1000, precision: 2)], interest: true);
    var rows = Enumerable.Range(0, 60).Select(index => Row(request, request.References[1], new DateTime(2026, 1, 2, 10, 0, 0).AddDays(index), -10.11))
        .Prepend(Row(request, request.References[0], new(2026, 1, 1, 9, 0, 0), 50000)).ToArray();
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Validate(request, result);
}
static void Imported()
{
    var request = Build(new Bank { Id = 102, Name = "民生对公", Type = BankTypes.Corporate }, new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 3, 31) }, [Rule(1, false, 5000, 60000, 1)], interest: true);
    var imported = new FlowRecord { BankId = 102, BankUserId = request.BankUser.Id, IsDocumentImported = true, AccountTime = new(2026, 1, 1, 9, 0, 0), TradeMoney = -18.01, SerialNum = "IMPORTED-SERIAL" };
    var rows = new[] { imported, Row(request, request.References[0], new(2026, 1, 5, 10, 0, 0), -12345) };
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Validate(request, result);
    var actual = result.Records.Single(row => row.IsDocumentImported); Near(-18.01, actual.TradeMoney!.Value);
    Check(actual.AccountTime == imported.AccountTime && actual.SerialNum == imported.SerialNum, "覆盖导入原值");
}
static void MonthlyModes(FlowGenerationSnapshot fixture)
{
    foreach (var mode in new[] { 1, 2 })
    {
        var request = Request(fixture); request.Config.SelectIndex = mode;
        object?[] args = [request, request.Config.StartTime, request.Config.EndTime, 0d, new Random(1), 1, null];
        Check(!(bool)typeof(FlowAutoGenerator).GetMethod("TryGenerateReferencePriorityResult", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!, "泄露到明确月度预算模式");
    }
}
static void Healthy()
{
    var request = Build(new Bank { Id = 3, Name = "个人农商", Type = BankTypes.Personal }, new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 3, 31), OpeningBalance = 1000000 }, [Rule(1, false, 5, 300, 20)]);
    var result = new FlowAutoGenerator().Generate(request); Validate(request, result); Near(1000000, result.OpeningBalance);
    Check(!FlowAutoGenerator.UsesReferencePriority(request.Config, result.Records) && result.Records.Count == 60, "改变已有健康路径");
}
sealed class ObservedRow { public DateTime Time { get; set; } public double Money { get; set; } public string Kind { get; set; } = ""; public int? SourceRule { get; set; } }
sealed class SeedScope : IDisposable
{
    private readonly string? previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    public SeedScope(int seed) => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString(CultureInfo.InvariantCulture));
    public void Dispose() => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous);
}
