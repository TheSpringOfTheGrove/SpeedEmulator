using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var fixtures = JsonSerializer.Deserialize<List<Fixture>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reported-configs.json")))!;
var summaries = new List<object>();
var tests = new (string Name, Action Run)[]
{
    ("农商实际300万配置：保留全年参照，日期分散且仅真实缺口补初", () => Reported(fixtures.Single(f => f.Bank.Id == 3), 12, summaries)),
    ("招行实际配置：完整月份有普通支出且收入不跨月换来源", () => Reported(fixtures.Single(f => f.Bank.Id == 16), 5, summaries)),
    ("建行对公实际配置：后期月份保留普通收支", () => Reported(fixtures.Single(f => f.Bank.Id == 113), 5, summaries)),
    ("工行对公大额配置：参照优先保留全年事件及金额区间", () => Sparse(fixtures.Single(f => f.Bank.Id == 104), summaries)),
    ("34类银行：普通预算不足也为后续月份留款", AllBanks),
    ("失败的普通付款不能搬走后月收入或更换来源", FailedDebit),
    ("只有按月收入时也不能提前用完全年进账额度", IncomeOnly),
    ("后月按月收入不能替换参照提前，硬缺口只补最小期初", PreserveIncomeIdentity),
    ("月中可用资金产生后，延后支出选择不同合法日期", SpreadDeferredDebits),
    ("显式月度预算模式保留每月收支总额", ExplicitMonthlyBudgets),
    ("资金充足时保留原次数及完整金额区间", Funded)
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
}
Directory.CreateDirectory("tmp/timeline-repair-20261102");
File.WriteAllText("tmp/timeline-repair-20261102/timeline-tests.json", JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{tests.Length - failures}/{tests.Length} timeline tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
static void Near(double expected, double actual) => Check(Math.Abs(expected - actual) < .009, $"expected={expected}, actual={actual}");
static DateTime Month(DateTime date) => new(date.Year, date.Month, 1);
static bool Ordinary(FlowRecord row) => !FlowGeneratedRowKinds.IsSystemInterest(row);
static double Meta(FlowRecord row, string key) => double.TryParse(row.ExtraFields.GetValueOrDefault(key), CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
static bool Variable(FlowRecord row) => Ordinary(row) && Meta(row, "__GeneratedAmountMax") > Meta(row, "__GeneratedAmountMin") + .000001;
static GenerateReferenceRule Rule(int index, bool income, double min, double max, int monthly = 0) => new()
{
    Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出", MinMoney = min, MaxMoney = max,
    PercentMonth = monthly, FloutLength = 0, StartDay = 9, EndDay = 20, TradeHoliday = true, TradeWeekend = true
};
static FlowAutoGenerationRequest Request(Bank bank, FlowGenerationConfig config, IReadOnlyList<GenerateReferenceRule> refs, IReadOnlyList<GenerateConstRule>? fixedItems = null)
{
    foreach (var name in new[] { "ConfigureFlowGenerationColumns", "ConfigureFlowRecordColumns" })
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [bank]);
    return new FlowAutoGenerationRequest
    {
        Bank = bank, BankUser = new BankUser { Id = 9000 + bank.Id, BankId = bank.Id, AccountNo = "9000000000000000", AutoCalculateInterest = true },
        Config = config.Clone(), References = refs.Select(rule => rule.Clone()).ToList(), ConstItems = fixedItems?.Select(rule => rule.Clone()).ToList() ?? [],
        InterestSetting = BankInterestSettingDefaults.CreateDefault(bank)
    };
}
static FlowAutoGenerationRequest FixtureRequest(Fixture fixture) => Request(new Bank { Id = fixture.Bank.Id, Name = fixture.Bank.Name, Type = fixture.Bank.Type }, fixture.Config, fixture.References, fixture.ConstItems);
static FlowRecord Row(FlowAutoGenerationRequest request, FlowRuleBase rule, DateTime time, double amount) =>
    (FlowRecord)typeof(FlowAutoGenerator).GetMethod("CreateRecordFromRule", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [request, rule, rule is GenerateConstRule ? request.Bank.ConstColumns : request.Bank.ReferenceColumns, time, amount])!;
static void Validate(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
{
    Check(result.MinimumBalance >= -.009 && !result.RequiresOpeningBalanceCorrection, "存在负余额");
    var balance = result.OpeningBalance;
    foreach (var row in result.Records)
    {
        balance = Math.Round(balance + (row.TradeMoney ?? 0), 2, MidpointRounding.AwayFromZero); Near(balance, row.Balance!.Value);
        if (!Ordinary(row) || row.IsDocumentImported) continue;
        var amount = Math.Abs(row.TradeMoney ?? 0); var min = Meta(row, "__GeneratedAmountMin"); var max = Meta(row, "__GeneratedAmountMax");
        if (double.IsNaN(min)) continue;
        Check(amount >= min - .000001 && amount <= max + .000001, "突破原参照金额范围");
        var unit = Meta(row, "__GeneratedAmountUnit");
        Check(Math.Abs(amount / unit - Math.Round(amount / unit)) < .000001, "改变原精度");
        if (row.ExtraFields.TryGetValue("__GeneratedBudgetWindowStart", out var from))
            Check(row.AccountTime >= DateTime.Parse(from, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), "离开已分配月份起点");
        if (row.ExtraFields.TryGetValue("__GeneratedBudgetWindowEnd", out var to))
            Check(row.AccountTime <= DateTime.Parse(to, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), "离开已分配月份终点");
        if (row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind") == "Reference")
        {
            var rule = request.References.Single(rule => rule.Index.ToString() == row.ExtraFields["__GeneratedSourceIndex"]);
            var allowed = request.Bank.Id == 8 ? ConstructionBankGenerationPolicy.IsReferenceDateAllowed(row.AccountTime!.Value, rule)
                : ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(row.AccountTime!.Value, rule);
            Check(allowed, "违反该银行周末节假日限制");
        }
    }
    Near(balance, result.FinalBalance);
    var tails = result.Records.Where(row => Variable(row) && Meta(row, "__GeneratedAmountPrecision") == 2)
        .Select(row => (int)Math.Round(Math.Abs(row.TradeMoney ?? 0) * 100) % 100).ToArray();
    Check(tails.Zip(tails.Skip(1)).All(pair => pair.First > 9 || pair.Second > 9), "低尾数不能连续出现");
    Check(tails.Count(tail => tail is >= 1 and <= 9) <= Math.Max(1, (int)Math.Floor(tails.Length * .02)),
        $"有调整空间时应降低低尾数频率: bank={request.Bank.Name}, low={tails.Count(tail=>tail is >=1 and <=9)}, total={tails.Length}");
}
static void Reported(Fixture fixture, int seeds, List<object> summaries)
{
    for (var seed = 1001; seed < 1001 + seeds; seed++)
    {
        using var scope = new SeedScope(seed); var request = FixtureRequest(fixture); var before = JsonSerializer.Serialize(request);
        var generator = new FlowAutoGenerator(); var result = generator.Generate(request); Validate(request, result);
        var referencePriority = FlowAutoGenerator.UsesReferencePriority(request.Config, result.Records);
        if (referencePriority)
        {
            var cumulative = request.Config.OpeningBalance; var minimum = cumulative;
            foreach (var row in result.Records.Where(Ordinary)) { cumulative += row.TradeMoney ?? 0; minimum = Math.Min(minimum, cumulative); }
            var needed = Math.Max(0, -minimum);
            Check(result.OpeningBalance >= request.Config.OpeningBalance + needed - .009
                && result.OpeningBalance <= request.Config.OpeningBalance + needed + 1.01,
                $"补初超过实际缺口与安全余量：seed={seed}, opening={result.OpeningBalance:F2}, needed={needed:F2}");
        }
        else Near(request.Config.OpeningBalance, result.OpeningBalance);
        var fullMonths = new List<DateTime>();
        for (var month = Month(request.Config.StartTime); month.AddMonths(1) <= request.Config.EndTime.Date.AddDays(1); month = month.AddMonths(1))
            if (month >= request.Config.StartTime.Date) fullMonths.Add(month);
        var rows = result.Records.Where(Ordinary).ToArray();
        var missing = fullMonths.Where(month => !rows.Any(row => row.TradeMoney < 0 && Variable(row) && Month(row.AccountTime!.Value) == month)).ToArray();
        if (missing.Length > 0)
        {
            Directory.CreateDirectory("tmp/timeline-repair-20261102");
            File.WriteAllText($"tmp/timeline-repair-20261102/missing-{fixture.Bank.Id}-{seed}.json", JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }
        var allExpenseDatesAllowed = request.References.Where(rule => rule.IsCheck && rule.IncomeAttribute == "支出" && rule.PercentMonth > 0)
            .All(rule => rule.TradeHoliday == true && rule.TradeWeekend == true);
        if (!referencePriority || allExpenseDatesAllowed)
            Check(missing.Length == 0, $"{fixture.Bank.Id},seed={seed}完整月份普通支出断档：{string.Join(',', missing.Select(month => month.ToString("yyyy-MM")))}");
        else
        {
            var expenses = rows.Where(row => row.TradeMoney < 0 && Variable(row)).ToArray();
            Check(expenses.Any(row => row.AccountTime < request.Config.StartTime.AddMonths(3))
                && expenses.Any(row => row.AccountTime > request.Config.EndTime.AddMonths(-3)), "日历筛选后普通支出仍只集中在前段或后段");
        }
        if (!referencePriority)
            Check(fullMonths.All(month => rows.Any(row => row.TradeMoney > 0 && Month(row.AccountTime!.Value) == month)), "按月收入被前期消耗");
        var peak = rows.GroupBy(row => row.AccountTime!.Value.Date).Max(group => group.Count());
        Check(peak < 25, $"日期集中：单日{peak}条");
        var ordinaryIncome = rows.Where(row => row.TradeMoney > 0).Sum(row => row.TradeMoney!.Value);
        Check(ordinaryIncome >= request.Config.AllInMoney - Math.Max(20, request.Config.AllInMoney * .001), "可由合法已有收入吸收的尾差丢失");
        foreach (var group in rows.Where(row => row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind") == "Reference")
                     .GroupBy(row => (Source: row.ExtraFields["__GeneratedSourceIndex"], Month: Month(row.AccountTime!.Value))))
        {
            var rule = request.References.Single(rule => rule.Index.ToString() == group.Key.Source);
            if (rule.PercentMonth > 0) Check(group.Count() <= rule.PercentMonth, "擅自增加每月次数");
        }
        if (fixture.Bank.Id == 3)
        {
            Check(result.ExpenseTotal > (ordinaryIncome - request.Config.LastMoney) * .99, "合法剩余支出预算没有利用");
            Check(fullMonths.All(month => rows.Count(row => row.TradeMoney == -2 && Month(row.AccountTime!.Value) == month) >= 2), "固定金额费用被删");
        }
        var snapshot = JsonSerializer.Serialize(result.Records);
        var repeated = generator.ApplyNegativeBalanceCorrection(request, result);
        Check(snapshot == JsonSerializer.Serialize(repeated.Records), "重复修正又改变月份、金额或编号");
        Check(before == JsonSerializer.Serialize(request), "修改了用户配置");
        summaries.Add(new { BankId = fixture.Bank.Id, Seed = seed, Rows = rows.Length, result.OpeningBalance, OrdinaryIncome = ordinaryIncome,
            result.ExpenseTotal, PeakDailyRows = peak, Months = rows.GroupBy(row => Month(row.AccountTime!.Value)).OrderBy(group => group.Key)
                .Select(group => new { Month = group.Key.ToString("yyyy-MM"), Income = group.Count(row => row.TradeMoney > 0),
                    VariableExpense = group.Count(row => row.TradeMoney < 0 && Variable(row)) }) });
    }
}
static void Sparse(Fixture fixture, List<object> summaries)
{
    for (var seed = 1001; seed <= 1010; seed++)
    {
        using var scope = new SeedScope(seed); var request = FixtureRequest(fixture); var result = new FlowAutoGenerator().Generate(request); Validate(request, result);
        Check(FlowAutoGenerator.UsesReferencePriority(request.Config, result.Records) && result.OpeningBalance > 0, "低预算大额月参照未按新优先级保留");
        var months = result.Records.Where(row => Variable(row) && row.TradeMoney < 0).Select(row => Month(row.AccountTime!.Value)).Distinct().OrderBy(month => month).ToArray();
        Check(months.Length >= 2, "预算可支付两笔最低普通支出，却没有分散事件");
        var middle = request.Config.StartTime.AddTicks((request.Config.EndTime - request.Config.StartTime).Ticks / 2);
        Check(months.Any(month => month < middle) && months.Any(month => month >= middle), "少量可负担事件仍集中在同一半段");
        Check(result.Records.Count(row => Variable(row) && row.TradeMoney < 0) > 20, "月参照仍被删成几笔");
        summaries.Add(new { BankId = fixture.Bank.Id, Seed = seed, result.OpeningBalance, ExpenseMonths = months.Select(month => month.ToString("yyyy-MM")), result.ExpenseTotal });
    }
}
static void AllBanks()
{
    var count = 0;
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    foreach (var bank in ((IReadOnlyDictionary<string, long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
                 .Select(pair => new Bank { Id = pair.Value, Name = pair.Key, Type = field.Contains("Corporate") ? BankTypes.Corporate : BankTypes.Personal }))
    {
        var request = Request(bank, new FlowGenerationConfig { StartTime = new(2025, 1, 1), EndTime = new(2025, 12, 31), AllInMoney = 3000, LastMoney = 1000 },
            [Rule(1, true, 1000, 4000), Rule(2, false, 100, 500, 1)]);
        request.BankUser.AutoCalculateInterest = false; request.References[1].TradeHoliday = false; request.References[1].TradeWeekend = false;
        bool Allowed(DateTime day) => bank.Id == 8 ? ConstructionBankGenerationPolicy.IsReferenceDateAllowed(day, request.References[1])
            : ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day, request.References[1]);
        var rows = Enumerable.Range(1, 12).Select(month => Enumerable.Range(15, 12).Select(day => new DateTime(2025, month, day)).First(Allowed))
            .Select(day => Row(request, request.References[1], day.AddHours(12), -500)).Append(Row(request, request.References[0], new(2025, 12, 23, 12, 0, 0), 3000)).ToArray();
        var before = JsonSerializer.Serialize(rows); var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Validate(request, result); Near(0, result.OpeningBalance);
        var months = result.Records.Where(row => row.TradeMoney < 0).Select(row => row.AccountTime!.Value.Month).Distinct().Order().ToArray();
        Check(months.Length >= 3 && months[0] <= 4 && months[^1] >= 10, $"{bank.Name}普通支出仍挤占全年预算：{string.Join(',', months)}");
        Check(result.FinalBalance >= 999.99, "挤占期末预留"); Check(before == JsonSerializer.Serialize(rows), "修改调用方流水"); count++;
    }
    Check(count == 34, "必须覆盖全部银行");
}
static void FailedDebit()
{
    var request = Request(new Bank { Id = 4, Name = "工行", Type = BankTypes.Personal },
        new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 2, 28), AllInMoney = 2000 },
        [Rule(1, true, 1000, 3000, 1), Rule(2, true, 1000, 5000), Rule(3, false, 100, 1000, 1)]);
    request.BankUser.AutoCalculateInterest = false;
    var time = new DateTime(2026, 2, 15, 10, 0, 0);
    var rows = new[] { Row(request, request.References[2], new(2026, 1, 10, 12, 0, 0), -500), Row(request, request.References[0], time, 2000) };
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Near(0, result.OpeningBalance);
    var income = result.Records.Single(row => row.TradeMoney > 0);
    Check(income.AccountTime == time && income.ExtraFields["__GeneratedSourceIndex"] == "1", "失败的付款偷搬或替换了后月收入");
    Check(result.ExpenseTotal == 0, "不能跨月伪造可用资金");
}
static void IncomeOnly()
{
    using var scope = new SeedScope(1001);
    var request = Request(new Bank { Id = 16, Name = "招行", Type = BankTypes.Personal },
        new FlowGenerationConfig { StartTime = new(2025, 1, 1), EndTime = new(2025, 12, 31), AllInMoney = 30000 },
        [Rule(1, true, 500, 3000, 2)]);
    request.BankUser.AutoCalculateInterest = false;
    var result = new FlowAutoGenerator().Generate(request); Validate(request, result); Near(0, result.OpeningBalance);
    Check(result.Records.Where(row => row.TradeMoney > 0).Select(row => Month(row.AccountTime!.Value)).Distinct().Count() == 12,
        "进账预算提前用尽，后期收入月份缺失");
    Check(result.IncomeTotal is >= 29990 and <= 30000, "合法收入尾差未吸收");
}
static void PreserveIncomeIdentity()
{
    var request = Request(new Bank { Id = 4, Name = "工行", Type = BankTypes.Personal },
        new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 2, 28), AllInMoney = 20000 },
        [Rule(1, true, 1000, 30000, 1), Rule(2, true, 10000, 50000)],
        [new GenerateConstRule { Index = 3, IsCheck = true, IncomeAttribute = "支出", MinMoney = 1000, MaxMoney = 1000, FixDay = "2", StartDay = 9, EndDay = 20 }]);
    request.BankUser.AutoCalculateInterest = false;
    var rows = new[] { Row(request, request.ConstItems[0], new(2026, 1, 2, 10, 0, 0), -1000), Row(request, request.References[0], new(2026, 2, 15, 10, 0, 0), 20000) };
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Near(1000, result.OpeningBalance);
    var income = result.Records.Single(row => row.TradeMoney > 0);
    Check(income.AccountTime!.Value.Month == 2 && income.ExtraFields["__GeneratedSourceIndex"] == "1", "后月按月来源不能替换提前");
}
static void SpreadDeferredDebits()
{
    var request = Request(new Bank { Id = 7, Name = "中信", Type = BankTypes.Personal },
        new FlowGenerationConfig { StartTime = new(2026, 1, 1), EndTime = new(2026, 1, 31), AllInMoney = 10000 }, [Rule(1, false, 100, 900)],
        [new GenerateConstRule { Index = 2, IsCheck = true, IncomeAttribute = "收入", MinMoney = 10000, MaxMoney = 10000, FixDay = "15", StartDay = 9, EndDay = 20 }]);
    request.BankUser.AutoCalculateInterest = false;
    var rows = Enumerable.Range(0, 12).Select(index => Row(request, request.References[0], new(2026, 1, 2, 10, index, 0), -500))
        .Append(Row(request, request.ConstItems[0], new(2026, 1, 15, 9, 0, 0), 10000)).ToArray();
    var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Validate(request, result); Near(0, result.OpeningBalance);
    Check(result.Records.Where(row => row.TradeMoney < 0).Select(row => row.AccountTime!.Value.Date).Distinct().Count() >= 8, "延后付款集中于同一天");
}
static void ExplicitMonthlyBudgets()
{
    foreach (var mode in new[] { 1, 2 })
    foreach (var id in new long[] { 3, 16, 104, 113 })
    {
        var request = Request(new Bank { Id = id, Name = "测试银行", Type = id < 100 ? BankTypes.Personal : BankTypes.Corporate },
            new FlowGenerationConfig { SelectIndex = mode, StartTime = new(2025, 1, 1), EndTime = new(2025, 12, 31), AllInMoney = 24000 },
            [Rule(1, true, 1000, 3000, 1), Rule(2, false, 100, 600, 2)]);
        request.BankUser.AutoCalculateInterest = false;
        var rows = Enumerable.Range(1, 12).SelectMany(month => new[] { Row(request, request.References[0], new(2025, month, 10, 12, 0, 0), 2000),
            Row(request, request.References[1], new(2025, month, 3, 12, 0, 0), -300), Row(request, request.References[1], new(2025, month, 20, 12, 0, 0), -450) }).ToArray();
        var result = new FlowAutoGenerator().ApplyAutomaticOpeningBalance(request, rows, 0); Validate(request, result); Near(0, result.OpeningBalance);
        foreach (var month in result.Records.GroupBy(row => Month(row.AccountTime!.Value)))
        {
            Near(2000, month.Where(row => row.TradeMoney > 0).Sum(row => row.TradeMoney!.Value));
            Near(750, -month.Where(row => row.TradeMoney < 0).Sum(row => row.TradeMoney!.Value));
        }
    }
}
static void Funded()
{
    using var seed = new SeedScope(1001);
    var request = Request(new Bank { Id = 3, Name = "个人农商", Type = BankTypes.Personal },
        new FlowGenerationConfig { StartTime = new(2025, 1, 1), EndTime = new(2025, 12, 31), OpeningBalance = 1000000 }, [Rule(1, false, 5, 300, 20)]);
    request.BankUser.AutoCalculateInterest = false; var result = new FlowAutoGenerator().Generate(request); Validate(request, result); Near(1000000, result.OpeningBalance);
    Check(result.Records.Count == 240 && result.Records.GroupBy(row => Month(row.AccountTime!.Value)).All(group => group.Count() == 20), "资金充足时不应减少次数");
    var average = result.Records.Average(row => Math.Abs(row.TradeMoney!.Value));
    Check(average is > 130 and < 175, "资金充足时不能压缩金额范围");
}
sealed class Fixture
{
    public Bank Bank { get; set; } = new();
    public FlowGenerationConfig Config { get; set; } = new();
    public List<GenerateReferenceRule> References { get; set; } = [];
    public List<GenerateConstRule> ConstItems { get; set; } = [];
}
sealed class SeedScope : IDisposable
{
    private readonly string? previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    public SeedScope(int seed) => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString(CultureInfo.InvariantCulture));
    public void Dispose() => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous);
}
