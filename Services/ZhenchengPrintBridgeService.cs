using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed class ZhenchengPrintBridgeService : IPrintPdfService
{
    private static readonly object SyncRoot = new();
    private const string PrintDiagnosticsWrittenDataKey = "SpeedEmulator.PrintDiagnosticsWritten";
    private const string PrintDiagnosticPathDataKey = "SpeedEmulator.PrintDiagnosticPath";
    private const string PrintDiagnosticSummaryDataKey = "SpeedEmulator.PrintDiagnosticSummary";
    private static readonly HashSet<string> AgriculturalPaperWideDetailPropertyNames = new(StringComparer.Ordinal)
    {
        "AccountNameAndNumber",
        "AccountAndName",
        "OppositeAccountAndName",
        "CounterpartyAccountAndName",
        "AccountNameAndRemark",
        "AccountAndRemark",
        "OppositeAccountAndRemark",
        "CounterpartyAccountAndRemark",
        "OppositeAccountNameAndRemark",
        "OppositeAccountNameRemark",
        "CounterpartyNameAndRemark",
        "OppositeInfo",
        "CounterpartyInfo",
        "CounterpartySummary",
        "CounterpartyDetail",
        "SubRemark",
        "SubDetail",
        "DetailRemark",
        "WideDetail",
        "Postscript",
        "AdditionalInfo",
        "AttachedInfo",
        "AppendInfo"
    };

    private static VendorBridge? bridge;

    private readonly IPrintPdfService? fallbackService;

    public ZhenchengPrintBridgeService(IPrintPdfService? fallbackService = null)
    {
        this.fallbackService = fallbackService ?? new QuestPdfPrintService();
    }

    public async Task<string> GeneratePreviewAsync(PrintRenderContext context)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "print-preview");
        Directory.CreateDirectory(directory);

        var fileName = $"{SanitizeFileName(context.Bank.Name)}-{SanitizeFileName(context.BankUser.AccountName)}-{DateTime.Now:yyyyMMddHHmmss}.pdf";
        var path = Path.Combine(directory, fileName);
        await ExportInternalAsync(context, path);
        return path;
    }

    public Task ExportAsync(PrintRenderContext context, string path)
    {
        return ExportInternalAsync(context, path);
    }

    public void OpenTemplateDesigner(PrintTemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.PdfData))
        {
            throw new InvalidOperationException("模板未找到");
        }

        DefaultStimulsoftExporter.OpenTemplateDesigner(ResolveVendorDir(), template);
    }

    public bool TryCreateBlankTemplate(PrintTemplate template)
    {
        return DefaultStimulsoftExporter.TryCreateBlankTemplate(ResolveVendorDir(), template);
    }

    public bool TryHydrateTemplate(PrintRenderContext context, PrintTemplate template, bool requirePdfData = true)
    {
        if (!string.IsNullOrWhiteSpace(template.PdfData))
        {
            return true;
        }

        try
        {
            return GetBridge().TryHydrateTemplate(context, template, requirePdfData);
        }
        catch
        {
            return false;
        }
    }

    public static string GetPrintDiagnosticMessage(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            var summary = ReadExceptionData(current, PrintDiagnosticSummaryDataKey);
            var path = ReadExceptionData(current, PrintDiagnosticPathDataKey);
            if (!string.IsNullOrWhiteSpace(summary) || !string.IsNullOrWhiteSpace(path))
            {
                var builder = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    builder.Append("调试定位：").Append(summary);
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(Environment.NewLine);
                    }

                    builder.Append("诊断文件：").Append(path);
                }

                return builder.ToString();
            }

            current = current.InnerException;
        }

        return string.Empty;
    }

    public static string GetPrintDiagnosticMessageForUi(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            var summary = ReadExceptionData(current, PrintDiagnosticSummaryDataKey);
            var path = ReadExceptionData(current, PrintDiagnosticPathDataKey);
            if (!string.IsNullOrWhiteSpace(summary) || !string.IsNullOrWhiteSpace(path))
            {
                var builder = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    builder.Append("Debug: ").Append(summary);
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(Environment.NewLine);
                    }

                    builder.Append("File: ").Append(path);
                }

                return builder.ToString();
            }

            current = current.InnerException;
        }

        return string.Empty;
    }

    private async Task ExportInternalAsync(PrintRenderContext context, string path)
    {
        try
        {
            await ExportInternalCoreAsync(context, path);
        }
        catch (Exception ex)
        {
            if (!HasPrintDiagnosticsWritten(ex))
            {
                TryWritePrintFailureDiagnostic(context, path, ex, renderBranch: "outer");
            }

            throw;
        }
    }

    private async Task ExportInternalCoreAsync(PrintRenderContext context, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var forceVendorRenderer = ShouldForceVendorRenderer(context);
        if (fallbackService is not null
            && !forceVendorRenderer
            && ShouldUseEditableQuestPdfRenderer(context.Template))
        {
            await fallbackService.ExportAsync(context, path);
            return;
        }

        VendorBridge currentBridge;
        try
        {
            currentBridge = GetBridge();
        }
        catch when (fallbackService is not null
            && !forceVendorRenderer
            && CanUseQuestPdfFallback(context.Template))
        {
            await fallbackService.ExportAsync(context, path);
            return;
        }

        if (currentBridge.TryExport(context, path))
        {
            return;
        }

        if (fallbackService is not null
            && !forceVendorRenderer
            && CanUseQuestPdfFallback(context.Template))
        {
            await fallbackService.ExportAsync(context, path);
            return;
        }

        throw new InvalidOperationException("模板未找到");
    }

    private static bool ShouldApplyLocalPdfConfig(PrintRenderContext context)
    {
        return !context.Template.IsSystem;
    }

    private static void TryWritePrintFailureDiagnostic(
        PrintRenderContext context,
        string path,
        Exception exception,
        IEnumerable? transformedRecords = null,
        object? transformedBankUser = null,
        string? renderBranch = null)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpeedEmulator",
                "print-debug");
            Directory.CreateDirectory(directory);

            var filePath = Path.Combine(directory, $"print-error-{DateTime.Now:yyyyMMddHHmmssfff}.json");
            var sourceLongestTextValues = GetLongestFlowTextValues(context.Records);
            var transformedLongestTextValues = GetLongestObjectTextValues(transformedRecords);
            var suspectedOverflowTextValues = GetSuspectedOverflowTextValues(transformedRecords);
            var sourceSuspectedOverflowTextValues = GetSuspectedOverflowTextValues(context.Records);
            var payload = new
            {
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                outputPath = path,
                renderBranch,
                bank = new
                {
                    context.Bank.Id,
                    context.Bank.Name,
                    context.Bank.Type
                },
                user = new
                {
                    context.BankUser.Id,
                    context.BankUser.BankId,
                    context.BankUser.BankName,
                    context.BankUser.AccountName,
                    context.BankUser.AccountNo,
                    context.BankUser.ChapterCode,
                    context.BankUser.ChapterBranch
                },
                template = new
                {
                    context.Template.Id,
                    context.Template.BankId,
                    context.Template.VendorId,
                    context.Template.VendorBankId,
                    context.Template.IsSystem,
                    context.Template.Name,
                    context.Template.PageRows,
                    context.Template.Remark
                },
                recordCount = context.Records.Count,
                localConfig = new
                {
                    context.Template.Config.RowCount,
                    context.Template.Config.MarginLeft,
                    context.Template.Config.MarginTop,
                    context.Template.Config.MarginRight,
                    context.Template.Config.MarginBottom,
                    context.Template.Config.FontFamily,
                    context.Template.Config.ColumnMinHeight,
                    context.Template.Config.SealLeft,
                    context.Template.Config.SealTop,
                    context.Template.Config.SealRight,
                    context.Template.Config.SealBottom,
                    context.Template.Config.SealWidth,
                    columnCount = context.Template.Config.Columns.Count
                },
                longestTextValues = sourceLongestTextValues,
                sourceSuspectedOverflowTextValues,
                sourceRecordSamples = GetObjectRecordSnapshots(context.Records, 8),
                transformedBankUser = GetObjectRecordSnapshots(transformedBankUser is null ? null : new[] { transformedBankUser }, 1).FirstOrDefault(),
                transformedLongestTextValues,
                suspectedOverflowTextValues,
                transformedRecordSamples = GetObjectRecordSnapshots(transformedRecords, 8),
                exception = exception.ToString()
            };

            File.WriteAllText(
                filePath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
            SetPrintDiagnosticExceptionData(
                exception,
                filePath,
                suspectedOverflowTextValues,
                transformedLongestTextValues,
                sourceSuspectedOverflowTextValues,
                sourceLongestTextValues);
        }
        catch
        {
            // Diagnostics must never hide the original print/rendering error.
        }
    }

    private static void TryWritePrintRenderProbe(
        PrintRenderContext context,
        string path,
        IEnumerable? transformedRecords,
        string? renderBranch = null)
    {
        if (!ShouldWriteVerbosePrintDiagnostics(context))
        {
            return;
        }

        try
        {
            var suspectedOverflowValues = GetSuspectedOverflowTextValues(transformedRecords);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpeedEmulator",
                "print-debug");
            Directory.CreateDirectory(directory);

            var filePath = Path.Combine(directory, $"print-probe-{DateTime.Now:yyyyMMddHHmmssfff}.json");
            var payload = new
            {
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                outputPath = path,
                renderBranch,
                bank = new
                {
                    context.Bank.Id,
                    context.Bank.Name,
                    context.Bank.Type
                },
                user = new
                {
                    context.BankUser.Id,
                    context.BankUser.AccountName,
                    context.BankUser.AccountNo
                },
                template = new
                {
                    context.Template.Id,
                    context.Template.VendorId,
                    context.Template.VendorBankId,
                    context.Template.Name,
                    context.Template.PageRows
                },
                sourceLongestTextValues = GetLongestFlowTextValues(context.Records),
                suspectedOverflowTextValues = suspectedOverflowValues,
                sourceRecordSamples = GetObjectRecordSnapshots(context.Records, 8),
                transformedLongestTextValues = GetLongestObjectTextValues(transformedRecords),
                transformedRecordSamples = GetObjectRecordSnapshots(transformedRecords, 8)
            };

            File.WriteAllText(
                filePath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never block print rendering.
        }
    }

    private static bool ShouldWriteVerbosePrintDiagnostics(PrintRenderContext context)
    {
        if (IsPrintBridgeDebugEnabledGlobal())
        {
            return true;
        }

        var templateName = context.Template.Name ?? string.Empty;
        return templateName.Contains("\u7EB8\u8D28\u7248", StringComparison.Ordinal);
    }

    private static void MarkPrintDiagnosticsWritten(Exception exception)
    {
        try
        {
            exception.Data[PrintDiagnosticsWrittenDataKey] = true;
        }
        catch
        {
            // Best-effort marker only.
        }
    }

    private static bool HasPrintDiagnosticsWritten(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            try
            {
                if (current.Data.Contains(PrintDiagnosticsWrittenDataKey))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }

            current = current.InnerException;
        }

        return false;
    }

    private static void SetPrintDiagnosticExceptionData(
        Exception exception,
        string filePath,
        IReadOnlyList<object> suspectedOverflowTextValues,
        IReadOnlyList<object> transformedLongestTextValues,
        IReadOnlyList<object> sourceSuspectedOverflowTextValues,
        IReadOnlyList<object> sourceLongestTextValues)
    {
        try
        {
            exception.Data[PrintDiagnosticPathDataKey] = filePath;
            var summary = BuildPrintDiagnosticSummaryAscii(
                suspectedOverflowTextValues,
                transformedLongestTextValues,
                sourceSuspectedOverflowTextValues,
                sourceLongestTextValues);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                exception.Data[PrintDiagnosticSummaryDataKey] = summary;
            }
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    private static string BuildPrintDiagnosticSummaryAscii(params IReadOnlyList<object>[] diagnosticGroups)
    {
        var items = diagnosticGroups
            .Where(group => group.Count > 0)
            .SelectMany(group => group)
            .Select(FormatDiagnosticItemAscii)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return string.Join("; ", items);
    }

    private static string FormatDiagnosticItemAscii(object item)
    {
        var row = Convert.ToString(GetPropertyValue(item, "Row"), CultureInfo.InvariantCulture);
        var field = Convert.ToString(GetPropertyValue(item, "Field"), CultureInfo.InvariantCulture);
        var length = Convert.ToString(GetPropertyValue(item, "Length"), CultureInfo.InvariantCulture);
        var longestRun = Convert.ToString(GetPropertyValue(item, "LongestRun"), CultureInfo.InvariantCulture);
        var value = Convert.ToString(GetPropertyValue(item, "Value"), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(field) && string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(row))
        {
            builder.Append("Row ").Append(row).Append(' ');
        }

        builder.Append("Field ").Append(string.IsNullOrWhiteSpace(field) ? "<unknown>" : field);
        if (!string.IsNullOrWhiteSpace(length))
        {
            builder.Append(" Length ").Append(length);
        }

        if (!string.IsNullOrWhiteSpace(longestRun))
        {
            builder.Append(" LongestRun ").Append(longestRun);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(" Value: ").Append(value);
        }

        return builder.ToString();
    }

    private static string BuildPrintDiagnosticSummary(params IReadOnlyList<object>[] diagnosticGroups)
    {
        var items = diagnosticGroups
            .Where(group => group.Count > 0)
            .SelectMany(group => group)
            .Select(FormatDiagnosticItem)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return string.Join("；", items);
    }

    private static string FormatDiagnosticItem(object item)
    {
        var row = Convert.ToString(GetPropertyValue(item, "Row"), CultureInfo.InvariantCulture);
        var field = Convert.ToString(GetPropertyValue(item, "Field"), CultureInfo.InvariantCulture);
        var length = Convert.ToString(GetPropertyValue(item, "Length"), CultureInfo.InvariantCulture);
        var longestRun = Convert.ToString(GetPropertyValue(item, "LongestRun"), CultureInfo.InvariantCulture);
        var value = Convert.ToString(GetPropertyValue(item, "Value"), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(field) && string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(row))
        {
            builder.Append("第 ").Append(row).Append(" 行 ");
        }

        builder.Append(string.IsNullOrWhiteSpace(field) ? "未知字段" : field);
        if (!string.IsNullOrWhiteSpace(length))
        {
            builder.Append(" 长度 ").Append(length);
        }

        if (!string.IsNullOrWhiteSpace(longestRun))
        {
            builder.Append(" 连续字符 ").Append(longestRun);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(" 值：").Append(value);
        }

        return builder.ToString();
    }

    private static string? ReadExceptionData(Exception exception, string key)
    {
        try
        {
            return exception.Data.Contains(key)
                ? Convert.ToString(exception.Data[key], CultureInfo.CurrentCulture)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<object> GetLongestFlowTextValues(IReadOnlyList<FlowRecord> records)
    {
        var properties = typeof(FlowRecord)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            .ToList();

        var values = new List<(int Row, string Field, string Value)>();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var row = record.Index > 0 ? record.Index : index + 1;
            foreach (var property in properties)
            {
                if (property.GetValue(record) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    values.Add((row, property.Name, value));
                }
            }

            foreach (var item in record.ExtraFields)
            {
                if (!string.IsNullOrWhiteSpace(item.Value))
                {
                    values.Add((row, item.Key, item.Value));
                }
            }
        }

        return values
            .OrderByDescending(item => item.Value.Length)
            .ThenBy(item => item.Row)
            .Take(25)
            .Select(item => new
            {
                item.Row,
                item.Field,
                Length = item.Value.Length,
                Value = TruncateForDiagnostic(item.Value, 160)
            })
            .Cast<object>()
            .ToList();
    }

    private static IReadOnlyList<object> GetLongestObjectTextValues(IEnumerable? records)
    {
        if (records is null)
        {
            return [];
        }

        var values = ReadObjectTextValues(records);
        return values
            .OrderByDescending(item => NormalizeSingleLinePrintText(item.Value).Length)
            .ThenBy(item => item.Row)
            .Take(40)
            .Select(item => new
            {
                item.Row,
                item.Field,
                Length = NormalizeSingleLinePrintText(item.Value).Length,
                Value = TruncateForDiagnostic(item.Value, 220)
            })
            .Cast<object>()
            .ToList();
    }

    private static IReadOnlyList<object> GetSuspectedOverflowTextValues(IEnumerable? records)
    {
        if (records is null)
        {
            return [];
        }

        var values = ReadObjectTextValues(records);
        return values
            .Select(item => new
            {
                item.Row,
                item.Field,
                Value = NormalizeSingleLinePrintText(item.Value)
            })
            .Where(item => LooksLikeLongPaperDetail(item.Value) || HasLongContinuousTextRun(item.Value, 16))
            .OrderByDescending(item => item.Value.Length)
            .ThenBy(item => item.Row)
            .Take(40)
            .Select(item => new
            {
                item.Row,
                item.Field,
                Length = item.Value.Length,
                LongestRun = GetLongestContinuousTextRunLength(item.Value),
                Value = TruncateForDiagnostic(item.Value, 220)
            })
            .Cast<object>()
            .ToList();
    }

    private static List<(int Row, string Field, string Value)> ReadObjectTextValues(IEnumerable records)
    {
        var values = new List<(int Row, string Field, string Value)>();
        var index = 0;
        foreach (var record in records)
        {
            index++;
            if (record is null || record is string)
            {
                continue;
            }

            var row = ReadDiagnosticRow(record, index);
            foreach (var property in record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || !property.CanRead)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = property.GetValue(record);
                }
                catch
                {
                    continue;
                }

                if (value is string text && !string.IsNullOrWhiteSpace(text))
                {
                    values.Add((row, property.Name, text));
                    continue;
                }

                if (string.Equals(property.Name, nameof(FlowRecord.ExtraFields), StringComparison.Ordinal)
                    || string.Equals(property.Name, "ExtraFields", StringComparison.Ordinal))
                {
                    ReadDiagnosticExtraFields(value, row, values);
                }
            }
        }

        return values;
    }

    private static IReadOnlyList<object> GetObjectRecordSnapshots(IEnumerable? records, int take)
    {
        if (records is null || take <= 0)
        {
            return [];
        }

        var snapshots = new List<object>();
        var index = 0;
        foreach (var record in records)
        {
            index++;
            if (record is null || record is string)
            {
                continue;
            }

            var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 || !property.CanRead)
                {
                    continue;
                }

                object? value;
                try
                {
                    value = property.GetValue(record);
                }
                catch
                {
                    continue;
                }

                if (string.Equals(property.Name, nameof(FlowRecord.ExtraFields), StringComparison.Ordinal)
                    || string.Equals(property.Name, "ExtraFields", StringComparison.Ordinal))
                {
                    AddDiagnosticExtraFields(value, fields);
                    continue;
                }

                var text = Convert.ToString(value, CultureInfo.CurrentCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    fields[property.Name] = TruncateForDiagnostic(text, 220);
                }
            }

            snapshots.Add(new
            {
                Row = ReadDiagnosticRow(record, index),
                Fields = fields
            });

            if (snapshots.Count >= take)
            {
                break;
            }
        }

        return snapshots;
    }

    private static int ReadDiagnosticRow(object record, int fallback)
    {
        foreach (var propertyName in new[] { "Index", "Id" })
        {
            var property = record.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            try
            {
                var value = property.GetValue(record);
                if (value is not null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var row) && row > 0)
                {
                    return row;
                }
            }
            catch
            {
                // Best-effort diagnostics only.
            }
        }

        return fallback;
    }

    private static void AddDiagnosticExtraFields(object? extraFields, IDictionary<string, string> fields)
    {
        if (extraFields is null)
        {
            return;
        }

        if (extraFields is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = Convert.ToString(entry.Key, CultureInfo.CurrentCulture) ?? string.Empty;
                var value = Convert.ToString(entry.Value, CultureInfo.CurrentCulture) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    fields[$"ExtraFields.{key}"] = TruncateForDiagnostic(value, 220);
                }
            }

            return;
        }

        if (extraFields is not IEnumerable enumerable || extraFields is string)
        {
            return;
        }

        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            var key = Convert.ToString(GetPropertyValue(item, "Key"), CultureInfo.CurrentCulture) ?? string.Empty;
            var value = Convert.ToString(GetPropertyValue(item, "Value"), CultureInfo.CurrentCulture) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                fields[$"ExtraFields.{key}"] = TruncateForDiagnostic(value, 220);
            }
        }
    }

    private static void ReadDiagnosticExtraFields(
        object? extraFields,
        int row,
        ICollection<(int Row, string Field, string Value)> values)
    {
        if (extraFields is null)
        {
            return;
        }

        if (extraFields is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = Convert.ToString(entry.Key, CultureInfo.CurrentCulture) ?? string.Empty;
                var value = Convert.ToString(entry.Value, CultureInfo.CurrentCulture) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add((row, $"ExtraFields.{key}", value));
                }
            }

            return;
        }

        if (extraFields is not IEnumerable enumerable || extraFields is string)
        {
            return;
        }

        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            var key = Convert.ToString(GetPropertyValue(item, "Key"), CultureInfo.CurrentCulture) ?? string.Empty;
            var value = Convert.ToString(GetPropertyValue(item, "Value"), CultureInfo.CurrentCulture) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add((row, $"ExtraFields.{key}", value));
            }
        }
    }

    private static bool HasLongContinuousTextRun(string text, int minRunLength)
    {
        return GetLongestContinuousTextRunLength(text) >= minRunLength;
    }

    private static int GetLongestContinuousTextRunLength(string text)
    {
        var maxRun = 0;
        var currentRun = 0;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                currentRun++;
                maxRun = Math.Max(maxRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        return maxRun;
    }

    private static string TruncateForDiagnostic(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static VendorBridge GetBridge()
    {
        lock (SyncRoot)
        {
            if (bridge is not null)
            {
                return bridge;
            }

            var vendorDir = ResolveVendorDir();
            bridge = new VendorBridge(vendorDir);
            return bridge;
        }
    }

    private static string ResolveVendorDir()
    {
        return ZhenchengRuntimeLocator.ResolveRequired();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((string.IsNullOrWhiteSpace(value) ? "print" : value)
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "print" : sanitized;
    }

    private sealed class VendorBridge
    {
        private readonly string vendorDir;
        private readonly VendorLoadContext loadContext;
        private readonly Assembly mainAssembly;
        private readonly Type bankUserType;
        private readonly Type flowType;
        private readonly Type templateType;
        private readonly Type configType;
        private readonly Type flowListType;
        private readonly MethodInfo configFactory;
        private readonly MethodInfo renderFactory;
        private readonly MethodInfo? templateListMethod;
        private readonly MethodInfo generatePdfMethod;

        public VendorBridge(string vendorDir)
        {
            this.vendorDir = vendorDir;
            var mainDll = Path.Combine(vendorDir, ZhenchengRuntimeLocator.MainDllName);
            var previousDirectory = Directory.GetCurrentDirectory();

            try
            {
                Directory.SetCurrentDirectory(vendorDir);
                Environment.SetEnvironmentVariable(
                    "PATH",
                    vendorDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));
                CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

                loadContext = new VendorLoadContext(mainDll);
                mainAssembly = loadContext.LoadFromAssemblyPath(mainDll);
                bankUserType = RequireType("MainEntry.entity.BankUser");
                flowType = RequireType("MainEntry.entity.GenerateFlowRecord");
                templateType = RequireType("MainEntry.entity.PDFTemplate");
                configType = RequireType("MainEntry.entity.PdfConfig.PDFConfig");
                flowListType = typeof(List<>).MakeGenericType(flowType);

                var types = GetLoadableTypes(mainAssembly).ToList();
                templateListMethod = FindTemplateListMethod(types);
                configFactory = types
                    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    .FirstOrDefault(method =>
                        method.ReturnType == configType
                        && method.GetParameters() is [{ ParameterType: var parameterType }]
                        && parameterType == typeof(string))
                    ?? throw new MissingMethodException("PDFConfig factory was not found.");

                renderFactory = FindRenderFactory(types)
                    ?? throw new MissingMethodException("PDF render factory was not found.");

                var questPdfAssembly = LoadQuestPdfAssembly();
                SetQuestPdfLicense(questPdfAssembly);
                ConfigureQuestPdfFonts(questPdfAssembly, vendorDir);
                PrimeVendorDynamicImageCache(mainAssembly, vendorDir);
                generatePdfMethod = FindGeneratePdfMethod(questPdfAssembly);
            }
            finally
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.SetCurrentDirectory(previousDirectory);
                }
            }
        }

        public bool TryExport(PrintRenderContext context, string path)
        {
            var previousDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(vendorDir);
            try
            {
                var resolvedTemplate = ResolveTemplate(context);
                if (resolvedTemplate is null)
                {
                    return false;
                }

                if (resolvedTemplate.Config is null)
                {
                    if (string.IsNullOrWhiteSpace(context.Template.PdfData)
                        && string.IsNullOrWhiteSpace(resolvedTemplate.PdfData))
                    {
                        return false;
                    }

                    try
                    {
                        return DefaultStimulsoftExporter.ExportOrThrow(
                            vendorDir,
                            CreateRenderContext(context, resolvedTemplate),
                            path);
                    }
                    catch (Exception ex)
                    {
                        TryWritePrintFailureDiagnostic(context, path, ex, renderBranch: "vendor-stimulsoft");
                        var wrapped = CreateRenderException("Stimulsoft", context.Template, ex);
                        MarkPrintDiagnosticsWritten(wrapped);
                        throw wrapped;
                    }
                }

                var bankUser = CreateVendorBankUser(context);
                var records = CreateVendorFlowRecords(context);
                ApplyTemplateSpecificBankUserFieldsFromVendorRecords(context, bankUser, records);
                TryWritePrintRenderProbe(context, path, records, "vendor-records-created");
                try
                {
                    PrimeVendorDynamicImageCache(mainAssembly, vendorDir);
                    if (!IsDefaultQuestPdfBridgeDisabled() && !IsAgriculturalBankPersonalPaperTemplate(context))
                    {
                        try
                        {
                            if (DefaultQuestPdfExporter.ExportOrThrow(vendorDir, context, path))
                            {
                                return true;
                            }
                        }
                        catch (Exception)
                        {
                            if (IsPrintBridgeDebugEnabled())
                            {
                                throw;
                            }

                            // Keep the isolated vendor context as a fallback. Some
                            // older templates are sensitive to the exact assembly
                            // loading path, so a default-context miss should not
                            // block templates that already render successfully.
                        }
                    }

                    return TryExportWithVendorQuestPdf(context, resolvedTemplate, bankUser, records, path);
                }
                catch (Exception ex)
                {
                    TryWritePrintFailureDiagnostic(context, path, ex, records, bankUser, "vendor-questpdf");
                    var wrapped = CreateRenderException("QuestPDF", context.Template, ex);
                    MarkPrintDiagnosticsWritten(wrapped);
                    throw wrapped;
                }
            }
            finally
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.SetCurrentDirectory(previousDirectory);
                }
            }
        }

        public bool TryHydrateTemplate(PrintRenderContext context, PrintTemplate template, bool requirePdfData)
        {
            var previousDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(vendorDir);
            try
            {
                var resolvedTemplate = ResolveTemplate(context);
                if (resolvedTemplate is null)
                {
                    return false;
                }

                HydrateTemplate(resolvedTemplate, template);
                return requirePdfData
                    ? !string.IsNullOrWhiteSpace(template.PdfData)
                    : resolvedTemplate.Config is not null
                        || resolvedTemplate.Template is not null
                        || !string.IsNullOrWhiteSpace(template.PdfData);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.SetCurrentDirectory(previousDirectory);
                }
            }
        }

        private static void HydrateTemplate(ResolvedTemplate resolvedTemplate, PrintTemplate target)
        {
            if (string.IsNullOrWhiteSpace(target.Name))
            {
                target.Name = resolvedTemplate.Name;
            }

            if (resolvedTemplate.PageRows > 0)
            {
                target.PageRows = resolvedTemplate.PageRows;
            }

            if (!string.IsNullOrWhiteSpace(resolvedTemplate.PdfData))
            {
                target.PdfData = resolvedTemplate.PdfData;
            }

            if (resolvedTemplate.Template is null)
            {
                return;
            }

            target.VendorId = ReadLong(resolvedTemplate.Template, "Id", target.VendorId);
            target.VendorBankId = ReadLong(resolvedTemplate.Template, "BankId", target.VendorBankId);
            target.PageRows = (int)ReadLong(resolvedTemplate.Template, "PageSize", target.PageRows);
            target.Remark = ReadString(resolvedTemplate.Template, "Remark", target.Remark);
            target.PdfData = ReadString(resolvedTemplate.Template, "PdfData", target.PdfData);
        }

        private ResolvedTemplate? ResolveTemplate(PrintRenderContext context)
        {
            var hasPdfData = !string.IsNullOrWhiteSpace(context.Template.PdfData);
            var hasQuestPdfLayout = !context.Template.IsSystem
                && !hasPdfData
                && PrintTemplateQuestPdfConversionService.HasQuestPdfLayout(context.Template);
            if (hasPdfData && !hasQuestPdfLayout)
            {
                return new ResolvedTemplate(context.Template.Name, null, null, context.Template.PdfData, context.Template.PageRows);
            }

            var candidateNames = GetCandidateTemplateNames(context.Template.Name).ToList();
            var preferNameMatch = HasPreferredTemplateAlias(context.Template.Name);

            var vendorTemplate = LoadVendorTemplate(context, candidateNames, preferNameMatch);
            if (vendorTemplate is not null)
            {
                var vendorName = ReadString(vendorTemplate, "Name", context.Template.Name);
                var vendorPdfData = ReadString(vendorTemplate, "PdfData", string.Empty);
                var vendorPageRows = (int)ReadLong(vendorTemplate, "PageSize", context.Template.PageRows);
                var vendorConfig = templateType.GetProperty("PdfConfig", BindingFlags.Public | BindingFlags.Instance)?.GetValue(vendorTemplate)
                    ?? CreateVendorConfig(candidateNames.Prepend(vendorName));
                if (vendorConfig is null)
                {
                    return string.IsNullOrWhiteSpace(vendorPdfData)
                        ? null
                        : new ResolvedTemplate(vendorName, null, vendorTemplate, vendorPdfData, vendorPageRows);
                }

                if (ShouldApplyLocalPdfConfig(context))
                {
                    ApplyLocalPdfConfigToVendorConfig(vendorConfig, context.Template.Config, context.Template.PageRows);
                }
                Set(vendorTemplate, "PdfConfig", vendorConfig);
                return new ResolvedTemplate(vendorName, vendorConfig, vendorTemplate, vendorPdfData, vendorPageRows);
            }

            foreach (var name in candidateNames)
            {
                var config = CreateVendorConfig([name]);
                if (config is not null)
                {
                    if (ShouldApplyLocalPdfConfig(context))
                    {
                        ApplyLocalPdfConfigToVendorConfig(config, context.Template.Config, context.Template.PageRows);
                    }
                    return new ResolvedTemplate(name, config, null, string.Empty, context.Template.PageRows);
                }
            }

            if (hasPdfData && !hasQuestPdfLayout)
            {
                return new ResolvedTemplate(context.Template.Name, null, null, context.Template.PdfData, context.Template.PageRows);
            }

            return null;
        }

        private object? CreateVendorConfig(IEnumerable<string> names)
        {
            foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (configFactory.Invoke(null, [name]) is { } config)
                    {
                        return config;
                    }
                }
                catch
                {
                    // Not every display name has a matching hard-coded config.
                    // Continue with aliases so one miss does not block preview.
                }
            }

            return null;
        }

        private object? LoadVendorTemplate(
            PrintRenderContext context,
            IReadOnlyCollection<string> candidateNames,
            bool preferNameMatch)
        {
            if (templateListMethod is null)
            {
                return null;
            }

            var parameterType = templateListMethod.GetParameters()[0].ParameterType;
            foreach (var bankId in GetVendorTemplateBankIds(context))
            {
                try
                {
                    var argument = Convert.ChangeType(bankId, parameterType, CultureInfo.InvariantCulture);
                    if (templateListMethod.Invoke(null, [argument]) is not IEnumerable enumerable)
                    {
                        continue;
                    }

                    var templates = enumerable.Cast<object>().ToList();
                    if (!preferNameMatch)
                    {
                        var byId = context.Template.VendorId > 0
                            ? templates.FirstOrDefault(item => ReadLong(item, "Id", 0) == context.Template.VendorId)
                            : null;
                        if (byId is not null)
                        {
                            return byId;
                        }
                    }

                    var byName = candidateNames
                        .Select(candidate => templates.FirstOrDefault(item =>
                            string.Equals(ReadString(item, "Name", string.Empty), candidate, StringComparison.Ordinal)))
                        .FirstOrDefault(item => item is not null);
                    if (byName is not null)
                    {
                        return byName;
                    }
                }
                catch
                {
                    // Some vendor bank ids can fail if the local template store is incomplete.
                }
            }

            return null;
        }

        private static IEnumerable<long> GetVendorTemplateBankIds(PrintRenderContext context)
        {
            var ids = new[]
            {
                context.Template.VendorBankId,
                context.Template.BankId,
                context.Bank.Id
            };

            return ids.Where(id => id > 0).Distinct();
        }

        private static InvalidOperationException CreateRenderException(string renderer, PrintTemplate template, Exception ex)
        {
            var root = UnwrapReflectionException(ex);
            var rawMessage = string.IsNullOrWhiteSpace(root.Message) ? ex.Message : root.Message;
            var templateName = string.IsNullOrWhiteSpace(template.Name) ? "未命名模板" : template.Name;

            if (string.Equals(renderer, "QuestPDF", StringComparison.OrdinalIgnoreCase)
                && rawMessage.Contains("conflicting size constraints", StringComparison.OrdinalIgnoreCase))
            {
                return new InvalidOperationException(
                    $"模板“{templateName}”走 QuestPDF 硬编码模板，当前字段或数据长度与模板页面尺寸冲突。请先换同银行其它模板，或缩短过长字段后再生成。原始错误：{rawMessage}",
                    root);
            }

            if (string.Equals(renderer, "Stimulsoft", StringComparison.OrdinalIgnoreCase))
            {
                return new InvalidOperationException(
                    $"模板“{templateName}”走 Stimulsoft 模板流，渲染失败。请检查模板数据、运行时文件和授权环境是否完整。原始错误：{rawMessage}",
                    root);
            }

            return new InvalidOperationException($"模板“{templateName}”渲染失败：{rawMessage}", root);
        }

        private static Exception UnwrapReflectionException(Exception ex)
        {
            var current = ex;
            while (current is TargetInvocationException && current.InnerException is not null)
            {
                current = current.InnerException;
            }

            return current.GetBaseException();
        }

        private static bool IsDefaultQuestPdfBridgeDisabled()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("SPEEDEMULATOR_DISABLE_DEFAULT_QUESTPDF_BRIDGE"),
                "1",
                StringComparison.Ordinal);
        }

        private static bool IsPrintBridgeDebugEnabled()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("SPEEDEMULATOR_PRINT_BRIDGE_DEBUG"),
                "1",
                StringComparison.Ordinal);
        }

        private object CreateVendorBankUser(PrintRenderContext context)
        {
            var target = Activator.CreateInstance(bankUserType) ?? throw new InvalidOperationException("Cannot create vendor BankUser.");
            var values = CreateValueMap(context.BankUser);
            ApplyMatchingProperties(target, values);

            Set(target, "Id", context.BankUser.Id);
            Set(target, "BankId", GetVendorBankId(context));
            Set(target, "Username", FirstNotBlank(context.BankUser.AccountName, GetValue(values, "Username"), GetValue(values, "AccountName")));
            var accountNumber = NormalizePrintNumber(FirstNotBlank(context.BankUser.AccountNo, GetValue(values, "AccountNum"), GetValue(values, "Account"), GetValue(values, "CardNum")));
            var userNumber = NormalizePrintNumber(ResolveBankUserNumber(context, values));
            var customerNo = FirstNotBlank(GetValue(values, "CustomerNo"), userNumber);
            var printNo = FirstNotBlank(GetValue(values, "PrintNo"), userNumber);
            if (IsAgriculturalBankPersonalPaperTemplate(context))
            {
                userNumber = ResolveAgriculturalPersonalPaperSequence(context, values);
                customerNo = userNumber;
                printNo = userNumber;
            }

            Set(target, "UserNum", userNumber);
            Set(target, "AccountNum", accountNumber);
            Set(target, "Account", accountNumber);
            Set(target, "CardNum", accountNumber);
            Set(target, "CustomerNo", customerNo);
            Set(target, "PrintNo", printNo);
            Set(target, "IdNum", FirstNotBlank(context.BankUser.IdNumber, GetValue(values, "IdNum")));
            Set(target, "StartTime", context.BankUser.StartDate);
            Set(target, "EndTime", context.BankUser.EndDate);
            Set(target, "OpenBranch", ResolveVendorOpenBranch(context, values));
            Set(target, "Currency", NormalizeCurrency(FirstNotBlank(context.BankUser.Currency, GetValue(values, "Currency"))));
            Set(target, "Remark", context.BankUser.Remark);
            Set(target, "InitialBalance", (double)context.BankUser.OpeningBalance);
            Set(target, "IsAutoInterest", context.BankUser.AutoCalculateInterest);
            ApplyVendorBankUserStampFields(context, target, context.BankUser, values);
            Set(target, "BankTitle", context.Bank.Name);
            ApplyTemplateSpecificBankUserFields(context, target, values);
            return target;
        }

        private IList CreateVendorFlowRecords(PrintRenderContext context)
        {
            var records = (IList)(Activator.CreateInstance(flowListType) ?? throw new InvalidOperationException("Cannot create vendor flow list."));
            for (var index = 0; index < context.Records.Count; index++)
            {
                var source = context.Records[index];
                var target = Activator.CreateInstance(flowType) ?? throw new InvalidOperationException("Cannot create vendor flow record.");
                var values = CreateValueMap(source);
                ApplyFlowRecordColumnAliases(context.Bank, source, values);
                ApplyPrintFieldFallbacks(context.Bank, source, values);
                ApplyMatchingProperties(target, values);

                var tradeMoney = source.TradeMoney ?? ParseNullableDouble(GetValue(values, "TradeMoney")) ?? 0d;
                Set(target, "Index", source.Index > 0 ? source.Index : index + 1);
                Set(target, "Id", source.Id);
                Set(target, "BankId", GetVendorBankId(context));
                Set(target, "BankUserId", context.BankUser.Id);
                var accountNumber = NormalizePrintNumber(FirstNotBlank(
                    GetValue(values, "AccountNum"),
                    GetValue(values, "Account"),
                    context.BankUser.AccountNo));
                var sequenceNumber = FirstNotBlank(
                    GetValue(values, "SequenceNum"),
                    GetValue(values, "SerialNum"),
                    GetValue(values, "LogNum"),
                    $"P{index + 1:000000000}");
                var serialNumber = NormalizePrintNumber(FirstNotBlank(GetValue(values, "SerialNum"), sequenceNumber));
                Set(target, "AccountNum", accountNumber);
                Set(target, "Account", accountNumber);
                Set(target, "SequenceNum", serialNumber);
                Set(target, "SerialNum", serialNumber);
                Set(target, "OppositeAccount", NormalizePrintNumber(FirstNotBlank(source.OppositeAccount, GetValue(values, "OppositeAccount"))));
                Set(target, "AccountTime", source.AccountTime);
                Set(target, "TradeMoney", tradeMoney);
                Set(target, "Balance", source.Balance);
                Set(target, "IncomeAttribute", FirstNotBlank(source.IncomeAttribute, tradeMoney >= 0 ? "\u6536\u5165" : "\u652F\u51FA"));
                Set(target, "CreditAmount", source.CreditAmount ?? (tradeMoney > 0 ? tradeMoney : null));
                Set(target, "DebitAmount", source.DebitAmount ?? (tradeMoney < 0 ? Math.Abs(tradeMoney) : null));
                ApplyResolvedFlowTextFields(target, context.Bank, source, values);
                ApplyTemplateSpecificFlowTextLimits(context, source, values, target);
                records.Add(target);
            }

            return records;
        }

        private bool TryExportWithVendorQuestPdf(
            PrintRenderContext context,
            ResolvedTemplate resolvedTemplate,
            object bankUser,
            object records,
            string path)
        {
            Exception? lastLayoutException = null;
            foreach (var pageRows in GetVendorQuestPdfPageRowAttempts(context, resolvedTemplate))
            {
                try
                {
                    var template = CreateVendorTemplate(context, resolvedTemplate, pageRows);
                    var document = renderFactory.Invoke(null, [bankUser, records, template]);
                    if (document is null)
                    {
                        return false;
                    }

                    NormalizeVendorDynamicImageDocument(context, document);
                    generatePdfMethod.Invoke(null, [document, path]);
                    return true;
                }
                catch (Exception ex) when (ShouldRetryVendorQuestPdfWithFewerRows(context, ex, pageRows))
                {
                    lastLayoutException = ex;
                    TryDeleteFile(path);
                }
            }

            if (lastLayoutException is not null)
            {
                throw lastLayoutException;
            }

            return false;
        }

        private static IEnumerable<int?> GetVendorQuestPdfPageRowAttempts(
            PrintRenderContext context,
            ResolvedTemplate resolvedTemplate)
        {
            var pageRows = resolvedTemplate.PageRows > 0 ? resolvedTemplate.PageRows : context.Template.PageRows;
            if (pageRows <= 0)
            {
                yield return null;
                yield break;
            }

            yield return pageRows;

            if (!IsPaperPrintTemplate(context))
            {
                yield break;
            }

            for (var rows = pageRows - 1; rows >= 1; rows--)
            {
                yield return rows;
            }
        }

        private static bool ShouldRetryVendorQuestPdfWithFewerRows(PrintRenderContext context, Exception ex, int? pageRows)
        {
            return pageRows > 1
                && IsPaperPrintTemplate(context)
                && IsQuestPdfLayoutConflict(ex);
        }

        private static bool IsQuestPdfLayoutConflict(Exception ex)
        {
            for (Exception? current = ex; current is not null; current = current.InnerException)
            {
                var typeName = current.GetType().FullName ?? string.Empty;
                var message = current.Message ?? string.Empty;
                if (typeName.Contains("DocumentLayoutException", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("conflicting size constraints", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("Decoration slot", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("more space than is available", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var root = UnwrapReflectionException(ex);
            var rootTypeName = root.GetType().FullName ?? string.Empty;
            var rootMessage = root.Message ?? string.Empty;
            return rootTypeName.Contains("DocumentLayoutException", StringComparison.OrdinalIgnoreCase)
                || rootMessage.Contains("conflicting size constraints", StringComparison.OrdinalIgnoreCase)
                || rootMessage.Contains("Decoration slot", StringComparison.OrdinalIgnoreCase)
                || rootMessage.Contains("more space than is available", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Ignore intermediate render artifacts; the next attempt can
                // normally overwrite the same preview path.
            }
        }

        private object CreateVendorTemplate(PrintRenderContext context, ResolvedTemplate resolvedTemplate)
        {
            return CreateVendorTemplate(context, resolvedTemplate, null);
        }

        private object CreateVendorTemplate(PrintRenderContext context, ResolvedTemplate resolvedTemplate, int? pageRowsOverride)
        {
            var target = Activator.CreateInstance(templateType) ?? throw new InvalidOperationException("Cannot create vendor PDFTemplate.");
            var source = resolvedTemplate.Template;
            Set(target, "Id", source is null ? context.Template.VendorId : ReadLong(source, "Id", context.Template.VendorId));
            Set(target, "BankId", source is null ? GetVendorBankId(context) : ReadLong(source, "BankId", GetVendorBankId(context)));
            Set(target, "IsSystem", source is null ? context.Template.IsSystem : ReadBoolean(source, "IsSystem", context.Template.IsSystem));
            Set(target, "Name", resolvedTemplate.Name);
            Set(target, "PageSize", pageRowsOverride ?? (source is null ? resolvedTemplate.PageRows : ReadLong(source, "PageSize", resolvedTemplate.PageRows)));
            Set(target, "Remark", source is null ? context.Template.Remark : ReadString(source, "Remark", context.Template.Remark));
            Set(target, "PdfConfig", resolvedTemplate.Config);
            Set(target, "PdfData", FirstNotBlank(source is null ? null : ReadString(source, "PdfData", string.Empty), resolvedTemplate.PdfData, context.Template.PdfData));
            return target;
        }

        private static long GetVendorBankId(PrintRenderContext context)
        {
            return context.Template.VendorBankId > 0 ? context.Template.VendorBankId : context.Bank.Id;
        }

        private Assembly LoadQuestPdfAssembly()
        {
            var loaded = loadContext.Assemblies.FirstOrDefault(item => item.GetName().Name == "QuestPDF");
            return loaded ?? loadContext.LoadFromAssemblyPath(Path.Combine(vendorDir, "QuestPDF.dll"));
        }

        private static void SetQuestPdfLicense(Assembly questPdfAssembly)
        {
            var settingsType = questPdfAssembly.GetType("QuestPDF.Settings");
            var licenseProperty = settingsType?.GetProperty("License", BindingFlags.Public | BindingFlags.Static);
            if (licenseProperty is null)
            {
                return;
            }

            var licenseType = Nullable.GetUnderlyingType(licenseProperty.PropertyType) ?? licenseProperty.PropertyType;
            if (!licenseType.IsEnum)
            {
                return;
            }

            var value = Enum.Parse(licenseType, "Community");
            licenseProperty.SetValue(null, value);

            var debugProperty = settingsType?.GetProperty("EnableDebugging", BindingFlags.Public | BindingFlags.Static);
            if (debugProperty?.PropertyType == typeof(bool) && debugProperty.CanWrite)
            {
                debugProperty.SetValue(null, IsPrintBridgeDebugEnabledGlobal());
            }
        }

        private static void ConfigureQuestPdfFonts(Assembly questPdfAssembly, string vendorDir)
        {
            var fontsDir = Path.Combine(vendorDir, "fonts");
            var fontPath = Path.Combine(fontsDir, "wryh2.ttf");
            if (!Directory.Exists(fontsDir))
            {
                return;
            }

            try
            {
                var settingsType = questPdfAssembly.GetType("QuestPDF.Settings");
                var fontDiscoveryPaths = settingsType?.GetProperty("FontDiscoveryPaths", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var clearMethod = fontDiscoveryPaths?.GetType().GetMethod("Clear", Type.EmptyTypes);
                var addMethod = fontDiscoveryPaths?.GetType().GetMethod("Add", [typeof(string)]);
                clearMethod?.Invoke(fontDiscoveryPaths, null);
                addMethod?.Invoke(fontDiscoveryPaths, [fontsDir]);
            }
            catch
            {
                // Font discovery is best-effort. The renderer can still work
                // with system fonts if the bundled font API changes.
            }

            if (!File.Exists(fontPath))
            {
                return;
            }

            try
            {
                var fontManagerType = questPdfAssembly.GetType("QuestPDF.Drawing.FontManager")
                    ?? questPdfAssembly.GetTypes().FirstOrDefault(type => type.Name == "FontManager");
                var registerMethod = fontManagerType?
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "RegisterFontWithCustomName")
                        {
                            return false;
                        }

                        var parameters = method.GetParameters();
                        return parameters.Length == 2
                            && parameters[0].ParameterType == typeof(string)
                            && parameters[1].ParameterType == typeof(Stream);
                    });

                if (registerMethod is not null)
                {
                    using var stream = File.OpenRead(fontPath);
                    registerMethod.Invoke(null, ["微信", stream]);
                }
            }
            catch
            {
                // Registering the exact vendor font improves print parity, but
                // should not prevent preview generation if the font was already
                // registered or the QuestPDF API changed.
            }
        }

        private static MethodInfo FindGeneratePdfMethod(Assembly questPdfAssembly)
        {
            return questPdfAssembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .FirstOrDefault(method =>
                {
                    if (method.Name != "GeneratePdf")
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType.FullName == "QuestPDF.Infrastructure.IDocument"
                        && parameters[1].ParameterType == typeof(string);
                })
                ?? throw new MissingMethodException("QuestPDF GeneratePdf extension was not found.");
        }

        private MethodInfo? FindRenderFactory(IReadOnlyCollection<Type> types)
        {
            static IEnumerable<MethodInfo> StaticMethods(Type type)
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            bool IsRenderFactory(MethodInfo method)
            {
                var parameters = method.GetParameters();
                return parameters.Length == 3
                    && parameters[0].ParameterType == bankUserType
                    && parameters[1].ParameterType == flowListType
                    && parameters[2].ParameterType == templateType
                    && method.ReturnType.FullName == "QuestPDF.Infrastructure.IDocument";
            }

            var vendorPrintFactory = types
                .Where(type => string.Equals(type.Name, "_0003_001A_0016", StringComparison.Ordinal))
                .SelectMany(StaticMethods)
                .FirstOrDefault(IsRenderFactory);

            return vendorPrintFactory
                ?? types.SelectMany(StaticMethods).FirstOrDefault(IsRenderFactory);
        }

        private MethodInfo? FindTemplateListMethod(IReadOnlyCollection<Type> types)
        {
            var listTemplateType = typeof(List<>).MakeGenericType(templateType);
            return types
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1
                        && (parameters[0].ParameterType == typeof(long) || parameters[0].ParameterType == typeof(int))
                        && method.ReturnType == listTemplateType;
                });
        }

        private static string ReadString(object source, string propertyName, string fallback)
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            var text = Convert.ToString(value, CultureInfo.CurrentCulture);
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        private static long ReadLong(object source, string propertyName, long fallback)
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            if (value is long number)
            {
                return number;
            }

            if (value is int integer)
            {
                return integer;
            }

            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static bool ReadBoolean(object source, string propertyName, bool fallback)
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            if (value is bool boolean)
            {
                return boolean;
            }

            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
                ? parsed
                : fallback;
        }

        private Type RequireType(string fullName)
        {
            return mainAssembly.GetType(fullName, throwOnError: true)
                ?? throw new TypeLoadException(fullName);
        }

        private static PrintRenderContext CreateRenderContext(PrintRenderContext context, ResolvedTemplate resolvedTemplate)
        {
            if (string.IsNullOrWhiteSpace(resolvedTemplate.PdfData))
            {
                return context;
            }

            var template = context.Template.Clone();
            template.Name = resolvedTemplate.Name;
            template.PdfData = resolvedTemplate.PdfData;
            template.PageRows = resolvedTemplate.PageRows;

            return new PrintRenderContext
            {
                Bank = context.Bank,
                BankUser = context.BankUser,
                Records = context.Records,
                Template = template
            };
        }

        private sealed record ResolvedTemplate(string Name, object? Config, object? Template, string PdfData = "", int PageRows = 0);
    }

    private sealed class DefaultQuestPdfExporter
    {
        private static readonly object ExporterSyncRoot = new();
        private static readonly HashSet<string> ResolverDirectories = new(StringComparer.OrdinalIgnoreCase);
        private static DefaultQuestPdfExporter? current;

        private readonly string vendorDir;
        private readonly Assembly mainAssembly;
        private readonly Type bankUserType;
        private readonly Type flowType;
        private readonly Type templateType;
        private readonly Type configType;
        private readonly Type flowListType;
        private readonly MethodInfo configFactory;
        private readonly MethodInfo renderFactory;
        private readonly MethodInfo? templateListMethod;
        private readonly MethodInfo generatePdfMethod;

        private DefaultQuestPdfExporter(string vendorDir)
        {
            this.vendorDir = vendorDir;
            RegisterResolver(vendorDir);
            LoadOptionalAssemblies(vendorDir, "QuestPDF.dll");
            LoadOptionalAssemblies(vendorDir, "QuestPdfSkia.dll");
            LoadOptionalAssemblies(vendorDir, "SkiaSharp*.dll");
            LoadOptionalAssemblies(vendorDir, "HarfBuzzSharp.dll");
            LoadOptionalAssemblies(vendorDir, "SixLabors*.dll");

            var mainDll = Path.Combine(vendorDir, ZhenchengRuntimeLocator.MainDllName);
            mainAssembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
            {
                try
                {
                    return string.Equals(assembly.Location, mainDll, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }) ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(mainDll);

            bankUserType = mainAssembly.GetType("MainEntry.entity.BankUser", throwOnError: true)!;
            flowType = mainAssembly.GetType("MainEntry.entity.GenerateFlowRecord", throwOnError: true)!;
            templateType = mainAssembly.GetType("MainEntry.entity.PDFTemplate", throwOnError: true)!;
            configType = mainAssembly.GetType("MainEntry.entity.PdfConfig.PDFConfig", throwOnError: true)!;
            flowListType = typeof(List<>).MakeGenericType(flowType);

            var types = GetLoadableTypes(mainAssembly).ToList();
            templateListMethod = FindTemplateListMethod(types);
            configFactory = types
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .FirstOrDefault(method =>
                    method.ReturnType == configType
                    && method.GetParameters() is [{ ParameterType: var parameterType }]
                    && parameterType == typeof(string))
                ?? throw new MissingMethodException("PDFConfig factory was not found.");

            renderFactory = FindRenderFactory(types)
                ?? throw new MissingMethodException("PDF render factory was not found.");

            var questPdfAssembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(item => item.GetName().Name == "QuestPDF")
                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(vendorDir, "QuestPDF.dll"));
            SetQuestPdfLicense(questPdfAssembly);
            ConfigureQuestPdfFonts(questPdfAssembly, vendorDir);
            PrimeVendorDynamicImageCache(mainAssembly, vendorDir);
            generatePdfMethod = FindGeneratePdfMethod(questPdfAssembly);
        }

        public static bool ExportOrThrow(string vendorDir, PrintRenderContext context, string path)
        {
            if (!string.IsNullOrWhiteSpace(context.Template.PdfData))
            {
                return false;
            }

            var previousDirectory = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(vendorDir);
                var exporter = Get(vendorDir);
                return exporter.Export(context, path);
            }
            finally
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.SetCurrentDirectory(previousDirectory);
                }
            }
        }

        private static DefaultQuestPdfExporter Get(string vendorDir)
        {
            lock (ExporterSyncRoot)
            {
                if (current is not null && string.Equals(current.vendorDir, vendorDir, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                current = new DefaultQuestPdfExporter(vendorDir);
                return current;
            }
        }

        private bool Export(PrintRenderContext context, string path)
        {
            var resolvedTemplate = ResolveTemplate(context);
            if (resolvedTemplate is null || resolvedTemplate.Config is null)
            {
                return false;
            }

            if (!context.Template.IsSystem
                && !string.IsNullOrWhiteSpace(context.Template.PdfData)
                && !PrintTemplateQuestPdfConversionService.HasQuestPdfLayout(context.Template)
                && resolvedTemplate.Template is null)
            {
                return false;
            }

            var bankUser = CreateBankUser(context);
            var records = CreateFlowRecords(context);
            ApplyTemplateSpecificBankUserFieldsFromVendorRecords(context, bankUser, records);
            var template = resolvedTemplate.Template ?? CreateTemplate(context, resolvedTemplate);
            PrimeVendorDynamicImageCache(mainAssembly, vendorDir);
            var document = renderFactory.Invoke(null, [bankUser, records, template]);
            if (document is null)
            {
                return false;
            }

            NormalizeVendorDynamicImageDocument(context, document);
            generatePdfMethod.Invoke(null, [document, path]);
            return File.Exists(path);
        }

        private ResolvedTemplate? ResolveTemplate(PrintRenderContext context)
        {
            var candidateNames = GetCandidateTemplateNames(context.Template.Name).ToList();
            var preferNameMatch = HasPreferredTemplateAlias(context.Template.Name);

            var vendorTemplate = LoadVendorTemplate(context, candidateNames, preferNameMatch);
            if (vendorTemplate is not null)
            {
                var vendorName = ReadString(vendorTemplate, "Name", context.Template.Name);
                var vendorConfig = templateType.GetProperty("PdfConfig", BindingFlags.Public | BindingFlags.Instance)?.GetValue(vendorTemplate)
                    ?? CreateConfig(candidateNames.Prepend(vendorName));
                if (vendorConfig is null)
                {
                    return null;
                }

                if (ShouldApplyLocalPdfConfig(context))
                {
                    ApplyLocalPdfConfigToVendorConfig(vendorConfig, context.Template.Config, context.Template.PageRows);
                }
                Set(vendorTemplate, "PdfConfig", vendorConfig);
                return new ResolvedTemplate(vendorName, vendorConfig, vendorTemplate);
            }

            foreach (var name in candidateNames)
            {
                var config = CreateConfig([name]);
                if (config is not null)
                {
                    if (ShouldApplyLocalPdfConfig(context))
                    {
                        ApplyLocalPdfConfigToVendorConfig(config, context.Template.Config, context.Template.PageRows);
                    }
                    return new ResolvedTemplate(name, config, null);
                }
            }

            return null;
        }

        private object? CreateConfig(IEnumerable<string> names)
        {
            foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (configFactory.Invoke(null, [name]) is { } config)
                    {
                        return config;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private object? LoadVendorTemplate(
            PrintRenderContext context,
            IReadOnlyCollection<string> candidateNames,
            bool preferNameMatch)
        {
            if (templateListMethod is null)
            {
                return null;
            }

            var parameterType = templateListMethod.GetParameters()[0].ParameterType;
            foreach (var bankId in GetVendorTemplateBankIds(context))
            {
                try
                {
                    var argument = Convert.ChangeType(bankId, parameterType, CultureInfo.InvariantCulture);
                    if (templateListMethod.Invoke(null, [argument]) is not IEnumerable enumerable)
                    {
                        continue;
                    }

                    var templates = enumerable.Cast<object>().ToList();
                    if (!preferNameMatch)
                    {
                        var byId = context.Template.VendorId > 0
                            ? templates.FirstOrDefault(item => ReadLong(item, "Id", 0) == context.Template.VendorId)
                            : null;
                        if (byId is not null)
                        {
                            return byId;
                        }
                    }

                    var byName = candidateNames
                        .Select(candidate => templates.FirstOrDefault(item =>
                            string.Equals(ReadString(item, "Name", string.Empty), candidate, StringComparison.Ordinal)))
                        .FirstOrDefault(item => item is not null);
                    if (byName is not null)
                    {
                        return byName;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private object CreateBankUser(PrintRenderContext context)
        {
            var target = Activator.CreateInstance(bankUserType) ?? throw new InvalidOperationException("Cannot create vendor BankUser.");
            var values = CreateValueMap(context.BankUser);
            ApplyMatchingProperties(target, values);

            Set(target, "Id", context.BankUser.Id);
            Set(target, "BankId", GetVendorBankId(context));
            Set(target, "Username", FirstNotBlank(context.BankUser.AccountName, GetValue(values, "Username"), GetValue(values, "AccountName")));
            var accountNumber = NormalizePrintNumber(FirstNotBlank(context.BankUser.AccountNo, GetValue(values, "AccountNum"), GetValue(values, "Account"), GetValue(values, "CardNum")));
            var userNumber = NormalizePrintNumber(ResolveBankUserNumber(context, values));
            var customerNo = FirstNotBlank(GetValue(values, "CustomerNo"), userNumber);
            var printNo = FirstNotBlank(GetValue(values, "PrintNo"), userNumber);
            if (IsAgriculturalBankPersonalPaperTemplate(context))
            {
                userNumber = ResolveAgriculturalPersonalPaperSequence(context, values);
                customerNo = userNumber;
                printNo = userNumber;
            }

            Set(target, "UserNum", userNumber);
            Set(target, "AccountNum", accountNumber);
            Set(target, "Account", accountNumber);
            Set(target, "CardNum", accountNumber);
            Set(target, "CustomerNo", customerNo);
            Set(target, "PrintNo", printNo);
            Set(target, "IdNum", FirstNotBlank(context.BankUser.IdNumber, GetValue(values, "IdNum")));
            Set(target, "StartTime", context.BankUser.StartDate);
            Set(target, "EndTime", context.BankUser.EndDate);
            Set(target, "OpenBranch", ResolveVendorOpenBranch(context, values));
            Set(target, "Currency", NormalizeCurrency(FirstNotBlank(context.BankUser.Currency, GetValue(values, "Currency"))));
            Set(target, "Remark", context.BankUser.Remark);
            Set(target, "InitialBalance", (double)context.BankUser.OpeningBalance);
            Set(target, "IsAutoInterest", context.BankUser.AutoCalculateInterest);
            ApplyVendorBankUserStampFields(context, target, context.BankUser, values);
            Set(target, "BankTitle", context.Bank.Name);
            ApplyTemplateSpecificBankUserFields(context, target, values);
            return target;
        }

        private IList CreateFlowRecords(PrintRenderContext context)
        {
            var records = (IList)(Activator.CreateInstance(flowListType) ?? throw new InvalidOperationException("Cannot create vendor flow list."));
            for (var index = 0; index < context.Records.Count; index++)
            {
                var source = context.Records[index];
                var target = Activator.CreateInstance(flowType) ?? throw new InvalidOperationException("Cannot create vendor flow record.");
                var values = CreateValueMap(source);
                ApplyFlowRecordColumnAliases(context.Bank, source, values);
                ApplyPrintFieldFallbacks(context.Bank, source, values);
                ApplyMatchingProperties(target, values);

                var tradeMoney = source.TradeMoney ?? ParseNullableDouble(GetValue(values, "TradeMoney")) ?? 0d;
                Set(target, "Index", source.Index > 0 ? source.Index : index + 1);
                Set(target, "Id", source.Id);
                Set(target, "BankId", GetVendorBankId(context));
                Set(target, "BankUserId", context.BankUser.Id);
                var accountNumber = NormalizePrintNumber(FirstNotBlank(
                    GetValue(values, "AccountNum"),
                    GetValue(values, "Account"),
                    context.BankUser.AccountNo));
                var sequenceNumber = FirstNotBlank(
                    GetValue(values, "SequenceNum"),
                    GetValue(values, "SerialNum"),
                    GetValue(values, "LogNum"),
                    $"P{index + 1:000000000}");
                var serialNumber = NormalizePrintNumber(FirstNotBlank(GetValue(values, "SerialNum"), sequenceNumber));
                Set(target, "AccountNum", accountNumber);
                Set(target, "Account", accountNumber);
                Set(target, "SequenceNum", serialNumber);
                Set(target, "SerialNum", serialNumber);
                Set(target, "OppositeAccount", NormalizePrintNumber(FirstNotBlank(source.OppositeAccount, GetValue(values, "OppositeAccount"))));
                Set(target, "AccountTime", source.AccountTime);
                Set(target, "TradeMoney", tradeMoney);
                Set(target, "Balance", source.Balance);
                Set(target, "IncomeAttribute", FirstNotBlank(source.IncomeAttribute, tradeMoney >= 0 ? "\u6536\u5165" : "\u652F\u51FA"));
                Set(target, "CreditAmount", source.CreditAmount ?? (tradeMoney > 0 ? tradeMoney : null));
                Set(target, "DebitAmount", source.DebitAmount ?? (tradeMoney < 0 ? Math.Abs(tradeMoney) : null));
                ApplyResolvedFlowTextFields(target, context.Bank, source, values);
                ApplyTemplateSpecificFlowTextLimits(context, source, values, target);
                records.Add(target);
            }

            return records;
        }

        private object CreateTemplate(PrintRenderContext context, ResolvedTemplate resolvedTemplate)
        {
            var target = Activator.CreateInstance(templateType) ?? throw new InvalidOperationException("Cannot create vendor PDFTemplate.");
            var source = resolvedTemplate.Template;
            Set(target, "Id", source is null ? context.Template.VendorId : ReadLong(source, "Id", context.Template.VendorId));
            Set(target, "BankId", source is null ? GetVendorBankId(context) : ReadLong(source, "BankId", GetVendorBankId(context)));
            Set(target, "IsSystem", source is null ? context.Template.IsSystem : ReadBoolean(source, "IsSystem", context.Template.IsSystem));
            Set(target, "Name", resolvedTemplate.Name);
            Set(target, "PageSize", source is null ? context.Template.PageRows : ReadLong(source, "PageSize", context.Template.PageRows));
            Set(target, "Remark", source is null ? context.Template.Remark : ReadString(source, "Remark", context.Template.Remark));
            Set(target, "PdfConfig", resolvedTemplate.Config);
            Set(target, "PdfData", source is null ? context.Template.PdfData : ReadString(source, "PdfData", context.Template.PdfData));
            return target;
        }

        private MethodInfo? FindRenderFactory(IReadOnlyCollection<Type> types)
        {
            static IEnumerable<MethodInfo> StaticMethods(Type type)
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            bool IsRenderFactory(MethodInfo method)
            {
                var parameters = method.GetParameters();
                return parameters.Length == 3
                    && parameters[0].ParameterType == bankUserType
                    && parameters[1].ParameterType == flowListType
                    && parameters[2].ParameterType == templateType
                    && method.ReturnType.FullName == "QuestPDF.Infrastructure.IDocument";
            }

            var vendorPrintFactory = types
                .Where(type => string.Equals(type.Name, "_0003_001A_0016", StringComparison.Ordinal))
                .SelectMany(StaticMethods)
                .FirstOrDefault(IsRenderFactory);

            return vendorPrintFactory
                ?? types.SelectMany(StaticMethods).FirstOrDefault(IsRenderFactory);
        }

        private MethodInfo? FindTemplateListMethod(IReadOnlyCollection<Type> types)
        {
            var listTemplateType = typeof(List<>).MakeGenericType(templateType);
            return types
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1
                        && (parameters[0].ParameterType == typeof(long) || parameters[0].ParameterType == typeof(int))
                        && method.ReturnType == listTemplateType;
                });
        }

        private static IEnumerable<long> GetVendorTemplateBankIds(PrintRenderContext context)
        {
            var ids = new[]
            {
                context.Template.VendorBankId,
                context.Template.BankId,
                context.Bank.Id
            };

            return ids.Where(id => id > 0).Distinct();
        }

        private static long GetVendorBankId(PrintRenderContext context)
        {
            return context.Template.VendorBankId > 0 ? context.Template.VendorBankId : context.Bank.Id;
        }

        private static string ReadString(object source, string propertyName, string fallback)
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            var text = Convert.ToString(value, CultureInfo.CurrentCulture);
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        private static long ReadLong(object source, string propertyName, long fallback)
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            if (value is long number)
            {
                return number;
            }

            if (value is int integer)
            {
                return integer;
            }

            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static bool ReadBoolean(object source, string propertyName, bool fallback)
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            if (value is bool boolean)
            {
                return boolean;
            }

            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
                ? parsed
                : fallback;
        }

        private static void RegisterResolver(string vendorDir)
        {
            lock (ResolverDirectories)
            {
                if (!ResolverDirectories.Add(vendorDir))
                {
                    return;
                }
            }

            AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
            {
                foreach (var directory in ResolverDirectories)
                {
                    var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    try
                    {
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                    }
                    catch
                    {
                        return null;
                    }
                }

                return null;
            };
        }

        private static void LoadOptionalAssemblies(string vendorDir, string searchPattern)
        {
            foreach (var file in Directory.EnumerateFiles(vendorDir, searchPattern))
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(file).Name;
                    if (AssemblyLoadContext.Default.Assemblies.Any(item => string.Equals(item.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
                }
                catch
                {
                }
            }
        }

        private static void SetQuestPdfLicense(Assembly questPdfAssembly)
        {
            var settingsType = questPdfAssembly.GetType("QuestPDF.Settings");
            var licenseProperty = settingsType?.GetProperty("License", BindingFlags.Public | BindingFlags.Static);
            if (licenseProperty is null)
            {
                return;
            }

            var licenseType = Nullable.GetUnderlyingType(licenseProperty.PropertyType) ?? licenseProperty.PropertyType;
            if (!licenseType.IsEnum)
            {
                return;
            }

            var value = Enum.Parse(licenseType, "Community");
            licenseProperty.SetValue(null, value);

            var debugProperty = settingsType?.GetProperty("EnableDebugging", BindingFlags.Public | BindingFlags.Static);
            if (debugProperty?.PropertyType == typeof(bool) && debugProperty.CanWrite)
            {
                debugProperty.SetValue(null, IsPrintBridgeDebugEnabledGlobal());
            }
        }

        private static void ConfigureQuestPdfFonts(Assembly questPdfAssembly, string vendorDir)
        {
            var fontsDir = Path.Combine(vendorDir, "fonts");
            var fontPath = Path.Combine(fontsDir, "wryh2.ttf");
            if (!Directory.Exists(fontsDir))
            {
                return;
            }

            try
            {
                var settingsType = questPdfAssembly.GetType("QuestPDF.Settings");
                var fontDiscoveryPaths = settingsType?.GetProperty("FontDiscoveryPaths", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var clearMethod = fontDiscoveryPaths?.GetType().GetMethod("Clear", Type.EmptyTypes);
                var addMethod = fontDiscoveryPaths?.GetType().GetMethod("Add", [typeof(string)]);
                clearMethod?.Invoke(fontDiscoveryPaths, null);
                addMethod?.Invoke(fontDiscoveryPaths, [fontsDir]);
            }
            catch
            {
            }

            if (!File.Exists(fontPath))
            {
                return;
            }

            try
            {
                var fontManagerType = questPdfAssembly.GetType("QuestPDF.Drawing.FontManager")
                    ?? questPdfAssembly.GetTypes().FirstOrDefault(type => type.Name == "FontManager");
                var registerMethod = fontManagerType?
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "RegisterFontWithCustomName")
                        {
                            return false;
                        }

                        var parameters = method.GetParameters();
                        return parameters.Length == 2
                            && parameters[0].ParameterType == typeof(string)
                            && parameters[1].ParameterType == typeof(Stream);
                    });

                if (registerMethod is not null)
                {
                    using var stream = File.OpenRead(fontPath);
                    registerMethod.Invoke(null, ["寰俊", stream]);
                }
            }
            catch
            {
            }
        }

        private static MethodInfo FindGeneratePdfMethod(Assembly questPdfAssembly)
        {
            return questPdfAssembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .FirstOrDefault(method =>
                {
                    if (method.Name != "GeneratePdf")
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType.FullName == "QuestPDF.Infrastructure.IDocument"
                        && parameters[1].ParameterType == typeof(string);
                })
                ?? throw new MissingMethodException("QuestPDF GeneratePdf extension was not found.");
        }

        private sealed record ResolvedTemplate(string Name, object? Config, object? Template);
    }

    private sealed class DefaultStimulsoftExporter
    {
        private static readonly object ExporterSyncRoot = new();
        private static readonly HashSet<string> ResolverDirectories = new(StringComparer.OrdinalIgnoreCase);
        private static DefaultStimulsoftExporter? current;

        private readonly string vendorDir;
        private readonly Type bankUserType;
        private readonly Type flowType;
        private readonly Type templateType;
        private readonly Type flowListType;
        private readonly MethodInfo exportMethod;
        private readonly MethodInfo templateDesignerMethod;
        private readonly Assembly mainAssembly;

        private DefaultStimulsoftExporter(string vendorDir)
        {
            this.vendorDir = vendorDir;
            RegisterResolver(vendorDir);
            LoadOptionalAssemblies(vendorDir, "Stimulsoft*.dll");

            var mainDll = Path.Combine(vendorDir, ZhenchengRuntimeLocator.MainDllName);
            mainAssembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
            {
                try
                {
                    return string.Equals(assembly.Location, mainDll, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }) ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(mainDll);

            bankUserType = mainAssembly.GetType("MainEntry.entity.BankUser", throwOnError: true)!;
            flowType = mainAssembly.GetType("MainEntry.entity.GenerateFlowRecord", throwOnError: true)!;
            templateType = mainAssembly.GetType("MainEntry.entity.PDFTemplate", throwOnError: true)!;
            flowListType = typeof(List<>).MakeGenericType(flowType);
            exportMethod = GetLoadableTypes(mainAssembly)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return method.ReturnType == typeof(void)
                        && parameters.Length == 4
                        && parameters[0].ParameterType == bankUserType
                        && parameters[1].ParameterType == flowListType
                        && parameters[2].ParameterType == typeof(Stream)
                        && parameters[3].ParameterType == typeof(string);
                })
                ?? throw new MissingMethodException("Vendor Stimulsoft export method was not found.");
            templateDesignerMethod = exportMethod.DeclaringType?
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method =>
                {
                    var parameters = method.GetParameters();
                    return method.ReturnType == typeof(void)
                        && parameters.Length == 1
                        && parameters[0].ParameterType == templateType;
                })
                ?? throw new MissingMethodException("Vendor Stimulsoft template designer method was not found.");

            InitializeStimulsoftPrintRuntime();
        }

        public static bool TryExport(string vendorDir, PrintRenderContext context, string path)
        {
            if (string.IsNullOrWhiteSpace(context.Template.PdfData))
            {
                return false;
            }

            try
            {
                var exporter = Get(vendorDir);
                exporter.Export(context, path);
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        public static bool ExportOrThrow(string vendorDir, PrintRenderContext context, string path)
        {
            if (string.IsNullOrWhiteSpace(context.Template.PdfData))
            {
                return false;
            }

            var previousDirectory = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(vendorDir);
                var exporter = Get(vendorDir);
                exporter.Export(context, path);
                return File.Exists(path);
            }
            finally
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.SetCurrentDirectory(previousDirectory);
                }
            }
        }

        public static void OpenTemplateDesigner(string vendorDir, PrintTemplate template)
        {
            if (string.IsNullOrWhiteSpace(template.PdfData))
            {
                throw new InvalidOperationException("模板未找到");
            }

            var previousDirectory = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(vendorDir);
                var exporter = Get(vendorDir);
                exporter.OpenTemplateDesigner(template);
            }
            finally
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.SetCurrentDirectory(previousDirectory);
                }
            }
        }

        public static bool TryCreateBlankTemplate(string vendorDir, PrintTemplate template)
        {
            var previousDirectory = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(vendorDir);
                var exporter = Get(vendorDir);
                exporter.CreateBlankTemplate(template);
                return !string.IsNullOrWhiteSpace(template.PdfData);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.SetCurrentDirectory(previousDirectory);
                }
            }
        }

        private static DefaultStimulsoftExporter Get(string vendorDir)
        {
            lock (ExporterSyncRoot)
            {
                if (current is not null && string.Equals(current.vendorDir, vendorDir, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                current = new DefaultStimulsoftExporter(vendorDir);
                return current;
            }
        }

        private static void RegisterResolver(string vendorDir)
        {
            lock (ResolverDirectories)
            {
                if (!ResolverDirectories.Add(vendorDir))
                {
                    return;
                }
            }

            AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
            {
                foreach (var directory in ResolverDirectories)
                {
                    var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    try
                    {
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                    }
                    catch
                    {
                        return null;
                    }
                }

                return null;
            };
        }

        private static void LoadOptionalAssemblies(string vendorDir, string searchPattern)
        {
            foreach (var file in Directory.EnumerateFiles(vendorDir, searchPattern))
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(file).Name;
                    if (AssemblyLoadContext.Default.Assemblies.Any(item => string.Equals(item.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
                }
                catch
                {
                }
            }
        }

        private void InitializeStimulsoftPrintRuntime()
        {
            if (exportMethod.DeclaringType is { } exportType)
            {
                RuntimeHelpers.RunClassConstructor(exportType.TypeHandle);
                foreach (var method in exportType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(method => !method.IsSpecialName
                        && method.ReturnType == typeof(void)
                        && method.GetParameters().Length == 0))
                {
                    try
                    {
                        method.Invoke(null, null);
                    }
                    catch
                    {
                    }
                }
            }

            RegisterReportUtilityFunctions();
        }

        private void RegisterReportUtilityFunctions()
        {
            var reportUtilsType = mainAssembly.GetType("MainEntry.utils.ReportUtils");
            if (reportUtilsType is null)
            {
                return;
            }

            var functionsType = AssemblyLoadContext.Default.Assemblies
                .Select(assembly => assembly.GetType("Stimulsoft.Report.Dictionary.StiFunctions"))
                .FirstOrDefault(type => type is not null);
            if (functionsType is null)
            {
                return;
            }

            var addFunction = functionsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "AddFunction")
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 10
                        && parameters[0].ParameterType == typeof(string)
                        && parameters[3].ParameterType == typeof(string)
                        && parameters[4].ParameterType == typeof(Type)
                        && parameters[5].ParameterType == typeof(Type);
                });
            if (addFunction is null)
            {
                return;
            }

            foreach (var function in reportUtilsType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var parameters = function.GetParameters();
                RegisterReportFunction(addFunction, function, parameters, "ReportUtils", reportUtilsType);
                RegisterReportFunction(addFunction, function, parameters, "string", reportUtilsType);
            }
        }

        private static void RegisterReportFunction(MethodInfo addFunction, MethodInfo function, ParameterInfo[] parameters, string category, Type ownerType)
        {
            try
            {
                addFunction.Invoke(null,
                [
                    category,
                    category,
                    function.Name,
                    function.Name,
                    ownerType,
                    function.ReturnType,
                    string.Empty,
                    parameters.Select(parameter => parameter.ParameterType).ToArray(),
                    parameters.Select(parameter => parameter.Name ?? string.Empty).ToArray(),
                    parameters.Select(parameter => parameter.Name ?? string.Empty).ToArray()
                ]);
            }
            catch
            {
            }
        }

        private void Export(PrintRenderContext context, string path)
        {
            var bankUser = CreateBankUser(context);
            var records = CreateFlowRecords(context);
            ApplyTemplateSpecificBankUserFieldsFromVendorRecords(context, bankUser, records);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(context.Template.PdfData));
            exportMethod.Invoke(null, [bankUser, records, stream, path]);
        }

        private void OpenTemplateDesigner(PrintTemplate template)
        {
            var vendorTemplate = CreateTemplate(template);
            templateDesignerMethod.Invoke(null, [vendorTemplate]);
            CopyTemplateBack(vendorTemplate, template);
        }

        private void CreateBlankTemplate(PrintTemplate template)
        {
            var reportType = AssemblyLoadContext.Default.Assemblies
                .Select(assembly => assembly.GetType("Stimulsoft.Report.StiReport", false))
                .FirstOrDefault(type => type is not null)
                ?? throw new MissingMemberException("Stimulsoft.Report.StiReport was not found.");
            var report = Activator.CreateInstance(reportType)
                ?? throw new InvalidOperationException("Cannot create blank Stimulsoft report.");
            var saveToString = reportType.GetMethod("SaveToString", Type.EmptyTypes)
                ?? throw new MissingMethodException(reportType.FullName, "SaveToString");

            template.PdfData = Convert.ToString(saveToString.Invoke(report, null)) ?? string.Empty;
            template.PageSize = "A4Portrait";
            template.PageRows = 0;
            template.Remark = string.Empty;
            template.VendorId = 0;
            template.VendorBankId = 0;
            template.IsSystem = false;
        }

        private object CreateTemplate(PrintTemplate source)
        {
            var target = Activator.CreateInstance(templateType) ?? throw new InvalidOperationException("Cannot create vendor PDFTemplate.");
            Set(target, "Id", source.VendorId > 0 ? source.VendorId : source.Id);
            Set(target, "BankId", source.VendorBankId > 0 ? source.VendorBankId : source.BankId);
            Set(target, "IsSystem", false);
            Set(target, "Name", source.Name);
            Set(target, "PageSize", source.PageRows);
            Set(target, "Remark", source.Remark);
            Set(target, "PdfData", source.PdfData);
            return target;
        }

        private static void CopyTemplateBack(object vendorTemplate, PrintTemplate target)
        {
            target.Name = ReadString(vendorTemplate, "Name", target.Name);
            target.PageRows = ReadInt(vendorTemplate, "PageSize", target.PageRows);
            target.Remark = ReadString(vendorTemplate, "Remark", target.Remark);
            target.PdfData = ReadString(vendorTemplate, "PdfData", target.PdfData);
            target.IsSystem = false;

            var vendorId = ReadLong(vendorTemplate, "Id", target.VendorId);
            if (vendorId > 0)
            {
                target.VendorId = vendorId;
            }

            var vendorBankId = ReadLong(vendorTemplate, "BankId", target.VendorBankId);
            if (vendorBankId > 0)
            {
                target.VendorBankId = vendorBankId;
            }
        }

        private object CreateBankUser(PrintRenderContext context)
        {
            var target = Activator.CreateInstance(bankUserType) ?? throw new InvalidOperationException("Cannot create vendor BankUser.");
            var values = CreateValueMap(context.BankUser);
            ApplyMatchingProperties(target, values);

            Set(target, "Id", context.BankUser.Id);
            Set(target, "BankId", GetVendorBankId(context));
            Set(target, "Username", FirstNotBlank(context.BankUser.AccountName, GetValue(values, "Username"), GetValue(values, "AccountName")));
            var accountNumber = NormalizePrintNumber(FirstNotBlank(context.BankUser.AccountNo, GetValue(values, "AccountNum"), GetValue(values, "Account"), GetValue(values, "CardNum")));
            var userNumber = NormalizePrintNumber(ResolveBankUserNumber(context, values));
            var customerNo = FirstNotBlank(GetValue(values, "CustomerNo"), userNumber);
            var printNo = FirstNotBlank(GetValue(values, "PrintNo"), userNumber);
            if (IsAgriculturalBankPersonalPaperTemplate(context))
            {
                userNumber = ResolveAgriculturalPersonalPaperSequence(context, values);
                customerNo = userNumber;
                printNo = userNumber;
            }

            Set(target, "UserNum", userNumber);
            Set(target, "AccountNum", accountNumber);
            Set(target, "Account", accountNumber);
            Set(target, "CardNum", accountNumber);
            Set(target, "CustomerNo", customerNo);
            Set(target, "PrintNo", printNo);
            Set(target, "IdNum", FirstNotBlank(context.BankUser.IdNumber, GetValue(values, "IdNum")));
            Set(target, "StartTime", context.BankUser.StartDate);
            Set(target, "EndTime", context.BankUser.EndDate);
            Set(target, "OpenBranch", ResolveVendorOpenBranch(context, values));
            Set(target, "Currency", NormalizeCurrency(FirstNotBlank(context.BankUser.Currency, GetValue(values, "Currency"))));
            Set(target, "Remark", context.BankUser.Remark);
            Set(target, "InitialBalance", (double)context.BankUser.OpeningBalance);
            Set(target, "IsAutoInterest", context.BankUser.AutoCalculateInterest);
            ApplyVendorBankUserStampFields(context, target, context.BankUser, values);
            Set(target, "BankTitle", context.Bank.Name);
            ApplyTemplateSpecificBankUserFields(context, target, values);
            return target;
        }

        private IList CreateFlowRecords(PrintRenderContext context)
        {
            var records = (IList)(Activator.CreateInstance(flowListType) ?? throw new InvalidOperationException("Cannot create vendor flow list."));
            for (var index = 0; index < context.Records.Count; index++)
            {
                var source = context.Records[index];
                var target = Activator.CreateInstance(flowType) ?? throw new InvalidOperationException("Cannot create vendor flow record.");
                var values = CreateValueMap(source);
                ApplyFlowRecordColumnAliases(context.Bank, source, values);
                ApplyPrintFieldFallbacks(context.Bank, source, values);
                ApplyMatchingProperties(target, values);

                var tradeMoney = source.TradeMoney ?? ParseNullableDouble(GetValue(values, "TradeMoney")) ?? 0d;
                Set(target, "Index", source.Index > 0 ? source.Index : index + 1);
                Set(target, "Id", source.Id);
                Set(target, "BankId", GetVendorBankId(context));
                Set(target, "BankUserId", context.BankUser.Id);
                var accountNumber = NormalizePrintNumber(FirstNotBlank(
                    GetValue(values, "AccountNum"),
                    GetValue(values, "Account"),
                    context.BankUser.AccountNo));
                var sequenceNumber = FirstNotBlank(
                    GetValue(values, "SequenceNum"),
                    GetValue(values, "SerialNum"),
                    GetValue(values, "LogNum"),
                    $"P{index + 1:000000000}");
                var serialNumber = NormalizePrintNumber(FirstNotBlank(GetValue(values, "SerialNum"), sequenceNumber));
                Set(target, "AccountNum", accountNumber);
                Set(target, "Account", accountNumber);
                Set(target, "SequenceNum", serialNumber);
                Set(target, "SerialNum", serialNumber);
                Set(target, "OppositeAccount", NormalizePrintNumber(FirstNotBlank(source.OppositeAccount, GetValue(values, "OppositeAccount"))));
                Set(target, "AccountTime", source.AccountTime);
                Set(target, "TradeMoney", tradeMoney);
                Set(target, "Balance", source.Balance);
                Set(target, "IncomeAttribute", FirstNotBlank(source.IncomeAttribute, tradeMoney >= 0 ? "\u6536\u5165" : "\u652F\u51FA"));
                Set(target, "CreditAmount", source.CreditAmount ?? (tradeMoney > 0 ? tradeMoney : null));
                Set(target, "DebitAmount", source.DebitAmount ?? (tradeMoney < 0 ? Math.Abs(tradeMoney) : null));
                ApplyResolvedFlowTextFields(target, context.Bank, source, values);
                ApplyTemplateSpecificFlowTextLimits(context, source, values, target);
                records.Add(target);
            }

            return records;
        }

        private static string ReadString(object source, string propertyName, string fallback)
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            var text = Convert.ToString(value, CultureInfo.CurrentCulture);
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        private static int ReadInt(object source, string propertyName, int fallback)
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            if (value is int number)
            {
                return number;
            }

            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static long ReadLong(object source, string propertyName, long fallback)
        {
            var value = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
            if (value is long number)
            {
                return number;
            }

            if (value is int integer)
            {
                return integer;
            }

            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static long GetVendorBankId(PrintRenderContext context)
        {
            return context.Template.VendorBankId > 0 ? context.Template.VendorBankId : context.Bank.Id;
        }
    }

    private sealed class VendorLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public VendorLoadContext(string mainAssemblyPath)
            : base(isCollectible: false)
        {
            resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }

    private static Dictionary<string, object?> CreateValueMap(object source)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            values[property.Name] = property.GetValue(source);
        }

        if (source.GetType().GetProperty("ExtraFields")?.GetValue(source) is IDictionary<string, string> extraFields)
        {
            foreach (var item in extraFields)
            {
                var key = NormalizeFieldName(item.Key);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    values[key] = item.Value;
                }
            }
        }

        return values;
    }

    private static void ApplyMatchingProperties(object target, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            if (values.TryGetValue(property.Name, out var value))
            {
                Set(target, property, value);
            }
        }
    }

    private static void ApplyVendorBankUserStampFields(
        PrintRenderContext context,
        object target,
        BankUser bankUser,
        IReadOnlyDictionary<string, object?> values)
    {
        var stampCode = FirstNotBlank(
            bankUser.ChapterCode,
            GetValue(values, "StampCode"),
            GetValue(values, "ChapterCode"),
            GetValue(values, "ZhangCode"));
        var stampBranch = FirstNotBlank(
            bankUser.ChapterBranch,
            GetValue(values, "StampBranch"),
            GetValue(values, "ChapterBranch"),
            GetValue(values, "OpenBranch"));
        var printBranch = ResolveBankUserPrintBranch(context, values);

        if (IsAgriculturalBankPersonalPaperTemplate(context))
        {
            stampCode = NormalizeAgriculturalPaperStampCode(stampCode);
            stampBranch = NormalizeAgriculturalPaperStampBranch(stampBranch);
            printBranch = NormalizeAgriculturalPaperPrintBranch(printBranch);
        }

        Set(target, "IsPrintStamp", bankUser.ShouldPrintSeal);
        Set(target, "ZhangImg", bankUser.ShouldPrintSeal ? FirstNotBlank(bankUser.SealImagePath, GetValue(values, "ZhangImg")) : string.Empty);
        Set(target, "StampCode", stampCode);
        Set(target, "StampBranch", stampBranch);
        Set(target, "ChapterCode", stampCode);
        Set(target, "ChapterBranch", stampBranch);
        Set(target, "ZhangCode", stampCode);
        var resolvedPrintBranch = FirstNotBlank(printBranch, IsAgriculturalBankPersonalPaperTemplate(context) ? string.Empty : stampBranch);
        Set(target, "PrintBranch", resolvedPrintBranch);
        Set(target, "PrintAgency", resolvedPrintBranch);
        Set(target, "PrintOrg", resolvedPrintBranch);
        Set(target, "PrintInstitution", resolvedPrintBranch);
        Set(target, "PrintNet", resolvedPrintBranch);
        Set(target, "PrintNetwork", resolvedPrintBranch);
    }

    private static void ApplyTemplateSpecificBankUserFields(
        PrintRenderContext context,
        object target,
        IReadOnlyDictionary<string, object?> values)
    {
        if (IsIcbcPrintContext(context))
        {
            var operationArea = FirstNotBlank(
                ResolveIcbcOperationAreaFromRecords(context),
                GetBankUserColumnValue(context, values, "\u5730\u533A", "\u5730\u533A\u53F7", "\u64CD\u4F5C\u5730\u533A"),
                GetValue(values, "OperationArea"),
                GetValue(values, nameof(FlowRecord.AreaNum)));
            if (!string.IsNullOrWhiteSpace(operationArea))
            {
                Set(target, "OperationArea", operationArea);
            }
        }
    }

    private static void ApplyTemplateSpecificBankUserFieldsFromVendorRecords(
        PrintRenderContext context,
        object target,
        IEnumerable? vendorRecords)
    {
        if (!IsIcbcPrintContext(context) || vendorRecords is null)
        {
            return;
        }

        var operationArea = vendorRecords
            .Cast<object>()
            .Select(record => FirstNotBlank(
                Convert.ToString(GetPropertyValue(record, nameof(FlowRecord.AreaNum)), CultureInfo.CurrentCulture),
                Convert.ToString(GetPropertyValue(record, nameof(FlowRecord.BranchNum)), CultureInfo.CurrentCulture),
                Convert.ToString(GetPropertyValue(record, nameof(FlowRecord.NetNum)), CultureInfo.CurrentCulture)))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        operationArea = FirstNotBlank(operationArea, ResolveIcbcOperationAreaFromRecords(context));
        if (!string.IsNullOrWhiteSpace(operationArea))
        {
            Set(target, "OperationArea", operationArea);
        }
    }

    private static string ResolveIcbcOperationAreaFromRecords(PrintRenderContext context)
    {
        foreach (var record in context.Records)
        {
            var values = CreateValueMap(record);
            ApplyFlowRecordColumnAliases(context.Bank, record, values);
            var operationArea = FirstNotBlank(
                record.AreaNum,
                GetValue(values, nameof(FlowRecord.AreaNum)),
                GetFlowExtraFieldValue(context.Bank, record, values, "\u5730\u533A", "\u5730\u533A\u53F7"),
                record.BranchNum,
                GetValue(values, nameof(FlowRecord.BranchNum)),
                record.NetNum,
                GetValue(values, nameof(FlowRecord.NetNum)));
            if (!string.IsNullOrWhiteSpace(operationArea))
            {
                return operationArea;
            }
        }

        return string.Empty;
    }

    private static string ResolveBankUserPrintBranch(PrintRenderContext context, IReadOnlyDictionary<string, object?> values)
    {
        var configured = GetBankUserColumnValue(
            context,
            values,
            "\u6253\u5370\u673A\u6784",
            "\u6253\u5370\u7F51\u70B9",
            "\u64CD\u4F5C\u7F51\u70B9",
            "\u53D7\u7406\u884C",
            "\u673A\u6784\u53F7",
            "\u7F51\u70B9\u53F7");

        return FirstNotBlank(
            configured,
            GetValue(values, "PrintAgency"),
            GetValue(values, "PrintOrg"),
            GetValue(values, "PrintInstitution"),
            GetValue(values, "PrintBranch"),
            GetValue(values, "PrintNet"),
            GetValue(values, "PrintNetwork"),
            GetValue(values, "OpenBranch"));
    }

    private static string ResolveVendorOpenBranch(PrintRenderContext context, IReadOnlyDictionary<string, object?> values)
    {
        var openBranch = FirstNotBlank(context.BankUser.OpenBranch, GetValue(values, "OpenBranch"));
        return IsAgriculturalBankPersonalPaperTemplate(context)
            ? NormalizeAgriculturalPaperPrintBranch(openBranch)
            : openBranch;
    }

    private static string NormalizeAgriculturalPaperStampCode(string? value)
    {
        var text = NormalizeSingleLinePrintText(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return LimitSingleLinePrintText(text, 16);
    }

    private static string NormalizeAgriculturalPaperStampBranch(string? value)
    {
        var text = NormalizeSingleLinePrintText(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return LimitSingleLinePrintText(text, 16);
    }

    private static string NormalizeAgriculturalPaperPrintBranch(string? value)
    {
        var text = NormalizeSingleLinePrintText(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = CompactAgriculturalPaperBranchName(text);
        return LimitSingleLinePrintText(text, 4);
    }

    private static string CompactAgriculturalPaperBranchName(string value)
    {
        var text = NormalizeSingleLinePrintText(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.StartsWith("\u4E2D\u56FD\u519C\u4E1A\u94F6\u884C\u80A1\u4EFD\u6709\u9650\u516C\u53F8", StringComparison.Ordinal))
        {
            text = "\u519C\u884C" + text["\u4E2D\u56FD\u519C\u4E1A\u94F6\u884C\u80A1\u4EFD\u6709\u9650\u516C\u53F8".Length..];
        }
        else if (text.StartsWith("\u4E2D\u56FD\u519C\u4E1A\u94F6\u884C", StringComparison.Ordinal))
        {
            text = "\u519C\u884C" + text["\u4E2D\u56FD\u519C\u4E1A\u94F6\u884C".Length..];
        }
        else if (text.StartsWith("\u519C\u4E1A\u94F6\u884C", StringComparison.Ordinal))
        {
            text = "\u519C\u884C" + text["\u519C\u4E1A\u94F6\u884C".Length..];
        }

        if (text.Length > 4 && text.EndsWith("\u652F\u884C", StringComparison.Ordinal))
        {
            text = text[..^2];
        }
        else if (text.Length > 4 && text.EndsWith("\u8425\u4E1A\u90E8", StringComparison.Ordinal))
        {
            text = text[..^3];
        }

        return text;
    }

    private static void ApplyFlowRecordColumnAliases(Bank bank, FlowRecord source, Dictionary<string, object?> values)
    {
        if (source.ExtraFields.Count == 0 || bank.FlowColumns.Count == 0)
        {
            return;
        }

        var flowColumnIndex = 0;
        foreach (var column in bank.FlowColumns)
        {
            var columnName = column.Name ?? string.Empty;
            if (string.Equals(columnName, "ID", StringComparison.Ordinal)
                || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.Ordinal))
            {
                continue;
            }

            var (field, _) = ExcelColumnFieldResolver.ResolveFlowRecordField(columnName);
            if (string.IsNullOrWhiteSpace(field) || IsValuePresent(values, field))
            {
                flowColumnIndex++;
                continue;
            }

            var legacyKey = CreateLegacyFlowRecordExtraFieldPath(bank.Name, columnName, flowColumnIndex);
            if (TryGetExtraFieldValue(source, legacyKey, out var value))
            {
                values[field] = value;
            }

            flowColumnIndex++;
        }
    }

    private static void ApplyPrintFieldFallbacks(Bank bank, FlowRecord source, Dictionary<string, object?> values)
    {
        if (!bank.Name.Contains("\u5EFA\u884C", StringComparison.Ordinal)
            && !bank.Name.Contains("\u5EFA\u8BBE", StringComparison.Ordinal))
        {
            return;
        }

        var tradePlace = FirstNotBlank(
            GetValue(values, nameof(FlowRecord.TradePlace)),
            GetValue(values, nameof(FlowRecord.NetNum)),
            GetFlowExtraFieldValue(bank, source, values, "\u5546\u6237\u7F51\u70B9\u53F7\u53CA\u540D\u79F0", "\u5546\u6237\u7F51\u70B9\u53F7\u53CA\u540D", "\u4EA4\u6613\u5730\u70B9", "\u4EA4\u6613\u573A\u6240", "\u7F51\u70B9\u540D\u79F0", "\u4EA4\u6613\u7F51\u70B9", "\u5730\u70B9"),
            GetValue(values, nameof(FlowRecord.TradeExplain)),
            GetValue(values, nameof(FlowRecord.MerchantName)),
            GetValue(values, nameof(FlowRecord.ProductBrief)),
            GetValue(values, nameof(FlowRecord.OppositeBank)));
        SetValueIfBlank(values, nameof(FlowRecord.TradePlace), tradePlace);
        SetValueIfBlank(values, nameof(FlowRecord.NetNum), tradePlace);

        var remark = FirstNotBlank(
            GetValue(values, nameof(FlowRecord.Remark)),
            GetFlowExtraFieldValue(bank, source, values, "\u9644\u8A00", "\u8F6C\u8D26\u9644\u8A00", "\u5907\u6CE8", "\u7559\u8A00", "\u56DE\u5355\u4E2A\u6027\u4FE1\u606F", "\u7528\u9014", "\u4EA4\u6613\u7528\u9014"),
            GetValue(values, nameof(FlowRecord.Usage)),
            GetValue(values, nameof(FlowRecord.TradeExplain)),
            GetValue(values, nameof(FlowRecord.ProductBrief)),
            GetValue(values, nameof(FlowRecord.TradePlace)));
        SetValueIfBlank(values, nameof(FlowRecord.Remark), remark);
    }

    private static void ApplyResolvedFlowTextFields(object target, Bank bank, FlowRecord source, IReadOnlyDictionary<string, object?> values)
    {
        var productName = FirstNotBlank(source.ProductName, GetValue(values, nameof(FlowRecord.ProductName)));
        var productBrief = FirstNotBlank(source.ProductBrief, GetValue(values, nameof(FlowRecord.ProductBrief)));
        var productType = FirstNotBlank(source.ProductType, GetValue(values, nameof(FlowRecord.ProductType)));
        var tradeExplain = FirstNotBlank(source.TradeExplain, GetValue(values, nameof(FlowRecord.TradeExplain)));
        var tradeChannel = FirstNotBlank(source.TradeChannel, GetValue(values, nameof(FlowRecord.TradeChannel)));
        var tradeChannelEn = FirstNotBlank(source.TradeChannelEn, GetValue(values, nameof(FlowRecord.TradeChannelEn)));
        var tradeCode = FirstNotBlank(source.TradeCode, GetValue(values, nameof(FlowRecord.TradeCode)));
        var cashCheck = FirstNotBlank(source.CashCheck, GetValue(values, nameof(FlowRecord.CashCheck)));
        var usage = FirstNotBlank(source.Usage, GetValue(values, nameof(FlowRecord.Usage)));
        var merchantName = FirstNotBlank(source.MerchantName, GetValue(values, nameof(FlowRecord.MerchantName)));
        var branchNum = FirstNotBlank(source.BranchNum, GetValue(values, nameof(FlowRecord.BranchNum)));
        var netNum = FirstNotBlank(source.NetNum, GetValue(values, nameof(FlowRecord.NetNum)));
        var areaNum = FirstNotBlank(source.AreaNum, GetValue(values, nameof(FlowRecord.AreaNum)));
        var tradePlace = FirstNotBlank(source.TradePlace, GetValue(values, nameof(FlowRecord.TradePlace)));
        var remark = FirstNotBlank(source.Remark, GetValue(values, nameof(FlowRecord.Remark)));
        var areaText = FirstNotBlank(
            areaNum,
            GetFlowExtraFieldValue(bank, source, values, "\u5730\u533A", "\u5730\u533A\u53F7"),
            branchNum,
            netNum);
        var locationText = FirstNotBlank(
            tradePlace,
            netNum,
            GetFlowExtraFieldValue(bank, source, values, "\u5730\u70B9", "\u4EA4\u6613\u7F51\u70B9", "\u4EA4\u6613\u5730\u70B9", "\u4EA4\u6613\u573A\u6240", "\u7F51\u70B9\u540D\u79F0", "\u4EA4\u6613\u884C\u6240"),
            areaNum,
            branchNum);

        if (bank.Name.Contains("\u5EFA\u884C", StringComparison.Ordinal)
            || bank.Name.Contains("\u5EFA\u8BBE", StringComparison.Ordinal))
        {
            tradePlace = FirstNotBlank(tradePlace, netNum, tradeExplain, merchantName, productBrief, source.OppositeBank, GetValue(values, nameof(FlowRecord.OppositeBank)));
            netNum = FirstNotBlank(netNum, tradePlace);
            tradeExplain = FirstNotBlank(tradeExplain, tradePlace);
            usage = FirstNotBlank(usage, tradePlace);
            merchantName = FirstNotBlank(merchantName, tradePlace);
            remark = FirstNotBlank(remark, tradePlace, usage, tradeExplain, productBrief);
        }

        if (IsIcbcBank(bank))
        {
            areaNum = FirstNotBlank(areaNum, areaText);
            branchNum = FirstNotBlank(branchNum, areaNum);
            netNum = FirstNotBlank(netNum, areaNum);
        }

        if (IsAgriculturalBank(bank))
        {
            netNum = FirstNotBlank(netNum, locationText, tradePlace, areaNum, branchNum);
            tradePlace = FirstNotBlank(tradePlace, locationText, netNum, areaNum, branchNum);
            branchNum = FirstNotBlank(branchNum, netNum, tradePlace, areaNum);
            areaNum = FirstNotBlank(areaNum, netNum, tradePlace, branchNum);
            tradeChannelEn = FirstNotBlank(tradeChannelEn, tradeChannel);
        }

        if (IsMinshengBank(bank))
        {
            branchNum = FirstNotBlank(branchNum, netNum, tradePlace);
            netNum = FirstNotBlank(netNum, branchNum, tradePlace);
            tradePlace = FirstNotBlank(tradePlace, netNum, branchNum);
        }

        if (IsPingAnBank(bank) || IsPostalBank(bank))
        {
            var summary = FirstNotBlank(remark, productBrief, tradeExplain, productName, usage);
            remark = FirstNotBlank(remark, summary);
            productBrief = FirstNotBlank(productBrief, summary);
            tradeExplain = FirstNotBlank(tradeExplain, summary);
            productName = FirstNotBlank(productName, summary);
            tradeCode = FirstNotBlank(tradeCode, summary);
            netNum = FirstNotBlank(netNum, tradePlace, branchNum);
            branchNum = FirstNotBlank(branchNum, netNum, tradePlace);
            tradePlace = FirstNotBlank(tradePlace, netNum, branchNum);
        }

        if (IsChinaMerchantsBank(bank))
        {
            var summaryCode = FirstNotBlank(productBrief, tradeExplain, productName, remark, usage);
            productBrief = FirstNotBlank(productBrief, summaryCode);
            productName = FirstNotBlank(productName, summaryCode);
            tradeExplain = FirstNotBlank(tradeExplain, summaryCode);
            tradeCode = FirstNotBlank(tradeCode, summaryCode);
        }

        if (IsBankOfChina(bank))
        {
            var tradeName = FirstNotBlank(productName, tradeExplain, productBrief, tradeCode, cashCheck, remark, usage);
            productName = FirstNotBlank(productName, tradeName);
            tradeExplain = FirstNotBlank(tradeExplain, tradeName);
            tradeCode = FirstNotBlank(tradeCode, tradeName);
        }

        if (IsWechatBank(bank))
        {
            productName = FirstNotBlank(
                productName,
                productType,
                tradeExplain,
                productBrief,
                cashCheck,
                usage,
                remark);
            productType = FirstNotBlank(productType, productName);
        }

        if (IsAlipayBank(bank))
        {
            productType = FirstNotBlank(
                productType,
                source.IncomeAttribute,
                GetValue(values, nameof(FlowRecord.IncomeAttribute)),
                usage);
            remark = FirstNotBlank(remark, productBrief, tradeExplain, usage);
            tradeChannel = FirstNotBlank(tradeChannel, cashCheck, tradeExplain);
        }

        Set(target, nameof(FlowRecord.ProductName), productName);
        Set(target, nameof(FlowRecord.ProductBrief), productBrief);
        Set(target, nameof(FlowRecord.ProductType), productType);
        Set(target, nameof(FlowRecord.TradeExplain), tradeExplain);
        Set(target, nameof(FlowRecord.TradeChannel), tradeChannel);
        Set(target, nameof(FlowRecord.TradeCode), tradeCode);
        Set(target, nameof(FlowRecord.CashCheck), cashCheck);
        Set(target, nameof(FlowRecord.Usage), usage);
        Set(target, nameof(FlowRecord.MerchantName), merchantName);
        Set(target, nameof(FlowRecord.BranchNum), branchNum);
        Set(target, nameof(FlowRecord.NetNum), netNum);
        Set(target, nameof(FlowRecord.AreaNum), areaNum);
        Set(target, nameof(FlowRecord.TradePlace), tradePlace);
        Set(target, nameof(FlowRecord.Remark), remark);
        Set(target, nameof(FlowRecord.TradeChannelEn), tradeChannelEn);

        if (IsWechatBank(bank))
        {
            ApplyWechatPrintFieldAliases(target, source, values, productName);
        }

        if (IsAlipayBank(bank))
        {
            ApplyAlipayPrintFieldAliases(target, source, values, merchantName);
        }

        if (IsChinaMerchantsBank(bank))
        {
            ApplySummaryTextAliases(target, FirstNotBlank(productBrief, tradeCode, tradeExplain, productName, remark, usage));
        }

        if (IsPostalBank(bank))
        {
            ApplySummaryTextAliases(target, FirstNotBlank(remark, productBrief, productName, tradeExplain, tradeCode, usage));
        }
    }

    private static bool IsAlipayBank(Bank bank)
    {
        return bank.Name.Contains("\u652F\u4ED8\u5B9D", StringComparison.Ordinal);
    }

    private static bool IsWechatBank(Bank bank)
    {
        return bank.Name.Contains("\u5FAE\u4FE1", StringComparison.Ordinal);
    }

    private static bool IsIcbcBank(Bank bank)
    {
        return bank.Name.Contains("\u5DE5\u884C", StringComparison.Ordinal)
            || bank.Name.Contains("\u5DE5\u5546", StringComparison.Ordinal);
    }

    private static bool IsIcbcPrintContext(PrintRenderContext context)
    {
        var templateName = context.Template.Name ?? string.Empty;
        return IsIcbcBank(context.Bank)
            || context.Bank.Id == 4
            || context.Template.BankId == 4
            || context.Template.VendorBankId == 62
            || templateName.Contains("\u5DE5\u884C", StringComparison.Ordinal)
            || templateName.Contains("\u5DE5\u5546", StringComparison.Ordinal);
    }

    private static bool IsAgriculturalBank(Bank bank)
    {
        return bank.Name.Contains("\u519C\u884C", StringComparison.Ordinal)
            || bank.Name.Contains("\u519C\u4E1A", StringComparison.Ordinal);
    }

    private static bool IsMinshengBank(Bank bank)
    {
        return bank.Name.Contains("\u6C11\u751F", StringComparison.Ordinal);
    }

    private static bool IsPingAnBank(Bank bank)
    {
        return bank.Name.Contains("\u5E73\u5B89", StringComparison.Ordinal);
    }

    private static bool IsPostalBank(Bank bank)
    {
        return bank.Name.Contains("\u90AE\u653F", StringComparison.Ordinal)
            || bank.Name.Contains("\u90AE\u50A8", StringComparison.Ordinal);
    }

    private static bool IsChinaMerchantsBank(Bank bank)
    {
        return bank.Name.Contains("\u62DB\u884C", StringComparison.Ordinal)
            || bank.Name.Contains("\u62DB\u5546", StringComparison.Ordinal);
    }

    private static bool IsBankOfChina(Bank bank)
    {
        return bank.Name.Contains("\u4E2D\u884C", StringComparison.Ordinal)
            || bank.Name.Contains("\u4E2D\u56FD\u94F6\u884C", StringComparison.Ordinal);
    }

    private static void ApplySummaryTextAliases(object target, string value)
    {
        foreach (var propertyName in new[]
        {
            nameof(FlowRecord.ProductCode),
            nameof(FlowRecord.ProductType),
            nameof(FlowRecord.VoucherType),
            nameof(FlowRecord.HandleStatus),
            nameof(FlowRecord.NoticeType),
            nameof(FlowRecord.InterfacePage),
            nameof(FlowRecord.AppNum),
            nameof(FlowRecord.DepositTerm),
            nameof(FlowRecord.AgreedTerm),
            nameof(FlowRecord.CreditType)
        })
        {
            SetTargetValueIfBlank(target, propertyName, value);
        }
    }

    private static void ApplyAlipayPrintFieldAliases(
        object target,
        FlowRecord source,
        IReadOnlyDictionary<string, object?> values,
        string merchantName)
    {
        var receiptNum = FirstNotBlank(
            source.ReceiptNum,
            GetValue(values, nameof(FlowRecord.ReceiptNum)),
            merchantName,
            source.MerchantName,
            GetValue(values, nameof(FlowRecord.MerchantName)));

        SetTargetValueIfBlank(target, nameof(FlowRecord.ReceiptNum), receiptNum);
        SetTargetValueIfBlank(target, nameof(FlowRecord.MerchantName), receiptNum);
        Set(target, nameof(FlowRecord.ProductType), string.Empty);
        Set(target, nameof(FlowRecord.OppositeUsername), string.Empty);
        Set(target, nameof(FlowRecord.Remark), string.Empty);
        Set(target, nameof(FlowRecord.TradeChannel), string.Empty);
    }

    private static void ApplyWechatPrintFieldAliases(
        object target,
        FlowRecord source,
        IReadOnlyDictionary<string, object?> values,
        string productName)
    {
        var serialNumber = NormalizePrintNumber(FirstNotBlank(
            source.SerialNum,
            GetValue(values, nameof(FlowRecord.SerialNum)),
            GetValue(values, nameof(FlowRecord.SequenceNum)),
            GetValue(values, nameof(FlowRecord.LogNum)),
            GetValue(values, nameof(FlowRecord.TradeCode))));

        SetTargetValueIfBlank(target, nameof(FlowRecord.SerialNum), serialNumber);
        SetTargetValueIfBlank(target, nameof(FlowRecord.SequenceNum), serialNumber);
        SetTargetValueIfBlank(target, nameof(FlowRecord.LogNum), serialNumber);
        SetTargetValueIfBlank(target, nameof(FlowRecord.TradeCode), serialNumber);
        SetTargetValueIfBlank(target, nameof(FlowRecord.VoucherNum), serialNumber);
        SetTargetValueIfBlank(target, nameof(FlowRecord.ReceiptNum), serialNumber);
        SetTargetValueIfBlank(target, nameof(FlowRecord.ProductName), productName);
        SetTargetValueIfBlank(target, nameof(FlowRecord.ProductType), productName);
    }

    private static void ApplyTemplateSpecificFlowTextLimits(
        PrintRenderContext context,
        FlowRecord source,
        IReadOnlyDictionary<string, object?> values,
        object target)
    {
        if (!IsAgriculturalBankPersonalPaperTemplate(context))
        {
            return;
        }

        NormalizeAgriculturalPaperPaperRowFields(source, values, target);

        TrimTextProperty(target, nameof(FlowRecord.NetNum), 8);
        TrimTextProperty(target, nameof(FlowRecord.TradePlace), 8);
        TrimTextProperty(target, nameof(FlowRecord.VoucherNum), 10);
        TrimTextProperty(target, nameof(FlowRecord.SerialNum), 10);
        TrimTextProperty(target, nameof(FlowRecord.SequenceNum), 10);
        TrimTextProperty(target, nameof(FlowRecord.LogNum), 10);
        TrimTextProperty(target, nameof(FlowRecord.OppositeBank), 8);
    }

    private static void MoveAgriculturalPaperLongDetailToWideField(
        FlowRecord source,
        IReadOnlyDictionary<string, object?> values,
        object target)
    {
        var rawWideDetail = GetAgriculturalPaperLongDetailCandidates(source, values, target)
            .Select(NormalizeSingleLinePrintText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .FirstOrDefault(LooksLikeLongPaperDetail);
        if (string.IsNullOrWhiteSpace(rawWideDetail))
        {
            return;
        }

        var safeWideDetail = NormalizeSingleLinePrintText(rawWideDetail);
        SetAgriculturalPaperWideDetail(target, safeWideDetail);
        RestoreAgriculturalPaperMainRowRemark(source, values, target, rawWideDetail);
        RestoreAgriculturalPaperCounterpartyFields(source, values, target);

        ClearAgriculturalPaperNarrowDetailFields(target);
        ClearAgriculturalPaperUnsafeLongTextFields(target, rawWideDetail, safeWideDetail);
    }

    private static void NormalizeAgriculturalPaperPaperRowFields(
        FlowRecord source,
        IReadOnlyDictionary<string, object?> values,
        object target)
    {
        var longDetail = GetAgriculturalPaperLongDetailCandidates(source, values, target)
            .Select(NormalizeSingleLinePrintText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .FirstOrDefault(LooksLikeLongPaperDetail);
        var oppositeUsername = NormalizeSingleLinePrintText(FirstNotBlank(
            source.OppositeUsername,
            GetValue(values, nameof(FlowRecord.OppositeUsername)),
            GetValue(values, "OppositeUserName"),
            GetValue(values, "OppositeName"),
            GetValue(values, "CounterpartyName"),
            ReadStringProperty(target, nameof(FlowRecord.OppositeUsername), string.Empty)));
        var oppositeAccount = NormalizeSingleLinePrintText(FirstNotBlank(
            source.OppositeAccount,
            GetValue(values, nameof(FlowRecord.OppositeAccount)),
            GetValue(values, "OppositeAccountNum"),
            GetValue(values, "CounterpartyAccount"),
            GetValue(values, "OtherAccount"),
            ReadStringProperty(target, nameof(FlowRecord.OppositeAccount), string.Empty)));
        var subAccountToken = ExtractAgriculturalPaperAccountToken(
            source.SubAccountNum,
            GetValue(values, nameof(FlowRecord.SubAccountNum)),
            ReadStringProperty(target, nameof(FlowRecord.SubAccountNum), string.Empty),
            oppositeAccount);

        var paperSubrowDetail = NormalizeSingleLinePrintText(longDetail ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(oppositeUsername))
        {
            Set(target, nameof(FlowRecord.OppositeUsername), PrepareAgriculturalPaperCounterpartyText(oppositeUsername));
        }

        if (!string.IsNullOrWhiteSpace(subAccountToken))
        {
            Set(target, nameof(FlowRecord.OppositeAccount), PrepareAgriculturalPaperAccountText(subAccountToken));
        }
        else if (!string.IsNullOrWhiteSpace(oppositeAccount))
        {
            var accountToken = ExtractAgriculturalPaperAccountToken(oppositeAccount);
            Set(
                target,
                nameof(FlowRecord.OppositeAccount),
                PrepareAgriculturalPaperAccountText(
                    string.IsNullOrWhiteSpace(accountToken) ? oppositeAccount : accountToken));
        }

        RestoreAgriculturalPaperMainRowRemark(source, values, target, longDetail ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(paperSubrowDetail))
        {
            Set(target, nameof(FlowRecord.Remark), PrepareAgriculturalPaperDetailText(paperSubrowDetail));
        }

        ClearAgriculturalPaperNarrowDetailFields(target);
        ClearAgriculturalPaperUnsafeLongTextFields(target, longDetail ?? string.Empty);
        ClearStringProperties(target, AgriculturalPaperWideDetailPropertyNames.ToArray());
        ClearStringProperties(
            target,
            nameof(FlowRecord.Account),
            nameof(FlowRecord.AccountNum),
            nameof(FlowRecord.SubAccountNum),
            "CardNum",
            "CardNo",
            "BankAccount",
            "BankAccountNo",
            "BankCardNo");
    }

    private static void NormalizeAgriculturalPaperMainRowFields(
        FlowRecord source,
        IReadOnlyDictionary<string, object?> values,
        object target)
    {
        var detailParts = new List<string>();
        AddAgriculturalPaperDetailPart(
            detailParts,
            ReadFirstStringProperty(target, AgriculturalPaperWideDetailPropertyNames));

        var oppositeAccount = NormalizeSingleLinePrintText(FirstNotBlank(
            source.OppositeAccount,
            GetValue(values, nameof(FlowRecord.OppositeAccount)),
            GetValue(values, "OppositeAccountNum"),
            GetValue(values, "CounterpartyAccount"),
            GetValue(values, "OtherAccount"),
            ReadStringProperty(target, nameof(FlowRecord.OppositeAccount), string.Empty)));
        var subAccountToken = ExtractAgriculturalPaperAccountToken(
            source.SubAccountNum,
            GetValue(values, nameof(FlowRecord.SubAccountNum)),
            ReadStringProperty(target, nameof(FlowRecord.SubAccountNum), string.Empty),
            oppositeAccount);
        if (!string.IsNullOrWhiteSpace(subAccountToken))
        {
            Set(target, nameof(FlowRecord.SubAccountNum), subAccountToken);
        }

        var longDetail = GetAgriculturalPaperLongDetailCandidates(source, values, target)
            .Select(NormalizeSingleLinePrintText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .FirstOrDefault(LooksLikeLongPaperDetail);
        AddAgriculturalPaperDetailPart(detailParts, longDetail);

        var hasWideDetail = detailParts.Count > 0;
        if (hasWideDetail)
        {
            SetAgriculturalPaperWideDetail(target, string.Join(" ", detailParts));
        }

        ClearStringProperties(
            target,
            nameof(FlowRecord.Account),
            nameof(FlowRecord.AccountNum),
            "CardNum",
            "CardNo",
            "BankAccount",
            "BankAccountNo",
            "BankCardNo");
    }

    private static string ExtractAgriculturalPaperAccountToken(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = NormalizeSingleLinePrintText(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = new string(token
                    .Where(character => char.IsLetterOrDigit(character) || character == '*' || character == '-')
                    .ToArray());
                var significantCount = candidate.Count(character => char.IsLetterOrDigit(character));
                if (candidate.Length is >= 6 and <= 32 && significantCount >= 6)
                {
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    private static bool HasWritableStringProperty(object target, IEnumerable<string> propertyNames)
    {
        var type = target.GetType();
        foreach (var propertyName in propertyNames)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is { CanWrite: true } && property.PropertyType == typeof(string))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadFirstStringProperty(object target, IEnumerable<string> propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = NormalizeSingleLinePrintText(ReadStringProperty(target, propertyName, string.Empty));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static void AddAgriculturalPaperDetailPart(ICollection<string> parts, string? value)
    {
        var normalized = NormalizeSingleLinePrintText(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!parts.Contains(normalized, StringComparer.Ordinal))
        {
            parts.Add(normalized);
        }
    }

    private static void ClearStringProperties(object target, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanWrite || property.PropertyType != typeof(string))
            {
                continue;
            }

            property.SetValue(target, string.Empty);
        }
    }

    private static IEnumerable<string> GetAgriculturalPaperLongDetailCandidates(
        FlowRecord source,
        IReadOnlyDictionary<string, object?> values,
        object target)
    {
        yield return source.Remark;
        yield return source.TradeExplain;
        yield return source.Usage;
        yield return source.MerchantName;

        foreach (var fieldName in new[]
                 {
                     nameof(FlowRecord.Remark),
                     nameof(FlowRecord.TradeExplain),
                     nameof(FlowRecord.Usage),
                     nameof(FlowRecord.MerchantName),
                     "TradeMemo",
                     "Description",
                     "Note",
                     "Postscript",
                     "AdditionalInfo",
                     "AttachedInfo",
                     "AppendInfo",
                     "MerchantOrderNo",
                     "MerchantOrderNum",
                     "MerchantNo"
                 })
        {
            yield return GetValue(values, fieldName) ?? string.Empty;
            yield return ReadStringProperty(target, fieldName, string.Empty);
        }

        foreach (var item in source.ExtraFields)
        {
            if (IsAgriculturalPaperLongDetailFieldName(item.Key))
            {
                yield return item.Value;
            }
        }
    }

    private static bool IsAgriculturalPaperLongDetailFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        return fieldName.Contains("\u5907\u6CE8", StringComparison.Ordinal)
            || fieldName.Contains("\u9644\u8A00", StringComparison.Ordinal)
            || fieldName.Contains("\u7559\u8A00", StringComparison.Ordinal)
            || fieldName.Contains("\u8BF4\u660E", StringComparison.Ordinal)
            || fieldName.Contains("\u7528\u9014", StringComparison.Ordinal)
            || fieldName.Contains("\u5546\u6237", StringComparison.Ordinal)
            || fieldName.Contains("\u5546\u5BB6", StringComparison.Ordinal)
            || fieldName.Contains("\u4EA4\u6613\u5BF9\u65B9", StringComparison.Ordinal)
            || fieldName.Contains("\u5BF9\u65B9\u6237\u540D", StringComparison.Ordinal)
            || fieldName.Contains("\u5BF9\u65B9\u540D\u79F0", StringComparison.Ordinal)
            || fieldName.Contains("\u6237\u540D", StringComparison.Ordinal);
    }

    private static void SetAgriculturalPaperWideDetail(object target, string wideDetail)
    {
        foreach (var propertyName in AgriculturalPaperWideDetailPropertyNames)
        {
            Set(target, propertyName, wideDetail);
        }
    }

    private static string PrepareAgriculturalPaperAccountText(string text)
    {
        return AppendAgriculturalPaperInlineSeparator(NormalizeAgriculturalPaperInlineDetailText(text));
    }

    private static string PrepareAgriculturalPaperCounterpartyText(string text)
    {
        return AppendAgriculturalPaperInlineSeparator(NormalizeAgriculturalPaperInlineDetailText(text));
    }

    private static string PrepareAgriculturalPaperDetailText(string text)
    {
        return NormalizeAgriculturalPaperInlineDetailText(text);
    }

    private static string NormalizeAgriculturalPaperInlineDetailText(string text)
    {
        return NormalizeSingleLinePrintText(text)
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string AppendAgriculturalPaperInlineSeparator(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text + "  ";
    }

    private static void RestoreAgriculturalPaperMainRowRemark(
        FlowRecord source,
        IReadOnlyDictionary<string, object?> values,
        object target,
        string rawWideDetail)
    {
        var currentRemark = NormalizeSingleLinePrintText(ReadStringProperty(target, nameof(FlowRecord.Remark), string.Empty));
        if (string.IsNullOrWhiteSpace(currentRemark))
        {
            return;
        }

        var normalizedRawDetail = NormalizeSingleLinePrintText(rawWideDetail);
        if (!string.Equals(currentRemark, normalizedRawDetail, StringComparison.Ordinal)
            && !LooksLikeLongPaperDetail(currentRemark))
        {
            return;
        }

        var shortRemark = NormalizeSingleLinePrintText(FirstNotBlank(
            source.TradeExplain,
            GetValue(values, nameof(FlowRecord.TradeExplain)),
            source.Usage,
            GetValue(values, nameof(FlowRecord.Usage)),
            source.MerchantName,
            GetValue(values, nameof(FlowRecord.MerchantName)),
            source.ProductBrief,
            GetValue(values, nameof(FlowRecord.ProductBrief))));
        Set(target, nameof(FlowRecord.Remark), LooksLikeLongPaperDetail(shortRemark) ? string.Empty : shortRemark);
    }

    private static void RestoreAgriculturalPaperCounterpartyFields(
        FlowRecord source,
        IReadOnlyDictionary<string, object?> values,
        object target)
    {
        var oppositeUsername = FirstNotBlank(
            source.OppositeUsername,
            GetValue(values, nameof(FlowRecord.OppositeUsername)),
            GetValue(values, "OppositeUserName"),
            GetValue(values, "OppositeName"),
            GetValue(values, "CounterpartyName"));
        if (!string.IsNullOrWhiteSpace(oppositeUsername))
        {
            Set(target, nameof(FlowRecord.OppositeUsername), NormalizeSingleLinePrintText(oppositeUsername));
        }

        var oppositeAccount = FirstNotBlank(
            source.OppositeAccount,
            GetValue(values, nameof(FlowRecord.OppositeAccount)),
            GetValue(values, "CounterpartyAccount"),
            GetValue(values, "OtherAccount"));
        if (!string.IsNullOrWhiteSpace(oppositeAccount))
        {
            var accountToken = ExtractAgriculturalPaperAccountToken(oppositeAccount);
            Set(
                target,
                nameof(FlowRecord.OppositeAccount),
                string.IsNullOrWhiteSpace(accountToken)
                    ? NormalizeSingleLinePrintText(oppositeAccount)
                    : accountToken);
        }
    }

    private static void ClearAgriculturalPaperNarrowDetailFields(object target)
    {
        foreach (var propertyName in new[]
                 {
                     nameof(FlowRecord.TradeExplain),
                     nameof(FlowRecord.Usage),
                     nameof(FlowRecord.MerchantName),
                     nameof(FlowRecord.OppositeBank),
                     "MerchantOrderNo",
                     "MerchantOrderNum",
                     "MerchantNo"
                 })
        {
            ClearStringPropertyIfLongPaperDetail(target, propertyName);
        }
    }

    private static void ClearAgriculturalPaperUnsafeLongTextFields(object target, params string[] unsafeTexts)
    {
        var normalizedUnsafeTexts = unsafeTexts
            .Select(NormalizeSingleLinePrintText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.PropertyType != typeof(string))
            {
                continue;
            }

            if (AgriculturalPaperWideDetailPropertyNames.Contains(property.Name))
            {
                continue;
            }

            if (string.Equals(property.Name, nameof(FlowRecord.Remark), StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(property.Name, nameof(FlowRecord.OppositeUsername), StringComparison.Ordinal)
                || string.Equals(property.Name, nameof(FlowRecord.OppositeAccount), StringComparison.Ordinal))
            {
                continue;
            }

            var text = property.GetValue(target) as string;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalizedText = NormalizeSingleLinePrintText(text);
            if (normalizedUnsafeTexts.Any(unsafeText => string.Equals(normalizedText, unsafeText, StringComparison.Ordinal))
                || LooksLikeLongPaperDetail(normalizedText))
            {
                property.SetValue(target, string.Empty);
            }
        }
    }

    private static bool LooksLikeLongPaperDetail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalizedText = NormalizeSingleLinePrintText(text);
        if (normalizedText.Length < 20)
        {
            return false;
        }

        var consecutiveLettersOrDigits = 0;
        foreach (var character in normalizedText)
        {
            if (char.IsLetterOrDigit(character))
            {
                consecutiveLettersOrDigits++;
                if (consecutiveLettersOrDigits >= 12)
                {
                    return true;
                }
            }
            else
            {
                consecutiveLettersOrDigits = 0;
            }
        }

        return false;
    }

    private static void ClearStringPropertyIfLongPaperDetail(object target, string propertyName)
    {
        var current = NormalizeSingleLinePrintText(ReadStringProperty(target, propertyName, string.Empty));
        if (LooksLikeLongPaperDetail(current))
        {
            Set(target, propertyName, string.Empty);
        }
    }

    private static string ReadStringProperty(object target, string propertyName, string fallback)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        var value = property?.GetValue(target);
        var text = Convert.ToString(value, CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static bool IsAgriculturalBankPersonalPaperTemplate(PrintRenderContext context)
    {
        var templateName = context.Template.Name ?? string.Empty;
        var bankName = context.Bank.Name ?? string.Empty;
        var isAgriculturalBank = context.Bank.Id == 11
            || context.Template.BankId == 11
            || context.Template.VendorBankId == 55
            || bankName.Contains("\u519C\u884C", StringComparison.Ordinal)
            || templateName.Contains("\u519C\u884C", StringComparison.Ordinal);
        var isPersonalPaper = templateName.Contains("\u4E2A\u4EBA", StringComparison.Ordinal)
            && templateName.Contains("\u7EB8\u8D28\u7248", StringComparison.Ordinal);

        return isAgriculturalBank && isPersonalPaper;
    }

    private static bool IsPaperPrintTemplate(PrintRenderContext context)
    {
        return (context.Template.Name ?? string.Empty).Contains("\u7EB8\u8D28\u7248", StringComparison.Ordinal);
    }

    private static void TrimTextProperty(object target, string propertyName, int maxLength)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.CanRead || !property.CanWrite || property.PropertyType != typeof(string))
        {
            return;
        }

        var text = property.GetValue(target) as string;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var limitedText = LimitSingleLinePrintText(text, maxLength);
        if (!string.Equals(text, limitedText, StringComparison.Ordinal))
        {
            property.SetValue(target, limitedText);
        }
    }

    private static void TrimAllStringProperties(object target, int defaultMaxLength, IReadOnlyDictionary<string, int> propertyMaxLengths)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.PropertyType != typeof(string))
            {
                continue;
            }

            if (property.Name.Contains("Date", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Time", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = property.GetValue(target) as string;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var maxLength = propertyMaxLengths.TryGetValue(property.Name, out var configuredMaxLength)
                ? configuredMaxLength
                : defaultMaxLength;
            var limitedText = LimitSingleLinePrintText(text, maxLength);
            if (!string.Equals(text, limitedText, StringComparison.Ordinal))
            {
                property.SetValue(target, limitedText);
            }
        }
    }

    private static string InsertSoftLineBreaks(string text, int runLength)
    {
        if (runLength <= 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = text.Replace("\u200B", string.Empty, StringComparison.Ordinal);
        var builder = new StringBuilder(text.Length + (text.Length / runLength));
        var run = 0;
        foreach (var character in text)
        {
            builder.Append(character);

            if (char.IsWhiteSpace(character)
                || char.IsPunctuation(character)
                || char.IsSymbol(character))
            {
                run = 0;
                continue;
            }

            run++;
            if (run >= runLength)
            {
                builder.Append('\u200B');
                run = 0;
            }
        }

        return builder.ToString();
    }

    private static string InsertHardLineBreaks(string text, int runLength)
    {
        if (runLength <= 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var normalized = text.Replace("\u200B", string.Empty, StringComparison.Ordinal);
        var builder = new StringBuilder(normalized.Length + (normalized.Length / runLength));
        var run = 0;
        foreach (var character in normalized)
        {
            builder.Append(character);

            if (char.IsWhiteSpace(character)
                || char.IsPunctuation(character)
                || char.IsSymbol(character))
            {
                run = 0;
                continue;
            }

            run++;
            if (run >= runLength)
            {
                builder.Append('\n');
                run = 0;
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string LimitSingleLinePrintText(string text, int maxLength)
    {
        var normalizedText = NormalizeSingleLinePrintText(text);
        if (maxLength <= 0 || normalizedText.Length <= maxLength)
        {
            return normalizedText;
        }

        return normalizedText[..maxLength];
    }

    private static string NormalizeSingleLinePrintText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        foreach (var character in normalized)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                continue;
            }

            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static string GetFlowExtraFieldValue(Bank bank, FlowRecord source, IReadOnlyDictionary<string, object?> values, params string[] columnNames)
    {
        foreach (var columnName in columnNames)
        {
            if (values.TryGetValue(columnName, out var directValue))
            {
                var directText = Convert.ToString(directValue, CultureInfo.CurrentCulture);
                if (!string.IsNullOrWhiteSpace(directText))
                {
                    return directText;
                }
            }

            if (TryGetExtraFieldValue(source, columnName, out var extraValue))
            {
                return extraValue;
            }
        }

        if (bank.FlowColumns.Count == 0 || source.ExtraFields.Count == 0)
        {
            return string.Empty;
        }

        var wantedNames = columnNames.ToHashSet(StringComparer.Ordinal);
        var flowColumnIndex = 0;
        foreach (var column in bank.FlowColumns)
        {
            var columnName = column.Name ?? string.Empty;
            if (string.Equals(columnName, "ID", StringComparison.Ordinal)
                || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.Ordinal))
            {
                continue;
            }

            if (wantedNames.Contains(columnName))
            {
                var legacyKey = CreateLegacyFlowRecordExtraFieldPath(bank.Name, columnName, flowColumnIndex);
                if (TryGetExtraFieldValue(source, legacyKey, out var value))
                {
                    return value;
                }
            }

            flowColumnIndex++;
        }

        return string.Empty;
    }

    private static void SetValueIfBlank(Dictionary<string, object?> values, string key, object? value)
    {
        if (IsValuePresent(values, key))
        {
            return;
        }

        var text = Convert.ToString(value, CultureInfo.CurrentCulture);
        if (!string.IsNullOrWhiteSpace(text))
        {
            values[key] = value;
        }
    }

    private static bool IsValuePresent(IReadOnlyDictionary<string, object?> values, string key)
    {
        return values.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.CurrentCulture));
    }

    private static string CreateLegacyFlowRecordExtraFieldPath(string bankName, string columnName, int columnIndex)
    {
        var raw = $"{bankName}|FlowRecord|{columnIndex}|{columnName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"FlowField_{Convert.ToHexString(hash)[..12]}";
    }

    private static bool TryGetExtraFieldValue(FlowRecord source, string key, out string value)
    {
        if (source.ExtraFields.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var bracketedKey = key.StartsWith("[", StringComparison.Ordinal) ? key : $"[{key}]";
        if (!string.Equals(bracketedKey, key, StringComparison.Ordinal)
            && source.ExtraFields.TryGetValue(bracketedKey, out value!)
            && !string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var unbracketedKey = key.Trim('[', ']');
        if (!string.Equals(unbracketedKey, key, StringComparison.Ordinal)
            && source.ExtraFields.TryGetValue(unbracketedKey, out value!)
            && !string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static void Set(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        Set(target, property, value);
    }

    private static void SetTargetValueIfBlank(object target, string propertyName, object? value)
    {
        if (string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.CurrentCulture)))
        {
            return;
        }

        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        var current = Convert.ToString(property.GetValue(target), CultureInfo.CurrentCulture);
        if (!string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        Set(target, property, value);
    }

    private static void ApplyLocalPdfConfigToVendorConfig(object? vendorConfig, PrintPdfConfig? localConfig, int pageRows)
    {
        if (vendorConfig is null || localConfig is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(localConfig.Name))
        {
            Set(vendorConfig, "Name", localConfig.Name);
        }

        var rowCount = localConfig.RowCount > 0 ? localConfig.RowCount : pageRows;
        if (rowCount > 0)
        {
            Set(vendorConfig, "RowCount", rowCount);
        }

        Set(vendorConfig, "MarginLeft", localConfig.MarginLeft);
        Set(vendorConfig, "MarginTop", localConfig.MarginTop);
        Set(vendorConfig, "MarginRight", localConfig.MarginRight);
        Set(vendorConfig, "MarginBottom", localConfig.MarginBottom);
        Set(vendorConfig, "FontFamily", localConfig.FontFamily);
        Set(vendorConfig, "TabSize", localConfig.TabSize);
        Set(vendorConfig, "ColumnMinHeight", localConfig.ColumnMinHeight);
        Set(vendorConfig, "FirstPageOffset", localConfig.FirstPageOffset);
        Set(vendorConfig, "Desc", localConfig.Descending);
        Set(vendorConfig, "ZhangLeft", localConfig.SealLeft);
        Set(vendorConfig, "ZhangTop", localConfig.SealTop);
        Set(vendorConfig, "ZhangRight", localConfig.SealRight);
        Set(vendorConfig, "ZhangBottom", localConfig.SealBottom);
        Set(vendorConfig, "ZhangSize", localConfig.SealWidth);
        if (localConfig.Columns.Count > 0)
        {
            ApplyLocalPdfColumns(vendorConfig, localConfig.Columns);
        }
    }

    private static void ApplyLocalPdfColumns(object vendorConfig, IReadOnlyList<PrintPdfColumn> localColumns)
    {
        var property = vendorConfig.GetType().GetProperty("PdfColumns", BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetValue(vendorConfig) is not IList vendorColumns || vendorColumns.Count == 0)
        {
            return;
        }

        var usedIndexes = new HashSet<int>();
        for (var index = 0; index < localColumns.Count; index++)
        {
            var localColumn = localColumns[index];
            var targetIndex = FindVendorPdfColumnIndex(vendorColumns, localColumn.Name, usedIndexes);
            if (targetIndex < 0 && index < vendorColumns.Count && !usedIndexes.Contains(index))
            {
                targetIndex = index;
            }

            if (targetIndex < 0 || vendorColumns[targetIndex] is not { } vendorColumn)
            {
                continue;
            }

            usedIndexes.Add(targetIndex);
            Set(vendorColumn, "Name", localColumn.Name);
            Set(vendorColumn, "ColumnWidth", localColumn.Width);
            Set(vendorColumn, "FontSize", localColumn.FontSize);
            Set(vendorColumn, "FontFamily", localColumn.FontFamily);
            Set(vendorColumn, "LineHeight", localColumn.LineHeight);
        }
    }

    private static int FindVendorPdfColumnIndex(IList vendorColumns, string name, ISet<int> usedIndexes)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }

        for (var index = 0; index < vendorColumns.Count; index++)
        {
            if (usedIndexes.Contains(index) || vendorColumns[index] is not { } item)
            {
                continue;
            }

            var vendorName = Convert.ToString(GetPropertyValue(item, "Name"), CultureInfo.CurrentCulture);
            if (string.Equals(vendorName, name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static object? GetPropertyValue(object target, string propertyName)
    {
        return target.GetType()
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(target);
    }

    private static void PrimeVendorDynamicImageCache(Assembly assembly, string vendorDir)
    {
        try
        {
            var types = GetLoadableTypes(assembly).ToList();
            var dynamicImageTypes = types.Where(type =>
                type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Any(field => field.FieldType == typeof(byte[]))
                && type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Any(field => field.FieldType == typeof(byte[][]))
                && type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Any(constructor =>
                {
                    var parameters = constructor.GetParameters();
                    return parameters.Length >= 1
                        && string.Equals(parameters[0].ParameterType.FullName, "MainEntry.entity.PdfConfig.PDFConfig", StringComparison.Ordinal);
                }))
                .ToList();
            if (IsPrintBridgeDebugEnabledGlobal())
            {
                Console.WriteLine($"[PrintBridge] dynamic image candidates: {dynamicImageTypes.Count}");
            }

            if (dynamicImageTypes.Count == 0)
            {
                return;
            }

            var resourceName = ResolveVendorString(assembly, -659841973);
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                resourceName = "alipay.png";
            }

            var imagePath = ResolveVendorImagePath(vendorDir, resourceName);
            if (IsPrintBridgeDebugEnabledGlobal())
            {
                Console.WriteLine($"[PrintBridge] dynamic image resource: {resourceName}; path={imagePath}; exists={File.Exists(imagePath)}");
            }

            if (!File.Exists(imagePath))
            {
                return;
            }

            var imageBytes = File.ReadAllBytes(imagePath);
            var slices = CreateVendorImageSlices(imageBytes, 5);
            if (IsPrintBridgeDebugEnabledGlobal())
            {
                Console.WriteLine($"[PrintBridge] dynamic image bytes={imageBytes.Length}; slices={(slices is null ? "<null>" : string.Join(",", slices.Select(slice => slice.Length.ToString(CultureInfo.InvariantCulture))))}");
            }

            if (slices is null)
            {
                return;
            }

            foreach (var dynamicImageType in dynamicImageTypes)
            {
                var sourceField = dynamicImageType
                    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(field => field.FieldType == typeof(byte[]));
                var sliceField = dynamicImageType
                    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(field => field.FieldType == typeof(byte[][]));
                if (sourceField is null || sliceField is null)
                {
                    continue;
                }

                if (sliceField.GetValue(null) is byte[][] cachedSlices
                    && cachedSlices.Length >= 5
                    && cachedSlices.Take(5).All(slice => slice is { Length: > 0 }))
                {
                    continue;
                }

                sourceField.SetValue(null, imageBytes);
                sliceField.SetValue(null, slices);

                if (IsPrintBridgeDebugEnabledGlobal())
                {
                    var sourceLength = sourceField.GetValue(null) is byte[] sourceBytes ? sourceBytes.Length.ToString(CultureInfo.InvariantCulture) : "<null>";
                    var sliceLengths = sliceField.GetValue(null) is byte[][] sliceBytes
                        ? string.Join(",", sliceBytes.Select(slice => slice?.Length.ToString(CultureInfo.InvariantCulture) ?? "<null>"))
                        : "<null>";
                    Console.WriteLine($"[PrintBridge] primed dynamic image: {dynamicImageType.FullName}; source={sourceLength}; slices={sliceLengths}");
                }
            }
        }
        catch
        {
            if (IsPrintBridgeDebugEnabledGlobal())
            {
                throw;
            }

            // This cache only supports a subset of vendor QuestPDF templates.
            // A failed warm-up should not block templates that do not use it.
        }
    }

    private static string? ResolveVendorString(Assembly assembly, int key)
    {
        try
        {
            var method = GetLoadableTypes(assembly)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                .FirstOrDefault(method =>
                    method.ReturnType == typeof(string)
                    && method.GetParameters() is [{ ParameterType: var parameterType }]
                    && parameterType == typeof(int));
            return method?.Invoke(null, [key]) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveVendorImagePath(string vendorDir, string resourceName)
    {
        var normalizedName = resourceName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var candidates = new[]
        {
            Path.Combine(vendorDir, normalizedName),
            Path.Combine(vendorDir, "static", "bank", normalizedName),
            Path.Combine(vendorDir, "alipay.png"),
            Path.Combine(vendorDir, "static", "bank", "alipay.png")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
    }

    private static byte[][]? CreateVendorImageSlices(byte[] imageBytes, int count)
    {
        if (count <= 0)
        {
            return null;
        }

        try
        {
            var slices = new byte[count][];
            using var stream = new MemoryStream(imageBytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            {
                return null;
            }

            for (var index = 0; index < count; index++)
            {
                var left = frame.PixelWidth * index / count;
                var right = frame.PixelWidth * (index + 1) / count;
                var width = Math.Max(1, right - left);
                var crop = new CroppedBitmap(frame, new Int32Rect(left, 0, width, frame.PixelHeight));
                crop.Freeze();

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(crop));
                using var output = new MemoryStream();
                encoder.Save(output);
                slices[index] = output.ToArray();
            }

            return slices;
        }
        catch
        {
            return null;
        }
    }

    private static void Set(object target, PropertyInfo property, object? value)
    {
        try
        {
            var converted = ConvertValue(value, property.PropertyType);
            property.SetValue(target, converted);
        }
        catch
        {
            // Vendor models are intentionally populated best-effort because bank
            // templates differ. Missing or incompatible optional fields are ignored.
        }
    }

    private static object? ConvertValue(object? value, Type propertyType)
    {
        var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (value is null)
        {
            return Nullable.GetUnderlyingType(propertyType) is null && propertyType.IsValueType
                ? Activator.CreateInstance(propertyType)
                : null;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return Nullable.GetUnderlyingType(propertyType) is null && propertyType.IsValueType
                ? Activator.CreateInstance(propertyType)
                : null;
        }

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (targetType == typeof(DateTime))
        {
            if (value is DateTime dateTime)
            {
                return dateTime;
            }

            return TryParseDateTime(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
                ? parsed
                : null;
        }

        if (targetType == typeof(bool))
        {
            if (value is bool boolean)
            {
                return boolean;
            }

            var booleanText = Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.Equals(booleanText, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(booleanText, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(booleanText, "\u662F", StringComparison.OrdinalIgnoreCase);
        }

        if (targetType == typeof(double))
        {
            return ParseNullableDouble(value) ?? 0d;
        }

        if (targetType == typeof(decimal))
        {
            return ParseNullableDecimal(value) ?? 0m;
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static double? ParseNullableDouble(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is double number)
        {
            return number;
        }

        if (value is decimal decimalNumber)
        {
            return (double)decimalNumber;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace(",", string.Empty);
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            || double.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("zh-CN"), out parsed)
            ? parsed
            : null;
    }

    private static decimal? ParseNullableDecimal(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is decimal number)
        {
            return number;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace(",", string.Empty);
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            || decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("zh-CN"), out parsed)
            ? parsed
            : null;
    }

    private static bool TryParseDateTime(string? value, out DateTime parsed)
    {
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd H:mm:ss",
            "yyyy/M/d HH:mm:ss",
            "yyyy/M/d H:mm:ss",
            "yyyy/M/d",
            "yyyy\u5E74MM\u6708dd\u65E5 HH:mm:ss",
            "yyyy\u5E74M\u6708d\u65E5 H:mm:ss"
        };

        return DateTime.TryParseExact(value, formats, culture, DateTimeStyles.None, out parsed)
            || DateTime.TryParse(value, culture, DateTimeStyles.None, out parsed)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
    }

    private static bool IsPrintBridgeDebugEnabledGlobal()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("SPEEDEMULATOR_PRINT_BRIDGE_DEBUG"),
            "1",
            StringComparison.Ordinal);
    }

    private static void NormalizeVendorDynamicImageDocument(PrintRenderContext context, object document)
    {
        if (!IsAlipayPrintContext(context))
        {
            return;
        }

        try
        {
            foreach (var field in document.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType != typeof(int) || field.IsInitOnly)
                {
                    continue;
                }

                var value = (int)(field.GetValue(document) ?? 0);
                if (IsPrintBridgeDebugEnabledGlobal())
                {
                    Console.WriteLine($"[PrintBridge] document int field: {document.GetType().FullName}.{field.Name}={value}");
                }

                if (value <= 0 || value % 5 == 0)
                {
                    continue;
                }

                var normalizedValue = ((value + 4) / 5) * 5;
                field.SetValue(document, normalizedValue);
                if (IsPrintBridgeDebugEnabledGlobal())
                {
                    Console.WriteLine($"[PrintBridge] normalized dynamic image page count: {field.Name} {value}->{normalizedValue}");
                }
            }
        }
        catch
        {
            if (IsPrintBridgeDebugEnabledGlobal())
            {
                throw;
            }

            // Only used for vendor templates that paint a sliced background.
            // If reflection cannot adjust it, let the normal renderer surface
            // the original template error.
        }
    }

    private static bool IsAlipayPrintContext(PrintRenderContext context)
    {
        return context.Bank.Name.Contains("支付宝", StringComparison.Ordinal)
            || context.Template.Name.Contains("支付宝", StringComparison.Ordinal);
    }

    private static IEnumerable<string> GetCandidateTemplateNames(string name)
    {
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in BuildCandidateTemplateNames(name))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && yielded.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool HasPreferredTemplateAlias(string name)
    {
        return GetPreferredTemplateAliases(name).Any();
    }

    private static bool ShouldPreferQuestPdfConfigFactory(string name)
    {
        return HasPreferredTemplateAlias(name);
    }

        private static bool CanUseQuestPdfFallback(PrintTemplate template)
        {
            return IsEditableQuestPdfTemplate(template)
                || (!template.IsSystem
                    && (PrintTemplateQuestPdfConversionService.HasQuestPdfLayout(template)
                        || template.Config.Columns.Count > 0));
        }

        private static bool ShouldForceVendorRenderer(PrintRenderContext context)
        {
            return context.Template.IsSystem
                || context.Template.VendorId > 0
                || context.Template.VendorBankId > 0
                || !string.IsNullOrWhiteSpace(context.Template.PdfData)
                || IsAgriculturalBankPersonalPaperTemplate(context);
        }

        private static bool ShouldUseEditableQuestPdfRenderer(PrintTemplate template)
        {
            return IsEditableQuestPdfTemplate(template)
                || (!template.IsSystem
                    && PrintTemplateQuestPdfConversionService.HasQuestPdfLayout(template));
        }

        private static bool IsEditableQuestPdfTemplate(PrintTemplate template)
        {
            return !template.IsSystem
                && template.Id > 0
                && template.VendorId <= 0
                && string.IsNullOrWhiteSpace(template.PdfData)
                && (PrintTemplateQuestPdfConversionService.HasQuestPdfLayout(template)
                    || template.Config.Columns.Count > 0);
        }

    private static IEnumerable<string> BuildCandidateTemplateNames(string name)
    {
        if (TryRemoveDerivedTemplateSuffix(name, out var derivedSourceName))
        {
            foreach (var candidate in BuildCandidateTemplateNames(derivedSourceName))
            {
                yield return candidate;
            }

            yield return derivedSourceName;
        }

        if (TryRemoveImportedTemplateSuffix(name, out var importedSourceName))
        {
            foreach (var alias in GetPreferredTemplateAliases(importedSourceName))
            {
                yield return alias;
            }

            yield return importedSourceName;
        }

        foreach (var preferredName in GetPreferredTemplateAliases(name))
        {
            yield return preferredName;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            yield return name;
        }

        var latestSuffixes = new[] { "\uFF08\u6700\u65B0\u7248\uFF09", "\uFF08\u6700\u65B0\uFF09", "(\u6700\u65B0\u7248)", "(\u6700\u65B0)" };
        foreach (var latestSuffix in latestSuffixes)
        {
            if (name.Contains(latestSuffix, StringComparison.Ordinal))
            {
                var baseName = name.Replace(latestSuffix, string.Empty, StringComparison.Ordinal);
                yield return baseName + "2";
                yield return baseName;
            }
        }

        var electronicSuffix = "\u7535\u5B50\u7248";
        if (name.EndsWith(electronicSuffix, StringComparison.Ordinal))
        {
            yield return name + "2";
        }

        var paperSuffix = "\u7EB8\u8D28\u7248";
        if (name.EndsWith(paperSuffix, StringComparison.Ordinal))
        {
            yield return name + "2";
        }

        var doubleDashIndex = name.IndexOf("--", StringComparison.Ordinal);
        if (doubleDashIndex > 0)
        {
            yield return name[..doubleDashIndex];
            if (name.StartsWith("\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248", StringComparison.Ordinal))
            {
                yield return "\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--\u5317\u4EAC3";
                yield return "\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--\u5317\u4EAC2";
                yield return "\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--\u5317\u4EAC";
            }
        }

        var dashIndex = name.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex > 0)
        {
            yield return name[..dashIndex];
            if (name.StartsWith("\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248", StringComparison.Ordinal))
            {
                yield return "\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--\u5317\u4EAC3";
                yield return "\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--\u5317\u4EAC2";
                yield return "\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--\u5317\u4EAC";
            }
        }

        foreach (var withoutDuplicateSuffix in TrimTrailingNumericDuplicateSuffixes(name))
        {
            yield return withoutDuplicateSuffix;
        }
    }

    private static bool TryRemoveDerivedTemplateSuffix(string name, out string sourceName)
    {
        sourceName = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var suffixes = new[] { "-复制", "_复制", " 复制", "-改", "_改", " 改" };
        foreach (var suffix in suffixes)
        {
            var suffixIndex = name.LastIndexOf(suffix, StringComparison.Ordinal);
            if (suffixIndex <= 0)
            {
                continue;
            }

            var remainder = name[(suffixIndex + suffix.Length)..];
            if (remainder.Length > 0 && remainder.Any(item => !char.IsDigit(item)))
            {
                continue;
            }

            sourceName = name[..suffixIndex];
            return !string.IsNullOrWhiteSpace(sourceName);
        }

        return false;
    }

    private static bool TryRemoveImportedTemplateSuffix(string name, out string sourceName)
    {
        const string suffix = "-导入";
        sourceName = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var suffixIndex = name.LastIndexOf(suffix, StringComparison.Ordinal);
        if (suffixIndex <= 0)
        {
            return false;
        }

        var remainder = name[(suffixIndex + suffix.Length)..];
        if (remainder.Length > 0 && remainder.Any(item => !char.IsDigit(item)))
        {
            return false;
        }

        sourceName = name[..suffixIndex];
        return !string.IsNullOrWhiteSpace(sourceName);
    }

    private static IEnumerable<string> TrimTrailingNumericDuplicateSuffixes(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !char.IsDigit(name[^1]))
        {
            yield break;
        }

        var current = name;
        while (current.Length > 1 && char.IsDigit(current[^1]))
        {
            current = current[..^1];
            if (!string.IsNullOrWhiteSpace(current))
            {
                foreach (var alias in GetPreferredTemplateAliases(current))
                {
                    yield return alias;
                }

                yield return current;
            }
        }
    }

    private static IEnumerable<string> GetPreferredTemplateAliases(string name)
    {
        var exactAliases = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["\u5E73\u5B89\u4E2A\u4EBA\u7535\u5B50\u7248"] = ["\u5E73\u5B89\u4E2A\u4EBA\u7535\u5B50\u72482"],
            ["\u5E73\u5B89\u4E2A\u4EBA\u7535\u5B50\u72483"] = ["\u5E73\u5B89\u4E2A\u4EBA\u7535\u5B50\u72482", "\u5E73\u5B89\u4E2A\u4EBA\u7535\u5B50\u7248"],
            ["\u62DB\u884C\u4E2A\u4EBA\u7535\u5B50\u72486"] = ["\u62DB\u884C\u4E2A\u4EBA\u7535\u5B50\u72487", "\u62DB\u884C\u4E2A\u4EBA\u7535\u5B50\u72485"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u7248"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72482"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u724813_\u6C34\u53702"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u724812_\u6C34\u53702"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u724813_\u6C34\u5370"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u724812_\u6C34\u5370"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u724813"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u724814", "\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u724812"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72488_\u6C34\u53702"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72487_\u6C34\u53702"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72488_\u6C34\u5370"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72487_\u6C34\u5370"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72488"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72489", "\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72487"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72486_\u6C34\u53702"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72487_\u6C34\u53702"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72486_\u6C34\u5370"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72487_\u6C34\u5370"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72486"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72487"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72485_\u6C34\u5370"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72484_\u6C34\u5370"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72485"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7535\u5B50\u72484"],
            ["\u5174\u4E1A\u4E2A\u4EBA\u7EB8\u8D28\u72482"] = ["\u5174\u4E1A\u4E2A\u4EBA\u7EB8\u8D28\u72483", "\u5174\u4E1A\u4E2A\u4EBA\u7EB8\u8D28\u7248"],
            ["\u90AE\u653F\u5BF9\u516C\u7535\u5B50\u72482"] = ["\u90AE\u653F\u5BF9\u516C\u7EB8\u8D28\u7248"],
            ["\u90AE\u653F\u5BF9\u516C\u7535\u5B50\u7248"] = ["\u90AE\u653F\u5BF9\u516C\u7EB8\u8D28\u7248"],
            ["\u519C\u884C\u5BF9\u516C\u7535\u5B50\u72484"] = ["\u519C\u884C\u5BF9\u516C\u7EB8\u8D28\u7248"],
            ["\u519C\u884C\u5BF9\u516C\u7535\u5B50\u72483"] = ["\u519C\u884C\u5BF9\u516C\u7EB8\u8D28\u7248"],
            ["\u519C\u884C\u5BF9\u516C\u7535\u5B50\u72482"] = ["\u519C\u884C\u5BF9\u516C\u7EB8\u8D28\u7248"],
            ["\u519C\u884C\u5BF9\u516C\u7535\u5B50\u7248"] = ["\u519C\u884C\u5BF9\u516C\u7EB8\u8D28\u7248"],
            ["\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\u7248\uFF09"] = ["\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\uFF09", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\u7248\uFF09", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u72482", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248"],
            ["\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\uFF09"] = ["\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\uFF09", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\u7248\uFF09", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u72482", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248"],
            ["\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248(\u6700\u65B0\u7248)"] = ["\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\uFF09", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\u7248\uFF09", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u72482", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248"],
            ["\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248(\u6700\u65B0)"] = ["\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\uFF09", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248\uFF08\u6700\u65B0\u7248\uFF09", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u72482", "\u519C\u884C\u4E2A\u4EBA\u7535\u5B50\u7248"],
            ["\u846B\u82A6\u5C9B"] = ["\u5B89\u5FBD\u519C\u91D1"],
        };

        if (exactAliases.TryGetValue(name, out var aliases))
        {
            foreach (var alias in aliases)
            {
                yield return alias;
            }
        }

        if (name.StartsWith("\u5EFA\u884C\u4E2A\u4EBA\u7535\u5B50\u7248", StringComparison.Ordinal)
            && TryReadTrailingNumber(name, "\u5EFA\u884C\u4E2A\u4EBA\u7535\u5B50\u7248", out var ccbVersion)
            && (ccbVersion is >= 23 and <= 27 || ccbVersion is >= 31 and <= 36))
        {
            yield return ccbVersion >= 31
                ? "\u5EFA\u884C\u4E2A\u4EBA\u7535\u5B50\u724830"
                : "\u5EFA\u884C\u4E2A\u4EBA\u7535\u5B50\u724822";
        }

        if (name.StartsWith("\u519C\u884C\u5BF9\u516C\u7535\u5B50\u7248", StringComparison.Ordinal)
            && !name.Contains("\u6700\u65B0", StringComparison.Ordinal)
            && !name.Contains("\u65B0", StringComparison.Ordinal))
        {
            yield return "\u519C\u884C\u5BF9\u516C\u7EB8\u8D28\u7248";
            yield return "\u519C\u884C\u5BF9\u516C\u7535\u5B50\u7248\uFF08\u65B0\uFF09";
            yield return "\u519C\u884C\u5BF9\u516C\u7535\u5B50\u7248\uFF08\u6700\u65B0\uFF09";
        }

        if (name.StartsWith("\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248-", StringComparison.Ordinal)
            || name.StartsWith("\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--", StringComparison.Ordinal))
        {
            yield return "\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--\u5317\u4EAC3";
            yield return "\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--\u5317\u4EAC2";
            yield return "\u519C\u5546\u94F6\u884C\u5BF9\u516C\u7248--\u5317\u4EAC";
        }
    }

    private static bool TryReadTrailingNumber(string text, string prefix, out int number)
    {
        number = 0;
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = text[prefix.Length..];
        return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private static string NormalizeFieldName(string field)
    {
        var result = field.Trim();
        return result.Length >= 2 && result[0] == '[' && result[^1] == ']'
            ? result[1..^1]
            : result;
    }

    private static string? GetValue(IReadOnlyDictionary<string, object?> values, string key)
    {
        return values.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.CurrentCulture) : null;
    }

    private static string ResolveBankUserNumber(PrintRenderContext context, IReadOnlyDictionary<string, object?> values)
    {
        return FirstNotBlank(
            GetValue(values, "UserNum"),
            context.BankUser.UserCode,
            GetValue(values, "CustomerNo"),
            GetValue(values, "SerialNum"),
            GetValue(values, "PrintNo"));
    }

    private static string ResolveAgriculturalPersonalPaperSequence(PrintRenderContext context, IReadOnlyDictionary<string, object?> values)
    {
        var configuredSequence = GetBankUserColumnValue(
            context,
            values,
            "\u8D26\u6237\u5E8F\u53F7",
            "\u8D26\u53F7\u5E8F\u53F7",
            "\u5E8F\u53F7");

        return NormalizeShortPrintSequence(FirstNotBlank(
            configuredSequence,
            GetValue(values, "PrintNo"),
            GetValue(values, "CustomerNo"),
            GetValue(values, "UserNum")));
    }

    private static string GetBankUserColumnValue(PrintRenderContext context, IReadOnlyDictionary<string, object?> values, params string[] columnNames)
    {
        foreach (var columnName in columnNames)
        {
            var column = context.Bank.Columns.FirstOrDefault(item => string.Equals(item.Name, columnName, StringComparison.Ordinal));
            if (column is null || string.IsNullOrWhiteSpace(column.Field))
            {
                continue;
            }

            var field = NormalizeFieldName(column.Field);
            if (values.TryGetValue(field, out var value))
            {
                var text = Convert.ToString(value, CultureInfo.CurrentCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (context.BankUser.ExtraFields.TryGetValue(column.Field, out var extraValue)
                || context.BankUser.ExtraFields.TryGetValue(field, out extraValue))
            {
                if (!string.IsNullOrWhiteSpace(extraValue))
                {
                    return extraValue;
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeShortPrintSequence(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "000";
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits))
        {
            return "000";
        }

        return digits.Length >= 3 ? digits[^3..] : digits.PadLeft(3, '0');
    }

    private static string FirstNotBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string NormalizePrintNumber(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "6228480398401725673" : value.Trim();
        if (text.Length >= 18)
        {
            return text;
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length >= 18)
        {
            return digits;
        }

        var seed = string.IsNullOrWhiteSpace(digits) ? "6228480398401725673" : digits;
        return (seed + "6228480398401725673")[..18];
    }

    private static string NormalizeCurrency(string value)
    {
        if (string.Equals(value, "RMB", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "CNY", StringComparison.OrdinalIgnoreCase))
        {
            return "\u4EBA\u6C11\u5E01";
        }

        return value;
    }
}
