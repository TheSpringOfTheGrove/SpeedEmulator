using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;
using SpeedEmulator.Repositories;
using SpeedEmulator.ViewModels;

internal static partial class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly string Root = Environment.GetEnvironmentVariable("SPEEDEMULATOR_TEST_ROOT")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string Output = Environment.GetEnvironmentVariable("SPEEDEMULATOR_TEST_OUTPUT")
        ?? Path.Combine(Root, "tmp", "bank-print-regressions");
    private static readonly Type Bridge = typeof(ZhenchengPrintBridgeService);
    private static readonly string[] Fields = ["OppositeAccount", "SequenceNum", "SerialNum", "Account", "ProductBrief", "Remark", "TradeExplain", "DepositTerm", "AgreedTerm", "NoticeType", "AppNum", "InterfacePage", "TradePlace", "NetNum", "BranchNum", "AreaNum", "TradeChannel", "Currency", "OppositeUsername"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.SetCurrentDirectory(Root);
        Directory.CreateDirectory(Output);
        Environment.SetEnvironmentVariable(ZhenchengRuntimeLocator.VendorDirEnvName, Path.Combine(Root, "VendorRuntime", "Zhencheng"));
        var banks = new[] { "LegacyPersonalBankIds", "LegacyCorporateBankIds" }.SelectMany(field =>
            ((IReadOnlyDictionary<string, long>)typeof(MainViewModel).GetField(field, Static)!.GetValue(null)!)
            .Select(pair => new Bank { Id = pair.Value, Name = pair.Key, Type = field.Contains("Personal") ? BankTypes.Personal : BankTypes.Corporate })).ToArray();
        foreach (var bank in banks) typeof(MainViewModel).GetMethod("ConfigureBankColumns", Static)!.Invoke(null, [bank]);
        var catalog = new List<Entry>();
        foreach (var bank in banks)
        {
            List<PrintTemplate> templates;
            var previous = Console.Out;
            try
            {
                Console.SetOut(TextWriter.Null);
                templates = (List<PrintTemplate>)typeof(JsonPrintTemplateRepository).GetMethod("CreateSystemTemplates", Static)!.Invoke(null, [bank])!;
            }
            finally { Console.SetOut(previous); }
            foreach (var template in templates) catalog.Add(new(bank, template));
            Console.WriteLine($"CATALOG {bank.Name}: {templates.Count} templates");
        }
        if (args.Contains("--boc-fields")) { TestBankOfChinaTradeNames(catalog); return; }
        if (args.Contains("--abc-fields")) { TestAgriculturalOutlets(catalog); return; }
        if (args.Contains("--restored-fields")) { TestRestoredBankBindings(catalog); return; }
        if (args.Contains("--cgb-fields")) { TestGuangfaMerchantMapping(catalog, args); return; }
        if (args.Contains("--bocom-fields")) { TestBocomPersonalPdf(catalog, args); return; }
        if (args.Contains("--wechat-fields")) { TestWechatPersonalStatementFields(catalog); return; }
        if (args.Contains("--ccb-serials")) { TestConstructionBankCorporateSerialNumbers(catalog, renderPdfs: true); return; }
        if (args.Contains("--spdb-name")) { TestSpdbTransactionNameFallback(catalog, renderPdf: true); return; }
        RunRegressions(catalog);
        if (args.Contains("--boc-render")) { RenderBankOfChinaTradeNames(catalog); return; }
        if (args.Contains("--abc-render")) { RenderAgriculturalOutlets(catalog); return; }
        if (args.Contains("--restored-render")) { RenderRestoredBankBindings(catalog); return; }
        if (args.Contains("--render-bound")) { RenderBound(catalog); return; }
        if (args.Contains("--render")) { Render(catalog); return; }

        var results = new List<object>();
        foreach (var entry in catalog)
        {
            foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
            foreach (var variant in new[] { "empty", "short", "distinct", "imported", "hidden" })
            {
                var source = Row(entry, variant);
                var context = Context(entry, source);
                try
                {
                    var before = JsonSerializer.Serialize(source);
                    var converted = Convert(context, exporter);
                    var target = converted[0]!;
                    VerifyValues(context, source, target);
                    if (converted.Count != 1 || before != JsonSerializer.Serialize(source)) throw new Exception("打印修改了原流水");
                    var changes = Fields.Select(field => new { Field = field, Source = Value(source, field), Target = Value(target, field) })
                        .Where(value => value.Source != value.Target).ToArray();
                    results.Add(new { Bank = entry.Bank.Name, entry.Bank.Id, Template = entry.Template.Name, entry.Template.VendorBankId, Variant = variant, Exporter = exporter,
                        PrintedHeaders = entry.Template.Config.Columns.Select(column => column.Name).ToArray(),
                        Changes = changes, SourceColumns = entry.Bank.FlowColumns.Select(column => new { column.Name, column.Field }).ToArray() });
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"{entry.Bank.Name}/{entry.Template.Name}/{exporter}/{variant}", ex.GetBaseException());
                }
            }
        }
        File.WriteAllText(Path.Combine(Output, "catalog.json"), JsonSerializer.Serialize(catalog.Select(entry => new { Bank = entry.Bank.Name, entry.Bank.Id, entry.Bank.Type, entry.Template.Name, entry.Template.VendorBankId, entry.Template.VendorId, entry.Template.PageRows, HasPdfData = !string.IsNullOrWhiteSpace(entry.Template.PdfData), Headers = entry.Template.Config.Columns.Select(column => column.Name).ToArray() }), Json));
        File.WriteAllText(Path.Combine(Output, "bridge-audit.json"), JsonSerializer.Serialize(results, Json));
        Console.WriteLine($"SCAN completed: {banks.Length} bank categories, {catalog.Count} system templates, {results.Count} bridge cases; no source data saved.");
    }

    private static FlowRecord Row(Entry entry, string variant)
    {
        var row = new FlowRecord
        {
            Index = 1, Id = 1, BankUserId = 990200, AccountTime = new DateTime(2026, 3, 21),
            Account = "1122334455667788990", TradeMoney = 53.56, Balance = 1053.56, CreditAmount = 53.56,
            ProductBrief = "结息", TradeExplain = "", Remark = "", Currency = "RMB", IncomeAttribute = "收入"
        };
        if (variant == "short") { row.OppositeAccount = "1234567890"; row.OppositeUsername = "测试商户"; }
        if (variant == "imported") row.IsDocumentImported = true;
        if (variant == "hidden") row.ExtraFields["OppositeAccount"] = "STALE-ACCOUNT";
        if (variant == "distinct")
        {
            row.ProductName = "类型辰"; row.VoucherNum = "EXT42";
            row.ProductBrief = "摘要甲"; row.Remark = "附言乙"; row.TradeExplain = "注释丙";
            row.TradePlace = "场所丁"; row.NetNum = "网点戊"; row.BranchNum = "机构己"; row.AreaNum = "地区庚";
            row.TradeChannel = "渠道辛"; row.InterfacePage = "界面壬"; row.OppositeBank = "对手行癸";
            row.SequenceNum = "321"; row.SerialNum = "654"; row.LogNum = "ABC1234";
            row.OppositeAccount = "1234567890"; row.OppositeUsername = "测试商户";
        }
        if (variant == "distinct")
        {
            var summaryColumn = entry.Bank.FlowColumns.FirstOrDefault(column => column.Name == "银行摘要");
            if (summaryColumn?.Field is { } binding) row.ExtraFields[binding.Trim('[', ']')] = "银行摘要卯";
        }
        return row;
    }

    private static PrintRenderContext Context(Entry entry, FlowRecord source) => new()
    {
        Bank = entry.Bank, Template = entry.Template, Records = [source],
        BankUser = new BankUser
        {
            Id = 990200, BankId = entry.Bank.Id, BankName = entry.Bank.Name, AccountName = "测试勿作凭证",
            AccountNo = "9988776655443322110", CardNo = "8877665544332211009", UserCode = "88888888",
            OpenBranch = "测试机构", StartDate = new DateTime(2026, 3, 1), EndDate = new DateTime(2026, 3, 31), Currency = "人民币",
            ExtraFields = new() { ["OperationArea"] = "9999", ["Operator"] = "888888", ["PrintBranch"] = "测试网点" }
        }
    };

    private static IList Convert(PrintRenderContext context, string exporter = "VendorBridge")
    {
        var core = Assembly.LoadFrom(Path.Combine(Root, "VendorRuntime", "Zhencheng", "caiwu-core.dll"));
        var flowType = core.GetType("caiwu_core.entity.GenerateFlowRecord")!;
        var type = Bridge.GetNestedType(exporter, BindingFlags.NonPublic)!;
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("flowType", Instance)!.SetValue(instance, flowType);
        type.GetField("flowListType", Instance)!.SetValue(instance, typeof(List<>).MakeGenericType(flowType));
        return (IList)type.GetMethod(exporter == "VendorBridge" ? "CreateVendorFlowRecords" : "CreateFlowRecords", Instance)!.Invoke(instance, [context])!;
    }

    private static string Value(object source, string field) => System.Convert.ToString(source.GetType().GetProperty(field)!.GetValue(source)) ?? "";

    private static void Render(List<Entry> catalog)
    {
        var samples = new (string Bank, string Template)[]
        {
            ("招行", "招行个人纸质版"), ("招行", "招行个人电子版13"),
            ("兴业", "兴业个人电子版11"), ("兴业", "兴业个人电子版14"), ("兴业", "兴业个人电子版2"),
            ("建行", "建行个人电子版13"), ("建行", "建行个人纸质版"),
            ("交行", "交通个人电子版2"), ("民生", "民生个人电子版"),
            ("光大", "光大个人电子版"), ("广发", "广发个人纸质版"),
            ("华夏", "华夏个人电子版"), ("浦发", "浦发个人纸质版"), ("浦发", "浦发个人电子版"),
            ("中信", "中信个人纸质版"), ("中信", "中信个人电子版2"),
            ("邮政", "邮政个人电子版6_新"), ("农行", "农行个人纸质版"), ("农行", "农行个人电子版"),
            ("中行", "中行个人电子版"), ("中行", "中行个人电子版6"), ("平安", "平安个人电子版"),
            ("建行对公", "建行对公纸质版3"), ("工行对公", "工行对公纸质版"), ("民生对公", "民生对公电子版"),
            ("支付宝", "支付宝个人版")
        };
        var directory = Path.Combine(Root, "tmp", "pdfs", "bank-print-regressions");
        Directory.CreateDirectory(directory);
        var outcomes = new List<object>();
        var onlyBank = Environment.GetEnvironmentVariable("SPEEDEMULATOR_AUDIT_ONLY");
        var unexpectedErrors = 0;
        foreach (var sample in samples)
        {
            if (!string.IsNullOrEmpty(onlyBank) && sample.Bank != onlyBank) continue;
            var entry = catalog.FirstOrDefault(entry => entry.Bank.Name == sample.Bank && entry.Template.Name == sample.Template);
            if (entry is null) { Console.WriteLine($"MISSING {sample}"); continue; }
            foreach (var variant in new[] { "empty", "short", "distinct", "imported" })
            {
                var path = Path.Combine(directory, entry.Bank.Name + "-" + entry.Template.Name + "-" + variant + ".pdf");
                try
                {
                    var source = Row(entry, variant);
                    var context = Context(entry, source);
                    if (!string.IsNullOrEmpty(onlyBank)) context.BankUser.AccountName = "测试";
                    var previous = Console.Out;
                    try { Console.SetOut(TextWriter.Null); new ZhenchengPrintBridgeService().ExportAsync(context, path).GetAwaiter().GetResult(); }
                    finally { Console.SetOut(previous); }
                    using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
                    var text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
                    VerifyRendered(entry, variant, text);
                    outcomes.Add(new { Bank = entry.Bank.Name, Template = entry.Template.Name, Variant = variant, Path = path, Pages = pdf.NumberOfPages, FakeNumber = text.Contains("6228480398401725673"), HasPaddedShort = text.Contains("123456789062284803"), Text = text });
                    Console.WriteLine($"PDF {entry.Bank.Name}/{entry.Template.Name}/{variant}: pages={pdf.NumberOfPages}, fake={text.Contains("6228480398401725673")}, padded={text.Contains("123456789062284803")}");
                }
                catch (Exception ex)
                {
                    outcomes.Add(new { Bank = entry.Bank.Name, Template = entry.Template.Name, Variant = variant, Error = ex.GetBaseException().Message });
                    if (entry.Bank.Name != "华夏" || !ex.GetBaseException().Message.Contains("QuestPDF, Version=2026.7.3.0")) unexpectedErrors++;
                    Console.WriteLine($"ERROR {sample}/{variant}: {ex.GetBaseException().Message}");
                }
            }
        }
        File.WriteAllText(Path.Combine(Output, string.IsNullOrEmpty(onlyBank) ? "render-audit.json" : "isolated-" + onlyBank + ".json"), JsonSerializer.Serialize(outcomes, Json));
        Console.WriteLine($"RENDER completed: {outcomes.Count} cases.");
        if (unexpectedErrors > 0) throw new Exception($"{unexpectedErrors} unexpected PDF regressions");
    }
    private sealed record Entry(Bank Bank, PrintTemplate Template);
}
