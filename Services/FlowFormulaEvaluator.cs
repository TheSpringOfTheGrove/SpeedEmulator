using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public enum FormulaDiagnosticSeverity
{
    Warning,
    Error
}

public sealed record FormulaDiagnostic(
    FormulaDiagnosticSeverity Severity,
    int RecordIndex,
    string ColumnName,
    string Field,
    string Message);

public sealed record FlowFormulaEvaluationSummary(
    int RecordCount,
    int ChangedRecordCount,
    int ChangedFieldCount,
    int ReplacedBlockCount,
    IReadOnlyList<FormulaDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(item => item.Severity == FormulaDiagnosticSeverity.Error);

    public int WarningCount => Diagnostics.Count(item => item.Severity == FormulaDiagnosticSeverity.Warning);
}

public interface IFormulaRandomSource
{
    int Next(int maxExclusive);
}

public sealed class CryptographicFormulaRandomSource : IFormulaRandomSource
{
    public int Next(int maxExclusive)
    {
        return RandomNumberGenerator.GetInt32(maxExclusive);
    }
}

public interface IFlowFormulaEvaluator
{
    FlowFormulaEvaluationSummary Evaluate(
        Bank bank,
        IReadOnlyList<FlowRecord> records,
        FormulaDataSet? dataSet);
}

public sealed class FlowFormulaEvaluator : IFlowFormulaEvaluator
{
    private const string OuterDelimiter = "\\\\";
    private const int MaximumRandomLength = 4096;
    private const int MaximumFieldLength = 16_384;
    private const string Digits = "0123456789";
    private const string LowerLetters = "abcdefghijklmnopqrstuvwxyz";
    private const string UpperLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string DigitsAndUpperLetters = Digits + UpperLetters;
    private const string DigitsAndLowerLetters = Digits + LowerLetters;
    private const string AllLettersAndDigits = Digits + LowerLetters + UpperLetters;

    private static readonly IReadOnlyDictionary<string, PropertyInfo> RecordProperties = typeof(FlowRecord)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        .ToDictionary(property => property.Name, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, PropertyInfo> StringProperties = RecordProperties
        .Where(item => item.Value.CanWrite && item.Value.PropertyType == typeof(string))
        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private readonly IFormulaRandomSource random;

    public FlowFormulaEvaluator(IFormulaRandomSource? random = null)
    {
        this.random = random ?? new CryptographicFormulaRandomSource();
    }

    public FlowFormulaEvaluationSummary Evaluate(
        Bank bank,
        IReadOnlyList<FlowRecord> records,
        FormulaDataSet? dataSet)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(records);

        var fields = ResolveFormulaFields(bank);
        var diagnostics = new List<FormulaDiagnostic>();
        var changedRecordCount = 0;
        var changedFieldCount = 0;
        var replacedBlockCount = 0;
        var rowSelectionCache = new ExcelRowSelectionCache(dataSet, random);

        for (var recordOffset = 0; recordOffset < records.Count; recordOffset++)
        {
            var record = records[recordOffset];
            record.ReplaceIndex = -1;
            var recordChanged = false;
            foreach (var field in fields)
            {
                var currentValue = GetFieldValue(record, field.Field);
                if (string.IsNullOrEmpty(currentValue) || !currentValue.Contains(OuterDelimiter, StringComparison.Ordinal))
                {
                    continue;
                }

                var result = EvaluateField(
                    bank,
                    record,
                    recordOffset,
                    field,
                    currentValue,
                    dataSet,
                    rowSelectionCache,
                    diagnostics);
                replacedBlockCount += result.ReplacedBlockCount;
                if (!string.Equals(result.Value, currentValue, StringComparison.Ordinal))
                {
                    SetFieldValue(record, field.Field, result.Value);
                    changedFieldCount++;
                    recordChanged = true;
                }
            }

            if (recordChanged)
            {
                changedRecordCount++;
            }

            // The correlated Excel row is evaluation context only. It must not
            // leak into later saves or make a new conversion reuse an old row.
            record.ReplaceIndex = -1;
        }

        return new FlowFormulaEvaluationSummary(
            records.Count,
            changedRecordCount,
            changedFieldCount,
            replacedBlockCount,
            diagnostics);
    }

    internal static void CopyEvaluatedValues(Bank bank, FlowRecord source, FlowRecord target)
    {
        foreach (var field in ResolveFormulaFields(bank))
        {
            SetFieldValue(target, field.Field, GetFieldValue(source, field.Field));
        }

        target.ReplaceIndex = source.ReplaceIndex;
    }

    private FieldEvaluationResult EvaluateField(
        Bank bank,
        FlowRecord record,
        int recordOffset,
        FormulaField field,
        string input,
        FormulaDataSet? dataSet,
        ExcelRowSelectionCache rowSelectionCache,
        List<FormulaDiagnostic> diagnostics)
    {
        var output = new StringBuilder(Math.Min(input.Length + 32, MaximumFieldLength));
        var cursor = 0;
        var replacedBlockCount = 0;
        while (cursor < input.Length)
        {
            var start = input.IndexOf(OuterDelimiter, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                output.Append(input, cursor, input.Length - cursor);
                break;
            }

            output.Append(input, cursor, start - cursor);
            var contentStart = start + OuterDelimiter.Length;
            var end = input.IndexOf(OuterDelimiter, contentStart, StringComparison.Ordinal);
            if (end < 0)
            {
                AddDiagnostic(
                    diagnostics,
                    FormulaDiagnosticSeverity.Error,
                    record,
                    recordOffset,
                    field,
                    "公式缺少结束双反斜杠。");
                output.Append(input, start, input.Length - start);
                break;
            }

            var content = input.Substring(contentStart, end - contentStart);
            output.Append(EvaluateBlock(
                bank,
                record,
                recordOffset,
                field,
                content,
                dataSet,
                rowSelectionCache,
                diagnostics));
            replacedBlockCount++;
            cursor = end + OuterDelimiter.Length;

            if (output.Length > MaximumFieldLength)
            {
                AddDiagnostic(
                    diagnostics,
                    FormulaDiagnosticSeverity.Error,
                    record,
                    recordOffset,
                    field,
                    $"公式结果超过 {MaximumFieldLength:N0} 个字符。");
                return new FieldEvaluationResult(input, replacedBlockCount);
            }
        }

        if (output.Length > MaximumFieldLength)
        {
            AddDiagnostic(
                diagnostics,
                FormulaDiagnosticSeverity.Error,
                record,
                recordOffset,
                field,
                $"公式结果超过 {MaximumFieldLength:N0} 个字符。");
            return new FieldEvaluationResult(input, replacedBlockCount);
        }

        var value = output.ToString();
        if (value.Contains(OuterDelimiter, StringComparison.Ordinal))
        {
            AddDiagnostic(
                diagnostics,
                FormulaDiagnosticSeverity.Warning,
                record,
                recordOffset,
                field,
                "转换结果仍包含公式定界符，可能来自字段复制的顺序依赖。");
        }

        return new FieldEvaluationResult(value, replacedBlockCount);
    }

    private string EvaluateBlock(
        Bank bank,
        FlowRecord record,
        int recordOffset,
        FormulaField field,
        string content,
        FormulaDataSet? dataSet,
        ExcelRowSelectionCache rowSelectionCache,
        List<FormulaDiagnostic> diagnostics)
    {
        var output = new StringBuilder(content.Length + 16);
        for (var index = 0; index < content.Length;)
        {
            var character = content[index];
            if (TryReadRandomToken(content, index, character, out var consumed, out var length, out var alphabet))
            {
                if (length > MaximumRandomLength)
                {
                    AddDiagnostic(
                        diagnostics,
                        FormulaDiagnosticSeverity.Error,
                        record,
                        recordOffset,
                        field,
                        $"随机长度 {length:N0} 超过上限 {MaximumRandomLength:N0}。");
                    output.Append(content, index, consumed);
                }
                else
                {
                    AppendRandom(output, (int)length, alphabet);
                }

                index += consumed;
                continue;
            }

            if (character == '%' && TryReadDelimitedToken(content, index, '%', out consumed, out var dateFormat))
            {
                if (record.AccountTime is { } accountTime)
                {
                    try
                    {
                        output.Append(accountTime.ToString(dateFormat, CultureInfo.InvariantCulture));
                    }
                    catch (FormatException)
                    {
                        AddDiagnostic(
                            diagnostics,
                            FormulaDiagnosticSeverity.Error,
                            record,
                            recordOffset,
                            field,
                            $"日期格式“{dateFormat}”无效。");
                        output.Append(content, index, consumed);
                    }
                }
                else
                {
                    AddDiagnostic(
                        diagnostics,
                        FormulaDiagnosticSeverity.Warning,
                        record,
                        recordOffset,
                        field,
                        "流水没有交易时间，日期公式已替换为空。");
                }

                index += consumed;
                continue;
            }

            if (character == '$' && TryReadDelimitedToken(content, index, '$', out consumed, out var independentColumn))
            {
                output.Append(ReadExcelValue(
                    dataSet,
                    independentColumn,
                    correlated: false,
                    record,
                    recordOffset,
                    field,
                    rowSelectionCache,
                    diagnostics));
                index += consumed;
                continue;
            }

            if (character == '^' && TryReadDelimitedToken(content, index, '^', out consumed, out var correlatedColumn))
            {
                output.Append(ReadExcelValue(
                    dataSet,
                    correlatedColumn,
                    correlated: true,
                    record,
                    recordOffset,
                    field,
                    rowSelectionCache,
                    diagnostics));
                index += consumed;
                continue;
            }

            if (character == '&' && TryReadDelimitedToken(content, index, '&', out consumed, out var columnName))
            {
                var referencedColumn = bank.FlowColumns.FirstOrDefault(column =>
                    string.Equals(column.Name?.Trim(), columnName.Trim(), StringComparison.Ordinal));
                if (referencedColumn is null || string.IsNullOrWhiteSpace(referencedColumn.Field))
                {
                    AddDiagnostic(
                        diagnostics,
                        FormulaDiagnosticSeverity.Warning,
                        record,
                        recordOffset,
                        field,
                        $"未找到流水列“{columnName}”，已替换为空。");
                }
                else
                {
                    output.Append(GetFieldValue(record, referencedColumn.Field));
                }

                index += consumed;
                continue;
            }

            output.Append(character);
            index++;
        }

        return output.ToString();
    }

    private string ReadExcelValue(
        FormulaDataSet? dataSet,
        string columnName,
        bool correlated,
        FlowRecord record,
        int recordOffset,
        FormulaField field,
        ExcelRowSelectionCache rowSelectionCache,
        List<FormulaDiagnostic> diagnostics)
    {
        if (dataSet is null || dataSet.Rows.Count == 0)
        {
            AddDiagnostic(
                diagnostics,
                correlated ? FormulaDiagnosticSeverity.Warning : FormulaDiagnosticSeverity.Error,
                record,
                recordOffset,
                field,
                correlated
                    ? $"尚未读取随机分子 Excel，“{columnName}”已替换为空。"
                    : $"尚未读取随机分子 Excel，无法为“{columnName}”生成非空内容。");
            return string.Empty;
        }

        var normalizedColumnName = columnName.Trim();
        if (!rowSelectionCache.ContainsColumn(normalizedColumnName))
        {
            AddDiagnostic(
                diagnostics,
                correlated ? FormulaDiagnosticSeverity.Warning : FormulaDiagnosticSeverity.Error,
                record,
                recordOffset,
                field,
                correlated
                    ? $"随机分子 Excel 不存在列“{normalizedColumnName}”，已替换为空。"
                    : $"随机分子 Excel 不存在列“{normalizedColumnName}”，无法生成非空内容。");
            return string.Empty;
        }

        int rowIndex;
        if (correlated)
        {
            rowIndex = record.ReplaceIndex;
            if (rowIndex < 0 || rowIndex >= dataSet.Rows.Count)
            {
                rowIndex = random.Next(dataSet.Rows.Count);
                record.ReplaceIndex = rowIndex;
            }
        }
        else
        {
            rowIndex = rowSelectionCache.TakeRandomRowWithValue(normalizedColumnName);
            if (rowIndex < 0)
            {
                AddDiagnostic(
                    diagnostics,
                    FormulaDiagnosticSeverity.Error,
                    record,
                    recordOffset,
                    field,
                    $"随机分子 Excel 的列“{normalizedColumnName}”没有非空数据。");
                return string.Empty;
            }
        }

        if (!dataSet.TryGetValue(rowIndex, normalizedColumnName, out var value))
        {
            AddDiagnostic(
                diagnostics,
                correlated ? FormulaDiagnosticSeverity.Warning : FormulaDiagnosticSeverity.Error,
                record,
                recordOffset,
                field,
                correlated
                    ? $"随机分子 Excel 不存在列“{normalizedColumnName}”，已替换为空。"
                    : $"随机分子 Excel 的列“{normalizedColumnName}”未能生成非空内容。");
            return string.Empty;
        }

        if (!correlated && string.IsNullOrWhiteSpace(value))
        {
            AddDiagnostic(
                diagnostics,
                FormulaDiagnosticSeverity.Error,
                record,
                recordOffset,
                field,
                $"随机分子 Excel 的列“{normalizedColumnName}”未能生成非空内容。");
            return string.Empty;
        }

        return value;
    }

    private static bool TryReadRandomToken(
        string content,
        int start,
        char opener,
        out int consumed,
        out long length,
        out string alphabet)
    {
        consumed = 0;
        length = 0;
        alphabet = string.Empty;
        var closer = opener switch
        {
            '(' => ')',
            '{' => '}',
            '<' => '>',
            '[' => ']',
            '@' => '@',
            '#' => '#',
            _ => '\0'
        };
        if (closer == '\0')
        {
            return false;
        }

        var end = content.IndexOf(closer, start + 1);
        if (end < 0)
        {
            return false;
        }

        var lengthText = content.AsSpan(start + 1, end - start - 1);
        if (lengthText.IsEmpty || !lengthText.ToString().All(char.IsDigit))
        {
            return false;
        }

        if (!long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length))
        {
            length = long.MaxValue;
        }

        alphabet = opener switch
        {
            '(' => Digits,
            '{' => LowerLetters,
            '<' => UpperLetters,
            '[' => DigitsAndUpperLetters,
            '@' => DigitsAndLowerLetters,
            '#' => AllLettersAndDigits,
            _ => string.Empty
        };
        consumed = end - start + 1;
        return true;
    }

    private static bool TryReadDelimitedToken(
        string content,
        int start,
        char delimiter,
        out int consumed,
        out string value)
    {
        consumed = 0;
        value = string.Empty;
        var end = content.IndexOf(delimiter, start + 1);
        if (end < 0)
        {
            return false;
        }

        consumed = end - start + 1;
        value = content.Substring(start + 1, end - start - 1);
        return true;
    }

    private void AppendRandom(StringBuilder output, int length, string alphabet)
    {
        for (var index = 0; index < length; index++)
        {
            output.Append(alphabet[random.Next(alphabet.Length)]);
        }
    }

    private static IReadOnlyList<FormulaField> ResolveFormulaFields(Bank bank)
    {
        var fields = new List<FormulaField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in bank.FlowColumns)
        {
            var field = column.Field?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(field) || !seen.Add(field) || !IsStringField(column, field))
            {
                continue;
            }

            fields.Add(new FormulaField(column.Name?.Trim() ?? field, field));
        }

        return fields;
    }

    private static bool IsStringField(ColumnDefinition column, string field)
    {
        if (TryGetIndexerField(field, out _))
        {
            return true;
        }

        if (RecordProperties.TryGetValue(field, out var property))
        {
            return property.CanWrite && property.PropertyType == typeof(string);
        }

        return string.Equals(column.Type, "string", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Type, "text", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFieldValue(FlowRecord record, string field)
    {
        if (TryGetIndexerField(field, out var indexerField))
        {
            return record[indexerField];
        }

        if (StringProperties.TryGetValue(field, out var property))
        {
            return (string?)property.GetValue(record) ?? string.Empty;
        }

        return record[field];
    }

    private static void SetFieldValue(FlowRecord record, string field, string value)
    {
        if (TryGetIndexerField(field, out var indexerField))
        {
            record[indexerField] = value;
            return;
        }

        if (StringProperties.TryGetValue(field, out var property))
        {
            property.SetValue(record, value);
            return;
        }

        record[field] = value;
    }

    private static bool TryGetIndexerField(string field, out string indexerField)
    {
        if (field.Length >= 2 && field.StartsWith('[') && field.EndsWith(']'))
        {
            indexerField = field[1..^1];
            return true;
        }

        indexerField = string.Empty;
        return false;
    }

    private static void AddDiagnostic(
        ICollection<FormulaDiagnostic> diagnostics,
        FormulaDiagnosticSeverity severity,
        FlowRecord record,
        int recordOffset,
        FormulaField field,
        string message)
    {
        diagnostics.Add(new FormulaDiagnostic(
            severity,
            record.Index > 0 ? record.Index : recordOffset + 1,
            field.ColumnName,
            field.Field,
            message));
    }

    private sealed class ExcelRowSelectionCache
    {
        private readonly FormulaDataSet? dataSet;
        private readonly IFormulaRandomSource random;
        private readonly Dictionary<string, IReadOnlyList<int>> rowsByColumn =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<int>> remainingRowsByColumn =
            new(StringComparer.OrdinalIgnoreCase);

        public ExcelRowSelectionCache(FormulaDataSet? dataSet, IFormulaRandomSource random)
        {
            this.dataSet = dataSet;
            this.random = random;
        }

        public bool ContainsColumn(string columnName)
        {
            return dataSet?.Headers.Contains(columnName.Trim(), StringComparer.OrdinalIgnoreCase) == true;
        }

        public IReadOnlyList<int> GetRowsWithValue(string columnName)
        {
            var normalizedColumnName = columnName.Trim();
            if (rowsByColumn.TryGetValue(normalizedColumnName, out var cached))
            {
                return cached;
            }

            IReadOnlyList<int> rows = dataSet is null
                ? []
                : Enumerable.Range(0, dataSet.Rows.Count)
                    .Where(rowIndex =>
                        dataSet.TryGetValue(rowIndex, normalizedColumnName, out var value)
                        && !string.IsNullOrWhiteSpace(value))
                    .ToArray();
            rowsByColumn[normalizedColumnName] = rows;
            return rows;
        }

        public int TakeRandomRowWithValue(string columnName)
        {
            var normalizedColumnName = columnName.Trim();
            var candidateRows = GetRowsWithValue(normalizedColumnName);
            if (candidateRows.Count == 0)
            {
                return -1;
            }

            if (!remainingRowsByColumn.TryGetValue(normalizedColumnName, out var remainingRows)
                || remainingRows.Count == 0)
            {
                remainingRows = new List<int>(candidateRows);
                remainingRowsByColumn[normalizedColumnName] = remainingRows;
            }

            var selectedOffset = random.Next(remainingRows.Count);
            var selectedRow = remainingRows[selectedOffset];
            var lastOffset = remainingRows.Count - 1;
            remainingRows[selectedOffset] = remainingRows[lastOffset];
            remainingRows.RemoveAt(lastOffset);
            return selectedRow;
        }

    }

    private readonly record struct FormulaField(string ColumnName, string Field);

    private readonly record struct FieldEvaluationResult(string Value, int ReplacedBlockCount);
}
