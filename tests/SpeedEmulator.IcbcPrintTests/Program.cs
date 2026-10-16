using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

internal static class Program
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly Type Bridge = typeof(ZhenchengPrintBridgeService);
    private static readonly BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string[] Templates =
    [
        "工行个人电子版", "工行个人电子版2", "工行个人电子版3", "工行个人电子版3_无支行",
        "工行个人电子版4", "工行个人电子版5", "工行个人电子版6", "工行个人电子版7", "工行个人电子版_无支行",
        "工行个人纸质版", "工行个人纸质版2", "工行个人纸质版3", "工行个人纸质版_无支行"
    ];

    [STAThread]
    private static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable(ZhenchengRuntimeLocator.VendorDirEnvName, Path.Combine(Root, "VendorRuntime", "Zhencheng"));
        if (args.Contains("--probe"))
        {
            RenderSamples("before", verify: false);
            return;
        }

        var tests = new (string Name, Action Test)[]
        {
            ("工行空账号、短账号及原始序号不补号", TestNumbers),
            ("工行摘要仅取注释，不挪用附言及其他字段", TestSummary),
            ("工行空值不填入其他字段，已设置值原样保留", TestExactFields),
            ("工行三条打印桥接路径保持相同数据", TestBridgePaths),
            ("旧版明细字段别名仍可读取且不会串列", TestLegacyAliases),
            ("未绑定的历史扩展字段不能覆盖明细空值", TestHiddenExtras),
            ("自定义模板与其他银行不进入原生表格适配", TestNativeScope),
            ("其他银行与工行对公同样不补造账号", TestScope),
            ("打印桥接不修改源流水且不改变条数金额", TestSourceUnchanged)
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine($"{tests.Length}/{tests.Length} ICBC bridge tests passed.");
        if (args.Contains("--render")) RenderSamples("after", verify: true);
    }

    private static PrintRenderContext Context(string template = "工行个人纸质版", params FlowRecord[] records) => new()
    {
        Bank = new Bank { Id = 4, Name = "工行", Type = BankTypes.Personal },
        BankUser = new BankUser
        {
            Id = 990104, BankId = 4, BankName = "工行", AccountName = "测试数据-不得用于证明",
            AccountNo = "TEST-ACCOUNT", CardNo = "TEST-CARD", UserCode = "TEST-ONLY",
            StartDate = new DateTime(2026, 3, 19), EndDate = new DateTime(2026, 3, 22),
            ExtraFields = new() { ["OperationArea"] = "0605" }
        },
        Records = records,
        Template = new PrintTemplate
        {
            Name = template, BankId = 4, VendorBankId = 62, IsSystem = true, PageRows = 25,
            Config = new PrintPdfConfig { Descending = false }
        }
    };

    private static FlowRecord Row(int index = 1) => new()
    {
        Index = index, Id = index, BankId = 4, BankUserId = 990104,
        AccountTime = new DateTime(2026, 3, 20, 0, 0, 0).AddMinutes(index - 1),
        TradeMoney = index == 1 ? 53.56 : -12.34, Balance = index == 1 ? 53.56 : 41.22,
        Currency = "", TradeExplain = index == 1 ? "利息" : "消费",
        ProductBrief = "原摘要", Remark = "个人活期结息", Account = "ROW-ACCOUNT",
        SequenceNum = "", OppositeAccount = index == 1 ? "" : "1000107101"
    };

    private static object Call(string method, params object?[] args) =>
        Bridge.GetMethod(method, Static)!.Invoke(null, args)!;

    private static IList Convert(PrintRenderContext context, string exporter = "VendorBridge")
    {
        // Only exercise record preparation; avoid exporter constructors and database access.
        var core = Assembly.LoadFrom(Path.Combine(Root, "VendorRuntime", "Zhencheng", "caiwu-core.dll"));
        var recordType = core.GetType("caiwu_core.entity.GenerateFlowRecord")!;
        var exporterType = Bridge.GetNestedType(exporter, BindingFlags.NonPublic)!;
        var instance = RuntimeHelpers.GetUninitializedObject(exporterType);
        exporterType.GetField("flowType", Instance)!.SetValue(instance, recordType);
        exporterType.GetField("flowListType", Instance)!.SetValue(instance, typeof(List<>).MakeGenericType(recordType));
        var method = exporter == "VendorBridge" ? "CreateVendorFlowRecords" : "CreateFlowRecords";
        return (IList)exporterType.GetMethod(method, Instance)!.Invoke(instance, [context])!;
    }

    private static string Value(object record, string field) =>
        System.Convert.ToString(record.GetType().GetProperty(field)!.GetValue(record)) ?? "";

    private static void Equal(string expected, string actual, string field)
    {
        if (expected != actual) throw new InvalidOperationException($"{field}: expected '{expected}', actual '{actual}'.");
    }

    private static void TestNumbers()
    {
        foreach (var template in Templates)
        foreach (var account in new[] { "", "1000107101", "243300133", "6228483971039234116", "AB-001" })
        {
            var source = Row();
            source.OppositeAccount = account;
            source.SequenceNum = "007";
            source.SerialNum = "HIDDEN-SERIAL";
            var target = Convert(Context(template, source))[0]!;
            Equal(account, Value(target, nameof(FlowRecord.OppositeAccount)), template + "/account");
            Equal("007", Value(target, nameof(FlowRecord.SerialNum)), template + "/serial");
            Equal(source.Account, Value(target, nameof(FlowRecord.Account)), template + "/own-account");
        }
    }

    private static void TestSummary()
    {
        foreach (var template in Templates)
        foreach (var comment in new[] { "", "利息", "消费", "自定义注释" })
        {
            var source = Row();
            source.TradeExplain = comment;
            var target = Convert(Context(template, source))[0]!;
            Equal(comment, Value(target, nameof(FlowRecord.TradeExplain)), template + "/comment");
            Equal(comment, Value(target, nameof(FlowRecord.Remark)), template + "/native-summary");
            Equal("", Value(target, nameof(FlowRecord.AgreedTerm)), template + "/agreed-term");
            Equal("", Value(target, nameof(FlowRecord.InterfacePage)), template + "/channel");
        }
    }

    private static void TestExactFields()
    {
        foreach (var populated in new[] { false, true })
        {
            var source = Row();
            foreach (var field in new[] { "DepositTerm", "AgreedTerm", "NoticeType", "AppNum", "AreaNum", "NetNum", "BranchNum", "InterfacePage", "TradePlace", "OppositeUsername", "Currency", "CashCheck", "TradeCode" })
                typeof(FlowRecord).GetProperty(field)!.SetValue(source, populated ? "V-" + field : "");
            var target = Convert(Context(records: source))[0]!;
            foreach (var field in new[] { "DepositTerm", "AgreedTerm", "NoticeType", "AppNum", "NetNum", "BranchNum", "InterfacePage", "TradePlace", "OppositeUsername", "Currency", "CashCheck", "TradeCode" })
                Equal(populated ? "V-" + field : "", Value(target, field), field);
            Equal("0605", Value(target, "AreaNum"), "地区取用户操作地区，不取流水地区号");
        }
    }

    private static void TestBridgePaths()
    {
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        {
            var target = Convert(Context(records: Row()), exporter)[0]!;
            Equal("", Value(target, "OppositeAccount"), exporter);
            Equal("利息", Value(target, "Remark"), exporter);
            Equal("", Value(target, "AgreedTerm"), exporter);
            Equal("", Value(target, "SerialNum"), exporter);
        }
    }

    private static void TestScope()
    {
        foreach (var (name, type, template, expected) in new[]
        {
            ("招行", BankTypes.Personal, "招行个人纸质版", ""),
            ("中行", BankTypes.Personal, "中行个人电子版", ""),
            ("农行", BankTypes.Personal, "农行个人电子版", ""),
            ("工行对公", BankTypes.Corporate, "工行对公纸质版", "")
        })
        {
            var context = Context(template, Row());
            context.Bank.Id = 999;
            context.Bank.Name = name;
            context.Bank.Type = type;
            context.Template.BankId = 999;
            context.Template.VendorBankId = 999;
            var values = Call("CreateValueMap", context.Records[0]);
            Equal(expected, (string)Call("ResolveVendorOppositeAccount", context, context.Records[0], values), name);
        }
    }

    private static void TestLegacyAliases()
    {
        var row = Row();
        row.TradeExplain = "";
        var context = Context(records: row);
        var key = (string)Call("CreateLegacyFlowRecordExtraFieldPath", "工行", "注释", 0);
        context.Bank.FlowColumns.Add(new ColumnDefinition { Name = "注释", Field = $"[{key}]" });
        row.ExtraFields[key] = "旧注释";
        var target = Convert(context)[0]!;
        Equal("旧注释", Value(target, "Remark"), "legacy-summary");
        Equal("", Value(target, "AgreedTerm"), "legacy-agreed-term");
    }

    private static void TestHiddenExtras()
    {
        var row = Row();
        row.TradeExplain = "";
        row.ExtraFields["OppositeAccount"] = "HIDDEN-ACCOUNT";
        row.ExtraFields["TradeExplain"] = "HIDDEN-COMMENT";
        row.ExtraFields["SequenceNum"] = "HIDDEN-SEQ";
        var context = Context(records: row);
        context.Bank.FlowColumns.Add(new ColumnDefinition { Name = "注释", Field = "TradeExplain" });
        var target = Convert(context)[0]!;
        Equal("", Value(target, "OppositeAccount"), "hidden-account");
        Equal("", Value(target, "Remark"), "hidden-comment");
        Equal("", Value(target, "SequenceNum"), "hidden-sequence");
    }

    private static void TestNativeScope()
    {
        foreach (var (bankName, bankType, template, payload) in new[]
        {
            ("招行", BankTypes.Personal, "工行个人纸质版", ""),
            ("工行对公", BankTypes.Corporate, "工行个人纸质版", ""),
            ("工行", BankTypes.Personal, "工行个人纸质版-自定义", ""),
            ("工行", BankTypes.Personal, "工行个人纸质版", "custom-stimulsoft-data")
        })
        {
            var context = Context(template, Row());
            context.Bank.Name = bankName;
            context.Bank.Type = bankType;
            context.Template.PdfData = payload;
            var original = new object();
            if (!ReferenceEquals(original, Call("PrepareIcbcPersonalDocument", context, original)))
                throw new InvalidOperationException("Unexpected native document adapter scope.");
            if (bankName == "工行")
                Equal("个人活期结息", Value(Convert(context)[0]!, "Remark"), "custom-remark");
        }
    }

    private static void TestSourceUnchanged()
    {
        var rows = new[] { Row(), Row(2) };
        var before = JsonSerializer.Serialize(rows);
        var target = Convert(Context(records: rows));
        Equal(before, JsonSerializer.Serialize(rows), "source-mutated");
        if (target.Count != rows.Length) throw new InvalidOperationException("Record count changed.");
        for (var i = 0; i < rows.Length; i++)
        {
            Equal(rows[i].TradeMoney.ToString()!, Value(target[i]!, "TradeMoney"), "amount");
            Equal(rows[i].Balance.ToString()!, Value(target[i]!, "Balance"), "balance");
        }
    }

    private static void RenderSamples(string phase, bool verify)
    {
        var output = Path.Combine(Root, "tmp", "pdfs", "icbc-source-fields", phase);
        Directory.CreateDirectory(output);
        foreach (var template in Templates)
        {
            var path = Path.Combine(output, template + ".pdf");
            var rows = Enumerable.Range(1, 28).Select(index =>
            {
                var row = Row(index);
                if (index == 2)
                {
                    row.Account = "ROW-2";
                    row.SequenceNum = "7";
                    row.AreaNum = "1234";
                    row.Currency = "USD";
                    row.CashCheck = "钞";
                    row.InterfacePage = "手机银行";
                    row.TradePlace = "测试场所";
                    row.OppositeUsername = "测试商户";
                }
                if (index == 3)
                {
                    row.TradeExplain = "";
                    row.OppositeAccount = "ABC";
                }
                return row;
            }).ToArray();
            var context = Context(template, rows);
            new ZhenchengPrintBridgeService().ExportAsync(context, path).GetAwaiter().GetResult();
            using var document = UglyToad.PdfPig.PdfDocument.Open(path);
            var text = string.Join("\n", document.GetPages().Select(page => page.Text));
            Console.WriteLine($"RENDER {template}: {document.NumberOfPages} pages, {rows.Length} rows");
            if (verify && (text.Contains("6228480398401725673") || text.Contains("个人活期结息")))
                throw new InvalidOperationException($"{template} still contains a fallback value.");
            if (verify && (text.Contains("TEST-ACCOUNT") || text.Contains("(空)") || !text.Contains("ROW-2") || !text.Contains("USD") || !text.Contains("0605")))
                throw new InvalidOperationException($"{template} still uses template defaults.");
        }
        if (verify)
        {
            var context = Context("工行个人纸质版", Row(), Row(2));
            context.Template.Config.Descending = true;
            new ZhenchengPrintBridgeService().ExportAsync(context, Path.Combine(output, "descending.pdf")).GetAwaiter().GetResult();
            var row = Row();
            row.AreaNum = "4906";
            context = Context("工行个人纸质版", row);
            context.BankUser.ExtraFields["OperationArea"] = "";
            new ZhenchengPrintBridgeService().ExportAsync(context, Path.Combine(output, "area-empty.pdf")).GetAwaiter().GetResult();
        }
    }
}
