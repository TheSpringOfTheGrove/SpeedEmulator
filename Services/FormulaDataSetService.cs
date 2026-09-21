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
        string sequence,
        string name,
        string cardNumber,
        string bankName,
        string convenienceStore = "",
        string supermarket = "",
        string fruitStore = "",
        string breakfastStore = "",
        string gasStation = "")
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["排序"] = sequence,
            ["姓名"] = name,
            ["卡号"] = cardNumber,
            ["开户行"] = bankName,
            ["便利店"] = convenienceStore,
            ["超市"] = supermarket,
            ["水果店"] = fruitStore,
            ["早餐店"] = breakfastStore,
            ["加油站"] = gasStation
        };
    }

    private static FormulaDataSet CreateBuiltInExampleDataSet()
    {
        return new FormulaDataSet
        {
            FileName = "随机分子Excel示例.xlsx",
            ImportedAtUtc = DateTime.UtcNow,
            Headers = ["排序", "姓名", "卡号", "开户行", "便利店", "超市", "水果店", "早餐店", "加油站"],
            Rows =
            [
                CreateExampleRow("1", "宋力", "6217001540007157926", "中国建设银行", "罗森便利店", "永辉超市", "百乐园", "煎饺王子", "中国石油"),
                CreateExampleRow("2", "姜小青", "6228480619772268576", "中国农业银行", "芙蓉兴盛便利店", "沃尔玛超市", "鲜丰水果", "柳州螺蛳粉", "中国石化"),
                CreateExampleRow("3", "潘伟源", "6228480618096445779", "中国农业银行", "美宜佳便利店", "盒马生鲜超市", "绿叶水果", "桂林米粉"),
                CreateExampleRow("4", "黄绍莲", "6230522320045079876", "中国农业银行", "7-ELEVEN便利店", "合物美超市", "果多美", "香光早餐店"),
                CreateExampleRow("5", "黄晓军", "6228480030399499711", "中国农业银行", "全家便利店", "麦德龙超市", "叶氏兄弟", "严氏烧卖"),
                CreateExampleRow("6", "刘元杰", "6221884520021492156", "中国邮政储蓄银行", "快客便利店", "开市客(Costco)", "洪九果品", "狗不理包子铺"),
                CreateExampleRow("7", "宋斌", "6217755017000363320", "徽商银行", "红旗连锁便利店", "华润万家超市", "花果鲜苏洪鲜鹤原素水果店", "丽华早点"),
                CreateExampleRow("8", "张璐颖", "6217001210049401704", "中国建设银行", "365便利店", "家乐福超市", "奇果鲜生天天果园诚实果品"),
                CreateExampleRow("9", "雷中能", "6217002730013191584", "中国建设银行", "便利蜂便利店", "大润发超市"),
                CreateExampleRow("10", "张铃", "6222600110000471396", "交通银行", "喜士多便利店", "美特好超市"),
                CreateExampleRow("11", "汤小苏", "6215581110007368188", "中国工商银行", "银座CC便利店", "奥乐齐超市"),
                CreateExampleRow("12", "林辉", "6228480128081087370", "中国农业银行", "天福便利店", "山姆会员店"),
                CreateExampleRow("13", "上官杨平", "6222081202013744378", "中国工商银行", "盒马NB便利店"),
                CreateExampleRow("14", "高伟成", "6226732400013853", "中国工商银行", "奥乐齐便利店"),
                CreateExampleRow("15", "林辉", "6222084000005743102", "中国工商银行"),
                CreateExampleRow("16", "吴祝芳", "6008450388012778374", "中国农业银行"),
                CreateExampleRow("17", "王曼曼", "6086732400013853", "光大银行"),
                CreateExampleRow("18", "谢师友", "6222623210003954506", "交通银行"),
                CreateExampleRow("19", "何邦云", "6228480316092902063", "中国农业银行"),
                CreateExampleRow("20", "孔毅荣", "6228480248271214574", "中国农业银行"),
                CreateExampleRow("21", "王跃翠", "6217002220018548152", "中国建设银行"),
                CreateExampleRow("22", "朱建琴", "6228480310475340611", "中国农业银行"),
                CreateExampleRow("23", "朱秀年", "62284803183889624772", "中国农业银行"),
                CreateExampleRow("24", "范正英", "6230521980078496270", "中国农业银行"),
                CreateExampleRow("25", "陈彩凤", "6013826111004237578", "中国银行"),
                CreateExampleRow("26", "陈伟", "6228480031414122114", "中国农业银行")
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
                    new XElement(
                        mainNs + "worksheet",
                        new XElement(
                            mainNs + "cols",
                            CreateWorkbookColumn(mainNs, 1, 7),
                            CreateWorkbookColumn(mainNs, 2, 18),
                            CreateWorkbookColumn(mainNs, 3, 25),
                            CreateWorkbookColumn(mainNs, 4, 22),
                            CreateWorkbookColumn(mainNs, 5, 18),
                            CreateWorkbookColumn(mainNs, 6, 17),
                            CreateWorkbookColumn(mainNs, 7, 26),
                            CreateWorkbookColumn(mainNs, 8, 17),
                            CreateWorkbookColumn(mainNs, 9, 15)),
                        sheetData)));
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

    private static XElement CreateWorkbookColumn(XNamespace ns, int columnIndex, double width)
    {
        return new XElement(
            ns + "col",
            new XAttribute("min", columnIndex),
            new XAttribute("max", columnIndex),
            new XAttribute("width", width.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("customWidth", "1"));
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
