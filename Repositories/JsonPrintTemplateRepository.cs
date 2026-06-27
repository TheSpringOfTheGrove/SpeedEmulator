using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

namespace SpeedEmulator.Repositories;

public sealed class JsonPrintTemplateRepository : IPrintTemplateRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object syncRoot = new();
    private readonly string storagePath;
    private Dictionary<long, List<PrintTemplate>> templatesByBank = [];
    private bool loaded;

    public JsonPrintTemplateRepository()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "print-templates.json");
    }

    public Task<IReadOnlyList<PrintTemplate>> ListByBankAsync(Bank bank)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            var templates = CreateSystemTemplates(bank);
            if (templatesByBank.TryGetValue(bank.Id, out var savedTemplates))
            {
                var deletedTemplates = savedTemplates
                    .Where(item => item.IsDeleted)
                    .Select(item => item.Clone())
                    .ToList();
                templates.AddRange(savedTemplates
                    .Where(item => !item.IsDeleted)
                    .Select(item => item.Clone()));
                if (deletedTemplates.Count > 0)
                {
                    templates = templates
                        .Where(item => !deletedTemplates.Any(deleted => IsDeletedTemplateMatch(item, deleted)))
                        .ToList();
                }
            }

            templates = DeduplicateDisplayTemplates(templates);
            return Task.FromResult<IReadOnlyList<PrintTemplate>>(templates);
        }
    }

    public Task SaveAsync(Bank bank, PrintTemplate template)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            if (!templatesByBank.TryGetValue(bank.Id, out var templates))
            {
                templates = [];
                templatesByBank[bank.Id] = templates;
            }

            var copy = template.Clone();
            copy.BankId = bank.Id;
            copy.IsSystem = false;
            copy.IsDeleted = false;
            templates.RemoveAll(item => item.IsDeleted && IsDeletedTemplateMatch(copy, item));

            var index = FindExistingTemplateIndex(templates, copy);
            if (index >= 0 && copy.Id <= 0)
            {
                copy.Id = templates[index].Id;
            }
            else if (index < 0 && copy.Id <= 0)
            {
                var maxId = templates
                    .Where(item => !item.IsDeleted && item.Id > 0)
                    .Select(item => item.Id)
                    .DefaultIfEmpty(0)
                    .Max();
                copy.Id = maxId + 1;
            }

            templates.RemoveAll(item =>
                !item.IsDeleted
                && item.Id != copy.Id
                && IsSameTemplateIdentity(item, copy));

            index = FindExistingTemplateIndex(templates, copy);
            if (index >= 0)
            {
                templates[index] = copy;
            }
            else
            {
                templates.Add(copy);
            }

            Persist();
            return Task.CompletedTask;
        }
    }

    public Task DeleteAsync(Bank bank, PrintTemplate template)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            if (!templatesByBank.TryGetValue(bank.Id, out var templates))
            {
                templates = [];
                templatesByBank[bank.Id] = templates;
            }

            var removedCount = templates.RemoveAll(item =>
                !item.IsSystem
                && !item.IsDeleted
                && item.Id == template.Id);
            if (removedCount == 0 && !template.IsSystem)
            {
                templates.RemoveAll(item => item.IsDeleted && IsDeletedTemplateMatch(template, item));
                var hidden = template.Clone();
                hidden.BankId = bank.Id;
                hidden.IsSystem = false;
                hidden.IsDeleted = true;
                hidden.PdfData = string.Empty;
                hidden.Config = new PrintPdfConfig();
                templates.Add(hidden);
            }

            Persist();
            return Task.CompletedTask;
        }
    }

    private static List<PrintTemplate> CreateSystemTemplates(Bank bank)
    {
        var definitions = ZhenchengTemplateCatalog.TryGetTemplateDefinitions(bank)?.ToList()
            ?? GetTemplateDefinitions(bank).ToList();
        definitions = DeduplicateTemplateDefinitions(definitions).ToList();
        var templates = new List<PrintTemplate>(definitions.Count);
        for (var index = 0; index < definitions.Count; index++)
        {
            templates.Add(CreateSystemTemplate(bank, definitions[index], index));
        }

        return templates;
    }

    private static IEnumerable<PrintTemplateDefinition> DeduplicateTemplateDefinitions(IEnumerable<PrintTemplateDefinition> definitions)
    {
        return definitions
            .GroupBy(
                item => $"{NormalizeTemplateKeyPart(item.Name)}\u001f{NormalizeTemplateKeyPart(item.Remark)}\u001f{item.PageRows}\u001f{item.Orientation}",
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.IsSystem)
                .ThenByDescending(item => item.VendorId > 0)
                .ThenByDescending(item => !string.IsNullOrWhiteSpace(item.PdfData))
                .First());
    }

    private static bool IsDeletedTemplateMatch(PrintTemplate template, PrintTemplate deleted)
    {
        if (template.IsSystem)
        {
            return false;
        }

        if (deleted.VendorId > 0 && template.VendorId == deleted.VendorId)
        {
            return true;
        }

        return template.Id == deleted.Id
            && string.Equals(NormalizeTemplateKeyPart(template.Name), NormalizeTemplateKeyPart(deleted.Name), StringComparison.Ordinal)
            && string.Equals(NormalizeTemplateKeyPart(template.Remark), NormalizeTemplateKeyPart(deleted.Remark), StringComparison.Ordinal)
            && template.PageRows == deleted.PageRows;
    }

    private static string NormalizeTemplateKeyPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
    }

    private static List<PrintTemplate> DeduplicateDisplayTemplates(IEnumerable<PrintTemplate> templates)
    {
        return templates
            .GroupBy(GetTemplateIdentityKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.Id > 0)
                .ThenByDescending(item => item.IsSystem)
                .First())
            .ToList();
    }

    private static int FindExistingTemplateIndex(List<PrintTemplate> templates, PrintTemplate template)
    {
        if (template.Id > 0)
        {
            var byId = templates.FindIndex(item => !item.IsDeleted && item.Id == template.Id);
            if (byId >= 0)
            {
                return byId;
            }
        }

        return templates.FindIndex(item => !item.IsDeleted && IsSameTemplateIdentity(item, template));
    }

    private static bool IsSameTemplateIdentity(PrintTemplate left, PrintTemplate right)
    {
        return string.Equals(GetTemplateIdentityKey(left), GetTemplateIdentityKey(right), StringComparison.Ordinal);
    }

    private static string GetTemplateIdentityKey(PrintTemplate template)
    {
        var vendorKey = template.VendorId > 0
            ? $"vendor:{template.VendorBankId}:{template.VendorId}"
            : "local";

        return string.Join(
            '\u001f',
            vendorKey,
            NormalizeTemplateKeyPart(template.Name),
            NormalizeTemplateKeyPart(template.Remark),
            template.PageRows.ToString());
    }

    private static PrintTemplate CreateSystemTemplate(Bank bank, PrintTemplateDefinition definition, int index)
    {
        var columns = CreateStatementColumns(bank).ToList();

        if (columns.Count == 0)
        {
            columns.Add(new PrintPdfColumn
            {
                Name = "交易日期",
                Field = nameof(FlowRecord.AccountTime),
                Type = "DateTime",
                Width = 74
            });
        }

        return new PrintTemplate
        {
            Id = -(index + 1),
            BankId = bank.Id,
            VendorId = definition.VendorId,
            VendorBankId = definition.VendorBankId,
            IsSystem = definition.IsSystem,
            Name = definition.Name,
            PageSize = definition.Orientation,
            PageRows = definition.PageRows,
            Remark = definition.Remark,
            PdfData = definition.PdfData,
            Config = new PrintPdfConfig
            {
                Name = definition.Name,
                Desc = definition.Description,
                RowCount = definition.PageRows <= 0 ? 28 : definition.PageRows,
                MarginLeft = definition.Orientation == "A4Portrait" ? 54 : 22,
                MarginTop = definition.Orientation == "A4Portrait" ? 54 : 22,
                MarginRight = definition.Orientation == "A4Portrait" ? 54 : 22,
                MarginBottom = definition.Orientation == "A4Portrait" ? 40 : 22,
                HeaderFontSize = definition.Orientation == "A4Portrait" ? 7 : 8,
                BodyFontSize = definition.Orientation == "A4Portrait" ? 5.2 : 6.2,
                ColumnMinHeight = definition.Orientation == "A4Portrait" ? 13 : 15,
                SealWidth = definition.Orientation == "A4Portrait" ? 86 : 104,
                Columns = columns
            }
        };
    }

    private static IEnumerable<PrintTemplateDefinition> GetTemplateDefinitions(Bank bank)
    {
        var name = bank.Name;
        var typeName = bank.GetBankType();

        if (name == "农行" && bank.Type == BankTypes.Personal)
        {
            yield return Template("农行个人电子版（最新版）", 0, string.Empty, "A4Portrait");
            yield return Template("农行个人电子版2", 0, string.Empty, "A4Portrait");
            yield return Template("农行个人电子版", 46, string.Empty, "A4Portrait");
            for (var version = 10; version >= 2; version--)
            {
                yield return Template($"农行个人纸质版{version}", version is 10 ? 0 : version is 9 ? 27 : version is 4 or 3 ? 30 : 15, VersionRemark(version), "A4Portrait");
            }

            yield return Template("农行个人纸质版", 15, string.Empty, "A4Portrait");
            yield return Template("农行个人苏州版", 0, "2025(苏州)", "A4Portrait");
            yield return Template("农行个人长沙版", 0, string.Empty, "A4Portrait");
            yield break;
        }

        if (bank.Type == BankTypes.Corporate)
        {
            yield return Template($"{name}电子版", 0, string.Empty, "A4Portrait");
            yield return Template($"{name}电子版2", 0, string.Empty, "A4Portrait");
            yield return Template($"{name}纸质版", 15, string.Empty, "A4Portrait");
            yield return Template($"{name}纸质版2", 15, string.Empty, "A4Portrait");
            yield break;
        }

        foreach (var item in GetPersonalTemplateNames(name))
        {
            yield return item;
        }

        static string VersionRemark(int version)
        {
            return version switch
            {
                10 => "2025(苏州)",
                9 => "2025",
                8 => "2025(6)",
                7 => "2025(5)",
                _ => string.Empty
            };
        }

        PrintTemplateDefinition Template(string templateName, int rows, string remark, string orientation)
        {
            return new PrintTemplateDefinition(
                templateName,
                rows,
                remark,
                orientation,
                $"{bank.Name}{typeName}系统打印模板");
        }

        IEnumerable<PrintTemplateDefinition> GetPersonalTemplateNames(string bankName)
        {
            var rows = bankName switch
            {
                "支付宝" or "微信" => 30,
                "中行" or "中信" or "工行" or "建行" => 27,
                _ => 15
            };

            if (bankName == "民生")
            {
                yield return Template("民生个人电子版", 0, string.Empty, "A4Portrait");
                yield return Template("民生个人电子版2", 0, string.Empty, "A4Portrait");
                yield return Template("民生个人纸质版", rows, string.Empty, "A4Portrait");
                yield return Template("民生个人纸质版2", rows, string.Empty, "A4Portrait");
                yield break;
            }

            if (bankName == "平安")
            {
                yield return Template("平安个人电子版", 0, string.Empty, "A4Portrait");
                yield return Template("平安个人纸质版1", rows, string.Empty, "A4Portrait");
                yield return Template("平安个人纸质版2", rows, string.Empty, "A4Portrait");
                yield break;
            }

            if (bankName == "交行")
            {
                yield return Template("交行个人电子版", 0, string.Empty, "A4Portrait");
                yield return Template("交行个人电子版3", 0, string.Empty, "A4Portrait");
                yield return Template("交行个人纸质版", rows, string.Empty, "A4Portrait");
                yield break;
            }

            if (bankName == "工行")
            {
                yield return Template("工行个人电子版", 0, string.Empty, "A4Portrait");
                yield return Template("工行个人电子版2", 0, string.Empty, "A4Portrait");
                yield return Template("工行个人纸质版", rows, string.Empty, "A4Portrait");
                yield return Template("工行个人纸质版2", rows, string.Empty, "A4Portrait");
                yield break;
            }

            yield return Template($"{bankName}个人电子版", 0, string.Empty, "A4Portrait");
            yield return Template($"{bankName}个人纸质版", rows, string.Empty, "A4Portrait");
            yield return Template($"{bankName}个人纸质版2", rows, string.Empty, "A4Portrait");
        }
    }

    private static PrintPdfColumn CreatePrintColumn(ColumnDefinition column)
    {
        var width = column.Width <= 0 ? 100 : column.Width;
        if (IsIdColumn(column))
        {
            width = Math.Clamp(width, 36, 52);
        }

        return new PrintPdfColumn
        {
            Name = column.Name ?? string.Empty,
            Field = column.Field ?? string.Empty,
            Type = column.Type ?? string.Empty,
            Width = Math.Clamp(width, 36, 180),
            LineHeight = 18,
            FontSize = 8
        };
    }

    private static IEnumerable<PrintPdfColumn> CreateStatementColumns(Bank bank)
    {
        var visibleColumns = bank.FlowColumns
            .Select((column, index) => new { Column = column, Index = index })
            .Where(item => item.Column.Show
                && !string.IsNullOrWhiteSpace(item.Column.Field)
                && !IsIdColumn(item.Column))
            .OrderBy(item => item.Column.Order)
            .ThenBy(item => item.Index)
            .ToList();

        var addedDateTime = false;
        foreach (var item in visibleColumns)
        {
            var column = item.Column;
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

            var printColumn = CreatePrintColumn(column);
            printColumn.Width = GetStatementColumnWidth(column, printColumn.Width);
            yield return printColumn;
        }
    }

    private static double GetStatementColumnWidth(ColumnDefinition column, double fallback)
    {
        return column.Field switch
        {
            nameof(FlowRecord.TradeMoney) => 58,
            nameof(FlowRecord.Balance) or nameof(FlowRecord.BalanceAmount) => 62,
            nameof(FlowRecord.LogNum) => 68,
            nameof(FlowRecord.TradeChannel) => 52,
            nameof(FlowRecord.Remark) => 130,
            nameof(FlowRecord.OppositeUsername) => 88,
            nameof(FlowRecord.OppositeAccount) => 88,
            nameof(FlowRecord.OppositeBank) => 88,
            _ => Math.Clamp(fallback, 44, 120)
        };
    }

    private static bool IsIdColumn(ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PrintTemplateDefinition(
        string Name,
        int PageRows,
        string Remark,
        string Orientation,
        string Description,
        string PdfData = "",
        bool IsSystem = true,
        long VendorId = 0,
        long VendorBankId = 0);

    private sealed class ZhenchengTemplateCatalog
    {
        private static readonly object CatalogSyncRoot = new();
        private static ZhenchengTemplateCatalog? current;
        private static bool attemptedLoad;

        private readonly Dictionary<long, List<VendorTemplateItem>> templatesByVendorBankId;

        private ZhenchengTemplateCatalog(Dictionary<long, List<VendorTemplateItem>> templatesByVendorBankId)
        {
            this.templatesByVendorBankId = templatesByVendorBankId;
        }

        public static IReadOnlyList<PrintTemplateDefinition>? TryGetTemplateDefinitions(Bank bank)
        {
            var catalog = GetCatalog();
            if (catalog is null)
            {
                return null;
            }

            var items = catalog.ResolveTemplates(bank);
            if (items.Count == 0)
            {
                return null;
            }

            return items
                .Select(item => new PrintTemplateDefinition(
                    item.Name,
                    item.PageRows,
                    item.Remark,
                    "A4Portrait",
                    $"{bank.Name}{bank.GetBankType()}系统打印模板",
                    item.PdfData,
                    item.IsSystem,
                    item.Id,
                    item.BankId))
                .ToList();
        }

        private static ZhenchengTemplateCatalog? GetCatalog()
        {
            lock (CatalogSyncRoot)
            {
                if (attemptedLoad)
                {
                    return current;
                }

                attemptedLoad = true;
                try
                {
                    current = LoadCatalog();
                }
                catch
                {
                    current = null;
                }

                return current;
            }
        }

        private static ZhenchengTemplateCatalog? LoadCatalog()
        {
            var vendorDir = ResolveVendorDir();
            if (vendorDir is null)
            {
                return null;
            }

            var mainDll = Path.Combine(vendorDir, ZhenchengRuntimeLocator.MainDllName);
            var previousDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(vendorDir);
            try
            {
                Environment.SetEnvironmentVariable(
                    "PATH",
                    vendorDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));

                var loadContext = new VendorLoadContext(mainDll);
                var assembly = loadContext.LoadFromAssemblyPath(mainDll);
                var pdfTemplateType = assembly.GetType("MainEntry.entity.PDFTemplate", throwOnError: true)!;
                var listPdfTemplateType = typeof(List<>).MakeGenericType(pdfTemplateType);
                var listMethod = GetLoadableTypes(assembly)
                    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    .FirstOrDefault(method =>
                    {
                        var parameters = method.GetParameters();
                        return parameters.Length == 1
                            && (parameters[0].ParameterType == typeof(long) || parameters[0].ParameterType == typeof(int))
                            && method.ReturnType == listPdfTemplateType;
                    });

                if (listMethod is null)
                {
                    return null;
                }

                var result = new Dictionary<long, List<VendorTemplateItem>>();
                var parameterType = listMethod.GetParameters()[0].ParameterType;
                for (var bankId = 1L; bankId <= 180L; bankId++)
                {
                    try
                    {
                        var argument = Convert.ChangeType(bankId, parameterType);
                        var value = InvokeSilently(() => listMethod.Invoke(null, [argument]));
                        if (value is not IEnumerable enumerable)
                        {
                            continue;
                        }

                        var items = enumerable
                            .Cast<object>()
                            .Select(item => ReadTemplate(item, bankId))
                            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                            .ToList();
                        if (items.Count > 0)
                        {
                            result[bankId] = items;
                        }
                    }
                    catch
                    {
                        // Some vendor bank ids can fail if local template data is incomplete.
                        // Keep scanning so one bad id does not disable all printing templates.
                    }
                }

                return result.Count == 0 ? null : new ZhenchengTemplateCatalog(result);
            }
            finally
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.SetCurrentDirectory(previousDirectory);
                }
            }
        }

        private IReadOnlyList<VendorTemplateItem> ResolveTemplates(Bank bank)
        {
            var best = templatesByVendorBankId
                .Select(item => new { item.Key, item.Value, Score = ScoreTemplates(bank, item.Value) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Key)
                .FirstOrDefault();

            return best?.Value ?? [];
        }

        private static int ScoreTemplates(Bank bank, IReadOnlyList<VendorTemplateItem> templates)
        {
            return templates.Sum(item => ScoreTemplateName(bank, item.Name));
        }

        private static int ScoreTemplateName(Bank bank, string templateName)
        {
            var bankName = bank.Name;
            var isCorporate = bank.Type == BankTypes.Corporate;
            if (!isCorporate && templateName.Contains("对公", StringComparison.Ordinal))
            {
                return 0;
            }

            if (isCorporate && templateName.Contains("个人", StringComparison.Ordinal))
            {
                return 0;
            }

            var aliases = GetBankTemplateAliases(bankName).ToList();
            var score = 0;

            if (isCorporate)
            {
                if (templateName.Contains(bankName, StringComparison.Ordinal))
                {
                    score += 100;
                }

                foreach (var alias in aliases)
                {
                    if (templateName.Contains($"{alias}对公", StringComparison.Ordinal)
                        || templateName.Contains($"对公{alias}", StringComparison.Ordinal))
                    {
                        score += 120;
                    }
                    else if (templateName.Contains(alias, StringComparison.Ordinal))
                    {
                        score += 30;
                    }
                }

            }
            else
            {
                if (templateName.Contains(bankName, StringComparison.Ordinal))
                {
                    score += 100;
                }

                foreach (var alias in aliases)
                {
                    if (templateName.Contains($"{alias}个人", StringComparison.Ordinal)
                        || templateName.Contains($"个人{alias}", StringComparison.Ordinal))
                    {
                        score += 120;
                    }
                    else if (templateName.Contains(alias, StringComparison.Ordinal))
                    {
                        score += 30;
                    }
                }

            }

            return Math.Max(0, score);
        }

        private static IEnumerable<string> GetBankTemplateAliases(string bankName)
        {
            var baseName = bankName
                .Replace("个人", string.Empty, StringComparison.Ordinal)
                .Replace("对公", string.Empty, StringComparison.Ordinal);

            if (!string.IsNullOrWhiteSpace(bankName))
            {
                yield return bankName;
            }

            if (!string.IsNullOrWhiteSpace(baseName) && baseName != bankName)
            {
                yield return baseName;
            }

            string[] extraAliases = baseName switch
            {
                "工行" => ["工商"],
                "农行" => ["农业"],
                "建行" => ["建设"],
                "中行" => ["中国银行"],
                "交行" => ["交通"],
                "招行" => ["招商"],
                "个人农商" or "对公农商" or "农商" => ["农商"],
                _ => Array.Empty<string>()
            };

            foreach (var alias in extraAliases)
            {
                yield return alias;
            }
        }

        private static VendorTemplateItem ReadTemplate(object source, long bankId)
        {
            var type = source.GetType();
            return new VendorTemplateItem(
                ConvertToLong(type.GetProperty("Id")?.GetValue(source)),
                bankId,
                Convert.ToString(type.GetProperty("Name")?.GetValue(source)) ?? string.Empty,
                ConvertToInt(type.GetProperty("PageSize")?.GetValue(source)),
                Convert.ToString(type.GetProperty("Remark")?.GetValue(source)) ?? string.Empty,
                Convert.ToString(type.GetProperty("PdfData")?.GetValue(source)) ?? string.Empty,
                ConvertToBool(type.GetProperty("IsSystem")?.GetValue(source), defaultValue: true));
        }

        private static long ConvertToLong(object? value)
        {
            return value switch
            {
                long number => number,
                int number => number,
                short number => number,
                byte number => number,
                string text when long.TryParse(text, out var parsed) => parsed,
                _ => 0
            };
        }

        private static int ConvertToInt(object? value)
        {
            return value switch
            {
                int number => number,
                long number => (int)number,
                short number => number,
                byte number => number,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => 0
            };
        }

        private static bool ConvertToBool(object? value, bool defaultValue)
        {
            return value switch
            {
                bool boolean => boolean,
                string text when bool.TryParse(text, out var parsed) => parsed,
                string text when text == "1" || text == "是" => true,
                string text when text == "0" || text == "否" => false,
                _ => defaultValue
            };
        }

        private static T? InvokeSilently<T>(Func<T?> action)
        {
            var output = Console.Out;
            var error = Console.Error;
            try
            {
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                return action();
            }
            finally
            {
                Console.SetOut(output);
                Console.SetError(error);
            }
        }

        private static string? ResolveVendorDir()
        {
            return ZhenchengRuntimeLocator.Resolve();
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

        private sealed record VendorTemplateItem(long Id, long BankId, string Name, int PageRows, string Remark, string PdfData, bool IsSystem);

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
    }

    private void EnsureLoaded()
    {
        if (loaded)
        {
            return;
        }

        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(storagePath))
        {
            try
            {
                var json = File.ReadAllText(storagePath);
                templatesByBank = JsonSerializer.Deserialize<Dictionary<long, List<PrintTemplate>>>(json, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                templatesByBank = [];
            }
        }

        loaded = true;
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(storagePath, JsonSerializer.Serialize(templatesByBank, JsonOptions));
    }
}
