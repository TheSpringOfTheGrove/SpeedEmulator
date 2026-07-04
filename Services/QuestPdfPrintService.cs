using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed class QuestPdfPrintService : IPrintPdfService
{
    public Task<string> GeneratePreviewAsync(PrintRenderContext context)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "print-preview");
        Directory.CreateDirectory(directory);

        var fileName = $"{SanitizeFileName(context.Bank.Name)}-{SanitizeFileName(context.BankUser.AccountName)}-{DateTime.Now:yyyyMMddHHmmss}.pdf";
        var path = Path.Combine(directory, fileName);
        ExportCore(context, path);
        return Task.FromResult(path);
    }

    public Task ExportAsync(PrintRenderContext context, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ExportCore(context, path);
        return Task.CompletedTask;
    }

    public static void OpenPdf(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }

    private static void ExportCore(PrintRenderContext context, string path)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var renderContext = context;
        var maxAttempts = Math.Max(1, Math.Min(GetConfiguredRowCount(context), 80));
        Exception? lastLayoutException = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var document = new BankFlowPrintDocument(renderContext);
                document.GeneratePdf(path);
                return;
            }
            catch (Exception exception) when (IsLayoutConstraintException(exception)
                && TryCreateFallbackContext(renderContext, out var fallbackContext))
            {
                lastLayoutException = exception;
                renderContext = fallbackContext;
            }
        }

        if (lastLayoutException is not null)
        {
            throw lastLayoutException;
        }
    }

    private static int GetConfiguredRowCount(PrintRenderContext context)
    {
        if (context.Template.Config.RowCount > 0)
        {
            return context.Template.Config.RowCount;
        }

        return context.Template.PageRows > 0 ? context.Template.PageRows : 1;
    }

    private static bool IsLayoutConstraintException(Exception exception)
    {
        return exception.Message.Contains("conflicting size constraints", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("space than is available", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateFallbackContext(PrintRenderContext context, out PrintRenderContext fallbackContext)
    {
        fallbackContext = context;
        var rowCount = GetConfiguredRowCount(context);
        if (rowCount <= 1)
        {
            return false;
        }

        var template = context.Template.Clone();
        var nextRowCount = rowCount - 1;
        template.PageRows = nextRowCount;
        template.Config.RowCount = nextRowCount;
        fallbackContext = new PrintRenderContext
        {
            Bank = context.Bank,
            BankUser = context.BankUser,
            Records = context.Records,
            Template = template
        };

        return true;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((string.IsNullOrWhiteSpace(value) ? "print" : value)
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "print" : sanitized;
    }

    private sealed class BankFlowPrintDocument : IDocument
    {
        private readonly PrintRenderContext context;

        public BankFlowPrintDocument(PrintRenderContext context)
        {
            this.context = context;
        }

        public DocumentMetadata GetMetadata()
        {
            return DocumentMetadata.Default;
        }

        public void Compose(IDocumentContainer container)
        {
            var config = context.Template.Config;
            var fontFamily = string.IsNullOrWhiteSpace(config.FontFamily) ? "Microsoft YaHei" : config.FontFamily;
            var recordPages = CreateRecordPages();
            for (var index = 0; index < recordPages.Count; index++)
            {
                var pageRecords = recordPages[index];
                var isFirstPage = index == 0;
                container.Page(page =>
                {
                    page.Size(IsLandscape(context.Template) ? PageSizes.A4.Landscape() : PageSizes.A4);
                    page.MarginLeft((float)config.MarginLeft);
                    page.MarginTop((float)config.MarginTop);
                    page.MarginRight((float)config.MarginRight);
                    page.MarginBottom((float)config.MarginBottom);
                    page.DefaultTextStyle(text => text
                        .FontFamily(fontFamily)
                        .FontSize((float)config.BodyFontSize)
                        .FontColor(Colors.Black));

                    page.Content().Column(column =>
                    {
                        column.Item().Element(ComposeHeader);
                        column.Item().Element(content => ComposeContent(content, pageRecords, isFirstPage));
                        column.Item().ExtendVertical();
                        column.Item().Element(ComposeFooter);
                    });
                });
            }
        }

        private void ComposeContent(IContainer container, IReadOnlyList<FlowRecord> records, bool isFirstPage)
        {
            var firstPageOffset = isFirstPage ? context.Template.Config.FirstPageOffset : 0;
            container
                .PaddingTop((float)Math.Max(firstPageOffset, 0))
                .Element(content => ComposeTable(content, records));
        }

        private void ComposeHeader(IContainer container)
        {
            container.Column(column =>
            {
                var headerFontSize = context.Template.Config.HeaderFontSize > 0
                    ? context.Template.Config.HeaderFontSize
                    : IsLandscape(context.Template) ? 13 : 9.5f;
                column.Item().AlignCenter().Text(GetStatementTitle())
                    .FontSize((float)headerFontSize)
                    .SemiBold();

                column.Item().PaddingTop(8).Row(row =>
                {
                    row.RelativeItem().Column(left =>
                    {
                        left.Item().Text(text =>
                        {
                            text.Span("户名：");
                            text.Span(EmptyAsDash(context.BankUser.AccountName));
                        });
                        left.Item().PaddingTop(3).Text(text =>
                        {
                            text.Span("币种：");
                            text.Span(EmptyAsDash(context.BankUser.Currency));
                        });
                        left.Item().PaddingTop(3).Text(text =>
                        {
                            text.Span("起止日期：");
                            text.Span($"{FormatShortDate(context.BankUser.StartDate)}-{FormatShortDate(context.BankUser.EndDate)}");
                        });
                    });

                    row.RelativeItem().AlignRight().Column(right =>
                    {
                        right.Item().AlignRight().Text(text =>
                        {
                            text.Span("账户：");
                            text.Span(EmptyAsDash(context.BankUser.AccountNo));
                        });
                        right.Item().PaddingTop(3).AlignRight().Text(text =>
                        {
                            text.Span("开户机构：");
                            text.Span(EmptyAsDash(context.BankUser.OpenBranch));
                        });
                        right.Item().PaddingTop(3).AlignRight().Text(text =>
                        {
                            text.Span("电子流水号：");
                            text.Span(EmptyAsDash(context.BankUser.UserCode));
                        });
                    });
                });

                var sealPath = GetSealImagePath();
                if (!string.IsNullOrWhiteSpace(sealPath))
                {
                    column.Item()
                        .Element(item => ComposeSeal(item, sealPath));
                }

                column.Item().PaddingTop(4).LineHorizontal(1.2f).LineColor(Colors.Black);
            });
        }

        private void ComposeSeal(IContainer container, string sealPath)
        {
            var config = context.Template.Config;
            var contentWidth = GetPageContentWidth();
            var sealLeft = Math.Min(Math.Max(config.SealLeft, 0), Math.Max(contentWidth - 1, 0));
            var sealWidth = Math.Min(Math.Max(config.SealWidth, 1), Math.Max(contentWidth - sealLeft, 1));

            container
                .PaddingTop((float)Math.Max(config.SealTop, 0))
                .Width((float)contentWidth)
                .PaddingLeft((float)sealLeft)
                .Width((float)sealWidth)
                .Image(sealPath)
                .FitWidth();
        }

        private double GetPageContentWidth()
        {
            var pageSize = IsLandscape(context.Template)
                ? PageSizes.A4.Landscape()
                : PageSizes.A4;
            var width = pageSize.Width - context.Template.Config.MarginLeft - context.Template.Config.MarginRight;
            return Math.Max(width, 1);
        }

        private void ComposeTable(IContainer container, IReadOnlyList<FlowRecord> records)
        {
            var columns = context.Template.Config.Columns.Count == 0
                ? [new PrintPdfColumn { Name = "交易日期", Field = nameof(FlowRecord.AccountTime), Type = "Date", Width = 52 }]
                : context.Template.Config.Columns;
            var renderPaperSubrows = IsPaperTemplate();
            container.PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(definition =>
                {
                    foreach (var column in columns)
                    {
                        definition.RelativeColumn((float)Math.Max(column.Width, 1));
                    }
                });

                table.Header(header =>
                {
                    foreach (var column in columns)
                    {
                        header.Cell()
                            .Element(HeaderCellStyle)
                            .Text(column.Name)
                            .FontFamily(GetColumnFontFamily(column))
                            .FontSize((float)GetColumnFontSize(column))
                            .SemiBold();
                    }
                });

                for (var rowIndex = 0; rowIndex < records.Count; rowIndex++)
                {
                    var record = records[rowIndex];
                    foreach (var column in columns)
                    {
                        table.Cell()
                            .Element(cell => BodyCellStyle(cell, column))
                            .ScaleToFit()
                            .Text(text =>
                            {
                                var value = GetRecordValue(record, column, rowIndex + 1);
                                var span = text.Span(value)
                                    .FontFamily(GetColumnFontFamily(column))
                                    .FontSize((float)GetColumnFontSize(column));
                                if (IsTradeMoneyColumn(column) && record.TradeMoney.HasValue)
                                {
                                    if (record.TradeMoney.Value > 0)
                                    {
                                        span.FontColor(Colors.Red.Medium);
                                    }
                                    else if (record.TradeMoney.Value < 0)
                                    {
                                        span.FontColor(Colors.Green.Darken2);
                                    }
                                }
                            });
                    }

                    if (renderPaperSubrows)
                    {
                        var subrowLines = GetPaperSubrowLines(record);
                        if (subrowLines.Count > 0)
                        {
                            table.Cell()
                                .ColumnSpan((uint)columns.Count)
                                .Element(PaperSubrowCellStyle)
                                .Column(column =>
                                {
                                    foreach (var line in subrowLines)
                                    {
                                        column.Item()
                                            .Text(PreparePaperSubrowLine(line))
                                            .FontFamily(GetPaperSubrowFontFamily())
                                            .FontSize((float)GetPaperSubrowFontSize());
                                    }
                                });
                        }
                    }
                }
            });
        }

        private bool IsPaperTemplate()
        {
            var templateName = context.Template.Name ?? string.Empty;
            return templateName.Contains("\u7EB8\u8D28", StringComparison.Ordinal);
        }

        private IContainer PaperSubrowCellStyle(IContainer container)
        {
            var minHeight = Math.Max(context.Template.Config.ColumnMinHeight * 0.6, 7);
            var leftPadding = Math.Max(context.Template.Config.TabSize, 70);
            return container
                .MinHeight((float)minHeight)
                .PaddingLeft((float)leftPadding)
                .PaddingRight(2)
                .PaddingVertical(0.5f)
                .AlignMiddle();
        }

        private string GetPaperSubrowFontFamily()
        {
            return string.IsNullOrWhiteSpace(context.Template.Config.FontFamily)
                ? "Microsoft YaHei"
                : context.Template.Config.FontFamily;
        }

        private double GetPaperSubrowFontSize()
        {
            return Math.Max(context.Template.Config.BodyFontSize - 0.5, 5);
        }

        private void ComposeFooter(IContainer container)
        {
            container.PaddingTop(4).AlignRight().Text(text =>
            {
                text.Span("第 ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
                text.Span(" 页");
            });
        }

        private IReadOnlyList<IReadOnlyList<FlowRecord>> CreateRecordPages()
        {
            var records = context.Template.Config.Descending
                ? context.Records.OrderByDescending(item => item.AccountTime ?? DateTime.MinValue).ToList()
                : context.Records.ToList();
            var rowCount = context.Template.Config.RowCount > 0
                ? context.Template.Config.RowCount
                : context.Template.PageRows;

            if (rowCount <= 0)
            {
                return [records];
            }

            if (!IsPaperTemplate())
            {
                var chunkedPages = records
                    .Chunk(rowCount)
                    .Select(chunk => (IReadOnlyList<FlowRecord>)chunk.ToList())
                    .ToList();
                if (chunkedPages.Count == 0)
                {
                    chunkedPages.Add([]);
                }

                return chunkedPages;
            }

            var pages = new List<IReadOnlyList<FlowRecord>>();
            var currentPage = new List<FlowRecord>();
            var usedRows = 0;
            foreach (var record in records)
            {
                var recordRows = GetPaperRecordRowUnits(record);
                if (currentPage.Count > 0 && usedRows + recordRows > rowCount)
                {
                    pages.Add(currentPage.ToList());
                    currentPage.Clear();
                    usedRows = 0;
                }

                currentPage.Add(record);
                usedRows += recordRows;
            }

            if (currentPage.Count > 0)
            {
                pages.Add(currentPage.ToList());
            }

            if (pages.Count == 0)
            {
                pages.Add([]);
            }

            return pages;
        }

        private int GetPaperRecordRowUnits(FlowRecord record)
        {
            return Math.Max(1, 1 + GetPaperSubrowLines(record).Count);
        }

        private IContainer HeaderCellStyle(IContainer container)
        {
            return container
                .BorderBottom(0.75f)
                .BorderColor(Colors.Black)
                .Height((float)Math.Max(context.Template.Config.ColumnMinHeight, 14))
                .PaddingHorizontal(2)
                .PaddingVertical(2)
                .AlignMiddle()
                .ScaleToFit();
        }

        private IContainer BodyCellStyle(IContainer container, PrintPdfColumn column)
        {
            var minHeight = Math.Max(context.Template.Config.ColumnMinHeight, column.LineHeight);
            return container
                .Height((float)Math.Max(minHeight, 1))
                .PaddingHorizontal(2)
                .PaddingVertical(1)
                .AlignMiddle();
        }

        private string? GetSealImagePath()
        {
            if (!context.BankUser.ShouldPrintSeal)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(context.BankUser.SealImagePath) && File.Exists(context.BankUser.SealImagePath))
            {
                return context.BankUser.SealImagePath;
            }

            return FindVendorSealImage();
        }

        private static bool IsTradeMoneyColumn(PrintPdfColumn column)
        {
            return string.Equals(column.Field, nameof(FlowRecord.TradeMoney), StringComparison.OrdinalIgnoreCase)
                || string.Equals(column.Name, "交易金额", StringComparison.OrdinalIgnoreCase);
        }

        private string GetRecordValue(FlowRecord record, PrintPdfColumn column, int rowIndex)
        {
            if (IsIdColumn(column))
            {
                return rowIndex.ToString(CultureInfo.InvariantCulture);
            }

            var value = ReadEntityValue(record, column.Field);
            if (value is null && string.Equals(column.Field, nameof(FlowRecord.Balance), StringComparison.OrdinalIgnoreCase))
            {
                value = record.BalanceAmount;
            }

            var formatted = ApplyTabSize(FormatValue(value, column.Type));
            if (ShouldSuppressPaperMainRowValue(record, column, formatted))
            {
                return GetPaperMainRowFallback(record, column);
            }

            return formatted;
        }

        private bool ShouldSuppressPaperMainRowValue(FlowRecord record, PrintPdfColumn column, string value)
        {
            if (!IsPaperTemplate() || string.IsNullOrWhiteSpace(value) || !IsPaperDetailColumn(column))
            {
                return false;
            }

            if (LooksLikePaperSubrowDetail(value))
            {
                return true;
            }

            return IsPaperCounterpartyColumn(column)
                && HasPaperSubrowDetail(record);
        }

        private string GetPaperMainRowFallback(FlowRecord record, PrintPdfColumn column)
        {
            if (!IsPaperRemarkColumn(column))
            {
                return string.Empty;
            }

            var fallback = NormalizePaperText(FirstNotBlank(
                record.TradeExplain,
                record.Usage,
                record.MerchantName,
                record.ProductBrief));

            return LooksLikePaperSubrowDetail(fallback) ? string.Empty : fallback;
        }

        private bool HasPaperSubrowDetail(FlowRecord record)
        {
            return GetPaperSubrowParts(record).Count > 0;
        }

        private IReadOnlyList<string> GetPaperSubrowLines(FlowRecord record)
        {
            var parts = GetPaperSubrowParts(record);
            if (parts.Count == 0)
            {
                return Array.Empty<string>();
            }

            var maxLineLength = GetPaperSubrowMaxLineLength();
            var lines = new List<string>();
            var current = string.Empty;

            foreach (var part in parts)
            {
                foreach (var segment in SplitPaperSubrowPart(part, maxLineLength))
                {
                    if (string.IsNullOrWhiteSpace(current))
                    {
                        current = segment;
                        continue;
                    }

                    if (current.Length + 1 + segment.Length <= maxLineLength)
                    {
                        current = $"{current} {segment}";
                        continue;
                    }

                    lines.Add(current);
                    current = segment;
                }
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                lines.Add(current);
            }

            return lines;
        }

        private IReadOnlyList<string> GetPaperSubrowParts(FlowRecord record)
        {
            var detail = FindPaperSubrowDetail(record);
            var parts = new List<string>();

            AddPaperSubrowPart(parts, record.OppositeAccount, detail);
            AddPaperSubrowPart(parts, record.OppositeUsername, detail);
            AddPaperSubrowPart(parts, detail, null);

            return parts;
        }

        private static IReadOnlyList<string> SplitPaperSubrowPart(string value, int maxLineLength)
        {
            var normalized = NormalizePaperText(value);
            if (normalized.Length == 0)
            {
                return Array.Empty<string>();
            }

            if (normalized.Length <= maxLineLength)
            {
                return [normalized];
            }

            var result = new List<string>();
            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                if (token.Length <= maxLineLength)
                {
                    result.Add(token);
                    continue;
                }

                for (var index = 0; index < token.Length; index += maxLineLength)
                {
                    result.Add(token.Substring(index, Math.Min(maxLineLength, token.Length - index)));
                }
            }

            return result;
        }

        private int GetPaperSubrowMaxLineLength()
        {
            var fontSize = Math.Max(GetPaperSubrowFontSize(), 1);
            var contentWidth = GetPageContentWidth();
            var availableWidth = Math.Max(contentWidth - Math.Max(context.Template.Config.TabSize, 70) - 4, 120);
            var estimatedCharacters = (int)Math.Floor(availableWidth / (fontSize * 0.72));
            return Math.Clamp(estimatedCharacters, 28, 64);
        }

        private string PreparePaperSubrowLine(string value)
        {
            var normalized = NormalizePaperText(value);
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            var segmentLength = Math.Max(8, GetPaperSubrowMaxLineLength() / 2);
            return InsertSoftBreaks(normalized, segmentLength);
        }

        private string FindPaperSubrowDetail(FlowRecord record)
        {
            var candidates = new List<string?>
            {
                record.Remark,
                record.TradeExplain,
                record.Usage,
                record.MerchantName,
                record.ProductBrief,
                record.OppositeBank
            };

            candidates.AddRange(record.ExtraFields.Values);
            return NormalizePaperText(candidates.FirstOrDefault(LooksLikePaperSubrowDetail));
        }

        private static void AddPaperSubrowPart(List<string> parts, string? value, string? textAlreadyContainingValue)
        {
            var normalized = NormalizePaperText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(textAlreadyContainingValue)
                && NormalizePaperText(textAlreadyContainingValue).Contains(normalized, StringComparison.Ordinal))
            {
                return;
            }

            if (parts.Any(item => item.Contains(normalized, StringComparison.Ordinal)
                || normalized.Contains(item, StringComparison.Ordinal)))
            {
                return;
            }

            parts.Add(normalized);
        }

        private static bool IsPaperDetailColumn(PrintPdfColumn column)
        {
            return IsPaperRemarkColumn(column) || IsPaperCounterpartyColumn(column);
        }

        private static bool IsPaperRemarkColumn(PrintPdfColumn column)
        {
            var name = column.Name ?? string.Empty;
            var field = column.Field ?? string.Empty;
            return string.Equals(field, nameof(FlowRecord.Remark), StringComparison.OrdinalIgnoreCase)
                || string.Equals(field, nameof(FlowRecord.TradeExplain), StringComparison.OrdinalIgnoreCase)
                || string.Equals(field, nameof(FlowRecord.Usage), StringComparison.OrdinalIgnoreCase)
                || ContainsAny(name, "\u5907\u6CE8", "\u9644\u8A00", "\u6458\u8981", "\u7528\u9014")
                || ContainsAny(field, "Remark", "Postscript", "Detail", "Explain", "Usage", "Append", "Attached");
        }

        private static bool IsPaperCounterpartyColumn(PrintPdfColumn column)
        {
            var name = column.Name ?? string.Empty;
            var field = column.Field ?? string.Empty;
            return string.Equals(field, nameof(FlowRecord.OppositeAccount), StringComparison.OrdinalIgnoreCase)
                || string.Equals(field, nameof(FlowRecord.OppositeUsername), StringComparison.OrdinalIgnoreCase)
                || string.Equals(field, nameof(FlowRecord.OppositeBank), StringComparison.OrdinalIgnoreCase)
                || ContainsAny(name, "\u5BF9\u65B9", "\u6237\u540D", "\u9644\u8A00")
                || ContainsAny(field, "Opposite", "Counterparty", "AccountNameAnd", "CounterpartyAccountAnd");
        }

        private static bool LooksLikePaperSubrowDetail(string? value)
        {
            var normalized = NormalizePaperText(value);
            if (normalized.Length < 18)
            {
                return false;
            }

            return normalized.Contains("NG20", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("UA20", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("NA20", StringComparison.OrdinalIgnoreCase)
                || (HasLongContinuousRun(normalized, 16) && ContainsCjk(normalized))
                || (normalized.Count(char.IsDigit) >= 16 && ContainsCjk(normalized));
        }

        private static bool HasLongContinuousRun(string value, int minimumLength)
        {
            var count = 0;
            foreach (var character in value)
            {
                if (char.IsWhiteSpace(character) || char.IsPunctuation(character))
                {
                    count = 0;
                    continue;
                }

                count++;
                if (count >= minimumLength)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsCjk(string value)
        {
            return value.Any(character => character >= '\u4E00' && character <= '\u9FFF');
        }

        private static bool ContainsAny(string value, params string[] candidates)
        {
            return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        }

        private static string FirstNotBlank(params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static string NormalizePaperText(string? value)
        {
            return string.Join(
                " ",
                (value ?? string.Empty)
                    .Replace("\u200B", string.Empty, StringComparison.Ordinal)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string InsertSoftBreaks(string value, int segmentLength)
        {
            if (value.Length <= segmentLength)
            {
                return value;
            }

            var result = new List<string>();
            for (var index = 0; index < value.Length; index += segmentLength)
            {
                result.Add(value.Substring(index, Math.Min(segmentLength, value.Length - index)));
            }

            return string.Join("\u200B", result);
        }

        private string ApplyTabSize(string value)
        {
            if (context.Template.Config.TabSize <= 0 || !value.Contains('\t'))
            {
                return value;
            }

            var spaceCount = Math.Max(1, (int)Math.Round(context.Template.Config.TabSize));
            return value.Replace("\t", new string(' ', spaceCount), StringComparison.Ordinal);
        }

        private static object? ReadEntityValue(FlowRecord record, string field)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                return null;
            }

            if (field.StartsWith('[') && field.EndsWith(']') && field.Length > 2)
            {
                return record[field[1..^1]];
            }

            var property = typeof(FlowRecord).GetProperty(
                field,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            return property is null ? record[field] : property.GetValue(record);
        }

        private static string FormatValue(object? value, string type)
        {
            return value switch
            {
                null => string.Empty,
                DateTime dateTime when string.Equals(type, "Date", StringComparison.OrdinalIgnoreCase) => dateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                DateTime dateTime when string.Equals(type, "Time", StringComparison.OrdinalIgnoreCase) => dateTime.ToString("HHmmss", CultureInfo.InvariantCulture),
                DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset when string.Equals(type, "Date", StringComparison.OrdinalIgnoreCase) => dateTimeOffset.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset when string.Equals(type, "Time", StringComparison.OrdinalIgnoreCase) => dateTimeOffset.ToString("HHmmss", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                double number when IsMoneyType(type) => number.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture),
                decimal number when IsMoneyType(type) => number.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture),
                float number when IsMoneyType(type) => number.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture),
                double number => number.ToString("0.##", CultureInfo.InvariantCulture),
                decimal number => number.ToString("0.##", CultureInfo.InvariantCulture),
                float number => number.ToString("0.##", CultureInfo.InvariantCulture),
                bool boolean => boolean ? "TRUE" : "FALSE",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            };
        }

        private static string FormatShortDate(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : "-";
        }

        private static bool IsMoneyType(string type)
        {
            return string.Equals(type, "Money", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIdColumn(PrintPdfColumn column)
        {
            return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase)
                || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLandscape(PrintTemplate template)
        {
            return template.PageSize.Contains("Landscape", StringComparison.OrdinalIgnoreCase)
                || template.PageSize.Contains("横", StringComparison.OrdinalIgnoreCase);
        }

        private string GetStatementTitle()
        {
            return context.Bank.Name switch
            {
                "农行" => "中国农业银行账户活期交易明细清单",
                "工行" => "中国工商银行账户交易明细清单",
                "中行" => "中国银行账户交易明细清单",
                "建行" => "中国建设银行账户交易明细清单",
                "交行" => "交通银行账户交易明细清单",
                "招行" => "招商银行账户交易明细清单",
                "邮政" => "中国邮政储蓄银行账户交易明细清单",
                "中信" => "中信银行账户交易明细清单",
                "民生" => "中国民生银行账户交易明细清单",
                "光大" => "中国光大银行账户交易明细清单",
                "广发" => "广发银行账户交易明细清单",
                "浦发" => "上海浦东发展银行账户交易明细清单",
                "平安" => "平安银行账户交易明细清单",
                "兴业" => "兴业银行账户交易明细清单",
                "华夏" => "华夏银行账户交易明细清单",
                "支付宝" => "支付宝账户交易明细清单",
                "微信" => "微信支付账户交易明细清单",
                _ => $"{context.Bank.Name}账户交易明细清单"
            };
        }

        private string? FindVendorSealImage()
        {
            var vendorDir = ZhenchengRuntimeLocator.Resolve();
            if (vendorDir is null)
            {
                return null;
            }

            var bankAssetDirectory = Path.Combine(vendorDir, "static", "bank");
            if (!Directory.Exists(bankAssetDirectory))
            {
                return null;
            }

            var candidates = context.Bank.Name switch
            {
                "农行" => ["农行电子版.png", "农行纸质版.png"],
                "工行" => ["工商个人电子版公章.png", "工行个人纸质版.png"],
                "中行" => ["中行印章.png", "boc_zhang.png"],
                "建行" => ["建行电子版公章.png", "建行纸质版公章.png"],
                "交行" => ["交行个人电子版.png", "交通银行纸质版公章.bmp"],
                "招行" => ["招行个人电子版公章.png", "招行纸质版.bmp"],
                "邮政" => ["邮政电子章.png", "邮政个人电子版.png"],
                "民生" => ["民生个人电子版.bmp", "民生电子版.png"],
                "光大" => ["光大电子公章.bmp", "光大个人纸质版.png"],
                "广发" => ["广发电子版公章.png", "广发纸质版.png"],
                "浦发" => ["浦发个人公章.png", "浦发电子版.bmp"],
                "平安" => ["平安个人电子章.png", "平安电子章.png"],
                "兴业" => ["兴业个人.png", "兴业-logo.png"],
                "中信" => ["中信电子章.png", "中信银行.png"],
                "微信" => ["微信.png"],
                "支付宝" => ["alipay.png"],
                _ => Array.Empty<string>()
            };

            foreach (var candidate in candidates)
            {
                var path = Path.Combine(bankAssetDirectory, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static double GetColumnFontSize(PrintPdfColumn column)
        {
            return column.FontSize > 0 ? column.FontSize : 5.2;
        }

        private string GetColumnFontFamily(PrintPdfColumn column)
        {
            var fontFamily = string.IsNullOrWhiteSpace(column.FontFamily)
                ? context.Template.Config.FontFamily
                : column.FontFamily;
            return string.IsNullOrWhiteSpace(fontFamily) ? "Microsoft YaHei" : fontFamily;
        }

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }
    }
}
