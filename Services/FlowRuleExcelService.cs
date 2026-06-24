using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Win32;
using SpeedEmulator.Models;
using ColumnDefinition = SpeedEmulator.Models.ColumnDefinition;

namespace SpeedEmulator.Services;

public interface IFlowRuleExcelService
{
    string? PickImportFile();

    string? PickExportFile(string defaultFileName);

    IReadOnlyList<GenerateReferenceRule> ImportReferences(string path, IReadOnlyList<ColumnDefinition> columns, long bankId);

    IReadOnlyList<GenerateConstRule> ImportConstItems(string path, IReadOnlyList<ColumnDefinition> columns, long bankId);

    void ExportReferences(string path, IEnumerable<GenerateReferenceRule> rows, IReadOnlyList<ColumnDefinition> columns);

    void ExportConstItems(string path, IEnumerable<GenerateConstRule> rows, IReadOnlyList<ColumnDefinition> columns);
}

public sealed class FlowRuleExcelService : IFlowRuleExcelService
{
    private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

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

    public IReadOnlyList<GenerateReferenceRule> ImportReferences(string path, IReadOnlyList<ColumnDefinition> columns, long bankId)
    {
        return ImportRules(path, columns, () => GenerateReferenceRule.CreateDefault(bankId), bankId);
    }

    public IReadOnlyList<GenerateConstRule> ImportConstItems(string path, IReadOnlyList<ColumnDefinition> columns, long bankId)
    {
        return ImportRules(path, columns, () => GenerateConstRule.CreateDefault(bankId), bankId);
    }

    public void ExportReferences(string path, IEnumerable<GenerateReferenceRule> rows, IReadOnlyList<ColumnDefinition> columns)
    {
        ExportRules(path, rows, columns);
    }

    public void ExportConstItems(string path, IEnumerable<GenerateConstRule> rows, IReadOnlyList<ColumnDefinition> columns)
    {
        ExportRules(path, rows, columns);
    }

    private static IReadOnlyList<T> ImportRules<T>(
        string path,
        IReadOnlyList<ColumnDefinition> columns,
        Func<T> createRule,
        long bankId)
        where T : FlowRuleBase
    {
        var sheet = ReadFirstSheet(path);
        if (sheet.Count == 0)
        {
            return [];
        }

        var excelColumns = GetExcelColumns(columns).ToList();
        var headerMap = CreateHeaderMap(sheet[0], excelColumns);
        var result = new List<T>();

        foreach (var row in sheet.Skip(1))
        {
            if (!headerMap.Keys.Any(columnIndex => row.TryGetValue(columnIndex, out var value) && !string.IsNullOrWhiteSpace(value)))
            {
                continue;
            }

            var rule = createRule();
            rule.BankId = bankId;
            rule.Id = 0;

            foreach (var (columnIndex, column) in headerMap)
            {
                if (!row.TryGetValue(columnIndex, out var rawValue))
                {
                    continue;
                }

                SetRuleValue(rule, column, rawValue);
            }

            result.Add(rule);
        }

        return result;
    }

    private static void ExportRules<T>(string path, IEnumerable<T> rows, IReadOnlyList<ColumnDefinition> columns)
        where T : FlowRuleBase
    {
        var excelColumns = GetExcelColumns(columns).ToList();
        var table = new List<List<object?>>
        {
            excelColumns.Select(column => (object?)(column.Name ?? string.Empty)).ToList()
        };

        foreach (var row in rows)
        {
            table.Add(excelColumns.Select(column => GetRuleValue(row, column)).ToList());
        }

        WriteWorkbook(path, table);
    }

    private static IEnumerable<ColumnDefinition> GetExcelColumns(IEnumerable<ColumnDefinition> columns)
    {
        return columns
            .Where(column => !IsIdColumn(column) && !string.IsNullOrWhiteSpace(column.Field))
            .OrderBy(column => column.Order)
            .ThenBy(column => column.Name);
    }

    private static Dictionary<int, ColumnDefinition> CreateHeaderMap(
        IReadOnlyDictionary<int, string> headerRow,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var usedColumnIndexes = new HashSet<int>();
        var result = new Dictionary<int, ColumnDefinition>();

        foreach (var (cellColumnIndex, header) in headerRow.OrderBy(item => item.Key))
        {
            var normalizedHeader = NormalizeHeader(header);
            if (string.IsNullOrWhiteSpace(normalizedHeader) || normalizedHeader == NormalizeHeader("ID"))
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

    private static string NormalizeHeader(string value)
    {
        return string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))).Trim();
    }

    private static void SetRuleValue(FlowRuleBase rule, ColumnDefinition column, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(column.Field))
        {
            return;
        }

        var field = column.Field;
        var value = rawValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (TryGetIndexerField(field, out var indexerField))
        {
            rule[indexerField] = value;
            return;
        }

        var property = GetRuleProperty(rule.GetType(), field);
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

        if (converted is not null)
        {
            property.SetValue(rule, converted);
        }
    }

    private static object? GetRuleValue(FlowRuleBase rule, ColumnDefinition column)
    {
        if (string.IsNullOrWhiteSpace(column.Field))
        {
            return null;
        }

        if (TryGetIndexerField(column.Field, out var indexerField))
        {
            return rule[indexerField];
        }

        var property = GetRuleProperty(rule.GetType(), column.Field);
        var value = property?.GetValue(rule);
        return value;
    }

    private static PropertyInfo? GetRuleProperty(Type ruleType, string field)
    {
        return ruleType.GetProperty(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
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

    private static bool IsIdColumn(ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Field, nameof(FlowRuleBase.Id), StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<Dictionary<int, string>> ReadFirstSheet(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var sharedStrings = ReadSharedStrings(archive);
        var sheetEntry = GetFirstWorksheetEntry(archive)
            ?? throw new InvalidDataException("未找到Excel工作表");

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

    private static void WriteWorkbook(string path, IReadOnlyList<IReadOnlyList<object?>> rows)
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

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", CreateContentTypesXml());
        WriteEntry(archive, "_rels/.rels", CreateRootRelationshipsXml());
        WriteEntry(archive, "xl/workbook.xml", CreateWorkbookXml());
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", CreateWorkbookRelationshipsXml());
        WriteEntry(archive, "xl/worksheets/sheet1.xml", CreateWorksheetXml(rows));
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
                new XElement(ns + "sheets",
                    new XElement(ns + "sheet",
                        new XAttribute("name", "Sheet1"),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(relNs + "id", "rId1")))));
    }

    private static XDocument CreateWorkbookRelationshipsXml()
    {
        XNamespace ns = PackageRelationshipsNamespace;
        return new XDocument(
            new XElement(ns + "Relationships",
                new XElement(ns + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet1.xml"))));
    }

    private static XDocument CreateWorksheetXml(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        XNamespace ns = MainNamespace;
        var maxColumnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var dimension = maxColumnCount == 0 || rows.Count == 0
            ? "A1"
            : $"A1:{GetColumnName(maxColumnCount)}{rows.Count}";

        var sheetRows = rows.Select((row, rowIndex) =>
            new XElement(ns + "row",
                new XAttribute("r", rowIndex + 1),
                row.Select((value, columnIndex) => CreateCell(ns, rowIndex + 1, columnIndex + 1, value))));

        return new XDocument(
            new XElement(ns + "worksheet",
                new XElement(ns + "dimension", new XAttribute("ref", dimension)),
                new XElement(ns + "sheetData", sheetRows)));
    }

    private static XElement CreateCell(XNamespace ns, int rowIndex, int columnIndex, object? value)
    {
        var reference = $"{GetColumnName(columnIndex)}{rowIndex}";
        if (value is null)
        {
            return new XElement(ns + "c", new XAttribute("r", reference));
        }

        if (value is bool boolean)
        {
            return new XElement(ns + "c",
                new XAttribute("r", reference),
                new XAttribute("t", "b"),
                new XElement(ns + "v", boolean ? "1" : "0"));
        }

        if (value is int or long or double or float or decimal)
        {
            return new XElement(ns + "c",
                new XAttribute("r", reference),
                new XElement(ns + "v", Convert.ToString(value, CultureInfo.InvariantCulture)));
        }

        return new XElement(ns + "c",
            new XAttribute("r", reference),
            new XAttribute("t", "inlineStr"),
            new XElement(ns + "is",
                new XElement(ns + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)));
    }
}
