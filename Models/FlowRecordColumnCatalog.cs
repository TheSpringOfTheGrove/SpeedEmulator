using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Models;

public static class FlowRecordColumnCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<Dictionary<string, IReadOnlyList<string>>> FlowColumns = new(
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

    private sealed class ColumnDocument
    {
        public Dictionary<string, List<string>> Banks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
