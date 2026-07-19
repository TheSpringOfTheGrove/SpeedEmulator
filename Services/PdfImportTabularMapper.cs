using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SpeedEmulator.Models;
using ColumnDefinition = SpeedEmulator.Models.ColumnDefinition;

namespace SpeedEmulator.Services;

internal static class PdfImportTabularMapper
{
    private enum FlowMoneyDirection
    {
        Unknown,
        Income,
        Expense
    }

    public static IReadOnlyList<ColumnDefinition> GetBankUserColumns(Bank bank)
    {
        return bank.Columns
            .Where(column => !IsIdColumn(column)
                && !string.IsNullOrWhiteSpace(column.Field))
            .OrderBy(column => column.Order)
            .ThenBy(column => column.Name)
            .ToList();
    }

    public static IReadOnlyList<ColumnDefinition> GetFlowExportColumns(Bank bank)
    {
        if (!FlowRecordColumnCatalog.TryGetExportFlowColumns(bank.Name, out var columnNames))
        {
            return bank.FlowColumns
                .Where(column => !IsIdColumn(column)
                    && !string.IsNullOrWhiteSpace(column.Field))
                .OrderBy(column => column.Order)
                .ThenBy(column => column.Name)
                .ToList();
        }

        var result = new List<ColumnDefinition>();
        var usedFixedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(FlowRecord.Index)
        };

        for (var index = 0; index < columnNames.Count; index++)
        {
            var columnName = columnNames[index];
            var (field, type) = ExcelColumnFieldResolver.ResolveFlowRecordField(bank.Name, columnName);
            if (field is null || !usedFixedFields.Add(field))
            {
                field = CreateExportFlowExtraFieldPath(bank.Name, columnName, index);
            }

            result.Add(new ColumnDefinition
            {
                Name = columnName,
                Field = field,
                Type = type,
                Width = ExcelColumnFieldResolver.GetFlowRecordColumnWidth(columnName, type),
                Order = (index + 1) * 10,
                Show = true
            });
        }

        return result;
    }

    public static IEnumerable<ColumnDefinition> BuildImportFlowColumns(
        IReadOnlyDictionary<int, string> headerRow,
        Bank bank)
    {
        var exportColumnsByHeader = GetFlowExportColumns(bank)
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .GroupBy(column => NormalizeHeader(column.Name!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var flowColumnsByHeader = bank.FlowColumns
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .GroupBy(column => NormalizeHeader(column.Name!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var usedFixedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(FlowRecord.Index)
        };

        foreach (var (columnIndex, header) in headerRow.OrderBy(item => item.Key))
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            var normalized = NormalizeHeader(header);
            var column = exportColumnsByHeader.TryGetValue(normalized, out var exportColumn)
                ? exportColumn
                : flowColumnsByHeader.TryGetValue(normalized, out var flowColumn)
                    ? flowColumn
                    : null;
            var field = column?.Field;
            var type = column?.Type ?? "Text";

            if (string.IsNullOrWhiteSpace(field))
            {
                (field, type) = ExcelColumnFieldResolver.ResolveFlowRecordField(bank.Name, header);
            }

            if (string.IsNullOrWhiteSpace(field) || !usedFixedFields.Add(field))
            {
                field = CreateExportFlowExtraFieldPath(bank.Name, header, columnIndex);
                type = ExcelColumnFieldResolver.ResolveFlowRecordField(bank.Name, header).Type;
            }

            yield return new ColumnDefinition
            {
                Name = header,
                Field = field,
                Type = type,
                Width = ExcelColumnFieldResolver.GetFlowRecordColumnWidth(header, type),
                Order = columnIndex * 10,
                Show = true
            };
        }
    }

    public static IEnumerable<ColumnDefinition> BuildImportUserColumns(
        IReadOnlyDictionary<int, string> headerRow,
        Bank bank)
    {
        var bankColumnsByHeader = bank.Columns
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .GroupBy(column => NormalizeHeader(column.Name!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (columnIndex, header) in headerRow.OrderBy(item => item.Key))
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            var normalized = NormalizeHeader(header);
            if (bankColumnsByHeader.TryGetValue(normalized, out var bankColumn)
                && !IsIdColumn(bankColumn)
                && !string.IsNullOrWhiteSpace(bankColumn.Field))
            {
                yield return bankColumn;
                continue;
            }

            var (field, type) = ExcelColumnFieldResolver.ResolveBankUserField(header);
            field ??= CreateExportUserExtraFieldPath(bank.Name, header, columnIndex);
            yield return new ColumnDefinition
            {
                Name = header,
                Field = field,
                Type = type,
                Width = ExcelColumnFieldResolver.GetBankUserColumnWidth(header, type),
                Order = columnIndex * 10,
                Show = true
            };
        }
    }

    public static int FindHeaderRow(
        IReadOnlyList<Dictionary<int, string>> rows,
        IReadOnlyList<ColumnDefinition> columns,
        int searchLimit = 20)
    {
        var bestIndex = -1;
        var bestScore = 0;
        for (var index = 0; index < Math.Min(rows.Count, searchLimit); index++)
        {
            var score = CreateHeaderMap(rows[index], columns, ignoreIdColumn: true).Count;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestScore >= 2 ? bestIndex : -1;
    }

    public static Dictionary<int, ColumnDefinition> CreateHeaderMap(
        IReadOnlyDictionary<int, string> headerRow,
        IReadOnlyList<ColumnDefinition> columns,
        bool ignoreIdColumn)
    {
        var usedColumnIndexes = new HashSet<int>();
        var result = new Dictionary<int, ColumnDefinition>();

        foreach (var (cellColumnIndex, header) in headerRow.OrderBy(item => item.Key))
        {
            var normalizedHeader = NormalizeHeader(header);
            if (string.IsNullOrWhiteSpace(normalizedHeader)
                || (ignoreIdColumn && normalizedHeader == NormalizeHeader("ID")))
            {
                continue;
            }

            var matchIndex = Enumerable.Range(0, columns.Count)
                .FirstOrDefault(index => !usedColumnIndexes.Contains(index)
                    && NormalizeHeader(columns[index].Name ?? string.Empty) == normalizedHeader, -1);

            if (matchIndex < 0)
            {
                continue;
            }

            usedColumnIndexes.Add(matchIndex);
            result[cellColumnIndex] = columns[matchIndex];
        }

        return result;
    }

    public static bool ContainsRowData(IReadOnlyDictionary<int, string> row, IEnumerable<int> columnIndexes)
    {
        return columnIndexes.Any(columnIndex => row.TryGetValue(columnIndex, out var value) && !string.IsNullOrWhiteSpace(value));
    }

    public static void SetEntityValue(object entity, ColumnDefinition column, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(column.Field))
        {
            return;
        }

        var value = rawValue?.Trim() ?? string.Empty;
        if (TryGetIndexerField(column.Field, out var indexerField))
        {
            SetIndexerValue(entity, indexerField, value);
            return;
        }

        if (IsIdColumn(column) || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.Ordinal))
        {
            return;
        }

        var property = GetEntityProperty(entity.GetType(), column.Field);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object? converted = null;

        if (targetType == typeof(string))
        {
            converted = value;
        }
        else if (targetType == typeof(bool))
        {
            converted = ParseBoolean(value);
        }
        else if (targetType == typeof(int))
        {
            if (TryParseDouble(value, out var number))
            {
                converted = Convert.ToInt32(Math.Round(number, MidpointRounding.AwayFromZero));
            }
        }
        else if (targetType == typeof(long))
        {
            if (TryParseDouble(value, out var number))
            {
                converted = Convert.ToInt64(Math.Round(number, MidpointRounding.AwayFromZero));
            }
        }
        else if (targetType == typeof(double))
        {
            if (TryParseDouble(value, out var number))
            {
                converted = number;
            }
        }
        else if (targetType == typeof(decimal))
        {
            if (TryParseDecimal(value, out var number))
            {
                converted = number;
            }
        }
        else if (targetType == typeof(DateTime))
        {
            if (TryParseDateTime(value, out var dateTime))
            {
                converted = dateTime;
            }
        }

        if (converted is not null)
        {
            property.SetValue(entity, converted);
        }
    }

    public static void NormalizeImportedFlowRecord(FlowRecord record, Bank bank, BankUser bankUser)
    {
        record.BankId = bank.Id;
        record.BankUserId = bankUser.Id;
        var direction = ResolveFlowMoneyDirection(record.IncomeAttribute);

        if (!record.TradeMoney.HasValue)
        {
            if (record.CreditAmount.HasValue)
            {
                record.TradeMoney = Math.Abs(record.CreditAmount.Value);
                direction = direction == FlowMoneyDirection.Unknown ? FlowMoneyDirection.Income : direction;
            }
            else if (record.DebitAmount.HasValue)
            {
                record.TradeMoney = 0 - Math.Abs(record.DebitAmount.Value);
                direction = direction == FlowMoneyDirection.Unknown ? FlowMoneyDirection.Expense : direction;
            }
        }

        if (record.TradeMoney.HasValue)
        {
            var amount = ApplyFlowMoneyDirection(record.TradeMoney.Value, direction);
            record.TradeMoney = amount;

            if (amount > 0 && !record.CreditAmount.HasValue)
            {
                record.CreditAmount = Math.Abs(amount);
            }
            else if (amount < 0 && !record.DebitAmount.HasValue)
            {
                record.DebitAmount = Math.Abs(amount);
            }

            if (string.IsNullOrWhiteSpace(record.IncomeAttribute))
            {
                record.IncomeAttribute = amount >= 0 ? "收入" : "支出";
            }
            else
            {
                record.IncomeAttribute = NormalizeFlowIncomeAttribute(record.IncomeAttribute);
            }

            if (string.IsNullOrWhiteSpace(record.IncomeFlag))
            {
                record.IncomeFlag = amount >= 0 ? "收入" : "支出";
            }
        }

        if (!record.Balance.HasValue && record.BalanceAmount.HasValue)
        {
            record.Balance = record.BalanceAmount;
        }
        else if (record.Balance.HasValue && !record.BalanceAmount.HasValue)
        {
            record.BalanceAmount = record.Balance;
        }
    }

    public static string NormalizeHeader(string value)
    {
        return string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))).Trim();
    }

    public static bool TryParseDateTime(string value, out DateTime dateTime)
    {
        var normalized = value.Trim();
        if (TryParseDouble(normalized, out var serialDate)
            && serialDate > 20000
            && serialDate < 80000)
        {
            try
            {
                dateTime = DateTime.FromOADate(serialDate);
                return true;
            }
            catch (ArgumentException)
            {
            }
        }

        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-M-d H:mm:ss",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/M/d H:mm:ss",
            "yyyy.MM.dd HH:mm:ss",
            "yyyy.M.d H:mm:ss",
            "yyyy年MM月dd日 HH:mm:ss",
            "yyyy年M月d日 H:mm:ss",
            "yyyyMMddHHmmss",
            "yyyyMMdd HHmmss",
            "yyyyMMdd HH:mm:ss",
            "yyyy-MM-dd",
            "yyyy-M-d",
            "yyyy/MM/dd",
            "yyyy/M/d",
            "yyyy.MM.dd",
            "yyyy.M.d",
            "yyyy年MM月dd日",
            "yyyy年M月d日",
            "yyyyMMdd",
            "MM-dd HH:mm:ss",
            "M-d H:mm:ss",
            "MM/dd HH:mm:ss",
            "M/d H:mm:ss"
        };

        return DateTime.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime)
            || DateTime.TryParse(normalized, CultureInfo.CurrentCulture, DateTimeStyles.None, out dateTime);
    }

    public static bool TryParseDouble(string value, out double number)
    {
        var normalized = NormalizeNumberText(value);
        return double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out number)
            || double.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out number);
    }

    private static bool TryParseDecimal(string value, out decimal number)
    {
        var normalized = NormalizeNumberText(value);
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out number)
            || decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out number);
    }

    private static string NormalizeNumberText(string value)
    {
        var normalized = value.Trim()
            .Replace(",", string.Empty)
            .Replace("，", string.Empty)
            .Replace("￥", string.Empty)
            .Replace("¥", string.Empty);

        if (normalized.StartsWith('(') && normalized.EndsWith(')'))
        {
            normalized = $"-{normalized[1..^1]}";
        }

        return normalized;
    }

    private static bool? ParseBoolean(string value)
    {
        var normalized = value.Trim();
        if (string.Equals(normalized, "TRUE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "是", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "YES", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalized, "FALSE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "否", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "N", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "NO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static FlowMoneyDirection ResolveFlowMoneyDirection(string? value)
    {
        var normalized = NormalizeHeader(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return FlowMoneyDirection.Unknown;
        }

        if (normalized is "支出" or "出" or "借" or "借方" or "D")
        {
            return FlowMoneyDirection.Expense;
        }

        if (normalized is "收入" or "入" or "贷" or "贷方" or "C")
        {
            return FlowMoneyDirection.Income;
        }

        return FlowMoneyDirection.Unknown;
    }

    private static double ApplyFlowMoneyDirection(double amount, FlowMoneyDirection direction)
    {
        return direction switch
        {
            FlowMoneyDirection.Income => Math.Abs(amount),
            FlowMoneyDirection.Expense => 0 - Math.Abs(amount),
            _ => amount
        };
    }

    private static string NormalizeFlowIncomeAttribute(string value)
    {
        return ResolveFlowMoneyDirection(value) switch
        {
            FlowMoneyDirection.Income => "收入",
            FlowMoneyDirection.Expense => "支出",
            _ => value
        };
    }

    private static PropertyInfo? GetEntityProperty(Type entityType, string field)
    {
        return entityType.GetProperty(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
    }

    private static void SetIndexerValue(object entity, string fieldName, string value)
    {
        switch (entity)
        {
            case BankUser bankUser:
                bankUser[fieldName] = value;
                break;
            case FlowRecord flowRecord:
                flowRecord[fieldName] = value;
                break;
        }
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

    private static string CreateExportUserExtraFieldPath(string bankName, string columnName, int columnIndex)
    {
        var raw = $"{bankName}|ExportUser|{columnIndex}|{columnName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"[ExportUserField_{Convert.ToHexString(hash)[..12]}]";
    }

    private static string CreateExportFlowExtraFieldPath(string bankName, string columnName, int columnIndex)
    {
        var raw = $"{bankName}|ExportFlow|{columnIndex}|{columnName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"[ExportFlowField_{Convert.ToHexString(hash)[..12]}]";
    }

    private static bool IsIdColumn(ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Field, nameof(BankUser.Id), StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.OrdinalIgnoreCase);
    }
}
