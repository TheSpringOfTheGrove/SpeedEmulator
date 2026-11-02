using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("仅个人工行按总进账模式启用", TestScope),
    ("截图配置按月尝试后筛选日期，条数动态且不强行补满", TestReported),
    ("末尾一天及周末节假日不重抽日期补次数", TestAttemptDates),
    ("计息及三种余额修正保留自然金额和条数", TestCorrections),
    ("凌晨支出先发生时补期初，金额日期保持", TestEarlyExpense),
    ("固定日期及固定整数保留，导入流水不动", TestFixedAndImported),
    ("只有固定日期项目时修正保留原金额", TestConstOnly),
    ("预算增大时金额随区间展开，没有固定小额上限", TestBudgetAdaptation),
    ("百元精度收入可接近总进账而不强凑非法整千", TestCoarseIncome),
    ("粗粒度支出采用可达值且不突破范围", TestCoarseExpense),
    ("零支出预算不添加可选消费", TestZeroExpense),
    ("计划支出小于月度项目时保留金额并自动补期初", TestImpossible),
    ("34类银行负余额统一补期初，普通流水金额日期编号不变", TestAutomaticOpeningAllBanks),
    ("34类银行实际生成固定支出早于收入时自动补初", TestFixedGenerationAllBanks),
    ("收支都是两位小数时尾数分散不能改变收支方向", TestDecimalDirections),
    ("小预算下月度支出仍均匀覆盖完整参照区间", TestUniformExpenseRange),
    ("自然生成结果通过界面校验，不再被旧余额目标压回", TestGenerationAcceptance),
    ("补初保留导入结息及利息税，后续结息承接真实导入金额", TestImportedInterest),
    ("大额进账按来源精度控制尾差，不能按百分比缩水", TestIncomePrecisionBudget),
    ("可选支出预算尾差由既有容量吸收，不拆成几百笔小额", TestOptionalExpenseTail),
    ("百万可选收入保留整个时间轴，不集中到首日", TestOptionalIncomeTimeline)
};
var failures=0;
foreach(var test in tests)
{
    try{test.Run();Console.WriteLine("PASS "+test.Name);}
    catch(Exception exception){failures++;Console.Error.WriteLine("FAIL "+test.Name+": "+exception);}
}
Console.WriteLine($"{tests.Length-failures}/{tests.Length} ICBC tests passed.");
return failures==0?0:1;

static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
static void Near(double expected,double actual,double tolerance=.009)=>Check(Math.Abs(expected-actual)<=tolerance+.000001,$"expected={expected}, actual={actual}");
static string Kind(FlowRecord r)=>r.ExtraFields.GetValueOrDefault("__GeneratedSourceKind", "");
static int Tail(FlowRecord r)=>(int)Math.Round(Math.Abs(r.TradeMoney??0)*100,MidpointRounding.AwayFromZero)%100;
static GenerateReferenceRule Rule(int index,bool income,double min,double max,int precision=0,int monthly=0)=>new()
{
    Index=index,IsCheck=true,IncomeAttribute=income?"收入":"支出",MinMoney=min,MaxMoney=max,FloutLength=precision,
    PercentMonth=monthly,StartDay=income?9:7,EndDay=income?17:22,TradeHoliday=false,TradeWeekend=false,ProductBrief="模拟项目"+index
};
static FlowAutoGenerationRequest Request(bool interest=false)
{
    var rules=new List<GenerateReferenceRule>{Rule(1,false,10000,60000,-2),Rule(2,true,10000,80000,-2)};
    foreach(var x in new[]{(10d,200d,0,3),(10d,1000d,1,3),(10d,3000d,0,3),(10d,500d,2,2),(10d,1000d,2,2),
        (10d,200d,2,3),(10d,100d,2,3),(10d,200d,0,3),(10d,500d,0,3),(10d,300d,0,2),(10d,200d,0,3),
        (10d,500d,2,3),(10d,500d,0,2),(10d,200d,2,2),(10d,100d,2,1),(100d,300d,-2,1),(200d,500d,-1,1),
        (10d,200d,2,1),(8d,100d,2,2),(10d,300d,2,3)})rules.Add(Rule(rules.Count+1,false,x.Item1,x.Item2,x.Item3,x.Item4));
    return Build(new FlowGenerationConfig{StartTime=new DateTime(2025,1,1),EndTime=new DateTime(2026,1,1),AllInMoney=30000,
        AllOutMoney=20000,LastMoney=10000,OpeningBalance=250000,MinInMoneyMonth1=2307.69,MaxInMoneyMonth1=2307.69,
        MinOutMoneyMonth1=1538.46,MaxOutMoneyMonth1=1538.46},rules,interest:interest);
}
static FlowAutoGenerationRequest Build(FlowGenerationConfig config,IReadOnlyList<GenerateReferenceRule> rules,
    IReadOnlyList<GenerateConstRule>? fixedItems=null,bool interest=false)=>new()
{
    Bank=new Bank{Id=4,Name="工行",Type=BankTypes.Personal},BankUser=new BankUser{Id=9004,BankId=4,AutoCalculateInterest=interest},
    Config=config,References=rules,ConstItems=fixedItems??[],InterestSetting=BankInterestSettingDefaults.CreateDefault(new Bank{Id=4,Name="工行",Type=BankTypes.Personal})
};
static IDisposable Seed(int seed)=>new EnvironmentScope("SPEEDEMULATOR_FLOW_TEST_SEED",seed.ToString(CultureInfo.InvariantCulture));
static void Validate(FlowAutoGenerationRequest request,FlowAutoGenerationResult result,bool precise=true,bool incomeBudget=true)
{
    var ordinaryIncome=result.Records.Where(r=>!FlowGeneratedRowKinds.IsSystemInterest(r)&&r.TradeMoney>0).Sum(r=>r.TradeMoney??0);
    if(incomeBudget)Check(ordinaryIncome>=request.Config.AllInMoney*.89-.009&&ordinaryIncome<=request.Config.AllInMoney+.99,"普通收入应接近配置总进账");
    Check(result.MinimumBalance>=-.009&&!result.RequiresOpeningBalanceCorrection,"自动补初后不能还有负余额");
    var balance=result.OpeningBalance;
    foreach(var row in result.Records)
    {
        balance=Math.Round(balance+(row.TradeMoney??0),2,MidpointRounding.AwayFromZero);Near(balance,row.Balance!.Value);
        if(FlowGeneratedRowKinds.IsSystemInterest(row)||row.IsDocumentImported)continue;
        var amount=Math.Abs(row.TradeMoney??0);
        if(Kind(row) is "Reference" or "Const")
        {
            var min=double.Parse(row.ExtraFields["__GeneratedAmountMin"],CultureInfo.InvariantCulture);
            var max=double.Parse(row.ExtraFields["__GeneratedAmountMax"],CultureInfo.InvariantCulture);
            var unit=double.Parse(row.ExtraFields["__GeneratedAmountUnit"],CultureInfo.InvariantCulture);
            Check(amount>=min-.000001&&amount<=max+.000001,"来源金额范围不可突破");Near(Math.Round(amount/unit),amount/unit,.000001);
            if(unit<1||unit>=10)Check((long)Math.Round(amount/unit)%10!=0,"有效金额位不能为零");
        }
        Check(Tail(row) is not (>=1 and <=9),"不能出现禁止的.01至.09");
    }
    Near(balance,result.FinalBalance);
    if(BankInterestPolicy.ShouldCalculate(request.Bank,request.BankUser))
    {
        var verified=result.Records.Select(row=>row.Clone()).ToList();
        var calculate=typeof(FlowAutoGenerator).GetMethod("RecalculateInterestRecords",BindingFlags.NonPublic|BindingFlags.Static)!;
        calculate.Invoke(null,[verified,request,result.OpeningBalance,request.Config.StartTime.Date,request.Config.EndTime.Date.AddDays(1).AddSeconds(-1),new Random(0x1CBC)]);
        var actual=result.Records.Where(FlowGeneratedRowKinds.IsSystemInterest).OrderBy(r=>r.AccountTime).Select(r=>r.TradeMoney).ToArray();
        var expected=verified.Where(FlowGeneratedRowKinds.IsSystemInterest).OrderBy(r=>r.AccountTime).Select(r=>r.TradeMoney).ToArray();
        Check(actual.SequenceEqual(expected),"最终收口和尾数分散后，结息必须与最终日余额重新计算一致");
    }
}
static void Distribution(FlowAutoGenerationResult result)
{
    var expense=result.Records.Where(r=>r.TradeMoney<0&&!FlowGeneratedRowKinds.IsSystemInterest(r)).ToArray();
    Check(expense.Length is >=300 and <=440,"日期筛选后条数应接近真诚的375条，而非补满598条");
    Check(expense.Count(r=>Math.Abs(r.TradeMoney!.Value)<=15)<expense.Length*.1,"不能再把支出压在15元以内");
    Check(expense.Count(r=>Math.Abs(r.TradeMoney!.Value)>100)>=expense.Length*.5,"全区间取值应有足够百元以上支出");
    Check(expense.Count(r=>Math.Abs(r.TradeMoney!.Value)>1000)>=8,"必须保留千元支出，接近真诚的宽区间分布");
    Check(expense.Average(r=>Math.Abs(r.TradeMoney!.Value)) is >200 and <400,"支出均值须接近真诚，不能再受约2万元预算压缩");
    Check(expense.Select(r=>r.TradeMoney).Distinct().Count()>150,"金额须具有变化，不能只使用少量固定值");
    foreach(var month in expense.GroupBy(r=>r.AccountTime!.Value.ToString("yyyy-MM")))Check(month.Count()<=46,"每月生成数不得超过尝试次数");
    var p2=expense.Where(r=>r.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision")=="2").ToArray();
    Check(p2.GroupBy(Tail).Max(group=>group.Count())<=Math.Ceiling(p2.Length/81d)+3,"不能转为某一个合法尾数的集中");
    foreach(var day in p2.GroupBy(r=>r.AccountTime!.Value.Date))Check(day.GroupBy(Tail).Max(group=>group.Count())<=3,"单日尾数不能扎堆");
}
static void TestScope()
{
    var request=Request();Check(IndustrialScope(request.Bank,request.Config),"个人工行启用");
    foreach(var name in new[]{"中行","建行","农行","微信","支付宝","兴业"})Check(!IndustrialScope(new Bank{Id=99,Name=name,Type=BankTypes.Personal},request.Config),"其他银行不能进入");
    Check(!IndustrialScope(new Bank{Id=4,Name="工行对公",Type=BankTypes.Corporate},request.Config),"对公排除");
    foreach(var mode in new[]{1,2}){var config=request.Config.Clone();config.SelectIndex=mode;Check(!IndustrialScope(request.Bank,config),"月分配模式不能改变");}
    static bool IndustrialScope(Bank bank,FlowGenerationConfig config)=>IcbcGenerationPolicy.AppliesTo(bank,config);
}
static void TestReported()
{
    var request=Request();var input=JsonSerializer.Serialize(request);var counts=new HashSet<int>();
    for(var seed=1001;seed<=1030;seed++)
    {
        using var scope=Seed(seed);var result=new FlowAutoGenerator().Generate(request);Validate(request,result);Distribution(result);
        counts.Add(result.Records.Count);
        foreach(var row in result.Records.Where(r=>Kind(r)=="Reference"))
            Check(IcbcGenerationPolicy.IsReferenceDateAllowed(row.AccountTime!.Value,request.References.Single(r=>r.Index.ToString()==row.ExtraFields["__GeneratedSourceIndex"])),"普通参照不能落在禁止交易日期");
        Check(result.MinimumBalance>=-.009,"可行配置应消除负余额");
    }
    Check(input==JsonSerializer.Serialize(request),"不可改写输入配置");
    Check(counts.Count>=8,"相同配置多次生成条数应自然变化");
}
static void TestAttemptDates()
{
    using var scope=Seed(1077);var request=Request();
    var result=new FlowAutoGenerator().Generate(request);
    Check(!result.Records.Any(r=>Kind(r)=="Reference"&&r.AccountTime!.Value.Date==new DateTime(2026,1,1)),"最后仅一天且为禁止节假日的月份不能补入46条");
    foreach(var source in result.Records.Where(r=>Kind(r)=="Reference").GroupBy(r=>r.ExtraFields["__GeneratedSourceIndex"]))
    {
        var rule=request.References.Single(r=>r.Index.ToString()==source.Key);
        if(rule.PercentMonth<=0)continue;
        foreach(var month in source.GroupBy(r=>r.AccountTime!.Value.ToString("yyyy-MM")))
            Check(month.Count()<=rule.PercentMonth,"被筛掉的尝试不能用其它日期补齐");
    }
    var permissive=Rule(99,false,1,2);permissive.TradeHoliday=true;permissive.TradeWeekend=true;
    Check(IcbcGenerationPolicy.IsReferenceDateAllowed(new DateTime(2025,1,8),permissive),"工行勾选允许周末和节假日不排除普通工作日");
}
static void TestCorrections()
{
    var request=Request(true);var generator=new FlowAutoGenerator();
    for(var seed=1101;seed<=1105;seed++)
    {
        using var scope=Seed(seed);var source=generator.Generate(request);Validate(request,source);Distribution(source);
        Check(source.Records.Any(FlowGeneratedRowKinds.IsInterest),"真实结息不能丢失");
        foreach(var corrected in new[]{generator.ApplyNegativeBalanceCorrection(request,source),generator.ApplyFinalBalanceCorrection(request,source),generator.ApplyOpeningBalanceCorrection(request,source,source.OpeningBalance+5000)})
        {
            Validate(request,corrected);Distribution(corrected);Check(corrected.Records.Count==source.Records.Count,"修正不增删普通流水");
            Check(OrdinarySnapshot(source)==OrdinarySnapshot(corrected),"补初及余额修正不能改金额、日期或编号");
        }
    }
}
static void TestEarlyExpense()
{
    using var scope=Seed(1212);var request=Request();var generator=new FlowAutoGenerator();var source=generator.Generate(request);
    var row=source.Records.First(r=>Kind(r)=="Reference"&&r.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex")=="3");
    row.AccountTime=request.Config.StartTime.Date.AddHours(7);
    var original=row.AccountTime;var result=generator.ApplyNegativeBalanceCorrection(request,source);Validate(request,result);Distribution(result);
    Check(result.Records.Single(r=>Kind(r)=="Reference"&&r.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex")=="3"&&r.AccountTime==original) is not null,"负余额修正必须保留早支出原日期");
}
static void TestFixedAndImported()
{
    using var scope=Seed(1301);
    var config=new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,3,31),AllInMoney=3000,LastMoney=1000,OpeningBalance=5000};
    var request=Build(config,[Rule(1,false,100,100,2,1),Rule(2,false,5,80,2)],
        [new GenerateConstRule{Index=1,IsCheck=true,IncomeAttribute="收入",MinMoney=1000,MaxMoney=1000,FloutLength=2,FixDay="15",ReCnt="1",StartDay=9,EndDay=17}]);
    request.References[0].TradeHoliday=true;request.References[0].TradeWeekend=true;
    var generator=new FlowAutoGenerator();var result=generator.Generate(request);Validate(request,result);
    Check(result.Records.Where(r=>Kind(r)=="Const").All(r=>r.AccountTime!.Value.Day==15&&r.TradeMoney==1000),"固定工资日期金额不能变");
    Check(result.Records.Count(r=>Kind(r)=="Reference"&&r.ExtraFields.GetValueOrDefault("__GeneratedSourceIndex")=="1"&&r.TradeMoney==-100)==3,"固定整数优先于配置小数位");
    var imported=new FlowRecord{AccountTime=new DateTime(2026,3,31,22,0,0),TradeMoney=-18.53,IsDocumentImported=true,Remark="synthetic-import"};
    result.Records.Add(imported);var corrected=generator.ApplyFinalBalanceCorrection(request,result);Validate(request,corrected);
    var preserved=corrected.Records.Single(r=>r.IsDocumentImported);Near(-18.53,preserved.TradeMoney!.Value);Check(preserved.AccountTime==imported.AccountTime,"导入日期不能移动");
}
static void TestCoarseIncome()
{
    for(var seed=1401;seed<=1410;seed++)
    {
        using var scope=Seed(seed);var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,1,31),AllInMoney=30000,LastMoney=30000},[Rule(1,true,10000,80000,-2)]);
        var result=new FlowAutoGenerator().Generate(request);Validate(request,result);Check(result.Records.Count(r=>r.TradeMoney>0)>=1,"应生成合法百元精度收入");
    }
}
static void TestConstOnly()
{
    using var scope=Seed(1351);
    var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,3,31),AllInMoney=3000,LastMoney=2700,OpeningBalance=5000},[],
        [new GenerateConstRule{Index=1,IsCheck=true,IncomeAttribute="收入",MinMoney=500,MaxMoney=1500,FloutLength=0,FixDay="15",ReCnt="1",StartDay=9,EndDay=17},
            new GenerateConstRule{Index=2,IsCheck=true,IncomeAttribute="支出",MinMoney=100,MaxMoney=100,FloutLength=2,FixDay="20",ReCnt="1",StartDay=9,EndDay=17}],interest:true);
    var generator=new FlowAutoGenerator();var source=generator.Generate(request);Validate(request,source,precise:false,incomeBudget:false);
    foreach(var corrected in new[]{generator.ApplyFinalBalanceCorrection(request,source),generator.ApplyNegativeBalanceCorrection(request,source),generator.ApplyOpeningBalanceCorrection(request,source,6000)})
    {
        Validate(request,corrected,precise:false,incomeBudget:false);
        Check(corrected.Records.Where(r=>Kind(r)=="Const").Select(r=>r.TradeMoney).SequenceEqual(source.Records.Where(r=>Kind(r)=="Const").Select(r=>r.TradeMoney)),"修正不能修改既有固定日期金额");
    }
}
static void TestBudgetAdaptation()
{
    using var scope=Seed(1371);var request=Request();request.Config.AllInMoney=200000;request.Config.AllOutMoney=190000;
    var result=new FlowAutoGenerator().Generate(request);Validate(request,result);
    Check(result.Records.Count(r=>r.TradeMoney < -1000)>=10,"预算充足时应自然覆盖千元支出，不能把区间限制死在几百元");
}
static void TestCoarseExpense()
{
    using var scope=Seed(1501);var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,1,31),AllInMoney=30000,LastMoney=10000},[Rule(1,true,10000,80000,-2),Rule(2,false,10000,60000,-2)]);
    var result=new FlowAutoGenerator().Generate(request);Validate(request,result,precise:false);
}
static void TestZeroExpense()
{
    using var scope=Seed(1601);var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,1,31),AllInMoney=1000,LastMoney=1000},[Rule(1,true,100,200),Rule(2,false,5,80,2)]);
    var result=new FlowAutoGenerator().Generate(request);Validate(request,result);Check(!result.Records.Any(r=>r.TradeMoney<0),"零支出不强加消费");
}
static void TestImpossible()
{
    using var scope=Seed(1701);var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,1,31),AllInMoney=1000,LastMoney=900},[Rule(1,true,100,200),Rule(2,false,1200,1600,0,2)]);
    request.References[1].TradeHoliday=true;request.References[1].TradeWeekend=true;
    var result=new FlowAutoGenerator().Generate(request);Validate(request,result);
    Check(FlowAutoGenerator.UsesReferencePriority(request.Config,result.Records)
        && result.Records.Count(row=>row.TradeMoney<0)==2 && result.ExpenseTotal is >=2400 and <=3200
        && result.OpeningBalance>0,"参照优先时应保留两次合法月支出，仅为真实缺口补期初");
}

static string OrdinarySnapshot(FlowAutoGenerationResult result)=>JsonSerializer.Serialize(result.Records.Where(r=>!FlowGeneratedRowKinds.IsSystemInterest(r))
    .Select(r=>new{r.Id,r.AccountTime,r.TradeMoney,r.SerialNum,r.VoucherNum,r.MerchantName,r.IsDocumentImported}));
static void TestAutomaticOpeningAllBanks()
{
    var count=0;
    foreach(var field in new[]{"LegacyPersonalBankIds","LegacyCorporateBankIds"})
    {
        var catalog=(IReadOnlyDictionary<string,long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field,BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        foreach(var item in catalog)
        {
            var bank=new Bank{Id=item.Value,Name=item.Key,Type=field.Contains("Personal")?BankTypes.Personal:BankTypes.Corporate};
            var request=new FlowAutoGenerationRequest{Bank=bank,BankUser=new BankUser{Id=9004,BankId=bank.Id,AutoCalculateInterest=false},
                Config=new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,1,31)},References=[],ConstItems=[]};
            var rows=new List<FlowRecord>{new(){Id=1,BankId=bank.Id,BankUserId=9004,AccountTime=new DateTime(2026,1,2),TradeMoney=-250.33,SerialNum="keep-a"},
                new(){Id=2,BankId=bank.Id,BankUserId=9004,AccountTime=new DateTime(2026,1,15),TradeMoney=100,SerialNum="keep-b"},
                new(){Id=3,BankId=bank.Id,BankUserId=9004,AccountTime=new DateTime(2026,1,16),TradeMoney=-75.23,SerialNum="imported",IsDocumentImported=true}};
            var source=new FlowAutoGenerationResult{Records=rows,OpeningBalance=0,IncomeTotal=100,ExpenseTotal=325.56,FinalBalance=-225.56,MinimumBalance=-250.33,RequiresOpeningBalanceCorrection=true,RequiredOpeningBalance=250.33};
            var snapshot=JsonSerializer.Serialize(source);var generator=new FlowAutoGenerator();
            var result=generator.ApplyNegativeBalanceCorrection(request,source);
            Near(250.33,result.OpeningBalance);Near(0,result.MinimumBalance);Near(24.77,result.FinalBalance);
            Check(OrdinarySnapshot(source)==OrdinarySnapshot(result),"普通金额、日期、导入行和编号不能被补初逻辑改写: "+bank.Name);
            var again=generator.ApplyNegativeBalanceCorrection(request,result);Near(result.OpeningBalance,again.OpeningBalance);
            var higher=generator.ApplyOpeningBalanceCorrection(request,result,500);Near(500,higher.OpeningBalance);
            Check(OrdinarySnapshot(source)==OrdinarySnapshot(higher),"显式改期初也不能改写普通流水");
            Check(snapshot==JsonSerializer.Serialize(source),"不可污染调用方原始数据");count++;
        }
    }
    Check(count==34,"必须覆盖个人和对公全部34类银行");
}
static void TestFixedGenerationAllBanks()
{
    var count=0;using var scope=Seed(1801);
    foreach(var field in new[]{"LegacyPersonalBankIds","LegacyCorporateBankIds"})
    {
        var catalog=(IReadOnlyDictionary<string,long>)typeof(SpeedEmulator.ViewModels.MainViewModel).GetField(field,BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        foreach(var item in catalog)
        {
            var bank=new Bank{Id=item.Value,Name=item.Key,Type=field.Contains("Personal")?BankTypes.Personal:BankTypes.Corporate};
            foreach(var method in new[]{"ConfigureFlowGenerationColumns","ConfigureFlowRecordColumns"})
                typeof(SpeedEmulator.ViewModels.MainViewModel).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[bank]);
            var request=new FlowAutoGenerationRequest{Bank=bank,BankUser=new BankUser{Id=9004,BankId=bank.Id,AutoCalculateInterest=false},
                Config=new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,1,31),AllInMoney=1000,LastMoney=500},References=[],
                ConstItems=[new GenerateConstRule{Index=1,IsCheck=true,IncomeAttribute="收入",MinMoney=1000,MaxMoney=1000,FixDay="15",ReCnt="1",TradeHoliday=true,TradeWeekend=true,FloutLength=0},
                    new GenerateConstRule{Index=2,IsCheck=true,IncomeAttribute="支出",MinMoney=500,MaxMoney=500,FixDay="2",ReCnt="1",TradeHoliday=true,TradeWeekend=true,FloutLength=0}]};
            var input=JsonSerializer.Serialize(request);var result=new FlowAutoGenerator().Generate(request);
            Check(result.MinimumBalance>=-.009,"实际生成仍有负余额: "+bank.Name);
            Check(result.Records.Count==2,"补初不增删固定流水: "+bank.Name);
            Check(result.Records.Any(r=>r.TradeMoney==-500&&r.AccountTime!.Value.Day==2),"固定支出不可改动: "+bank.Name);
            Check(result.Records.Any(r=>r.TradeMoney==1000&&r.AccountTime!.Value.Day==15),"固定收入不可改动: "+bank.Name);
            Near(500,result.OpeningBalance);Check(input==JsonSerializer.Serialize(request),"生成不可改写调用方输入");count++;
        }
    }
    Check(count==34,"实际生成应覆盖34类银行");
}
static void TestDecimalDirections()
{
    using var scope=Seed(1901);
    var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,3,31),AllInMoney=10000,LastMoney=1000},
        [Rule(1,true,50,200,2),Rule(2,false,10,100,2)]);
    var result=new FlowAutoGenerator().Generate(request);Validate(request,result);
    foreach(var row in result.Records.Where(r=>Kind(r)=="Reference"))
        Check(row.ExtraFields["__GeneratedSourceIndex"]=="1"?row.TradeMoney>0:row.TradeMoney<0,"尾数分散不得翻转参照原始收支方向");
    Check(result.IncomeTotal>8900&&result.ExpenseTotal>7900,"两位小数的收入和支出必须分别保留");
}
static void TestUniformExpenseRange()
{
    using var scope=Seed(2001);
    var expense=Rule(1,false,5,300,2,200);expense.TradeHoliday=true;expense.TradeWeekend=true;
    var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,12,31),AllInMoney=1000,LastMoney=500,OpeningBalance=1000000},
        [expense,Rule(2,true,1000,1000)]);
    var result=new FlowAutoGenerator().Generate(request);Validate(request,result);
    var values=result.Records.Where(r=>r.TradeMoney<0).Select(r=>Math.Abs(r.TradeMoney!.Value)).ToArray();
    Check(values.Length==2400,"全部日期允许时应保留每月200次尝试");
    Check(values.Average() is >145 and <160,"金额均值应接近完整5至300元区间中点");
    var bins=values.GroupBy(value=>Math.Min(9,(int)((value-5)/29.5))).ToDictionary(g=>g.Key,g=>g.Count());
    Check(bins.Count==10&&bins.Values.All(count=>count is >160 and <320),"完整区间的十个等宽分段应均匀覆盖");
    Check(values.Count(value=>value<10)>10,"低于10元的合法金额也应自然出现");
    Check(result.ExpenseTotal>300000&&result.OpeningBalance==1000000,"资金足够时完整均匀分布保留且期初不改");
}
static void TestGenerationAcceptance()
{
    using var scope=Seed(2101);var request=Request(true);var result=new FlowAutoGenerator().Generate(request);
    var type=typeof(SpeedEmulator.ViewModels.FlowGenerationViewModel);
    var vm=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
    type.GetField("<Bank>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm,request.Bank);
    type.GetField("config",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm,request.Config.Clone());
    object?[] args=[result,request.BankUser,request.InterestSetting,null];
    var accepted=(bool)type.GetMethod("IsGenerationResultAccepted",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,args)!;
    Check(accepted,"自然生成应通过界面校验: "+args[3]);
    Check(Math.Abs(result.FinalBalance-result.OpeningBalance-request.Config.LastMoney)>1000,"该用例须实际覆盖旧目标与自然余额不兼容的情况");
}
static void TestImportedInterest()
{
    var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,6,30)},[],interest:true);
    request.InterestSetting!.RatePercent="365";
    List<FlowRecord> Rows(double interest)=>[
        new(){Id=1,AccountTime=new DateTime(2026,1,2),TradeMoney=-5000,IsDocumentImported=true,SerialNum="expense"},
        new(){Id=2,AccountTime=new DateTime(2026,1,15),TradeMoney=1000,IsDocumentImported=true,SerialNum="income"},
        new(){Id=3,AccountTime=new DateTime(2026,3,20,9,23,0),TradeMoney=interest,IsDocumentImported=true,GeneratedRowKind="Interest",Remark="原始结息",SerialNum="interest"},
        new(){Id=4,AccountTime=new DateTime(2026,3,20,9,30,0),TradeMoney=-.01,IsDocumentImported=true,GeneratedRowKind="InterestTax",Remark="原始利息税",SerialNum="tax"}];
    var generator=new FlowAutoGenerator();var original=Rows(12.03);
    var result=generator.ApplyAutomaticOpeningBalance(request,original,0);
    foreach(var row in original)
    {
        var saved=result.Records.Single(r=>r.Id==row.Id);
        Check(saved.AccountTime==row.AccountTime&&saved.TradeMoney==row.TradeMoney&&saved.SerialNum==row.SerialNum&&saved.Remark==row.Remark
            &&saved.GeneratedRowKind==row.GeneratedRowKind,"不能改动或删除导入的结息、利息税和普通流水");
    }
    Check(result.OpeningBalance>0&&result.MinimumBalance>=0,"含导入结息的账单也应补初");
    var higher=generator.ApplyAutomaticOpeningBalance(request,Rows(112.03),0);
    double June(FlowAutoGenerationResult value)=>value.Records.Single(r=>!r.IsDocumentImported&&FlowGeneratedRowKinds.IsInterest(r)&&r.AccountTime!.Value.Month==6).TradeMoney!.Value;
    Near(93,June(higher)-June(result));
    Check(result.Records.Count(r=>FlowGeneratedRowKinds.IsInterest(r)&&r.AccountTime!.Value.Month==3)==1,"导入结息实际为20日时，不能再按默认21日重复生成");
}
static void TestIncomePrecisionBudget()
{
    foreach(var total in new[]{30000d,1000000d})
    {
        using var scope=Seed(2201);var request=Request();request.Config.AllInMoney=total;
        var result=new FlowAutoGenerator().Generate(request);Validate(request,result);
        var ordinary=result.Records.Where(r=>!FlowGeneratedRowKinds.IsSystemInterest(r)&&r.TradeMoney>0).Sum(r=>r.TradeMoney??0);
        Check(ordinary>=total-1200&&ordinary<=total,"百元精度的尾差必须由精度控制，百万进账也不能缩水几万元");
    }
}
static void TestOptionalExpenseTail()
{
    using var scope=Seed(2301);
    var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,12,31),AllInMoney=20000,LastMoney=10000},
        [Rule(1,true,20000,20000),Rule(2,false,1000,10000),Rule(3,false,1,2)]);
    var result=new FlowAutoGenerator().Generate(request);Validate(request,result);
    Check(result.Records.Count(r=>r.TradeMoney<0)<30,"剩余预算不足大额下限时不能只剩小额池、拆出几百笔");
    Check(result.ExpenseTotal>=9900&&result.ExpenseTotal<=10000,"既有可选大额范围仍能合法吸收尾差");
}
static void TestOptionalIncomeTimeline()
{
    using var scope=Seed(2401);
    var request=Build(new FlowGenerationConfig{StartTime=new DateTime(2026,1,1),EndTime=new DateTime(2026,12,31),AllInMoney=1000000,LastMoney=1000000},
        [Rule(1,true,1000,15000,-2)]);
    var result=new FlowAutoGenerator().Generate(request);Validate(request,result);
    var income=result.Records.Where(r=>r.TradeMoney>0).ToArray();
    Check(income.Length>60&&income.Select(r=>r.AccountTime!.Value.Date).Distinct().Count()>30,"收入应保留全时间轴的自然分布");
    Check(income.GroupBy(r=>r.AccountTime!.Value.Date).Max(g=>g.Count())<10,"不能把所有可选收入挤到首日");
}
sealed class EnvironmentScope(string key,string value):IDisposable
{
    private readonly string? previous=Environment.GetEnvironmentVariable(key);
    private readonly bool initialized=Set(key,value);
    private static bool Set(string key,string value){Environment.SetEnvironmentVariable(key,value);return true;}
    public void Dispose()=>Environment.SetEnvironmentVariable(key,previous);
}
