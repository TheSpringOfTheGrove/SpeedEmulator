using System.Globalization;
using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

internal static partial class Program
{
    private const string CcbActualSerialNumber = "150430687JBVD6PEW3S";
    private const string CcbShortSerialNumber = "20148040";

    private static void TestConstructionBankCorporateSerialNumbers(List<Entry> catalog, bool renderPdfs = false)
    {
        var entries = catalog
            .Where(entry => entry.Bank.Name == "建行对公")
            .ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidOperationException("未找到建行对公模板");
        }

        var conversionCases = 0;
        foreach (var entry in entries)
        foreach (var imported in new[] { false, true })
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        {
            var row = BindingRow(1, imported);
            row.SerialNum = CcbActualSerialNumber;
            row.SequenceNum = "1";
            row.LogNum = "ROW-FALLBACK";
            var before = JsonSerializer.Serialize(row);
            var target = Convert(Context(entry, row), exporter)[0]!;

            Equal(CcbActualSerialNumber, RemoveInvisiblePrintCharacters(Value(target, "SerialNum")),
                $"{entry.Template.Name}/{exporter}/imported={imported}/SerialNum");
            Equal(CcbActualSerialNumber, RemoveInvisiblePrintCharacters(Value(target, "SequenceNum")),
                $"{entry.Template.Name}/{exporter}/imported={imported}/SequenceNum");
            Equal(before, JsonSerializer.Serialize(row), "建行对公打印桥接不得修改原始流水");
            conversionCases++;
        }

        var paperEntries = entries
            .Where(entry => entry.Template.Name is "建行对公纸质版8" or "建行对公纸质版9")
            .OrderBy(entry => entry.Template.Name, StringComparer.Ordinal)
            .ToArray();
        if (paperEntries.Length != 2)
        {
            throw new InvalidOperationException("建行对公纸质版8、9模板不完整");
        }

        foreach (var entry in paperEntries)
        foreach (var imported in new[] { false, true })
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        {
            var row = BindingRow(1, imported);
            row.SerialNum = CcbShortSerialNumber;
            row.SequenceNum = "1";
            row.LogNum = "ROW-FALLBACK";
            var target = Convert(Context(entry, row), exporter)[0]!;
            Equal(CcbShortSerialNumber, RemoveInvisiblePrintCharacters(Value(target, "SerialNum")),
                $"{entry.Template.Name}/{exporter}/imported={imported}/short SerialNum");
            Equal(CcbShortSerialNumber, RemoveInvisiblePrintCharacters(Value(target, "SequenceNum")),
                $"{entry.Template.Name}/{exporter}/imported={imported}/short SequenceNum");
            conversionCases++;
        }

        var displayTemplateCount = entries.Count(entry => entry.Template.Config.Columns.Any(column =>
            column.Name.Contains("流水号", StringComparison.Ordinal)));
        if (!renderPdfs)
        {
            Console.WriteLine($"PASS CCB corporate serial bridge: {entries.Length} templates / {displayTemplateCount} serial-column templates / {conversionCases} cases; no P-row fallback");
            return;
        }

        var output = Path.Combine(Root, "tmp", "pdfs", "ccb-corporate-serials");
        Directory.CreateDirectory(output);
        foreach (var entry in entries.OrderBy(entry => entry.Template.Name, StringComparer.Ordinal))
        {
            var shortRow = BindingRow(1, imported: false);
            shortRow.SerialNum = CcbShortSerialNumber;
            shortRow.SequenceNum = "1";
            shortRow.LogNum = "ROW-FALLBACK";
            var importedRow = BindingRow(2, imported: true);
            importedRow.SerialNum = CcbActualSerialNumber;
            importedRow.SequenceNum = "2";
            importedRow.LogNum = "ROW-FALLBACK";
            var firstContext = Context(entry, shortRow);
            var context = new PrintRenderContext
            {
                Bank = firstContext.Bank,
                BankUser = firstContext.BankUser,
                Template = firstContext.Template,
                Records = [shortRow, importedRow]
            };
            var path = Path.Combine(output, entry.Template.Name + ".pdf");
            var previous = Console.Out;
            try
            {
                Directory.SetCurrentDirectory(Path.Combine(Root, "VendorRuntime", "Zhencheng"));
                Console.SetOut(TextWriter.Null);
                new ZhenchengPrintBridgeService().ExportAsync(context, path).GetAwaiter().GetResult();
            }
            finally
            {
                Console.SetOut(previous);
                Directory.SetCurrentDirectory(Root);
            }

            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
            var text = RemoveInvisiblePrintCharacters(string.Join("\n", pdf.GetPages().Select(page => page.Text)));
            var displaysTransactionSerial = entry.Template.Config.Columns.Any(column =>
                column.Name.Contains("流水号", StringComparison.Ordinal));
            if (displaysTransactionSerial
                && (!text.Contains(CcbShortSerialNumber, StringComparison.Ordinal)
                    || !text.Contains(CcbActualSerialNumber, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(entry.Template.Name + " PDF未显示真实交易流水号");
            }
            if (text.Contains("P000000001", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(entry.Template.Name + " PDF仍显示行号兜底");
            }
        }

        Console.WriteLine($"PASS CCB corporate serials: {entries.Length} templates / {displayTemplateCount} serial-column templates / {conversionCases} bridge cases / {entries.Length} mixed-length PDFs; no P-row fallback");
    }

    private static string RemoveInvisiblePrintCharacters(string value)
    {
        return string.Concat(value.Where(character =>
            !char.IsWhiteSpace(character)
            && char.GetUnicodeCategory(character) != UnicodeCategory.Format));
    }
}
