using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;

var vendorDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "VendorRuntime", "Zhencheng"));
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(vendorDirectory, $"{name.Name}.dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(vendorDirectory, "caiwu-core.dll"));
var factoryType = assembly.GetType("caiwu_core.entity.PdfConfig.PdfConfigFactory", throwOnError: true)!;
var createConfig = factoryType.GetMethod("GenPdfConfig", BindingFlags.Public | BindingFlags.Static)
    ?? throw new MissingMethodException(factoryType.FullName, "GenPdfConfig");
var config = createConfig.Invoke(null, ["建行个人电子版30"])
    ?? throw new InvalidOperationException("Template config was not created.");

Console.WriteLine($"Config type: {config.GetType().FullName}");
foreach (var property in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
{
    var value = property.GetValue(config);
    if (value is string or ValueType || value is null)
    {
        Console.WriteLine($"{property.Name}={value}");
    }
}

Console.WriteLine("Columns:");
var columns = config.GetType().GetProperty("PdfColumns", BindingFlags.Public | BindingFlags.Instance)?.GetValue(config) as IEnumerable;
if (columns is not null)
{
    foreach (var column in columns)
    {
        Console.WriteLine("- " + string.Join(", ", column!.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => $"{property.Name}={property.GetValue(column)}")));
    }
}

Console.WriteLine("GenerateFlowRecord properties:");
var recordType = assembly.GetType("caiwu_core.entity.GenerateFlowRecord", throwOnError: true)!;
foreach (var property in recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
{
    Console.WriteLine($"- {property.Name}:{property.PropertyType.Name}");
}

var outputDirectory = Path.Combine(Path.GetTempPath(), "speed-emulator-date-probe");
Directory.CreateDirectory(outputDirectory);
var bank = new Bank { Id = 8, Name = "建行", Type = BankTypes.Personal };
var templateRepository = new JsonPrintTemplateRepository();
var resolvedTemplate = (await templateRepository.ListByBankAsync(bank))
    .First(item => string.Equals(item.Name, "建行个人电子版30", StringComparison.Ordinal));
Console.WriteLine($"Resolved template: IsSystem={resolvedTemplate.IsSystem}, IsPageRowsOverride={resolvedTemplate.IsPageRowsOverride}, PageRows={resolvedTemplate.PageRows}");
Console.WriteLine($"Resolved config: Name={resolvedTemplate.Config.Name}, RowCount={resolvedTemplate.Config.RowCount}, Margins={resolvedTemplate.Config.MarginLeft}/{resolvedTemplate.Config.MarginTop}/{resolvedTemplate.Config.MarginRight}/{resolvedTemplate.Config.MarginBottom}, Font={resolvedTemplate.Config.FontFamily}, MinHeight={resolvedTemplate.Config.ColumnMinHeight}");
foreach (var column in resolvedTemplate.Config.Columns)
{
    Console.WriteLine($"Resolved column: {column.Name}, Field={column.Field}, Width={column.Width}, Font={column.FontFamily}/{column.FontSize}");
}

var user = new BankUser
{
    Id = 1217,
    BankId = bank.Id,
    BankName = bank.Name,
    AccountName = "日期探针",
    AccountNo = "6217003760175509504",
    CardNo = "6217003760175509504",
    Currency = "人民币",
    StartDate = new DateTime(2025, 6, 25),
    EndDate = new DateTime(2025, 6, 25)
};
var bridge = new ZhenchengPrintBridgeService();

if (args.Contains("--inspect", StringComparer.OrdinalIgnoreCase))
{
    return;
}

if (args.Contains("--actual", StringComparer.OrdinalIgnoreCase))
{
    var recordsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpeedEmulator",
        "flow-records",
        "8-1217.json");
    var actualRecords = JsonSerializer.Deserialize<List<FlowRecord>>(
            File.ReadAllText(recordsPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            })
        ?? throw new InvalidOperationException("CCB records were not loaded.");
    if (args.Contains("--actual-clean", StringComparer.OrdinalIgnoreCase))
    {
        actualRecords = actualRecords
            .Select(record =>
            {
                record.ExtraFields = new Dictionary<string, string>(StringComparer.Ordinal);
                return record;
            })
            .ToList();
    }
    if (args.Contains("--actual-one", StringComparer.OrdinalIgnoreCase))
    {
        actualRecords = actualRecords.Take(1).ToList();
    }
    if (args.Contains("--actual-minimal", StringComparer.OrdinalIgnoreCase))
    {
        var selectedFields = args
            .FirstOrDefault(argument => argument.StartsWith("--minimal-fields=", StringComparison.OrdinalIgnoreCase))?
            .Substring("--minimal-fields=".Length)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        actualRecords = actualRecords
            .Take(resolvedTemplate.PageRows)
            .Select(record => new FlowRecord
            {
                Id = record.Id,
                Index = record.Index,
                BankId = record.BankId,
                BankUserId = record.BankUserId,
                IsDocumentImported = record.IsDocumentImported,
                AccountTime = record.AccountTime,
                TradeMoney = record.TradeMoney,
                Balance = record.Balance,
                IncomeAttribute = record.IncomeAttribute,
                Account = record.Account,
                AccountNum = record.AccountNum,
                ProductName = record.ProductName,
                ProductBrief = record.ProductBrief,
                OppositeAccount = record.OppositeAccount,
                OppositeUsername = record.OppositeUsername,
                SequenceNum = selectedFields.Contains("sequence") ? record.SequenceNum : string.Empty,
                Currency = selectedFields.Contains("currency") ? record.Currency : "RMB",
                Remark = selectedFields.Contains("remark") ? record.Remark : string.Empty,
                TradePlace = selectedFields.Contains("place") ? record.TradePlace : string.Empty,
                DebitAmount = selectedFields.Contains("debit") ? record.DebitAmount : null,
                BalanceAmount = selectedFields.Contains("balance-amount") ? record.BalanceAmount : null,
                IncomeFlag = selectedFields.Contains("income-flag") ? record.IncomeFlag : string.Empty,
                ExtraFields = new Dictionary<string, string>(StringComparer.Ordinal)
            })
            .ToList();
    }
    if (args.Contains("--actual-no-place", StringComparer.OrdinalIgnoreCase))
    {
        foreach (var record in actualRecords)
        {
            record.TradePlace = string.Empty;
        }
    }
    if (args.Contains("--actual-no-remark", StringComparer.OrdinalIgnoreCase))
    {
        foreach (var record in actualRecords)
        {
            record.Remark = string.Empty;
        }
    }
    if (args.Contains("--actual-dump", StringComparer.OrdinalIgnoreCase))
    {
        var context = new PrintRenderContext
        {
            Bank = bank,
            BankUser = user,
            Records = actualRecords.Take(resolvedTemplate.PageRows).ToArray(),
            Template = resolvedTemplate
        };
        var getPrintRecords = typeof(ZhenchengPrintBridgeService).GetMethod(
            "GetVendorPrintRecords",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("GetVendorPrintRecords");
        var printRecord = ((IReadOnlyList<FlowRecord>)getPrintRecords.Invoke(null, [context])!).First();
        Console.WriteLine($"Raw: Index={context.Records[0].Index}, Sequence={context.Records[0].SequenceNum}, Serial={context.Records[0].SerialNum}, Time={context.Records[0].AccountTime:O}");
        Console.WriteLine($"Print: Index={printRecord.Index}, Sequence={printRecord.SequenceNum}, Serial={printRecord.SerialNum}, Time={printRecord.AccountTime:O}");
        Console.WriteLine("Print extras: " + string.Join(" | ", printRecord.ExtraFields.Select(item => $"{item.Key}={item.Value}")));
        var createVendorContext = typeof(ZhenchengPrintBridgeService).GetMethod(
            "CreateVendorPrintContext",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("CreateVendorPrintContext");
        var vendorContext = (PrintRenderContext)createVendorContext.Invoke(null, [context])!;
        var vendorRecord = vendorContext.Records.First();
        Console.WriteLine($"Vendor: Index={vendorRecord.Index}, Sequence={vendorRecord.SequenceNum}, Serial={vendorRecord.SerialNum}, Time={vendorRecord.AccountTime:O}");
        Console.WriteLine("Vendor extras: " + string.Join(" | ", vendorRecord.ExtraFields.Select(item => $"{item.Key}={item.Value}")));
        return;
    }
    var actualOutputPath = Path.Combine(outputDirectory, "ccb-actual.pdf");
    await bridge.ExportAsync(new PrintRenderContext
    {
        Bank = bank,
        BankUser = user,
        Records = actualRecords.Take(resolvedTemplate.PageRows).ToArray(),
        Template = resolvedTemplate
    }, actualOutputPath);
    Console.WriteLine($"Rendered actual: {actualOutputPath}");
    return;
}

var template = new PrintTemplate
{
    BankId = bank.Id,
    VendorBankId = 58,
    IsSystem = true,
    Name = "建行个人电子版30",
    PageRows = 28
};

foreach (var (label, accountTime) in new[]
{
    ("with-time", new DateTime(2025, 6, 25, 13, 26, 5)),
    ("date-only", new DateTime(2025, 6, 25))
})
{
    var record = new FlowRecord
    {
        Id = 1,
        Index = 1,
        BankId = bank.Id,
        BankUserId = user.Id,
        AccountTime = accountTime,
        TradeMoney = -861d,
        Balance = 20419.3d,
        IncomeAttribute = "支出",
        Account = user.AccountNo,
        AccountNum = user.AccountNo,
        ProductName = "消费",
        ProductBrief = "消费",
        Remark = "财付通-微信支付-微信转账",
        TradePlace = "财付通-微信支付-微信转账",
        OppositeAccount = "492026228480398401",
        OppositeUsername = "测试对方"
    };

    var outputPath = Path.Combine(outputDirectory, $"ccb-{label}.pdf");
    await bridge.ExportAsync(new PrintRenderContext
    {
        Bank = bank,
        BankUser = user,
        Records = [record],
        Template = template
    }, outputPath);
    Console.WriteLine($"Rendered {label}: {outputPath}");
}

var pageTemplate = template.Clone();
pageTemplate.IsPageRowsOverride = true;
pageTemplate.Config = new PrintPdfConfig
{
    RowCount = 28,
    MarginLeft = 18,
    MarginTop = 16,
    MarginRight = 18,
    MarginBottom = 16,
    FontFamily = "Microsoft YaHei",
    ColumnMinHeight = 18,
    SealRight = 70,
    SealWidth = 110
};
var pageRecords = Enumerable.Range(0, 28)
    .Select(index => new FlowRecord
    {
        Id = index + 1,
        Index = index + 1,
        BankId = bank.Id,
        BankUserId = user.Id,
        AccountTime = new DateTime(2025, 6, 25).AddDays(index).AddHours(13).AddMinutes(index),
        TradeMoney = -861d,
        Balance = 20419.3d - index,
        IncomeAttribute = "支出",
        Account = user.AccountNo,
        AccountNum = user.AccountNo,
        ProductName = "消费",
        ProductBrief = "消费",
        Remark = "财付通-微信支付-微信转账",
        TradePlace = "财付通-微信支付-微信转账",
        OppositeAccount = "492026228480398401",
        OppositeUsername = "测试对方"
    })
    .ToArray();
var pageOutputPath = Path.Combine(outputDirectory, "ccb-page-rows.pdf");
await bridge.ExportAsync(new PrintRenderContext
{
    Bank = bank,
    BankUser = user,
    Records = pageRecords,
    Template = pageTemplate
}, pageOutputPath);
Console.WriteLine($"Rendered page-rows: {pageOutputPath}");
