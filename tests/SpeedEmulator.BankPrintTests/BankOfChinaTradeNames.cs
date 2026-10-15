using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

internal static partial class Program
{
    private static FlowRecord BankOfChinaNameRow(int index, string name, bool imported = false) => new()
    {
        Id = index, Index = index, BankUserId = 990200,
        AccountTime = new DateTime(2026, 3, 21, 9, 0, 0).AddMinutes(index),
        TradeMoney = 1, CreditAmount = 1, Balance = index, Currency = "人民币",
        ProductName = name, TradeExplain = "隐藏说明", ProductBrief = "摘要勿填",
        TradeCode = "代码勿填", Remark = "独立附言", Usage = "用途勿填", CashCheck = "钞汇勿填",
        IsDocumentImported = imported
    };

    private static void TestBankOfChinaTradeNames(List<Entry> catalog)
    {
        var count = 0;
        foreach (var entry in catalog.Where(entry => entry.Bank.Name == "中行" && entry.Bank.Type == BankTypes.Personal))
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        foreach (var imported in new[] { false, true })
        foreach (var name in new[] { "跨行转账", "" })
        {
            var row = BankOfChinaNameRow(1, name, imported);
            row.ExtraFields["ProductName"] = "未绑定旧名称";
            var before = JsonSerializer.Serialize(row);
            var context = Context(entry, row);
            var rows = Convert(context, exporter);
            var target = rows[0]!;
            var label = entry.Template.Name + "/" + exporter + "/" + imported;
            Equal(name, Value(target, "ProductName"), label + "/保留列表交易名称");
            Equal(name, Value(target, "TradeExplain"), label + "/原生交易名称只取列表交易名称");
            Equal(row.TradeCode, Value(target, "TradeCode"), label + "/不覆盖独立交易代码");
            Equal(row.ProductBrief, Value(target, "ProductBrief"), label + "/不覆盖摘要");
            Equal(row.Remark, Value(target, "Remark"), label + "/不覆盖附言");
            Equal(before, JsonSerializer.Serialize(row), label + "/不修改原流水");
            if (rows.Count != 1) throw new Exception(label + "/流水条数改变");
            count++;
        }

        // 自定义列表绑定也必须取实际单元格，包括用户清空后的空值。
        var bank = new Bank { Id = 18, Name = "中行", Type = BankTypes.Personal };
        bank.FlowColumns.Add(new ColumnDefinition { Name = "交易名称", Field = "[boc-name]" });
        var template = catalog.First(entry => entry.Template.Name == "中行个人电子版6").Template;
        foreach (var name in new[] { "自定义名称", "" })
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        {
            var row = BankOfChinaNameRow(1, "隐藏属性");
            row.ExtraFields["boc-name"] = name;
            var context = Context(new Entry(bank, template), row);
            Equal(name, Value(Convert(context, exporter)[0]!, "TradeExplain"), "中行交易名称遵循实际列表绑定，不回读隐藏属性");
            count++;
        }

        // 对公模板是独立字段定义，不属于个人交易名称的适配范围。
        foreach (var entry in catalog.Where(entry => entry.Bank.Name == "中行对公"))
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        {
            var row = BankOfChinaNameRow(1, "对公产品名称");
            Equal(row.TradeExplain, Value(Convert(Context(entry, row), exporter)[0]!, "TradeExplain"), "不改变中行对公交易说明");
            count++;
        }
        Console.WriteLine($"PASS 中行交易名称: {count} field cases (all personal templates, 3 exporters, generated/imported, names/blanks/custom bindings; corporate unchanged)");
    }

    private static void RenderBankOfChinaTradeNames(List<Entry> catalog)
    {
        var output = Path.Combine(Root, "tmp", "pdfs", "boc-name-fix-20261015");
        Directory.CreateDirectory(output);
        var report = new List<object>();
        foreach (var entry in catalog.Where(entry => entry.Bank.Name == "中行" && entry.Bank.Type == BankTypes.Personal))
        {
            var records = Enumerable.Range(1, 31).Select(index =>
                BankOfChinaNameRow(index, index % 3 == 0 ? "" : $"名称{index:000}", index % 2 == 0)).ToArray();
            var first = Context(entry, records[0]);
            first.BankUser.AccountName = "测试数据-不得用于证明";
            var context = new PrintRenderContext { Bank = first.Bank, BankUser = first.BankUser, Template = first.Template, Records = records };
            var before = JsonSerializer.Serialize(records);
            var path = Path.Combine(output, entry.Template.Name + ".pdf");
            var previous = Console.Out;
            try
            {
                Console.SetOut(TextWriter.Null);
                new ZhenchengPrintBridgeService().ExportAsync(context, path).GetAwaiter().GetResult();
            }
            finally { Console.SetOut(previous); }
            Equal(before, JsonSerializer.Serialize(records), "实际PDF导出不改原始流水");
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
            var text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
            foreach (var row in records.Where(row => row.ProductName.Length > 0))
                if (!text.Contains(row.ProductName)) throw new Exception(entry.Template.Name + "/PDF缺失交易名称 " + row.ProductName);
            foreach (var forbidden in new[] { "隐藏说明", "摘要勿填", "代码勿填", "用途勿填", "钞汇勿填" })
                if (text.Contains(forbidden)) throw new Exception(entry.Template.Name + "/PDF用其他字段填充了交易名称: " + forbidden);
            if (pdf.NumberOfPages < 2) throw new Exception("中行多页回归未覆盖分页: " + entry.Template.Name);
            report.Add(new { Template = entry.Template.Name, Path = path, Pages = pdf.NumberOfPages,
                Rows = records.Select(row => new { Time = row.AccountTime!.Value.ToString("HH:mm:ss"), Name = row.ProductName }).ToArray() });
            Console.WriteLine($"PASS PDF {entry.Template.Name}: {pdf.NumberOfPages} pages, 31 mixed rows");
        }
        File.WriteAllText(Path.Combine(output, "audit.json"), JsonSerializer.Serialize(report, Json));
        Console.WriteLine($"PASS {report.Count} BOC personal PDF templates: all source names present, no unrelated fallback text, source data unchanged");
    }
}
