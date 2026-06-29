using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public static class PrintTemplateQuestPdfConversionService
{
    private const int LayoutVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static bool HasQuestPdfLayout(PrintTemplate? template)
    {
        return template is not null
            && !string.IsNullOrWhiteSpace(template.QuestPdfLayoutData)
            && template.Config.Columns.Count > 0;
    }

    public static bool EnsureConverted(Bank bank, PrintTemplate template)
    {
        if (HasQuestPdfLayout(template))
        {
            return false;
        }

        var config = CreateConfig(bank, template);
        template.Config = config;
        UpdateLayoutSnapshot(template);
        return true;
    }

    public static PrintPdfConfig CreateDefaultConfig(Bank bank, PrintTemplate template)
    {
        return CreateConfig(bank, template);
    }

    public static void UpdateLayoutSnapshot(PrintTemplate template)
    {
        template.QuestPdfLayoutData = JsonSerializer.Serialize(
            new QuestPdfLayoutSnapshot(
                LayoutVersion,
                template.Name,
                template.PageSize,
                template.PageRows,
                template.Config.Clone()),
            JsonOptions);
    }

    private static PrintPdfConfig CreateConfig(Bank bank, PrintTemplate template)
    {
        var parsed = TryParseStimulsoftTemplate(template.PdfData);
        var pageSize = parsed?.PageSize ?? template.PageSize;
        var isPortrait = IsPortrait(pageSize);
        var existing = template.Config?.Clone() ?? new PrintPdfConfig();
        var hasExistingLayoutConfig = existing.Columns.Count > 0;
        var columns = hasExistingLayoutConfig
            ? existing.Columns.Select(item => item.Clone()).ToList()
            : parsed?.Columns.Count > 0
            ? parsed.Columns
            : CreateColumnsFromBank(bank).ToList();

        if (columns.Count == 0)
        {
            columns.Add(new PrintPdfColumn
            {
                Name = "交易日期",
                Field = nameof(FlowRecord.AccountTime),
                Type = "Date",
                Width = 52,
                LineHeight = 13,
                FontSize = 5.2
            });
        }

        return new PrintPdfConfig
        {
            Name = string.IsNullOrWhiteSpace(existing.Name) ? template.Name : existing.Name,
            Desc = existing.Desc,
            RowCount = template.PageRows > 0 ? template.PageRows : existing.RowCount,
            MarginLeft = hasExistingLayoutConfig ? existing.MarginLeft : existing.MarginLeft > 0 ? existing.MarginLeft : parsed?.Margins.Left ?? (isPortrait ? 54 : 22),
            MarginTop = hasExistingLayoutConfig ? existing.MarginTop : existing.MarginTop > 0 ? existing.MarginTop : parsed?.Margins.Top ?? (isPortrait ? 54 : 22),
            MarginRight = hasExistingLayoutConfig ? existing.MarginRight : existing.MarginRight > 0 ? existing.MarginRight : parsed?.Margins.Right ?? (isPortrait ? 54 : 22),
            MarginBottom = hasExistingLayoutConfig ? existing.MarginBottom : existing.MarginBottom > 0 ? existing.MarginBottom : parsed?.Margins.Bottom ?? (isPortrait ? 40 : 22),
            FontFamily = string.IsNullOrWhiteSpace(existing.FontFamily) ? "Microsoft YaHei" : existing.FontFamily,
            TabSize = existing.TabSize,
            HeaderFontSize = hasExistingLayoutConfig ? existing.HeaderFontSize : existing.HeaderFontSize > 0 ? existing.HeaderFontSize : isPortrait ? 7 : 8,
            BodyFontSize = hasExistingLayoutConfig ? existing.BodyFontSize : existing.BodyFontSize > 0 ? existing.BodyFontSize : isPortrait ? 5.2 : 6.2,
            ColumnMinHeight = hasExistingLayoutConfig ? existing.ColumnMinHeight : existing.ColumnMinHeight > 0 ? existing.ColumnMinHeight : isPortrait ? 13 : 15,
            Descending = existing.Descending,
            FirstPageOffset = existing.FirstPageOffset,
            SealLeft = existing.SealLeft,
            SealTop = existing.SealTop,
            SealRight = hasExistingLayoutConfig ? existing.SealRight : existing.SealRight <= 0 ? 70 : existing.SealRight,
            SealBottom = existing.SealBottom,
            SealWidth = hasExistingLayoutConfig ? existing.SealWidth : existing.SealWidth > 0 ? existing.SealWidth : isPortrait ? 86 : 104,
            Columns = columns
        };
    }

    private static ParsedTemplate? TryParseStimulsoftTemplate(string pdfData)
    {
        if (string.IsNullOrWhiteSpace(pdfData) || !pdfData.TrimStart().StartsWith('<'))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(pdfData, LoadOptions.None);
            var page = document.Descendants().FirstOrDefault(item => item.Name.LocalName.StartsWith("Page", StringComparison.Ordinal));
            var pageWidth = ReadDouble(page, "PageWidth");
            var pageHeight = ReadDouble(page, "PageHeight");
            var pageSize = pageWidth > pageHeight ? "A4Landscape" : "A4Portrait";
            var margins = ParseMargins(page?.Elements().FirstOrDefault(item => item.Name.LocalName == "Margins")?.Value);
            var flowColumns = ReadBusinessObjectColumns(document, "流水")
                .Select(CreateColumnFromBusinessObject)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList();

            return new ParsedTemplate(pageSize, margins, flowColumns);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<BusinessColumn> ReadBusinessObjectColumns(XDocument document, string objectName)
    {
        var businessObject = document
            .Descendants()
            .FirstOrDefault(item =>
                string.Equals(item.Name.LocalName, objectName, StringComparison.Ordinal)
                || item.Elements().Any(child =>
                    child.Name.LocalName == "Name"
                    && string.Equals(child.Value, objectName, StringComparison.Ordinal)));

        var columns = businessObject?
            .Elements()
            .FirstOrDefault(item => item.Name.LocalName == "Columns");
        if (columns is null)
        {
            yield break;
        }

        foreach (var value in columns.Elements().Where(item => item.Name.LocalName == "value"))
        {
            var parts = value.Value.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            yield return new BusinessColumn(parts[0].Trim(), parts[1].Trim(), parts.Length > 2 ? parts[2].Trim() : string.Empty);
        }
    }

    private static PrintPdfColumn? CreateColumnFromBusinessObject(BusinessColumn column)
    {
        if (string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Name, "序号", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(column.Field, nameof(FlowRecord.AccountTime), StringComparison.OrdinalIgnoreCase)
            || column.Field.EndsWith("Time", StringComparison.OrdinalIgnoreCase)
            || column.Type.Contains("DateTime", StringComparison.OrdinalIgnoreCase))
        {
            return new PrintPdfColumn
            {
                Name = "交易日期",
                Field = nameof(FlowRecord.AccountTime),
                Type = "Date",
                Width = 52,
                LineHeight = 13,
                FontSize = 5.2
            };
        }

        var (field, resolvedType) = ExcelColumnFieldResolver.ResolveFlowRecordField(column.Name);
        field ??= column.Field;
        var type = ResolveColumnType(column, resolvedType);
        return new PrintPdfColumn
        {
            Name = NormalizePrintColumnName(column.Name),
            Field = field,
            Type = type,
            Width = GetColumnWidth(field, column.Name, type),
            LineHeight = 13,
            FontSize = 5.2
        };
    }

    private static IEnumerable<PrintPdfColumn> CreateColumnsFromBank(Bank bank)
    {
        var addedDateTime = false;
        foreach (var column in bank.FlowColumns
            .Where(item => item.Show && !string.IsNullOrWhiteSpace(item.Field) && !IsIdColumn(item))
            .OrderBy(item => item.Order))
        {
            if (string.Equals(column.Field, nameof(FlowRecord.AccountTime), StringComparison.OrdinalIgnoreCase))
            {
                if (!addedDateTime)
                {
                    addedDateTime = true;
                    yield return new PrintPdfColumn
                    {
                        Name = "交易日期",
                        Field = nameof(FlowRecord.AccountTime),
                        Type = "Date",
                        Width = 52,
                        LineHeight = 13,
                        FontSize = 5.2
                    };
                    yield return new PrintPdfColumn
                    {
                        Name = "交易时间",
                        Field = nameof(FlowRecord.AccountTime),
                        Type = "Time",
                        Width = 50,
                        LineHeight = 13,
                        FontSize = 5.2
                    };
                }

                continue;
            }

            yield return new PrintPdfColumn
            {
                Name = column.Name ?? string.Empty,
                Field = column.Field ?? string.Empty,
                Type = column.Type ?? string.Empty,
                Width = GetColumnWidth(column.Field ?? string.Empty, column.Name ?? string.Empty, column.Type ?? string.Empty),
                LineHeight = 13,
                FontSize = 5.2
            };
        }
    }

    private static string ResolveColumnType(BusinessColumn column, string resolvedType)
    {
        if (column.Type.Contains("Double", StringComparison.OrdinalIgnoreCase)
            || column.Type.Contains("Decimal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolvedType, "Money", StringComparison.OrdinalIgnoreCase))
        {
            return "Money";
        }

        return resolvedType;
    }

    private static string NormalizePrintColumnName(string name)
    {
        return name switch
        {
            "记账时间" => "交易日期",
            "账户余额" => "本次余额",
            _ => name
        };
    }

    private static double GetColumnWidth(string field, string name, string type)
    {
        if (string.Equals(type, "Money", StringComparison.OrdinalIgnoreCase))
        {
            return field == nameof(FlowRecord.Balance) || field == nameof(FlowRecord.BalanceAmount) ? 62 : 58;
        }

        return field switch
        {
            nameof(FlowRecord.AccountTime) => 52,
            nameof(FlowRecord.LogNum) => 68,
            nameof(FlowRecord.TradeChannel) => 52,
            nameof(FlowRecord.Remark) => 130,
            nameof(FlowRecord.OppositeUsername) => 88,
            nameof(FlowRecord.OppositeAccount) => 88,
            nameof(FlowRecord.OppositeBank) => 88,
            _ => Math.Clamp(ExcelColumnFieldResolver.GetFlowRecordColumnWidth(name, type), 44, 120)
        };
    }

    private static PrintMargins ParseMargins(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new PrintMargins(54, 54, 54, 40);
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return new PrintMargins(54, 54, 54, 40);
        }

        return new PrintMargins(
            ParseDouble(parts[0]) ?? 54,
            ParseDouble(parts[1]) ?? 54,
            ParseDouble(parts[2]) ?? 54,
            ParseDouble(parts[3]) ?? 40);
    }

    private static double ReadDouble(XElement? parent, string childName)
    {
        return ParseDouble(parent?.Elements().FirstOrDefault(item => item.Name.LocalName == childName)?.Value) ?? 0;
    }

    private static double? ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsPortrait(string pageSize)
    {
        return pageSize.Contains("Portrait", StringComparison.OrdinalIgnoreCase)
            || pageSize.Contains("纵", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdColumn(ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ParsedTemplate(string PageSize, PrintMargins Margins, List<PrintPdfColumn> Columns);

    private sealed record PrintMargins(double Left, double Top, double Right, double Bottom);

    private sealed record BusinessColumn(string Field, string Name, string Type);

    private sealed record QuestPdfLayoutSnapshot(int Version, string TemplateName, string PageSize, int PageRows, PrintPdfConfig Config);
}
