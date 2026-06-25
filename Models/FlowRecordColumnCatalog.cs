using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Models;

public static class FlowRecordColumnCatalog
{
    private static readonly string[] FlowPageBaseColumns =
    [
        "记账时间",
        "交易金额",
        "账户余额"
    ];

    private static readonly HashSet<string> FlowRuleControlColumns = new(StringComparer.Ordinal)
    {
        "ID",
        "选择",
        "收支属性",
        "最小金额",
        "最大金额",
        "小数位",
        "开始时间",
        "结束时间",
        "每月出现次数",
        "节假日交易",
        "周六日交易",
        "固定添加日",
        "次数"
    };

    private static readonly HashSet<string> ExactAmountColumns = new(StringComparer.Ordinal)
    {
        "收入",
        "支出",
        "存入",
        "支取",
        "借方",
        "贷方",
        "金额",
        "余额"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<Dictionary<string, IReadOnlyList<string>>> FlowColumns = new(
        ParseFlowPageColumns);

    private static readonly Lazy<Dictionary<string, IReadOnlyList<string>>> ExportFlowColumns = new(
        () => ParseColumns("zhencheng-flow-export-columns.json"));

    private static readonly Lazy<Dictionary<string, IReadOnlyList<string>>> ExportUserColumns = new(
        () => ParseColumns("zhencheng-flow-export-user-columns.json"));

    public static bool TryGetFlowColumns(string bankName, out IReadOnlyList<string> columns)
    {
        return TryGetColumns(FlowColumns.Value, bankName, out columns);
    }

    public static bool TryGetExportUserColumns(string bankName, out IReadOnlyList<string> columns)
    {
        return TryGetColumns(ExportUserColumns.Value, bankName, out columns);
    }

    public static bool TryGetExportFlowColumns(string bankName, out IReadOnlyList<string> columns)
    {
        return TryGetColumns(ExportFlowColumns.Value, bankName, out columns);
    }

    private static bool TryGetColumns(
        IReadOnlyDictionary<string, IReadOnlyList<string>> source,
        string bankName,
        out IReadOnlyList<string> columns)
    {
        if (source.TryGetValue(bankName, out var configuredColumns) && configuredColumns.Count > 0)
        {
            columns = configuredColumns;
            return true;
        }

        columns = [];
        return false;
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseColumns(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", fileName);
        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, fileName);
        }

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<ColumnDocument>(stream, JsonOptions);
            return document?.Banks
                .Where(item => item.Value.Count > 0)
                .ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value, StringComparer.OrdinalIgnoreCase)
                ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseFlowPageColumns()
    {
        var configuredColumns = ParseColumns("zhencheng-flow-page-columns.json");
        if (configuredColumns.Count > 0)
        {
            return configuredColumns;
        }

        return ParseFlowPageColumnsFromRuleColumns();
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseFlowPageColumnsFromRuleColumns()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "zhencheng-flow-rule-columns.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "zhencheng-flow-rule-columns.json");
        }

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<FlowRuleColumnDocument>(stream, JsonOptions);
            if (document?.Banks.Count is not > 0)
            {
                return [];
            }

            return document.Banks
                .Select(item => new
                {
                    item.Key,
                    Columns = CreateFlowPageColumns(item.Value)
                })
                .Where(item => item.Columns.Count > 0)
                .ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Columns, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    private static List<string> CreateFlowPageColumns(FlowRuleColumnSet source)
    {
        var columns = new List<string>(FlowPageBaseColumns);
        var ruleColumns = source.Reference.Count > 0 ? source.Reference : source.Const;

        foreach (var column in ruleColumns)
        {
            if (ShouldSkipFlowPageColumn(column))
            {
                continue;
            }

            var normalizedColumn = NormalizeFlowPageColumnName(column);
            if (columns.Contains(normalizedColumn, StringComparer.Ordinal))
            {
                continue;
            }

            columns.Add(normalizedColumn);
        }

        return columns;
    }

    private static bool ShouldSkipFlowPageColumn(string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return true;
        }

        var normalizedColumn = NormalizeFlowPageColumnName(columnName.Trim());
        if (FlowRuleControlColumns.Contains(normalizedColumn))
        {
            return true;
        }

        if (ExactAmountColumns.Contains(normalizedColumn))
        {
            return true;
        }

        return normalizedColumn.Contains("日期", StringComparison.Ordinal)
            || normalizedColumn.Contains("时间", StringComparison.Ordinal)
            || normalizedColumn.Contains("金额", StringComparison.Ordinal)
            || normalizedColumn.Contains("余额", StringComparison.Ordinal)
            || normalizedColumn.Contains("发生额", StringComparison.Ordinal);
    }

    private static string NormalizeFlowPageColumnName(string columnName)
    {
        return columnName.Trim() switch
        {
            "贷币" => "货币",
            "转现标志" => "现转标志",
            "对方开户行银联号" => "对方开户行联行号",
            "产品业务种类" => "业务产品种类",
            _ => columnName.Trim()
        };
    }

    private sealed class ColumnDocument
    {
        public Dictionary<string, List<string>> Banks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FlowRuleColumnDocument
    {
        public Dictionary<string, FlowRuleColumnSet> Banks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FlowRuleColumnSet
    {
        public List<string> Reference { get; set; } = [];

        public List<string> Const { get; set; } = [];
    }
}
