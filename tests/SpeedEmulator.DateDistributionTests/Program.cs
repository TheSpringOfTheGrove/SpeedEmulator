using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

DateDistributionTestOptions.Visible = args.Contains("--visible");
var tests = new (string Name, Action Run)[]
{
    ("期末预留不足不能重新分配已经排好的可选收入", PreserveScheduledIncome),
    ("34类银行：同月收入分散，金额、来源及总笔数保留", AllBanksClump),
    ("34类银行：支出集中也按合法日期分散", AllBanksDebits),
    ("允许周末与仅工作日参照按各自可用日期计算容量", MixedCalendars),
    ("已分散结果不变，重复修正保持幂等", Healthy),
    ("固定日期、导入数据不参加日期散布", ProtectedRows),
    ("仅一天可交易及必要现金约束不会人为抬高期初", HardDates),
    ("自定义月明细预算边界保留", ExplicitPeriods),
    ("变更日期后按原期初重新计算结息", Interest),
    ("生成结果后续余额修正仍检查日期分布且保留金额", GeneratedCorrections),
    ("兴业实际40条全可选配置：多种随机结果不再整月挤首日", Industrial),
    ("34类银行：全可选配置生成结果日期检查", AllBanksGenerated)
};
var failures = 0;
foreach (var test in tests)
{
    if(args.Contains("--generated-only")&&!test.Name.Contains("实际40条")&&!test.Name.Contains("生成结果日期检查"))continue;
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{tests.Length-failures}/{tests.Length} date distribution tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Near(double a, double b) => Check(Math.Abs(a-b)<.009, $"expected {a}, actual {b}");
static object Call(string method, params object?[] values) => typeof(FlowAutoGenerator).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,values)!;
static IEnumerable<Bank> Banks() => new[]{"LegacyPersonalBankIds","LegacyCorporateBankIds"}.SelectMany(field =>
    ((IReadOnlyDictionary<string,long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field,BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!)
        .Select(pair=>new Bank{Id=pair.Value,Name=pair.Key,Type=field.Contains("Corporate")?BankTypes.Corporate:BankTypes.Personal}));
static GenerateReferenceRule Rule(int index, bool income, double min=5000,double max=50000) => new()
{Index=index,IsCheck=true,IncomeAttribute=income?"收入":"支出",MinMoney=min,MaxMoney=max,FloutLength=0,PercentMonth=0,StartDay=9,EndDay=16,TradeWeekend=true,TradeHoliday=true};
static FlowAutoGenerationRequest Request(Bank? bank=null, bool interest=false, int mode=0)
{
    bank ??= Banks().Single(bank=>bank.Id==14);
    foreach(var method in new[]{"ConfigureFlowGenerationColumns","ConfigureFlowRecordColumns"})
        typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[bank]);
    return new FlowAutoGenerationRequest{Bank=bank,BankUser=new BankUser{Id=9000+bank.Id,BankId=bank.Id,AccountNo="9000000000000000",AutoCalculateInterest=interest},
        Config=new FlowGenerationConfig{SelectIndex=mode,StartTime=new(2025,9,16),EndTime=new(2025,9,30),AllInMoney=285000,LastMoney=10000},
        References=[Rule(1,true),Rule(2,false,5000,1000000)],ConstItems=[],InterestSetting=BankInterestSettingDefaults.CreateDefault(bank)};
}
static FlowRecord Row(FlowAutoGenerationRequest request, FlowRuleBase rule, DateTime time, double amount) =>
    (FlowRecord)Call("CreateRecordFromRule",request,rule,rule is GenerateConstRule?request.Bank.ConstColumns:request.Bank.ReferenceColumns,time,amount);
static bool Allowed(FlowAutoGenerationRequest request, DateTime day, FlowRuleBase rule) => request.Bank.Id==8
    ? ConstructionBankGenerationPolicy.IsReferenceDateAllowed(day,rule):ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(day,rule);
static FlowAutoGenerationResult Source(List<FlowRecord> rows,double opening=0) => (FlowAutoGenerationResult)Call("CreateAutomaticBalanceResult",rows,opening);
static FlowAutoGenerationResult Finish(FlowAutoGenerationRequest req,FlowAutoGenerationResult rows) => (FlowAutoGenerationResult)Call("CompleteGeneratedDateDistribution",req,rows);
static List<FlowRecord> Clump(FlowAutoGenerationRequest req,bool expense=false)
{
    var day=Enumerable.Range(0,15).Select(offset=>req.Config.StartTime.AddDays(offset)).First(day=>Allowed(req,day,req.References[0]));
    var rows=Enumerable.Range(0,19).Select(i=>Row(req,req.References[expense?1:0],day.AddHours(9).AddMinutes(i), (expense?-1:1)*(6000+i*1000))).ToList();
    if(!expense)
    {
        var last=Enumerable.Range(0,15).Select(i=>req.Config.StartTime.AddDays(i)).Last(day=>Allowed(req,day,req.References[1]));
        rows.Add(Row(req,req.References[1],last.AddHours(16),-275000));
    }
    return rows;
}
static void Validate(FlowAutoGenerationRequest req,FlowAutoGenerationResult result)
{
    var balance=result.OpeningBalance;
    foreach(var row in result.Records)
    {
        balance=Math.Round(balance+(row.TradeMoney??0),2,MidpointRounding.AwayFromZero);Near(balance,row.Balance!.Value);Check(balance>=-.009,"负余额");
        if(row.IsDocumentImported||FlowGeneratedRowKinds.IsSystemInterest(row))continue;
        if(row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind")!="Reference")continue;
        var rule=req.References.Single(r=>r.Index.ToString()==row.ExtraFields["__GeneratedSourceIndex"]);
        Check(Allowed(req,row.AccountTime!.Value,rule),"违反周末/节假日规则");
        Check(row.AccountTime.Value>=req.Config.StartTime&&row.AccountTime.Value<req.Config.EndTime.AddDays(1),"超出日期范围");
        Check(row.AccountTime.Value.Hour>=Math.Min(rule.StartDay!.Value,rule.EndDay!.Value)&&row.AccountTime.Value.Hour<=Math.Max(rule.StartDay.Value,rule.EndDay.Value),"超出小时区间");
        Check(Math.Abs(row.TradeMoney??0)>=rule.MinMoney-.009&&Math.Abs(row.TradeMoney??0)<=rule.MaxMoney+.009,"超出原金额范围");
    }
    Near(balance,result.FinalBalance);
}
static void Spread(FlowAutoGenerationRequest req,FlowAutoGenerationResult result,bool expense=false)
{
    var rows=result.Records.Where(row=>!FlowGeneratedRowKinds.IsSystemInterest(row)&&(expense?row.TradeMoney<0:row.TradeMoney>0)).ToArray();
    var groups=rows.GroupBy(row=>row.AccountTime!.Value.Date).ToArray();
    var days=Enumerable.Range(0,(req.Config.EndTime-req.Config.StartTime).Days+1).Select(i=>req.Config.StartTime.AddDays(i)).Count(day=>Allowed(req,day,req.References[0]));
    Check(groups.Length>=Math.Min(days,rows.Length)/2,$"{req.Bank.Name}仍集中：{rows.Length}笔只分布{groups.Length}天");
    Check(groups.Max(group=>group.Count())<=Math.Max(3,(int)Math.Ceiling(rows.Length/(double)days*1.5)),req.Bank.Name+"单日异常集中");
}
static void PreserveScheduledIncome()
{
    using var seed=new SeedScope(72);var req=Request();req.Config.EndTime=new(2026,3,16);req.Config.LastMoney=1000;
    var rows=new List<FlowRecord>();
    for(var month=0;month<6;month++)for(var i=0;i<3;i++)
    {
        rows.Add(Row(req,req.References[0],new DateTime(2025,9,20).AddMonths(month).AddDays(i*2).AddHours(12),6000+month*100+i*10));
        rows.Add(Row(req,req.References[1],new DateTime(2025,9,18).AddMonths(month).AddDays(i*2).AddHours(13),-(6000+month*100+i*10)));
    }
    req.Config.AllInMoney=rows.Where(row=>row.TradeMoney>0).Sum(row=>row.TradeMoney!.Value);
    var expected=rows.Where(row=>row.TradeMoney>0).Select(row=>row.AccountTime).ToArray();
    var result=new FlowAutoGenerator().ApplyAutomaticOpeningBalance(req,rows,0);Validate(req,result);
    Check(expected.SequenceEqual(result.Records.Where(row=>row.TradeMoney>0).Select(row=>row.AccountTime)),"已排程收入被整批重排");Near(0,result.OpeningBalance);
}
static void AllBanksClump()
{
    foreach(var bank in Banks())
    {
        using var seed=new SeedScope(72);var req=Request(bank);var rows=Clump(req);var source=Source(rows);var before=JsonSerializer.Serialize(rows);
        var result=Finish(req,source);Validate(req,result);Spread(req,result);
        Near(source.IncomeTotal,result.IncomeTotal);Near(source.ExpenseTotal,result.ExpenseTotal);Near(0,result.OpeningBalance);
        Check(result.Records.Count==source.Records.Count,"改变条数");
        Check(source.Records.Select(row=>row.TradeMoney).Order().SequenceEqual(result.Records.Select(row=>row.TradeMoney).Order()),"日期修复改了金额");
        Check(before==JsonSerializer.Serialize(rows),"改变调用方原数据");
    }
}
static void AllBanksDebits()
{
    foreach(var bank in Banks())
    {
        using var seed=new SeedScope(73);var req=Request(bank);var source=Source(Clump(req,true),400000);var result=Finish(req,source);
        Validate(req,result);Spread(req,result,true);Near(400000,result.OpeningBalance);Near(source.ExpenseTotal,result.ExpenseTotal);
    }
}
static void Healthy()
{
    var req=Request();var rows=Enumerable.Range(0,10).Select(i=>Row(req,req.References[0],new DateTime(2025,9,16+i,12,0,0),6000+i*1000)).ToList();
    var source=Source(rows);Check(ReferenceEquals(source,Finish(req,source)),"健康日期被改动");
    var result=Finish(req,Source(Clump(req)));var before=JsonSerializer.Serialize(result.Records);
    Check(ReferenceEquals(result,Finish(req,result))&&before==JsonSerializer.Serialize(result.Records),"重复检查又改动日期");
}
static void MixedCalendars()
{
    var baseRequest=Request(Banks().Single(bank=>bank.Id==8));
    var permissive=Rule(1,true);var weekdays=Rule(2,true);weekdays.TradeWeekend=false;weekdays.TradeHoliday=false;
    var req=new FlowAutoGenerationRequest{Bank=baseRequest.Bank,BankUser=baseRequest.BankUser,Config=baseRequest.Config,
        References=[permissive,weekdays],ConstItems=[],InterestSetting=baseRequest.InterestSetting};
    req.Config.StartTime=new(2025,9,26);
    var rows=Enumerable.Range(0,90).Select(i=>Row(req,permissive,new DateTime(2025,9,27,9,0,0).AddMinutes(i),6000+i*100))
        .Concat(Enumerable.Range(0,10).Select(i=>Row(req,weekdays,new DateTime(2025,9,26,9,0,0).AddMinutes(i),30000+i*100))).ToList();
    var result=Finish(req,Source(rows));Validate(req,result);Near(0,result.OpeningBalance);
    var permissiveRows=result.Records.Where(row=>row.ExtraFields["__GeneratedSourceIndex"]=="1").ToArray();
    Check(permissiveRows.Length==90 && result.Records.Count==100,"日期分配改变来源笔数");
    Check(result.Records.GroupBy(row=>row.AccountTime!.Value.Date).Count()==5
        && result.Records.GroupBy(row=>row.AccountTime!.Value.Date).All(group=>group.Count()==20),"可行的五日分配仍集中于少数日期");
    Check(permissiveRows.Any(row=>row.AccountTime!.Value.Day is 26 or 29 or 30),"允许周末不应排除普通工作日");
}
static void ProtectedRows()
{
    var req=Request();var rows=Clump(req);var fixedRule=new GenerateConstRule{Index=3,IsCheck=true,IncomeAttribute="收入",MinMoney=5000,MaxMoney=5000,FixDay="16",StartDay=9,EndDay=16};
    var fixedRow=Row(req,fixedRule,new(2025,9,16,15,0,0),5000);rows.Add(fixedRow);
    var imported=Row(req,req.References[0],new(2025,9,16,15,1,0),7000);imported.IsDocumentImported=true;imported.Id=123;rows.Add(imported);
    var source=Source(rows);var beforeFixed=JsonSerializer.Serialize(fixedRow);var beforeImported=JsonSerializer.Serialize(imported);var result=Finish(req,source);
    // Balances follow the new chronology, but protected transaction fields do not change.
    foreach(var original in new[]{fixedRow,imported})
    {
        var kept=result.Records.Single(row=>row.AccountTime==original.AccountTime&&row.TradeMoney==original.TradeMoney);
        Check(kept.IsDocumentImported==original.IsDocumentImported&&kept.SerialNum==original.SerialNum,"改写保护字段");
    }
    Check(JsonSerializer.Serialize(fixedRow)==beforeFixed&&JsonSerializer.Serialize(imported)==beforeImported,"改写原数据");
}
static void HardDates()
{
    var req=Request();req.Config.EndTime=req.Config.StartTime;
    var rows=Enumerable.Range(0,19).Select(i=>Row(req,req.References[0],req.Config.StartTime.AddHours(9).AddMinutes(i),6000+i*1000)).ToList();
    var source=Source(rows);Check(ReferenceEquals(source,Finish(req,source)),"单一合法日期被强行移动");
    req.Config.EndTime=new(2025,9,30);rows.Add(Row(req,new GenerateConstRule{Index=4,IsCheck=true,IncomeAttribute="支出",MinMoney=285000,MaxMoney=285000,FixDay="16"},new(2025,9,16,10,0,0),-285000));
    var result=Finish(req,Source(rows));Near(0,result.OpeningBalance);Near(0,result.MinimumBalance);Check(result.Records.All(row=>row.AccountTime!.Value.Date==req.Config.StartTime),"破坏了首日硬付款的资金来源");
}
static void ExplicitPeriods()
{
    var req=Request(mode:2);req.Config.MonthGenData.Add(new(){StartTime=new(2025,9,16),EndTime=new(2025,9,20)});
    req.Config.MonthGenData.Add(new(){StartTime=new(2025,9,21),EndTime=new(2025,9,30)});
    var result=Finish(req,Source(Clump(req)));
    Check(result.Records.Where(row=>row.TradeMoney>0).All(row=>row.AccountTime<new DateTime(2025,9,21)),"跨过自定义预算期间边界");
    Check(result.Records.Where(row=>row.TradeMoney>0).Select(row=>row.AccountTime!.Value.Date).Distinct().Count()>=4,"自定义期间内没有分散");
}
static void Interest()
{
    var req=Request(interest:true);var result=Finish(req,Source(Clump(req)));Validate(req,result);Spread(req,result);Near(0,result.OpeningBalance);
    var again=new FlowAutoGenerator().ApplyNegativeBalanceCorrection(req,result);
    Near(result.IncomeTotal,again.IncomeTotal);Near(result.ExpenseTotal,again.ExpenseTotal);Near(result.FinalBalance,again.FinalBalance);
}
static void GeneratedCorrections()
{
    var req=Request();var generated=(FlowAutoGenerationResult)Call("CompleteGeneratedDatePhase",req,Source(Clump(req)),true);
    var amounts=generated.Records.Select(row=>row.TradeMoney).Order().ToArray();var index=0;
    foreach(var row in generated.Records.Where(row=>row.TradeMoney>0))row.AccountTime=req.Config.StartTime.AddHours(9).AddMinutes(index++);
    var corrected=new FlowAutoGenerator().ApplyNegativeBalanceCorrection(req,generated);Validate(req,corrected);Spread(req,corrected);
    Check(amounts.SequenceEqual(corrected.Records.Select(row=>row.TradeMoney).Order()),"后续日期修正改变原金额");
    Check(corrected.Records.Any(row=>row.ExtraFields.GetValueOrDefault("__GeneratedDateDistributionFrame")=="1"),"丢失了生成日期校验标记");
}
static FlowAutoGenerationRequest IndustrialRequest(Bank? bank=null)
{
    var req=Request(bank,interest:true);req.Config.EndTime=new(2026,3,16);req.Config.AllInMoney=3000000;req.Config.AllOutMoney=2990000;
    req.Config.MinInMoneyMonth1=req.Config.MaxInMoneyMonth1=428571.43;req.Config.MinOutMoneyMonth1=req.Config.MaxOutMoneyMonth1=427142.86;
    var refs=Enumerable.Range(1,40).Select(i=>{var r=Rule(i,i>20,5000,i<=10||i>=30?50000:30000);r.FloutLength=-1;return r;}).ToArray();
    return new FlowAutoGenerationRequest{Bank=req.Bank,BankUser=req.BankUser,Config=req.Config,References=refs,ConstItems=[],InterestSetting=req.InterestSetting};
}
static void CheckGenerated(FlowAutoGenerationRequest req,FlowAutoGenerationResult result)
{
    Validate(req,result);
    if(DateDistributionTestOptions.Visible&&!FlowAutoGenerator.UsesReferencePriority(req.Config,result.Records)&&!ReferenceNaturalGenerationPolicy.AppliesTo(req.Bank,req.Config))
        Check(Math.Abs(result.FinalBalance-result.OpeningBalance-req.Config.LastMoney)<=FlowAutoGenerator.AcceptedFinalBalanceTolerance,"界面收尾后期末仍超范围");
    var income=result.Records.Where(row=>row.TradeMoney>0&&!FlowGeneratedRowKinds.IsSystemInterest(row)).ToArray();
    foreach(var month in income.GroupBy(row=>(row.AccountTime!.Value.Year,row.AccountTime.Value.Month)))
    {
        if(month.Count()<8)continue;
        Check(month.Select(row=>row.AccountTime!.Value.Date).Distinct().Count()>1,$"{req.Bank.Name} {month.Key} 的{month.Count()}笔收入仍集中在一天");
    }
}
static FlowAutoGenerationResult GenerateVisible(FlowAutoGenerationRequest req)
{
    var engine=new FlowAutoGenerator();var result=engine.Generate(req);
    if(DateDistributionTestOptions.Visible&&!FlowAutoGenerator.UsesReferencePriority(req.Config,result.Records)&&!ReferenceNaturalGenerationPolicy.AppliesTo(req.Bank,req.Config)
        &&Math.Abs(result.FinalBalance-result.OpeningBalance-req.Config.LastMoney)>FlowAutoGenerator.AcceptedFinalBalanceTolerance)
        result=engine.ApplyFinalBalanceCorrection(req,result);
    return result;
}
static void Industrial()
{
    var summaries=new List<object>();
    foreach(var seed in Enumerable.Range(1,100))
    {
        using var scope=new SeedScope(seed);var req=IndustrialRequest();var before=JsonSerializer.Serialize(req);FlowAutoGenerationResult result;
        try{result=GenerateVisible(req);}catch(Exception error){throw new Exception("兴业 seed="+seed,error);}
        CheckGenerated(req,result);Near(0,result.OpeningBalance);Check(before==JsonSerializer.Serialize(req),"修改用户配置");
        summaries.Add(new{Seed=seed,Rows=result.Records.Count,FirstDateIncome=result.Records.Count(row=>row.TradeMoney>0&&row.AccountTime?.Date==req.Config.StartTime.Date),
            result.IncomeTotal,result.ExpenseTotal,result.FinalBalance});
    }
    Directory.CreateDirectory("tmp/date-distribution-20261102");File.WriteAllText("tmp/date-distribution-20261102/industrial.json",JsonSerializer.Serialize(summaries));
}
static void AllBanksGenerated()
{
    foreach(var bank in Banks())foreach(var seed in new[]{16,72,1001,1003})
    {using var scope=new SeedScope(seed);var req=IndustrialRequest(bank);try{CheckGenerated(req,GenerateVisible(req));}catch(Exception error){throw new Exception(bank.Name+" seed="+seed,error);}}
}
sealed class SeedScope : IDisposable
{
    private readonly string? old=Environment.GetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED");
    public SeedScope(int seed)=>Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED",seed.ToString(CultureInfo.InvariantCulture));
    public void Dispose()=>Environment.SetEnvironmentVariable("SPEEDEMULATOR_FLOW_TEST_SEED",old);
}
static class DateDistributionTestOptions { public static bool Visible; }
