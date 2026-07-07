using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed class ZhenchengFlowGenerationAdapter
{
    private static readonly object BridgeLock = new();
    private static VendorBridge? bridge;

    public bool TryGenerate(
        FlowAutoGenerationRequest request,
        out FlowAutoGenerationResult result,
        out string message)
    {
        result = null!;
        message = string.Empty;

        var references = request.References
            .Where(item => item.IsCheck)
            .Select(item => item.Clone())
            .ToList();
        var constItems = request.ConstItems
            .Where(item => item.IsCheck)
            .Select(item => item.Clone())
            .ToList();

        if (references.Count == 0 && constItems.Count == 0)
        {
            message = "真诚实验适配器未运行：没有已勾选的参照明细或固定日期增加项目。";
            return false;
        }

        for (var index = 0; index < references.Count; index++)
        {
            references[index].Index = index;
        }

        for (var index = 0; index < constItems.Count; index++)
        {
            constItems[index].Index = index;
        }

        try
        {
            var activeBridge = GetBridge();
            var records = activeBridge.GenerateRecords(request, references, constItems, out var effectiveRequest);
            if (records.Count == 0)
            {
                message = "真诚完整黑盒调用未返回流水，已停止生成。";
                return false;
            }

            result = BuildResult(effectiveRequest, records);
            message = $"已使用真诚完整黑盒调用生成 {result.Records.Count} 条流水。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"真诚完整黑盒调用失败，已停止生成：{Unwrap(ex).Message}";
            return false;
        }
    }

    private static VendorBridge GetBridge()
    {
        lock (BridgeLock)
        {
            bridge ??= VendorBridge.Load();
            return bridge;
        }
    }

    private static DateTime NormalizeEndDate(DateTime value)
    {
        return value.TimeOfDay == TimeSpan.Zero
            ? value.Date.AddDays(1).AddTicks(-1)
            : value;
    }

    private static double RoundMoney(double value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private static FlowAutoGenerationResult BuildResult(FlowAutoGenerationRequest request, List<FlowRecord> records)
    {
        var openingBalance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
        ApplyBalances(records, openingBalance);
        var incomeTotal = RoundMoney(records.Where(item => item.TradeMoney > 0).Sum(item => item.TradeMoney ?? 0));
        var expenseTotal = RoundMoney(records.Where(item => item.TradeMoney < 0).Sum(item => Math.Abs(item.TradeMoney ?? 0)));
        var finalBalance = records.LastOrDefault()?.Balance ?? openingBalance;
        var minimumBalance = records.Select(item => item.Balance ?? openingBalance).Append(openingBalance).Min();

        return new FlowAutoGenerationResult
        {
            Records = records,
            OpeningBalance = openingBalance,
            IncomeTotal = incomeTotal,
            ExpenseTotal = expenseTotal,
            FinalBalance = RoundMoney(finalBalance),
            MinimumBalance = RoundMoney(minimumBalance),
            RequiresOpeningBalanceCorrection = minimumBalance < -0.009d,
            RequiredOpeningBalance = minimumBalance < -0.009d
                ? RoundMoney(openingBalance - minimumBalance)
                : openingBalance
        };
    }

    private static void ApplyBalances(List<FlowRecord> records, double openingBalance)
    {
        records.Sort((left, right) =>
        {
            var result = Nullable.Compare(left.AccountTime, right.AccountTime);
            return result != 0 ? result : string.CompareOrdinal(left.SerialNum, right.SerialNum);
        });

        var balance = RoundMoney(openingBalance);
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var amount = RoundMoney(record.TradeMoney ?? 0);
            balance = RoundMoney(balance + amount);
            record.Balance = balance;
            record.BalanceAmount = balance;

            record.Index = index + 1;
            record.TradeMoney = amount;
            record.IncomeAttribute = amount >= 0 ? "收入" : "支出";
            record.CreditAmount = amount > 0 ? amount : null;
            record.DebitAmount = amount < 0 ? Math.Abs(amount) : null;
            record.IncomeFlag = amount >= 0 ? "C" : "D";
        }
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: not null } target)
        {
            ex = target.InnerException!;
        }

        return ex;
    }

    private sealed record VendorPlanItem(
        DateTime AccountTime,
        double Amount,
        int SourceKind,
        int SourceIndex,
        bool MoveFlag);

    private sealed record PlanOrderEntry(int Index, VendorPlanItem Item);

    private sealed record SameDirectionRun(int Start, int End, int Sign)
    {
        public int Length => End - Start + 1;
    }

    private readonly record struct PlanRemovalCandidate(VendorPlanItem Item, int Index, bool IsFixedIncome);

    private sealed class VendorBridge
    {
        private const string VendorInterestMarkerColumnName = "__CodexInterestProductBrief";
        private const string AmountUnitField = "__GeneratedAmountUnit";
        private const string SystemRowKindField = "__GeneratedSystemRowKind";
        private const string InterestRowKind = "Interest";
        private const string InterestText = "\u7ed3\u606f";
        private const string InterestTaxText = "\u5229\u606f\u7a0e";
        private const string PersonalCurrentInterestRemark = "\u4e2a\u4eba\u6d3b\u671f\u7ed3\u606f";
        private const string TransferText = "\u8f6c\u8d26";
        private const string CashText = "\u949e";
        private const string CurrentDepositText = "\u6d3b\u671f";
        private const string IncomeText = "\u6536\u5165";
        private const string IcbcShortName = "\u5de5\u884c";
        private const string IcbcFullName = "\u5de5\u5546";
        private const string ExternalSystemFlowColumnName = "\u5916\u90e8\u7cfb\u7edf\u6d41\u6c34";
        private static readonly string[] ConsumerExpenseKeywords =
        [
            "\u6d88\u8d39",
            "\u652f\u4ed8",
            "\u5feb\u6377",
            "\u65e0\u5361",
            "\u8d22\u4ed8\u901a",
            "\u652f\u4ed8\u5b9d",
            "\u5fae\u4fe1",
            "\u4e91\u95ea\u4ed8",
            "\u94f6\u8054",
            "\u5237\u5361",
            "\u626b\u7801",
            "\u5546\u6237",
            "\u4eac\u4e1c",
            "\u7f8e\u56e2",
            "\u62fc\u591a\u591a",
            "\u591a\u591a",
            "\u6296\u97f3",
            "\u6dd8\u5b9d",
            "\u5929\u732b",
            "\u997f\u4e86\u4e48",
            "\u751f\u6d3b\u7f34\u8d39",
            "\u624b\u673a\u5145\u503c",
            "POS"
        ];
        private static readonly string[] NonConsumerExpenseKeywords =
        [
            "\u7ed3\u606f",
            "\u5229\u606f",
            "\u5229\u606f\u7a0e",
            "\u5de5\u8d44",
            "\u4ed6\u884c\u6c47\u5165",
            "\u6c47\u5165",
            "\u8f6c\u5165",
            "\u6536\u6b3e",
            "\u8f6c\u8d26",
            "\u8f6c\u652f",
            "\u8f6c\u5b58",
            "\u63d0\u73b0",
            "\u53d6\u73b0",
            "\u5b58\u6b3e",
            "\u67dc\u9762",
            "\u51b2\u6b63",
            "\u51b2\u9500",
            "\u8d37\u6b3e",
            "\u8fd8\u6b3e"
        ];
        private static readonly string[] PaymentLikeIncomeKeywords =
        [
            "代付",
            "支付",
            "付款",
            "消费",
            "红包",
            "零钱提现",
            "财付通",
            "支付宝",
            "微信"
        ];
        private static readonly string[] AgriculturalUaPostscriptKeywords =
        [
            "财付通",
            "微信",
            "微信支付",
            "扫二维码",
            "二维码付款",
            "支付宝",
            "多多支付",
            "拼多多",
            "手机充值",
            "特约商户"
        ];
        private const int MaxSparseIncomeSplitCount = 8;

        private readonly Type referenceType;
        private readonly Type constType;
        private readonly Type bankType;
        private readonly Type bankUserType;
        private readonly Type flowType;
        private readonly Type columnType;
        private readonly Type computeType;
        private readonly Type monthGenerateType;
        private readonly Type flowListType;
        private readonly MethodInfo generateMethod;
        private readonly MethodInfo? processInterestMethod;
        private readonly Type? bankRateConfigType;
        private readonly MethodInfo? getBankRateConfigMethod;
        private readonly MethodInfo? saveBankRateConfigMethod;
        private readonly IReadOnlyList<MethodInfo> vendorClientFactoryMethods;
        private readonly MethodInfo? createVendorLogNumberMethod;
        private readonly MethodInfo? createVendorRemarkMethod;
        private readonly Type? bankRateType;
        private readonly FieldInfo? globalBankRateListField;
        private readonly PlanItemReader planItemReader;

        private VendorBridge(
            Type referenceType,
            Type constType,
            Type bankType,
            Type bankUserType,
            Type flowType,
            Type columnType,
            Type computeType,
            Type monthGenerateType,
            MethodInfo generateMethod,
            MethodInfo? processInterestMethod,
            Type? bankRateConfigType,
            MethodInfo? getBankRateConfigMethod,
            MethodInfo? saveBankRateConfigMethod,
            IReadOnlyList<MethodInfo> vendorClientFactoryMethods,
            MethodInfo? createVendorLogNumberMethod,
            MethodInfo? createVendorRemarkMethod,
            Type? bankRateType,
            FieldInfo? globalBankRateListField,
            Type planItemType)
        {
            this.referenceType = referenceType;
            this.constType = constType;
            this.bankType = bankType;
            this.bankUserType = bankUserType;
            this.flowType = flowType;
            this.columnType = columnType;
            this.computeType = computeType;
            this.monthGenerateType = monthGenerateType;
            flowListType = typeof(List<>).MakeGenericType(flowType);
            this.generateMethod = generateMethod;
            this.processInterestMethod = processInterestMethod;
            this.bankRateConfigType = bankRateConfigType;
            this.getBankRateConfigMethod = getBankRateConfigMethod;
            this.saveBankRateConfigMethod = saveBankRateConfigMethod;
            this.vendorClientFactoryMethods = vendorClientFactoryMethods;
            this.createVendorLogNumberMethod = createVendorLogNumberMethod;
            this.createVendorRemarkMethod = createVendorRemarkMethod;
            this.bankRateType = bankRateType;
            this.globalBankRateListField = globalBankRateListField;
            planItemReader = new PlanItemReader(planItemType);
        }

        public static VendorBridge Load()
        {
            var vendorDir = ZhenchengRuntimeLocator.Resolve()
                ?? throw new DirectoryNotFoundException("未找到真诚运行目录。");
            var mainDll = Path.Combine(vendorDir, ZhenchengRuntimeLocator.MainDllName);
            var loadContext = new VendorLoadContext(mainDll, vendorDir);
            var assembly = loadContext.LoadFromAssemblyPath(mainDll);

            var referenceType = GetRequiredType(assembly, "MainEntry.entity.GenerateReference");
            var constType = GetRequiredType(assembly, "MainEntry.entity.GenerateConst");
            var bankType = GetRequiredType(assembly, "MainEntry.entity.Bank");
            var bankUserType = GetRequiredType(assembly, "MainEntry.entity.BankUser");
            var flowType = GetRequiredType(assembly, "MainEntry.entity.GenerateFlowRecord");
            var columnType = GetRequiredType(assembly, "MainEntry.entity.Column");
            var computeType = GetRequiredType(assembly, "MainEntry.entity.ComputeEntity");
            var monthGenerateType = GetRequiredType(assembly, "MainEntry.entity.condition.MonthGenerate");
            var bankRateConfigType = assembly.GetType("MainEntry.entity.BankRateConfig", throwOnError: false);
            var bankRateType = assembly.GetType("MainEntry.entity.BankRate", throwOnError: false);
            var globalStateType = GetRequiredType(assembly, "_0008_001A_0018");
            var generatorType = GetRequiredType(assembly, "_0003_001A_0015");
            var flowRecordHelperType = GetRequiredType(assembly, "_0008_001A_0005");
            var planItemType = GetRequiredType(assembly, "_0002_001A_0015");
            var flowListType = typeof(List<>).MakeGenericType(flowType);

            var generateMethod = generatorType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                {
                    if (!IsMetadataName(method.Name, "_0008"))
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 3
                        && parameters[0].ParameterType == typeof(List<>).MakeGenericType(referenceType)
                        && parameters[1].ParameterType == typeof(List<>).MakeGenericType(constType)
                        && parameters[2].ParameterType == computeType;
                })
                ?? throw new MissingMethodException(generatorType.FullName, "_0008(List<GenerateReference>, List<GenerateConst>, ComputeEntity)");

            var processInterestMethod = flowType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "ProcessIntestData", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 3
                        && parameters[0].ParameterType.IsByRef
                        && parameters[0].ParameterType.GetElementType() == flowListType
                        && parameters[1].ParameterType == bankType
                        && parameters[2].ParameterType == bankUserType;
                });

            var allTypes = SafeGetTypes(assembly).ToList();
            List<MethodInfo> bankRateConfigMethods = bankRateConfigType is null
                ? []
                : allTypes
                    .SelectMany(type => SafeMethods(type))
                    .Where(method => method.IsStatic)
                    .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == bankRateConfigType)
                        || method.ReturnType == bankRateConfigType)
                    .ToList();
            var getBankRateConfigMethod = bankRateConfigType is null
                ? null
                : bankRateConfigMethods.FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1
                        && parameters[0].ParameterType == typeof(long)
                        && method.ReturnType == bankRateConfigType;
                });
            var saveBankRateConfigMethod = bankRateConfigType is null
                ? null
                : bankRateConfigMethods.FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1
                        && parameters[0].ParameterType == bankRateConfigType
                        && method.ReturnType == bankRateConfigType;
                });
            var vendorClientFactoryMethods = allTypes
                .SelectMany(type => SafeMethods(type))
                .Where(method => method.IsStatic
                    && method.GetParameters().Length == 0
                    && string.Equals(method.ReturnType.FullName, "SqlSugar.SqlSugarClient", StringComparison.Ordinal))
                .ToList();
            var flowRecordHelperMethods = SafeMethods(flowRecordHelperType)
                .Where(method => method.IsStatic)
                .ToList();
            var createVendorRemarkMethod = FindVendorFlowStringMethod(flowRecordHelperMethods, flowType, "_0002");
            var createVendorLogNumberMethod = FindVendorFlowStringMethod(flowRecordHelperMethods, flowType, "_0008");
            var globalBankRateListField = globalStateType
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(field => bankRateType is not null
                    && field.FieldType.IsGenericType
                    && field.FieldType.GetGenericTypeDefinition() == typeof(List<>)
                    && field.FieldType.GetGenericArguments()[0] == bankRateType);

            return new VendorBridge(
                referenceType,
                constType,
                bankType,
                bankUserType,
                flowType,
                columnType,
                computeType,
                monthGenerateType,
                generateMethod,
                processInterestMethod,
                bankRateConfigType,
                getBankRateConfigMethod,
                saveBankRateConfigMethod,
                vendorClientFactoryMethods,
                createVendorLogNumberMethod,
                createVendorRemarkMethod,
                bankRateType,
                globalBankRateListField,
                planItemType);
        }

        public List<FlowRecord> GenerateRecords(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            out FlowAutoGenerationRequest effectiveRequest)
        {
            var budgetedRequest = CreateBudgetedGenerationRequest(request);
            effectiveRequest = budgetedRequest;
            var vendorReferences = CreateVendorRuleList(referenceType, references);
            var vendorConstItems = CreateVendorRuleList(constType, constItems);
            var computeEntity = CreateComputeEntity(budgetedRequest);
            var generated = generateMethod.Invoke(null, [vendorReferences, vendorConstItems, computeEntity]);
            var planItems = generated is IEnumerable enumerable
                ? enumerable.Cast<object>().Select(planItemReader.Read).ToList()
                : [];
            if (planItems.Count == 0)
            {
                return [];
            }

            planItems = NormalizePlanItemAmountsByRule(references, constItems, planItems);
            planItems = EnsureRequiredReferencePlanItems(budgetedRequest, references, planItems);
            planItems = DropReferenceIncomeWhenFixedIncomeCoversTarget(budgetedRequest, references, constItems, planItems);

            planItems = ExpandSparseIncomePlanItems(budgetedRequest, references, planItems);
            planItems = PreferNonPaymentIncomePlanItems(budgetedRequest, references, planItems);
            planItems = NormalizePlanItemAmountsByRule(references, constItems, planItems);
            planItems = NormalizePlanItemTotals(budgetedRequest, references, constItems, planItems);
            planItems = PreferNonPaymentIncomePlanItems(budgetedRequest, references, planItems);
            planItems = NormalizePlanItemAmountsByRule(references, constItems, planItems);
            planItems = EnsureDailyPlanItemCoverage(budgetedRequest, references, constItems, planItems);
            planItems = NormalizePlanItemAmountsByRule(references, constItems, planItems);
            planItems = StabilizePlanItemBalances(budgetedRequest, references, constItems, planItems);

            var vendorBank = CreateVendorBank(budgetedRequest);
            var vendorBankUser = CreateVendorBankUser(budgetedRequest);
            var vendorRecords = CreateVendorFlowRecords(
                budgetedRequest,
                references,
                constItems,
                vendorReferences,
                vendorConstItems,
                vendorBankUser,
                planItems);
            ProcessVendorInterestIfNeeded(budgetedRequest, vendorBank, vendorBankUser, ref vendorRecords);

            var records = vendorRecords
                .Cast<object>()
                .Select((item, index) => CreateFlowRecordFromVendorRecord(budgetedRequest, item, index))
                .Where(item => item.TradeMoney.HasValue && Math.Abs(item.TradeMoney.Value) > 0.009d)
                .ToList();
            BackfillInterestRecords(budgetedRequest, records);
            FlowAutoGenerator.ForceCloseExternalFinalBalance(budgetedRequest, records);
            return records;
        }

        private static FlowAutoGenerationRequest CreateBudgetedGenerationRequest(FlowAutoGenerationRequest request)
        {
            var config = request.Config.Clone();
            var sourceSelectIndex = config.SelectIndex;
            config.AllOutMoney = CalculateTargetExpense(request);
            ApplyMonthlyBudget(config, sourceSelectIndex);
            config.SelectIndex = 2;

            return new FlowAutoGenerationRequest
            {
                Bank = request.Bank,
                BankUser = request.BankUser,
                Config = config,
                References = request.References,
                ConstItems = request.ConstItems,
                InterestSetting = request.InterestSetting,
                OpeningBalanceOverride = request.OpeningBalanceOverride
            };
        }

        private static List<VendorPlanItem> DropReferenceIncomeWhenFixedIncomeCoversTarget(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            IReadOnlyList<VendorPlanItem> planItems)
        {
            var targetIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney));
            var fixedIncomeTotal = CalculateFixedIncomeTotal(references, constItems, planItems);
            if (fixedIncomeTotal < targetIncome - 0.009d)
            {
                return planItems.ToList();
            }

            return planItems
                .Where(item => item.Amount <= 0.009d
                    || item.SourceKind != 0
                    || !TryResolveReferenceRuleIndex(item, references, out _, out var rule)
                    || !IsIncomeReferenceRule(rule))
                .ToList();
        }

        private static double CalculateFixedIncomeTotal(
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            IEnumerable<VendorPlanItem> planItems)
        {
            return RoundMoney(planItems
                .Where(item => item.Amount > 0.009d
                    && TryGetPlanRule(item, references, constItems, out var rule)
                    && rule is GenerateConstRule
                    && IsRuleSigned(rule, isIncome: true))
                .Sum(item => item.Amount));
        }

        private sealed record MonthlyBudgetRange(DateTime Start, DateTime End, double InMoney, double OutMoney);

        private static void ApplyMonthlyBudget(FlowGenerationConfig config, int sourceSelectIndex)
        {
            var rows = CreateMonthlyBudgetRows(config, sourceSelectIndex).ToList();
            config.MonthGenData.Clear();
            foreach (var row in rows)
            {
                config.MonthGenData.Add(new MonthGenerateRule
                {
                    StartTime = row.Start,
                    EndTime = row.End,
                    InMoney = row.InMoney,
                    OutMoney = row.OutMoney
                });
            }

            if (rows.Count == 0)
            {
                return;
            }

            // Keep the user's calculated monthly min/max ranges. MonthGenData carries
            // the concrete per-month targets for the vendor generator.
        }

        private static IEnumerable<MonthlyBudgetRange> CreateMonthlyBudgetRows(FlowGenerationConfig config, int sourceSelectIndex)
        {
            var start = config.StartTime.Date;
            var end = NormalizeEndDate(config.EndTime).Date;
            if (end < start)
            {
                (start, end) = (end, start);
            }

            var ranges = new List<MonthlyBudgetRange>();
            var cursor = new DateTime(start.Year, start.Month, 1);
            while (cursor <= end)
            {
                var monthStart = cursor < start ? start : cursor;
                var monthEndRaw = cursor.AddMonths(1).AddDays(-1);
                var monthEnd = monthEndRaw > end ? end : monthEndRaw;
                ranges.Add(new MonthlyBudgetRange(monthStart, monthEnd, 0, 0));
                cursor = cursor.AddMonths(1);
            }

            if (ranges.Count == 0)
            {
                yield break;
            }

            var totalIncome = RoundMoney(Math.Max(0, config.AllInMoney));
            var totalExpense = RoundMoney(Math.Max(0, config.AllOutMoney));
            var inTargets = CreateMonthlyTargets(
                ranges,
                config.MonthGenData.Select(item => (item.StartTime.Date, item.EndTime.Date, Amount: item.InMoney)),
                totalIncome);
            var outTargets = CreateMonthlyTargets(
                ranges,
                config.MonthGenData.Select(item => (item.StartTime.Date, item.EndTime.Date, Amount: item.OutMoney)),
                totalExpense);
            var incomeBounds = GetMonthlyTargetBounds(config, sourceSelectIndex, isIncome: true);
            var expenseBounds = GetMonthlyTargetBounds(config, sourceSelectIndex, isIncome: false);
            inTargets = FitMonthlyTargetsWithinBounds(inTargets, ranges, totalIncome, incomeBounds.Min, incomeBounds.Max);
            outTargets = FitMonthlyTargetsWithinBounds(outTargets, ranges, totalExpense, expenseBounds.Min, expenseBounds.Max);

            for (var index = 0; index < ranges.Count; index++)
            {
                yield return ranges[index] with
                {
                    InMoney = inTargets[index],
                    OutMoney = outTargets[index]
                };
            }
        }

        private static List<double> CreateMonthlyTargets(
            IReadOnlyList<MonthlyBudgetRange> ranges,
            IEnumerable<(DateTime StartTime, DateTime EndTime, double Amount)> explicitRows,
            double total)
        {
            var values = ranges
                .Select(range => RoundMoney(explicitRows
                    .Where(item => item.EndTime >= range.Start && item.StartTime <= range.End)
                    .Sum(item => item.Amount)))
                .ToList();
            var explicitTotal = RoundMoney(values.Sum());
            if (explicitTotal <= 0.009d)
            {
                var totalDays = Math.Max(1, ranges.Sum(GetCoveredDays));
                var running = 0d;
                for (var index = 0; index < values.Count; index++)
                {
                    values[index] = index == values.Count - 1
                        ? RoundMoney(total - running)
                        : RoundMoney(total * GetCoveredDays(ranges[index]) / totalDays);
                    running = RoundMoney(running + values[index]);
                }

                return values;
            }

            var scale = total / explicitTotal;
            var assigned = 0d;
            for (var index = 0; index < values.Count; index++)
            {
                values[index] = index == values.Count - 1
                    ? RoundMoney(total - assigned)
                    : RoundMoney(values[index] * scale);
                assigned = RoundMoney(assigned + values[index]);
            }

            return values;
        }

        private static (double Min, double Max) GetMonthlyTargetBounds(
            FlowGenerationConfig config,
            int sourceSelectIndex,
            bool isIncome)
        {
            var first = isIncome
                ? (Min: config.MinInMoneyMonth1, Max: config.MaxInMoneyMonth1)
                : (Min: config.MinOutMoneyMonth1, Max: config.MaxOutMoneyMonth1);
            var second = isIncome
                ? (Min: config.MinInMoneyMonth2, Max: config.MaxInMoneyMonth2)
                : (Min: config.MinOutMoneyMonth2, Max: config.MaxOutMoneyMonth2);
            var selected = sourceSelectIndex == 1 ? second : first;
            if (selected.Min <= 0.009d && selected.Max <= 0.009d)
            {
                selected = sourceSelectIndex == 1 ? first : second;
            }

            var min = RoundMoney(Math.Max(0, selected.Min));
            var max = RoundMoney(Math.Max(0, selected.Max));
            if (max > 0.009d && max < min)
            {
                (min, max) = (max, min);
            }

            return (min, max);
        }

        private static List<double> FitMonthlyTargetsWithinBounds(
            IReadOnlyList<double> source,
            IReadOnlyList<MonthlyBudgetRange> ranges,
            double total,
            double min,
            double max)
        {
            if (source.Count == 0 || (min <= 0.009d && max <= 0.009d))
            {
                return source.ToList();
            }

            var upper = max > 0.009d ? max : double.PositiveInfinity;
            var lowerBounds = ranges
                .Select(range => RoundMoney(min * GetMonthCoverageRatio(range)))
                .ToList();
            var upperBounds = ranges
                .Select(range => double.IsPositiveInfinity(upper)
                    ? double.PositiveInfinity
                    : RoundMoney(upper * GetMonthCoverageRatio(range)))
                .ToList();
            if (lowerBounds.Sum() > total + 0.009d
                || (!upperBounds.Any(double.IsPositiveInfinity) && upperBounds.Sum() < total - 0.009d))
            {
                return source.ToList();
            }

            var values = source
                .Select((value, index) => RoundMoney(Math.Min(upperBounds[index], Math.Max(lowerBounds[index], value))))
                .ToList();
            RedistributeMonthlyTargetDiff(values, total, lowerBounds, upperBounds);
            return values;
        }

        private static void RedistributeMonthlyTargetDiff(
            List<double> values,
            double total,
            IReadOnlyList<double> lowerBounds,
            IReadOnlyList<double> upperBounds)
        {
            for (var pass = 0; pass < 4; pass++)
            {
                var diff = RoundMoney(total - values.Sum());
                if (Math.Abs(diff) <= 0.009d)
                {
                    return;
                }

                var candidates = values
                    .Select((value, index) => new { Value = value, Index = index })
                    .Where(item => diff > 0
                        ? item.Value < upperBounds[item.Index] - 0.009d
                        : item.Value > lowerBounds[item.Index] + 0.009d)
                    .OrderBy(item => diff > 0 ? item.Value : -item.Value)
                    .ToList();
                if (candidates.Count == 0)
                {
                    return;
                }

                foreach (var candidate in candidates)
                {
                    diff = RoundMoney(total - values.Sum());
                    if (Math.Abs(diff) <= 0.009d)
                    {
                        return;
                    }

                    var room = diff > 0
                        ? RoundMoney(upperBounds[candidate.Index] - values[candidate.Index])
                        : RoundMoney(values[candidate.Index] - lowerBounds[candidate.Index]);
                    if (room <= 0.009d)
                    {
                        continue;
                    }

                    var change = Math.Min(Math.Abs(diff), room);
                    values[candidate.Index] = RoundMoney(values[candidate.Index] + (diff > 0 ? change : -change));
                }
            }
        }

        private static int GetCoveredDays(MonthlyBudgetRange range)
        {
            return Math.Max(1, (range.End.Date - range.Start.Date).Days + 1);
        }

        private static double GetMonthCoverageRatio(MonthlyBudgetRange range)
        {
            var daysInMonth = DateTime.DaysInMonth(range.Start.Year, range.Start.Month);
            return Math.Clamp(GetCoveredDays(range) / (double)Math.Max(1, daysInMonth), 0.01d, 1d);
        }

        private static double CalculateTargetIncome(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            IEnumerable<VendorPlanItem> planItems)
        {
            return RoundMoney(Math.Max(0, request.Config.AllInMoney));
        }

        private static List<VendorPlanItem> EnsureRequiredReferencePlanItems(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<VendorPlanItem> planItems)
        {
            var items = planItems.ToList();
            var months = EnumerateMonthStarts(request).ToList();
            if (months.Count == 0)
            {
                return items;
            }

            for (var ruleIndex = 0; ruleIndex < references.Count; ruleIndex++)
            {
                var rule = references[ruleIndex];
                var requiredCount = Math.Max(0, rule.PercentMonth ?? 0);
                if (requiredCount <= 0)
                {
                    continue;
                }

                foreach (var month in months)
                {
                    var existingCount = CountRequiredReferencePlanItemsInMonth(items, references, ruleIndex, rule, month);
                    for (var sequence = existingCount; sequence < requiredCount; sequence++)
                    {
                        if (!TryCreateRequiredReferencePlanItem(
                                request,
                                rule,
                                ruleIndex,
                                month,
                                sequence,
                                requiredCount,
                                out var item))
                        {
                            continue;
                        }

                        items.Add(item);
                    }
                }
            }

            return items;
        }

        private static int CountRequiredReferencePlanItemsInMonth(
            IEnumerable<VendorPlanItem> items,
            IReadOnlyList<GenerateReferenceRule> references,
            int ruleIndex,
            GenerateReferenceRule rule,
            DateTime month)
        {
            return items.Count(item => IsRequiredReferencePlanItemForRule(item, references, ruleIndex, rule, month));
        }

        private static bool IsRequiredReferencePlanItemForRule(
            VendorPlanItem item,
            IReadOnlyList<GenerateReferenceRule> references,
            int ruleIndex,
            GenerateReferenceRule rule,
            DateTime month)
        {
            if (item.SourceKind != 0 || !IsSameMonth(item.AccountTime, month))
            {
                return false;
            }

            var sign = IsIncomeReferenceRule(rule) ? 1 : -1;
            if (GetAmountSign(item.Amount) != sign)
            {
                return false;
            }

            if (!TryResolveReferenceRuleIndex(item, references, out var resolvedRuleIndex, out _)
                || resolvedRuleIndex != ruleIndex)
            {
                return false;
            }

            var absolute = Math.Abs(item.Amount);
            var (min, max) = GetRuleAmountRange(rule);
            if (absolute < min - 0.009d || absolute > max + 0.009d)
            {
                return false;
            }

            return true;
        }

        private static bool TryCreateRequiredReferencePlanItem(
            FlowAutoGenerationRequest request,
            GenerateReferenceRule rule,
            int ruleIndex,
            DateTime month,
            int sequence,
            int requiredCount,
            out VendorPlanItem item)
        {
            item = null!;
            if (!TryGetAllowedReferenceRange(request, rule, month, out var allowedStart, out var allowedEnd))
            {
                return false;
            }

            var accountTime = CreateRequiredReferenceAccountTime(request, rule, allowedStart, allowedEnd, ruleIndex, sequence, requiredCount);
            var amount = CreateRequiredReferenceAmount(rule, ruleIndex, month, sequence);
            if (amount <= 0.009d)
            {
                return false;
            }

            item = new VendorPlanItem(
                accountTime,
                IsIncomeReferenceRule(rule) ? amount : -amount,
                0,
                ruleIndex,
                false);
            return true;
        }

        private static DateTime CreateRequiredReferenceAccountTime(
            FlowAutoGenerationRequest request,
            GenerateReferenceRule rule,
            DateTime allowedStart,
            DateTime allowedEnd,
            int ruleIndex,
            int sequence,
            int requiredCount)
        {
            var startDate = allowedStart.Date;
            var endDate = allowedEnd.Date;
            var dayCount = Math.Max(1, (endDate - startDate).Days + 1);
            var dayOffset = Math.Min(
                dayCount - 1,
                (int)Math.Floor(sequence * dayCount / (double)Math.Max(1, requiredCount)));
            var date = startDate.AddDays(dayOffset);
            var start = MaxDateTime(allowedStart, date);
            var end = MinDateTime(allowedEnd, date.AddDays(1).AddTicks(-1));
            if (start > end)
            {
                start = allowedStart;
                end = allowedEnd;
            }

            return CreateRuleTimeInRange(request, rule, start, end, sequence * 997 + ruleIndex * 389 + 113);
        }

        private static double CreateRequiredReferenceAmount(
            GenerateReferenceRule rule,
            int ruleIndex,
            DateTime month,
            int sequence)
        {
            var (min, max) = GetRuleAmountRange(rule);
            if (max <= min + 0.009d)
            {
                return NormalizeSignedAmountByRule(min, rule);
            }

            var lowBiasMax = min + ((max - min) * 0.35d);
            var bucket = Math.Abs(((month.Year * 31 + month.Month * 17 + ruleIndex * 23 + sequence * 37) % 1000) / 999d);
            var amount = min + ((lowBiasMax - min) * bucket);
            amount = RoundAmountByFloutLength(amount, rule.FloutLength);
            amount = Math.Min(max, Math.Max(min, amount));
            return RoundMoney(amount);
        }

        private static List<VendorPlanItem> ExpandSparseIncomePlanItems(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<VendorPlanItem> planItems)
        {
            var incomeReferenceIndexes = references
                .Select((rule, index) => new { Rule = rule, Index = index })
                .Where(item => IsIncomeReferenceRule(item.Rule))
                .Select(item => item.Index)
                .ToList();
            if (incomeReferenceIndexes.Count == 0)
            {
                return planItems.ToList();
            }

            var incomeIndexSet = incomeReferenceIndexes.ToHashSet();
            if (incomeReferenceIndexes.Count > 2)
            {
                return DistributeSparseIncomePlanItemsByMonth(request, references, planItems.ToList(), incomeIndexSet);
            }

            var topUpIncomeIndexes = incomeReferenceIndexes
                .Where(index => !IsRequiredPlanRule(references[index]))
                .OrderBy(index => IsPaymentLikeIncomeRule(references[index]))
                .ThenBy(index => GetRuleAmountRange(references[index]).Min)
                .ToList();
            if (topUpIncomeIndexes.Count == 0)
            {
                return DistributeSparseIncomePlanItemsByMonth(request, references, planItems.ToList(), incomeIndexSet);
            }

            var topUpIndexSet = topUpIncomeIndexes.ToHashSet();
            var normalized = TopUpSparseIncomePlanItems(request, references, planItems, topUpIncomeIndexes, topUpIndexSet);
            var expanded = new List<VendorPlanItem>(normalized.Count);
            foreach (var item in normalized)
            {
                if (TryGetSparseIncomeRule(item, references, topUpIndexSet, out var rule))
                {
                    expanded.AddRange(SplitSparseIncomePlanItem(item, rule));
                }
                else
                {
                    expanded.Add(item);
                }
            }

            return DistributeSparseIncomePlanItemsByMonth(request, references, expanded, incomeIndexSet);
        }

        private static List<VendorPlanItem> PreferNonPaymentIncomePlanItems(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<VendorPlanItem> planItems)
        {
            var preferredIncomeRules = references
                .Select((rule, index) => new { Rule = rule, Index = index })
                .Where(item => IsIncomeReferenceRule(item.Rule) && !IsPaymentLikeIncomeRule(item.Rule))
                .ToList();
            if (preferredIncomeRules.Count == 0)
            {
                return planItems.ToList();
            }

            var items = planItems.ToList();
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.SourceKind != 0
                    || item.Amount <= 0.009d
                    || !TryResolveReferenceRuleIndex(item, references, out _, out var currentRule)
                    || IsRequiredPlanRule(currentRule)
                    || !IsPaymentLikeIncomeRule(currentRule))
                {
                    continue;
                }

                var replacement = preferredIncomeRules
                    .Select(candidate => new
                    {
                        candidate.Rule,
                        candidate.Index,
                        Score = ScoreIncomeReplacementRule(request, item, candidate.Rule, candidate.Index)
                    })
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => IsRequiredPlanRule(candidate.Rule))
                    .ThenBy(candidate => Math.Abs(candidate.Index - item.SourceIndex))
                    .FirstOrDefault();
                if (replacement is null)
                {
                    continue;
                }

                var amount = NormalizeSignedAmountByRule(item.Amount, replacement.Rule);
                var accountTime = item.AccountTime;
                if (!TryGetAllowedRuleRange(request, replacement.Rule, accountTime, out _, out _))
                {
                    accountTime = CreateBalancingAccountTime(request, replacement.Rule, index);
                }

                items[index] = item with
                {
                    AccountTime = accountTime,
                    Amount = amount,
                    SourceIndex = replacement.Index
                };
            }

            return items;
        }

        private static int ScoreIncomeReplacementRule(
            FlowAutoGenerationRequest request,
            VendorPlanItem item,
            GenerateReferenceRule rule,
            int ruleIndex)
        {
            var score = 0;
            var absolute = Math.Abs(item.Amount);
            var (min, max) = GetRuleAmountRange(rule);
            if (absolute >= min - 0.009d && absolute <= max + 0.009d)
            {
                score += 50;
            }

            if (TryGetAllowedRuleRange(request, rule, item.AccountTime, out _, out _))
            {
                score += 20;
            }

            score -= Math.Min(20, Math.Abs(ruleIndex - item.SourceIndex));
            return score;
        }

        private static List<VendorPlanItem> TopUpSparseIncomePlanItems(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<VendorPlanItem> planItems,
            IReadOnlyList<int> incomeReferenceIndexes,
            ISet<int> incomeIndexSet)
        {
            var items = planItems.ToList();
            var targetIncome = RoundMoney(Math.Max(0, request.Config.AllInMoney));
            var currentIncome = RoundMoney(items.Where(item => item.Amount > 0).Sum(item => item.Amount));
            for (var pass = 0; pass < 4 && currentIncome > targetIncome + 0.009d; pass++)
            {
                var before = currentIncome;
                TrimSparseIncomePlanItems(references, items, incomeIndexSet, RoundMoney(currentIncome - targetIncome));
                currentIncome = RoundMoney(items.Where(item => item.Amount > 0).Sum(item => item.Amount));
                if (currentIncome >= before - 0.009d)
                {
                    break;
                }
            }

            var remaining = RoundMoney(targetIncome - currentIncome);
            if (remaining <= 0.009d)
            {
                return items;
            }

            for (var index = 0; index < items.Count && remaining > 0.009d; index++)
            {
                var item = items[index];
                if (!TryGetSparseIncomeRule(item, references, incomeIndexSet, out var rule))
                {
                    continue;
                }

                var (_, max) = GetRuleAmountRange(rule);
                var desired = Math.Min(max, item.Amount + remaining);
                var adjusted = RoundAmountDownByFloutLength(desired, rule.FloutLength);
                if (adjusted <= item.Amount + 0.009d)
                {
                    continue;
                }

                var delta = RoundMoney(adjusted - item.Amount);
                if (delta > remaining + 0.009d)
                {
                    continue;
                }

                items[index] = item with { Amount = adjusted };
                remaining = RoundMoney(remaining - delta);
            }

            var addIndex = 0;
            while (remaining > 0.009d && incomeReferenceIndexes.Count > 0)
            {
                var ruleIndex = incomeReferenceIndexes[addIndex % incomeReferenceIndexes.Count];
                var rule = references[ruleIndex];
                var (min, max) = GetRuleAmountRange(rule);
                if (remaining + 0.009d < min)
                {
                    break;
                }

                var amount = remaining >= min * 2
                    ? min
                    : Math.Min(max, remaining);
                amount = RoundAmountDownByFloutLength(amount, rule.FloutLength);
                if (amount + 0.009d < min || amount > max + 0.009d)
                {
                    break;
                }

                items.Add(new VendorPlanItem(
                    CreateSparseIncomeAccountTime(request, rule, addIndex),
                    amount,
                    0,
                    ruleIndex,
                    false));
                remaining = RoundMoney(remaining - amount);
                addIndex++;
            }

            return items;
        }

        private static void TrimSparseIncomePlanItems(
            IReadOnlyList<GenerateReferenceRule> references,
            List<VendorPlanItem> items,
            ISet<int> incomeIndexSet,
            double excess)
        {
            foreach (var index in items
                         .Select((item, index) => new { Item = item, Index = index })
                         .Where(item => TryGetSparseIncomeRule(item.Item, references, incomeIndexSet, out _))
                         .OrderByDescending(item => item.Item.Amount)
                         .Select(item => item.Index)
                         .ToList())
            {
                if (excess <= 0.009d)
                {
                    return;
                }

                var item = items[index];
                if (!TryGetSparseIncomeRule(item, references, incomeIndexSet, out var rule))
                {
                    continue;
                }

                var (min, _) = GetRuleAmountRange(rule);
                var desired = Math.Max(min, item.Amount - excess);
                var adjusted = Math.Max(min, RoundAmountDownByFloutLength(desired, rule.FloutLength));
                if (adjusted >= item.Amount - 0.009d)
                {
                    continue;
                }

                items[index] = item with { Amount = adjusted };
                excess = RoundMoney(excess - (item.Amount - adjusted));
            }
        }

        private sealed class IncomeMonthBucket
        {
            public required DateTime Start { get; init; }

            public required DateTime End { get; init; }

            public required int MonthIndex { get; init; }

            public double Target { get; set; }

            public double Assigned { get; set; }

            public int Count { get; set; }

            public HashSet<int> UsedDays { get; } = [];
        }

        private static List<VendorPlanItem> DistributeSparseIncomePlanItemsByMonth(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<VendorPlanItem> items,
            ISet<int> incomeIndexSet)
        {
            var buckets = CreateIncomeMonthBuckets(request);
            if (buckets.Count == 0)
            {
                return items.ToList();
            }

            foreach (var item in items.Where(item => item.Amount > 0.009d && !TryGetSparseIncomeRule(item, references, incomeIndexSet, out _)))
            {
                var bucket = FindBucketByDate(buckets, item.AccountTime);
                if (bucket is not null)
                {
                    bucket.Assigned = RoundMoney(bucket.Assigned + item.Amount);
                }
            }

            var result = items.ToList();
            var sparseItems = result
                .Select((item, index) => new { Item = item, Index = index })
                .Where(item => TryGetSparseIncomeRule(item.Item, references, incomeIndexSet, out _))
                .OrderBy(item => item.Item.AccountTime)
                .ThenByDescending(item => item.Item.Amount)
                .ToList();

            for (var sequence = 0; sequence < sparseItems.Count; sequence++)
            {
                var entry = sparseItems[sequence];
                if (!TryGetSparseIncomeRule(entry.Item, references, incomeIndexSet, out var rule))
                {
                    continue;
                }

                var bucket = SelectIncomeMonthBucket(buckets, rule, entry.Item.Amount);
                var time = CreateSparseIncomeAccountTime(request, rule, bucket, sequence);
                bucket.Count++;
                bucket.Assigned = RoundMoney(bucket.Assigned + entry.Item.Amount);
                result[entry.Index] = entry.Item with { AccountTime = time };
            }

            return result;
        }

        private static List<IncomeMonthBucket> CreateIncomeMonthBuckets(FlowAutoGenerationRequest request)
        {
            var start = request.Config.StartTime.Date;
            var end = NormalizeEndDate(request.Config.EndTime).Date;
            if (end < start)
            {
                (start, end) = (end, start);
            }

            var buckets = new List<IncomeMonthBucket>();
            var cursor = new DateTime(start.Year, start.Month, 1);
            var monthIndex = 0;
            while (cursor <= end)
            {
                var monthStart = cursor < start ? start : cursor;
                var monthEndRaw = cursor.AddMonths(1).AddDays(-1);
                var monthEnd = monthEndRaw > end ? end : monthEndRaw;
                buckets.Add(new IncomeMonthBucket
                {
                    Start = monthStart,
                    End = monthEnd,
                    MonthIndex = monthIndex++
                });
                cursor = cursor.AddMonths(1);
            }

            ApplyIncomeMonthTargets(request, buckets);
            return buckets;
        }

        private static void ApplyIncomeMonthTargets(FlowAutoGenerationRequest request, List<IncomeMonthBucket> buckets)
        {
            var total = RoundMoney(Math.Max(0, request.Config.AllInMoney));
            if (buckets.Count == 0)
            {
                return;
            }

            foreach (var bucket in buckets)
            {
                bucket.Target = RoundMoney(request.Config.MonthGenData
                    .Where(item => item.EndTime.Date >= bucket.Start && item.StartTime.Date <= bucket.End)
                    .Sum(item => item.InMoney));
            }

            var explicitTotal = RoundMoney(buckets.Sum(item => item.Target));
            if (explicitTotal > 0.009d)
            {
                var scale = total / explicitTotal;
                var assigned = 0d;
                for (var index = 0; index < buckets.Count; index++)
                {
                    buckets[index].Target = index == buckets.Count - 1
                        ? RoundMoney(total - assigned)
                        : RoundMoney(buckets[index].Target * scale);
                    assigned = RoundMoney(assigned + buckets[index].Target);
                }

                return;
            }

            var average = RoundMoney(total / buckets.Count);
            var running = 0d;
            for (var index = 0; index < buckets.Count; index++)
            {
                buckets[index].Target = index == buckets.Count - 1
                    ? RoundMoney(total - running)
                    : average;
                running = RoundMoney(running + buckets[index].Target);
            }
        }

        private static IncomeMonthBucket SelectIncomeMonthBucket(
            IReadOnlyList<IncomeMonthBucket> buckets,
            GenerateReferenceRule rule,
            double amount)
        {
            return buckets
                .OrderByDescending(bucket => HasNonAdjacentIncomeDay(bucket, rule))
                .ThenBy(bucket => bucket.Assigned / Math.Max(1d, bucket.Target))
                .ThenByDescending(bucket => bucket.Target - bucket.Assigned >= amount ? 1 : 0)
                .ThenBy(bucket => bucket.Count)
                .ThenBy(bucket => bucket.MonthIndex)
                .First();
        }

        private static IncomeMonthBucket? FindBucketByDate(IEnumerable<IncomeMonthBucket> buckets, DateTime value)
        {
            var date = value.Date;
            return buckets.FirstOrDefault(bucket => date >= bucket.Start && date <= bucket.End);
        }

        private static IEnumerable<VendorPlanItem> SplitSparseIncomePlanItem(VendorPlanItem item, GenerateReferenceRule rule)
        {
            var (min, max) = GetRuleAmountRange(rule);
            var amount = RoundAmountByFloutLength(item.Amount, rule.FloutLength);
            var maxByMinimum = (int)Math.Floor((amount + 0.009d) / min);
            var count = Math.Min(MaxSparseIncomeSplitCount, maxByMinimum);
            if (count <= 1)
            {
                yield return item with { Amount = amount };
                yield break;
            }

            var parts = new List<double>(count);
            var remaining = amount;
            for (var index = 0; index < count; index++)
            {
                var remainingParts = count - index - 1;
                var part = remainingParts == 0
                    ? remaining
                    : RoundAmountByFloutLength(remaining / (remainingParts + 1), rule.FloutLength);
                var maxForCurrent = RoundAmountDownByFloutLength(remaining - remainingParts * min, rule.FloutLength);
                part = Math.Min(part, maxForCurrent);
                part = Math.Min(part, max);
                if (part + 0.009d < min)
                {
                    yield return item with { Amount = amount };
                    yield break;
                }

                part = RoundMoney(part);
                parts.Add(part);
                remaining = RoundMoney(remaining - part);
            }

            if (Math.Abs(parts.Sum() - amount) > 0.009d
                || parts.Any(part => part + 0.009d < min || part > max + 0.009d))
            {
                yield return item with { Amount = amount };
                yield break;
            }

            for (var index = 0; index < parts.Count; index++)
            {
                yield return item with { Amount = parts[index] };
            }
        }

        private static bool TryGetSparseIncomeRule(
            VendorPlanItem item,
            IReadOnlyList<GenerateReferenceRule> references,
            ISet<int> incomeIndexSet,
            out GenerateReferenceRule rule)
        {
            if (item.SourceKind == 0
                && item.Amount > 0.009d
                && TryResolveReferenceRuleIndex(item, references, out var sourceIndex, out rule)
                && incomeIndexSet.Contains(sourceIndex))
            {
                return true;
            }

            rule = null!;
            return false;
        }

        private static bool IsIncomeReferenceRule(GenerateReferenceRule rule)
        {
            return (rule.IncomeAttribute ?? string.Empty).Contains(IncomeText, StringComparison.Ordinal);
        }

        private static bool IsPaymentLikeIncomeRule(FlowRuleBase rule)
        {
            if (!IsRuleSigned(rule, isIncome: true))
            {
                return false;
            }

            var text = string.Join(" ", new[]
            {
                rule.ProductName,
                rule.ProductBrief,
                rule.ProductType,
                rule.IncomeType,
                rule.Usage,
                rule.TradeExplain,
                rule.Remark,
                rule.TradeChannel,
                rule.OppositeUsername,
                rule.OppositeBank
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            return ContainsAny(text, PaymentLikeIncomeKeywords);
        }

        private static (double Min, double Max) GetRuleAmountRange(FlowRuleBase rule)
        {
            var min = Math.Max(0.01d, rule.MinMoney ?? 0.01d);
            var max = Math.Max(min, rule.MaxMoney ?? min);
            return (RoundMoney(min), RoundMoney(max));
        }

        private static DateTime CreateSparseIncomeAccountTime(
            FlowAutoGenerationRequest request,
            GenerateReferenceRule rule,
            int sequence)
        {
            var start = request.Config.StartTime;
            var end = NormalizeEndDate(request.Config.EndTime);
            var monthCount = Math.Max(1, ((end.Year - start.Year) * 12) + end.Month - start.Month + 1);
            var month = start.Date.AddMonths(sequence % monthCount);
            var monthStart = month < start.Date ? start.Date : month;
            var monthEndRaw = month.AddMonths(1).AddDays(-1);
            var monthEnd = monthEndRaw > end.Date ? end.Date : monthEndRaw;
            var dayCount = Math.Max(1, (monthEnd - monthStart).Days + 1);
            var date = monthStart.AddDays(sequence % dayCount);
            return CreateRuleTimeInRange(
                request,
                rule,
                MaxDateTime(start, date),
                MinDateTime(end, date.AddDays(1).AddTicks(-1)),
                sequence * 41 + 11);
        }

        private static DateTime CreateSparseIncomeAccountTime(
            FlowAutoGenerationRequest request,
            GenerateReferenceRule rule,
            IncomeMonthBucket bucket,
            int sequence)
        {
            var date = SelectSparseIncomeDate(rule, bucket, sequence);
            var start = request.Config.StartTime;
            var end = NormalizeEndDate(request.Config.EndTime);
            return CreateRuleTimeInRange(
                request,
                rule,
                MaxDateTime(start, date),
                MinDateTime(end, date.AddDays(1).AddTicks(-1)),
                sequence * 43 + bucket.MonthIndex * 17);
        }

        private static DateTime SelectSparseIncomeDate(
            GenerateReferenceRule rule,
            IncomeMonthBucket bucket,
            int sequence)
        {
            var candidates = CreateSparseIncomeDateCandidates(rule, bucket).ToList();
            if (candidates.Count == 0)
            {
                bucket.UsedDays.Add(bucket.Start.Day);
                return bucket.Start;
            }

            var nonAdjacent = candidates
                .Where(date => bucket.UsedDays.All(day => Math.Abs(day - date.Day) > 1))
                .ToList();
            var pool = nonAdjacent.Count > 0 ? nonAdjacent : candidates;
            var idealOffset = (sequence * 5 + bucket.MonthIndex * 3) % pool.Count;
            var selected = pool
                .Select((date, index) => new
                {
                    Date = date,
                    Index = index,
                    Distance = bucket.UsedDays.Count == 0
                        ? int.MaxValue
                        : bucket.UsedDays.Min(day => Math.Abs(day - date.Day))
                })
                .OrderByDescending(item => item.Distance)
                .ThenBy(item => Math.Abs(item.Index - idealOffset))
                .ThenBy(item => item.Date)
                .First()
                .Date;

            bucket.UsedDays.Add(selected.Day);
            return selected;
        }

        private static IEnumerable<DateTime> CreateSparseIncomeDateCandidates(GenerateReferenceRule rule, IncomeMonthBucket bucket)
        {
            for (var date = bucket.Start.Date; date <= bucket.End.Date; date = date.AddDays(1))
            {
                if (!bucket.UsedDays.Contains(date.Day))
                {
                    yield return date;
                }
            }
        }

        private static bool HasNonAdjacentIncomeDay(IncomeMonthBucket bucket, GenerateReferenceRule rule)
        {
            return CreateSparseIncomeDateCandidates(rule, bucket)
                .Any(date => bucket.UsedDays.All(day => Math.Abs(day - date.Day) > 1));
        }

        private static List<VendorPlanItem> NormalizePlanItemTotals(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            IReadOnlyList<VendorPlanItem> source)
        {
            var items = source.ToList();
            NormalizeSignedPlanTotal(
                request,
                references,
                constItems,
                items,
                isIncome: true,
                targetTotal: CalculateTargetIncome(request, references, constItems, items));
            var actualIncomeTotal = SumPlanTotal(items, isIncome: true);
            NormalizeSignedPlanTotal(
                request,
                references,
                constItems,
                items,
                isIncome: false,
                targetTotal: CalculateTargetExpenseFromIncome(request, actualIncomeTotal));
            return items;
        }

        private static List<VendorPlanItem> NormalizePlanItemAmountsByRule(
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            IReadOnlyList<VendorPlanItem> source)
        {
            var items = new List<VendorPlanItem>(source.Count);
            foreach (var item in source)
            {
                if (Math.Abs(item.Amount) <= 0.009d
                    || !TryGetPlanRule(item, references, constItems, out var rule))
                {
                    items.Add(item);
                    continue;
                }

                items.Add(item with { Amount = NormalizeSignedAmountByRule(item.Amount, rule) });
            }

            return items;
        }

        private static double NormalizeSignedAmountByRule(double amount, FlowRuleBase rule)
        {
            var sign = amount < 0 ? -1d : 1d;
            var value = RoundAmountByFloutLength(Math.Abs(amount), rule.FloutLength);
            var (min, max) = GetRuleAmountRange(rule);
            value = Math.Min(max, Math.Max(min, value));
            value = RoundAmountByFloutLength(value, rule.FloutLength);
            value = Math.Min(max, Math.Max(min, value));
            return RoundMoney(sign * value);
        }

        private static List<VendorPlanItem> EnsureDailyPlanItemCoverage(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            IReadOnlyList<VendorPlanItem> source)
        {
            var items = source.ToList();
            if (references.Count == 0)
            {
                return items;
            }

            var start = request.Config.StartTime.Date;
            var end = NormalizeEndDate(request.Config.EndTime).Date;
            if (end < start)
            {
                (start, end) = (end, start);
            }

            var dayCount = (end - start).Days + 1;
            if (dayCount <= 1)
            {
                return items;
            }

            var occupiedDates = items
                .Where(item => Math.Abs(item.Amount) > 0.009d)
                .Select(item => item.AccountTime.Date)
                .ToHashSet();
            var sequence = 0;
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                if (occupiedDates.Contains(date))
                {
                    continue;
                }

                var desiredSign = ChooseDailyCoverageSign(request, items, date);
                if (!TryCreateDailyCoveragePlanItem(
                        request,
                        references,
                        items,
                        date,
                        desiredSign,
                        sequence,
                        out var item)
                    && !TryCreateDailyCoveragePlanItem(
                        request,
                        references,
                        items,
                        date,
                        -desiredSign,
                        sequence,
                        out item))
                {
                    continue;
                }

                items.Add(item);
                occupiedDates.Add(date);
                sequence++;
            }

            return items;
        }

        private static int ChooseDailyCoverageSign(
            FlowAutoGenerationRequest request,
            IReadOnlyList<VendorPlanItem> items,
            DateTime date)
        {
            var projectedDiff = GetProjectedFinalBalanceDiff(request, items);
            if (projectedDiff > 500d)
            {
                return -1;
            }

            if (projectedDiff < -500d)
            {
                return 1;
            }

            var previousSign = items
                .Where(item => item.AccountTime.Date < date && GetAmountSign(item.Amount) != 0)
                .OrderByDescending(item => item.AccountTime)
                .Select(item => GetAmountSign(item.Amount))
                .FirstOrDefault();
            return previousSign > 0 ? -1 : 1;
        }

        private static bool TryCreateDailyCoveragePlanItem(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<VendorPlanItem> items,
            DateTime date,
            int sign,
            int sequence,
            out VendorPlanItem item)
        {
            item = null!;
            var isIncome = sign > 0;
            var candidates = references
                .Select((rule, index) => new { Rule = rule, Index = index })
                .Where(candidate => IsRuleSigned(candidate.Rule, isIncome)
                    && TryCreateDailyCoverageTime(request, candidate.Rule, items, date, sequence + candidate.Index, out _))
                .OrderBy(candidate => IsRequiredPlanRule(candidate.Rule))
                .ThenBy(candidate => isIncome && IsPaymentLikeIncomeRule(candidate.Rule))
                .ThenBy(candidate => GetRuleAmountRange(candidate.Rule).Min)
                .ThenBy(candidate => candidate.Index)
                .ToList();
            foreach (var candidate in candidates)
            {
                if (!TryCreateDailyCoverageTime(request, candidate.Rule, items, date, sequence + candidate.Index, out var accountTime))
                {
                    continue;
                }

                var (min, _) = GetRuleAmountRange(candidate.Rule);
                var amount = NormalizeSignedAmountByRule(sign * min, candidate.Rule);
                if (Math.Abs(amount) <= 0.009d)
                {
                    continue;
                }

                item = new VendorPlanItem(accountTime, amount, 0, candidate.Index, false);
                if (sign < 0 && WouldPlanHaveNegativeBalance(request, items.Append(item)))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool TryCreateDailyCoverageTime(
            FlowAutoGenerationRequest request,
            GenerateReferenceRule rule,
            IReadOnlyList<VendorPlanItem> items,
            DateTime date,
            int sequence,
            out DateTime accountTime)
        {
            accountTime = default;
            if (!TryGetAllowedReferenceRange(request, rule, date, out var allowedStart, out var allowedEnd))
            {
                return false;
            }

            var start = MaxDateTime(allowedStart, date.Date);
            var end = MinDateTime(allowedEnd, date.Date.AddDays(1).AddTicks(-1));
            if (start > end)
            {
                return false;
            }

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = TruncateToSecond(CreateRuleTimeInRange(request, rule, start, end, sequence + attempt * 29));
                if (!IsSecondOccupied(items, -1, candidate))
                {
                    accountTime = candidate;
                    return true;
                }
            }

            return false;
        }

        private static List<VendorPlanItem> StabilizePlanItemBalances(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            IReadOnlyList<VendorPlanItem> source)
        {
            var items = source.ToList();
            var isWechat = IsWechatBank(request.Bank);
            MoveMonthStartReferenceExpensesAfterIncome(request, references, items);
            RunBalanceStabilizationPasses(request, references, constItems, items);
            SpreadSameDirectionPlanItems(request, references, constItems, items);
            SpreadDenseSameDayPlanItems(request, references, constItems, items);
            if (isWechat)
            {
                DeduplicatePlanItemAccountTimes(request, references, constItems, items);
                return items;
            }

            RunBalanceStabilizationPasses(request, references, constItems, items);
            RestoreExpenseTotalSafely(request, references, constItems, items);
            RunBalanceStabilizationPasses(request, references, constItems, items);
            SpreadSameDirectionPlanItems(request, references, constItems, items);
            SpreadDenseSameDayPlanItems(request, references, constItems, items);
            DeduplicatePlanItemAccountTimes(request, references, constItems, items);
            RunBalanceStabilizationPasses(request, references, constItems, items);
            SpreadSameDirectionPlanItems(request, references, constItems, items);
            DeduplicatePlanItemAccountTimes(request, references, constItems, items);

            return items;
        }

        private static void RunBalanceStabilizationPasses(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items)
        {
            var maxPasses = IsWechatBank(request.Bank)
                ? Math.Min(Math.Max(4, items.Count / 20), 24)
                : Math.Min(Math.Max(24, items.Count * 2), 240);
            for (var pass = 0; pass < maxPasses; pass++)
            {
                if (!TryFindFirstNegativeExpense(request, items, out var problemIndex, out var problemTime))
                {
                    break;
                }

                if (TryAdvanceIncomeBeforeProblem(
                        request,
                        references,
                        constItems,
                        items,
                        problemTime,
                        pass))
                {
                    continue;
                }

                if (TryDeferReferenceExpenseBeforeProblem(
                        request,
                        references,
                        constItems,
                        items,
                        problemIndex,
                        problemTime,
                        pass))
                {
                    continue;
                }

                if (TryReduceOrRemoveOptionalExpenseBeforeProblem(
                        request,
                        references,
                        constItems,
                        items,
                        problemIndex))
                {
                    continue;
                }

                break;
            }
        }

        private static void SpreadSameDirectionPlanItems(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items)
        {
            var maxPasses = IsWechatBank(request.Bank)
                ? Math.Min(Math.Max(2, items.Count / 40), 12)
                : Math.Min(Math.Max(items.Count, 1), 120);
            for (var pass = 0; pass < maxPasses; pass++)
            {
                var ordered = GetOrderedPlanEntries(items);
                var runs = FindSameDirectionRuns(ordered)
                    .OrderByDescending(run => run.Length)
                    .ThenBy(run => run.Start)
                    .ToList();
                if (runs.Count == 0)
                {
                    return;
                }

                var changed = false;
                foreach (var run in runs)
                {
                    var runChanged = TryMoveOppositePlanItemIntoRun(
                        request,
                        references,
                        constItems,
                        items,
                        ordered,
                        run.Start,
                        run.End,
                        run.Sign,
                        pass);
                    runChanged = runChanged || TryMoveRunPlanItemAfterOpposite(
                        request,
                        references,
                        constItems,
                        items,
                        ordered,
                        run.Start,
                        run.End,
                        run.Sign,
                        pass);
                    runChanged = runChanged || (run.Sign > 0
                        && TryInsertSmallOptionalExpenseIntoIncomeRun(
                            request,
                            references,
                            constItems,
                            items,
                            ordered,
                            run.Start,
                            run.End,
                            pass));
                    runChanged = runChanged || (run.Sign < 0
                        && TryInsertSmallIncomeIntoExpenseRun(
                            request,
                            references,
                            constItems,
                            items,
                            ordered,
                            run.Start,
                            run.End,
                            pass));
                    runChanged = runChanged || TryRemoveOptionalSameDirectionRunItems(
                        references,
                        constItems,
                        items,
                        ordered,
                        run.Start,
                        run.End,
                        maxRunLength: 2);

                    if (runChanged)
                    {
                        changed = true;
                        break;
                    }
                }

                if (!changed)
                {
                    return;
                }
            }
        }

        private static bool TryRemoveOptionalSameDirectionRunItems(
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            IReadOnlyList<PlanOrderEntry> ordered,
            int runStart,
            int runEnd,
            int maxRunLength)
        {
            var runLength = runEnd - runStart + 1;
            var excess = runLength - maxRunLength;
            if (excess <= 0)
            {
                return false;
            }

            var removableIndexes = ordered
                .Skip(runStart)
                .Take(runLength)
                .Where(entry => TryGetPlanRule(entry.Item, references, constItems, out var rule)
                    && !IsRequiredPlanRule(rule))
                .OrderBy(entry => Math.Abs(entry.Item.Amount))
                .ThenByDescending(entry => entry.Item.AccountTime)
                .Select(entry => entry.Index)
                .Take(excess)
                .OrderByDescending(index => index)
                .ToList();
            if (removableIndexes.Count == 0)
            {
                return false;
            }

            foreach (var index in removableIndexes)
            {
                items.RemoveAt(index);
            }

            return true;
        }

        private static void SpreadDenseSameDayPlanItems(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items)
        {
            var passCount = IsWechatBank(request.Bank) ? 1 : 3;
            for (var pass = 0; pass < passCount; pass++)
            {
                var changed = false;
                var ordered = items
                    .Select((item, index) => new PlanOrderEntry(index, item))
                    .Where(entry => GetAmountSign(entry.Item.Amount) != 0)
                    .OrderBy(entry => entry.Item.AccountTime)
                    .ThenBy(entry => entry.Index)
                    .ToList();

                foreach (var monthGroup in ordered
                             .GroupBy(entry => new
                             {
                                 Month = new DateTime(entry.Item.AccountTime.Year, entry.Item.AccountTime.Month, 1),
                                 Sign = GetAmountSign(entry.Item.Amount)
                             })
                             .OrderBy(group => group.Key.Month)
                             .ThenBy(group => group.Key.Sign))
                {
                    var entries = monthGroup.ToList();
                    if (entries.Count <= 3)
                    {
                        continue;
                    }

                    var maxPerDay = CalculateMaxSameDirectionPerDay(request, monthGroup.Key.Month, entries.Count);
                    var sameSignDayCounts = entries
                        .GroupBy(entry => entry.Item.AccountTime.Date)
                        .ToDictionary(group => group.Key, group => group.Count());
                    var totalDayCounts = ordered
                        .Where(entry => IsSameMonth(entry.Item.AccountTime, monthGroup.Key.Month))
                        .GroupBy(entry => entry.Item.AccountTime.Date)
                        .ToDictionary(group => group.Key, group => group.Count());

                    foreach (var denseDay in sameSignDayCounts
                                 .Where(item => item.Value > maxPerDay)
                                 .OrderByDescending(item => item.Value)
                                 .ThenBy(item => item.Key)
                                 .ToList())
                    {
                        var movableIndexes = entries
                            .Where(entry => entry.Item.AccountTime.Date == denseDay.Key
                                && TryGetReferencePlanRule(entry.Item, references, out _))
                            .OrderBy(entry => IsPlanItemRequired(entry.Item, references, constItems))
                            .ThenBy(entry => Math.Abs(entry.Item.Amount))
                            .ThenBy(entry => entry.Item.AccountTime)
                            .Select(entry => entry.Index)
                            .ToList();

                        foreach (var index in movableIndexes)
                        {
                            if (sameSignDayCounts.GetValueOrDefault(denseDay.Key) <= maxPerDay)
                            {
                                break;
                            }

                            if (!TryMovePlanItemToSparseDay(
                                    request,
                                    references,
                                    constItems,
                                    items,
                                    index,
                                    sameSignDayCounts,
                                    totalDayCounts,
                                    pass + index))
                            {
                                continue;
                            }

                            changed = true;
                        }

                        if (sameSignDayCounts.GetValueOrDefault(denseDay.Key) > maxPerDay
                            && TryRemoveOptionalDenseSameDayPlanItems(
                                references,
                                constItems,
                                items,
                                denseDay.Key,
                                monthGroup.Key.Sign,
                                maxPerDay,
                                sameSignDayCounts,
                                totalDayCounts))
                        {
                            changed = true;
                        }
                    }
                }

                if (!changed)
                {
                    return;
                }
            }
        }

        private static int CalculateMaxSameDirectionPerDay(
            FlowAutoGenerationRequest request,
            DateTime month,
            int itemCount)
        {
            var start = request.Config.StartTime.Date;
            var end = NormalizeEndDate(request.Config.EndTime).Date;
            var monthStart = month < start ? start : month;
            var monthEndRaw = month.AddMonths(1).AddDays(-1);
            var monthEnd = monthEndRaw > end ? end : monthEndRaw;
            var coveredDays = Math.Max(1, (monthEnd - monthStart).Days + 1);
            return Math.Clamp((int)Math.Ceiling(itemCount / Math.Max(1d, coveredDays)), 2, 3);
        }

        private static bool TryRemoveOptionalDenseSameDayPlanItems(
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            DateTime date,
            int sign,
            int maxPerDay,
            Dictionary<DateTime, int> sameSignDayCounts,
            Dictionary<DateTime, int> totalDayCounts)
        {
            var excess = sameSignDayCounts.GetValueOrDefault(date) - maxPerDay;
            if (excess <= 0)
            {
                return false;
            }

            var removableIndexes = items
                .Select((item, index) => new { Item = item, Index = index })
                .Where(entry => entry.Item.AccountTime.Date == date
                    && GetAmountSign(entry.Item.Amount) == sign
                    && TryGetPlanRule(entry.Item, references, constItems, out var rule)
                    && !IsRequiredPlanRule(rule))
                .OrderBy(entry => Math.Abs(entry.Item.Amount))
                .ThenByDescending(entry => entry.Item.AccountTime)
                .Select(entry => entry.Index)
                .Take(excess)
                .OrderByDescending(index => index)
                .ToList();
            if (removableIndexes.Count == 0)
            {
                return false;
            }

            foreach (var index in removableIndexes)
            {
                items.RemoveAt(index);
                sameSignDayCounts[date] = Math.Max(0, sameSignDayCounts.GetValueOrDefault(date) - 1);
                totalDayCounts[date] = Math.Max(0, totalDayCounts.GetValueOrDefault(date) - 1);
            }

            return true;
        }

        private static bool TryMovePlanItemToSparseDay(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            int index,
            Dictionary<DateTime, int> sameSignDayCounts,
            Dictionary<DateTime, int> totalDayCounts,
            int sequence)
        {
            if (index < 0
                || index >= items.Count
                || !TryGetPlanRule(items[index], references, constItems, out var rule)
                || !TryGetAllowedRuleRange(request, rule, items[index].AccountTime, out var allowedStart, out var allowedEnd))
            {
                return false;
            }

            var original = items[index];
            var originalDate = original.AccountTime.Date;
            var candidateDates = EnumerateDates(allowedStart.Date, allowedEnd.Date)
                .Where(date => date != originalDate)
                .ToList();
            if (candidateDates.Count == 0)
            {
                return false;
            }

            var monthDates = sameSignDayCounts.Keys
                .Where(date => IsSameMonth(date, originalDate))
                .ToHashSet();
            var idealOffset = Math.Abs((sequence * 5 + original.SourceIndex * 3) % candidateDates.Count);
            foreach (var date in candidateDates
                         .Select((date, dateIndex) => new
                         {
                             Date = date,
                             Index = dateIndex,
                             SameSignCount = sameSignDayCounts.GetValueOrDefault(date),
                             TotalCount = totalDayCounts.GetValueOrDefault(date),
                             Adjacent = monthDates.Contains(date.AddDays(-1)) || monthDates.Contains(date.AddDays(1)),
                             Distance = Math.Abs((date - originalDate).Days)
                         })
                         .OrderBy(item => item.SameSignCount)
                         .ThenBy(item => item.Adjacent)
                         .ThenBy(item => item.TotalCount)
                         .ThenByDescending(item => item.Distance)
                         .ThenBy(item => Math.Abs(item.Index - idealOffset))
                         .ThenBy(item => item.Date)
                         .Take(12))
            {
                if (!TryCreatePlanItemTimeOnDate(
                        request,
                        rule,
                        items,
                        index,
                        date.Date,
                        sequence,
                        out var movedTime))
                {
                    continue;
                }

                items[index] = original with { AccountTime = movedTime };
                if (WouldPlanHaveNegativeBalance(request, items))
                {
                    items[index] = original;
                    continue;
                }

                sameSignDayCounts[originalDate] = Math.Max(0, sameSignDayCounts.GetValueOrDefault(originalDate) - 1);
                sameSignDayCounts[date.Date] = sameSignDayCounts.GetValueOrDefault(date.Date) + 1;
                totalDayCounts[originalDate] = Math.Max(0, totalDayCounts.GetValueOrDefault(originalDate) - 1);
                totalDayCounts[date.Date] = totalDayCounts.GetValueOrDefault(date.Date) + 1;
                return true;
            }

            return false;
        }

        private static bool TryCreatePlanItemTimeOnDate(
            FlowAutoGenerationRequest request,
            FlowRuleBase rule,
            IReadOnlyList<VendorPlanItem> items,
            int currentIndex,
            DateTime date,
            int sequence,
            out DateTime accountTime)
        {
            accountTime = default;
            if (!TryGetAllowedRuleRange(request, rule, date, out var allowedStart, out var allowedEnd))
            {
                return false;
            }

            var start = MaxDateTime(allowedStart, date.Date);
            var end = MinDateTime(allowedEnd, date.Date.AddDays(1).AddTicks(-1));
            if (start > end)
            {
                return false;
            }

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = TruncateToSecond(CreateRuleTimeInRange(request, rule, start, end, sequence + attempt * 13));
                if (candidate >= start
                    && candidate <= end
                    && !IsSecondOccupied(items, currentIndex, candidate))
                {
                    accountTime = candidate;
                    return true;
                }
            }

            return false;
        }

        private static void DeduplicatePlanItemAccountTimes(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items)
        {
            var maxPasses = IsWechatBank(request.Bank)
                ? Math.Min(Math.Max(1, items.Count / 120), 8)
                : Math.Min(Math.Max(1, items.Count), 180);
            for (var pass = 0; pass < maxPasses; pass++)
            {
                var duplicateGroups = items
                    .Select((item, index) => new PlanOrderEntry(index, item))
                    .GroupBy(entry => TruncateToSecond(entry.Item.AccountTime))
                    .Where(group => group.Count() > 1)
                    .OrderBy(group => group.Key)
                    .ToList();
                if (duplicateGroups.Count == 0)
                {
                    return;
                }

                var changed = false;
                foreach (var group in duplicateGroups)
                {
                    foreach (var entry in group
                                 .OrderBy(entry => IsPlanItemRequired(entry.Item, references, constItems))
                                 .ThenByDescending(entry => Math.Abs(entry.Item.Amount))
                                 .ThenBy(entry => entry.Index)
                                 .Skip(1)
                                 .ToList())
                    {
                        if (TryShiftDuplicatePlanItemTime(
                                request,
                                references,
                                constItems,
                                items,
                                entry.Index,
                                pass + entry.Index))
                        {
                            changed = true;
                        }
                    }
                }

                if (!changed)
                {
                    return;
                }
            }
        }

        private static bool TryShiftDuplicatePlanItemTime(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            int index,
            int sequence)
        {
            if (index < 0
                || index >= items.Count
                || !TryGetPlanRule(items[index], references, constItems, out var rule)
                || !TryGetAllowedRuleRange(request, rule, items[index].AccountTime, out var allowedStart, out var allowedEnd))
            {
                return false;
            }

            var original = items[index];
            var skipBalanceCheck = IsWechatBank(request.Bank);
            var date = original.AccountTime.Date;
            var start = MaxDateTime(allowedStart, date);
            var end = MinDateTime(allowedEnd, date.AddDays(1).AddTicks(-1));
            if (start > end)
            {
                return false;
            }

            foreach (var candidate in CreateNearbyUniqueTimeCandidates(original.AccountTime, start, end, sequence)
                         .Where(candidate => !IsSecondOccupied(items, index, candidate))
                         .Take(24))
            {
                items[index] = original with { AccountTime = candidate };
                if (skipBalanceCheck || !WouldPlanHaveNegativeBalance(request, items))
                {
                    return true;
                }
            }

            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = TruncateToSecond(CreateRuleTimeInRange(request, rule, start, end, sequence + attempt * 17));
                if (IsSecondOccupied(items, index, candidate))
                {
                    continue;
                }

                items[index] = original with { AccountTime = candidate };
                if (skipBalanceCheck || !WouldPlanHaveNegativeBalance(request, items))
                {
                    return true;
                }
            }

            items[index] = original;
            return false;
        }

        private static IEnumerable<DateTime> CreateNearbyUniqueTimeCandidates(
            DateTime original,
            DateTime start,
            DateTime end,
            int sequence)
        {
            for (var attempt = 1; attempt <= 90; attempt++)
            {
                var seconds = 17 + Math.Abs((sequence + attempt) * 13 % 211);
                var forward = TruncateToSecond(original.AddSeconds(seconds * attempt));
                if (forward >= start && forward <= end)
                {
                    yield return forward;
                }

                var backward = TruncateToSecond(original.AddSeconds(-seconds * attempt));
                if (backward >= start && backward <= end)
                {
                    yield return backward;
                }
            }
        }

        private static bool IsSecondOccupied(IReadOnlyList<VendorPlanItem> items, int currentIndex, DateTime value)
        {
            var second = TruncateToSecond(value);
            return items
                .Select((item, index) => new { Item = item, Index = index })
                .Any(entry => entry.Index != currentIndex && TruncateToSecond(entry.Item.AccountTime) == second);
        }

        private static DateTime TruncateToSecond(DateTime value)
        {
            return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));
        }

        private static IEnumerable<DateTime> EnumerateDates(DateTime start, DateTime end)
        {
            for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            {
                yield return date;
            }
        }

        private static List<PlanOrderEntry> GetOrderedPlanEntries(IReadOnlyList<VendorPlanItem> items)
        {
            return items
                .Select((item, index) => new PlanOrderEntry(index, item))
                .OrderBy(entry => entry.Item.AccountTime)
                .ThenBy(entry => entry.Index)
                .ToList();
        }

        private static IEnumerable<SameDirectionRun> FindSameDirectionRuns(IReadOnlyList<PlanOrderEntry> ordered)
        {
            for (var index = 0; index < ordered.Count;)
            {
                var currentSign = GetAmountSign(ordered[index].Item.Amount);
                if (currentSign == 0)
                {
                    index++;
                    continue;
                }

                var end = index + 1;
                while (end < ordered.Count && GetAmountSign(ordered[end].Item.Amount) == currentSign)
                {
                    end++;
                }

                if (end - index > 2)
                {
                    yield return new SameDirectionRun(index, end - 1, currentSign);
                }

                index = end;
            }
        }

        private static bool TryMoveOppositePlanItemIntoRun(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            IReadOnlyList<PlanOrderEntry> ordered,
            int runStart,
            int runEnd,
            int sign,
            int pass)
        {
            var gaps = Enumerable.Range(runStart + 1, Math.Max(0, runEnd - runStart - 1))
                .Select(insertAfter => new
                {
                    InsertAfter = insertAfter,
                    LeftTime = ordered[insertAfter].Item.AccountTime,
                    RightTime = ordered[insertAfter + 1].Item.AccountTime
                })
                .Where(gap => gap.RightTime > gap.LeftTime.AddSeconds(1))
                .OrderByDescending(gap => (gap.RightTime - gap.LeftTime).TotalMinutes)
                .ThenBy(gap => gap.InsertAfter)
                .ToList();

            foreach (var gap in gaps)
            {
                var candidates = ordered
                    .Select((entry, orderIndex) => new { Entry = entry, OrderIndex = orderIndex })
                    .Where(item => (item.OrderIndex < runStart || item.OrderIndex > runEnd)
                        && GetAmountSign(item.Entry.Item.Amount) == -sign
                        && TryGetPlanRule(item.Entry.Item, references, constItems, out _))
                    .OrderBy(item => item.OrderIndex > runEnd ? 0 : 1)
                    .ThenBy(item => Math.Abs(item.OrderIndex - gap.InsertAfter))
                    .ThenBy(item => Math.Abs(item.Entry.Item.Amount))
                    .ToList();

                foreach (var candidate in candidates)
                {
                    if (!TryGetPlanRule(candidate.Entry.Item, references, constItems, out var rule)
                        || !TryCreateInterleavedAccountTime(
                            request,
                            rule,
                            gap.LeftTime,
                            gap.RightTime,
                            pass + candidate.OrderIndex,
                            out var accountTime))
                    {
                        continue;
                    }

                    var original = items[candidate.Entry.Index];
                    items[candidate.Entry.Index] = original with { AccountTime = accountTime };
                    if (!WouldPlanHaveNegativeBalance(request, items))
                    {
                        return true;
                    }

                    items[candidate.Entry.Index] = original;
                }
            }

            return false;
        }

        private static bool TryInsertSmallOptionalExpenseIntoIncomeRun(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            IReadOnlyList<PlanOrderEntry> ordered,
            int runStart,
            int runEnd,
            int pass)
        {
            var candidates = references
                .Select((rule, index) => new { Rule = (FlowRuleBase)rule, SourceKind = 0, SourceIndex = index })
                .Where(candidate => IsRuleSigned(candidate.Rule, isIncome: false))
                .Select(candidate =>
                {
                    var range = GetRuleAmountRange(candidate.Rule);
                    return new
                    {
                        candidate.Rule,
                        candidate.SourceKind,
                        candidate.SourceIndex,
                        Amount = NormalizeSignedAmountByRule(-range.Min, candidate.Rule),
                        Required = IsRequiredPlanRule(candidate.Rule)
                    };
                })
                .Where(candidate => Math.Abs(candidate.Amount) <= 300d)
                .OrderBy(candidate => candidate.Required)
                .ThenBy(candidate => Math.Abs(candidate.Amount))
                .ThenBy(candidate => candidate.SourceIndex)
                .ToList();
            if (candidates.Count == 0)
            {
                return false;
            }

            var gaps = Enumerable.Range(runStart + 1, Math.Max(0, runEnd - runStart - 1))
                .Select(insertAfter => new
                {
                    InsertAfter = insertAfter,
                    LeftTime = ordered[insertAfter].Item.AccountTime,
                    RightTime = ordered[insertAfter + 1].Item.AccountTime
                })
                .Where(gap => gap.RightTime > gap.LeftTime.AddSeconds(1))
                .OrderByDescending(gap => (gap.RightTime - gap.LeftTime).TotalMinutes)
                .ThenBy(gap => gap.InsertAfter)
                .ToList();

            foreach (var gap in gaps)
            {
                foreach (var candidate in candidates)
                {
                    if (!TryCreateInterleavedAccountTime(
                            request,
                            candidate.Rule,
                            gap.LeftTime,
                            gap.RightTime,
                            pass + gap.InsertAfter + candidate.SourceIndex,
                            out var accountTime))
                    {
                        var fallbackStart = gap.LeftTime.AddSeconds(1);
                        var fallbackEnd = gap.RightTime.AddSeconds(-1);
                        if (fallbackStart > fallbackEnd)
                        {
                            continue;
                        }

                        accountTime = CreateRuleTimeInRange(
                            request,
                            candidate.Rule,
                            fallbackStart,
                            fallbackEnd,
                            pass + gap.InsertAfter + candidate.SourceIndex);
                    }

                    var planItem = new VendorPlanItem(
                        accountTime,
                        candidate.Amount,
                        candidate.SourceKind,
                        candidate.SourceIndex,
                        false);
                    items.Add(planItem);
                    if (!WouldPlanHaveNegativeBalance(request, items))
                    {
                        return true;
                    }

                    items.RemoveAt(items.Count - 1);
                }
            }

            return false;
        }

        private static bool TryInsertSmallIncomeIntoExpenseRun(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            IReadOnlyList<PlanOrderEntry> ordered,
            int runStart,
            int runEnd,
            int pass)
        {
            var currentDiff = GetProjectedFinalBalanceDiff(request, items);
            if (currentDiff > -250d)
            {
                return false;
            }

            var candidates = references
                .Select((rule, index) => new { Rule = (FlowRuleBase)rule, SourceKind = 0, SourceIndex = index })
                .Where(candidate => IsRuleSigned(candidate.Rule, isIncome: true)
                    && !IsRequiredPlanRule(candidate.Rule))
                .Select(candidate =>
                {
                    var range = GetRuleAmountRange(candidate.Rule);
                    return new
                    {
                        candidate.Rule,
                        candidate.SourceKind,
                        candidate.SourceIndex,
                        Amount = NormalizeSignedAmountByRule(range.Min, candidate.Rule),
                        Required = false
                    };
                })
                .Where(candidate => candidate.Amount <= 1200d
                    && currentDiff + candidate.Amount <= 800d)
                .OrderBy(candidate => candidate.Required)
                .ThenBy(candidate => candidate.Amount)
                .ThenBy(candidate => candidate.SourceIndex)
                .ToList();
            if (candidates.Count == 0)
            {
                return false;
            }

            var gaps = Enumerable.Range(runStart + 1, Math.Max(0, runEnd - runStart - 1))
                .Select(insertAfter => new
                {
                    InsertAfter = insertAfter,
                    LeftTime = ordered[insertAfter].Item.AccountTime,
                    RightTime = ordered[insertAfter + 1].Item.AccountTime
                })
                .Where(gap => gap.RightTime > gap.LeftTime.AddSeconds(1))
                .OrderByDescending(gap => (gap.RightTime - gap.LeftTime).TotalMinutes)
                .ThenBy(gap => gap.InsertAfter)
                .ToList();

            foreach (var gap in gaps)
            {
                foreach (var candidate in candidates)
                {
                    if (!TryCreateInterleavedAccountTime(
                            request,
                            candidate.Rule,
                            gap.LeftTime,
                            gap.RightTime,
                            pass + gap.InsertAfter + candidate.SourceIndex,
                            out var accountTime))
                    {
                        var fallbackStart = gap.LeftTime.AddSeconds(1);
                        var fallbackEnd = gap.RightTime.AddSeconds(-1);
                        if (fallbackStart > fallbackEnd)
                        {
                            continue;
                        }

                        accountTime = CreateRuleTimeInRange(
                            request,
                            candidate.Rule,
                            fallbackStart,
                            fallbackEnd,
                            pass + gap.InsertAfter + candidate.SourceIndex);
                    }

                    items.Add(new VendorPlanItem(
                        accountTime,
                        candidate.Amount,
                        candidate.SourceKind,
                        candidate.SourceIndex,
                        false));
                    if (!WouldPlanHaveNegativeBalance(request, items))
                    {
                        return true;
                    }

                    items.RemoveAt(items.Count - 1);
                }
            }

            return false;
        }

        private static bool TryMoveRunPlanItemAfterOpposite(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            IReadOnlyList<PlanOrderEntry> ordered,
            int runStart,
            int runEnd,
            int sign,
            int pass)
        {
            var opposite = ordered
                .Select((entry, orderIndex) => new { Entry = entry, OrderIndex = orderIndex })
                .Where(item => item.OrderIndex > runEnd
                    && GetAmountSign(item.Entry.Item.Amount) == -sign)
                .OrderBy(item => item.OrderIndex)
                .FirstOrDefault();
            if (opposite is null)
            {
                return false;
            }

            foreach (var mover in ordered
                         .Select((entry, orderIndex) => new { Entry = entry, OrderIndex = orderIndex })
                         .Where(item => item.OrderIndex >= runStart + 2
                             && item.OrderIndex <= runEnd
                             && TryGetPlanRule(item.Entry.Item, references, constItems, out _))
                         .OrderByDescending(item => item.OrderIndex)
                         .ToList())
            {
                if (!TryGetPlanRule(mover.Entry.Item, references, constItems, out var rule)
                    || !TryCreateAccountTimeAfterAnchor(
                        request,
                        rule,
                        opposite.Entry.Item.AccountTime,
                        pass + mover.OrderIndex,
                        out var accountTime))
                {
                    continue;
                }

                var original = items[mover.Entry.Index];
                items[mover.Entry.Index] = original with { AccountTime = accountTime };
                if (!WouldPlanHaveNegativeBalance(request, items))
                {
                    return true;
                }

                items[mover.Entry.Index] = original;
            }

            return false;
        }

        private static bool TryCreateInterleavedAccountTime(
            FlowAutoGenerationRequest request,
            FlowRuleBase rule,
            DateTime leftTime,
            DateTime rightTime,
            int sequence,
            out DateTime accountTime)
        {
            accountTime = default;

            foreach (var month in EnumerateMonthStarts(leftTime, rightTime))
            {
                if (!TryGetAllowedRuleRange(request, rule, month, out var allowedStart, out var allowedEnd))
                {
                    continue;
                }

                var start = MaxDateTime(allowedStart, leftTime.AddSeconds(1));
                var end = MinDateTime(allowedEnd, rightTime.AddSeconds(-1));
                if (start > end)
                {
                    continue;
                }

                accountTime = CreateRuleTimeInRange(request, rule, start, end, sequence);
                return true;
            }

            return false;
        }

        private static bool TryCreateAccountTimeAfterAnchor(
            FlowAutoGenerationRequest request,
            FlowRuleBase rule,
            DateTime anchorTime,
            int sequence,
            out DateTime accountTime)
        {
            accountTime = default;
            if (!TryGetAllowedRuleRange(request, rule, anchorTime, out var allowedStart, out var allowedEnd))
            {
                return false;
            }

            var start = MaxDateTime(allowedStart, anchorTime.AddSeconds(1));
            if (start > allowedEnd)
            {
                return false;
            }

            accountTime = CreateRuleTimeInRange(request, rule, start, allowedEnd, sequence);
            return true;
        }

        private static DateTime CreateTimeInWindow(DateTime start, DateTime end, int sequence)
        {
            var seconds = Math.Max(0, (int)Math.Min(int.MaxValue, (end - start).TotalSeconds));
            if (seconds == 0)
            {
                return start;
            }

            var offset = Math.Abs((sequence * 211 + 37) % (seconds + 1));
            return start.AddSeconds(offset);
        }

        private static DateTime CreateRuleTimeInRange(
            FlowAutoGenerationRequest request,
            FlowRuleBase rule,
            DateTime start,
            DateTime end,
            int sequence)
        {
            if (end < start)
            {
                (start, end) = (end, start);
            }

            var firstDate = start.Date;
            var lastDate = end.Date;
            var dayCount = Math.Max(1, (lastDate - firstDate).Days + 1);
            var (startHour, endHour) = GetRuleHourRange(rule);

            for (var attempt = 0; attempt < Math.Min(dayCount + 6, 40); attempt++)
            {
                var offset = Math.Abs((sequence * 17 + attempt * 7 + 3) % dayCount);
                var date = firstDate.AddDays(offset);
                var dayStart = MaxDateTime(start, date.AddHours(startHour));
                var dayEnd = MinDateTime(end, date.AddHours(endHour).AddMinutes(59).AddSeconds(59));
                if (dayStart > dayEnd)
                {
                    continue;
                }

                return CreateTimeInWindow(dayStart, dayEnd, sequence + attempt * 31);
            }

            var fallbackStart = MaxDateTime(start, request.Config.StartTime);
            var fallbackEnd = MinDateTime(end, NormalizeEndDate(request.Config.EndTime));
            return fallbackStart <= fallbackEnd ? fallbackStart : start;
        }

        private static (int StartHour, int EndHour) GetRuleHourRange(FlowRuleBase rule)
        {
            var startHour = Math.Clamp(rule.StartDay ?? 9, 0, 23);
            var endHour = Math.Clamp(rule.EndDay ?? 17, 0, 23);
            if (endHour < startHour)
            {
                (startHour, endHour) = (endHour, startHour);
            }

            return (startHour, endHour);
        }

        private static bool WouldPlanHaveNegativeBalance(
            FlowAutoGenerationRequest request,
            IEnumerable<VendorPlanItem> items)
        {
            var balance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
            foreach (var item in items.OrderBy(item => item.AccountTime))
            {
                balance = RoundMoney(balance + RoundMoney(item.Amount));
                if (balance < -0.009d)
                {
                    return true;
                }
            }

            return false;
        }

        private static int GetAmountSign(double amount)
        {
            if (amount > 0.009d)
            {
                return 1;
            }

            return amount < -0.009d ? -1 : 0;
        }

        private static double GetProjectedFinalBalanceDiff(
            FlowAutoGenerationRequest request,
            IEnumerable<VendorPlanItem> items)
        {
            return RoundMoney(items.Sum(item => RoundMoney(item.Amount)) - request.Config.LastMoney);
        }

        private static void MoveMonthStartReferenceExpensesAfterIncome(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            List<VendorPlanItem> items)
        {
            foreach (var month in items
                         .GroupBy(item => new DateTime(item.AccountTime.Year, item.AccountTime.Month, 1))
                         .ToList())
            {
                var firstIncome = month
                    .Where(item => item.Amount > 0.009d)
                    .Select(item => (DateTime?)item.AccountTime)
                    .Min();
                if (!firstIncome.HasValue)
                {
                    continue;
                }

                var sequence = 0;
                foreach (var entry in items
                             .Select((item, index) => new { Item = item, Index = index })
                             .Where(entry => entry.Item.Amount < -0.009d
                                 && entry.Item.AccountTime < firstIncome.Value
                                 && IsSameMonth(entry.Item.AccountTime, month.Key)
                                 && TryGetReferencePlanRule(entry.Item, references, out _))
                             .OrderBy(entry => entry.Item.AccountTime)
                             .ThenByDescending(entry => Math.Abs(entry.Item.Amount))
                             .ToList())
                {
                    if (!TryGetReferencePlanRule(entry.Item, references, out var rule))
                    {
                        continue;
                    }

                    if (TryCreateDeferredReferenceExpenseTime(
                            request,
                            rule,
                            entry.Item.AccountTime,
                            firstIncome.Value,
                            items,
                            sequence++,
                            out var deferredTime))
                    {
                        items[entry.Index] = entry.Item with { AccountTime = deferredTime };
                    }
                }
            }
        }

        private static bool TryFindFirstNegativeExpense(
            FlowAutoGenerationRequest request,
            IReadOnlyList<VendorPlanItem> items,
            out int problemIndex,
            out DateTime problemTime)
        {
            problemIndex = -1;
            problemTime = default;
            var balance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
            var lastExpenseIndex = -1;
            foreach (var entry in items
                         .Select((item, index) => new { Item = item, Index = index })
                         .OrderBy(entry => entry.Item.AccountTime)
                         .ThenBy(entry => entry.Index))
            {
                if (entry.Item.Amount < -0.009d)
                {
                    lastExpenseIndex = entry.Index;
                }

                balance = RoundMoney(balance + RoundMoney(entry.Item.Amount));
                if (balance >= -0.009d)
                {
                    continue;
                }

                problemIndex = entry.Item.Amount < -0.009d ? entry.Index : lastExpenseIndex;
                problemTime = entry.Item.AccountTime;
                return problemIndex >= 0;
            }

            return false;
        }

        private static bool TryDeferReferenceExpenseBeforeProblem(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            int problemIndex,
            DateTime problemTime,
            int pass)
        {
            var candidates = items
                .Select((item, index) => new { Item = item, Index = index })
                .Where(entry => entry.Item.Amount < -0.009d
                    && entry.Item.AccountTime <= problemTime
                    && IsSameMonth(entry.Item.AccountTime, problemTime)
                    && TryGetReferencePlanRule(entry.Item, references, out _))
                .OrderBy(entry => entry.Index == problemIndex ? 0 : 1)
                .ThenBy(entry => IsPlanItemRequired(entry.Item, references, constItems))
                .ThenByDescending(entry => Math.Abs(entry.Item.Amount))
                .ThenBy(entry => entry.Item.AccountTime)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (!TryGetReferencePlanRule(candidate.Item, references, out var rule)
                    || !TryGetAllowedReferenceRange(request, rule, candidate.Item.AccountTime, out _, out var allowedEnd))
                {
                    continue;
                }

                var anchor = items
                    .Where(item => item.Amount > 0.009d
                        && IsSameMonth(item.AccountTime, candidate.Item.AccountTime)
                        && item.AccountTime <= allowedEnd
                        && item.AccountTime > candidate.Item.AccountTime)
                    .Select(item => (DateTime?)item.AccountTime)
                    .Max();
                if (!anchor.HasValue)
                {
                    continue;
                }

                if (TryCreateDeferredReferenceExpenseTime(
                        request,
                        rule,
                        candidate.Item.AccountTime,
                        anchor.Value,
                        items,
                        pass + candidate.Index,
                        out var deferredTime))
                {
                    items[candidate.Index] = candidate.Item with { AccountTime = deferredTime };
                    return true;
                }
            }

            return false;
        }

        private static bool TryCreateDeferredReferenceExpenseTime(
            FlowAutoGenerationRequest request,
            GenerateReferenceRule rule,
            DateTime originalTime,
            DateTime anchorTime,
            IReadOnlyList<VendorPlanItem> items,
            int sequence,
            out DateTime deferredTime)
        {
            deferredTime = originalTime;
            if (!TryGetAllowedReferenceRange(request, rule, originalTime, out var allowedStart, out var allowedEnd)
                || anchorTime >= allowedEnd)
            {
                return false;
            }

            var firstDate = anchorTime.Date > allowedStart.Date ? anchorTime.Date : allowedStart.Date;
            var lastDate = allowedEnd.Date;
            if (firstDate > lastDate)
            {
                return false;
            }

            var usedDays = items
                .Where(item => IsSameMonth(item.AccountTime, originalTime))
                .GroupBy(item => item.AccountTime.Day)
                .ToDictionary(group => group.Key, group => group.Count());
            var candidateDates = Enumerable.Range(0, (lastDate - firstDate).Days + 1)
                .Select(offset => firstDate.AddDays(offset))
                .Where(date => date > anchorTime.Date || date.AddDays(1).AddTicks(-1) > anchorTime)
                .ToList();
            if (candidateDates.Count == 0)
            {
                return false;
            }

            var idealOffset = Math.Abs((sequence * 3 + rule.Index) % candidateDates.Count);
            var date = candidateDates
                .Select((candidate, index) => new
                {
                    Date = candidate,
                    Index = index,
                    Count = usedDays.GetValueOrDefault(candidate.Day),
                    Adjacent = usedDays.ContainsKey(candidate.Day - 1) || usedDays.ContainsKey(candidate.Day + 1)
                })
                .OrderBy(item => item.Count)
                .ThenBy(item => item.Adjacent)
                .ThenBy(item => Math.Abs(item.Index - idealOffset))
                .ThenBy(item => item.Date)
                .First()
                .Date;

            var candidateTime = date
                .AddHours(9 + Math.Abs((sequence * 5 + rule.Index) % 10))
                .AddMinutes(Math.Abs((sequence * 17 + rule.Index * 7) % 60))
                .AddSeconds(Math.Abs((sequence * 23 + rule.Index * 11) % 60));
            if (candidateTime < allowedStart)
            {
                candidateTime = allowedStart.AddMinutes(5 + sequence % 30);
            }

            if (candidateTime <= anchorTime)
            {
                candidateTime = anchorTime.AddMinutes(5 + sequence % 45);
            }

            if (candidateTime > allowedEnd)
            {
                candidateTime = allowedEnd.AddMinutes(-Math.Min(60, 5 + sequence % 45));
            }

            if (candidateTime <= anchorTime || candidateTime <= originalTime || candidateTime < allowedStart || candidateTime > allowedEnd)
            {
                return false;
            }

            deferredTime = candidateTime;
            return true;
        }

        private static bool TryAdvanceIncomeBeforeProblem(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            DateTime problemTime,
            int pass)
        {
            var candidates = items
                .Select((item, index) => new { Item = item, Index = index })
                .Where(entry => entry.Item.Amount > 0.009d
                    && entry.Item.AccountTime > problemTime
                    && TryGetPlanRule(entry.Item, references, constItems, out _))
                .OrderBy(entry => IsSameMonth(entry.Item.AccountTime, problemTime) ? 0 : 1)
                .ThenBy(entry => entry.Item.AccountTime)
                .ThenByDescending(entry => entry.Item.Amount)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (!TryGetPlanRule(candidate.Item, references, constItems, out var rule))
                {
                    continue;
                }

                if (TryCreateAdvancedIncomeTime(
                        request,
                        rule,
                        problemTime,
                        items,
                        pass + candidate.Index,
                        out var advancedTime))
                {
                    items[candidate.Index] = candidate.Item with { AccountTime = advancedTime };
                    return true;
                }
            }

            return false;
        }

        private static bool TryCreateAdvancedIncomeTime(
            FlowAutoGenerationRequest request,
            FlowRuleBase rule,
            DateTime problemTime,
            IReadOnlyList<VendorPlanItem> items,
            int sequence,
            out DateTime advancedTime)
        {
            advancedTime = default;
            if (!TryGetAdvanceIncomeRangeBeforeProblem(request, rule, problemTime, out var allowedStart, out var allowedEnd))
            {
                return false;
            }

            var firstDate = allowedStart.Date;
            var lastDate = allowedEnd.Date;
            var usedDays = items
                .Where(item => item.AccountTime.Date >= firstDate && item.AccountTime.Date <= lastDate)
                .GroupBy(item => item.AccountTime.Day)
                .ToDictionary(group => group.Key, group => group.Count());
            var candidateDates = Enumerable.Range(0, (lastDate - firstDate).Days + 1)
                .Select(offset => firstDate.AddDays(offset))
                .Where(date => date >= allowedStart.Date && date <= allowedEnd.Date)
                .ToList();
            if (candidateDates.Count == 0)
            {
                return false;
            }

            var date = candidateDates
                .Select((candidate, index) => new
                {
                    Date = candidate,
                    Index = index,
                    Count = usedDays.GetValueOrDefault(candidate.Day)
                })
                .OrderBy(item => item.Count)
                .ThenByDescending(item => item.Date)
                .ThenBy(item => Math.Abs(item.Index - Math.Abs(sequence % candidateDates.Count)))
                .First()
                .Date;

            var candidateTime = date
                .AddHours(9 + Math.Abs((sequence * 3 + rule.Index) % 8))
                .AddMinutes(Math.Abs((sequence * 19 + rule.Index * 5) % 60))
                .AddSeconds(Math.Abs((sequence * 29 + rule.Index * 7) % 60));
            if (candidateTime < allowedStart)
            {
                candidateTime = allowedStart.AddMinutes(3 + sequence % 20);
            }

            if (candidateTime > allowedEnd)
            {
                candidateTime = allowedEnd.AddMinutes(-Math.Min(30, 3 + sequence % 20));
            }

            if (candidateTime < allowedStart || candidateTime > allowedEnd || candidateTime >= problemTime)
            {
                return false;
            }

            advancedTime = candidateTime;
            return true;
        }

        private static bool TryReduceOrRemoveOptionalExpenseBeforeProblem(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            int problemIndex)
        {
            if (problemIndex < 0 || problemIndex >= items.Count)
            {
                return false;
            }

            var item = items[problemIndex];
            if (item.Amount >= -0.009d
                || !TryGetPlanRule(item, references, constItems, out var rule)
                || IsRequiredPlanRule(rule))
            {
                return false;
            }

            var balanceBefore = RoundMoney(GetBalanceBeforePlanItem(request, items, problemIndex));
            var current = Math.Abs(item.Amount);
            if (balanceBefore >= current - 0.009d)
            {
                return false;
            }

            var (min, _) = GetRuleAmountRange(rule);
            var allowed = RoundAmountDownByFloutLength(Math.Max(0, balanceBefore), rule.FloutLength);
            if (allowed >= min - 0.009d && allowed > 0.009d)
            {
                allowed = Math.Min(current, allowed);
                if (allowed < current - 0.009d)
                {
                    items[problemIndex] = item with { Amount = -allowed };
                    return true;
                }
            }

            items.RemoveAt(problemIndex);
            return true;
        }

        private static double GetBalanceBeforePlanItem(
            FlowAutoGenerationRequest request,
            IReadOnlyList<VendorPlanItem> items,
            int targetIndex)
        {
            var balance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
            foreach (var entry in items
                         .Select((item, index) => new { Item = item, Index = index })
                         .OrderBy(entry => entry.Item.AccountTime)
                         .ThenBy(entry => entry.Index))
            {
                if (entry.Index == targetIndex)
                {
                    return balance;
                }

                balance = RoundMoney(balance + RoundMoney(entry.Item.Amount));
            }

            return balance;
        }

        private static void RestoreExpenseTotalSafely(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items)
        {
            var targetExpense = CalculateTargetExpenseFromIncome(request, SumPlanTotal(items, isIncome: true));
            var maxPasses = IsWechatBank(request.Bank) ? 3 : 12;
            for (var pass = 0; pass < maxPasses; pass++)
            {
                var diff = RoundMoney(targetExpense - SumPlanTotal(items, isIncome: false));
                if (diff <= 0.009d || diff <= 500d)
                {
                    return;
                }

                var changed = TryIncreaseSafeOptionalExpenses(request, references, constItems, items, ref diff);
                if (diff <= 0.009d || diff <= 500d)
                {
                    return;
                }

                changed = TryAddSafeOptionalExpenseItem(request, references, constItems, items, ref diff, pass) || changed;
                if (!changed)
                {
                    return;
                }
            }
        }

        private static bool TryIncreaseSafeOptionalExpenses(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            ref double remaining)
        {
            var changed = false;
            foreach (var index in items
                         .Select((item, index) => new { Item = item, Index = index })
                         .Where(entry => entry.Item.Amount < -0.009d
                             && TryGetPlanRule(entry.Item, references, constItems, out var rule)
                             && !IsRequiredPlanRule(rule))
                         .OrderByDescending(entry => GetSafeExpenseIncreaseCapacityForItem(request, items, entry.Index))
                         .ThenByDescending(entry => entry.Item.AccountTime)
                         .Select(entry => entry.Index)
                         .ToList())
            {
                if (remaining <= 0.009d)
                {
                    break;
                }

                var item = items[index];
                if (!TryGetPlanRule(item, references, constItems, out var rule))
                {
                    continue;
                }

                var (_, max) = GetRuleAmountRange(rule);
                var current = Math.Abs(item.Amount);
                var ruleCapacity = RoundMoney(max - current);
                var safeCapacity = GetSafeExpenseIncreaseCapacityForItem(request, items, index);
                var increase = Math.Min(remaining, Math.Min(ruleCapacity, safeCapacity));
                if (increase <= 0.009d)
                {
                    continue;
                }

                var adjusted = RoundAmountDownByFloutLength(current + increase, rule.FloutLength);
                adjusted = Math.Min(max, adjusted);
                if (adjusted <= current + 0.009d)
                {
                    continue;
                }

                items[index] = item with { Amount = -adjusted };
                remaining = RoundMoney(remaining - (adjusted - current));
                changed = true;
            }

            return changed;
        }

        private static bool TryAddSafeOptionalExpenseItem(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            ref double remaining,
            int pass)
        {
            var candidates = references
                .Select((rule, index) => new { Rule = (FlowRuleBase)rule, SourceKind = 0, SourceIndex = index })
                .Where(candidate => IsRuleSigned(candidate.Rule, isIncome: false)
                    && !IsRequiredPlanRule(candidate.Rule))
                .OrderByDescending(candidate => GetRuleAmountRange(candidate.Rule).Max)
                .ThenBy(candidate => GetRuleAmountRange(candidate.Rule).Min)
                .ToList();
            if (candidates.Count == 0)
            {
                return false;
            }

            var months = EnumerateMonthStarts(request).Reverse().ToList();
            foreach (var candidate in candidates)
            {
                var (min, max) = GetRuleAmountRange(candidate.Rule);
                foreach (var month in months)
                {
                    if (!TryGetAllowedRuleRange(request, candidate.Rule, month, out var allowedStart, out var allowedEnd))
                    {
                        continue;
                    }

                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        var accountTime = CreateSafeExpenseAccountTime(allowedStart, allowedEnd, pass + attempt + candidate.SourceIndex);
                        var safeCapacity = GetSafeExpenseCapacityAtTime(request, items, accountTime);
                        var amount = Math.Min(remaining, Math.Min(max, safeCapacity));
                        amount = RoundAmountDownByFloutLength(amount, candidate.Rule.FloutLength);
                        if (amount < min - 0.009d || amount <= 0.009d)
                        {
                            continue;
                        }

                        items.Add(new VendorPlanItem(
                            accountTime,
                            -amount,
                            candidate.SourceKind,
                            candidate.SourceIndex,
                            false));
                        remaining = RoundMoney(remaining - amount);
                        return true;
                    }
                }
            }

            return false;
        }

        private static double GetSafeExpenseIncreaseCapacityForItem(
            FlowAutoGenerationRequest request,
            IReadOnlyList<VendorPlanItem> items,
            int targetIndex)
        {
            var balance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
            var found = false;
            var minAfter = double.PositiveInfinity;
            foreach (var entry in items
                         .Select((item, index) => new { Item = item, Index = index })
                         .OrderBy(entry => entry.Item.AccountTime)
                         .ThenBy(entry => entry.Index))
            {
                balance = RoundMoney(balance + RoundMoney(entry.Item.Amount));
                if (entry.Index == targetIndex)
                {
                    found = true;
                }

                if (found)
                {
                    minAfter = Math.Min(minAfter, balance);
                }
            }

            return double.IsPositiveInfinity(minAfter)
                ? 0
                : RoundMoney(Math.Max(0, minAfter));
        }

        private static double GetSafeExpenseCapacityAtTime(
            FlowAutoGenerationRequest request,
            IReadOnlyList<VendorPlanItem> items,
            DateTime accountTime)
        {
            var balance = RoundMoney(request.OpeningBalanceOverride ?? request.Config.OpeningBalance);
            var inserted = false;
            var minAfter = double.PositiveInfinity;
            foreach (var entry in items
                         .Select((item, index) => new { Item = item, Index = index })
                         .OrderBy(entry => entry.Item.AccountTime)
                         .ThenBy(entry => entry.Index))
            {
                if (!inserted && entry.Item.AccountTime >= accountTime)
                {
                    inserted = true;
                    minAfter = Math.Min(minAfter, balance);
                }

                balance = RoundMoney(balance + RoundMoney(entry.Item.Amount));
                if (inserted)
                {
                    minAfter = Math.Min(minAfter, balance);
                }
            }

            if (!inserted)
            {
                minAfter = balance;
            }

            return RoundMoney(Math.Max(0, minAfter));
        }

        private static DateTime CreateSafeExpenseAccountTime(DateTime allowedStart, DateTime allowedEnd, int sequence)
        {
            var windowMinutes = Math.Max(1, (int)Math.Min(int.MaxValue, (allowedEnd - allowedStart).TotalMinutes));
            var offset = Math.Abs((sequence * 37 + 11) % windowMinutes);
            var accountTime = allowedEnd.AddMinutes(-offset).AddSeconds(-Math.Abs(sequence * 13 % 60));
            if (accountTime < allowedStart)
            {
                accountTime = allowedStart.AddMinutes(Math.Min(windowMinutes - 1, Math.Abs(sequence * 17 % windowMinutes)));
            }

            return accountTime > allowedEnd ? allowedEnd : accountTime;
        }

        private static IEnumerable<DateTime> EnumerateMonthStarts(FlowAutoGenerationRequest request)
        {
            var start = request.Config.StartTime.Date;
            var end = NormalizeEndDate(request.Config.EndTime).Date;
            if (end < start)
            {
                (start, end) = (end, start);
            }

            var cursor = new DateTime(start.Year, start.Month, 1);
            while (cursor <= end)
            {
                yield return cursor;
                cursor = cursor.AddMonths(1);
            }
        }

        private static IEnumerable<DateTime> EnumerateMonthStarts(DateTime start, DateTime end)
        {
            start = start.Date;
            end = end.Date;
            if (end < start)
            {
                (start, end) = (end, start);
            }

            var cursor = new DateTime(start.Year, start.Month, 1);
            var endMonth = new DateTime(end.Year, end.Month, 1);
            while (cursor <= endMonth)
            {
                yield return cursor;
                cursor = cursor.AddMonths(1);
            }
        }

        private static bool TryGetAdvanceIncomeRangeBeforeProblem(
            FlowAutoGenerationRequest request,
            FlowRuleBase rule,
            DateTime problemTime,
            out DateTime allowedStart,
            out DateTime allowedEnd)
        {
            var firstMonth = new DateTime(request.Config.StartTime.Year, request.Config.StartTime.Month, 1);
            var cursor = new DateTime(problemTime.Year, problemTime.Month, 1);
            while (cursor >= firstMonth)
            {
                if (TryGetAllowedRuleRange(request, rule, cursor, out var start, out var end))
                {
                    end = MinDateTime(end, problemTime.AddSeconds(-1));
                    if (start <= end)
                    {
                        allowedStart = start;
                        allowedEnd = end;
                        return true;
                    }
                }

                cursor = cursor.AddMonths(-1);
            }

            allowedStart = default;
            allowedEnd = default;
            return false;
        }

        private static bool TryGetAllowedRuleRange(
            FlowAutoGenerationRequest request,
            FlowRuleBase rule,
            DateTime monthTime,
            out DateTime allowedStart,
            out DateTime allowedEnd)
        {
            return rule switch
            {
                GenerateReferenceRule reference => TryGetAllowedReferenceRange(request, reference, monthTime, out allowedStart, out allowedEnd),
                GenerateConstRule constRule => TryGetAllowedConstRange(request, constRule, monthTime, out allowedStart, out allowedEnd),
                _ => TryGetAllowedFlowRuleRange(request, rule, monthTime, out allowedStart, out allowedEnd)
            };
        }

        private static bool TryGetAllowedConstRange(
            FlowAutoGenerationRequest request,
            GenerateConstRule rule,
            DateTime monthTime,
            out DateTime allowedStart,
            out DateTime allowedEnd)
        {
            if (TryParseFixDays(rule.FixDay).Any())
            {
                var days = TryParseFixDays(rule.FixDay).ToList();
                var daysInMonth = DateTime.DaysInMonth(monthTime.Year, monthTime.Month);
                var candidates = days
                    .Select(day => Math.Clamp(day, 1, daysInMonth))
                    .Distinct()
                    .Select(day => new DateTime(monthTime.Year, monthTime.Month, day))
                    .OrderBy(date => date)
                    .ToList();
                if (candidates.Count == 0)
                {
                    allowedStart = default;
                    allowedEnd = default;
                    return false;
                }

                var (startHour, endHour) = GetRuleHourRange(rule);
                allowedStart = candidates.First().AddHours(startHour);
                allowedEnd = candidates.Last().AddHours(endHour).AddMinutes(59).AddSeconds(59);
            }
            else if (!TryGetAllowedFlowRuleRange(request, rule, monthTime, out allowedStart, out allowedEnd))
            {
                return false;
            }

            if (allowedStart < request.Config.StartTime)
            {
                allowedStart = request.Config.StartTime;
            }

            var requestEnd = NormalizeEndDate(request.Config.EndTime);
            if (allowedEnd > requestEnd)
            {
                allowedEnd = requestEnd;
            }

            return allowedStart <= allowedEnd;
        }

        private static IEnumerable<int> TryParseFixDays(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            foreach (var token in value.Split([',', ';', '|', '/', '\\', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(token, out var day) && day > 0)
                {
                    yield return day;
                }
            }
        }

        private static bool TryGetAllowedFlowRuleRange(
            FlowAutoGenerationRequest request,
            FlowRuleBase rule,
            DateTime monthTime,
            out DateTime allowedStart,
            out DateTime allowedEnd)
        {
            var (startHour, endHour) = GetRuleHourRange(rule);
            allowedStart = new DateTime(monthTime.Year, monthTime.Month, 1).AddHours(startHour);
            allowedEnd = new DateTime(monthTime.Year, monthTime.Month, DateTime.DaysInMonth(monthTime.Year, monthTime.Month))
                .AddHours(endHour)
                .AddMinutes(59)
                .AddSeconds(59);
            if (allowedStart < request.Config.StartTime)
            {
                allowedStart = request.Config.StartTime;
            }

            var requestEnd = NormalizeEndDate(request.Config.EndTime);
            if (allowedEnd > requestEnd)
            {
                allowedEnd = requestEnd;
            }

            return allowedStart <= allowedEnd;
        }

        private static DateTime MinDateTime(DateTime left, DateTime right)
        {
            return left <= right ? left : right;
        }

        private static DateTime MaxDateTime(DateTime left, DateTime right)
        {
            return left >= right ? left : right;
        }

        private static bool TryGetAllowedReferenceRange(
            FlowAutoGenerationRequest request,
            GenerateReferenceRule rule,
            DateTime monthTime,
            out DateTime allowedStart,
            out DateTime allowedEnd)
        {
            return TryGetAllowedFlowRuleRange(request, rule, monthTime, out allowedStart, out allowedEnd);
        }

        private static bool TryGetReferencePlanRule(
            VendorPlanItem item,
            IReadOnlyList<GenerateReferenceRule> references,
            out GenerateReferenceRule rule)
        {
            if (TryResolveReferenceRuleIndex(item, references, out _, out rule))
            {
                return true;
            }

            rule = null!;
            return false;
        }

        private static bool IsSameMonth(DateTime left, DateTime right)
        {
            return left.Year == right.Year && left.Month == right.Month;
        }

        private static double CalculateTargetExpense(FlowAutoGenerationRequest request)
        {
            return RoundMoney(Math.Max(0, request.Config.AllInMoney - request.Config.LastMoney));
        }

        private static double CalculateTargetExpenseFromIncome(FlowAutoGenerationRequest request, double incomeTotal)
        {
            return RoundMoney(Math.Max(0, incomeTotal - request.Config.LastMoney));
        }

        private static void NormalizeSignedPlanTotal(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            bool isIncome,
            double targetTotal)
        {
            TrimOptionalPlanItems(references, constItems, items, isIncome, targetTotal);
            var total = SumPlanTotal(items, isIncome);
            var diff = RoundMoney(targetTotal - total);
            if (Math.Abs(diff) <= 0.009d)
            {
                return;
            }

            if (diff > 0)
            {
                IncreasePlanTotal(references, constItems, items, isIncome, diff);
                diff = RoundMoney(targetTotal - SumPlanTotal(items, isIncome));
                if (diff > 0.009d)
                {
                    AddOptionalPlanItems(request, references, constItems, items, isIncome, diff);
                }
            }
            else
            {
                DecreasePlanTotal(references, constItems, items, isIncome, Math.Abs(diff));
            }
        }

        private static void TrimOptionalPlanItems(
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            bool isIncome,
            double targetTotal)
        {
            var excess = RoundMoney(SumPlanTotal(items, isIncome) - targetTotal);
            if (excess <= 0.009d)
            {
                return;
            }

            while (excess > 0.009d)
            {
                var candidates = items
                    .Select((item, index) => new { Item = item, Index = index })
                    .Select(entry => TryGetPlanRule(entry.Item, references, constItems, out var rule)
                        ? new
                        {
                            entry.Item,
                            entry.Index,
                            Rule = rule,
                            HasRule = true
                        }
                        : new
                        {
                            entry.Item,
                            entry.Index,
                            Rule = (FlowRuleBase)null!,
                            HasRule = false
                        })
                    .Where(entry => entry.HasRule
                        && IsPlanItemSigned(entry.Item, isIncome)
                        && !IsRequiredPlanRule(entry.Rule)
                        && Math.Abs(entry.Item.Amount) <= excess + 0.009d)
                    .Select(entry => new PlanRemovalCandidate(entry.Item, entry.Index, isIncome && entry.Rule is GenerateConstRule))
                    .ToList();
                PlanRemovalCandidate? removable = isIncome
                    ? SelectDistributedIncomeRemovalCandidate(candidates, excess)
                    : candidates.Count == 0
                        ? null
                        : candidates
                        .OrderByDescending(entry => entry.Item.AccountTime)
                        .ThenByDescending(entry => entry.Index)
                        .First();
                if (!removable.HasValue)
                {
                    break;
                }

                var selected = removable.Value;
                var amount = Math.Abs(selected.Item.Amount);
                items.RemoveAt(selected.Index);
                excess = RoundMoney(excess - amount);
            }

            if (excess <= 0.009d)
            {
                return;
            }

            var overshoot = items
                .Select((item, index) => new { Item = item, Index = index })
                .Where(entry => IsPlanItemSigned(entry.Item, isIncome)
                    && TryGetPlanRule(entry.Item, references, constItems, out var rule)
                    && !IsRequiredPlanRule(rule)
                    && Math.Abs(entry.Item.Amount) > excess + 0.009d)
                .OrderBy(entry => Math.Abs(entry.Item.Amount))
                .ThenBy(entry => isIncome ? 0 : -entry.Item.AccountTime.Ticks)
                .ThenBy(entry => isIncome ? entry.Index : -entry.Index)
                .FirstOrDefault();
            if (overshoot is not null)
            {
                items.RemoveAt(overshoot.Index);
            }
        }

        private static PlanRemovalCandidate? SelectDistributedIncomeRemovalCandidate(
            IReadOnlyList<PlanRemovalCandidate> candidates,
            double excess)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            var nonFixed = candidates
                .Where(candidate => !candidate.IsFixedIncome)
                .OrderByDescending(candidate => Math.Abs(candidate.Item.Amount))
                .ThenBy(candidate => candidate.Item.AccountTime)
                .FirstOrDefault();
            if (nonFixed.Item is not null)
            {
                return nonFixed;
            }

            var ordered = candidates
                .OrderBy(candidate => candidate.Item.AccountTime)
                .ThenBy(candidate => candidate.Index)
                .ToList();
            var average = ordered.Average(candidate => Math.Abs(candidate.Item.Amount));
            var removeCount = Math.Clamp(
                (int)Math.Ceiling(excess / Math.Max(0.01d, average)),
                1,
                ordered.Count);
            var keepCount = Math.Max(1, ordered.Count - removeCount);

            return ordered
                .Select((candidate, ordinal) => new
                {
                    Candidate = candidate,
                    Score = CalculateSpreadRemovalScore(ordinal, ordered.Count, keepCount)
                })
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Candidate.Item.AccountTime)
                .Select(candidate => candidate.Candidate)
                .First();
        }

        private static double CalculateSpreadRemovalScore(int ordinal, int count, int keepCount)
        {
            if (count <= 1)
            {
                return 0;
            }

            if (keepCount <= 1)
            {
                return Math.Abs(ordinal - ((count - 1) / 2d));
            }

            var bestDistance = double.PositiveInfinity;
            for (var slot = 0; slot < keepCount; slot++)
            {
                var ideal = slot * (count - 1d) / (keepCount - 1d);
                bestDistance = Math.Min(bestDistance, Math.Abs(ordinal - ideal));
            }

            return bestDistance;
        }

        private static void IncreasePlanTotal(
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            bool isIncome,
            double increase)
        {
            foreach (var index in GetAdjustablePlanItemIndexes(references, constItems, items, isIncome, preferLargeAmount: false))
            {
                if (increase <= 0.009d)
                {
                    return;
                }

                var item = items[index];
                if (!TryGetPlanRule(item, references, constItems, out var rule))
                {
                    continue;
                }

                var (_, max) = GetRuleAmountRange(rule);
                var current = Math.Abs(item.Amount);
                var capacity = RoundMoney(max - current);
                if (capacity <= 0.009d)
                {
                    continue;
                }

                var adjusted = RoundAmountDownByFloutLength(current + Math.Min(capacity, increase), rule.FloutLength);
                if (adjusted <= current + 0.009d)
                {
                    adjusted = RoundAmountByFloutLength(current + Math.Min(capacity, increase), rule.FloutLength);
                }

                adjusted = Math.Min(max, adjusted);
                if (adjusted <= current + 0.009d)
                {
                    continue;
                }

                items[index] = item with { Amount = isIncome ? adjusted : -adjusted };
                increase = RoundMoney(increase - (adjusted - current));
            }
        }

        private static void DecreasePlanTotal(
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            bool isIncome,
            double decrease)
        {
            foreach (var index in GetAdjustablePlanItemIndexes(references, constItems, items, isIncome, preferLargeAmount: true))
            {
                if (decrease <= 0.009d)
                {
                    return;
                }

                var item = items[index];
                if (!TryGetPlanRule(item, references, constItems, out var rule))
                {
                    continue;
                }

                var (min, _) = GetRuleAmountRange(rule);
                var current = Math.Abs(item.Amount);
                var capacity = RoundMoney(current - min);
                if (capacity <= 0.009d)
                {
                    continue;
                }

                var adjusted = Math.Max(min, RoundAmountDownByFloutLength(current - Math.Min(capacity, decrease), rule.FloutLength));
                if (adjusted >= current - 0.009d)
                {
                    continue;
                }

                items[index] = item with { Amount = isIncome ? adjusted : -adjusted };
                decrease = RoundMoney(decrease - (current - adjusted));
            }
        }

        private static IEnumerable<int> GetAdjustablePlanItemIndexes(
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            bool isIncome,
            bool preferLargeAmount)
        {
            var query = items
                .Select((item, index) => new { Item = item, Index = index })
                .Where(item => IsPlanItemSigned(item.Item, isIncome)
                    && TryGetPlanRule(item.Item, references, constItems, out _));

            query = preferLargeAmount
                ? query.OrderBy(item => IsPlanItemRequired(item.Item, references, constItems))
                    .ThenByDescending(item => Math.Abs(item.Item.Amount))
                    .ThenBy(item => item.Item.AccountTime)
                : query.OrderBy(item => IsPlanItemRequired(item.Item, references, constItems))
                    .ThenBy(item => Math.Abs(item.Item.Amount))
                    .ThenBy(item => item.Item.AccountTime);

            return query.Select(item => item.Index).ToList();
        }

        private static void AddOptionalPlanItems(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            List<VendorPlanItem> items,
            bool isIncome,
            double amount)
        {
            var candidates = references
                .Select((rule, index) => new { Rule = (FlowRuleBase)rule, SourceKind = 0, SourceIndex = index })
                .Where(item => IsRuleSigned(item.Rule, isIncome))
                .OrderBy(item => IsRequiredPlanRule(item.Rule))
                .ThenBy(item => isIncome && IsPaymentLikeIncomeRule(item.Rule))
                .ThenBy(item => GetRuleAmountRange(item.Rule).Min)
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            var sequence = 0;
            while (amount > 0.009d)
            {
                var added = false;
                foreach (var candidate in candidates)
                {
                    var (min, max) = GetRuleAmountRange(candidate.Rule);
                    if (amount + 0.009d < min)
                    {
                        continue;
                    }

                    var value = amount >= min * 2 ? min : Math.Min(max, amount);
                    value = RoundAmountDownByFloutLength(value, candidate.Rule.FloutLength);
                    if (value + 0.009d < min || value > max + 0.009d)
                    {
                        continue;
                    }

                    items.Add(new VendorPlanItem(
                        CreateBalancingAccountTime(request, candidate.Rule, sequence++),
                        isIncome ? value : -value,
                        candidate.SourceKind,
                        candidate.SourceIndex,
                        false));
                    amount = RoundMoney(amount - value);
                    added = true;
                    break;
                }

                if (!added)
                {
                    return;
                }
            }
        }

        private static DateTime CreateBalancingAccountTime(FlowAutoGenerationRequest request, FlowRuleBase rule, int sequence)
        {
            if (rule is GenerateReferenceRule reference)
            {
                var start = request.Config.StartTime.Date;
                var end = NormalizeEndDate(request.Config.EndTime).Date;
                var buckets = CreateIncomeMonthBuckets(request);
                var bucket = buckets.Count == 0
                    ? new IncomeMonthBucket { Start = start, End = end, MonthIndex = 0 }
                    : buckets[sequence % buckets.Count];
                return CreateSparseIncomeAccountTime(request, reference, bucket, sequence);
            }

            if (TryGetAllowedRuleRange(request, rule, request.Config.StartTime.AddMonths(sequence), out var allowedStart, out var allowedEnd))
            {
                return CreateRuleTimeInRange(request, rule, allowedStart, allowedEnd, sequence * 47 + 19);
            }

            var date = request.Config.StartTime.Date;
            return CreateRuleTimeInRange(request, rule, date, date.AddDays(1).AddTicks(-1), sequence * 47 + 19);
        }

        private static double SumPlanTotal(IEnumerable<VendorPlanItem> items, bool isIncome)
        {
            return RoundMoney(items
                .Where(item => IsPlanItemSigned(item, isIncome))
                .Sum(item => Math.Abs(item.Amount)));
        }

        private static bool IsPlanItemSigned(VendorPlanItem item, bool isIncome)
        {
            return isIncome ? item.Amount > 0.009d : item.Amount < -0.009d;
        }

        private static bool TryGetPlanRule(
            VendorPlanItem item,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            out FlowRuleBase rule)
        {
            if (TryGetPlanRuleIndex(item, references, constItems, out rule, out _))
            {
                return true;
            }

            rule = null!;
            return false;
        }

        private static bool TryGetPlanRuleIndex(
            VendorPlanItem item,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            out FlowRuleBase rule,
            out int sourceIndex)
        {
            if (item.SourceKind == 0 && TryResolveReferenceRuleIndex(item, references, out sourceIndex, out var referenceRule))
            {
                rule = referenceRule;
                return true;
            }

            if (item.SourceKind == 1 && TryResolveRuleIndex(item, constItems, out sourceIndex, out var constRule))
            {
                rule = constRule;
                return true;
            }

            rule = null!;
            sourceIndex = -1;
            return false;
        }

        private static bool TryResolveReferenceRuleIndex(
            VendorPlanItem item,
            IReadOnlyList<GenerateReferenceRule> references,
            out int sourceIndex,
            out GenerateReferenceRule rule)
        {
            if (item.SourceKind != 0)
            {
                sourceIndex = -1;
                rule = null!;
                return false;
            }

            return TryResolveRuleIndex(item, references, out sourceIndex, out rule);
        }

        private static bool TryResolveRuleIndex<T>(
            VendorPlanItem item,
            IReadOnlyList<T> rules,
            out int sourceIndex,
            out T rule)
            where T : FlowRuleBase
        {
            var best = EnumerateRuleIndexCandidates(rules, item.SourceIndex)
                .Select(index => new
                {
                    Index = index,
                    Rule = rules[index],
                    Score = ScoreRuleCandidate(item, rules[index])
                })
                .Where(candidate => candidate.Score >= 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Index == item.SourceIndex ? 0 : 1)
                .ThenBy(candidate => item.SourceIndex > 0 && candidate.Index == item.SourceIndex - 1 ? 0 : 1)
                .ThenBy(candidate => Math.Abs(candidate.Index - item.SourceIndex))
                .FirstOrDefault();

            if (best is null)
            {
                sourceIndex = -1;
                rule = null!;
                return false;
            }

            sourceIndex = best.Index;
            rule = best.Rule;
            return true;
        }

        private static IEnumerable<int> EnumerateRuleIndexCandidates<T>(IReadOnlyList<T> rules, int sourceIndex)
            where T : FlowRuleBase
        {
            var emitted = new HashSet<int>();

            if (sourceIndex >= 0 && sourceIndex < rules.Count && emitted.Add(sourceIndex))
            {
                yield return sourceIndex;
            }

            var oneBasedIndex = sourceIndex - 1;
            if (sourceIndex > 0 && oneBasedIndex < rules.Count && emitted.Add(oneBasedIndex))
            {
                yield return oneBasedIndex;
            }

            for (var index = 0; index < rules.Count; index++)
            {
                if (rules[index].Index == sourceIndex && emitted.Add(index))
                {
                    yield return index;
                }
            }
        }

        private static int ScoreRuleCandidate(VendorPlanItem item, FlowRuleBase rule)
        {
            var sign = GetAmountSign(item.Amount);
            var score = 0;
            if (sign != 0)
            {
                if (!IsRuleSigned(rule, sign > 0))
                {
                    return -1;
                }

                score += 100;
            }

            var absolute = Math.Abs(item.Amount);
            if (absolute > 0.009d)
            {
                var (min, max) = GetRuleAmountRange(rule);
                if (absolute >= min - 0.009d && absolute <= max + 0.009d)
                {
                    score += 50;
                }
                else if (absolute < min - 0.009d)
                {
                    score += Math.Max(
                        0,
                        20 - (int)Math.Min(20, Math.Ceiling((min - absolute) / Math.Max(1d, min) * 20d)));
                }
                else
                {
                    score += Math.Max(
                        0,
                        20 - (int)Math.Min(20, Math.Ceiling((absolute - max) / Math.Max(1d, max) * 20d)));
                }
            }

            return score;
        }

        private static bool IsPlanItemRequired(
            VendorPlanItem item,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems)
        {
            return TryGetPlanRule(item, references, constItems, out var rule) && IsRequiredPlanRule(rule);
        }

        private static bool IsRequiredPlanRule(FlowRuleBase rule)
        {
            return rule is GenerateReferenceRule reference && Math.Max(0, reference.PercentMonth ?? 0) > 0;
        }

        private static bool IsRuleSigned(FlowRuleBase rule, bool isIncome)
        {
            var text = rule.IncomeAttribute ?? string.Empty;
            var isRuleIncome = text.Contains(IncomeText, StringComparison.Ordinal);
            return isIncome ? isRuleIncome : !isRuleIncome;
        }

        private static double RoundAmountByFloutLength(double value, int? floutLength)
        {
            var length = floutLength ?? 0;
            if (length < 0)
            {
                var factor = Math.Pow(10, -length);
                return RoundMoney(Math.Round(value / factor, MidpointRounding.AwayFromZero) * factor);
            }

            return RoundMoney(Math.Round(value, length, MidpointRounding.AwayFromZero));
        }

        private static double RoundAmountDownByFloutLength(double value, int? floutLength)
        {
            var length = floutLength ?? 0;
            if (length < 0)
            {
                var factor = Math.Pow(10, -length);
                return RoundMoney(Math.Floor(value / factor) * factor);
            }

            var scale = Math.Pow(10, length);
            return RoundMoney(Math.Floor(value * scale) / scale);
        }

        private IList CreateVendorFlowRecords(
            FlowAutoGenerationRequest request,
            IReadOnlyList<GenerateReferenceRule> references,
            IReadOnlyList<GenerateConstRule> constItems,
            IList vendorReferences,
            IList vendorConstItems,
            object vendorBankUser,
            IReadOnlyList<VendorPlanItem> planItems)
        {
            var vendorRecords = (IList)(Activator.CreateInstance(flowListType)
                ?? throw new InvalidOperationException("无法创建真诚完整流水列表。"));
            foreach (var planItem in planItems)
            {
                var amount = RoundMoney(planItem.Amount);
                if (Math.Abs(amount) <= 0.009d)
                {
                    continue;
                }

                var resolvedSourceIndex = planItem.SourceIndex;
                if (TryGetPlanRuleIndex(planItem, references, constItems, out var localRule, out var localSourceIndex))
                {
                    amount = NormalizeSignedAmountByRule(amount, localRule);
                    resolvedSourceIndex = localSourceIndex;
                }

                var sourceRule = planItem.SourceKind switch
                {
                    0 => TryResolveVendorRule(vendorReferences, resolvedSourceIndex),
                    1 => TryResolveVendorRule(vendorConstItems, resolvedSourceIndex),
                    _ => null
                };
                if (sourceRule is null)
                {
                    continue;
                }

                var vendorRecord = Activator.CreateInstance(flowType)
                    ?? throw new InvalidOperationException("无法创建真诚完整流水记录。");
                CopyMatchingProperties(sourceRule, vendorRecord);
                ApplyVendorRecordBasics(request, vendorRecord, vendorBankUser, planItem, amount, vendorRecords.Count + 1);
                vendorRecords.Add(vendorRecord);
            }

            return vendorRecords;
        }

        private void ApplyVendorRecordBasics(
            FlowAutoGenerationRequest request,
            object vendorRecord,
            object vendorBankUser,
            VendorPlanItem planItem,
            double amount,
            int index)
        {
            SetProperty(vendorRecord, "Index", index);
            SetProperty(vendorRecord, "Id", 0L);
            SetProperty(vendorRecord, "BankId", request.Bank.Id);
            SetProperty(vendorRecord, "BankUserId", request.BankUser.Id);
            SetProperty(vendorRecord, "AccountTime", planItem.AccountTime);
            SetProperty(vendorRecord, "TradeMoney", amount);
            SetProperty(vendorRecord, "MoveFlag", planItem.MoveFlag);
            SetProperty(vendorRecord, "IncomeAttribute", amount >= 0 ? "收入" : "支出");
            SetProperty(vendorRecord, "IncomeFlag", amount >= 0 ? "C" : "D");
            SetProperty(vendorRecord, "CreditAmount", amount > 0 ? amount : null);
            SetProperty(vendorRecord, "DebitAmount", amount < 0 ? Math.Abs(amount) : null);
            SetStringPropertyIfBlank(vendorRecord, "Currency", FirstNonEmpty(request.BankUser.Currency, "RMB"));
            SetStringPropertyIfBlank(vendorRecord, "TradeCurrency", FirstNonEmpty(GetStringProperty(vendorRecord, "Currency"), request.BankUser.Currency, "RMB"));

            var accountValue = ResolveFlowAccountValue(request);
            SetStringPropertyIfBlank(vendorRecord, "Account", accountValue);
            SetStringPropertyIfBlank(vendorRecord, "AccountNum", FirstNonEmpty(GetStringProperty(vendorBankUser, "AccountNum"), request.BankUser.AccountNo));
            SetStringPropertyIfBlank(vendorRecord, "CashCheck", "转账");
        }

        private void ProcessVendorInterestIfNeeded(
            FlowAutoGenerationRequest request,
            object vendorBank,
            object vendorBankUser,
            ref IList vendorRecords)
        {
            if (!request.BankUser.AutoCalculateInterest || processInterestMethod is null)
            {
                return;
            }

            var rateConfig = EnsureVendorInterestConfig(request);
            SyncVendorBankRateList(request);
            AddVendorInterestPlaceholders(request, vendorBankUser, rateConfig, vendorRecords);
            SortVendorRecordsByDate(vendorRecords);

            var args = new object?[] { vendorRecords, vendorBank, vendorBankUser };
            processInterestMethod.Invoke(null, args);
            if (args[0] is IList processed)
            {
                vendorRecords = processed;
            }
        }

        private void SyncVendorBankRateList(FlowAutoGenerationRequest request)
        {
            if (bankRateType is null || globalBankRateListField is null)
            {
                throw new MissingFieldException("BankRate", "GlobalList");
            }

            var listType = typeof(List<>).MakeGenericType(bankRateType);
            var list = globalBankRateListField.GetValue(null) as IList;
            if (list is null || !listType.IsInstanceOfType(list))
            {
                list = (IList)(Activator.CreateInstance(listType)
                    ?? throw new InvalidOperationException("Unable to create vendor BankRate list."));
            }

            for (var index = list.Count - 1; index >= 0; index--)
            {
                if (TryParseDouble(GetPropertyValue(list[index]!, "BankId"), out var bankId)
                    && Convert.ToInt64(bankId) == request.Bank.Id)
                {
                    list.RemoveAt(index);
                }
            }

            var setting = request.InterestSetting;
            var months = ParseIntTokens(setting?.Months).Where(item => item is >= 1 and <= 12).Distinct().ToList();
            if (months.Count == 0)
            {
                months.AddRange([3, 6, 9, 12]);
            }

            var bankRate = Activator.CreateInstance(bankRateType)
                ?? throw new InvalidOperationException("Unable to create vendor BankRate.");
            SetProperty(bankRate, "BankId", request.Bank.Id);
            SetProperty(bankRate, "Month", string.Join(";", months));
            SetProperty(bankRate, "Day", ParseInt(setting?.SettlementDay, 21));
            SetProperty(bankRate, "Rate", ParseDouble(setting?.RatePercent, 0.15d));
            SetProperty(bankRate, "MessageString", CreateVendorRateMessageJson(request));
            list.Add(bankRate);
            globalBankRateListField.SetValue(null, list);
        }

        private static string CreateVendorRateMessageJson(FlowAutoGenerationRequest request)
        {
            var items = new[]
            {
                new Dictionary<string, string>
                {
                    ["name"] = VendorInterestMarkerColumnName,
                    ["value"] = ResolveInterestProductBrief(request)
                }
            };
            return JsonSerializer.Serialize(items);
        }

        private static string ResolveInterestProductBrief(FlowAutoGenerationRequest request)
        {
            return FirstNonEmpty(
                request.InterestSetting?.Fields.FirstOrDefault(item =>
                    string.Equals(item.Field, nameof(FlowRecord.ProductBrief), StringComparison.Ordinal))?.Value,
                "\u7ed3\u606f");
        }

        private object EnsureVendorInterestConfig(FlowAutoGenerationRequest request)
        {
            if (bankRateConfigType is null || saveBankRateConfigMethod is null)
            {
                throw new MissingMethodException("BankRateConfig", "Save");
            }

            EnsureVendorRateConfigTable();
            object? rateConfig = null;
            if (getBankRateConfigMethod is not null)
            {
                rateConfig = getBankRateConfigMethod.Invoke(null, [request.Bank.Id]);
            }

            rateConfig ??= Activator.CreateInstance(bankRateConfigType)
                ?? throw new InvalidOperationException("鏃犳硶鍒涘缓鐪熻瘹 BankRateConfig銆?");
            FillVendorInterestConfig(request, rateConfig);
            var saved = saveBankRateConfigMethod.Invoke(null, [rateConfig]);
            return saved ?? rateConfig;
        }

        private void EnsureVendorRateConfigTable()
        {
            if (bankRateConfigType is null)
            {
                return;
            }

            foreach (var method in vendorClientFactoryMethods)
            {
                var client = method.Invoke(null, null);
                var codeFirst = client?.GetType().GetProperty("CodeFirst")?.GetValue(client);
                var initTables = codeFirst?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(item =>
                    {
                        if (!string.Equals(item.Name, "InitTables", StringComparison.Ordinal))
                        {
                            return false;
                        }

                        var parameters = item.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType == typeof(Type[]);
                    });
                initTables?.Invoke(codeFirst, [new[] { bankRateConfigType }]);
            }
        }

        private void FillVendorInterestConfig(FlowAutoGenerationRequest request, object rateConfig)
        {
            var setting = request.InterestSetting;
            var months = ParseIntTokens(setting?.Months).Where(item => item is >= 1 and <= 12).Distinct().ToList();
            if (months.Count == 0)
            {
                months.AddRange([3, 6, 9, 12]);
            }

            SetProperty(rateConfig, "BankId", request.Bank.Id);
            SetProperty(rateConfig, "Day", ParseInt(setting?.SettlementDay, 21));
            SetProperty(rateConfig, "Month", string.Join(";", months));
            SetProperty(rateConfig, "StartHour", ParseInt(setting?.StartTime, 0));
            SetProperty(rateConfig, "EndHour", ParseInt(setting?.EndTime, 23));
            SetProperty(rateConfig, "Rate", ParseDouble(setting?.RatePercent, 0.15d));
            SetProperty(rateConfig, "Account", ResolveFlowAccountValue(request));
            SetProperty(rateConfig, "Currency", FirstNonEmpty(request.BankUser.Currency, "RMB"));
            SetProperty(rateConfig, "CashCheck", ResolveDefaultInterestCashCheck(request));
            SetProperty(rateConfig, "ProductBrief", ResolveInterestProductBrief(request));
            SetProperty(rateConfig, "ProductName", InterestText);
            SetProperty(rateConfig, "Usage", InterestText);
            SetProperty(rateConfig, "TradeExplain", InterestText);
            SetProperty(rateConfig, "Remark", PersonalCurrentInterestRemark);
            SetProperty(rateConfig, "SerialNum", "0000000001");

            if (setting is not null)
            {
                foreach (var field in setting.Fields)
                {
                    if (!string.IsNullOrWhiteSpace(field.Field)
                        && !field.Field.StartsWith("[", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(field.Value))
                    {
                        SetProperty(rateConfig, field.Field, field.Value);
                    }
                }
            }
        }

        private void AddVendorInterestPlaceholders(
            FlowAutoGenerationRequest request,
            object vendorBankUser,
            object rateConfig,
            IList vendorRecords)
        {
            var setting = request.InterestSetting;
            var months = ParseIntTokens(setting?.Months).Where(item => item is >= 1 and <= 12).Distinct().ToList();
            if (months.Count == 0)
            {
                months.AddRange([3, 6, 9, 12]);
            }

            var day = Math.Clamp(ParseInt(setting?.SettlementDay, 21), 1, 31);
            var startHour = Math.Clamp(ParseInt(setting?.StartTime, 0), 0, 23);
            var end = NormalizeEndDate(request.Config.EndTime);
            for (var year = request.Config.StartTime.Year; year <= end.Year; year++)
            {
                foreach (var month in months)
                {
                    var settleDay = Math.Min(day, DateTime.DaysInMonth(year, month));
                    var accountTime = new DateTime(year, month, settleDay, startHour, 0, 0);
                    if (accountTime < request.Config.StartTime || accountTime > end)
                    {
                        continue;
                    }

                    var vendorRecord = Activator.CreateInstance(flowType)
                        ?? throw new InvalidOperationException("鏃犳硶鍒涘缓鐪熻瘹缁撴伅鍗犱綅娴佹按銆?");
                    CopyMatchingProperties(rateConfig, vendorRecord);
                    SetProperty(vendorRecord, "Index", vendorRecords.Count + 1);
                    SetProperty(vendorRecord, "Id", 0L);
                    SetProperty(vendorRecord, "BankId", request.Bank.Id);
                    SetProperty(vendorRecord, "BankUserId", request.BankUser.Id);
                    SetProperty(vendorRecord, "AccountTime", accountTime);
                    SetProperty(vendorRecord, "TradeMoney", 0d);
                    SetProperty(vendorRecord, "MoveFlag", false);
                    SetProperty(vendorRecord, "IncomeAttribute", "\u6536\u5165");
                    SetProperty(vendorRecord, "IncomeFlag", "C");
                    SetStringPropertyIfBlank(vendorRecord, "Account", ResolveFlowAccountValue(request));
                    SetStringPropertyIfBlank(vendorRecord, "AccountNum", FirstNonEmpty(GetStringProperty(vendorBankUser, "AccountNum"), request.BankUser.AccountNo));
                    SetStringPropertyIfBlank(vendorRecord, "Currency", FirstNonEmpty(request.BankUser.Currency, "RMB"));
                    SetStringPropertyIfBlank(vendorRecord, "TradeCurrency", GetStringProperty(vendorRecord, "Currency"));
                    SetStringPropertyIfBlank(vendorRecord, "CashCheck", ResolveDefaultInterestCashCheck(request));
                    SetStringPropertyIfBlank(vendorRecord, "ProductBrief", ResolveInterestProductBrief(request));
                    SetStringPropertyIfBlank(vendorRecord, "ProductName", InterestText);
                    SetStringPropertyIfBlank(vendorRecord, "Usage", InterestText);
                    SetStringPropertyIfBlank(vendorRecord, "TradeExplain", InterestText);
                    SetStringPropertyIfBlank(vendorRecord, "Remark", PersonalCurrentInterestRemark);
                    SetStringPropertyIfBlank(vendorRecord, "SerialNum", "0000000001");
                    vendorRecords.Add(vendorRecord);
                }
            }
        }

        private static void SortVendorRecordsByDate(IList vendorRecords)
        {
            var ordered = vendorRecords
                .Cast<object>()
                .OrderBy(item => GetPropertyValue(item, "AccountTime") as DateTime? ?? DateTime.MaxValue)
                .ThenBy(item => GetStringProperty(item, "SerialNum"), StringComparer.Ordinal)
                .ToList();
            vendorRecords.Clear();
            foreach (var item in ordered)
            {
                vendorRecords.Add(item);
            }
        }

        private object CreateVendorBank(FlowAutoGenerationRequest request)
        {
            var vendorBank = Activator.CreateInstance(bankType)
                ?? throw new InvalidOperationException("无法创建真诚银行对象。");
            SetProperty(vendorBank, "Id", request.Bank.Id);
            SetProperty(vendorBank, "BankId", request.Bank.Id);
            SetProperty(vendorBank, "Name", request.Bank.Name);
            SetProperty(vendorBank, "Title", request.Bank.Name);
            SetProperty(vendorBank, "BankTitle", request.Bank.Name);
            SetProperty(vendorBank, "Type", request.Bank.Type);
            if (request.Bank.Rate.HasValue)
            {
                SetProperty(vendorBank, "Rate", request.Bank.Rate.Value);
                SetProperty(vendorBank, "Rame", request.Bank.Rate.Value);
            }

            SetProperty(vendorBank, "Columns", CreateVendorColumns(request.Bank.Columns));
            SetProperty(vendorBank, "ReferenceColumns", CreateVendorColumns(request.Bank.ReferenceColumns));
            SetProperty(vendorBank, "ConstColumms", CreateVendorColumns(request.Bank.ConstColumns));
            SetProperty(vendorBank, "FlowColumns", CreateVendorColumns(GetFlowColumnsForVendor(request)));
            return vendorBank;
        }

        private IList CreateVendorColumns(IEnumerable<ColumnDefinition> columns)
        {
            var list = (IList)(Activator.CreateInstance(typeof(List<>).MakeGenericType(columnType))
                ?? throw new InvalidOperationException("Unable to create vendor column list."));
            foreach (var column in columns)
            {
                var vendorColumn = Activator.CreateInstance(columnType)
                    ?? throw new InvalidOperationException("Unable to create vendor column.");
                SetProperty(vendorColumn, "Type", NormalizeVendorColumnType(column.Type));
                SetProperty(vendorColumn, "Name", column.Name ?? string.Empty);
                SetProperty(vendorColumn, "Field", ToVendorColumnField(column.Field));
                SetProperty(vendorColumn, "Width", column.Width);
                SetProperty(vendorColumn, "Order", column.Order);
                SetProperty(vendorColumn, "Show", column.Show);
                list.Add(vendorColumn);
            }

            return list;
        }

        private static IReadOnlyList<ColumnDefinition> GetFlowColumnsForVendor(FlowAutoGenerationRequest request)
        {
            if (request.BankUser.AutoCalculateInterest)
            {
                return
                [
                    new ColumnDefinition
                    {
                        Type = "string",
                        Name = VendorInterestMarkerColumnName,
                        Field = nameof(FlowRecord.ProductBrief),
                        Width = 100,
                        Order = 0,
                        Show = false
                    }
                ];
            }

            var recordPropertyNames = typeof(FlowRecord)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(item => item.GetIndexParameters().Length == 0)
                .Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal);
            var columns = request.Bank.FlowColumns
                .Where(item => !string.IsNullOrWhiteSpace(item.Field)
                    && recordPropertyNames.Contains(item.Field!))
                .ToList();
            return columns;
        }

        private static string ToVendorColumnField(string? field)
        {
            if (string.IsNullOrWhiteSpace(field)
                || field.Contains('_', StringComparison.Ordinal)
                || field.StartsWith("[", StringComparison.Ordinal))
            {
                return field ?? string.Empty;
            }

            var builder = new StringBuilder(field.Length + 8);
            for (var index = 0; index < field.Length; index++)
            {
                var character = field[index];
                if (char.IsUpper(character)
                    && index > 0
                    && (char.IsLower(field[index - 1])
                        || char.IsDigit(field[index - 1])
                        || (index + 1 < field.Length && char.IsLower(field[index + 1]))))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }

            return builder.ToString();
        }

        private static string NormalizeVendorColumnType(string? type)
        {
            return (type ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "int" or "integer" or "number" => "int",
                "double" or "decimal" or "money" or "float" => "double",
                "bool" or "boolean" => "bool",
                _ => "string"
            };
        }

        private object CreateVendorBankUser(FlowAutoGenerationRequest request)
        {
            var user = request.BankUser;
            var config = request.Config;
            var vendorUser = Activator.CreateInstance(bankUserType)
                ?? throw new InvalidOperationException("无法创建真诚用户对象。");

            SetProperty(vendorUser, "Id", user.Id);
            SetProperty(vendorUser, "BankId", request.Bank.Id);
            SetProperty(vendorUser, "BankTitle", request.Bank.Name);
            SetProperty(vendorUser, "Username", user.AccountName);
            SetProperty(vendorUser, "UserNum", FirstNonEmpty(user.UserCode, user.AccountNo, user.CardNo));
            SetProperty(vendorUser, "Account", FirstNonEmpty(user.AccountNo, user.CardNo));
            SetProperty(vendorUser, "AccountNum", FirstNonEmpty(user.AccountNo, user.CardNo));
            SetProperty(vendorUser, "CardNum", FirstNonEmpty(user.CardNo, user.AccountNo));
            SetProperty(vendorUser, "IdNum", user.IdNumber);
            SetProperty(vendorUser, "OpenBranch", user.OpenBranch);
            SetProperty(vendorUser, "Currency", FirstNonEmpty(user.Currency, "RMB"));
            SetProperty(vendorUser, "Remark", user.Remark);
            SetProperty(vendorUser, "StartTime", config.StartTime.Date);
            SetProperty(vendorUser, "EndTime", NormalizeEndDate(config.EndTime));
            SetProperty(vendorUser, "InitialBalance", request.OpeningBalanceOverride ?? config.OpeningBalance);
            SetProperty(vendorUser, "Balance", (double)user.Balance);
            SetProperty(vendorUser, "IsAutoInterest", user.AutoCalculateInterest);
            SetProperty(vendorUser, "GSelectTndex", config.SelectIndex);
            SetProperty(vendorUser, "GAllInMoney", config.AllInMoney);
            SetProperty(vendorUser, "GAllOutMoney", config.AllOutMoney);
            SetProperty(vendorUser, "GLastMoney", config.LastMoney);
            SetProperty(vendorUser, "GMinInMoneyMonth1", config.MinInMoneyMonth1);
            SetProperty(vendorUser, "GMaxInMoneyMonth1", config.MaxInMoneyMonth1);
            SetProperty(vendorUser, "GMinOutMoneyMonth1", config.MinOutMoneyMonth1);
            SetProperty(vendorUser, "GMaxOutMoneyMonth1", config.MaxOutMoneyMonth1);
            SetProperty(vendorUser, "GMinInMoneyMonth2", config.MinInMoneyMonth2);
            SetProperty(vendorUser, "GMaxInMoneyMonth2", config.MaxInMoneyMonth2);
            SetProperty(vendorUser, "GMinOutMoneyMonth2", config.MinOutMoneyMonth2);
            SetProperty(vendorUser, "GMaxOutMoneyMonth2", config.MaxOutMoneyMonth2);

            ApplyBankUserExtraFields(request.Bank, user, vendorUser);
            return vendorUser;
        }

        private static object? TryResolveVendorRule(IList rules, int sourceIndex)
        {
            if (sourceIndex >= 0 && sourceIndex < rules.Count)
            {
                return rules[sourceIndex];
            }

            if (sourceIndex > 0 && sourceIndex <= rules.Count)
            {
                return rules[sourceIndex - 1];
            }

            foreach (var item in rules)
            {
                if (item is null)
                {
                    continue;
                }

                var index = GetPropertyValue(item, "Index");
                if (index is not null
                    && int.TryParse(Convert.ToString(index, CultureInfo.InvariantCulture), out var parsed)
                    && parsed == sourceIndex)
                {
                    return item;
                }
            }

            return null;
        }

        private FlowRecord CreateFlowRecordFromVendorRecord(FlowAutoGenerationRequest request, object vendorRecord, int index)
        {
            var record = new FlowRecord
            {
                Index = index + 1,
                BankId = request.Bank.Id,
                BankUserId = request.BankUser.Id
            };

            foreach (var targetProperty in typeof(FlowRecord).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!targetProperty.CanWrite
                    || targetProperty.GetIndexParameters().Length > 0
                    || string.Equals(targetProperty.Name, nameof(FlowRecord.ExtraFields), StringComparison.Ordinal))
                {
                    continue;
                }

                var sourceValue = GetPropertyValue(vendorRecord, targetProperty.Name);
                if (sourceValue is not null)
                {
                    SetModelProperty(record, targetProperty, sourceValue);
                }
            }

            if (record.BankId == 0)
            {
                record.BankId = request.Bank.Id;
            }

            if (record.BankUserId == 0)
            {
                record.BankUserId = request.BankUser.Id;
            }

            if (string.IsNullOrWhiteSpace(record.Account))
            {
                record.Account = ResolveFlowAccountValue(request);
            }

            var amount = RoundMoney(record.TradeMoney ?? 0);
            record.TradeMoney = amount;
            if (!record.BalanceAmount.HasValue && record.Balance.HasValue)
            {
                record.BalanceAmount = record.Balance;
            }

            if (string.IsNullOrWhiteSpace(record.Currency))
            {
                record.Currency = FirstNonEmpty(request.BankUser.Currency, "RMB");
            }

            if (string.IsNullOrWhiteSpace(record.TradeCurrency))
            {
                record.TradeCurrency = record.Currency;
            }

            record.IncomeAttribute = amount >= 0 ? "收入" : "支出";
            record.CreditAmount = amount > 0 ? amount : null;
            record.DebitAmount = amount < 0 ? Math.Abs(amount) : null;
            record.IncomeFlag = amount >= 0 ? "C" : "D";
            BackfillConsumerExternalSystemFlowNumber(request, record);
            BackfillAgriculturalSystemFields(request, record, vendorRecord);
            return record;
        }

        private void BackfillAgriculturalSystemFields(FlowAutoGenerationRequest request, FlowRecord record, object vendorRecord)
        {
            if (!IsAgriculturalBank(request.Bank))
            {
                return;
            }

            var logFields = GetFlowRecordFields(request.Bank, nameof(FlowRecord.LogNum), "日志号");
            if (logFields.Count > 0)
            {
                var logNumber = FirstNonEmpty(
                    TryInvokeVendorFlowString(createVendorLogNumberMethod, vendorRecord),
                    CreateAgriculturalLogNumber(record));

                foreach (var field in logFields)
                {
                    SetFlowRecordStringIfBlank(record, field, logNumber);
                }
            }

            var remarkFields = GetFlowRecordFields(request.Bank, nameof(FlowRecord.Remark), "附言", "备注", "留言", "转账附言", "APP备注");
            if (remarkFields.Count == 0)
            {
                return;
            }

            var generatedRemark = CreateAgriculturalPostscript(
                record,
                TryInvokeVendorFlowString(createVendorRemarkMethod, vendorRecord));
            if (string.IsNullOrWhiteSpace(generatedRemark))
            {
                return;
            }

            foreach (var field in remarkFields)
            {
                var current = GetFlowRecordStringValue(record, field).Trim();
                if (HasGeneratedAgriculturalPostscriptPrefix(current))
                {
                    continue;
                }

                SetFlowRecordValue(record, field, generatedRemark);
            }
        }

        private static bool IsAgriculturalBank(Bank bank)
        {
            return bank.Name.Contains("农行", StringComparison.Ordinal)
                || bank.Name.Contains("农业", StringComparison.Ordinal);
        }

        private static List<string> GetFlowRecordFields(Bank bank, string field, params string[] columnNames)
        {
            var normalizedNames = columnNames
                .Select(NormalizeName)
                .ToHashSet(StringComparer.Ordinal);

            return bank.FlowColumns
                .Where(item => !string.IsNullOrWhiteSpace(item.Field))
                .Where(item => string.Equals(item.Field, field, StringComparison.Ordinal)
                    || normalizedNames.Contains(NormalizeName(item.Name ?? string.Empty)))
                .Select(item => item.Field!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static string TryInvokeVendorFlowString(MethodInfo? method, object vendorRecord)
        {
            if (method is null)
            {
                return string.Empty;
            }

            try
            {
                return Convert.ToString(method.Invoke(null, [vendorRecord]), CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string CreateAgriculturalLogNumber(FlowRecord record)
        {
            var time = record.AccountTime ?? DateTime.Today;
            var digits = RandomDigits(9);
            if (time < new DateTime(2024, 3, 15))
            {
                return digits;
            }

            if (time < new DateTime(2024, 8, 15))
            {
                return $"U{digits}";
            }

            const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            return $"{letters[RandomNumberGenerator.GetInt32(0, letters.Length)]}{digits}";
        }

        private static string CreateAgriculturalPostscript(FlowRecord record, string? vendorPostscript)
        {
            var originalPostscript = FirstNonEmpty(record.Remark?.Trim(), vendorPostscript?.Trim());
            if (HasGeneratedAgriculturalPostscriptPrefix(originalPostscript))
            {
                return originalPostscript;
            }

            var postscriptText = FirstNonEmpty(originalPostscript, ResolveAgriculturalPostscriptText(record));
            if (string.IsNullOrWhiteSpace(postscriptText))
            {
                return string.Empty;
            }

            var time = record.AccountTime ?? DateTime.Today;
            var brief = record.ProductBrief?.Trim() ?? string.Empty;
            var channel = record.TradeChannel?.Trim() ?? string.Empty;

            if (brief.Contains("抖音", StringComparison.Ordinal)
                || postscriptText.Contains("抖音", StringComparison.Ordinal))
            {
                return $"NA{time:yyyyMMdd}{RandomDigits(23)}{postscriptText}";
            }

            if (brief.Contains("代付", StringComparison.Ordinal)
                && postscriptText.Contains("零钱提现", StringComparison.Ordinal))
            {
                return $"NG{time:yyyyMMdd}{RandomDigits(23)}{postscriptText}";
            }

            if (IsAgriculturalUaPostscriptRecord(record, postscriptText, brief, channel))
            {
                return $"UA{time:MMdd}{RandomDigits(12)}{postscriptText}";
            }

            return postscriptText;
        }

        private static bool IsAgriculturalUaPostscriptRecord(FlowRecord record, string postscriptText, string brief, string channel)
        {
            var text = string.Join(" ", new[]
            {
                brief,
                postscriptText,
                channel,
                record.ProductName,
                record.ProductType,
                record.TradeExplain,
                record.OppositeUsername,
                record.OppositeBank,
                record.Usage,
                record.InterfacePage
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            return ContainsAny(text, AgriculturalUaPostscriptKeywords);
        }

        private static string ResolveAgriculturalPostscriptText(FlowRecord record)
        {
            return FirstNonEmpty(
                record.Usage,
                record.TradeExplain,
                record.ProductBrief,
                record.ProductName,
                record.OppositeUsername,
                record.TradeChannel,
                record.InterfacePage);
        }

        private static bool HasGeneratedAgriculturalPostscriptPrefix(string value)
        {
            return HasPrefixWithDigitCount(value, "UA", 16)
                || HasPrefixWithDigitCount(value, "NA", 31)
                || HasPrefixWithDigitCount(value, "NG", 31);
        }

        private static bool HasPrefixWithDigitCount(string value, string prefix, int digitCount)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length < prefix.Length + digitCount)
            {
                return false;
            }

            for (var index = prefix.Length; index < prefix.Length + digitCount; index++)
            {
                if (!char.IsDigit(value[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static void BackfillConsumerExternalSystemFlowNumber(FlowAutoGenerationRequest request, FlowRecord record)
        {
            if (!IsConsumerExpenseRecord(record))
            {
                return;
            }

            var fields = GetExternalSystemFlowFields(request.Bank);
            if (fields.Count == 0)
            {
                return;
            }

            var flowNumber = CreateExternalSystemFlowNumber(record);
            foreach (var field in fields)
            {
                SetFlowRecordValue(record, field, flowNumber);
            }
        }

        private static List<string> GetExternalSystemFlowFields(Bank bank)
        {
            return bank.FlowColumns
                .Where(item => IsExternalSystemFlowColumnName(item.Name) && !string.IsNullOrWhiteSpace(item.Field))
                .Select(item => item.Field!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static bool IsExternalSystemFlowColumnName(string? name)
        {
            return string.Equals(NormalizeName(name ?? string.Empty), ExternalSystemFlowColumnName, StringComparison.Ordinal);
        }

        private static string CreateExternalSystemFlowNumber(FlowRecord record)
        {
            var time = record.AccountTime ?? DateTime.Now;
            return $"{time:yyyyMMddHHmmss}{RandomDigits(16)}";
        }

        private static string RandomDigits(int length)
        {
            var builder = new StringBuilder(length);
            for (var index = 0; index < length; index++)
            {
                builder.Append(RandomNumberGenerator.GetInt32(0, 10).ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static bool IsConsumerExpenseRecord(FlowRecord record)
        {
            if ((record.TradeMoney ?? 0) >= -0.009d)
            {
                return false;
            }

            var nonConsumerText = BuildNonConsumerClassificationText(record);
            if (ContainsAny(nonConsumerText, NonConsumerExpenseKeywords))
            {
                return false;
            }

            var consumerText = BuildConsumerClassificationText(record);
            return ContainsAny(consumerText, ConsumerExpenseKeywords);
        }

        private static string BuildConsumerClassificationText(FlowRecord record)
        {
            return string.Join(" ", new[]
            {
                record.ProductName,
                record.ProductBrief,
                record.ProductType,
                record.Usage,
                record.TradeExplain,
                record.Remark,
                record.TradePlace,
                record.TradeChannel,
                record.OppositeUsername,
                record.MerchantName,
                record.InterfacePage
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string BuildNonConsumerClassificationText(FlowRecord record)
        {
            return string.Join(" ", new[]
            {
                record.ProductName,
                record.ProductBrief,
                record.ProductType,
                record.Usage,
                record.TradeExplain,
                record.Remark,
                record.TradeChannel,
                record.OppositeUsername,
                record.MerchantName
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static bool ContainsAny(string value, IEnumerable<string> tokens)
        {
            return tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private sealed record InterestRecordFieldProfile(
            string AppNum,
            string SequenceNum,
            string CashCheck,
            string DepositTerm,
            string AgreedTerm,
            string NoticeType,
            string AreaNum,
            string NetNum,
            string BranchNum,
            string Operator,
            string OperatorNum,
            string InterfacePage,
            string TradeChannel,
            string TradeCurrency,
            string Currency,
            string TradeCode);

        private static void BackfillInterestRecords(FlowAutoGenerationRequest request, List<FlowRecord> records)
        {
            if (!request.BankUser.AutoCalculateInterest || records.Count == 0)
            {
                return;
            }

            var interestBrief = ResolveInterestProductBrief(request);
            var profile = CreateInterestRecordFieldProfile(records, interestBrief);
            foreach (var record in records.Where(item => IsMappedSettlementInterestRecord(item, interestBrief)))
            {
                ApplyMappedInterestRecordDefaults(request, record, profile, interestBrief);
            }
        }

        private static InterestRecordFieldProfile CreateInterestRecordFieldProfile(IEnumerable<FlowRecord> records, string interestBrief)
        {
            var normalRecords = records
                .Where(item => !IsMappedSettlementInterestRecord(item, interestBrief))
                .ToList();

            return new InterestRecordFieldProfile(
                CommonValue(normalRecords, item => item.AppNum),
                CommonValue(normalRecords, item => item.SequenceNum),
                CommonValue(normalRecords, item => item.CashCheck),
                CommonValue(normalRecords, item => item.DepositTerm),
                CommonValue(normalRecords, item => item.AgreedTerm),
                CommonValue(normalRecords, item => item.NoticeType),
                CommonValue(normalRecords, item => item.AreaNum),
                CommonValue(normalRecords, item => item.NetNum),
                CommonValue(normalRecords, item => item.BranchNum),
                CommonValue(normalRecords, item => item.Operator),
                CommonValue(normalRecords, item => item.OperatorNum),
                CommonValue(normalRecords, item => item.InterfacePage),
                CommonValue(normalRecords, item => item.TradeChannel),
                CommonValue(normalRecords, item => item.TradeCurrency),
                CommonValue(normalRecords, item => item.Currency),
                CommonValue(normalRecords, item => item.TradeCode));
        }

        private static void ApplyMappedInterestRecordDefaults(
            FlowAutoGenerationRequest request,
            FlowRecord record,
            InterestRecordFieldProfile profile,
            string interestBrief)
        {
            ApplyInterestSettingFields(record, request.InterestSetting);

            var isIcbc = IsIcbcBank(request.Bank);
            record.ExtraFields[AmountUnitField] = "0.01";
            record.ExtraFields[SystemRowKindField] = InterestRowKind;

            var flowAccount = ResolveFlowAccountValue(request);
            if (!string.IsNullOrWhiteSpace(flowAccount)
                && (string.IsNullOrWhiteSpace(record.Account)
                    || IsKnownAlternateUserAccountValue(record.Account, request.BankUser)))
            {
                record.Account = flowAccount;
            }

            var accountNumber = ResolveBankUserAccountValue(request.Bank, request.BankUser, false);
            if (!string.IsNullOrWhiteSpace(accountNumber)
                && (string.IsNullOrWhiteSpace(record.AccountNum)
                    || IsKnownAlternateUserAccountValue(record.AccountNum, request.BankUser)))
            {
                record.AccountNum = accountNumber;
            }

            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.Currency), FirstNonEmpty(profile.Currency, request.BankUser.Currency, "RMB"));
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.TradeCurrency), FirstNonEmpty(profile.TradeCurrency, record.Currency, request.BankUser.Currency, "RMB"));
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.AppNum), FirstNonEmpty(profile.AppNum, isIcbc ? "1" : string.Empty));
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.SequenceNum), FirstNonEmpty(profile.SequenceNum, isIcbc ? "0" : string.Empty));
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.DepositTerm), FirstNonEmpty(profile.DepositTerm, isIcbc ? "000" : string.Empty));
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.AgreedTerm), FirstNonEmpty(profile.AgreedTerm, isIcbc ? "\u4e0d\u8f6c\u5b58" : string.Empty));
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.NoticeType), FirstNonEmpty(profile.NoticeType, isIcbc ? "0" : string.Empty));
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.AreaNum), profile.AreaNum);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.NetNum), profile.NetNum);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.BranchNum), profile.BranchNum);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.Operator), profile.Operator);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.OperatorNum), profile.OperatorNum);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.InterfacePage), profile.InterfacePage);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.TradeChannel), FirstNonEmpty(profile.TradeChannel, isIcbc ? "\u67dc\u9762" : string.Empty));
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.TradeCode), profile.TradeCode);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.ProductBrief), interestBrief);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.ProductName), InterestText);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.ProductType), CurrentDepositText);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.Usage), InterestText);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.TradeExplain), InterestText);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.Remark), PersonalCurrentInterestRemark);
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.SerialNum), "0000000001");
            SetFlowRecordStringIfBlank(record, nameof(FlowRecord.LogNum), "0000000001");

            var settingHasCashCheck = HasInterestSettingField(request.InterestSetting, nameof(FlowRecord.CashCheck));
            var cashCheck = FirstNonEmpty(profile.CashCheck, ResolveDefaultInterestCashCheck(request));
            if (!settingHasCashCheck
                && !string.IsNullOrWhiteSpace(cashCheck)
                && (string.IsNullOrWhiteSpace(record.CashCheck)
                    || (isIcbc && string.Equals(record.CashCheck, TransferText, StringComparison.Ordinal))))
            {
                record.CashCheck = cashCheck;
            }

            record.IncomeAttribute = "\u6536\u5165";
            record.IncomeFlag = "C";
            record.CreditAmount = record.TradeMoney > 0 ? record.TradeMoney : null;
            record.DebitAmount = null;
        }

        private static void ApplyInterestSettingFields(FlowRecord record, BankInterestSetting? setting)
        {
            if (setting is null)
            {
                return;
            }

            foreach (var field in setting.Fields.Where(item => !string.IsNullOrWhiteSpace(item.Field) && !string.IsNullOrWhiteSpace(item.Value)))
            {
                SetFlowRecordValue(record, field.Field, field.Value);
            }
        }

        private static bool IsMappedSettlementInterestRecord(FlowRecord record, string interestBrief)
        {
            if (Math.Abs(record.TradeMoney ?? 0) <= 0.009d)
            {
                return false;
            }

            var text = string.Join('|',
                record.ProductBrief,
                record.ProductName,
                record.Remark,
                record.Usage,
                record.TradeExplain);

            return !text.Contains(InterestTaxText, StringComparison.Ordinal)
                && (text.Contains(interestBrief, StringComparison.Ordinal)
                    || text.Contains(InterestText, StringComparison.Ordinal));
        }

        private static bool IsKnownAlternateUserAccountValue(string? value, BankUser user)
        {
            return !string.IsNullOrWhiteSpace(value)
                && (string.Equals(value, user.AccountNo, StringComparison.Ordinal)
                    || string.Equals(value, user.CardNo, StringComparison.Ordinal));
        }

        private static bool HasInterestSettingField(BankInterestSetting? setting, string field)
        {
            return setting?.Fields.Any(item =>
                string.Equals(item.Field, field, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(item.Value)) == true;
        }

        private static string ResolveDefaultInterestCashCheck(FlowAutoGenerationRequest request)
        {
            return IsIcbcBank(request.Bank) ? CashText : TransferText;
        }

        private static bool IsIcbcBank(Bank bank)
        {
            return bank.Name.Contains(IcbcShortName, StringComparison.Ordinal)
                || bank.Name.Contains(IcbcFullName, StringComparison.Ordinal);
        }

        private static bool IsWechatBank(Bank bank)
        {
            return bank.Name.Contains("微信", StringComparison.Ordinal);
        }

        private static string CommonValue(IEnumerable<FlowRecord> records, Func<FlowRecord, string?> selector)
        {
            var values = records
                .Select(selector)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToList();

            return values.Count == 1 ? values[0] : string.Empty;
        }

        private static string GetFlowRecordStringValue(FlowRecord record, string field)
        {
            if (TryGetIndexerField(field, out var indexerField))
            {
                return record[indexerField];
            }

            var property = typeof(FlowRecord).GetProperty(field, BindingFlags.Instance | BindingFlags.Public);
            if (property is null)
            {
                return record[field];
            }

            return property.GetValue(record) switch
            {
                null => string.Empty,
                string text => text,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                var value => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            };
        }

        private static void SetFlowRecordStringIfBlank(FlowRecord record, string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !string.IsNullOrWhiteSpace(GetFlowRecordStringValue(record, field)))
            {
                return;
            }

            SetFlowRecordValue(record, field, value);
        }

        private static void SetFlowRecordValue(FlowRecord record, string field, object value)
        {
            if (TryGetIndexerField(field, out var indexerField))
            {
                record[indexerField] = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                return;
            }

            var property = typeof(FlowRecord).GetProperty(field, BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanWrite)
            {
                record[field] = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                return;
            }

            SetModelProperty(record, property, value);
        }

        private object CreateComputeEntity(FlowAutoGenerationRequest request)
        {
            var config = request.Config;
            var compute = Activator.CreateInstance(computeType, [
                    (DateTime?)config.StartTime.Date,
                    (DateTime?)NormalizeEndDate(config.EndTime),
                    config.SelectIndex,
                    config.AllInMoney,
                    config.AllOutMoney,
                    config.LastMoney,
                    config.MinInMoneyMonth1,
                    config.MaxInMoneyMonth1,
                    config.MinOutMoneyMonth1,
                    config.MaxOutMoneyMonth1,
                    config.MinInMoneyMonth2,
                    config.MaxInMoneyMonth2,
                    config.MinOutMoneyMonth2,
                    config.MaxOutMoneyMonth2
                ])
                ?? throw new InvalidOperationException("无法创建真诚 ComputeEntity。");

            SetProperty(compute, "SelectIndex", config.SelectIndex);
            SetProperty(compute, "StartTime", config.StartTime.Date);
            SetProperty(compute, "EndTime", NormalizeEndDate(config.EndTime));
            SetProperty(compute, "AllInMoney", config.AllInMoney);
            SetProperty(compute, "AllOutMoney", config.AllOutMoney);
            SetProperty(compute, "LastMoney", config.LastMoney);
            SetProperty(compute, "MinInMoneyMonth1", config.MinInMoneyMonth1);
            SetProperty(compute, "MaxInMoneyMonth1", config.MaxInMoneyMonth1);
            SetProperty(compute, "MinOutMoneyMonth1", config.MinOutMoneyMonth1);
            SetProperty(compute, "MaxOutMoneyMonth1", config.MaxOutMoneyMonth1);
            SetProperty(compute, "MinInMoneyMonth2", config.MinInMoneyMonth2);
            SetProperty(compute, "MaxInMoneyMonth2", config.MaxInMoneyMonth2);
            SetProperty(compute, "MinOutMoneyMonth2", config.MinOutMoneyMonth2);
            SetProperty(compute, "MaxOutMoneyMonth2", config.MaxOutMoneyMonth2);

            ApplyMonthGenerateData(compute, config);
            return compute;
        }

        private void ApplyMonthGenerateData(object compute, FlowGenerationConfig config)
        {
            var property = computeType.GetProperty("MonthGenData", BindingFlags.Instance | BindingFlags.Public);
            if (property is null)
            {
                return;
            }

            var collection = property.GetValue(compute) as IList;
            if (collection is null)
            {
                var collectionType = typeof(System.Collections.ObjectModel.ObservableCollection<>).MakeGenericType(monthGenerateType);
                collection = (IList)(Activator.CreateInstance(collectionType)
                    ?? throw new InvalidOperationException("无法创建真诚 MonthGenData。"));
                property.SetValue(compute, collection);
            }

            collection.Clear();
            foreach (var item in config.MonthGenData)
            {
                var month = Activator.CreateInstance(monthGenerateType)
                    ?? throw new InvalidOperationException("无法创建真诚 MonthGenerate。");
                SetProperty(month, "StartTime", item.StartTime.Date);
                SetProperty(month, "EndTime", NormalizeEndDate(item.EndTime));
                SetProperty(month, "InMoney", item.InMoney);
                SetProperty(month, "OutMoney", item.OutMoney);
                collection.Add(month);
            }
        }

        private static IList CreateVendorRuleList<T>(Type targetType, IReadOnlyList<T> rules)
            where T : FlowRuleBase
        {
            var list = (IList)(Activator.CreateInstance(typeof(List<>).MakeGenericType(targetType))
                ?? throw new InvalidOperationException($"无法创建真诚规则列表：{targetType.Name}"));

            for (var index = 0; index < rules.Count; index++)
            {
                var vendorRule = Activator.CreateInstance(targetType)
                    ?? throw new InvalidOperationException($"无法创建真诚规则：{targetType.Name}");
                CopyMatchingProperties(rules[index], vendorRule);
                SetProperty(vendorRule, "Index", index);
                SetProperty(vendorRule, "IsCheck", true);
                ApplyRuleDefaults(rules[index], vendorRule);
                list.Add(vendorRule);
            }

            return list;
        }

        private static void ApplyRuleDefaults(FlowRuleBase source, object target)
        {
            var min = source.MinMoney ?? 0.01d;
            var max = source.MaxMoney ?? min;
            if (max < min)
            {
                (min, max) = (max, min);
            }

            SetPropertyIfNull(target, "MinMoney", min);
            SetPropertyIfNull(target, "MaxMoney", max);
            SetPropertyIfNull(target, "FloutLength", source.FloutLength ?? 0);
            SetPropertyIfNull(target, "StartDay", source.StartDay ?? 9);
            SetPropertyIfNull(target, "EndDay", source.EndDay ?? 17);
            SetPropertyIfNull(target, "TradeHoliday", source.TradeHoliday ?? false);
            SetPropertyIfNull(target, "TradeWeekend", source.TradeWeekend ?? false);

            if (source is GenerateReferenceRule reference)
            {
                SetPropertyIfNull(target, "PercentMonth", reference.PercentMonth ?? 0);
            }
            else if (source is GenerateConstRule constRule)
            {
                SetStringPropertyIfBlank(target, "FixDay", constRule.FixDay ?? string.Empty);
                SetStringPropertyIfBlank(target, "ReCnt", constRule.ReCnt ?? "1");
            }
        }

        private static void CopyMatchingProperties(object source, object target)
        {
            var sourceProperties = source.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(item => item.GetIndexParameters().Length == 0 && item.CanRead)
                .ToDictionary(item => item.Name, StringComparer.Ordinal);

            foreach (var targetProperty in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!targetProperty.CanWrite || targetProperty.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                if (!sourceProperties.TryGetValue(targetProperty.Name, out var sourceProperty))
                {
                    continue;
                }

                var value = sourceProperty.GetValue(source);
                SetProperty(target, targetProperty, value);
            }
        }

        private static void SetProperty(object target, string name, object? value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property is { CanWrite: true })
            {
                SetProperty(target, property, value);
            }
        }

        private static void SetProperty(object target, PropertyInfo property, object? value)
        {
            if (value is null)
            {
                property.SetValue(target, null);
                return;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (propertyType.IsInstanceOfType(value))
            {
                property.SetValue(target, value);
                return;
            }

            if (propertyType == typeof(string))
            {
                property.SetValue(target, Convert.ToString(value, CultureInfo.CurrentCulture));
                return;
            }

            property.SetValue(target, Convert.ChangeType(value, propertyType, CultureInfo.InvariantCulture));
        }

        private static void SetPropertyIfNull(object target, string name, object value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanWrite || property.GetValue(target) is not null)
            {
                return;
            }

            SetProperty(target, property, value);
        }

        private static void SetStringPropertyIfBlank(object target, string name, string value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property is null || !property.CanWrite || property.PropertyType != typeof(string))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace((string?)property.GetValue(target)))
            {
                property.SetValue(target, value);
            }
        }

        private static object? GetPropertyValue(object target, string name)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property is { CanRead: true } ? property.GetValue(target) : null;
        }

        private static string GetStringProperty(object target, string name)
        {
            var value = GetPropertyValue(target, name);
            return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        }

        private static void SetModelProperty(object target, PropertyInfo property, object value)
        {
            try
            {
                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (propertyType == typeof(string))
                {
                    property.SetValue(target, Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty);
                }
                else if (propertyType == typeof(double) && TryParseDouble(value, out var doubleValue))
                {
                    property.SetValue(target, doubleValue);
                }
                else if (propertyType == typeof(decimal) && TryParseDouble(value, out var decimalValue))
                {
                    property.SetValue(target, Convert.ToDecimal(decimalValue));
                }
                else if (propertyType == typeof(int) && TryParseDouble(value, out var intValue))
                {
                    property.SetValue(target, Convert.ToInt32(Math.Round(intValue, MidpointRounding.AwayFromZero)));
                }
                else if (propertyType == typeof(long) && TryParseDouble(value, out var longValue))
                {
                    property.SetValue(target, Convert.ToInt64(Math.Round(longValue, MidpointRounding.AwayFromZero)));
                }
                else if (propertyType == typeof(bool))
                {
                    property.SetValue(target, value is bool boolean
                        ? boolean
                        : bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) && parsed);
                }
                else if (propertyType == typeof(DateTime))
                {
                    if (value is DateTime dateTime)
                    {
                        property.SetValue(target, dateTime);
                    }
                    else if (DateTime.TryParse(Convert.ToString(value, CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsedDate))
                    {
                        property.SetValue(target, parsedDate);
                    }
                }
                else
                {
                    property.SetValue(target, Convert.ChangeType(value, propertyType, CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                if (property.PropertyType == typeof(string))
                {
                    property.SetValue(target, Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty);
                }
            }
        }

        private static bool TryParseDouble(object? value, out double number)
        {
            switch (value)
            {
                case null:
                    number = 0;
                    return false;
                case double typed:
                    number = typed;
                    return true;
                case float typed:
                    number = typed;
                    return true;
                case decimal typed:
                    number = (double)typed;
                    return true;
                case int typed:
                    number = typed;
                    return true;
                case long typed:
                    number = typed;
                    return true;
                default:
                    return double.TryParse(Convert.ToString(value, CultureInfo.CurrentCulture), NumberStyles.Any, CultureInfo.CurrentCulture, out number)
                        || double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out number);
            }
        }

        private static int ParseInt(string? value, int defaultValue)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed)
                || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : defaultValue;
        }

        private static double ParseDouble(string? value, double defaultValue)
        {
            return double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsed)
                || double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : defaultValue;
        }

        private static IEnumerable<int> ParseIntTokens(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            foreach (var token in value.Split([',', ';', ' ', '|', '/', '\\', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed)
                    || int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    yield return parsed;
                }
            }
        }

        private static void ApplyBankUserExtraFields(Bank bank, BankUser user, object vendorUser)
        {
            foreach (var column in bank.Columns.Where(item => !string.IsNullOrWhiteSpace(item.Field)))
            {
                var value = GetBankUserStringValue(user, column.Field!);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    SetStringPropertyIfBlank(vendorUser, column.Field!, value);
                }
            }
        }

        private static string GetBankUserStringValue(BankUser user, string field)
        {
            if (TryGetIndexerField(field, out var indexerField))
            {
                return user[indexerField];
            }

            var property = typeof(BankUser).GetProperty(field, BindingFlags.Instance | BindingFlags.Public);
            if (property is null)
            {
                return user[field];
            }

            return property.GetValue(user) switch
            {
                null => string.Empty,
                string text => text,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                var value => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            };
        }

        private static string ResolveFlowAccountValue(FlowAutoGenerationRequest request)
        {
            var accountColumn = request.Bank.FlowColumns
                .Where(item => string.Equals(item.Field, nameof(FlowRecord.Account), StringComparison.Ordinal))
                .FirstOrDefault(item => IsCardNumberName(item.Name) || IsAccountNumberName(item.Name));
            var preferCard = accountColumn is not null && IsCardNumberName(accountColumn.Name) && !IsAccountNumberName(accountColumn.Name);
            return ResolveBankUserAccountValue(request.Bank, request.BankUser, preferCard);
        }

        private static string ResolveBankUserAccountValue(Bank bank, BankUser user, bool preferCard)
        {
            return preferCard
                ? FirstNonEmpty(user.CardNo, FindBankUserColumnValue(bank, user, IsCardNumberName), user.AccountNo, FindBankUserColumnValue(bank, user, IsAccountNumberName))
                : FirstNonEmpty(user.AccountNo, FindBankUserColumnValue(bank, user, IsAccountNumberName), user.CardNo, FindBankUserColumnValue(bank, user, IsCardNumberName));
        }

        private static string FindBankUserColumnValue(Bank bank, BankUser user, Func<string?, bool> predicate)
        {
            foreach (var column in bank.Columns.Where(item => predicate(item.Name) && !string.IsNullOrWhiteSpace(item.Field)))
            {
                var value = GetBankUserStringValue(user, column.Field!);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static bool IsCardNumberName(string? name)
        {
            var value = NormalizeName(name ?? string.Empty);
            return value is "卡号" or "借记卡号" or "打印卡号" or "主卡卡号"
                || (value.Contains("卡号", StringComparison.Ordinal) && !value.Contains("账号", StringComparison.Ordinal) && !value.Contains("帐号", StringComparison.Ordinal));
        }

        private static bool IsAccountNumberName(string? name)
        {
            var value = NormalizeName(name ?? string.Empty);
            return value is "账号" or "帐号" or "账号卡号" or "卡号账户"
                or "客户账号" or "户口号" or "账户账号" or "账户号"
                or "客户账口" or "客户户口" or "支付宝账户" or "微信号";
        }

        private static string NormalizeName(string value)
        {
            return string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));
        }

        private static bool TryGetIndexerField(string field, out string indexerField)
        {
            if (field.Length >= 2 && field.StartsWith('[') && field.EndsWith(']'))
            {
                indexerField = field[1..^1];
                return true;
            }

            indexerField = string.Empty;
            return false;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value!;
                }
            }

            return string.Empty;
        }

        private static Type GetRequiredType(Assembly assembly, string name)
        {
            var metadataName = DecodeIlSpyMetadataName(name);
            var type = assembly.GetType(name, throwOnError: false)
                ?? assembly.GetType(metadataName, throwOnError: false)
                ?? SafeGetTypes(assembly).FirstOrDefault(item =>
                    IsMetadataName(item.FullName, name)
                    || IsMetadataName(item.Name, name));

            return type ?? throw new TypeLoadException($"真诚 DLL 中未找到类型：{name}");
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(item => item is not null)!;
            }
        }

        private static IEnumerable<MethodInfo> SafeMethods(Type type)
        {
            try
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            }
            catch
            {
                return [];
            }
        }

        private static MethodInfo? FindVendorFlowStringMethod(IEnumerable<MethodInfo> methods, Type flowType, string methodName)
        {
            return methods.FirstOrDefault(method =>
            {
                if (!IsMetadataName(method.Name, methodName) || method.ReturnType != typeof(string))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == flowType;
            });
        }

        private static bool IsMetadataName(string? actualName, string expectedIlSpyName)
        {
            return string.Equals(actualName, expectedIlSpyName, StringComparison.Ordinal)
                || string.Equals(actualName, DecodeIlSpyMetadataName(expectedIlSpyName), StringComparison.Ordinal);
        }

        private static string DecodeIlSpyMetadataName(string value)
        {
            var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Sum(item => item.Length + 1) - 1 != value.Length - 1)
            {
                return value;
            }

            var builder = new StringBuilder(parts.Length);
            foreach (var part in parts)
            {
                if (part.Length != 4 || !int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
                {
                    return value;
                }

                builder.Append((char)codePoint);
            }

            return builder.ToString();
        }
    }

    private sealed class PlanItemReader
    {
        private readonly MethodInfo accountTimeGetter;
        private readonly MethodInfo amountGetter;
        private readonly MethodInfo sourceKindGetter;
        private readonly MethodInfo sourceIndexGetter;
        private readonly MethodInfo moveFlagGetter;

        public PlanItemReader(Type itemType)
        {
            accountTimeGetter = FindGetter(itemType, typeof(DateTime));
            amountGetter = FindGetter(itemType, typeof(double));
            sourceKindGetter = FindGetter(itemType, typeof(int), "_0008");
            sourceIndexGetter = FindGetter(itemType, typeof(int), "_0002");
            moveFlagGetter = FindGetter(itemType, typeof(bool));
        }

        public VendorPlanItem Read(object item)
        {
            return new VendorPlanItem(
                (DateTime)accountTimeGetter.Invoke(item, null)!,
                (double)amountGetter.Invoke(item, null)!,
                (int)sourceKindGetter.Invoke(item, null)!,
                (int)sourceIndexGetter.Invoke(item, null)!,
                (bool)moveFlagGetter.Invoke(item, null)!);
        }

        private static MethodInfo FindGetter(Type type, Type returnType, string? methodName = null)
        {
            return type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method =>
                    method.GetParameters().Length == 0
                    && method.ReturnType == returnType
                    && (methodName is null || IsMetadataName(method.Name, methodName)))
                ?? throw new MissingMethodException(type.FullName, methodName ?? returnType.Name);
        }

        private static bool IsMetadataName(string actualName, string expectedIlSpyName)
        {
            return string.Equals(actualName, expectedIlSpyName, StringComparison.Ordinal)
                || string.Equals(actualName, DecodeIlSpyMetadataName(expectedIlSpyName), StringComparison.Ordinal);
        }

        private static string DecodeIlSpyMetadataName(string value)
        {
            var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Sum(item => item.Length + 1) - 1 != value.Length - 1)
            {
                return value;
            }

            var builder = new StringBuilder(parts.Length);
            foreach (var part in parts)
            {
                if (part.Length != 4 || !int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
                {
                    return value;
                }

                builder.Append((char)codePoint);
            }

            return builder.ToString();
        }
    }

    private sealed class VendorLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;
        private readonly string vendorDir;

        public VendorLoadContext(string mainAssemblyPath, string vendorDir)
        {
            resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            this.vendorDir = vendorDir;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var resolvedPath = resolver.ResolveAssemblyToPath(assemblyName);
            if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
            {
                return LoadFromAssemblyPath(resolvedPath);
            }

            var candidate = Path.Combine(vendorDir, assemblyName.Name + ".dll");
            return File.Exists(candidate)
                ? LoadFromAssemblyPath(candidate)
                : null;
        }
    }
}
