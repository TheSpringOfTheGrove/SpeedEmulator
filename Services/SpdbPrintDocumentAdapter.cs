using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using SpeedEmulator.Models;
using static SpeedEmulator.Services.NativePrintDocumentAdapter;

namespace SpeedEmulator.Services;

/// <summary>修正浦发个人电子版已确认的原生跨列绑定。</summary>
internal static class SpdbPrintDocumentAdapter
{
    internal static object Prepare(object document, Bank bank, IReadOnlyList<FlowRecord> records)
        => NativePrintDocumentAdapter.Prepare(document, container => Bind(container, bank, records));

    private static void Bind(object container, Bank bank, IReadOnlyList<FlowRecord> records)
    {
        var pages = Read(container, "Pages") as IEnumerable
            ?? throw new InvalidOperationException("原生模板页结构不受支持，已停止打印。");
        var methods = new Dictionary<Type, MethodInfo?>();
        var nextRow = 0;
        foreach (var page in pages.Cast<object>())
        {
            Dictionary<int, string>? activeColumns = null;
            foreach (var table in Descendants(Read(page, "Header"), methods).Concat(Descendants(Read(page, "Content"), methods))
                         .Where(node => node.GetType().FullName == "QuestPDF.Elements.Table.Table").Distinct())
            {
                var cells = (Read(table, "Cells") as IEnumerable)?.Cast<object>().ToArray() ?? [];
                var headers = (Read(table, "HeaderCells") as IEnumerable)?.Cast<object>().ToArray() ?? [];
                var headerInBody = headers.Length == 0;
                if (headerInBody) headers = cells.Where(cell => Number(cell, "Row") == 1).ToArray();
                var columns = headers.ToDictionary(cell => Number(cell, "Column"), cell =>
                    Regex.Replace(ReadText(cell, methods), @"[^\p{IsCJKUnifiedIdeographs}]", string.Empty));
                if (columns.Values.Contains("交易摘要") && columns.Values.Contains("交易账号"))
                {
                    activeColumns = columns;
                }
                else
                {
                    // 部分原生版式把表头、各条明细放在不同的 Table 中。
                    if (activeColumns is null || cells.Select(cell => Number(cell, "Column")).Distinct().Count() != activeColumns.Count) continue;
                    columns = activeColumns;
                    headerInBody = false;
                }
                foreach (var row in cells.Where(cell => !headerInBody || Number(cell, "Row") > 1)
                             .GroupBy(cell => Number(cell, "Row")).OrderBy(group => group.Key))
                {
                    if (row.All(cell => string.IsNullOrWhiteSpace(ReadText(cell, methods)))) continue;
                    if (nextRow >= records.Count) throw new InvalidOperationException("原生模板行数与流水明细不一致。");
                    var source = records[nextRow++];
                    var values = FlowPrintFieldValues.Create(bank, source);
                    foreach (var cell in row)
                    {
                        if (!columns.TryGetValue(Number(cell, "Column"), out var name)) continue;
                        var value = name switch
                        {
                            "交易账号" => Convert.ToString(values.GetValueOrDefault(nameof(FlowRecord.Account))) ?? string.Empty,
                            "交易摘要" => ResolveTransactionSummary(values),
                            "交易名称" => ResolveTransactionName(values),
                            _ => null
                        };
                        if (value is not null) WriteText(cell, value, methods);
                    }
                }
            }
        }
        if (nextRow != records.Count) throw new InvalidOperationException("未能完整绑定原生模板流水明细，已停止打印。");
    }

    private static string ResolveTransactionName(IReadOnlyDictionary<string, object?> values)
    {
        var transactionName = Convert.ToString(values.GetValueOrDefault(nameof(FlowRecord.ProductName))) ?? string.Empty;
        return string.IsNullOrWhiteSpace(transactionName)
            ? Convert.ToString(values.GetValueOrDefault(nameof(FlowRecord.ProductBrief))) ?? string.Empty
            : transactionName;
    }

    private static string ResolveTransactionSummary(IReadOnlyDictionary<string, object?> values)
        => Convert.ToString(values.GetValueOrDefault(nameof(FlowRecord.SerialNum))) ?? string.Empty;
}
