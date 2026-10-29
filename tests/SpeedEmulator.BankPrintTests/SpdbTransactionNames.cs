using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        static Dictionary<string, object?> Values(string productName, string productBrief) => new()
        {
            [nameof(FlowRecord.ProductName)] = productName,
            [nameof(FlowRecord.ProductBrief)] = productBrief
        };

        string Resolve(string productName, string productBrief) =>
            System.Convert.ToString(resolve.Invoke(null, [Values(productName, productBrief)])) ?? string.Empty;

        Equal("导入交易摘要", Resolve(string.Empty, "导入交易摘要"), "浦发导入数据交易名称应使用交易摘要");
        Equal("明确交易名称", Resolve("明确交易名称", "独立交易摘要"), "浦发明确交易名称不能被摘要覆盖");
        Equal(string.Empty, Resolve(string.Empty, string.Empty), "浦发交易名称和摘要均为空时保持空值");

        if (!renderPdf)
        {
            Console.WriteLine("PASS SPDB transaction-name fallback: imported summary / explicit name / blank values");
            return;
        }

        var entry = catalog.Single(item => item.Bank.Name == "浦发" && item.Template.Name == "浦发个人电子版");
        const string importedSummary = "导入交易名称甲";
        var imported = BindingRow(1, imported: true);
        imported.Account = "6216910101404255";
        imported.ProductName = string.Empty;
        imported.ProductBrief = importedSummary;
        imported.OppositeUsername = "导入对手";
        imported.OppositeAccount = "6216910101404255";
        var generated = BindingRow(2, imported: false);
        generated.Account = "6217920951726152";
        generated.ProductName = "明确交易名称乙";
        generated.ProductBrief = "独立交易摘要丙";
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
        if (Regex.Matches(text, Regex.Escape(importedSummary)).Count < 2)
        {
            throw new InvalidOperationException("浦发导入交易摘要未同时显示在交易名称和交易摘要列");
        }
        foreach (var expected in new[] { "明确交易名称乙", "独立交易摘要丙" })
        {
            if (!text.Contains(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("浦发PDF缺少：" + expected);
            }
        }

        Console.WriteLine("PASS SPDB imported transaction-name PDF: fallback visible, explicit name preserved, source unchanged");
    }
}
