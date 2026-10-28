using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("34类银行目录仅个人支付宝启用，其他银行逐字段不变", TestScope),
    ("普通消费、亲情卡、转账、还款和理财格式", TestFormats),
    ("商户收款收入也有订单号，个人转账支出保留空白", TestClassification),
    ("字段独立补空，固定值、公式、占位符和导入数据保留", TestOwnership),
    ("最终日期、跨月时段修正及克隆后号码同步", TestDateRefresh),
    ("退款按明确原单关联并复用商家号，原单移动后同步", TestRefund),
    ("修改或清除明确原单后重新关联，不沿用旧关系", TestRelationChange),
    ("缺少明确关系时不按相同金额或商户强配原单", TestMissingRelation),
    ("拼多多延期扣款引用原单且金额分值同步", TestDeferred),
    ("延期业务缺原单或下单日期使用独立模拟号", TestDeferredFallback),
    ("缴费项目跨月复用，收益日期前后两种格式", TestStableProfiles),
    ("批次碰撞、克隆ID和同日多条收益均去重", TestCollisions),
    ("5000条自动流水号及新订单去重且批次复核幂等", TestVolume),
    ("公式保存与追加时保留旧流水且为新号去重", TestSave),
    ("完整参照和固定项目生成使用最终日期且配置不变", TestGeneration),
    ("三个余额修正入口均刷新自动号并保留指定号", TestCorrections),
    ("原单上下文可由参照自定义字段传入", TestRuleContext)
};
var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {e}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} Alipay tests passed.");
return failed == 0 ? 0 : 1;

static Type Rules() => typeof(FlowAutoGenerator).Assembly.GetType("SpeedEmulator.Services.AlipayGeneratedNumberRules")!;
static object? Invoke(string method, params object[] args) => Rules().GetMethod(method, BindingFlags.Static | BindingFlags.Public)!.Invoke(null, args);
static void Apply(Bank bank, FlowRecord row) => Invoke("Apply", bank, row);
static bool Complete(Bank bank, params FlowRecord[] rows) => (bool)Invoke("Complete", bank, rows)!;
static Bank Bank()
{
    var bank = new Bank { Id = 1, Name = "支付宝", Type = BankTypes.Personal };
    foreach (var (name, field) in new[] { ("流水号", nameof(FlowRecord.SerialNum)), ("商家订单号", nameof(FlowRecord.MerchantName)),
                 ("备注", nameof(FlowRecord.Remark)), ("商品说明", nameof(FlowRecord.ProductBrief)), ("交易对方", nameof(FlowRecord.OppositeUsername)) })
        bank.FlowColumns.Add(new ColumnDefinition { Name = name, Field = field });
    return bank;
}
static FlowRecord Row(string product, double amount = -15.70, string party = "模拟商户") => new()
{
    BankId = 1, BankUserId = 42, Account = "simulation-account", AccountTime = new DateTime(2026, 10, 20, 12, 34, 56),
    ProductBrief = product, OppositeUsername = party, TradeMoney = amount, CashCheck = "转账", TradeChannel = "花呗",
    IncomeAttribute = amount < 0 ? "支出" : "收入"
};
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Match(string actual, string pattern) => Check(Regex.IsMatch(actual, "^(?:" + pattern + ")$"), $"{actual} 不符合 {pattern}");
static string Snapshot(object value) => JsonSerializer.Serialize(value);

static void TestScope()
{
    var count = 0;
    var matches = 0;
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    {
        var catalog = (IReadOnlyDictionary<string, long>)typeof(SpeedEmulator.ViewModels.MainViewModel)
            .GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var pair in catalog)
        {
            var bank = Bank(); bank.Id = pair.Value; bank.Name = pair.Key;
            bank.Type = field.Contains("Personal", StringComparison.Ordinal) ? BankTypes.Personal : BankTypes.Corporate;
            var applies = (bool)Invoke("AppliesTo", bank)!;
            var expected = bank.Name == "支付宝" && bank.Type == BankTypes.Personal;
            Check(applies == expected, bank.Name + "范围判断错误");
            count++;
            if (applies) { matches++; continue; }
            var row = Row("退款-亲情卡");
            var before = Snapshot(row);
            Apply(bank, row); Complete(bank, row);
            Check(Snapshot(row) == before, bank.Name + "其他银行不能更改任何字段");
        }
    }
    Check(count == 34 && matches == 1, "目录应只命中个人支付宝");
    foreach (var name in new[] { "微信", "招行", "农行", "邮政", "支付宝对公", "测试支付宝" })
    {
        var bank = Bank(); bank.Name = name;
        Check(!(bool)Invoke("AppliesTo", bank)!, "不能按名称包含关系或ID误命中");
    }
}

static void TestFormats()
{
    var cases = new (string Product, string Party, string Serial, string Merchant)[]
    {
        ("普通商品", "模拟商户", @"202610202300\d{16}", @"20261020123456\d{8}"),
        ("亲情卡", "模拟对象", @"202610200420\d{16}", @"202610202300\d{16}"),
        ("转账", "模拟对象", @"202610202000\d{20}", ""),
        ("提现-实时提现", "模拟银行", @"202610202000\d{20}", "[0-9a-f]{32}"),
        ("主动还款-花呗|信用购2026年11月账单", "花呗|信用购", @"202610202902\d{16}", ""),
        ("信用卡还款", "模拟银行", @"202610200000\d{16}", ""),
        ("水费-我家", "模拟供水单位", @"202610200000\d{16}", @"\d{8}"),
        ("电费自动缴费", "模拟电力单位", @"202610202300\d{16}", @"\d{13}"),
        ("收钱码收款", "模拟商户", @"202610202300\d{16}", @"\d{23}"),
        ("经营码交易", "模拟商户", @"202610202300\d{16}", @"\d{23}"),
        ("钻石充值", "微播", @"202610202300\d{16}", @"200107261020\d{16}"),
        ("抖音电商-订单编号模拟", "抖音电商商家", @"202610202300\d{16}", @"200105261020\d{16}"),
        ("模拟便利店", "美宜佳模拟店", @"202610202300\d{16}", "[0-9a-f]{32}"),
        ("支付宝转入到余利宝", "模拟银行", @"202610201025\d{20}", @"202610200201\d{8}"),
        ("余利宝转出到支付宝-基金赎回", "模拟银行", @"202610201025\d{20}", @"202610200201\d{8}"),
        ("余利宝-银行卡转入", "模拟银行", @"202610201012\d{28}", ""),
        ("余额宝-转出到银行卡", "模拟银行", @"202610200191\d{20}", @"LC20261020123456\d{20}"),
        ("余额宝-2026.10.19-收益发放", "模拟基金", @"20261020\d{12}", ""),
        ("余额宝-收益发放", "模拟基金", @"\d{20}20261020", ""),
        ("余额升级服务收益发放", "模拟基金", @"202610201025\d{20}", @"20261020\d{53}INTEREST")
    };
    foreach (var item in cases)
    {
        var row = Row(item.Product, -15.70, item.Party);
        Apply(Bank(), row); Match(row.SerialNum, item.Serial); Match(row.MerchantName, item.Merchant);
        Check(!Complete(Bank(), row), "没有数据变化时不能改变号码: " + item.Product);
    }
}

static void TestClassification()
{
    var income = Row("收钱码收款", 20); Apply(Bank(), income);
    Match(income.SerialNum, @"202610202300\d{16}"); Match(income.MerchantName, @"\d{23}");
    var transfer = Row("转账", -20); Apply(Bank(), transfer); Check(transfer.MerchantName == "", "转账支出不生成商家号");
    var consumer = Row("普通商品", -20, "模拟商户转账收款店"); Apply(Bank(), consumer);
    Match(consumer.SerialNum, @"202610202300\d{16}");
    foreach (var channel in new[] { "花呗", "余额宝", "账户余额", "银行卡" })
    {
        var family = Row("亲情卡"); family.TradeChannel = channel; Apply(Bank(), family);
        Match(family.SerialNum, @"202610200420\d{16}");
    }
}

static void TestOwnership()
{
    foreach (var supplied in new[] { "fixed-id", @"\\(28)\\", "/", "-" })
    {
        var row = Row("普通商品"); row.SerialNum = supplied; Apply(Bank(), row);
        Check(row.SerialNum == supplied, "指定流水号被覆盖"); Match(row.MerchantName, @"20261020123456\d{8}");
        row.AccountTime = row.AccountTime!.Value.AddMonths(1); Complete(Bank(), row);
        Check(row.SerialNum == supplied, "移动日期覆盖指定号"); Match(row.MerchantName, @"20261120123456\d{8}");
        row = Row("转账"); row.MerchantName = supplied; Apply(Bank(), row); Complete(Bank(), row);
        Check(row.MerchantName == supplied, "即使转账也必须保留指定商家号");
    }
    var imported = Row("普通商品"); imported.IsDocumentImported = true;
    var before = Snapshot(imported); Apply(Bank(), imported); Complete(Bank(), imported);
    Check(before == Snapshot(imported), "PDF导入空值不能被自动补全");
    var edited = Row("普通商品"); Apply(Bank(), edited);
    edited.SerialNum = "user-edit"; edited.AccountTime = edited.AccountTime!.Value.AddMonths(1);
    Complete(Bank(), edited); Check(edited.SerialNum == "user-edit", "生成后人工修改也不能覆盖");
    Match(edited.MerchantName, @"20261120123456\d{8}");
}

static void TestDateRefresh()
{
    foreach (var product in new[] { "普通商品", "亲情卡", "钻石充值", "余额宝-转出到银行卡" })
    {
        var row = Row(product, -50, "抖音"); Apply(Bank(), row);
        var clone = row.Clone(); clone.AccountTime = new DateTime(2027, 1, 2, 9, 8, 7);
        Check(Complete(Bank(), clone), "克隆后日期变更应同步");
        Check(clone.SerialNum.StartsWith("20270102"), "流水号仍为旧日期");
        Check(clone.MerchantName.Contains(product == "钻石充值" ? "270102" : "20270102"), "商家号仍为旧日期");
        Check(row.SerialNum.StartsWith("20261020"), "不能修改输入流水");
        var stable = Snapshot(clone); Check(!Complete(Bank(), clone) && Snapshot(clone) == stable, "重复完成应幂等");
    }
}

static void TestRefund()
{
    var parent = Row("亲情卡"); Apply(Bank(), parent);
    var refund = Row("退款-亲情卡", 15.70); refund.AccountTime = parent.AccountTime!.Value.AddDays(2);
    refund.ExtraFields["AlipayOriginalTradeNumber"] = parent.SerialNum; Apply(Bank(), refund);
    Complete(Bank(), refund, parent);
    Match(refund.SerialNum, parent.SerialNum + @"_202610223000\d{20}");
    Check(refund.MerchantName == parent.MerchantName, "退款必须复用原商家号");
    parent.AccountTime = parent.AccountTime.Value.AddDays(1);
    Complete(Bank(), parent, refund);
    Check(refund.SerialNum.StartsWith(parent.SerialNum + "_20261022"), "原单移动后关联应同步");
    Check(refund.MerchantName == parent.MerchantName, "原商家号移动后关联应同步");
    Check(!Complete(Bank(), parent, refund), "关联号码重复处理应幂等");
    var shared = Row("退款-亲情卡", 5); shared.AccountTime = refund.AccountTime; shared.MerchantName = parent.MerchantName;
    Apply(Bank(), shared); Complete(Bank(), parent, shared);
    Check(shared.SerialNum.StartsWith(parent.SerialNum + "_"), "明确且唯一的商家号应可关联");
}

static void TestMissingRelation()
{
    var parent = Row("亲情卡"); Apply(Bank(), parent);
    var refund = Row("退款-亲情卡", 15.70); refund.AccountTime = parent.AccountTime!.Value.AddDays(1);
    Apply(Bank(), refund); Complete(Bank(), parent, refund);
    Match(refund.SerialNum, @"202610213000\d{20}"); Check(refund.MerchantName == "", "不能凭金额相同猜关联");
    var another = parent.Clone(); another.SerialNum = "2026102004201111111111111111";
    another.ExtraFields.Clear();
    refund = Row("退款-亲情卡", 1); refund.AccountTime = parent.AccountTime.Value.AddDays(1); refund.MerchantName = parent.MerchantName;
    Apply(Bank(), refund); Complete(Bank(), parent, another, refund);
    Check(!refund.SerialNum.Contains('_'), "多条原单共享商家号时不能强配");
}

static void TestRelationChange()
{
    var first = Row("亲情卡"); var second = Row("亲情卡"); Apply(Bank(), first); Apply(Bank(), second);
    var refund = Row("退款-亲情卡", 1); refund.AccountTime = first.AccountTime!.Value.AddDays(1);
    refund.ExtraFields["AlipayOriginalTradeNumber"] = first.SerialNum; Apply(Bank(), refund);
    Complete(Bank(), first, second, refund);
    Check(refund.SerialNum.StartsWith(first.SerialNum + "_"), "初始关联错误");
    refund.ExtraFields["AlipayOriginalTradeNumber"] = second.SerialNum;
    Complete(Bank(), first, second, refund);
    Check(refund.SerialNum.StartsWith(second.SerialNum + "_") && refund.MerchantName == second.MerchantName, "修改原单仍沿用旧关系");
    refund.ExtraFields.Remove("AlipayOriginalTradeNumber");
    Complete(Bank(), first, second, refund);
    Match(refund.SerialNum, @"202610213000\d{20}"); Check(refund.MerchantName == "", "清除明确原单后应独立补全");
}

static void TestDeferred()
{
    var parent = Row("您于10月20日在拼多多使用先用后付下单本商品"); Apply(Bank(), parent);
    Match(parent.MerchantName, @"CDP10261020\d{20}");
    var child = Row("您于10月20日在拼多多使用先用后付下单本商品", -15.70);
    child.AccountTime = parent.AccountTime!.Value.AddDays(11); child.ExtraFields["AlipayOriginalTradeNumber"] = parent.SerialNum;
    Apply(Bank(), child); Complete(Bank(), parent, child);
    Check(child.SerialNum == parent.SerialNum + "_" + parent.MerchantName + "-1570", "延期扣款格式与原单不一致");
    Check(child.MerchantName == parent.MerchantName, "延期扣款要复用原商家号");
    child.TradeMoney = -2.66; child.AccountTime = child.AccountTime!.Value.AddDays(1);
    Complete(Bank(), parent, child);
    Check(child.SerialNum == parent.SerialNum + "_" + parent.MerchantName + "-266", "金额分值必须随最终金额变化");
    Check(child.SerialNum.StartsWith("20261020") && child.AccountTime.Value.Month == 11, "原下单日期不能被扣款日期覆盖");
    Check(!Complete(Bank(), parent, child), "延期关联重复处理应幂等");
}

static void TestDeferredFallback()
{
    var row = Row("拼多多先用后付扣款"); Apply(Bank(), row); Complete(Bank(), row);
    Match(row.SerialNum, @"202610202300\d{16}"); Match(row.MerchantName, @"20261020123456\d{8}");
    row = Row("您于9月1日在拼多多使用先用后付下单本商品"); Apply(Bank(), row); Complete(Bank(), row);
    Match(row.SerialNum, @"202610202300\d{16}"); Match(row.MerchantName, @"CDP10260901\d{20}");
    row = Row("您于12月31日在拼多多使用先用后付下单本商品"); row.AccountTime = new DateTime(2026, 1, 2);
    Apply(Bank(), row); Match(row.MerchantName, @"CDP10251231\d{20}");
    row = Row("您于13月32日在拼多多使用先用后付下单本商品"); Apply(Bank(), row);
    Match(row.MerchantName, @"20261020123456\d{8}");
}

static void TestStableProfiles()
{
    foreach (var product in new[] { "水费-我家", "电费自动缴费" })
    {
        var first = Row(product); var second = Row(product); second.AccountTime = second.AccountTime!.Value.AddMonths(1);
        Apply(Bank(), first); Apply(Bank(), second); Complete(Bank(), first, second);
        Check(first.MerchantName == second.MerchantName, "同缴费项目跨月不能换户号");
        var different = Row(product); different.BankUserId++; Apply(Bank(), different);
        Check(first.MerchantName != different.MerchantName, "不同用户不能共用模拟缴费号");
    }
    var a = Row("余额宝-收益发放", 0.12); var b = Row("余额宝-收益发放", 0.13); b.AccountTime = b.AccountTime!.Value.AddDays(1);
    Apply(Bank(), a); Apply(Bank(), b); Complete(Bank(), a, b);
    Check(a.SerialNum[..20] == b.SerialNum[..20], "同用户同产品收益编号前段应稳定");
    Check(a.SerialNum.EndsWith("20261020") && b.SerialNum.EndsWith("20261021"), "收益日期位于结尾");
}

static void TestCollisions()
{
    var first = Row("普通商品"); Apply(Bank(), first);
    var clone = first.Clone(); var manual = Row("普通商品"); manual.SerialNum = first.SerialNum; manual.MerchantName = first.MerchantName;
    var expected = Snapshot(manual); Complete(Bank(), manual, first, clone);
    Check(Snapshot(manual) == expected, "碰撞时不能改指定值");
    Check(new[] { manual, first, clone }.Select(r => r.SerialNum).Distinct().Count() == 3, "克隆流水要重新去重");
    Check(new[] { manual, first, clone }.Select(r => r.MerchantName).Distinct().Count() == 3, "新订单商家号碰撞要去重");
    var yield = Row("余额宝-收益发放", 0.12); Apply(Bank(), yield);
    var sameDay = Row("余额宝-收益发放", 0.13); Apply(Bank(), sameDay);
    Complete(Bank(), yield, sameDay); Check(yield.SerialNum != sameDay.SerialNum, "同日多条收益也不能重复流水号");
    Check(!Complete(Bank(), yield, sameDay), "收益碰撞修复应稳定");
}

static void TestVolume()
{
    var rows = Enumerable.Range(0, 5000).Select(_ => Row("普通商品")).ToArray();
    foreach (var row in rows) Apply(Bank(), row);
    Complete(Bank(), rows);
    Check(rows.Select(r => r.SerialNum).Distinct().Count() == rows.Length, "批次流水号重复");
    Check(rows.Select(r => r.MerchantName).Distinct().Count() == rows.Length, "新订单商家号重复");
    Check(!Complete(Bank(), rows), "大批次复核应幂等");
}

static void TestSave()
{
    var bank = Bank();
    var automatic = Row("普通商品"); Apply(bank, automatic);
    var existing = Row("旧流水"); existing.SerialNum = automatic.SerialNum; existing.MerchantName = automatic.MerchantName;
    existing.IsDocumentImported = true; existing.Id = 123;
    automatic.AccountTime = automatic.AccountTime!.Value.AddDays(1);
    existing.AccountTime = automatic.AccountTime;
    existing.SerialNum = "20261021" + existing.SerialNum[8..];
    existing.MerchantName = "20261021123456" + existing.MerchantName[14..];
    var expected = Snapshot(existing);
    var formula = Row("亲情卡"); formula.SerialNum = @"\\(28)\\"; formula.MerchantName = @"\\(31)\\"; Apply(bank, formula);
    var repository = new MemoryRepository();
    var service = new FlowRecordSaveService(repository, new FlowFormulaEvaluator(), new EmptyDataSet());
    var rows = new[] { existing, automatic, formula };
    service.SaveAllAsync(bank, 42, rows).GetAwaiter().GetResult();
    Check(Snapshot(existing) == expected, "追加保存不能改旧流水");
    Match(automatic.SerialNum, @"202610212300\d{16}");
    Check(automatic.SerialNum != existing.SerialNum && automatic.MerchantName != existing.MerchantName, "追加碰撞只能重发新号");
    Match(formula.SerialNum, @"\d{28}"); Match(formula.MerchantName, @"\d{31}");
    var saved = rows.Select(r => (r.SerialNum, r.MerchantName)).ToArray();
    service.SaveAllAsync(bank, 42, rows).GetAwaiter().GetResult();
    Check(rows.Select(r => (r.SerialNum, r.MerchantName)).SequenceEqual(saved), "二次保存不能再次生成");
    var evaluated = Row("普通商品"); Apply(bank, evaluated); evaluated.AccountTime = evaluated.AccountTime!.Value.AddDays(2);
    service.ConvertInPlace(bank, [evaluated]); Match(evaluated.SerialNum, @"202610222300\d{16}");
}

static GenerateReferenceRule Rule(int index, bool income, string product) => new()
{
    Index = index, BankId = 1, IsCheck = true, IncomeAttribute = income ? "收入" : "支出",
    MinMoney = 10, MaxMoney = 100, FloutLength = 2, StartDay = 9, EndDay = 20,
    TradeWeekend = true, TradeHoliday = true, ProductBrief = product, Remark = product, OppositeUsername = "模拟对象"
};
static FlowAutoGenerationRequest Request(IReadOnlyList<GenerateReferenceRule> rules, IReadOnlyList<GenerateConstRule>? fixedRules = null) => new()
{
    Bank = Bank(), BankUser = new BankUser { Id = 42, AccountNo = "simulation-account", AutoCalculateInterest = false },
    Config = new FlowGenerationConfig { StartTime = new DateTime(2026, 10, 1), EndTime = new DateTime(2026, 10, 31),
        AllInMoney = 1000, LastMoney = 100, OpeningBalance = 500 }, References = rules, ConstItems = fixedRules ?? []
};

static void TestGeneration()
{
    var income = Rule(1, true, "转账"); var expense = Rule(2, false, "收钱码收款");
    var fixedRule = new GenerateConstRule
    {
        Index = 1, BankId = 1, IsCheck = true, IncomeAttribute = "支出", MinMoney = 10, MaxMoney = 10, FloutLength = 0,
        StartDay = 9, EndDay = 20, FixDay = "15", TradeWeekend = true, TradeHoliday = true, ProductBrief = "亲情卡"
    };
    var request = Request([income, expense], [fixedRule]);
    var before = Snapshot(new { request.Config, request.References, request.ConstItems });
    var result = new FlowAutoGenerator().Generate(request);
    Check(result.Records.Count > 2, "应产生参照流水");
    Check(result.Records.Select(r => r.SerialNum).Distinct().Count() == result.Records.Count, "最终流水号重复");
    foreach (var row in result.Records)
    {
        var date = row.AccountTime!.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        Check(row.SerialNum.StartsWith(date), "最终日期没有同步");
        if (row.TradeMoney > 0) Check(row.MerchantName == "", "个人转账收入应为空");
        else Match(row.MerchantName, row.ProductBrief == "亲情卡" ? date + @"2300\d{16}" : @"\d{23}");
    }
    Check(result.Records.Any(r => r.ProductBrief == "亲情卡" && r.AccountTime!.Value.Day == 15), "固定项目必须生成");
    Check(before == Snapshot(new { request.Config, request.References, request.ConstItems }), "不能回写参照配置");
}

static void TestCorrections()
{
    foreach (var correction in new[] { "negative", "final", "opening" })
    {
        var request = Request([Rule(1, true, "转账"), Rule(2, false, "普通商品")]);
        var source = new FlowAutoGenerator().Generate(request);
        var ordinary = source.Records.First(r => r.TradeMoney < 0); ordinary.AccountTime = ordinary.AccountTime!.Value.AddHours(1);
        var explicitRow = source.Records.First(r => r.TradeMoney > 0); explicitRow.SerialNum = "provided-income";
        var generator = new FlowAutoGenerator();
        var result = correction switch
        {
            "negative" => generator.ApplyNegativeBalanceCorrection(request, source),
            "final" => generator.ApplyFinalBalanceCorrection(request, source),
            _ => generator.ApplyOpeningBalanceCorrection(request, source, source.OpeningBalance)
        };
        Check(result.Records.Any(r => r.SerialNum == "provided-income"), "余额修正覆盖指定号");
        foreach (var row in result.Records.Where(r => r.SerialNum != "provided-income"))
            Check(row.SerialNum.StartsWith(row.AccountTime!.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)), "余额修正后号码日期错误");
    }
}

static void TestRuleContext()
{
    var request = Request([Rule(1, true, "转账")]);
    var rule = Rule(2, false, "退款-亲情卡");
    rule.ExtraFields["AlipayOriginalTradeNumber"] = "2026100104201234567890123456";
    rule.ExtraFields["AlipayOriginalMerchantNumber"] = "2026100123001234567890123456";
    var create = typeof(FlowAutoGenerator).GetMethod("CreateRecordFromExternalPlan", BindingFlags.Static | BindingFlags.NonPublic)!;
    var row = (FlowRecord)create.Invoke(null, [request, rule, request.Bank.ReferenceColumns, new DateTime(2026, 10, 20), 15.70])!;
    Complete(request.Bank, row);
    Match(row.SerialNum, @"2026100104201234567890123456_202610203000\d{20}");
    Check(row.MerchantName == rule.ExtraFields["AlipayOriginalMerchantNumber"], "参照原商家订单号丢失");
}

sealed class EmptyDataSet : IFormulaDataSetService
{
    public string? PickImportFile() => null;
    public string? PickExampleExportFile(string? bankName = null, long? bankId = null) => null;
    public FormulaDataSetImportResult Import(long bankId, string path) => throw new NotSupportedException();
    public void ExportBuiltInExample(string path, string? bankName = null, long? bankId = null) => throw new NotSupportedException();
    public FormulaDataSet? Get(long bankId) => null;
    public bool Clear(long bankId) => false;
}
sealed class MemoryRepository : IFlowRecordRepository
{
    public List<FlowRecord> Rows { get; private set; } = [];
    public Task<IReadOnlyList<FlowRecord>> ListByUserAsync(Bank bank, long id) => Task.FromResult<IReadOnlyList<FlowRecord>>(Rows);
    public Task<IReadOnlyList<FlowRecord>> ListExistingByUserAsync(Bank bank, long id) => ListByUserAsync(bank, id);
    public Task SaveAllAsync(long bankId, long id, IEnumerable<FlowRecord> records) { Rows = records.Select(r => r.Clone()).ToList(); return Task.CompletedTask; }
    public Task MoveUserRecordsAsync(long bankId, long source, long target) => Task.CompletedTask;
    public Task<int> RecoverTemporaryUserRecordsAsync(long bankId, IReadOnlyList<BankUser> users) => Task.FromResult(0);
}
