using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed class ZhenchengPrintBridgeService : IPrintPdfService
{
    private static readonly object SyncRoot = new();
    private static VendorBridge? bridge;

    private readonly IPrintPdfService? fallbackService;

    public ZhenchengPrintBridgeService(IPrintPdfService? fallbackService = null)
    {
        this.fallbackService = fallbackService;
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

    private async Task ExportInternalAsync(PrintRenderContext context, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        VendorBridge currentBridge;
        try
        {
            currentBridge = GetBridge();
        }
        catch when (fallbackService is not null)
        {
            await fallbackService.ExportAsync(context, path);
            return;
        }

        if (currentBridge.TryExport(context, path))
        {
            return;
        }

        throw new InvalidOperationException("模板未找到");
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
        private readonly MethodInfo? stimulsoftExportMethod;
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
                LoadOptionalVendorAssemblies("Stimulsoft*.dll");
                bankUserType = RequireType("MainEntry.entity.BankUser");
                flowType = RequireType("MainEntry.entity.GenerateFlowRecord");
                templateType = RequireType("MainEntry.entity.PDFTemplate");
                configType = RequireType("MainEntry.entity.PdfConfig.PDFConfig");
                flowListType = typeof(List<>).MakeGenericType(flowType);

                var types = GetLoadableTypes(mainAssembly).ToList();
                configFactory = types
                    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    .FirstOrDefault(method =>
                        method.ReturnType == configType
                        && method.GetParameters() is [{ ParameterType: var parameterType }]
                        && parameterType == typeof(string))
                    ?? throw new MissingMethodException("PDFConfig factory was not found.");

                renderFactory = types
                    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    .FirstOrDefault(method =>
                    {
                        var parameters = method.GetParameters();
                        return parameters.Length == 3
                            && parameters[0].ParameterType == bankUserType
                            && parameters[1].ParameterType == flowListType
                            && parameters[2].ParameterType == templateType
                            && method.ReturnType.FullName == "QuestPDF.Infrastructure.IDocument";
                    })
                    ?? throw new MissingMethodException("PDF render factory was not found.");

                stimulsoftExportMethod = types
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
                    });

                var questPdfAssembly = LoadQuestPdfAssembly();
                SetQuestPdfLicense(questPdfAssembly);
                ConfigureQuestPdfFonts(questPdfAssembly, vendorDir);
                InitializeStimulsoftPrintRuntime();
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

                var bankUser = CreateVendorBankUser(context);
                var records = CreateVendorFlowRecords(context);
                if (resolvedTemplate.Config is null)
                {
                    if (stimulsoftExportMethod is null || string.IsNullOrWhiteSpace(context.Template.PdfData))
                    {
                        return false;
                    }

                    if (DefaultStimulsoftExporter.TryExport(vendorDir, context, path))
                    {
                        return true;
                    }

                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(context.Template.PdfData));
                    stimulsoftExportMethod.Invoke(null, [bankUser, records, stream, path]);
                    return File.Exists(path);
                }

                var template = CreateVendorTemplate(context, resolvedTemplate);
                var document = renderFactory.Invoke(null, [bankUser, records, template]);
                if (document is null)
                {
                    return false;
                }

                generatePdfMethod.Invoke(null, [document, path]);
                return true;
            }
            finally
            {
                if (Directory.Exists(previousDirectory))
                {
                    Directory.SetCurrentDirectory(previousDirectory);
                }
            }
        }

        private ResolvedTemplate? ResolveTemplate(PrintRenderContext context)
        {
            var hasPdfData = !string.IsNullOrWhiteSpace(context.Template.PdfData);
            if (hasPdfData)
            {
                return new ResolvedTemplate(context.Template.Name, null);
            }

            foreach (var name in GetCandidateTemplateNames(context.Template.Name))
            {
                try
                {
                    if (configFactory.Invoke(null, [name]) is { } config)
                    {
                        return new ResolvedTemplate(name, config);
                    }
                }
                catch
                {
                    // Not every display name has a matching hard-coded config.
                    // Continue with aliases so one miss does not block preview.
                }
            }

            if (hasPdfData)
            {
                return new ResolvedTemplate(context.Template.Name, null);
            }

            return null;
        }

        private object CreateVendorBankUser(PrintRenderContext context)
        {
            var target = Activator.CreateInstance(bankUserType) ?? throw new InvalidOperationException("Cannot create vendor BankUser.");
            var values = CreateValueMap(context.BankUser);
            ApplyMatchingProperties(target, values);

            Set(target, "Id", context.BankUser.Id);
            Set(target, "BankId", GetVendorBankId(context));
            Set(target, "Username", FirstNotBlank(context.BankUser.AccountName, GetValue(values, "Username"), GetValue(values, "AccountName")));
            Set(target, "UserNum", FirstNotBlank(context.BankUser.AccountNo, GetValue(values, "UserNum"), GetValue(values, "CardNum")));
            Set(target, "AccountNum", FirstNotBlank(context.BankUser.AccountNo, GetValue(values, "AccountNum"), GetValue(values, "UserNum")));
            Set(target, "Account", FirstNotBlank(context.BankUser.AccountNo, GetValue(values, "Account"), GetValue(values, "AccountNum")));
            Set(target, "CardNum", FirstNotBlank(GetValue(values, "CardNum"), context.BankUser.AccountNo));
            Set(target, "IdNum", FirstNotBlank(context.BankUser.IdNumber, GetValue(values, "IdNum")));
            Set(target, "StartTime", context.BankUser.StartDate);
            Set(target, "EndTime", context.BankUser.EndDate);
            Set(target, "OpenBranch", FirstNotBlank(context.BankUser.OpenBranch, GetValue(values, "OpenBranch")));
            Set(target, "Currency", NormalizeCurrency(FirstNotBlank(context.BankUser.Currency, GetValue(values, "Currency"))));
            Set(target, "Remark", context.BankUser.Remark);
            Set(target, "InitialBalance", (double)context.BankUser.OpeningBalance);
            Set(target, "IsAutoInterest", context.BankUser.AutoCalculateInterest);
            Set(target, "IsPrintStamp", context.BankUser.ShouldPrintSeal);
            Set(target, "ZhangImg", context.BankUser.ShouldPrintSeal ? context.BankUser.SealImagePath : string.Empty);
            Set(target, "BankTitle", context.Bank.Name);
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
                ApplyMatchingProperties(target, values);

                var tradeMoney = source.TradeMoney ?? ParseNullableDouble(GetValue(values, "TradeMoney")) ?? 0d;
                Set(target, "Index", source.Index > 0 ? source.Index : index + 1);
                Set(target, "Id", source.Id);
                Set(target, "BankId", GetVendorBankId(context));
                Set(target, "BankUserId", context.BankUser.Id);
                Set(target, "AccountTime", source.AccountTime);
                Set(target, "TradeMoney", tradeMoney);
                Set(target, "Balance", source.Balance);
                Set(target, "IncomeAttribute", FirstNotBlank(source.IncomeAttribute, tradeMoney >= 0 ? "\u6536\u5165" : "\u652F\u51FA"));
                Set(target, "CreditAmount", source.CreditAmount ?? (tradeMoney > 0 ? tradeMoney : null));
                Set(target, "DebitAmount", source.DebitAmount ?? (tradeMoney < 0 ? Math.Abs(tradeMoney) : null));
                records.Add(target);
            }

            return records;
        }

        private object CreateVendorTemplate(PrintRenderContext context, ResolvedTemplate resolvedTemplate)
        {
            var target = Activator.CreateInstance(templateType) ?? throw new InvalidOperationException("Cannot create vendor PDFTemplate.");
            Set(target, "Id", context.Template.VendorId);
            Set(target, "BankId", GetVendorBankId(context));
            Set(target, "IsSystem", context.Template.IsSystem);
            Set(target, "Name", resolvedTemplate.Name);
            Set(target, "PageSize", context.Template.PageRows);
            Set(target, "Remark", context.Template.Remark);
            Set(target, "PdfConfig", resolvedTemplate.Config);
            Set(target, "PdfData", context.Template.PdfData);
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

        private void LoadOptionalVendorAssemblies(string searchPattern)
        {
            foreach (var file in Directory.EnumerateFiles(vendorDir, searchPattern))
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(file).Name;
                    if (loadContext.Assemblies.Any(item => string.Equals(item.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    loadContext.LoadFromAssemblyPath(file);
                }
                catch
                {
                    // Optional vendor UI/export assemblies are loaded best-effort.
                    // Missing one should not block templates that do not need it.
                }
            }
        }

        private void InitializeStimulsoftPrintRuntime()
        {
            if (stimulsoftExportMethod?.DeclaringType is { } exportType)
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
                        // Vendor print initialization methods are obfuscated and
                        // version-dependent. Try all zero-arg setup hooks, but do
                        // not block templates that do not need a particular hook.
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

            var functionsType = loadContext.Assemblies
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
                try
                {
                    addFunction.Invoke(null,
                    [
                        "ReportUtils",
                        "ReportUtils",
                        function.Name,
                        function.Name,
                        function.ReturnType,
                        reportUtilsType,
                        string.Empty,
                        parameters.Select(parameter => parameter.ParameterType).ToArray(),
                        parameters.Select(parameter => parameter.Name ?? string.Empty).ToArray(),
                        parameters.Select(parameter => parameter.Name ?? string.Empty).ToArray()
                    ]);
                }
                catch
                {
                    // Duplicate function registrations are harmless; Stimulsoft
                    // keeps global function state per assembly load context.
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

        private Type RequireType(string fullName)
        {
            return mainAssembly.GetType(fullName, throwOnError: true)
                ?? throw new TypeLoadException(fullName);
        }

        private sealed record ResolvedTemplate(string Name, object? Config);
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
                try
                {
                    addFunction.Invoke(null,
                    [
                        "ReportUtils",
                        "ReportUtils",
                        function.Name,
                        function.Name,
                        function.ReturnType,
                        reportUtilsType,
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
        }

        private void Export(PrintRenderContext context, string path)
        {
            var bankUser = CreateBankUser(context);
            var records = CreateFlowRecords(context);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(context.Template.PdfData));
            exportMethod.Invoke(null, [bankUser, records, stream, path]);
        }

        private void OpenTemplateDesigner(PrintTemplate template)
        {
            var vendorTemplate = CreateTemplate(template);
            templateDesignerMethod.Invoke(null, [vendorTemplate]);
            CopyTemplateBack(vendorTemplate, template);
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
            Set(target, "UserNum", FirstNotBlank(context.BankUser.AccountNo, GetValue(values, "UserNum"), GetValue(values, "CardNum")));
            Set(target, "AccountNum", FirstNotBlank(context.BankUser.AccountNo, GetValue(values, "AccountNum"), GetValue(values, "UserNum")));
            Set(target, "Account", FirstNotBlank(context.BankUser.AccountNo, GetValue(values, "Account"), GetValue(values, "AccountNum")));
            Set(target, "CardNum", FirstNotBlank(GetValue(values, "CardNum"), context.BankUser.AccountNo));
            Set(target, "IdNum", FirstNotBlank(context.BankUser.IdNumber, GetValue(values, "IdNum")));
            Set(target, "StartTime", context.BankUser.StartDate);
            Set(target, "EndTime", context.BankUser.EndDate);
            Set(target, "OpenBranch", FirstNotBlank(context.BankUser.OpenBranch, GetValue(values, "OpenBranch")));
            Set(target, "Currency", NormalizeCurrency(FirstNotBlank(context.BankUser.Currency, GetValue(values, "Currency"))));
            Set(target, "Remark", context.BankUser.Remark);
            Set(target, "InitialBalance", (double)context.BankUser.OpeningBalance);
            Set(target, "IsAutoInterest", context.BankUser.AutoCalculateInterest);
            Set(target, "IsPrintStamp", context.BankUser.ShouldPrintSeal);
            Set(target, "ZhangImg", context.BankUser.ShouldPrintSeal ? context.BankUser.SealImagePath : string.Empty);
            Set(target, "BankTitle", context.Bank.Name);
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
                ApplyMatchingProperties(target, values);

                var tradeMoney = source.TradeMoney ?? ParseNullableDouble(GetValue(values, "TradeMoney")) ?? 0d;
                Set(target, "Index", source.Index > 0 ? source.Index : index + 1);
                Set(target, "Id", source.Id);
                Set(target, "BankId", GetVendorBankId(context));
                Set(target, "BankUserId", context.BankUser.Id);
                Set(target, "AccountTime", source.AccountTime);
                Set(target, "TradeMoney", tradeMoney);
                Set(target, "Balance", source.Balance);
                Set(target, "IncomeAttribute", FirstNotBlank(source.IncomeAttribute, tradeMoney >= 0 ? "\u6536\u5165" : "\u652F\u51FA"));
                Set(target, "CreditAmount", source.CreditAmount ?? (tradeMoney > 0 ? tradeMoney : null));
                Set(target, "DebitAmount", source.DebitAmount ?? (tradeMoney < 0 ? Math.Abs(tradeMoney) : null));
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

    private static void Set(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        Set(target, property, value);
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

    private static IEnumerable<string> BuildCandidateTemplateNames(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            yield return name;
        }

        var latestSuffixes = new[] { "\uFF08\u6700\u65B0\u7248\uFF09", "\uFF08\u6700\u65B0\uFF09" };
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
        }

        var dashIndex = name.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex > 0)
        {
            yield return name[..dashIndex];
        }
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

    private static string FirstNotBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
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
