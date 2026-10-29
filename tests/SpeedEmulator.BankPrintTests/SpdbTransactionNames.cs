using System.IO;
using System.Reflection;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

internal static partial class Program
{
    private static void TestSpdbTransactionNameFallback(List<Entry> catalog, bool renderPdf = false)
    {
        var adapter = typeof(ZhenchengPrintBridgeService).Assembly.GetType(
            "SpeedEmulator.Services.SpdbPrintDocumentAdapter",
            throwOnError: true)!;
        var resolve = adapter.GetMethod("ResolveTransactionName", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(adapter.FullName, "ResolveTransactionName");
        var resolveSummary = adapter.GetMethod("ResolveTransactionSummary", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(adapter.FullName, "ResolveTransactionSummary");

        static Dictionary<string, object?> Values(string productName, string productBrief, string serialNum = "") => new()
        {
            [nameof(FlowRecord.ProductName)] = productName,
            [nameof(FlowRecord.ProductBrief)] = productBrief,
            [nameof(FlowRecord.SerialNum)] = serialNum
        };

        string Resolve(string productName, string productBrief) =>
            System.Convert.ToString(resolve.Invoke(null, [Values(productName, productBrief)])) ?? string.Empty;

        Equal("导入交易摘要", Resolve(string.Empty, "导入交易摘要"), "浦发导入数据交易名称应使用交易摘要");
        Equal("明确交易名称", Resolve("明确交易名称", "独立交易摘要"), "浦发明确交易名称不能被摘要覆盖");
        Equal(string.Empty, Resolve(string.Empty, string.Empty), "浦发交易名称和摘要均为空时保持空值");
        Equal(
            "90848041",
            System.Convert.ToString(resolveSummary.Invoke(null, [Values("", "交易摘要", "90848041")])) ?? string.Empty,
            "浦发PDF交易摘要列应使用流水号");

        if (!renderPdf)
        {
            Console.WriteLine("PASS SPDB electronic fields: transaction-name fallback / serial number in summary column");
            return;
        }

        var entry = catalog.Single(item => item.Bank.Name == "浦发" && item.Template.Name == "浦发个人电子版");
        const string importedSummary = "导入交易名称甲";
        const string importedSerial = "90848041";
        var imported = BindingRow(1, imported: true);
        imported.Account = "6216910101404255";
        imported.ProductName = string.Empty;
        imported.ProductBrief = importedSummary;
        imported.SerialNum = importedSerial;
        imported.OppositeUsername = "导入对手";
        imported.OppositeAccount = "6216910101404255";
        var generated = BindingRow(2, imported: false);
        generated.Account = "6217920951726152";
        generated.ProductName = "明确交易名称乙";
        generated.ProductBrief = "独立交易摘要丙";
        generated.SerialNum = "63079836";
        var first = Context(entry, imported);
        var context = new PrintRenderContext
        {
            Bank = first.Bank,
            BankUser = first.BankUser,
            Template = first.Template,
            Records = [imported, generated]
        };
        var before = JsonSerializer.Serialize(context.Records);
        var output = Path.Combine(Root, "tmp", "pdfs", "spdb-imported-transaction-name.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var previous = Console.Out;
        try
        {
            Directory.SetCurrentDirectory(Path.Combine(Root, "VendorRuntime", "Zhencheng"));
            Console.SetOut(TextWriter.Null);
            new ZhenchengPrintBridgeService().ExportAsync(context, output).GetAwaiter().GetResult();
        }
        finally
        {
            Console.SetOut(previous);
            Directory.SetCurrentDirectory(Root);
        }

        Equal(before, JsonSerializer.Serialize(context.Records), "浦发打印不得修改原始流水");
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        var text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
        foreach (var expected in new[] { importedSummary, importedSerial, "明确交易名称乙", "63079836" })
        {
            if (!text.Contains(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("浦发PDF缺少：" + expected);
            }
        }
        if (text.Contains("独立交易摘要丙", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("浦发PDF交易摘要列错误使用了交易摘要，预期显示流水号");
        }

        Console.WriteLine("PASS SPDB electronic PDF: transaction name and serial-backed summary column visible, source unchanged");
    }
}
