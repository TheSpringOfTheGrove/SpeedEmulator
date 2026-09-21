using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SpeedEmulator.Services;

public sealed class FormulaDataSet
{
    public string FileName { get; init; } = string.Empty;

    public DateTime ImportedAtUtc { get; init; }

    public List<string> Headers { get; init; } = [];

    public List<Dictionary<string, string>> Rows { get; init; } = [];

    public bool TryGetValue(int rowIndex, string columnName, out string value)
    {
        value = string.Empty;
        if (rowIndex < 0 || rowIndex >= Rows.Count || string.IsNullOrWhiteSpace(columnName))
        {
            return false;
        }

        return Rows[rowIndex].TryGetValue(columnName.Trim(), out value!);
    }
}

public sealed record FormulaDataSetImportResult(
    FormulaDataSet DataSet,
    int EmptyRowsSkipped);

public interface IFormulaDataSetService
{
    string? PickImportFile();

    string? PickExampleExportFile();

    FormulaDataSetImportResult Import(long bankId, string path);

    void ExportBuiltInExample(string path);

    FormulaDataSet? Get(long bankId);

    bool Clear(long bankId);
}

public sealed class FormulaDataSetService : IFormulaDataSetService
{
    private const long MaximumFileBytes = 20L * 1024 * 1024;
    private const long MaximumExpandedBytes = 100L * 1024 * 1024;
    private const int MaximumRows = 100_000;
    private const int MaximumColumns = 512;
    private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly object syncRoot = new();
    private readonly Dictionary<long, FormulaDataSet?> cache = [];
    private readonly string storageDirectory;

    public FormulaDataSetService(string? storageDirectory = null)
    {
        this.storageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "formula-data");
    }

    public string? PickImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "读取随机分子 Excel 文件",
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickExampleExportFile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "下载随机分子 Excel 示例",
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            FileName = "随机分子Excel示例.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public FormulaDataSetImportResult Import(long bankId, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("随机分子 Excel 文件不存在。", path);
        }

        var fileInfo = new FileInfo(path);
        if (!string.Equals(fileInfo.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("随机分子数据只支持 .xlsx 文件。");
        }

        if (fileInfo.Length > MaximumFileBytes)
        {
            throw new InvalidDataException($"随机分子 Excel 文件不能超过 {MaximumFileBytes / 1024 / 1024} MB。");
        }

        var sheet = ReadFirstSheet(path);
        if (sheet.Count == 0)
        {
            throw new InvalidDataException("随机分子 Excel 第一张工作表为空。");
        }

        var headerRow = sheet[0];
        var lastHeaderColumn = headerRow
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => item.Key)
            .DefaultIfEmpty(0)
            .Max();
        if (lastHeaderColumn == 0)
        {
            throw new InvalidDataException("随机分子 Excel 第一行必须包含标题。");
        }

        if (lastHeaderColumn > MaximumColumns)
        {
            throw new InvalidDataException($"随机分子 Excel 最多支持 {MaximumColumns} 列。");
        }

        var headers = new List<string>(lastHeaderColumn);
        var headerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var columnIndex = 1; columnIndex <= lastHeaderColumn; columnIndex++)
        {
            var header = headerRow.GetValueOrDefault(columnIndex)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(header))
            {
                throw new InvalidDataException($"随机分子 Excel 第 {columnIndex} 列标题为空。");
            }

            if (!headerSet.Add(header))
            {
                throw new InvalidDataException($"随机分子 Excel 标题“{header}”重复。");
            }

            headers.Add(header);
        }

        var rows = new List<Dictionary<string, string>>();
        var emptyRowsSkipped = 0;
        foreach (var sourceRow in sheet.Skip(1))
        {
            var row = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
            var hasValue = false;
            for (var columnIndex = 1; columnIndex <= headers.Count; columnIndex++)
            {
                var value = sourceRow.GetValueOrDefault(columnIndex) ?? string.Empty;
                row[headers[columnIndex - 1]] = value;
                hasValue |= !string.IsNullOrWhiteSpace(value);
            }

            if (!hasValue)
            {
                emptyRowsSkipped++;
                continue;
            }

            rows.Add(row);
            if (rows.Count > MaximumRows)
            {
                throw new InvalidDataException($"随机分子 Excel 最多支持 {MaximumRows:N0} 行数据。");
            }
        }

        if (rows.Count == 0)
        {
            throw new InvalidDataException("随机分子 Excel 没有可用的数据行。");
        }

        var dataSet = new FormulaDataSet
        {
            FileName = fileInfo.Name,
            ImportedAtUtc = DateTime.UtcNow,
            Headers = headers,
            Rows = rows
        };

        lock (syncRoot)
        {
            Persist(bankId, dataSet);
            cache[bankId] = dataSet;
        }

        return new FormulaDataSetImportResult(dataSet, emptyRowsSkipped);
    }

    public void ExportBuiltInExample(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("导出路径不能为空。", nameof(path));
        }

        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("随机分子示例必须导出为 .xlsx 文件。");
        }

        var dataSet = CreateBuiltInExampleDataSet();
        WriteWorkbook(path, dataSet.Headers, dataSet.Rows);
    }

    public FormulaDataSet? Get(long bankId)
    {
        lock (syncRoot)
        {
            if (cache.TryGetValue(bankId, out var cached))
            {
                return cached;
            }

            var path = GetStoragePath(bankId);
            if (!File.Exists(path))
            {
                cache[bankId] = null;
                return null;
            }

            try
            {
                var dataSet = JsonSerializer.Deserialize<FormulaDataSet>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("随机分子数据文件内容为空。");
                NormalizeRows(dataSet);
                if (IsLegacyAutoLoadedExample(dataSet))
                {
                    File.Delete(path);
                    cache[bankId] = null;
                    return null;
                }

                cache[bankId] = dataSet;
                return dataSet;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("随机分子数据文件已损坏，请重新读取 Excel。", ex);
            }
        }
    }

    public bool Clear(long bankId)
    {
        lock (syncRoot)
        {
            cache[bankId] = null;
            var path = GetStoragePath(bankId);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    private void Persist(long bankId, FormulaDataSet dataSet)
    {
        Directory.CreateDirectory(storageDirectory);
        var path = GetStoragePath(bankId);
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(dataSet, JsonOptions));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetStoragePath(long bankId)
    {
        return Path.Combine(storageDirectory, $"{bankId.ToString(CultureInfo.InvariantCulture)}.json");
    }

    private static void NormalizeRows(FormulaDataSet dataSet)
    {
        for (var index = 0; index < dataSet.Rows.Count; index++)
        {
            dataSet.Rows[index] = new Dictionary<string, string>(
                dataSet.Rows[index],
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsLegacyAutoLoadedExample(FormulaDataSet dataSet)
    {
        return string.Equals(dataSet.FileName, "内置随机分子示例.xlsx", StringComparison.Ordinal)
            && dataSet.Rows.Count == 6
            && dataSet.Headers.SequenceEqual(["姓名", "卡号", "开户行", "手机号", "备注"], StringComparer.Ordinal);
    }

    private static Dictionary<string, string> CreateExampleRow(
        string name,
        string cardNumber,
        string bankName,
        string phoneNumber,
        string remark)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["姓名"] = name,
            ["卡号"] = cardNumber,
            ["开户行"] = bankName,
            ["手机号"] = phoneNumber,
            ["备注"] = remark
        };
    }

    private static FormulaDataSet CreateBuiltInExampleDataSet()
    {
        return new FormulaDataSet
        {
            FileName = "随机分子Excel示例.xlsx",
            ImportedAtUtc = DateTime.UtcNow,
            Headers = ["姓名", "卡号", "开户行", "手机号", "备注"],
            Rows =
            [
                CreateExampleRow("张三", "6222021001000000018", "中国工商银行北京分行", "13800000001", "示例一"),
                CreateExampleRow("李四", "6222021001000000026", "中国工商银行上海分行", "13800000002", "示例二"),
                CreateExampleRow("王五", "6222021001000000034", "中国工商银行广州分行", "13800000003", "示例三"),
                CreateExampleRow("赵六", "6222021001000000042", "中国工商银行深圳分行", "13800000004", "示例四"),
                CreateExampleRow("陈晨", "6222021001000000059", "中国工商银行杭州分行", "13800000005", "示例五"),
                CreateExampleRow("周宁", "6222021001000000067", "中国工商银行成都分行", "13800000006", "示例六")
            ]
        };
    }

    private static void WriteWorkbook(
        string path,
        IReadOnlyList<string> headers,
        IReadOnlyList<Dictionary<string, string>> rows)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                WriteXmlEntry(archive, "[Content_Types].xml", new XDocument(
                    new XElement(XName.Get("Types", "http://schemas.openxmlformats.org/package/2006/content-types"),
                        new XElement(XName.Get("Default", "http://schemas.openxmlformats.org/package/2006/content-types"),
                            new XAttribute("Extension", "rels"),
                            new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                        new XElement(XName.Get("Default", "http://schemas.openxmlformats.org/package/2006/content-types"),
                            new XAttribute("Extension", "xml"),
                            new XAttribute("ContentType", "application/xml")),
                        new XElement(XName.Get("Override", "http://schemas.openxmlformats.org/package/2006/content-types"),
                            new XAttribute("PartName", "/xl/workbook.xml"),
                            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                        new XElement(XName.Get("Override", "http://schemas.openxmlformats.org/package/2006/content-types"),
                            new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));

                WriteXmlEntry(archive, "_rels/.rels", new XDocument(
                    new XElement(XName.Get("Relationships", PackageRelationshipsNamespace),
                        new XElement(XName.Get("Relationship", PackageRelationshipsNamespace),
                            new XAttribute("Id", "rId1"),
                            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                            new XAttribute("Target", "xl/workbook.xml")))));

                XNamespace mainNs = MainNamespace;
                XNamespace relationshipNs = RelationshipsNamespace;
                WriteXmlEntry(archive, "xl/workbook.xml", new XDocument(
                    new XElement(mainNs + "workbook",
                        new XAttribute(XNamespace.Xmlns + "r", relationshipNs),
                        new XElement(mainNs + "sheets",
                            new XElement(mainNs + "sheet",
                                new XAttribute("name", "随机分子"),
                                new XAttribute("sheetId", "1"),
                                new XAttribute(relationshipNs + "id", "rId1"))))));

                WriteXmlEntry(archive, "xl/_rels/workbook.xml.rels", new XDocument(
                    new XElement(XName.Get("Relationships", PackageRelationshipsNamespace),
                        new XElement(XName.Get("Relationship", PackageRelationshipsNamespace),
                            new XAttribute("Id", "rId1"),
                            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                            new XAttribute("Target", "worksheets/sheet1.xml")))));

                var sheetData = new XElement(mainNs + "sheetData");
                sheetData.Add(CreateWorkbookRow(mainNs, 1, headers));
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var values = headers.Select(header => rows[rowIndex].GetValueOrDefault(header) ?? string.Empty);
                    sheetData.Add(CreateWorkbookRow(mainNs, rowIndex + 2, values));
                }

                WriteXmlEntry(archive, "xl/worksheets/sheet1.xml", new XDocument(
                    new XElement(mainNs + "worksheet", sheetData)));
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static XElement CreateWorkbookRow(XNamespace ns, int rowIndex, IEnumerable<string> values)
    {
        return new XElement(
            ns + "row",
            new XAttribute("r", rowIndex),
            values.Select((value, columnIndex) => new XElement(
                ns + "c",
                new XAttribute("r", $"{GetColumnName(columnIndex + 1)}{rowIndex}"),
                new XAttribute("t", "inlineStr"),
                new XElement(ns + "is", new XElement(ns + "t", value ?? string.Empty)))));
    }

    private static string GetColumnName(int columnIndex)
    {
        var result = string.Empty;
        while (columnIndex > 0)
        {
            columnIndex--;
            result = (char)('A' + (columnIndex % 26)) + result;
            columnIndex /= 26;
        }

        return result;
    }

    private static void WriteXmlEntry(ZipArchive archive, string path, XDocument document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        document.Save(stream, SaveOptions.DisableFormatting);
    }

    private static IReadOnlyList<Dictionary<int, string>> ReadFirstSheet(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var expandedBytes = archive.Entries.Sum(entry => entry.Length);
        if (expandedBytes > MaximumExpandedBytes)
        {
            throw new InvalidDataException($"随机分子 Excel 解压后不能超过 {MaximumExpandedBytes / 1024 / 1024} MB。");
        }

        var sharedStrings = ReadSharedStrings(archive);
        var sheetEntry = GetFirstWorksheetEntry(archive)
            ?? throw new InvalidDataException("随机分子 Excel 未找到工作表。");
        using var stream = sheetEntry.Open();
        var document = XDocument.Load(stream, LoadOptions.None);
        XNamespace ns = MainNamespace;

        return document
            .Descendants(ns + "row")
            .Take(MaximumRows + 2)
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
        var document = XDocument.Load(stream, LoadOptions.None);
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
        var workbook = XDocument.Load(workbookStream, LoadOptions.None);
        var relationId = (string?)workbook.Descendants(mainNs + "sheet").FirstOrDefault()?.Attribute(relNs + "id");
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
        var relationships = XDocument.Load(relationshipsStream, LoadOptions.None);
        var target = relationships
            .Descendants(packageRelNs + "Relationship")
            .FirstOrDefault(item => string.Equals((string?)item.Attribute("Id"), relationId, StringComparison.Ordinal))
            ?.Attribute("Target")
            ?.Value;
        if (string.IsNullOrWhiteSpace(target))
        {
            return archive.GetEntry("xl/worksheets/sheet1.xml");
        }

        var normalized = target.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("../", StringComparison.Ordinal))
        {
            normalized = normalized[3..];
        }

        var entryPath = normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"xl/{normalized}";
        return archive.GetEntry(entryPath);
    }

    private static string ReadCellValue(XElement cell, IReadOnlyList<string> sharedStrings, XNamespace ns)
    {
        var type = (string?)cell.Attribute("t");
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(cell.Descendants(ns + "t").Select(text => text.Value));
        }

        var value = cell.Element(ns + "v")?.Value ?? string.Empty;
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

    private static int GetColumnIndex(string reference)
    {
        var result = 0;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            result = checked((result * 26) + char.ToUpperInvariant(character) - 'A' + 1);
        }

        return result;
    }
}
