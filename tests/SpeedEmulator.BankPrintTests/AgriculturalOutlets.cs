using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

internal static partial class Program
{
    private static bool IsAgriculturalPaperEntry(Entry entry) => entry.Bank.Name == "农行"
        && entry.Bank.Type == BankTypes.Personal && entry.Template.Name.Contains("纸质版", StringComparison.Ordinal);

    private static FlowRecord AgriculturalOutletRow(int index, string place, bool imported = false) => new()
    {
        Id = index, Index = index, BankUserId = 990200,
        AccountTime = new DateTime(2026, 3, 1, 9, 0, 0).AddDays(index - 1),
        TradeMoney = 100 + index, CreditAmount = 100 + index, Balance = 1000 + index,
        Currency = "人民币", ProductBrief = "测试摘要", LogNum = $"T{index:00000000}",
        TradePlace = place, AreaNum = "888888", NetNum = "777777", BranchNum = "666666",
        TradeChannel = "超级网银", TradeChannelEn = "IBPS", Remark = "测试附言", IsDocumentImported = imported
    };

    private static void TestAgriculturalOutlets(List<Entry> catalog)
    {
        var count = 0;
        foreach (var entry in catalog.Where(IsAgriculturalPaperEntry))
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        foreach (var imported in new[] { false, true })
        foreach (var place in new[] { "139999", "009999", "" })
        {
            var row = AgriculturalOutletRow(1, place, imported);
            row.ExtraFields["TradePlace"] = "未绑定旧地点";
            var before = JsonSerializer.Serialize(row);
            var rows = Convert(Context(entry, row), exporter);
            var target = rows[0]!;
            var label = entry.Template.Name + "/" + exporter + "/" + imported;
            Equal(place, Value(target, "AreaNum"), label + "/交易网点只取明细地点，包括空值");
            Equal(place, Value(target, "TradePlace"), label + "/保留地点");
            Equal(row.NetNum, Value(target, "NetNum"), label + "/不覆盖独立网点号");
            Equal(row.BranchNum, Value(target, "BranchNum"), label + "/不覆盖机构号");
            Equal(before, JsonSerializer.Serialize(row), label + "/不修改原流水");
            if (rows.Count != 1) throw new Exception(label + "/流水条数改变");
            count++;
        }

        var bank = new Bank { Id = 11, Name = "农行", Type = BankTypes.Personal };
        bank.FlowColumns.Add(new ColumnDefinition { Name = "地点", Field = "[abc-place]" });
        var template = catalog.First(entry => entry.Template.Name == "农行个人纸质版").Template;
        foreach (var place in new[] { "009999", "" })
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        {
            var row = AgriculturalOutletRow(1, "隐藏地点");
            row.ExtraFields["abc-place"] = place;
            Equal(place, Value(Convert(Context(new Entry(bank, template), row), exporter)[0]!, "AreaNum"), "农行网点遵循实际绑定，不借用隐藏地点");
            count++;
        }
        foreach (var entry in catalog.Where(entry => entry.Bank.Name.StartsWith("农行", StringComparison.Ordinal) && !IsAgriculturalPaperEntry(entry)))
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        {
            var row = AgriculturalOutletRow(1, "139999");
            Equal(row.AreaNum, Value(Convert(Context(entry, row), exporter)[0]!, "AreaNum"), "不影响农行电子版或对公地区字段");
            count++;
        }

        foreach (var entry in catalog.Where(IsAgriculturalPaperEntry))
        {
            var row = AgriculturalOutletRow(1, "139999");
            row.OppositeAccount = "6228480398401725673";
            row.OppositeUsername = "测试商户";

            var target = Convert(Context(entry, row), "VendorBridge")[0]!;
            Equal(row.OppositeAccount, Value(target, nameof(FlowRecord.OppositeAccount)), entry.Template.Name + "/对方账号不补空格");
            Equal(row.OppositeUsername, Value(target, nameof(FlowRecord.OppositeUsername)), entry.Template.Name + "/对方户名不补空格");
            count++;
        }
        Console.WriteLine($"PASS 农行交易网点: {count} field cases (all paper templates, 3 exporters, generated/imported, leading zeros/blanks/custom bindings; electronic/corporate unchanged)");
    }

    private static void RenderAgriculturalOutlets(List<Entry> catalog)
    {
        var output = Path.Combine(Root, "tmp", "pdfs", "abc-outlet-fix-20261015");
        Directory.CreateDirectory(output);
        var report = new List<object>();
        foreach (var entry in catalog.Where(IsAgriculturalPaperEntry))
        {
            var records = Enumerable.Range(1, 34).Select(index => AgriculturalOutletRow(index,
                index % 3 == 0 ? "" : (index % 2 == 0 ? $"00{index:0000}" : $"13{index:0000}"), index % 2 == 0)).ToArray();
            var first = Context(entry, records[0]);
            first.BankUser.AccountName = "测试勿作凭证";
            first.BankUser.StartDate = new DateTime(2026, 3, 1);
            first.BankUser.EndDate = new DateTime(2026, 4, 3);
            var context = new PrintRenderContext { Bank = first.Bank, BankUser = first.BankUser, Template = first.Template, Records = records };
            var before = JsonSerializer.Serialize(records);
            var path = Path.Combine(output, entry.Template.Name + ".pdf");
            var previous = Console.Out;
            try { Console.SetOut(TextWriter.Null); new ZhenchengPrintBridgeService().ExportAsync(context, path).GetAwaiter().GetResult(); }
            finally { Console.SetOut(previous); }
            Equal(before, JsonSerializer.Serialize(records), "农行PDF导出不修改原始流水");
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
            var text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
            foreach (var row in records.Where(row => row.TradePlace.Length > 0))
                if (!text.Contains(row.TradePlace)) throw new Exception(entry.Template.Name + "/PDF缺失交易地点 " + row.TradePlace);
            foreach (var forbidden in new[] { "888888", "777777", "666666" })
                if (text.Contains(forbidden)) throw new Exception(entry.Template.Name + "/PDF网点借用了其他字段 " + forbidden);
            if (pdf.NumberOfPages < 2) throw new Exception(entry.Template.Name + "/未覆盖分页");
            report.Add(new { Template = entry.Template.Name, Path = path, Pages = pdf.NumberOfPages,
                Rows = records.Select(row => new { Date = row.AccountTime!.Value.ToString("yyyyMMdd"), Place = row.TradePlace }).ToArray() });
            Console.WriteLine($"PASS PDF {entry.Template.Name}: {pdf.NumberOfPages} pages, 34 mixed outlet rows");
        }
        File.WriteAllText(Path.Combine(output, "audit.json"), JsonSerializer.Serialize(report, Json));
        Console.WriteLine($"PASS {report.Count} ABC paper PDF templates: source outlets preserved, no location fallback, original data unchanged");
    }
}
