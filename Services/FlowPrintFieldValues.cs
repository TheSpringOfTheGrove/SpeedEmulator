using System.Reflection;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>读取明细列表实际绑定的数据；空值也是数据，不以历史扩展字段或其他列替代。</summary>
internal static class FlowPrintFieldValues
{
    internal static string IcbcOperationArea(PrintRenderContext context)
    {
        var column = context.Bank.Columns.FirstOrDefault(column => column.Name == "操作地区" && !string.IsNullOrWhiteSpace(column.Field));
        var binding = column?.Field ?? "OperationArea";
        var property = binding.StartsWith('[') ? null : typeof(BankUser).GetProperty(binding);
        if (property is not null) return Convert.ToString(property.GetValue(context.BankUser)) ?? string.Empty;
        var key = binding.Trim('[', ']');
        // 已配置的空值具有明确含义，不能从流水或其他用户字段兜底。
        if (context.BankUser.ExtraFields.TryGetValue(key, out var value)) return value?.Trim() ?? string.Empty;
        return column is null ? context.BankUser.ExtraFields.GetValueOrDefault("操作地区", string.Empty)?.Trim() ?? string.Empty : string.Empty;
    }

    internal static object? ReadColumn(Bank bank, FlowRecord record, string name)
    {
        var column = bank.FlowColumns.FirstOrDefault(column => column.Name == name && !string.IsNullOrWhiteSpace(column.Field));
        return column is null ? null : Read(record, column.Field!);
    }

    internal static object? Read(FlowRecord record, string binding)
    {
        var property = binding.StartsWith('[') ? null : typeof(FlowRecord).GetProperty(binding);
        return property is not null
            ? property.GetValue(record)
            : record.ExtraFields.GetValueOrDefault(binding.Trim('[', ']'), string.Empty);
    }

    internal static Dictionary<string, object?> Create(Bank bank, FlowRecord record)
    {
        var values = typeof(FlowRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToDictionary(property => property.Name, property => property.GetValue(record), StringComparer.OrdinalIgnoreCase);
        var directFields = bank.FlowColumns.Where(column => !string.IsNullOrWhiteSpace(column.Field)
                && typeof(FlowRecord).GetProperty(column.Field!) is not null)
            .Select(column => column.Field!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in bank.FlowColumns.Where(column => !string.IsNullOrWhiteSpace(column.Field)))
        {
            var (field, _) = ExcelColumnFieldResolver.ResolveFlowRecordField(bank.Name, column.Name ?? string.Empty);
            if (string.IsNullOrWhiteSpace(field)) continue;
            // “摘要2”“银行摘要”等扩展列可能被旧名称解析器识别成摘要，
            // 但它们不能覆盖另一列明确绑定的 ProductBrief（包括明确的空值）。
            if (column.Field != field && directFields.Contains(field)) continue;
            values[field] = Read(record, column.Field!);
        }
        return values;
    }
}
