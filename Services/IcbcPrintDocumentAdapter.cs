using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using SpeedEmulator.Models;
using static SpeedEmulator.Services.NativePrintDocumentAdapter;

namespace SpeedEmulator.Services;

/// <summary>仅替换工行个人原生表格的数据单元格，保留原有版式和页眉页脚。</summary>
internal static class IcbcPrintDocumentAdapter
{
    private static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["账号"] = nameof(FlowRecord.Account), ["序号"] = nameof(FlowRecord.SequenceNum),
        ["币种"] = nameof(FlowRecord.Currency), ["钞汇"] = nameof(FlowRecord.CashCheck),
        ["摘要"] = nameof(FlowRecord.TradeExplain), ["地区"] = nameof(FlowRecord.AreaNum),
        ["对方户名"] = nameof(FlowRecord.OppositeUsername), ["对方账号"] = nameof(FlowRecord.OppositeAccount),
        ["交易场所"] = nameof(FlowRecord.TradePlace), ["渠道"] = nameof(FlowRecord.InterfacePage)
    };

    internal static bool SupportsTemplate(string name) => name is
        "工行个人电子版" or "工行个人电子版2" or "工行个人电子版3" or "工行个人电子版3_无支行" or
        "工行个人电子版4" or "工行个人电子版5" or "工行个人电子版6" or "工行个人电子版7" or "工行个人电子版_无支行" or
        "工行个人纸质版" or "工行个人纸质版2" or "工行个人纸质版3" or "工行个人纸质版_无支行";

    internal static object Prepare(object document, string template, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => NativePrintDocumentAdapter.Prepare(document, container => BindCells(container, template, rows));

    private static void BindCells(object container, string template, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var pages = Read(container, "Pages") as IEnumerable
            ?? throw new InvalidOperationException("工行模板页结构不受支持，已停止打印以避免字段错位。");
        var nextRow = 0;
        var childMethods = new Dictionary<Type, MethodInfo?>();
        foreach (var page in pages.Cast<object>())
        foreach (var table in Descendants(Read(page, "Content"), childMethods).Where(node => node.GetType().FullName == "QuestPDF.Elements.Table.Table"))
        {
            var cells = (Read(table, "Cells") as IEnumerable)?.Cast<object>().ToArray() ?? [];
            var headers = (Read(table, "HeaderCells") as IEnumerable)?.Cast<object>().ToArray() ?? [];
            var headerInBody = headers.Length == 0;
            if (headerInBody) headers = cells.Where(cell => Number(cell, "Row") == 1).ToArray();
            var columns = headers.ToDictionary(cell => Number(cell, "Column"), cell =>
                Regex.Replace(ReadText(cell, childMethods), @"\s+", string.Empty));
            if (!columns.Values.Contains("交易日期") || !columns.Values.Contains("收入/支出金额")) continue;
            foreach (var row in cells.Where(cell => !headerInBody || Number(cell, "Row") > 1)
                         .GroupBy(cell => Number(cell, "Row")).OrderBy(group => group.Key))
            {
                // 原生电子版会补齐整页空白网格，它们不是流水记录。
                if (row.All(cell => string.IsNullOrWhiteSpace(ReadText(cell, childMethods)))) continue;
                if (nextRow >= rows.Count) throw new InvalidOperationException("工行模板行数与流水明细不一致。");
                var values = rows[nextRow++];
                foreach (var cell in row)
                {
                    if (!columns.TryGetValue(Number(cell, "Column"), out var name) || !Fields.TryGetValue(name, out var field)) continue;
                    var value = values.TryGetValue(field, out var raw) ? Convert.ToString(raw, CultureInfo.CurrentCulture) ?? "" : "";
                    // 仅保留所选模板原有的账号隐藏格式，不补充或更换任何数字。
                    if (name == "对方账号" && value.Length > 8 && template is "工行个人电子版5" or "工行个人电子版6" or "工行个人电子版7")
                        value = value[..4] + "****" + value[^4..];
                    WriteText(cell, value, childMethods);
                }
            }
        }
        if (nextRow != rows.Count) throw new InvalidOperationException("未能完整绑定工行流水明细，已停止打印。");
    }

}
