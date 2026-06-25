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
        var document = new BankFlowPrintDocument(context);
        document.GeneratePdf(path);
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
            container.Page(page =>
            {
                page.Size(IsLandscape(context.Template) ? PageSizes.A4.Landscape() : PageSizes.A4);
                page.MarginLeft((float)config.MarginLeft);
                page.MarginTop((float)config.MarginTop);
                page.MarginRight((float)config.MarginRight);
                page.MarginBottom((float)config.MarginBottom);
                page.DefaultTextStyle(text => text
                    .FontFamily(config.FontFamily)
                    .FontSize((float)config.BodyFontSize)
                    .FontColor(Colors.Black));

                page.Header().Element(ComposeHeader);
                page.Content().Element(ComposeTable);
                page.Footer().Element(ComposeFooter);
            });
        }

        private void ComposeHeader(IContainer container)
        {
            container.Column(column =>
            {
                column.Item().AlignCenter().Text(GetStatementTitle())
                    .FontSize(IsLandscape(context.Template) ? 13 : 9.5f)
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
                    column.Item().AlignRight().PaddingRight(60).Height(38).Image(sealPath).FitHeight();
                }

                column.Item().PaddingTop(4).LineHorizontal(1.2f).LineColor(Colors.Black);
            });
        }

        private void ComposeTable(IContainer container)
        {
            var columns = context.Template.Config.Columns.Count == 0
                ? [new PrintPdfColumn { Name = "交易日期", Field = nameof(FlowRecord.AccountTime), Type = "Date", Width = 52 }]
                : context.Template.Config.Columns;

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
                            .FontSize((float)GetColumnFontSize(column))
                            .SemiBold();
                    }
                });

                for (var rowIndex = 0; rowIndex < context.Records.Count; rowIndex++)
                {
                    var record = context.Records[rowIndex];
                    foreach (var column in columns)
                    {
                        table.Cell()
                            .Element(BodyCellStyle)
                            .Text(text =>
                            {
                                var value = GetRecordValue(record, column, rowIndex + 1);
                                var span = text.Span(value).FontSize((float)GetColumnFontSize(column));
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
                }
            });
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

        private static IContainer HeaderCellStyle(IContainer container)
        {
            return container
                .BorderBottom(0.75f)
                .BorderColor(Colors.Black)
                .MinHeight(14)
                .PaddingHorizontal(2)
                .PaddingVertical(2)
                .AlignMiddle();
        }

        private static IContainer BodyCellStyle(IContainer container)
        {
            return container
                .MinHeight(13)
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

        private static string GetRecordValue(FlowRecord record, PrintPdfColumn column, int rowIndex)
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

            return FormatValue(value, column.Type);
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

        private static string EmptyAsDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }
    }
}
