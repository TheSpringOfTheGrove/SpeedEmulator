using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using SpeedEmulator.Models;
using ColumnDefinition = SpeedEmulator.Models.ColumnDefinition;

namespace SpeedEmulator.Services;

public interface ITableExcelService
{
    string? PickImportFile();

    string? PickExportFile(string defaultFileName);

    IReadOnlyList<BankUser> ImportBankUsers(string path, Bank bank);

    void ExportBankUsers(string path, IEnumerable<BankUser> rows, Bank bank);

    IReadOnlyList<FlowRecord> ImportFlowRecords(string path, Bank bank, BankUser bankUser);

    void ExportFlowRecords(string path, IEnumerable<FlowRecord> rows, Bank bank, BankUser bankUser);
}

public sealed class TableExcelService : ITableExcelService
{
    private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const int HeaderCellStyleIndex = 1;
    private const int TextCellStyleIndex = 2;
    private const int MoneyCellStyleIndex = 3;
    private const string ExportDateTimeFormat = "yyyy-MM-dd HH:mm:ss";
    private static readonly string[] DateColumnMarkers = ["\u65e5\u671f", "\u65f6\u95f4", "date", "time"];

    private enum FlowMoneyDirection
    {
        Unknown,
        Income,
        Expense
    }

    public string? PickImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入EXCEL",
            Filter = "Excel 文件 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickExportFile(string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出EXCEL",
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            FileName = defaultFileName,
            AddExtension = true,
            DefaultExt = ".xlsx",
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public IReadOnlyList<BankUser> ImportBankUsers(string path, Bank bank)
    {
        var sheet = ReadFirstSheet(path);
        if (sheet.Count == 0)
        {
            return [];
        }

        var columns = GetBankUserColumns(bank, includeOnlyVisible: false).ToList();
        var headerMap = CreateHeaderMap(sheet[0], columns, ignoreIdColumn: true);
        var result = new List<BankUser>();

        foreach (var row in sheet.Skip(1))
        {
            if (!ContainsRowData(row, headerMap.Keys))
            {
                continue;
            }

            var user = BankUser.CreateDraft(bank);
            user.Id = 0;
            user.BackendId = 0;
            user.BankId = bank.Id;
            user.BankName = bank.Name;

            foreach (var (columnIndex, column) in headerMap)
            {
                if (row.TryGetValue(columnIndex, out var rawValue))
                {
                    SetEntityValue(user, column, rawValue);
                }
            }

            result.Add(user);
        }

        return result;
    }

    public void ExportBankUsers(string path, IEnumerable<BankUser> rows, Bank bank)
    {
        var columns = GetBankUserColumns(bank, includeOnlyVisible: false).ToList();
        var table = new List<List<object?>>
        {
            columns.Select(column => (object?)(column.Name ?? string.Empty)).ToList()
        };

        var rowIndex = 1;
        foreach (var row in rows)
        {
            table.Add(columns.Select(column => GetEntityValue(row, column, rowIndex)).ToList());
            rowIndex++;
        }

        WriteWorkbook(path, table, 0);
    }

    public IReadOnlyList<FlowRecord> ImportFlowRecords(string path, Bank bank, BankUser bankUser)
    {
        var sheet = ReadFirstSheet(path);
        if (sheet.Count == 0)
        {
            return [];
        }

        var columns = GetFlowExportColumns(bank).ToList();
        var flowHeaderRowIndex = FindHeaderRow(sheet, columns);
        if (flowHeaderRowIndex < 0)
        {
            throw new InvalidDataException("未找到流水明细表头。");
        }

        if (flowHeaderRowIndex >= 2)
        {
            ApplyBankUserInfo(sheet[0], sheet[1], bank, bankUser);
        }

        var headerMap = CreateHeaderMap(sheet[flowHeaderRowIndex], columns, ignoreIdColumn: true);
        var result = new List<FlowRecord>();

        foreach (var row in sheet.Skip(flowHeaderRowIndex + 1))
        {
            if (!ContainsRowData(row, headerMap.Keys))
            {
                continue;
            }

            var record = new FlowRecord
            {
                BankId = bank.Id,
                BankUserId = bankUser.Id
            };

            foreach (var (columnIndex, column) in headerMap)
            {
                if (row.TryGetValue(columnIndex, out var rawValue))
                {
                    SetEntityValue(record, column, rawValue);
                }
            }

            NormalizeImportedFlowRecord(record, bank, bankUser);
            result.Add(record);
        }

        return result;
    }

    public void ExportFlowRecords(string path, IEnumerable<FlowRecord> rows, Bank bank, BankUser bankUser)
    {
        var userColumns = GetFlowExportUserColumns(bank).ToList();
        var flowColumns = GetFlowExportColumns(bank).ToList();
        var table = new List<List<object?>>
        {
            userColumns.Select(column => (object?)(column.Name ?? string.Empty)).ToList(),
            userColumns.Select(column => GetEntityValue(bankUser, column, 1)).ToList(),
            flowColumns.Select(column => (object?)(column.Name ?? string.Empty)).ToList()
        };

        var rowIndex = 1;
        foreach (var row in rows)
        {
            table.Add(flowColumns.Select(column => GetEntityValue(row, column, rowIndex)).ToList());
            rowIndex++;
        }

        WriteWorkbook(path, table, 0, 2);
    }

    private static IEnumerable<ColumnDefinition> GetBankUserColumns(Bank bank, bool includeOnlyVisible)
    {
        return bank.Columns
            .Where(column => !IsIdColumn(column)
                && !string.IsNullOrWhiteSpace(column.Field)
                && (!includeOnlyVisible || column.Show))
            .OrderBy(column => column.Order)
            .ThenBy(column => column.Name);
    }

    private static IEnumerable<ColumnDefinition> GetFlowRecordColumns(IEnumerable<ColumnDefinition> columns)
    {
        return columns
            .Where(column => !IsIdColumn(column)
                && !string.IsNullOrWhiteSpace(column.Field))
            .OrderBy(column => column.Order)
            .ThenBy(column => column.Name);
    }

    private static IEnumerable<ColumnDefinition> GetFlowExportColumns(Bank bank)
    {
        if (!FlowRecordColumnCatalog.TryGetExportFlowColumns(bank.Name, out var columnNames))
        {
            return GetFlowRecordColumns(bank.FlowColumns);
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

    private static IEnumerable<ColumnDefinition> GetFlowExportUserColumns(Bank bank)
    {
        if (!FlowRecordColumnCatalog.TryGetExportUserColumns(bank.Name, out var columnNames))
        {
            return GetBankUserColumns(bank, includeOnlyVisible: false);
        }

        var result = new List<ColumnDefinition>();
        var bankColumnsByHeader = bank.Columns
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .GroupBy(column => NormalizeHeader(column.Name!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < columnNames.Count; index++)
        {
            var columnName = columnNames[index];
            var normalized = NormalizeHeader(columnName);
            if (bankColumnsByHeader.TryGetValue(normalized, out var bankColumn)
                && !IsIdColumn(bankColumn)
                && !string.IsNullOrWhiteSpace(bankColumn.Field))
            {
                result.Add(new ColumnDefinition
                {
                    Name = columnName,
                    Field = bankColumn.Field,
                    Type = bankColumn.Type,
                    Width = bankColumn.Width <= 0 ? 100 : bankColumn.Width,
                    Order = (index + 1) * 10,
                    Show = true
                });
                continue;
            }

            var (field, type) = ExcelColumnFieldResolver.ResolveBankUserField(columnName);
            field ??= CreateExportUserExtraFieldPath(bank.Name, columnName, index);
            result.Add(new ColumnDefinition
            {
                Name = columnName,
                Field = field,
                Type = type,
                Width = ExcelColumnFieldResolver.GetBankUserColumnWidth(columnName, type),
                Order = (index + 1) * 10,
                Show = true
            });
        }

        return result;
    }

    private static void ApplyBankUserInfo(
        IReadOnlyDictionary<int, string> headerRow,
        IReadOnlyDictionary<int, string> valueRow,
        Bank bank,
        BankUser bankUser)
    {
        var columns = BuildImportUserColumns(headerRow, bank).ToList();
        var headerMap = CreateHeaderMap(headerRow, columns, ignoreIdColumn: true);
        foreach (var (columnIndex, column) in headerMap)
        {
            if (valueRow.TryGetValue(columnIndex, out var rawValue))
            {
                SetEntityValue(bankUser, column, rawValue);
            }
        }

        bankUser.BankId = bank.Id;
        bankUser.BankName = bank.Name;
    }

    private static IEnumerable<ColumnDefinition> BuildImportUserColumns(IReadOnlyDictionary<int, string> headerRow, Bank bank)
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

    private static int FindHeaderRow(
        IReadOnlyList<Dictionary<int, string>> sheet,
        IReadOnlyList<ColumnDefinition> columns)
    {
        if (sheet.Count >= 3)
        {
            var thirdRowMap = CreateHeaderMap(sheet[2], columns, ignoreIdColumn: true);
            if (thirdRowMap.Count >= 2)
            {
                return 2;
            }
        }

        var bestIndex = -1;
        var bestScore = 0;
        for (var index = 0; index < Math.Min(sheet.Count, 8); index++)
        {
            var score = CreateHeaderMap(sheet[index], columns, ignoreIdColumn: true).Count;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestScore > 0 ? bestIndex : -1;
    }

    private static Dictionary<int, ColumnDefinition> CreateHeaderMap(
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

    private static bool ContainsRowData(IReadOnlyDictionary<int, string> row, IEnumerable<int> columnIndexes)
    {
        return columnIndexes.Any(columnIndex => row.TryGetValue(columnIndex, out var value) && !string.IsNullOrWhiteSpace(value));
    }

    private static void SetEntityValue(object entity, ColumnDefinition column, string rawValue)
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

    private static object? GetEntityValue(object entity, ColumnDefinition column, int rowIndex)
    {
        if (string.IsNullOrWhiteSpace(column.Field))
        {
            return null;
        }

        if (IsIdColumn(column) || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.Ordinal))
        {
            return rowIndex;
        }

        if (TryGetIndexerField(column.Field, out var indexerField))
        {
            return FormatCellValue(GetIndexerValue(entity, indexerField), column);
        }

        if (entity is FlowRecord record)
        {
            var derived = GetDerivedFlowValue(record, column.Field);
            if (derived is not null)
            {
                return FormatCellValue(derived, column);
            }
        }

        var property = GetEntityProperty(entity.GetType(), column.Field);
        return FormatCellValue(property?.GetValue(entity), column);
    }

    private static object? GetDerivedFlowValue(FlowRecord record, string field)
    {
        if (field == nameof(FlowRecord.TradeMoney))
        {
            return record.TradeMoney.HasValue ? Math.Abs(record.TradeMoney.Value) : null;
        }

        if (field == nameof(FlowRecord.CreditAmount))
        {
            var amount = record.CreditAmount ?? (record.TradeMoney > 0 ? record.TradeMoney : null);
            return amount.HasValue ? Math.Abs(amount.Value) : null;
        }

        if (field == nameof(FlowRecord.DebitAmount))
        {
            var amount = record.DebitAmount ?? (record.TradeMoney < 0 ? record.TradeMoney : null);
            return amount.HasValue ? Math.Abs(amount.Value) : null;
        }

        if (field == nameof(FlowRecord.IncomeAttribute) && record.TradeMoney.HasValue)
        {
            if (record.TradeMoney.Value > 0)
            {
                return "收入";
            }

            if (record.TradeMoney.Value < 0)
            {
                return "支出";
            }
        }

        if (field == nameof(FlowRecord.Balance))
        {
            return record.Balance ?? record.BalanceAmount;
        }

        return null;
    }

    private static object? FormatCellValue(object? value, ColumnDefinition column)
    {
        return value switch
        {
            null => null,
            DateTime dateTime => dateTime.ToString(ExportDateTimeFormat, CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString(ExportDateTimeFormat, CultureInfo.InvariantCulture),
            string text when IsDateLikeColumn(column) && TryParseDateTime(text, out var parsedDateTime) =>
                parsedDateTime.ToString(ExportDateTimeFormat, CultureInfo.InvariantCulture),
            bool boolean => boolean,
            decimal decimalValue => decimalValue,
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            long longValue => longValue,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static bool IsDateLikeColumn(ColumnDefinition column)
    {
        if (string.Equals(column.Type, "Date", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Type, "DateTime", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsDateMarker(column.Name) || ContainsDateMarker(column.Field);
    }

    private static bool ContainsDateMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return DateColumnMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static void NormalizeImportedFlowRecord(FlowRecord record, Bank bank, BankUser bankUser)
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

    private static FlowMoneyDirection ResolveFlowMoneyDirection(string? value)
    {
        var normalized = NormalizeHeader(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return FlowMoneyDirection.Unknown;
        }

        if (normalized == "支出")
        {
            return FlowMoneyDirection.Expense;
        }

        if (normalized == "收入")
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

    private static string GetIndexerValue(object entity, string fieldName)
    {
        return entity switch
        {
            BankUser bankUser => bankUser[fieldName],
            FlowRecord flowRecord => flowRecord[fieldName],
            _ => string.Empty
        };
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

    private static bool TryParseDouble(string value, out double number)
    {
        var normalized = value.Trim().Replace(",", string.Empty);
        return double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out number)
            || double.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out number);
    }

    private static bool TryParseDecimal(string value, out decimal number)
    {
        var normalized = value.Trim().Replace(",", string.Empty);
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out number)
            || decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out number);
    }

    private static bool TryParseDateTime(string value, out DateTime dateTime)
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
            "yyyy\u5e74MM\u6708dd\u65e5 HH:mm:ss",
            "yyyy\u5e74M\u6708d\u65e5 H:mm:ss",
            "yyyy年MM月dd日 HH:mm:ss",
            "yyyy年M月d日 H:mm:ss",
            "yyyy-MM-dd",
            "yyyy-M-d",
            "yyyy/MM/dd",
            "yyyy/M/d",
            "yyyy\u5e74MM\u6708dd\u65e5",
            "yyyy\u5e74M\u6708d\u65e5",
            "yyyy年MM月dd日",
            "yyyy年M月d日"
        };

        return DateTime.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime)
            || DateTime.TryParse(normalized, CultureInfo.CurrentCulture, DateTimeStyles.None, out dateTime);
    }

    private static bool IsIdColumn(ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Field, nameof(BankUser.Id), StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHeader(string value)
    {
        return string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))).Trim();
    }

    private static IReadOnlyList<Dictionary<int, string>> ReadFirstSheet(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var sharedStrings = ReadSharedStrings(archive);
        var sheetEntry = GetFirstWorksheetEntry(archive)
            ?? throw new InvalidDataException("未找到 Excel 工作表。");

        using var stream = sheetEntry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = MainNamespace;

        return document
            .Descendants(ns + "row")
            .Select(row => row
                .Elements(ns + "c")
                .Select(cell => new
                {
                    ColumnIndex = GetColumnIndex((string?)cell.Attribute("r") ?? string.Empty),
                    Value = ReadCellValue(cell, sharedStrings, ns)
                })
                .Where(item => item.ColumnIndex > 0)
                .GroupBy(item => item.ColumnIndex)
                .ToDictionary(group => group.Key, group => group.First().Value))
            .ToList();
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace ns = MainNamespace;

        return document
            .Descendants(ns + "si")
            .Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value)))
            .ToList();
    }

    private static ZipArchiveEntry? GetFirstWorksheetEntry(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        if (workbookEntry is null)
        {
            return archive.GetEntry("xl/worksheets/sheet1.xml");
        }

        XNamespace mainNs = MainNamespace;
        XNamespace relNs = RelationshipsNamespace;
        using var workbookStream = workbookEntry.Open();
        var workbook = XDocument.Load(workbookStream);
        var firstSheet = workbook.Descendants(mainNs + "sheet").FirstOrDefault();
        var relationId = (string?)firstSheet?.Attribute(relNs + "id");
        if (string.IsNullOrWhiteSpace(relationId))
        {
            return archive.GetEntry("xl/worksheets/sheet1.xml");
        }

        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (relationshipsEntry is null)
        {
            return archive.GetEntry("xl/worksheets/sheet1.xml");
        }

        XNamespace packageRelNs = PackageRelationshipsNamespace;
        using var relationshipsStream = relationshipsEntry.Open();
        var relationships = XDocument.Load(relationshipsStream);
        var target = relationships
            .Descendants(packageRelNs + "Relationship")
            .FirstOrDefault(item => string.Equals((string?)item.Attribute("Id"), relationId, StringComparison.Ordinal))
            ?.Attribute("Target")
            ?.Value;

        if (string.IsNullOrWhiteSpace(target))
        {
            return archive.GetEntry("xl/worksheets/sheet1.xml");
        }

        var sheetPath = NormalizePartPath(target);
        return archive.GetEntry(sheetPath) ?? archive.GetEntry("xl/worksheets/sheet1.xml");
    }

    private static string NormalizePartPath(string target)
    {
        var normalized = target.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"xl/{normalized}";
    }

    private static string ReadCellValue(XElement cell, IReadOnlyList<string> sharedStrings, XNamespace ns)
    {
        var type = (string?)cell.Attribute("t");
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(cell.Descendants(ns + "t").Select(text => text.Value));
        }

        var value = cell.Element(ns + "v")?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedStringIndex)
            && sharedStringIndex >= 0
            && sharedStringIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedStringIndex];
        }

        if (string.Equals(type, "b", StringComparison.OrdinalIgnoreCase))
        {
            return value == "1" ? "TRUE" : "FALSE";
        }

        return value;
    }

    private static int GetColumnIndex(string cellReference)
    {
        var result = 0;
        foreach (var character in cellReference.TakeWhile(char.IsLetter))
        {
            result = (result * 26) + char.ToUpperInvariant(character) - 'A' + 1;
        }

        return result;
    }

    private static string GetColumnName(int columnIndex)
    {
        var dividend = columnIndex;
        var name = string.Empty;
        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            name = Convert.ToChar('A' + modulo) + name;
            dividend = (dividend - modulo) / 26;
        }

        return name;
    }

    private static void WriteWorkbook(string path, IReadOnlyList<IReadOnlyList<object?>> rows, params int[] headerRowIndexes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var headers = NormalizeHeaderRowIndexes(rows, headerRowIndexes);
        var worksheet = CreateWorksheetXml(rows, headers, out var sharedStrings);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", CreateContentTypesXml());
        WriteEntry(archive, "_rels/.rels", CreateRootRelationshipsXml());
        WriteEntry(archive, "xl/workbook.xml", CreateWorkbookXml());
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", CreateWorkbookRelationshipsXml());
        WriteEntry(archive, "xl/styles.xml", CreateStylesXml());
        WriteEntry(archive, "xl/sharedStrings.xml", CreateSharedStringsXml(sharedStrings));
        WriteEntry(archive, "xl/worksheets/sheet1.xml", worksheet);
    }

    private static void WriteEntry(ZipArchive archive, string path, XDocument document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        document.Save(stream);
    }

    private static XDocument CreateContentTypesXml()
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        return new XDocument(
            new XElement(ns + "Types",
                new XElement(ns + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ns + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(ns + "Override",
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(ns + "Override",
                    new XAttribute("PartName", "/xl/styles.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")),
                new XElement(ns + "Override",
                    new XAttribute("PartName", "/xl/sharedStrings.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml")),
                new XElement(ns + "Override",
                    new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
    }

    private static XDocument CreateRootRelationshipsXml()
    {
        XNamespace ns = PackageRelationshipsNamespace;
        return new XDocument(
            new XElement(ns + "Relationships",
                new XElement(ns + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))));
    }

    private static XDocument CreateWorkbookXml()
    {
        XNamespace ns = MainNamespace;
        XNamespace relNs = RelationshipsNamespace;
        return new XDocument(
            new XElement(ns + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", relNs),
                new XElement(ns + "fileVersion",
                    new XAttribute("appName", "xl"),
                    new XAttribute("lastEdited", "7"),
                    new XAttribute("lowestEdited", "7")),
                new XElement(ns + "bookViews", new XElement(ns + "workbookView")),
                new XElement(ns + "sheets",
                    new XElement(ns + "sheet",
                        new XAttribute("name", "Sheet1"),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(relNs + "id", "rId1"))),
                new XElement(ns + "calcPr",
                    new XAttribute("fullCalcOnLoad", "1"),
                    new XAttribute("fullPrecision", "1"))));
    }

    private static XDocument CreateWorkbookRelationshipsXml()
    {
        XNamespace ns = PackageRelationshipsNamespace;
        return new XDocument(
            new XElement(ns + "Relationships",
                new XElement(ns + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet1.xml")),
                new XElement(ns + "Relationship",
                    new XAttribute("Id", "rId2"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                    new XAttribute("Target", "styles.xml")),
                new XElement(ns + "Relationship",
                    new XAttribute("Id", "rId3"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings"),
                    new XAttribute("Target", "sharedStrings.xml"))));
    }

    private static XDocument CreateStylesXml()
    {
        XNamespace ns = MainNamespace;
        return new XDocument(
            new XElement(ns + "styleSheet",
                new XElement(ns + "numFmts", new XAttribute("count", "0")),
                new XElement(ns + "fonts",
                    new XAttribute("count", "2"),
                    new XElement(ns + "font",
                        new XElement(ns + "sz", new XAttribute("val", "11")),
                        new XElement(ns + "name", new XAttribute("val", "Aptos Narrow"))),
                    new XElement(ns + "font",
                        new XElement(ns + "sz", new XAttribute("val", "11")),
                        new XElement(ns + "name", new XAttribute("val", "宋体")))),
                new XElement(ns + "fills",
                    new XAttribute("count", "2"),
                    new XElement(ns + "fill", new XElement(ns + "patternFill", new XAttribute("patternType", "none"))),
                    new XElement(ns + "fill", new XElement(ns + "patternFill", new XAttribute("patternType", "gray125")))),
                new XElement(ns + "borders",
                    new XAttribute("count", "1"),
                    new XElement(ns + "border",
                        new XElement(ns + "left"),
                        new XElement(ns + "right"),
                        new XElement(ns + "top"),
                        new XElement(ns + "bottom"),
                        new XElement(ns + "diagonal"))),
                new XElement(ns + "cellStyleXfs",
                    new XAttribute("count", "1"),
                    new XElement(ns + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "0"))),
                new XElement(ns + "cellXfs",
                    new XAttribute("count", "4"),
                    new XElement(ns + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "0"),
                        new XAttribute("xfId", "0")),
                    new XElement(ns + "xf",
                        new XAttribute("numFmtId", "0"),
                        new XAttribute("fontId", "1"),
                        new XAttribute("applyFont", "1")),
                    new XElement(ns + "xf",
                        new XAttribute("numFmtId", "49"),
                        new XAttribute("applyNumberFormat", "1"),
                        new XAttribute("fontId", "1"),
                        new XAttribute("applyFont", "1")),
                    new XElement(ns + "xf",
                        new XAttribute("numFmtId", "2"),
                        new XAttribute("applyNumberFormat", "1"),
                        new XAttribute("fontId", "1"),
                        new XAttribute("applyFont", "1"))),
                new XElement(ns + "cellStyles",
                    new XAttribute("count", "1"),
                    new XElement(ns + "cellStyle",
                        new XAttribute("name", "Normal"),
                        new XAttribute("xfId", "0"),
                        new XAttribute("builtinId", "0"))),
                new XElement(ns + "dxfs", new XAttribute("count", "0"))));
    }

    private static XDocument CreateSharedStringsXml(IReadOnlyList<string> sharedStrings)
    {
        XNamespace ns = MainNamespace;
        return new XDocument(
            new XElement(ns + "sst",
                new XAttribute("count", sharedStrings.Count),
                new XAttribute("uniqueCount", sharedStrings.Count),
                sharedStrings.Select(value => new XElement(ns + "si",
                    new XElement(ns + "t",
                        string.IsNullOrWhiteSpace(value) || value != value.Trim()
                            ? new XAttribute(XNamespace.Xml + "space", "preserve")
                            : null,
                        value)))));
    }

    private static XDocument CreateWorksheetXml(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        IReadOnlyList<int> headerRows,
        out IReadOnlyList<string> sharedStrings)
    {
        XNamespace ns = MainNamespace;
        var maxColumnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var dimension = maxColumnCount == 0 || rows.Count == 0
            ? "A1"
            : $"A1:{GetColumnName(maxColumnCount)}{rows.Count}";
        var sharedStringIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var sharedStringValues = new List<string>();
        var moneyColumnsByHeaderRow = headerRows.ToDictionary(
            headerRow => headerRow,
            headerRow => GetMoneyColumnIndexes(rows, headerRow),
            EqualityComparer<int>.Default);
        var columnWidths = CalculateColumnWidths(rows, headerRows, moneyColumnsByHeaderRow, maxColumnCount);

        var sheetRows = rows.Select((row, rowIndex) =>
            new XElement(ns + "row",
                new XAttribute("r", rowIndex + 1),
                Enumerable.Range(0, maxColumnCount)
                    .Select(columnIndex => columnIndex < row.Count ? row[columnIndex] : null)
                    .Select((value, columnIndex) => CreateCell(
                        ns,
                        rowIndex,
                        columnIndex,
                        value,
                        headerRows,
                        moneyColumnsByHeaderRow,
                        sharedStringIndexes,
                        sharedStringValues))));

        sharedStrings = sharedStringValues;

        return new XDocument(
            new XElement(ns + "worksheet",
                new XElement(ns + "dimension", new XAttribute("ref", dimension)),
                new XElement(ns + "sheetViews", new XElement(ns + "sheetView", new XAttribute("workbookViewId", "0"))),
                new XElement(ns + "sheetFormatPr", new XAttribute("defaultRowHeight", "15")),
                new XElement(ns + "cols",
                    columnWidths.Select((width, index) =>
                        new XElement(ns + "col",
                            new XAttribute("min", index + 1),
                            new XAttribute("max", index + 1),
                            new XAttribute("width", width.ToString("0.###############", CultureInfo.InvariantCulture)),
                            new XAttribute("customWidth", "1")))),
                new XElement(ns + "sheetData", sheetRows)));
    }

    private static XElement CreateCell(
        XNamespace ns,
        int zeroBasedRowIndex,
        int zeroBasedColumnIndex,
        object? value,
        IReadOnlyList<int> headerRows,
        IReadOnlyDictionary<int, HashSet<int>> moneyColumnsByHeaderRow,
        Dictionary<string, int> sharedStringIndexes,
        List<string> sharedStringValues)
    {
        var rowIndex = zeroBasedRowIndex + 1;
        var columnIndex = zeroBasedColumnIndex + 1;
        var reference = $"{GetColumnName(columnIndex)}{rowIndex}";
        var isHeader = headerRows.Contains(zeroBasedRowIndex);
        var isMoney = !isHeader && IsMoneyColumnForRow(zeroBasedRowIndex, zeroBasedColumnIndex, headerRows, moneyColumnsByHeaderRow);

        if (value is null)
        {
            return new XElement(ns + "c",
                new XAttribute("r", reference),
                new XAttribute("s", TextCellStyleIndex));
        }

        if (isHeader)
        {
            return CreateSharedStringCell(ns, reference, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, HeaderCellStyleIndex, sharedStringIndexes, sharedStringValues);
        }

        if (isMoney && TryConvertToDecimal(value, out var money))
        {
            return new XElement(ns + "c",
                new XAttribute("r", reference),
                new XAttribute("s", MoneyCellStyleIndex),
                new XElement(ns + "v", Math.Round(money, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture)));
        }

        if (value is bool boolean)
        {
            return new XElement(ns + "c",
                new XAttribute("r", reference),
                new XAttribute("s", TextCellStyleIndex),
                new XAttribute("t", "b"),
                new XElement(ns + "v", boolean ? "1" : "0"));
        }

        return CreateSharedStringCell(ns, reference, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, TextCellStyleIndex, sharedStringIndexes, sharedStringValues);
    }

    private static XElement CreateSharedStringCell(
        XNamespace ns,
        string reference,
        string value,
        int styleIndex,
        Dictionary<string, int> sharedStringIndexes,
        List<string> sharedStringValues)
    {
        if (!sharedStringIndexes.TryGetValue(value, out var sharedStringIndex))
        {
            sharedStringIndex = sharedStringValues.Count;
            sharedStringIndexes[value] = sharedStringIndex;
            sharedStringValues.Add(value);
        }

        return new XElement(ns + "c",
            new XAttribute("r", reference),
            new XAttribute("s", styleIndex),
            new XAttribute("t", "s"),
            new XElement(ns + "v", sharedStringIndex.ToString(CultureInfo.InvariantCulture)));
    }

    private static IReadOnlyList<int> NormalizeHeaderRowIndexes(IReadOnlyList<IReadOnlyList<object?>> rows, int[] headerRowIndexes)
    {
        var headers = headerRowIndexes
            .Where(index => index >= 0 && index < rows.Count)
            .Distinct()
            .OrderBy(index => index)
            .ToList();

        if (headers.Count == 0 && rows.Count > 0)
        {
            headers.Add(0);
        }

        return headers;
    }

    private static HashSet<int> GetMoneyColumnIndexes(IReadOnlyList<IReadOnlyList<object?>> rows, int headerRowIndex)
    {
        if (headerRowIndex < 0 || headerRowIndex >= rows.Count)
        {
            return [];
        }

        var headerRow = rows[headerRowIndex];
        return headerRow
            .Select((value, index) => new
            {
                Header = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                Index = index
            })
            .Where(item => IsMoneyHeader(item.Header))
            .Select(item => item.Index)
            .ToHashSet();
    }

    private static bool IsMoneyColumnForRow(
        int zeroBasedRowIndex,
        int zeroBasedColumnIndex,
        IReadOnlyList<int> headerRows,
        IReadOnlyDictionary<int, HashSet<int>> moneyColumnsByHeaderRow)
    {
        var headerRow = -1;
        foreach (var candidate in headerRows)
        {
            if (candidate >= zeroBasedRowIndex)
            {
                break;
            }

            headerRow = candidate;
        }

        return headerRow >= 0
            && moneyColumnsByHeaderRow.TryGetValue(headerRow, out var moneyColumns)
            && moneyColumns.Contains(zeroBasedColumnIndex);
    }

    private static bool IsMoneyHeader(string header)
    {
        var normalized = NormalizeHeader(header);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.Contains("属性", StringComparison.Ordinal)
            || normalized.Contains("类型", StringComparison.Ordinal)
            || normalized.Contains("币种", StringComparison.Ordinal)
            || string.Equals(normalized, "收支", StringComparison.Ordinal)
            || string.Equals(normalized, "收支属性", StringComparison.Ordinal)
            || string.Equals(normalized, "收入属性", StringComparison.Ordinal)
            || string.Equals(normalized, "支出属性", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("金额", StringComparison.Ordinal)
            || normalized.Contains("余额", StringComparison.Ordinal)
            || string.Equals(normalized, "收入", StringComparison.Ordinal)
            || string.Equals(normalized, "支出", StringComparison.Ordinal)
            || string.Equals(normalized, "借方", StringComparison.Ordinal)
            || string.Equals(normalized, "贷方", StringComparison.Ordinal)
            || string.Equals(normalized, "入账金额", StringComparison.Ordinal)
            || string.Equals(normalized, "出账金额", StringComparison.Ordinal);
    }

    private static IReadOnlyList<double> CalculateColumnWidths(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        IReadOnlyList<int> headerRows,
        IReadOnlyDictionary<int, HashSet<int>> moneyColumnsByHeaderRow,
        int maxColumnCount)
    {
        var widths = new List<double>();
        for (var columnIndex = 0; columnIndex < maxColumnCount; columnIndex++)
        {
            var maxWidth = 0d;
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var value = columnIndex < rows[rowIndex].Count ? rows[rowIndex][columnIndex] : null;
                var isMoney = !headerRows.Contains(rowIndex)
                    && IsMoneyColumnForRow(rowIndex, columnIndex, headerRows, moneyColumnsByHeaderRow);
                maxWidth = Math.Max(maxWidth, MeasureExcelTextWidth(GetCellDisplayText(value, isMoney)));
            }

            widths.Add(Math.Clamp(Math.Ceiling(maxWidth + 2.5d), 8d, 60d));
        }

        return widths;
    }

    private static string GetCellDisplayText(object? value, bool isMoney)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (isMoney && TryConvertToDecimal(value, out var money))
        {
            return Math.Round(money, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
        }

        if (value is bool boolean)
        {
            return boolean ? "TRUE" : "FALSE";
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static double MeasureExcelTextWidth(string value)
    {
        var width = 0d;
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                continue;
            }

            if (character <= 0x007f)
            {
                width += char.IsWhiteSpace(character) ? 0.6d : 1d;
            }
            else
            {
                width += 2d;
            }
        }

        return width;
    }

    private static bool TryConvertToDecimal(object value, out decimal number)
    {
        switch (value)
        {
            case decimal decimalValue:
                number = decimalValue;
                return true;
            case double doubleValue when !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue):
                number = Convert.ToDecimal(doubleValue);
                return true;
            case float floatValue when !float.IsNaN(floatValue) && !float.IsInfinity(floatValue):
                number = Convert.ToDecimal(floatValue);
                return true;
            case int intValue:
                number = intValue;
                return true;
            case long longValue:
                number = longValue;
                return true;
            case string stringValue:
                return TryParseDecimal(stringValue, out number);
            default:
                number = 0;
                return false;
        }
    }
}
