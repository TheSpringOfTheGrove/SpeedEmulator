using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

internal static partial class Program
{
    private sealed record BindingCase(Entry Entry, string SourceColumn, string TargetField, string PrintedColumn);

    private static readonly string[] CcbRemarkTemplates = [
        "建行对公纸质版4", "建行对公纸质版5", "建行对公纸质版6", "建行对公纸质版8", "建行对公纸质版9", "建行对公纸质版12",
        "建行纸质版红章", "建行纸质版红章2", "建行纸质版红章3", "建行纸质版红章4"
    ];

    private static IEnumerable<BindingCase> RestoredCases(List<Entry> catalog)
    {
        foreach (var entry in catalog)
        {
            if (entry.Bank.Name == "民生") yield return new(entry, "交易机构", "BranchNum", "交易机构");
            // 电子版5的旧式 MRT 将同一 ProductBrief 列标作“用途”，不更改原版式。
            if (entry.Bank.Name == "工行对公") yield return new(entry, "摘要", "ProductBrief", entry.Template.Name == "工商对公电子版5" ? "用途" : "摘要");
            if (entry.Template.Name == "工行个人纸质版（工资）") yield return new(entry, "注释", "Remark", "摘要");
            if (entry.Bank.Name == "邮政" && entry.Template.Name.Contains("纸质版")) yield return new(entry, "交易类型", "ProductType", "交易类型");
            if (entry.Bank.Name == "邮政对公")
            {
                yield return new(entry, "摘要", "ProductBrief", "摘要");
                yield return new(entry, "附言", "Remark", "附言");
            }
            if (entry.Bank.Name == "建行对公" && CcbRemarkTemplates.Contains(entry.Template.Name)) yield return new(entry, "备注", "TradeExplain", "备注");
            if (entry.Template.Name is "兴业个人电子版9" or "兴业个人电子版10" or "兴业个人电子版14") yield return new(entry, "交易渠道", "TradeChannel", "交易地点");
        }
    }

    private static void PutColumn(Bank bank, FlowRecord row, string name, string value)
    {
        var column = bank.FlowColumns.Single(column => column.Name == name);
        var property = column.Field!.StartsWith('[') ? null : typeof(FlowRecord).GetProperty(column.Field);
        if (property is not null) property.SetValue(row, value);
        else row.ExtraFields[column.Field.Trim('[', ']')] = value;
    }

    private static string ColumnValue(Bank bank, FlowRecord row, string name)
    {
        var column = bank.FlowColumns.Single(column => column.Name == name);
        var property = column.Field!.StartsWith('[') ? null : typeof(FlowRecord).GetProperty(column.Field);
        return property is not null ? System.Convert.ToString(property.GetValue(row)) ?? "" : row.ExtraFields.GetValueOrDefault(column.Field.Trim('[', ']'), "");
    }

    private static FlowRecord BindingRow(int index, bool imported = false) => new()
    {
        Id = index, Index = index, BankUserId = 990200,
        AccountTime = new DateTime(2026, 3, 21, 10, 0, 0).AddMinutes(index),
        TradeMoney = 1, CreditAmount = 1, Balance = 1000 + index, Currency = "人民币", IncomeAttribute = "收入",
        ProductName = "种类勿借", ProductBrief = "摘要勿借", Remark = "附言勿借", TradeExplain = "说明勿借",
        NetNum = "机构勿借", BranchNum = "旧值勿借", TradePlace = "地点勿借", TradeChannel = "渠道勿借",
        ProductType = "旧值勿借", IsDocumentImported = imported
    };

    private static Bank WithCustomBinding(Bank bank, string name)
    {
        var result = new Bank { Id = bank.Id, Name = bank.Name, Type = bank.Type };
        result.Columns.AddRange(bank.Columns);
        result.FlowColumns.AddRange(bank.FlowColumns.Select(column => new ColumnDefinition
        {
            Name = column.Name, Field = column.Name == name ? "[custom-test-binding]" : column.Field,
            Type = column.Type, Width = column.Width, Show = column.Show
        }));
        return result;
    }

    private static void TestRestoredBankBindings(List<Entry> catalog)
    {
        var cases = RestoredCases(catalog).ToArray();
        if (cases.Select(item => item.Entry.Template.Name).Distinct().Count() != 31) throw new Exception("遗漏受影响模板");
        var count = 0;
        foreach (var item in cases)
        foreach (var custom in new[] { false, true })
        foreach (var imported in new[] { false, true })
        foreach (var value in new[] { "正确字段", "" })
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        {
            var bank = custom ? WithCustomBinding(item.Entry.Bank, item.SourceColumn) : item.Entry.Bank;
            var row = BindingRow(1, imported);
            PutColumn(bank, row, item.SourceColumn, value);
            var before = JsonSerializer.Serialize(row);
            var context = Context(new Entry(bank, item.Entry.Template), row);
            var converted = Convert(context, exporter);
            var target = converted[0]!;
            Equal(value, Value(target, item.TargetField), item.Entry.Template.Name + "/" + item.SourceColumn + "/" + exporter + "/empty/custom/import=" + custom + "/" + imported);
            if (bank.Name == "邮政对公")
            {
                Equal(ColumnValue(bank, row, "摘要"), Value(target, "ProductBrief"), "邮政对公摘要不能借用交易类型/附言");
                Equal(ColumnValue(bank, row, "附言"), Value(target, "Remark"), "邮政对公独立附言不能借用摘要");
            }
            Equal(before, JsonSerializer.Serialize(row), "修复只调整打印映射，不修改原始流水");
            if (converted.Count != 1) throw new Exception("打印改变流水条数");
            count++;
        }

        var postal = catalog.First(entry => entry.Bank.Name == "邮政对公");
        foreach (var summary in new[] { "公司摘要", "" })
        foreach (var note in new[] { "独立附言", "" })
        {
            var row = BindingRow(1);
            PutColumn(postal.Bank, row, "摘要", summary);
            PutColumn(postal.Bank, row, "附言", note);
            var context = Context(postal, row);
            Equal(summary, LocalValue(context, row, new PrintPdfColumn { Name = "摘要", Field = "ProductBrief" }), "本地邮政对公摘要");
            Equal(note, LocalValue(context, row, new PrintPdfColumn { Name = "附言", Field = "Remark" }), "本地邮政对公独立附言");
        }
        var salary = catalog.First(entry => entry.Template.Name == "工行个人纸质版（工资）");
        var salaryRow = BindingRow(1);
        PutColumn(salary.Bank, salaryRow, "注释", "工资摘要");
        Equal("工资摘要", LocalValue(Context(salary, salaryRow), salaryRow, new PrintPdfColumn { Name = "摘要", Field = "Remark" }), "本地工行工资摘要");
        Console.WriteLine($"PASS restored bindings: {cases.Length} field bindings / 31 templates / {count} conversion cases; blanks/custom/imported and local render paths");
    }

    private static void RenderRestoredBankBindings(List<Entry> catalog)
    {
        var output = Path.Combine(Root, "tmp", "pdfs", "restored-bank-fields-20261015");
        Directory.CreateDirectory(output);
        var report = new List<object>();
        var errors = new List<string>();
        foreach (var group in RestoredCases(catalog).GroupBy(item => item.Entry))
        {
            var entry = group.Key;
            var bindings = group.ToArray();
            // 对公纸质版没有“附言”列；仍验证桥接中的独立附言，但不能要求模板增列。
            var printedBindings = bindings.Where(binding => entry.Template.Name != "邮政对公纸质版" || binding.SourceColumn != "附言").ToArray();
            var records = Enumerable.Range(1, 32).Select(index =>
            {
                var row = BindingRow(index, index % 2 == 0);
                for (var field = 0; field < bindings.Length; field++)
                    PutColumn(entry.Bank, row, bindings[field].SourceColumn, index % 3 == 0 ? "" : $"V{field + 1}{index:000}Z");
                return row;
            }).ToArray();
            var first = Context(entry, records[0]);
            var context = new PrintRenderContext { Bank = first.Bank, BankUser = first.BankUser, Template = first.Template, Records = records };
            var before = JsonSerializer.Serialize(records);
            var path = Path.Combine(output, entry.Template.Name + ".pdf");
            var previous = Console.Out;
            try
            {
                Directory.SetCurrentDirectory(Path.Combine(Root, "VendorRuntime", "Zhencheng"));
                Console.SetOut(TextWriter.Null);
                new ZhenchengPrintBridgeService().ExportAsync(context, path).GetAwaiter().GetResult();
                Equal(before, JsonSerializer.Serialize(records), "PDF导出不改原始流水");
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
                var text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
                foreach (var binding in printedBindings)
                foreach (var row in records)
                {
                    var value = ColumnValue(entry.Bank, row, binding.SourceColumn);
                    if (value.Length > 0 && !text.Contains(value)) throw new Exception("PDF缺失 " + binding.PrintedColumn + ": " + value);
                }
                report.Add(new { Template = entry.Template.Name, Path = path, Pages = pdf.NumberOfPages,
                    Fields = printedBindings.Select(binding => new { binding.SourceColumn, binding.TargetField, binding.PrintedColumn }),
                    Rows = records.Select(row => new { Time = row.AccountTime!.Value.ToString("HH:mm:ss"),
                        Values = printedBindings.ToDictionary(binding => binding.PrintedColumn, binding => ColumnValue(entry.Bank, row, binding.SourceColumn)) }) });
                previous.WriteLine($"PASS PDF {entry.Template.Name}: {pdf.NumberOfPages} pages, 32 mixed populated/blank rows");
            }
            catch (Exception ex)
            {
                var error = entry.Template.Name + ": " + ex.GetBaseException().Message;
                errors.Add(error);
                report.Add(new { Template = entry.Template.Name, Error = error });
                previous.WriteLine("FAIL PDF " + error);
            }
            finally { Console.SetOut(previous); Directory.SetCurrentDirectory(Root); }
        }
        File.WriteAllText(Path.Combine(output, "audit.json"), JsonSerializer.Serialize(report, Json));
        if (errors.Count > 0) throw new Exception($"{errors.Count} PDF verification failures: " + string.Join("; ", errors));
        Console.WriteLine($"PASS {report.Count} affected PDF templates, source records unchanged");
    }
}
