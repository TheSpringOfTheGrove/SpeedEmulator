using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;
using SpeedEmulator.ViewModels;

var tests = new (string Name, Action Run)[]
{
    ("勾选为允许、普通工作日始终可选、重合日期分别满足两个开关", TestCalendar),
    ("未设置的开关按界面未勾选处理", TestUnsetFlags),
    ("固定日期项目不受参照日期开关影响，候选缓存不串用", TestCandidateCache),
    ("随机重试耗尽后也不能退回被禁止日期", TestRandomFallback),
    ("旧版前后挪动日期同样过滤节假日", TestLegacyMoves),
    ("个人建行截图起止日期的最后月份可在工作日满足必现次数", TestCcbEndingMonth),
    ("34类银行、3种分配模式、4种开关组合均使用统一日期许可", TestBankMatrix),
    ("34类银行固定日期项目保持原日期和金额", TestFixedProjects)
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} calendar tests passed.");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static GenerateReferenceRule Rule(int index, bool income, bool? weekend, bool? holiday, int monthly = 0) => new()
{
    Index = index, IsCheck = true, IncomeAttribute = income ? "收入" : "支出",
    MinMoney = income ? 300 : 10, MaxMoney = income ? 1200 : 200,
    FloutLength = 0, PercentMonth = monthly, StartDay = 9, EndDay = 20,
    TradeWeekend = weekend, TradeHoliday = holiday, ProductBrief = "日历测试", OppositeUsername = "TEST-ONLY"
};

static IEnumerable<(bool Weekend, bool Holiday)> Flags() =>
    new[] { (false, false), (false, true), (true, false), (true, true) };

static void TestCalendar()
{
    var weekday = new DateTime(2025, 10, 9);
    var holiday = new DateTime(2025, 10, 2);
    var weekend = new DateTime(2025, 10, 18);
    var overlap = new DateTime(2025, 10, 4);
    foreach (var flags in Flags())
    {
        var rule = Rule(1, false, flags.Weekend, flags.Holiday);
        foreach (var filter in new Func<DateTime, FlowRuleBase, bool>[]
        {
            FlowGenerationCalendar.IsDateAllowed, ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed,
            ConstructionBankGenerationPolicy.IsReferenceDateAllowed, IcbcGenerationPolicy.IsReferenceDateAllowed
        })
        {
            Check(filter(weekday, rule), "勾选不能排除普通工作日");
            Check(filter(holiday, rule) == flags.Holiday, "工作日节假日的许可错误");
            Check(filter(weekend, rule) == flags.Weekend, "普通周末的许可错误");
            Check(filter(overlap, rule) == (flags.Weekend && flags.Holiday), "周末与节假日重合须两个开关都允许");
        }
    }
}

static void TestUnsetFlags()
{
    var rule = Rule(1, false, null, null);
    Check(FlowGenerationCalendar.IsDateAllowed(new(2026, 1, 5), rule), "空开关不能禁用普通工作日");
    Check(!FlowGenerationCalendar.IsDateAllowed(new(2026, 1, 1), rule), "空节假日开关不能视为已勾选");
    Check(!FlowGenerationCalendar.IsDateAllowed(new(2026, 1, 4), rule), "空周末开关不能视为已勾选");
}

static void TestCandidateCache()
{
    var type = typeof(FlowAutoGenerator).GetNestedType("NativeScheduleState", BindingFlags.NonPublic)!;
    var method = type.GetMethod("GetCandidateDates")!;
    var rule = Rule(1, false, false, false);
    var fixedRule = new GenerateConstRule { Index = 1, StartDay = 9, EndDay = 20, TradeWeekend = false, TradeHoliday = false };
    foreach (var fixedFirst in new[] { false, true })
    {
        var state = Activator.CreateInstance(type, [0])!;
        IReadOnlyList<DateTime> Dates(FlowRuleBase source) => (IReadOnlyList<DateTime>)method.Invoke(state,
            [new DateTime(2026, 1, 1), new DateTime(2026, 1, 7), source])!;
        if (fixedFirst) Dates(fixedRule); else Dates(rule);
        Check(Dates(fixedRule).Count == 7, "固定日期项目错误套用参照日期限制");
        Check(Dates(rule).Select(date => date.Day).SequenceEqual(new[] { 5, 6, 7 }), "候选缓存使固定项目与普通参照串用日期");
    }
}

static MethodInfo Method(string name) => typeof(FlowAutoGenerator).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

static void TestRandomFallback()
{
    var rule = Rule(1, false, false, false);
    var time = (DateTime)Method("PickTime").Invoke(null,
        [new DateTime(2026, 1, 1), new DateTime(2026, 1, 5, 23, 59, 59), rule, new FirstDateRandom()])!;
    Check(time.Date == new DateTime(2026, 1, 5) && time.Hour is >= 9 and <= 20, "随机重试后的日期或时段越界");
    try
    {
        Method("PickTime").Invoke(null, [new DateTime(2026, 1, 1), new DateTime(2026, 1, 4, 23, 59, 59), rule, new FirstDateRandom()]);
    }
    catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException("没有合法日期时不能用随机兜底伪造日期");
}

static void TestLegacyMoves()
{
    var rule = Rule(1, false, false, false);
    var before = Method("PickLatestRuleTimeBefore").Invoke(null,
        [rule, new DateTime(2026, 1, 1), new DateTime(2026, 1, 5), new HashSet<DateTime>(), new Random(21), 0]);
    var after = Method("PickLatestRuleTimeAfter").Invoke(null,
        [rule, new DateTime(2026, 1, 1), new DateTime(2026, 1, 4, 23, 59, 59), new HashSet<DateTime>(), new Random(21), 0]);
    Check(before is null && after is null, "日期修复不能挪入被禁止的节假日或周末");
}

static Bank[] Banks() => new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" }.SelectMany(field =>
    ((IReadOnlyDictionary<string, long>)typeof(MainViewModel).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
    .Select(pair => new Bank { Id = pair.Value, Name = pair.Key, Type = field.Contains("Personal") ? BankTypes.Personal : BankTypes.Corporate })).ToArray();

static FlowGenerationConfig Config(int mode = 0) => new()
{
    StartTime = new(2026, 1, 1), EndTime = new(2026, 1, 31), OpeningBalance = 1000000,
    AllInMoney = 10000, LastMoney = 5000, SelectIndex = mode,
    MinInMoneyMonth2 = 10000, MaxInMoneyMonth2 = 10000, MinOutMoneyMonth2 = 5000, MaxOutMoneyMonth2 = 5000,
    MonthGenData = [new MonthGenerateRule { StartTime = new(2026, 1, 1), EndTime = new(2026, 1, 31), InMoney = 10000, OutMoney = 5000 }]
};

static FlowAutoGenerationRequest Request(Bank bank, FlowGenerationConfig config, GenerateReferenceRule[] rules, GenerateConstRule[]? fixedRules = null) => new()
{
    Bank = bank, BankUser = new BankUser { Id = 990008, BankId = bank.Id, BankName = bank.Name, AutoCalculateInterest = false },
    Config = config, References = rules, ConstItems = fixedRules ?? []
};

static void Validate(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
{
    Check(result.Records.Count > 0, "未生成任何测试数据");
    foreach (var row in result.Records.Where(row => row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind") == "Reference"))
    {
        var rule = request.References.Single(rule => rule.Index.ToString() == row.ExtraFields["__GeneratedSourceIndex"]);
        Check(row.AccountTime is { } date && date.Date >= request.Config.StartTime.Date && date.Date <= request.Config.EndTime.Date
            && FlowGenerationCalendar.IsDateAllowed(date, rule), $"{request.Bank.Name}的模式{request.Config.SelectIndex}生成了被排除日期：{row.AccountTime}");
        Check(row.AccountTime!.Value.Hour >= rule.StartDay && row.AccountTime.Value.Hour <= rule.EndDay, "交易时段越界");
        var amount = Math.Abs(row.TradeMoney ?? 0);
        Check(amount >= rule.MinMoney - .009 && amount <= rule.MaxMoney + .009, "日期调整改变了金额范围");
    }
    Check(result.MinimumBalance >= -.009, "生成出现负余额");
}

static void TestCcbEndingMonth()
{
    var config = Config(); config.StartTime = new(2025, 11, 4); config.EndTime = new(2026, 12, 4);
    config.AllInMoney = 100000; config.LastMoney = 5000;
    var monthly = Rule(1, false, true, true, 1); monthly.MinMoney = 2; monthly.MaxMoney = 60;
    var request = Request(new Bank { Id = 8, Name = "建行", Type = BankTypes.Personal }, config,
        [monthly, Rule(2, false, true, true), Rule(3, true, true, true)]);
    using var seed = new CalendarSeed(44061);
    var result = new FlowAutoGenerator().Generate(request); Validate(request, result);
    var required = result.Records.Where(row => row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind") == "Reference"
        && row.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex") == "1").ToArray();
    Check(required.Length == 14 && required.GroupBy(row => (row.AccountTime!.Value.Year, row.AccountTime.Value.Month)).All(group => group.Count() == 1), "勾选开关改变每月次数");
    Check(required.Any(row => row.AccountTime!.Value.Year == 2026 && row.AccountTime.Value.Month == 12), "尾月工作日没有生成必现流水");
}

static void TestBankMatrix()
{
    var count = 0;
    var failures = new List<string>();
    foreach (var bank in Banks())
    foreach (var mode in new[] { 0, 1, 2 })
    foreach (var flags in Flags())
    {
        try
        {
            using var seed = new CalendarSeed(73061);
            var request = Request(bank, Config(mode), [Rule(1, true, flags.Weekend, flags.Holiday), Rule(2, false, flags.Weekend, flags.Holiday)]);
            var before = JsonSerializer.Serialize(request);
            var result = new FlowAutoGenerator().Generate(request); Validate(request, result);
            Check(JsonSerializer.Serialize(request) == before, "生成修改了原配置");
            count++;
        }
        catch (Exception error) { failures.Add($"{bank.Name}/mode={mode}/weekend={flags.Weekend}/holiday={flags.Holiday}: {error.Message}"); }
    }
    Console.WriteLine($"  Bank matrix: {count}/408 successful");
    Check(failures.Count == 0, string.Join(Environment.NewLine, failures));
}

static void TestFixedProjects()
{
    foreach (var bank in Banks())
    {
        using var seed = new CalendarSeed(73062);
        var config = Config(); config.AllInMoney = config.LastMoney = 1000;
        var rule = new GenerateConstRule { Index = 1, IsCheck = true, IncomeAttribute = "收入", MinMoney = 1000, MaxMoney = 1000,
            FloutLength = 0, FixDay = "3", ReCnt = "1", StartDay = 9, EndDay = 20, TradeWeekend = false, TradeHoliday = false };
        var result = new FlowAutoGenerator().Generate(Request(bank, config, [], [rule]));
        Check(result.Records.Count == 1 && result.Records[0].AccountTime!.Value.Date == new DateTime(2026, 1, 3)
            && result.Records[0].TradeMoney == 1000, bank.Name + "的固定日期被参照开关挪动或改变金额");
    }
}

sealed class FirstDateRandom : Random
{
    public override int Next(int minValue, int maxValue) => minValue;
}

sealed class CalendarSeed : IDisposable
{
    private readonly string? previous = Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    public CalendarSeed(int seed) => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", seed.ToString());
    public void Dispose() => Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED", previous);
}
