using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("34类银行全部执行尾数策略且保持总额条数日期及输入", TestAllBanks),
    ("有调整空间时收支两侧0几尾数全部消除，整数流水不变", TestDirectionQuotas),
    ("合法固定项目、导入、利息、其他精度与外部用户不调整", TestExclusions),
    ("只有0几可选或预算卡在下限时严格拒绝不合规结果", TestConstrained),
    ("单笔累计不超过0.99元，重复执行稳定", TestCumulative),
    ("按月范围及明细保持每月收支金额", TestMonthlyBudgets),
    ("同一天的不同明细预算窗口也不能混用", TestIntradayBudgets),
    ("建行金额分层保持，单值分层不调整", TestCcbBands),
    ("跨日利息变化时拒绝不合规结果且输入不变", TestInterestRollback),
    ("逐对调整只能解决一部分时也不能放行剩余0几", TestSameDayFallback),
    ("未配置利息且没有结息行时允许跨日安全配对", TestNoInterestSetting),
    ("同日微调保留利息、利息税和全部显示字段", TestSameDayInterest),
    ("已有负余额不恶化，收入支出配对均保护余额", TestNegativeBalances),
    ("支付宝金额关联订单号跟随调整且指定号码保留", TestAlipayAmountSerials),
    ("余额修正三个出口保持尾数且时间修正保留金额", TestCorrectionExits),
    ("大批量处理有界且新尾数不过度集中", TestVolume),
    ("统一生成出口涵盖中行、建行、微信及通用路径", TestGenerationExits),
    ("小分组也不预留0几名额，可行时全部消除", TestSmallGroups),
    ("中行收口优先避开0几且尾数有变化，固定项目不动", TestBocPreferredClosing),
    ("中行预算收口不再回退到禁止尾数，窄区间明确拒绝", TestBocClosingFallback),
    ("真实结息路径下中行新生成流水避免0几尾数", TestBocGenerationWithInterest),
    ("两位小数整个候选集严格排除01到09且边界完整", TestHardLegalAmounts),
    ("建行合法金额排名双向映射跳过所有禁用尾数", TestHardCcbRanking),
    ("所有普通尾数均被出口阻止，结息和导入原值保留", TestHardOutputGuard),
    ("无合法金额的参照与固定项目在生成前定位冲突", TestHardRuleConflicts),
    ("34类银行多次真实生成及三种余额修正均无禁用尾数", TestHardPublicGeneration),
    ("跨整数禁用区间的窄参照仍可使用两侧合法金额", TestNarrowLegalGap),
    ("修补候选按月及日期均匀轮转，不改变流水或日期", TestTimelineRepairOrdering),
    ("共用收支增减修补均匀落在全年各月，金额精度不变", TestTimelineSignedClosing),
    ("中行收口不再把修补集中于末尾月份", TestTimelineBocClosing),
    ("最终精确收口分散修补且实时保护所有后续余额", TestTimelineExactClosing),
    ("按月修补只在原预算月份内分散，不动其他月份", TestTimelineMonthlyClosing),
    ("最终出口按时间覆盖合法尾数轮次，避免近距离重复", TestFinalTailRounds),
    ("34类银行最后收口后均执行尾数间隔处理", TestFinalTailAllBanks),
    ("尾数轮次跨月连续但等额配对不跨预算窗口", TestFinalTailBudgetWindows),
    ("最终尾数分散保持结息原值及全部结息字段", TestFinalTailInterest),
    ("固定金额及窄区间无法轮转时保留硬约束", TestFinalTailLockedRanges),
    ("仅移动日期引入的尾数碰撞也会经过最终间隔检查", TestFinalTailTimeCorrection),
    ("相等的固定整数优先于正数小数位且不改配置", TestFixedIntegerBounds),
    ("34类银行固定整数参照和固定日期项目均可生成修正", TestFixedIntegerGeneration),
    ("非固定整数的范围及禁止尾数仍保留原校验", TestFixedIntegerScope)
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {e}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} cent-tail tests passed.");
return failures == 0 ? 0 : 1;

static object? Call(string name, params object?[] args)
{
    try
    {
        return typeof(FlowAutoGenerator).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(null, args);
    }
    catch (TargetInvocationException e) { throw e.InnerException!; }
}
static FlowAutoGenerationResult Complete(FlowAutoGenerationRequest request, FlowAutoGenerationResult source) =>
    (FlowAutoGenerationResult)Call("CompleteGeneratedResult", request, source)!;
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); }
    catch (InvalidOperationException e) { Check(e.Message.Contains(".01",StringComparison.Ordinal),"应说明禁止尾数或冲突范围: "+e.Message);return; }
    throw new Exception("必须拒绝含禁用尾数的结果，不能作为成功结果返回");
}
static string Snapshot(object value) => JsonSerializer.Serialize(value);
static int Tail(FlowRecord row) => (int)Math.Round(Math.Abs(row.TradeMoney ?? 0) * 100, MidpointRounding.AwayFromZero) % 100;
static Bank Bank(string name = "中行", long id = 18, string type = BankTypes.Personal) => new() { Id = id, Name = name, Type = type };
static FlowRecord Row(int id, double money, DateTime time, double min = 10.01, double max = 200.99,
    string kind = "Reference", int precision = 2) => new()
{
    Id = id, BankId = 18, BankUserId = 9001, Account = "synthetic-account", AccountTime = time, TradeMoney = money,
    ProductBrief = "模拟交易", SerialNum = "provided-" + id, VoucherNum = "voucher-" + id,
    ExtraFields = new Dictionary<string, string>
    {
        ["__GeneratedSourceKind"] = kind, ["__GeneratedSourceIndex"] = id.ToString(CultureInfo.InvariantCulture),
        ["__GeneratedAmountMin"] = min.ToString("R", CultureInfo.InvariantCulture), ["__GeneratedAmountMax"] = max.ToString("R", CultureInfo.InvariantCulture),
        ["__GeneratedAmountUnit"] = (precision == 2 ? ".01" : precision == 1 ? ".1" : precision == 0 ? "1" : "10"),
        ["__GeneratedAmountPrecision"] = precision.ToString(CultureInfo.InvariantCulture)
    }
};
static List<FlowRecord> Profile(int count = 200, int sign = -1, int idOffset = 0)
{
    var rows = new List<FlowRecord>();
    var regular = Enumerable.Range(11, 79).Where(tail => tail % 10 != 0).ToArray();
    for (var i = 0; i < count; i++)
    {
        var isLow = sign < 0 ? i >= count * 3 / 5 : i < count * 2 / 5;
        var amount = isLow ? 20.01 : 80 + i % 70 + regular[i % regular.Length] / 100d;
        rows.Add(Row(idOffset + i + 1, sign * amount, new DateTime(2026, 10, 1, 9, 0, 0).AddMinutes(i)));
    }
    return rows;
}
static FlowAutoGenerationRequest Request(List<FlowRecord> rows, Bank? bank = null)
{
    bank ??= Bank();
    foreach (var row in rows) row.BankId = bank.Id;
    var income = Math.Round(rows.Where(r => r.TradeMoney > 0).Sum(r => r.TradeMoney ?? 0), 2);
    var expense = Math.Round(rows.Where(r => r.TradeMoney < 0).Sum(r => Math.Abs(r.TradeMoney ?? 0)), 2);
    return new FlowAutoGenerationRequest
    {
        Bank = bank, BankUser = new BankUser { Id = 9001, AutoCalculateInterest = false, AccountNo = "synthetic-account" },
        Config = new FlowGenerationConfig { StartTime = rows.Min(r => r.AccountTime)!.Value.Date, EndTime = rows.Max(r => r.AccountTime)!.Value.Date,
            OpeningBalance = 1000000, AllInMoney = income, LastMoney = income - expense }, References = [], ConstItems = []
    };
}
static FlowAutoGenerationResult Result(FlowAutoGenerationRequest request, List<FlowRecord> rows)
{
    Call("ApplyBalances", rows, request.Config.OpeningBalance);
    var minimum = rows.Select(r => r.Balance ?? request.Config.OpeningBalance).Append(request.Config.OpeningBalance).Min();
    return new FlowAutoGenerationResult
    {
        Records = rows, OpeningBalance = request.Config.OpeningBalance,
        IncomeTotal = Math.Round(rows.Where(r => r.TradeMoney > 0).Sum(r => r.TradeMoney ?? 0), 2),
        ExpenseTotal = Math.Round(rows.Where(r => r.TradeMoney < 0).Sum(r => Math.Abs(r.TradeMoney ?? 0)), 2),
        FinalBalance = rows.Last().Balance!.Value, MinimumBalance = minimum,
        RequiresOpeningBalanceCorrection = minimum < 0, RequiredOpeningBalance = request.Config.OpeningBalance - Math.Min(0, minimum)
    };
}
static void Preserved(FlowAutoGenerationResult before, FlowAutoGenerationResult after)
{
    Check(before.Records.Count == after.Records.Count, "流水条数变化");
    Check(before.IncomeTotal == after.IncomeTotal && before.ExpenseTotal == after.ExpenseTotal, "收支总额变化");
    Check(Math.Abs(before.FinalBalance - after.FinalBalance) < .001, "末余额变化");
    Check(after.MinimumBalance >= before.MinimumBalance - .001, "最低余额恶化");
    var original = before.Records.ToDictionary(r => r.Id);
    foreach (var row in after.Records)
    {
        var old = original[row.Id];
        Check(row.AccountTime == old.AccountTime && row.Account == old.Account && row.VoucherNum == old.VoucherNum, "日期、账户或指定凭证变化");
        Check(Math.Abs(Math.Abs(row.TradeMoney ?? 0) - Math.Abs(old.TradeMoney ?? 0)) <= .990001, "单笔调整超过0.99元");
        if (row.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision") == "2")
        {
            var amount = Math.Abs(row.TradeMoney ?? 0);
            var min = double.Parse(row.ExtraFields["__GeneratedAmountMin"], CultureInfo.InvariantCulture);
            var max = double.Parse(row.ExtraFields["__GeneratedAmountMax"], CultureInfo.InvariantCulture);
            Check(amount >= min - .00001 && amount <= max + .00001 && Tail(row) % 10 != 0, "金额范围或精度改变");
        }
    }
}
static void Quota(IEnumerable<FlowRecord> rows)
{
    foreach (var group in rows.Where(r => r.ExtraFields.GetValueOrDefault("__GeneratedSourceKind") == "Reference"
                 && r.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision") == "2").GroupBy(r => Math.Sign(r.TradeMoney ?? 0)))
    {
        Check(group.All(r => Tail(r) is < 1 or > 9), "有调整空间时不应保留0几尾数");
    }
}

static void TestAllBanks()
{
    var count = 0;
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    {
        var catalog = (IReadOnlyDictionary<string, long>)typeof(SpeedEmulator.ViewModels.MainViewModel)
            .GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var pair in catalog)
        {
            var rows = Profile();
            var request = Request(rows, Bank(pair.Key, pair.Value, field.Contains("Personal", StringComparison.Ordinal) ? BankTypes.Personal : BankTypes.Corporate));
            var source = Result(request, rows); var snapshot = Snapshot(source);
            var result = Complete(request, source);
            Quota(result.Records); Preserved(source, result);
            Check(snapshot == Snapshot(source), "不能修改输入结果: " + pair.Key);
            Check(result.Records.Zip(source.Records).All(p => p.First.SerialNum == p.Second.SerialNum), "不能改指定流水号");
            count++;
        }
    }
    Check(count == 34, "应覆盖34类银行");
}
static void TestDirectionQuotas()
{
    var rows = Profile().Concat(Profile(sign: 1, idOffset: 1000)).ToList();
    rows.AddRange(Enumerable.Range(0, 1000).Select(i => Row(i + 2000, -50, new DateTime(2026, 10, 1, 8, 0, 0), 1, 100, precision: 0)));
    var request = Request(rows); var source = Result(request, rows); var result = Complete(request, source);
    Quota(result.Records); Preserved(source, result);
    Check(result.Records.Count(r => r.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision") == "0" && r.TradeMoney == -50) == 1000, "整数不得参与调整");
}
static void TestExclusions()
{
    var rows = Profile(); var time = rows[0].AccountTime!.Value;
    var exempt = new[]
    {
        Row(1001,-20.13,time,kind:"Const"), Row(1002,-20.13,time,20.13,20.13),
        Row(1003,-20.01,time), Row(1004,-20.1,time,10.1,100.9,precision:1),
        Row(1005,-20,time,10,100,precision:0), Row(1006,-20,time,10,90,precision:-1),
        Row(1007,-20.01,time), Row(1008,-20.01,time), Row(1009,-20.01,time), Row(1010,-20.01,time)
    };
    exempt[2].IsDocumentImported = true; exempt[6].GeneratedRowKind = "Interest";
    exempt[7].ExtraFields.Clear(); exempt[8].BankUserId = 9999;
    rows.AddRange(exempt); var request = Request(rows); exempt[9].BankId = 999;
    var source = Result(request, rows); var result = Complete(request, source);
    foreach (var row in exempt) Check(result.Records.Single(r => r.Id == row.Id).TradeMoney == row.TradeMoney, "不应调整排除项目: " + row.Id);
    Preserved(source, result);
}
static void TestConstrained()
{
    foreach (var max in new[] { 20.01, 20.09, 20.11 })
    {
        var rows = Enumerable.Range(1, 30).Select(i => Row(i, -20.01, new DateTime(2026, 10, 1).AddMinutes(i), 20.01, max)).ToList();
        var request = Request(rows); var source = Result(request, rows);
        var before=Snapshot(source);Reject(()=>Complete(request,source));Check(before==Snapshot(source),"冲突不能污染输入");
    }
}
static void TestCumulative()
{
    var rows = Profile(); var request = Request(rows); var source = Result(request, rows);
    var first = Complete(request, source); Quota(first.Records);
    Check(Snapshot(first) == Snapshot(Complete(request, first)), "同一结果再次处理不应漂移");
    var hardRows = Profile(200); var hardRequest = Request(hardRows); var initial = Result(hardRequest, hardRows);
    var result = initial;
    for (var pass = 0; pass < 5; pass++) { result = Complete(hardRequest, result); Preserved(initial, result); }
}
static void TestMonthlyBudgets()
{
    foreach (var mode in new[] { 1, 2 })
    {
        var rows = Profile(); var second = Profile(idOffset: 1000);
        foreach (var row in second) row.AccountTime = row.AccountTime!.Value.AddMonths(1);
        rows.AddRange(second); var request = Request(rows); request.Config.SelectIndex = mode;
        if (mode == 2)
        {
            request.Config.MonthGenData.Add(new MonthGenerateRule { StartTime = new DateTime(2026,10,1), EndTime = new DateTime(2026,10,31) });
            request.Config.MonthGenData.Add(new MonthGenerateRule { StartTime = new DateTime(2026,11,1), EndTime = new DateTime(2026,11,30) });
        }
        var source = Result(request, rows); var result = Complete(request, source); Quota(result.Records); Preserved(source, result);
        foreach (var group in source.Records.GroupBy(r => r.AccountTime!.Value.Month))
            Check(Math.Abs(group.Sum(r=>r.TradeMoney??0) - result.Records.Where(r=>r.AccountTime!.Value.Month==group.Key).Sum(r=>r.TradeMoney??0)) < .00001, "月预算变化");
    }
}
static void TestIntradayBudgets()
{
    var rows = Profile(40);
    foreach (var row in rows) row.AccountTime = new DateTime(2026,10,1, Tail(row)==1 ? 14:9, (int)row.Id,0);
    foreach (var row in rows.Where(r=>Tail(r)==1)) row.ExtraFields["__GeneratedAmountMin"]="20.01";
    var request = Request(rows); request.Config.SelectIndex = 2;
    request.Config.MonthGenData.Add(new MonthGenerateRule { StartTime = new DateTime(2026,10,1,8,0,0), EndTime = new DateTime(2026,10,1,12,0,0) });
    request.Config.MonthGenData.Add(new MonthGenerateRule { StartTime = new DateTime(2026,10,1,13,0,0), EndTime = new DateTime(2026,10,1,18,0,0) });
    var source = Result(request, rows);var before=Snapshot(source);Reject(()=>Complete(request,source));
    Check(before==Snapshot(source),"不能借其他预算窗口的余额完成尾数调整");
}
static void TestCcbBands()
{
    foreach (var locked in new[] { true, false })
    {
        var rows = Profile();
        foreach(var row in rows.Where(r=>Tail(r)==1)) row.TradeMoney=-20.11;
        foreach (var row in rows)
        {
            var amount = Math.Abs(row.TradeMoney!.Value);
            row.ExtraFields["__CcbStratumMin"] = (locked ? amount : Math.Max(10.01, amount - .15)).ToString("R",CultureInfo.InvariantCulture);
            row.ExtraFields["__CcbStratumMax"] = (locked ? amount : amount + .2).ToString("R",CultureInfo.InvariantCulture);
        }
        var request = Request(rows, Bank("建行",8)); var source = Result(request,rows); var result=Complete(request,source);
        if(locked) Check(ReferenceEquals(source,result), "单值分层不应改动");
        foreach(var row in result.Records)
            Check(Math.Abs(row.TradeMoney!.Value) >= double.Parse(row.ExtraFields["__CcbStratumMin"],CultureInfo.InvariantCulture)-.00001
                && Math.Abs(row.TradeMoney.Value) <= double.Parse(row.ExtraFields["__CcbStratumMax"],CultureInfo.InvariantCulture)+.00001,"突破建行分层");
        Preserved(source,result);
    }
}
static FlowAutoGenerationRequest WithInterest(List<FlowRecord> rows, double rate)
{
    var basic = Request(rows); basic.BankUser.AutoCalculateInterest=true;
    basic.Config.StartTime=new DateTime(2026,1,1); basic.Config.EndTime=new DateTime(2026,3,31);
    var setting=BankInterestSettingDefaults.CreateDefault(basic.Bank); setting.RatePercent=rate.ToString(CultureInfo.InvariantCulture);
    setting.GenerateInterestTaxRow=true;
    return new FlowAutoGenerationRequest {Bank=basic.Bank,BankUser=basic.BankUser,Config=basic.Config,
        References=[],ConstItems=[],InterestSetting=setting};
}
static void TestInterestRollback()
{
    var rows=Profile(60);
    foreach(var row in rows) row.AccountTime=new DateTime(2026,1, Tail(row)==1?3:1,9,(int)row.Id%60,0);
    foreach(var row in rows.Where(r=>Tail(r)==1)) row.ExtraFields["__GeneratedAmountMin"]="20.01";
    var request=WithInterest(rows,3650);
    BankInterestCalculationService.Recalculate(request.Bank,request.BankUser,request.InterestSetting,rows,request.Config.OpeningBalance,
        request.Config.StartTime,request.Config.EndTime,random:new Random(17));
    foreach(var row in rows.Where(r=>r.Id==0)) row.Id=2000+rows.IndexOf(row);
    var source=Result(request,rows); var before=Snapshot(source);Reject(()=>Complete(request,source));
    Check(before==Snapshot(source),"拒绝不能污染原结果或结息行");
}
static void TestSameDayInterest()
{
    var rows=Profile(); foreach(var row in rows) row.AccountTime=new DateTime(2026,1,1,9,0,0).AddMinutes(row.Id);
    var request=WithInterest(rows,.35);
    BankInterestCalculationService.Recalculate(request.Bank,request.BankUser,request.InterestSetting,rows,request.Config.OpeningBalance,
        request.Config.StartTime,request.Config.EndTime,random:new Random(17));
    foreach(var row in rows.Where(r=>r.Id==0)) {row.Id=2000+rows.IndexOf(row);row.Remark="保留的利息说明";row.VoucherNum="interest-voucher";}
    var source=Result(request,rows); var result=Complete(request,source); Preserved(source,result);
    foreach(var row in source.Records.Where(FlowGeneratedRowKinds.IsSystemInterest))
    {
        var same=result.Records.Single(r=>r.Id==row.Id);
        Check(same.TradeMoney==row.TradeMoney && same.Remark==row.Remark && same.SerialNum==row.SerialNum,"利息行发生变化");
    }
    Quota(result.Records);
}
static void TestSameDayFallback()
{
    var rows=Profile(60);
    foreach(var row in rows)
    {
        row.AccountTime=new DateTime(2026,1,row.Id>48?3:1,9,(int)row.Id%60,0);
        if(Tail(row)==1) row.ExtraFields["__GeneratedAmountMin"]="20.01";
    }
    var request=WithInterest(rows,3650);
    BankInterestCalculationService.Recalculate(request.Bank,request.BankUser,request.InterestSetting,rows,request.Config.OpeningBalance,
        request.Config.StartTime,request.Config.EndTime,random:new Random(17));
    foreach(var row in rows.Where(r=>r.Id==0)) row.Id=2000+rows.IndexOf(row);
    var source=Result(request,rows);var before=Snapshot(source);Reject(()=>Complete(request,source));
    Check(before==Snapshot(source),"部分成功也不能返回含禁用尾数的结果或修改输入");
}
static void TestNoInterestSetting()
{
    var rows=Profile(60);
    foreach(var row in rows)
    {
        row.AccountTime=new DateTime(2026,1,Tail(row)==1?3:1,9,(int)row.Id%60,0);
        if(Tail(row)==1) row.ExtraFields["__GeneratedAmountMin"]="20.01";
    }
    var request=Request(rows);request.BankUser.AutoCalculateInterest=true;
    var source=Result(request,rows);var result=Complete(request,source);Preserved(source,result);Quota(result.Records);
    var interest=Row(2000,100,new DateTime(2026,1,21));interest.GeneratedRowKind="Interest";rows.Add(interest);
    source=Result(request,rows);Reject(()=>Complete(request,source));
}
static void TestNegativeBalances()
{
    foreach(var sign in new[]{-1,1})
    {
        var rows=Profile(sign:sign);var request=Request(rows);request.Config.OpeningBalance=0;
        var source=Result(request,rows);var result=Complete(request,source);Preserved(source,result);Quota(result.Records);
    }
}
static void TestAlipayAmountSerials()
{
    var rows=Profile(); var bank=Bank("支付宝",1);
    bank.FlowColumns.Add(new ColumnDefinition{Name="流水号",Field=nameof(FlowRecord.SerialNum)});
    bank.FlowColumns.Add(new ColumnDefinition{Name="商家订单号",Field=nameof(FlowRecord.MerchantName)});
    var request=Request(rows,bank);
    var parent=Row(2000,-15.71,new DateTime(2026,9,1,9,0,0),15.71,15.71,"Const");parent.BankId=1;
    parent.SerialNum="2026090123001234567890123456";parent.MerchantName="CDP1026090112345678901234567890";
    rows.Insert(0,parent);
    var rules=typeof(FlowAutoGenerator).Assembly.GetType("SpeedEmulator.Services.AlipayGeneratedNumberRules")!;
    foreach(var row in rows.Where(r=>Tail(r)==1))
    {
        row.ProductBrief="您于9月1日在拼多多使用先用后付下单本商品";row.SerialNum=string.Empty;
        row.ExtraFields["AlipayOriginalTradeNumber"]=parent.SerialNum;
        rules.GetMethod("Apply")!.Invoke(null,[bank,row]);
    }
    var source=Result(request,rows);var result=Complete(request,source);Preserved(source,result);
    foreach(var row in result.Records.Where(r=>r.ProductBrief.Contains("先用后付",StringComparison.Ordinal)))
        if(row.SerialNum.Contains('_')) Check(row.SerialNum.EndsWith("-"+Math.Round(Math.Abs(row.TradeMoney!.Value)*100).ToString("0",CultureInfo.InvariantCulture)),"金额后缀没有同步");
    Check(result.Records.Where(r=>r.Id!=2000 && !r.ProductBrief.Contains("先用后付",StringComparison.Ordinal)).All(r=>r.SerialNum=="provided-"+r.Id),"指定号码被覆盖");
    Quota(result.Records.Where(r=>r.Id!=2000));
}
static void TestCorrectionExits()
{
    foreach(var exit in new[]{"negative","final","opening"})
    {
        var rows=Profile();var income=Row(2000,50000,new DateTime(2026,10,1,8,0,0),50000,50000,"Const",0);rows.Insert(0,income);
        var request=Request(rows);var source=Complete(request,Result(request,rows));var generator=new FlowAutoGenerator();
        var result=exit switch{"negative"=>generator.ApplyNegativeBalanceCorrection(request,source),"final"=>generator.ApplyFinalBalanceCorrection(request,source),
            _=>generator.ApplyOpeningBalanceCorrection(request,source,source.OpeningBalance)};
        Quota(result.Records);
    }
    var unchangedRows=Profile();var unchangedRequest=Request(unchangedRows);var unchanged=Complete(unchangedRequest,Result(unchangedRequest,unchangedRows));
    var timeOnlyRows=unchanged.Records.Select(r=>r.Clone()).ToList();
    foreach(var row in timeOnlyRows) row.AccountTime=row.AccountTime!.Value.AddDays(1);
    var timeOnly=Result(unchangedRequest,timeOnlyRows);
    Check(ReferenceEquals(timeOnly,Call("CompleteCorrectedGeneratedResult",unchangedRequest,unchanged,timeOnly)),"时间修正不能顺带改金额");
    var changedRows=unchanged.Records.Select(r=>r.Clone()).ToList();
    changedRows[0].TradeMoney-=.01;changedRows[1].TradeMoney+=.01;
    var changed=Result(unchangedRequest,changedRows);
    var completed=(FlowAutoGenerationResult)Call("CompleteCorrectedGeneratedResult",unchangedRequest,unchanged,changed)!;
    Quota(completed.Records);Preserved(changed,completed);
}
static void TestVolume()
{
    var rows=Profile(10000);var request=Request(rows);var source=Result(request,rows);var timer=Stopwatch.StartNew();
    var result=Complete(request,source);timer.Stop();Preserved(source,result);Quota(result.Records);
    var maxTail=result.Records.GroupBy(Tail).Where(g=>g.Key>=11 && g.Key<90).Max(g=>g.Count());
    Console.WriteLine("  Tail histogram: "+string.Join(", ",result.Records.GroupBy(Tail).OrderByDescending(g=>g.Count()).Take(8).Select(g=>$"{g.Key:D2}={g.Count()}")));
    Check(maxTail<rows.Count*.08,"正常尾数出现新的集中");
    Check(timer.Elapsed.TotalSeconds<15,"尾数处理耗时过长: "+timer.Elapsed);
    Console.WriteLine($"  10000 rows: {timer.ElapsedMilliseconds} ms, max regular tail count={maxTail}");
}
static void TestGenerationExits()
{
    foreach(var bank in new[]{Bank(),Bank("建行",8),Bank("微信",2),Bank("工行",4)})
    {
        var request=new FlowAutoGenerationRequest
        {
            Bank=bank,BankUser=new BankUser{Id=9001,AutoCalculateInterest=false},
            Config=new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,3,31),OpeningBalance=1000,AllInMoney=10000,LastMoney=1000},
            References=[new GenerateReferenceRule{Index=1,BankId=bank.Id,IsCheck=true,IncomeAttribute="收入",MinMoney=50,MaxMoney=200,FloutLength=2,TradeHoliday=true,TradeWeekend=true,StartDay=9,EndDay=20},
                new GenerateReferenceRule{Index=2,BankId=bank.Id,IsCheck=true,IncomeAttribute="支出",MinMoney=10,MaxMoney=100,FloutLength=2,TradeHoliday=true,TradeWeekend=true,StartDay=9,EndDay=20}],ConstItems=[]
        };
        var result=new FlowAutoGenerator().Generate(request);
        Check(result.Records.Count>0 && result.Records.All(r=>r.AccountTime>=request.Config.StartTime && r.AccountTime<=request.Config.EndTime.AddDays(1)),"生成结果异常");
        Check(Math.Abs(result.IncomeTotal-result.ExpenseTotal-1000)<=1000,"生成预算异常");
    }
}
static void TestSmallGroups()
{
    var rows=new List<FlowRecord>{Row(1,-40.53,new DateTime(2026,10,1,9,0,0)),
        Row(2,-20.01,new DateTime(2026,10,1,10,0,0),20.01,100.99)};
    var request=Request(rows);var source=Result(request,rows);var result=Complete(request,source);
    Quota(result.Records);Preserved(source,result);
}
static void TestBocPreferredClosing()
{
    var rows=Enumerable.Range(1,80).Select(i=>Row(i,-80.57,new DateTime(2026,10,1,9,0,0).AddMinutes(i),20.01,100.99)).ToList();
    var fixedRow=Row(1000,-30.17,new DateTime(2026,10,1,20,0,0),10.01,100.99,"Const");rows.Add(fixedRow);
    var target=Math.Round(rows.Sum(r=>Math.Abs(r.TradeMoney!.Value))-2500,2);
    Check((bool)Call("AdjustBankOfChinaSignedTotal",rows,false,target)!,"应完成优先收口");
    Check(Math.Abs(rows.Sum(r=>Math.Abs(r.TradeMoney!.Value))-target)<.001,"收口总额不准");
    Check(rows.Where(r=>r.Id!=1000).All(r=>Tail(r)>9),"收口不应集中制造0几尾数");
    Check(rows.Where(r=>r.TradeMoney>-80).Select(Tail).Distinct().Count()>8,"收口尾数不应固定为.11");
    Check(fixedRow.TradeMoney==-30.17,"不应改固定日期项目");
}
static void TestBocClosingFallback()
{
    var rows=new List<FlowRecord>{Row(1,-21.11,new DateTime(2026,10,1,9,0,0),20.01,30.99)};
    Check((bool)Call("AdjustBankOfChinaSignedTotal",rows,false,20.05)!,"不足偏好容量时仍应收口成功");
    Check(rows.Count==1&&Tail(rows[0])>9,"不得为满足不可行的精确目标而回退到20.05");
    rows=[Row(1,-20.01,new DateTime(2026,10,1,9,0,0),20.01,20.09)];
    Reject(()=>Call("AdjustBankOfChinaSignedTotal",rows,false,20.07));
}
static void TestBocGenerationWithInterest()
{
    var previous=Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    try
    {
        for(var seed=1701;seed<=1703;seed++)
        {
            Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED",seed.ToString(CultureInfo.InvariantCulture));
            var bank=Bank();var setting=BankInterestSettingDefaults.CreateDefault(bank);
            var request=new FlowAutoGenerationRequest{Bank=bank,BankUser=new BankUser{Id=9001,AutoCalculateInterest=true},InterestSetting=setting,
                Config=new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,10,31),OpeningBalance=35000,AllInMoney=10000,LastMoney=1000},
                References=Enumerable.Range(1,8).Select(i=>new GenerateReferenceRule{Index=i,IsCheck=true,BankId=18,IncomeAttribute="支出",
                    MinMoney=10+i,MaxMoney=150,FloutLength=2,PercentMonth=2,StartDay=9,EndDay=20,TradeHoliday=true,TradeWeekend=true,ProductBrief="模拟商户"+i}).ToList(),
                ConstItems=[new GenerateConstRule{Index=1,IsCheck=true,BankId=18,IncomeAttribute="收入",MinMoney=1000,MaxMoney=1000,FloutLength=0,
                    StartDay=9,EndDay=17,FixDay="15",ReCnt="1",TradeHoliday=true,TradeWeekend=true,ProductBrief="模拟工资"}]};
            var result=new FlowAutoGenerator().Generate(request);Quota(result.Records);
            Check(BankOfChinaGenerationBudget.TryValidate(bank,request.Config,result,out var reason),reason);
            var copy=result.Records.Select(r=>r.Clone()).ToList();
            BankInterestCalculationService.Recalculate(bank,request.BankUser,setting,copy,result.OpeningBalance,request.Config.StartTime,request.Config.EndTime,random:new Random(17));
            Check(result.Records.Where(FlowGeneratedRowKinds.IsSystemInterest).Select(r=>r.TradeMoney).SequenceEqual(copy.Where(FlowGeneratedRowKinds.IsSystemInterest).Select(r=>r.TradeMoney)),"最终利息不正确");
            Check(result.Records.Where(r=>r.ExtraFields.GetValueOrDefault("__GeneratedSourceKind")=="Const").All(r=>r.TradeMoney==1000),"固定工资不应改变");
        }
    }
    finally{Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED",previous);}
}

static void TestHardLegalAmounts()
{
    var rule=new GenerateReferenceRule{Index=1,IsCheck=true,MinMoney=.01,MaxMoney=3.99,FloutLength=2};
    var before=Snapshot(rule);var bounds=Call("GetRuleAmountBounds",rule)!;
    Check((double)bounds.GetType().GetProperty("Min")!.GetValue(bounds)! == .11,"第一个合法两位金额应为0.11");
    var values=(List<double>)Call("EnumerateDiscreteAmounts",bounds,1000)!;
    Check(values.Count==324 && values.Distinct().Count()==324,"4个完整元区间各应有81个合法值");
    Check(values.All(value=>value>=.11&&value<=3.99&&(long)Math.Round(value*100)%100>=11&&(long)Math.Round(value*100)%10!=0),"候选集中仍存在禁止值");
    foreach(var value in new[]{.01,.05,.09,1.01,1.09,2.00,3.10})
        Check(!(bool)Call("HasRequiredNonZeroPrecisionDigit",value,bounds)!,"候选校验应拒绝: "+value);
    Check((double)Call("FindValidAmountAtOrAbove",1d,3.99,bounds)! == 1.11,"整数边界向上必须跨过整段禁止区间");
    Check((double)Call("FindValidAmountAtOrBelow",2.10,.11,bounds)! == 1.99,"整数边界向下必须跨过整段禁止区间");
    Check(before==Snapshot(rule),"合法值派生不能改写用户金额配置");
    foreach(var precision in new[]{-2,-1,0,1})
    {
        var other=Call("GetRuleAmountBounds",new GenerateReferenceRule{MinMoney=100,MaxMoney=500,FloutLength=precision})!;
        var unchanged=(List<double>)Call("EnumerateDiscreteAmounts",other,10000)!;
        Check(unchanged.Count>0,"其他精度不应失去合法候选");
    }
}
static void TestHardCcbRanking()
{
    var type=typeof(FlowAutoGenerator).GetNestedType("CcbAmountRange",BindingFlags.NonPublic)!;
    foreach(var (min,max) in new[]{(.01,3.99),(.99,1.11),(20.01,20.11),(20.99,21.11),(5.12,5.29)})
    {
        var bounds=Call("GetRuleAmountBounds",new GenerateReferenceRule{MinMoney=min,MaxMoney=max,FloutLength=2})!;
        var range=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[bounds],null)!;
        var count=(long)type.GetProperty("Count")!.GetValue(range)!;
        var expected=Enumerable.Range((int)Math.Ceiling(min*100-.000001),(int)Math.Floor(max*100+.000001)-(int)Math.Ceiling(min*100-.000001)+1)
            .Where(cents=>cents%100>=11&&cents%10!=0).Select(cents=>cents/100d).ToArray();
        Check(count==expected.Length,"建行合法金额数量计算不符");
        for(long rank=0;rank<count;rank++)
        {
            var value=(double)type.GetMethod("Value")!.Invoke(range,[rank])!;
            Check(value==expected[rank],"建行排序映射包含禁用尾数或漏掉合法值");
            Check((long)type.GetMethod("RankAtOrBelow")!.Invoke(range,[value])! == rank,"建行反向排序不一致");
        }
    }
}
static void TestHardOutputGuard()
{
    foreach(var sign in new[]{-1,1})
    foreach(var tail in Enumerable.Range(1,9))
    foreach(var kind in new[]{"Reference","Const"})
    {
        var row=Row(1,sign*(20+tail/100d),new DateTime(2026,10,1),kind:kind);
        var rows=new List<FlowRecord>{row};var request=Request(rows);var source=Result(request,rows);var before=Snapshot(source);
        Reject(()=>Call("EnsureGeneratedCentTailResult",request,source));
        Check(before==Snapshot(source),"出口拒绝不能改写输入或固定值");
        row.IsDocumentImported=true;Check(ReferenceEquals(source,Call("EnsureGeneratedCentTailResult",request,source)),"导入数据必须原样保留");
        row.IsDocumentImported=false;row.GeneratedRowKind="Interest";
        Check(ReferenceEquals(source,Call("EnsureGeneratedCentTailResult",request,source)),"结息金额必须原样保留");
    }
    var synthetic=Row(0,-20.01,new DateTime(2026,10,1));synthetic.ExtraFields.Clear();
    var syntheticRows=new List<FlowRecord>{synthetic};var syntheticRequest=Request(syntheticRows);var syntheticResult=Result(syntheticRequest,syntheticRows);
    Reject(()=>Call("EnsureGeneratedCentTailResult",syntheticRequest,syntheticResult));
    synthetic.Id=42;Check(ReferenceEquals(syntheticResult,Call("EnsureGeneratedCentTailResult",syntheticRequest,syntheticResult)),"既有手工记录不应被作为新生成行改动");
    synthetic.Id=0;synthetic.TradeMoney=-20.11;
    Check(ReferenceEquals(syntheticResult,Call("EnsureGeneratedCentTailResult",syntheticRequest,syntheticResult)),"合法尾数应通过");
}
static void TestHardRuleConflicts()
{
    foreach(var fixedItem in new[]{false,true})
    foreach(var tail in Enumerable.Range(1,9))
    {
        var money=20+tail/100d;
        var request=new FlowAutoGenerationRequest{Bank=Bank(),BankUser=new BankUser{Id=9001,AutoCalculateInterest=false},
            Config=new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,3,31),OpeningBalance=10000,AllInMoney=1000,LastMoney=100},
            References=fixedItem?[]:[new GenerateReferenceRule{Index=7,IsCheck=true,MinMoney=money,MaxMoney=money,FloutLength=2}],
            ConstItems=fixedItem?[new GenerateConstRule{Index=7,IsCheck=true,MinMoney=money,MaxMoney=money,FloutLength=2,FixDay="15",ReCnt="1"}]:[]};
        var before=Snapshot(request);Reject(()=>new FlowAutoGenerator().Generate(request));
        Check(before==Snapshot(request),"冲突应提示用户，不能改写配置");
    }
    var rule=new GenerateReferenceRule{Index=7,IsCheck=true,MinMoney=20.01,MaxMoney=20.11,FloutLength=2};
    var bounds=Call("GetRuleAmountBounds",rule)!;
    Check((double)bounds.GetType().GetProperty("Min")!.GetValue(bounds)! == 20.11,"部分合法的窄区间应保留合法值");
    var inactive=new FlowAutoGenerationRequest{Bank=Bank(),BankUser=new BankUser{Id=9001},Config=new FlowGenerationConfig(),
        References=[new GenerateReferenceRule{Index=8,IsCheck=false,MinMoney=20.01,MaxMoney=20.09,FloutLength=2}],
        ConstItems=[new GenerateConstRule{Index=9,IsCheck=true,FixDay=null,MinMoney=20.01,MaxMoney=20.09,FloutLength=2}]};
    Call("ValidateGeneratedCentTailRules",inactive);
}
static void TestHardPublicGeneration()
{
    var previous=Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    var categories=0;var generatedRows=0;
    try
    {
        foreach(var field in new[]{"LegacyPersonalBankIds","LegacyCorporateBankIds"})
        {
            var catalog=(IReadOnlyDictionary<string,long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field,BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            foreach(var item in catalog)
            {
                var bank=Bank(item.Key,item.Value,field.Contains("Personal",StringComparison.Ordinal)?BankTypes.Personal:BankTypes.Corporate);
                foreach(var method in new[]{"ConfigureFlowGenerationColumns","ConfigureFlowRecordColumns"})
                    typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[bank]);
                for(var seed=2201;seed<=2203;seed++)
                {
                    Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED",seed.ToString(CultureInfo.InvariantCulture));
                    var request=new FlowAutoGenerationRequest{Bank=bank,BankUser=new BankUser{Id=9001,AutoCalculateInterest=false},
                        Config=new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,3,31),OpeningBalance=50000,AllInMoney=6000,LastMoney=1000},
                        References=[new GenerateReferenceRule{Index=1,BankId=bank.Id,IsCheck=true,IncomeAttribute="收入",MinMoney=20,MaxMoney=300,FloutLength=2,StartDay=9,EndDay=20,TradeHoliday=true,TradeWeekend=true,ProductBrief="模拟收入"},
                            new GenerateReferenceRule{Index=2,BankId=bank.Id,IsCheck=true,IncomeAttribute="支出",MinMoney=5,MaxMoney=80,FloutLength=2,StartDay=9,EndDay=20,TradeHoliday=true,TradeWeekend=true,ProductBrief="模拟消费"}],ConstItems=[]};
                    var generator=new FlowAutoGenerator();var config=Snapshot(request);
                    var result=generator.Generate(request);Quota(result.Records);generatedRows+=result.Records.Count;
                    foreach(var corrected in new[]{generator.ApplyNegativeBalanceCorrection(request,result),generator.ApplyFinalBalanceCorrection(request,result),generator.ApplyOpeningBalanceCorrection(request,result,result.OpeningBalance)})
                        Quota(corrected.Records);
                    Check(config==Snapshot(request),"不能为避免尾数改写用户配置: "+bank.Name);
                }
                categories++;
            }
        }
    }
    finally{Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED",previous);}
    Check(categories==34,"应真实覆盖全部34类银行");
    Console.WriteLine($"  strict generation: {categories} bank categories x3 seeds, {generatedRows} rows, plus all correction exits");
}
static void TestNarrowLegalGap()
{
    var previous=Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    try
    {
        foreach(var bank in new[]{Bank(),Bank("建行",8),Bank("工行",4),Bank("微信",2)})
        for(var seed=3201;seed<=3203;seed++)
        {
            Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED",seed.ToString(CultureInfo.InvariantCulture));
            var request=new FlowAutoGenerationRequest{Bank=bank,BankUser=new BankUser{Id=9001,AutoCalculateInterest=false},
                Config=new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,3,31),OpeningBalance=1000,AllInMoney=100,LastMoney=10},
                References=[new GenerateReferenceRule{Index=1,BankId=bank.Id,IsCheck=true,IncomeAttribute="收入",MinMoney=1.91,MaxMoney=2.19,FloutLength=2,StartDay=9,EndDay=20,TradeHoliday=true,TradeWeekend=true},
                    new GenerateReferenceRule{Index=2,BankId=bank.Id,IsCheck=true,IncomeAttribute="支出",MinMoney=.91,MaxMoney=1.19,FloutLength=2,StartDay=9,EndDay=20,TradeHoliday=true,TradeWeekend=true}],ConstItems=[]};
            var result=new FlowAutoGenerator().Generate(request);Quota(result.Records);
            Check(result.Records.Any(r=>r.TradeMoney<0),"合法窄范围支出不能被省略");
            foreach(var row in result.Records)
                Check(Math.Abs(row.TradeMoney!.Value)>=(row.TradeMoney>0?1.91:.91)-.00001&&Math.Abs(row.TradeMoney.Value)<=(row.TradeMoney>0?2.19:1.19)+.00001,"窄区间金额越界");
        }
    }
    finally{Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED",previous);}
}

static List<FlowRecord> TimelineRows(int months = 12, int perMonth = 10, double amount = -80.57) =>
    Enumerable.Range(0, months).SelectMany(month => Enumerable.Range(0, perMonth).Select(day =>
        Row(month * perMonth + day + 1, amount, new DateTime(2025, 1, 1, 9, 0, 0).AddMonths(month).AddDays(day), 20.11, 100.99))).ToList();

static void CheckTimelineSpread(IEnumerable<FlowRecord> rows, int months, int maxDifference = 1)
{
    var counts = rows.GroupBy(row => row.AccountTime!.Value.ToString("yyyy-MM")).Select(group => group.Count()).ToArray();
    Check(counts.Length == months && counts.Max() - counts.Min() <= maxDifference,
        "修补条数未均匀覆盖可用月份: " + string.Join(",", counts));
}

static void TestTimelineRepairOrdering()
{
    var rows = TimelineRows(19, 14);
    var before = Snapshot(rows);
    var ordered = (List<FlowRecord>)Call("OrderAmountRepairCandidates", rows)!;
    Check(before == Snapshot(rows), "候选排序不能改动输入流水");
    Check(ordered.Count == rows.Count && ordered.ToHashSet().SetEquals(rows), "不能复制、增删流水");
    for (var round = 1; round <= 7; round++) CheckTimelineSpread(ordered.Take(round * 19), 19, 0);
    Check(ordered.Select(row => row.Id).SequenceEqual(((List<FlowRecord>)Call("OrderAmountRepairCandidates", rows)!).Select(row => row.Id)), "同一候选集顺序应稳定");
    var monthRows = TimelineRows(1, 28);
    var monthOrder = (List<FlowRecord>)Call("OrderAmountRepairCandidates", monthRows)!;
    var dates = monthOrder.Take(7).Select(row => row.AccountTime!.Value.Day).Order().ToArray();
    Check(dates.First() <= 4 && dates.Last() >= 25 && dates.Zip(dates.Skip(1), (a,b) => b-a).Max() <= 5, "单月修补应覆盖月初月中月末");
    var unknown = Row(9999, -80.57, new DateTime(2026,1,1)); unknown.AccountTime = null; rows.Add(unknown);
    Check(((List<FlowRecord>)Call("OrderAmountRepairCandidates", rows)!).Last() == unknown, "无日期候选不能被丢弃或补造日期");
    Check(((List<FlowRecord>)Call("OrderAmountRepairCandidates", new List<FlowRecord>())!).Count == 0, "空候选必须可用");
    var large = Enumerable.Range(0, 100000).Select(i => Row(i+1, -80.57, new DateTime(2026,1,1).AddSeconds(i))).ToList();
    var watch = Stopwatch.StartNew(); var largeOrder = (List<FlowRecord>)Call("OrderAmountRepairCandidates", large)!;
    Check(largeOrder.Count == large.Count && largeOrder.ToHashSet().Count == large.Count, "大批量不能遗漏候选");
    Check(watch.Elapsed < TimeSpan.FromSeconds(10), "候选轮转不能按已耗尽月份反复扫描");
}

static void TestTimelineSignedClosing()
{
    foreach (var sign in new[] { -1, 1 })
    foreach (var increase in new[] { false, true })
    {
        var rows = TimelineRows(amount: sign * (increase ? 20.11 : 80.57));
        var times = rows.Select(row => row.AccountTime).ToArray();
        var before = rows.ToDictionary(row => row.Id, row => row.TradeMoney);
        var bounds = Call("GetRecordAmountBounds", rows[0])!;
        var unitMove = increase ? (double)Call("GetPreferredAmountUpperBound", rows[0], bounds)! - 20.11 : 80.57 - 20.11;
        var amount = Math.Round(unitMove * 24, 2);
        var total = rows.Sum(row => Math.Abs(row.TradeMoney!.Value));
        if (increase) Call("IncreaseSignedRecordsWithinBounds", rows, sign > 0, amount);
        else Call("DecreaseSignedRecordsWithinBounds", rows, sign > 0, amount, false);
        var changed = rows.Where(row => Math.Abs(row.TradeMoney!.Value - before[row.Id]!.Value) > .001).ToList();
        CheckTimelineSpread(changed, 12, 0);
        Check(Math.Abs(rows.Sum(row => Math.Abs(row.TradeMoney!.Value)) - total - (increase ? amount : -amount)) < .001, "修补收支总额不准确");
        Check(rows.Select(row => row.AccountTime).SequenceEqual(times), "金额修补不能移动日期");
        Check(rows.All(row => Math.Abs(row.TradeMoney!.Value) is >= 20.11 and <= 100.99 && Math.Sign(row.TradeMoney.Value) == sign && Tail(row) >= 11 && Tail(row)%10 != 0), "修补不能改变范围、方向或精度");
    }
}

static void TestTimelineBocClosing()
{
    foreach (var sign in new[] { -1, 1 })
    {
        var rows = TimelineRows(amount: sign * 80.57);
        var before = rows.ToDictionary(row => row.Id, row => row.TradeMoney);
        var times = rows.Select(row => row.AccountTime).ToArray();
        var target = Math.Round(rows.Sum(row => Math.Abs(row.TradeMoney!.Value)) - (80.57 - 20.11) * 24, 2);
        Call("AdjustBankOfChinaSignedTotal", rows, sign > 0, target);
        var changed = rows.Where(row => row.TradeMoney != before[row.Id]).ToList();
        CheckTimelineSpread(changed, 12);
        Check(rows.Select(row => row.AccountTime).SequenceEqual(times), "中行收口不能移动日期");
        Check(Math.Abs(rows.Sum(row => Math.Abs(row.TradeMoney!.Value)) - target) < .001, "中行收口总额变化");
        Check(changed.Select(Tail).Distinct().Count() > 8, "有空间时修补尾数也应变化");
    }
}

static void TestTimelineExactClosing()
{
    var rows = TimelineRows(); var request = Request(rows);
    var expense = rows.Sum(row => Math.Abs(row.TradeMoney!.Value));
    request.Config.LastMoney = -Math.Round(expense - (80.57 - 20.11) * 24, 2);
    Call("CloseExactFinalBalanceWithExistingRecords", rows, request, request.Config.OpeningBalance);
    CheckTimelineSpread(rows.Where(row => Tail(row) == 11), 12, 0);
    Check(Math.Abs(rows.Last().Balance!.Value - request.Config.OpeningBalance - request.Config.LastMoney) < .001, "精确收口未满足余额");

    rows = TimelineRows(perMonth: 1, amount: -80.57);
    rows.AddRange(Enumerable.Range(0,12).Select(month => Row(1000+month,100,new DateTime(2025,1,1,8,0,0).AddMonths(month),100,100,"Const",0)));
    request = Request(rows); request.Config.OpeningBalance = 0; request.Config.LastMoney = 50;
    Call("CloseExactFinalBalanceWithExistingRecords", rows, request, 0d);
    Check(rows.All(row => row.Balance >= -.009), "分散增加支出时必须实时检查后续最低余额");
    Check(Math.Abs(rows.Last().Balance!.Value - 50) < .009, "有充足容量时不应因保守静态余额而收口失败");
    Check(rows.Where(row => row.TradeMoney > 0).All(row => row.TradeMoney == 100), "不得修改固定收入");
    Quota(rows);
}

static void TestTimelineMonthlyClosing()
{
    var rows = TimelineRows(perMonth: 28);
    var before = rows.ToDictionary(row => row.Id, row => row.TradeMoney);
    Call("ReduceMonthlyOptionalExpense", rows, new DateTime(2025,6,1), new DateTime(2025,7,1).AddTicks(-1), Math.Round(80.57*7,2));
    var changed = rows.Where(row => row.TradeMoney != before[row.Id]).ToList();
    Check(changed.Count == 7 && changed.All(row => row.AccountTime!.Value.Month == 6), "按月预算不可跨月修补");
    Check(changed.Min(row => row.AccountTime!.Value.Day) <= 4 && changed.Max(row => row.AccountTime!.Value.Day) >= 25, "修补不能只落在当月末尾");
}

static List<FlowRecord> RepeatedTailRows(int count = 243, int sign = -1) => Enumerable.Range(0, count)
    .Select(i => Row(i + 1, sign * (50 + i % 20 + .31), new DateTime(2026, 1, 1, 9, 0, 0).AddHours(i * 3),20.11,200.99)).ToList();

static int RecentTailRepeats(IEnumerable<FlowRecord> rows, int window = 8)
{
    var count = 0;
    foreach (var group in rows.Where(r => !FlowGeneratedRowKinds.IsSystemInterest(r) && r.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision") == "2")
                 .GroupBy(r => (Math.Sign(r.TradeMoney ?? 0), r.Account)))
    {
        var last = new Dictionary<int,int>(); var position = 0;
        foreach (var row in group.OrderBy(r => r.AccountTime).ThenBy(r => r.Index))
        {
            var tail = Tail(row);
            if (last.TryGetValue(tail,out var previous) && position - previous < window) count++;
            last[tail] = position++;
        }
    }
    return count;
}

static void TestFinalTailRounds()
{
    foreach (var sign in new[] { -1, 1 })
    {
        var rows = RepeatedTailRows(sign:sign); var request = Request(rows); var source = Result(request,rows); var snapshot = Snapshot(source);
        var result = Complete(request,source);
        Preserved(source,result); Quota(result.Records);
        Check(snapshot == Snapshot(source), "尾数轮次不得改写源结果");
        var tails = result.Records.OrderBy(r => r.AccountTime).Select(Tail).ToArray();
        Check(tails.Take(81).Distinct().Count() == 81, "首轮应覆盖81个合法尾数: " + tails.Take(81).Distinct().Count());
        Check(tails.Skip(81).Take(81).Distinct().Count() == 81, "第二轮应覆盖81个合法尾数");
        Check(RecentTailRepeats(result.Records) <= 2, "有充分空间时不应留下大量近距离重复");
        Check(Snapshot(result) == Snapshot(Complete(request,result)), "最终轮次处理应幂等");
    }
}

static void TestFinalTailAllBanks()
{
    var count = 0;
    foreach (var field in new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" })
    {
        var catalog = (IReadOnlyDictionary<string,long>)typeof(SpeedEmulator.ViewModels.MainViewModel)
            .GetField(field,BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var pair in catalog)
        {
            var rows = RepeatedTailRows(162); var request = Request(rows,Bank(pair.Key,pair.Value,field.Contains("Personal",StringComparison.Ordinal)?BankTypes.Personal:BankTypes.Corporate));
            var source = Result(request,rows); var result = Complete(request,source);
            Preserved(source,result); Quota(result.Records);
            Check(result.Records.Take(81).Select(Tail).Distinct().Count() == 81, "银行首轮覆盖不足: " + pair.Key);
            Check(RecentTailRepeats(result.Records) <= 2, "银行仍有大量近距重复: " + pair.Key);
            count++;
        }
    }
    Check(count == 34, "应覆盖34类银行");
}

static void TestFinalTailBudgetWindows()
{
    var rows = RepeatedTailRows(162);
    for (var i = 0; i < rows.Count; i++) rows[i].AccountTime = new DateTime(2026,1,28,9,0,0).AddHours(i);
    var request = Request(rows); request.Config.SelectIndex = 2;
    request.Config.MonthGenData.Add(new MonthGenerateRule { StartTime=new DateTime(2026,1,28,0,0,0),EndTime=new DateTime(2026,1,30,11,59,59) });
    request.Config.MonthGenData.Add(new MonthGenerateRule { StartTime=new DateTime(2026,1,30,12,0,0),EndTime=new DateTime(2026,2,4,23,59,59) });
    var source = Result(request,rows); var result = Complete(request,source); Preserved(source,result);
    foreach (var period in request.Config.MonthGenData)
    {
        var a = source.Records.Where(r => r.AccountTime >= period.StartTime && r.AccountTime <= period.EndTime).Sum(r => r.TradeMoney ?? 0);
        var b = result.Records.Where(r => r.AccountTime >= period.StartTime && r.AccountTime <= period.EndTime).Sum(r => r.TradeMoney ?? 0);
        Check(Math.Abs(a-b) < .001, "尾数配对不得挪用不同明细窗口预算");
    }
    Check(RecentTailRepeats(result.Records) < RecentTailRepeats(source.Records) / 4, "预算窗口隔离后仍应明显减少重复");
}

static void TestFinalTailInterest()
{
    var rows = RepeatedTailRows(162);
    foreach (var row in rows) row.AccountTime = new DateTime(2026,1,1,9,0,0).AddMinutes(row.Id);
    var request = WithInterest(rows,.35);
    BankInterestCalculationService.Recalculate(request.Bank,request.BankUser,request.InterestSetting,rows,request.Config.OpeningBalance,
        request.Config.StartTime,request.Config.EndTime,random:new Random(17));
    foreach (var row in rows.Where(FlowGeneratedRowKinds.IsSystemInterest)) { row.Id=2000+rows.IndexOf(row); row.Remark="原始结息说明"; row.VoucherNum="original-interest-voucher"; }
    var source = Result(request,rows); var result = Complete(request,source); Preserved(source,result);
    Check(RecentTailRepeats(result.Records) <= 2, "同日合法配对应减少尾数重复");
    foreach (var row in source.Records.Where(FlowGeneratedRowKinds.IsSystemInterest))
        Check(Snapshot(row) == Snapshot(result.Records.Single(r => r.Id==row.Id)), "结息原值、时间及显示字段不得改变");
}

static void TestFinalTailLockedRanges()
{
    var rows = RepeatedTailRows(30);
    foreach (var row in rows)
    {
        row.TradeMoney=-20.11;
        row.ExtraFields["__GeneratedAmountMin"]="20.11";
        row.ExtraFields["__GeneratedAmountMax"]="20.11";
    }
    var request=Request(rows); var source=Result(request,rows); var snapshot=Snapshot(source); var result=Complete(request,source);
    Check(ReferenceEquals(source,result) && snapshot==Snapshot(result),"不能为轮转尾数突破唯一合法金额");
    rows=RepeatedTailRows(30);
    foreach (var row in rows) { row.TradeMoney=-20.91; row.ExtraFields["__GeneratedAmountMin"]="20.91"; row.ExtraFields["__GeneratedAmountMax"]="21.19"; }
    request=Request(rows); source=Result(request,rows); result=Complete(request,source); Preserved(source,result); Quota(result.Records);
    Check(result.Records.All(r => Math.Abs(r.TradeMoney!.Value) is >=20.91 and <=21.19),"跨整数窄区间必须保留范围");
}

static void TestFinalTailTimeCorrection()
{
    var rows=RepeatedTailRows();foreach(var row in rows)row.ExtraFields["__GeneratedSourceIndex"]="1";
    var request=Request(rows);var source=Complete(request,Result(request,rows));
    var pair=source.Records.GroupBy(Tail).First(group=>group.Count()>1).OrderBy(r=>r.AccountTime).Take(2).ToArray();
    var moved=source.Records.Select(r=>r.Clone()).ToList();
    moved.Single(r=>r.Id==pair[1].Id).AccountTime=pair[0].AccountTime!.Value.AddSeconds(1);
    var corrected=Result(request,moved);var before=RecentTailRepeats(corrected.Records);var snapshot=Snapshot(corrected);
    var result=(FlowAutoGenerationResult)Call("CompleteCorrectedGeneratedResult",request,source,corrected)!;
    Console.WriteLine($"  time-only spacing: before={before}, after={RecentTailRepeats(result.Records)}");
    Check(RecentTailRepeats(result.Records)<before,"只修日期后的相邻尾数重复也应处理");
    Check(snapshot==Snapshot(corrected),"不能改写时间修正的输入结果");
    Check(corrected.Records.Select(r=>r.TradeMoney).Order().SequenceEqual(result.Records.Select(r=>r.TradeMoney).Order()),"时间修正后必须保持原有金额集合");
    Preserved(corrected,result);Quota(result.Records);
}

static void TestFixedIntegerBounds()
{
    foreach (var fixedItem in new[] { false, true })
    foreach (var precision in new[] { 1, 2, 3 })
    foreach (var amount in new[] { 1d, 25d, 100d, 1000d })
    {
        FlowRuleBase rule = fixedItem ? new GenerateConstRule { FixDay="15" } : new GenerateReferenceRule();
        rule.Index=56;rule.IsCheck=true;rule.MinMoney=rule.MaxMoney=amount;rule.FloutLength=precision;
        var before=Snapshot(rule);var bounds=Call("GetRuleAmountBounds",rule)!;
        Check((double)bounds.GetType().GetProperty("Min")!.GetValue(bounds)! == amount &&
              (double)bounds.GetType().GetProperty("Max")!.GetValue(bounds)! == amount,"固定整数必须原值保留");
        Check((double)bounds.GetType().GetProperty("Unit")!.GetValue(bounds)! == 1 &&
              (int)bounds.GetType().GetProperty("Precision")!.GetValue(bounds)! == 0,"运行期应按整数处理");
        Check((bool)Call("HasRequiredNonZeroPrecisionDigit",amount,bounds)!,"固定整数不应再被小数末位检查拒绝");
        var values=(List<double>)Call("EnumerateDiscreteAmounts",bounds,10)!;
        Check(values.SequenceEqual(new[]{amount}),"固定整数只能产生唯一配置值");
        Check(before==Snapshot(rule),"不能修改用户的小数位配置");
    }
}

static void TestFixedIntegerGeneration()
{
    var previous=Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");var count=0;
    try
    {
        foreach(var field in new[]{"LegacyPersonalBankIds","LegacyCorporateBankIds"})
        {
            var catalog=(IReadOnlyDictionary<string,long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field,BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            foreach(var item in catalog)
            {
                Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED","5601");
                var bank=Bank(item.Key,item.Value,field.Contains("Personal",StringComparison.Ordinal)?BankTypes.Personal:BankTypes.Corporate);
                var request=new FlowAutoGenerationRequest{Bank=bank,BankUser=new BankUser{Id=9001,AutoCalculateInterest=false},
                    Config=new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,3,31),OpeningBalance=5000,AllInMoney=3000,LastMoney=1000},
                    References=[new GenerateReferenceRule{Index=56,BankId=bank.Id,IsCheck=true,IncomeAttribute="支出",MinMoney=100,MaxMoney=100,FloutLength=2,PercentMonth=1,StartDay=9,EndDay=20,TradeHoliday=true,TradeWeekend=true},
                        new GenerateReferenceRule{Index=57,BankId=bank.Id,IsCheck=true,IncomeAttribute="支出",MinMoney=5,MaxMoney=80,FloutLength=2,StartDay=9,EndDay=20,TradeHoliday=true,TradeWeekend=true}],
                    ConstItems=[new GenerateConstRule{Index=1,BankId=bank.Id,IsCheck=true,IncomeAttribute="收入",MinMoney=1000,MaxMoney=1000,FloutLength=2,FixDay="15",ReCnt="1",StartDay=9,EndDay=17,TradeHoliday=true,TradeWeekend=true}]};
                var config=Snapshot(request);var generator=new FlowAutoGenerator();var generated=generator.Generate(request);
                foreach(var result in new[]{generated,generator.ApplyNegativeBalanceCorrection(request,generated),generator.ApplyFinalBalanceCorrection(request,generated),generator.ApplyOpeningBalanceCorrection(request,generated,generated.OpeningBalance)})
                {
                    var fixedRows=result.Records.Where(row=>row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind")=="Reference"&&row.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex")=="56").ToArray();
                    var salary=result.Records.Where(row=>row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind")=="Const").ToArray();
                    Check(fixedRows.Length==3&&fixedRows.All(row=>row.TradeMoney==-100),"固定100元及每月次数必须保留: "+bank.Name);
                    Check(salary.Length==3&&salary.All(row=>row.TradeMoney==1000),"固定日期1000元必须保留: "+bank.Name);
                    Check(fixedRows.Concat(salary).All(row=>row.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision")=="0"&&row.ExtraFields.GetValueOrDefault("__GeneratedAmountUnit")=="1"),"元数据必须使用有效整数精度: "+bank.Name);
                    Check(result.Records.Where(row=>row.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex")=="57").All(row=>Tail(row)>=11&&Tail(row)%10!=0),"其他明细仍应遵守两位小数规则");
                }
                Check(config==Snapshot(request),"不能改写用户配置: "+bank.Name);count++;
            }
        }
    }
    finally{Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED",previous);}
    Check(count==34,"必须覆盖34类银行");
}

static void TestFixedIntegerScope()
{
    foreach(var (min,max) in new[]{(100d,100.01),(100.01,100.09),(20.01,20.01)})
    {
        var rule=new GenerateReferenceRule{Index=56,IsCheck=true,MinMoney=min,MaxMoney=max,FloutLength=2};
        var args=new object?[]{rule,null};var method=typeof(FlowAutoGenerator).GetMethod("TryGetRuleAmountBounds",BindingFlags.Static|BindingFlags.NonPublic)!;
        var valid=(bool)method.Invoke(null,args)!;
        Check(!valid,"不能把非固定整数的冲突范围自动扩大或取整");
    }
    var narrow=Call("GetRuleAmountBounds",new GenerateReferenceRule{MinMoney=99.99,MaxMoney=100,FloutLength=2})!;
    Check((double)narrow.GetType().GetProperty("Min")!.GetValue(narrow)! == 99.99 &&
          (double)narrow.GetType().GetProperty("Max")!.GetValue(narrow)! == 99.99 &&
          (int)narrow.GetType().GetProperty("Precision")!.GetValue(narrow)! == 2,"有合法小数的窄区间不能改取整数100");
    var fractional=Call("GetRuleAmountBounds",new GenerateReferenceRule{MinMoney=100.11,MaxMoney=100.11,FloutLength=2})!;
    Check((int)fractional.GetType().GetProperty("Precision")!.GetValue(fractional)! == 2,"合法固定小数仍应保留原精度");
    var coarse=Call("GetRuleAmountBounds",new GenerateReferenceRule{MinMoney=1000,MaxMoney=5000,FloutLength=-2})!;
    Check((double)coarse.GetType().GetProperty("Unit")!.GetValue(coarse)! == 100,"非固定整数的负数取整粒度不变");
}
