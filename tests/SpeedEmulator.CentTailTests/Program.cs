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
    ("固定项目、指定金额、导入、利息、其他精度与外部用户不调整", TestExclusions),
    ("只有0几可选或预算卡在下限时保留原结果，不报错", TestConstrained),
    ("单笔累计不超过0.99元，重复执行稳定", TestCumulative),
    ("按月范围及明细保持每月收支金额", TestMonthlyBudgets),
    ("同一天的不同明细预算窗口也不能混用", TestIntradayBudgets),
    ("建行金额分层保持，单值分层不调整", TestCcbBands),
    ("跨日利息变化时撤销，不改变结息行或余额", TestInterestRollback),
    ("跨日影响利息时仍可回退到同日配对", TestSameDayFallback),
    ("未配置利息且没有结息行时允许跨日安全配对", TestNoInterestSetting),
    ("同日微调保留利息、利息税和全部显示字段", TestSameDayInterest),
    ("已有负余额不恶化，收入支出配对均保护余额", TestNegativeBalances),
    ("支付宝金额关联订单号跟随调整且指定号码保留", TestAlipayAmountSerials),
    ("余额修正三个出口保持尾数且时间修正保留金额", TestCorrectionExits),
    ("大批量处理有界且新尾数不过度集中", TestVolume),
    ("统一生成出口涵盖中行、建行、微信及通用路径", TestGenerationExits),
    ("小分组也不预留0几名额，可行时全部消除", TestSmallGroups),
    ("中行收口优先避开0几且尾数有变化，固定项目不动", TestBocPreferredClosing),
    ("中行偏好容量不足时完整回退原范围和精确预算", TestBocClosingFallback),
    ("真实结息路径下中行新生成流水避免0几尾数", TestBocGenerationWithInterest)
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
        Row(1001,-20.01,time,kind:"Const"), Row(1002,-20.01,time,20.01,20.01),
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
        Check(ReferenceEquals(source, Complete(request, source)), "不可行尾数目标应保留原结果");
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
    var source = Result(request, rows); var result = Complete(request, source);
    Check(ReferenceEquals(source,result), "不能借其他预算窗口的余额完成尾数调整");
}
static void TestCcbBands()
{
    foreach (var locked in new[] { true, false })
    {
        var rows = Profile();
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
    var source=Result(request,rows); var before=Snapshot(source); var result=Complete(request,source);
    Check(ReferenceEquals(source,result), "改变利息的跨日方案必须撤销"); Check(before==Snapshot(source),"撤销不能污染原结果");
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
    var source=Result(request,rows);var result=Complete(request,source);Preserved(source,result);
    Check(result.Records.Count(r=>Tail(r)==1)<source.Records.Count(r=>Tail(r)==1),"应保留可行的同日调整");
    foreach(var day in rows.Where(r=>r.GeneratedRowKind==string.Empty).GroupBy(r=>r.AccountTime!.Value.Date))
        Check(Math.Abs(day.Sum(r=>r.TradeMoney??0)-result.Records.Where(r=>r.GeneratedRowKind==string.Empty && r.AccountTime!.Value.Date==day.Key).Sum(r=>r.TradeMoney??0))<.00001,"回退后日终金额不应变化");
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
    source=Result(request,rows);result=Complete(request,source);
    Check(ReferenceEquals(source,result),"已有结息且缺配置时不能接受跨日调整");
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
    var unchangedRows=Profile();var unchangedRequest=Request(unchangedRows);var unchanged=Result(unchangedRequest,unchangedRows);
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
    var fixedRow=Row(1000,-30.07,new DateTime(2026,10,1,20,0,0),10.01,100.99,"Const");rows.Add(fixedRow);
    var target=Math.Round(rows.Sum(r=>Math.Abs(r.TradeMoney!.Value))-2500,2);
    Check((bool)Call("AdjustBankOfChinaSignedTotal",rows,false,target)!,"应完成优先收口");
    Check(Math.Abs(rows.Sum(r=>Math.Abs(r.TradeMoney!.Value))-target)<.001,"收口总额不准");
    Check(rows.Where(r=>r.Id!=1000).All(r=>Tail(r)>9),"收口不应集中制造0几尾数");
    Check(rows.Where(r=>r.TradeMoney>-80).Select(Tail).Distinct().Count()>8,"收口尾数不应固定为.11");
    Check(fixedRow.TradeMoney==-30.07,"不应改固定日期项目");
}
static void TestBocClosingFallback()
{
    var rows=new List<FlowRecord>{Row(1,-21.11,new DateTime(2026,10,1,9,0,0),20.01,30.99)};
    Check((bool)Call("AdjustBankOfChinaSignedTotal",rows,false,20.05)!,"不足偏好容量时仍应收口成功");
    Check(rows.Count==1&&Math.Abs(rows[0].TradeMoney!.Value+20.05)<.001,"回退应精确使用原范围，不丢弃流水");
    rows=[Row(1,-20.01,new DateTime(2026,10,1,9,0,0),20.01,20.09)];
    Check((bool)Call("AdjustBankOfChinaSignedTotal",rows,false,20.07)!,"只有0几合法时仍应允许增加");
    Check(Math.Abs(rows[0].TradeMoney!.Value+20.07)<.001,"窄区间回退金额错误");
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
