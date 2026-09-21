using System.IO.Compression;
using System.Text;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("六类随机公式、日期和外层定界符", TestRandomAndDateFormulas),
    ("全部日期格式符", TestAllDateFormatTokens),
    ("Excel 独立随机与同行关联", TestExcelFormulas),
    ("流水显示列复制", TestFlowColumnCopy),
    ("自定义字段公式与字段复制", TestCustomFieldFormula),
    ("缺失 Excel 列兼容并告警", TestMissingExcelColumn),
    ("未闭合和超长公式阻止保存", TestInvalidFormulas),
    ("Excel 导入、持久化和清空", TestFormulaDataSetImport),
    ("内置 Excel 示例及公式依赖检查", TestBuiltInExcelExampleAndDependencies),
    ("统一保存和二次保存幂等", TestSaveAndIdempotency),
    ("公式错误时保存保持原子性", TestAtomicSaveFailure),
    ("固定日期五种模式解析", TestFixedDateModes),
    ("固定日期区间按月生成", TestFixedDateRangeGeneration),
    ("工商银行公式参照明细一次性迁移", TestIcbcFormulaReferenceMigration),
    ("工商银行参照明细全公式端到端", TestIcbcFormulaReferenceEndToEnd)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void TestRandomAndDateFormulas()
{
    var bank = CreateBank(
        ("ID", nameof(FlowRecord.Index)),
        ("对方账号", nameof(FlowRecord.OppositeAccount)),
        ("备注", nameof(FlowRecord.Remark)),
        ("商户名称", nameof(FlowRecord.MerchantName)));
    var record = new FlowRecord
    {
        Index = 1,
        AccountTime = new DateTime(2025, 6, 9, 14, 5, 7),
        OppositeAccount = @"prefix\\(4)-{2}-<2>-[3]-@3@-#3#-%yyyyMMdd%\\suffix",
        Remark = @"A\\(2)\\B\\{2}\\C",
        MerchantName = "plain(12)"
    };

    var summary = new FlowFormulaEvaluator(new SequenceRandomSource(0)).Evaluate(bank, [record], null);

    AssertEqual("prefix0000-aa-AA-000-000-000-20250609suffix", record.OppositeAccount);
    AssertEqual("A00BaaC", record.Remark);
    AssertEqual("plain(12)", record.MerchantName);
    AssertFalse(record.ExtraFields.ContainsKey(nameof(FlowRecord.Index)), "非字符串属性不能写入自定义字段");
    AssertEqual(2, summary.ChangedFieldCount);
    AssertFalse(summary.HasErrors, "有效公式不应产生错误");
}

static void TestAllDateFormatTokens()
{
    var bank = CreateBank(("备注", nameof(FlowRecord.Remark)));
    var record = new FlowRecord
    {
        AccountTime = new DateTime(2025, 6, 19, 14, 30, 15, 123),
        Remark = @"\\%yyyy|yy|MM|M|dd|d|HH|hh|mm|ss|fff|tt|dddd|ddd%\\"
    };

    var summary = new FlowFormulaEvaluator(new SequenceRandomSource(0)).Evaluate(bank, [record], null);

    AssertEqual("2025|25|06|6|19|19|14|02|30|15|123|PM|Thursday|Thu", record.Remark);
    AssertFalse(summary.HasErrors, "全部日期格式符都应正常解析");
}

static void TestExcelFormulas()
{
    var bank = CreateBank(
        ("对方户名", nameof(FlowRecord.OppositeUsername)),
        ("对方账号", nameof(FlowRecord.OppositeAccount)),
        ("备注", nameof(FlowRecord.Remark)));
    var dataSet = CreateDataSet();
    var record = new FlowRecord
    {
        Index = 2,
        OppositeUsername = @"\\$姓$$名$\\",
        OppositeAccount = @"\\^卡号^\\",
        Remark = @"\\^姓名^\\"
    };

    var summary = new FlowFormulaEvaluator(new SequenceRandomSource(0, 1, 1)).Evaluate(bank, [record], dataSet);

    AssertEqual("张二", record.OppositeUsername);
    AssertEqual("222", record.OppositeAccount);
    AssertEqual("李二", record.Remark);
    AssertEqual(-1, record.ReplaceIndex);
    AssertFalse(summary.HasErrors, "Excel 公式不应产生错误");
}

static void TestFlowColumnCopy()
{
    var bank = CreateBank(
        ("对方账号", nameof(FlowRecord.OppositeAccount)),
        ("商户名称", nameof(FlowRecord.MerchantName)));
    var record = new FlowRecord
    {
        OppositeAccount = @"\\618580(3)\\",
        MerchantName = @"\\&对方账号&\\"
    };

    var summary = new FlowFormulaEvaluator(new SequenceRandomSource(7)).Evaluate(bank, [record], null);

    AssertEqual("618580777", record.OppositeAccount);
    AssertEqual("618580777", record.MerchantName);
    AssertEqual(2, summary.ChangedFieldCount);
}

static void TestCustomFieldFormula()
{
    var bank = CreateBank(
        ("自定义源", "[source]"),
        ("自定义目标", "[target]"));
    var record = new FlowRecord();
    record["source"] = @"\\{2}\\";
    record["target"] = @"\\&自定义源&\\";

    var summary = new FlowFormulaEvaluator(new SequenceRandomSource(0)).Evaluate(bank, [record], null);

    AssertEqual("aa", record["source"]);
    AssertEqual("aa", record["target"]);
    AssertEqual(2, summary.ChangedFieldCount);
}

static void TestMissingExcelColumn()
{
    var bank = CreateBank(("对方户名", nameof(FlowRecord.OppositeUsername)));
    var record = new FlowRecord { OppositeUsername = @"A\\$不存在$\\B" };

    var summary = new FlowFormulaEvaluator(new SequenceRandomSource(0)).Evaluate(bank, [record], CreateDataSet());

    AssertEqual("AB", record.OppositeUsername);
    AssertEqual(1, summary.WarningCount);
    AssertFalse(summary.HasErrors, "缺失列按兼容规则仅告警");
}

static void TestInvalidFormulas()
{
    var bank = CreateBank(
        ("对方账号", nameof(FlowRecord.OppositeAccount)),
        ("备注", nameof(FlowRecord.Remark)),
        ("商户名称", nameof(FlowRecord.MerchantName)));
    var record = new FlowRecord
    {
        OppositeAccount = @"\\(12)",
        Remark = @"\\(4097)\\",
        MerchantName = @"\\(999999999999999999999999)\\"
    };

    var summary = new FlowFormulaEvaluator(new SequenceRandomSource(0)).Evaluate(bank, [record], null);

    AssertTrue(summary.HasErrors, "未闭合和超长公式必须报错");
    AssertEqual(3, summary.Diagnostics.Count(item => item.Severity == FormulaDiagnosticSeverity.Error));
}

static void TestFormulaDataSetImport()
{
    WithTemporaryDirectory(directory =>
    {
        var workbookPath = Path.Combine(directory, "formula.xlsx");
        var storagePath = Path.Combine(directory, "data");
        WriteWorkbook(workbookPath);

        var service = new FormulaDataSetService(storagePath);
        var result = service.Import(8, workbookPath);
        AssertEqual(4, result.DataSet.Headers.Count);
        AssertEqual(2, result.DataSet.Rows.Count);
        AssertTrue(result.DataSet.TryGetValue(1, "姓名", out var name), "应读取姓名列");
        AssertEqual("李二", name);

        var reloaded = new FormulaDataSetService(storagePath).Get(8);
        AssertTrue(reloaded is not null, "持久化数据应可重新载入");
        AssertEqual("222", reloaded!.Rows[1]["卡号"]);
        AssertTrue(service.Clear(8), "清空应删除当前银行数据");
        AssertTrue(service.Get(8) is null, "清空后不应再返回数据");
    });
}

static void TestBuiltInExcelExampleAndDependencies()
{
    WithTemporaryDirectory(directory =>
    {
        var service = new FormulaDataSetService(Path.Combine(directory, "formula-data"));
        var loaded = service.LoadBuiltInExample(4);
        AssertTrue(loaded.DataSet.Headers.Contains("姓名"), "示例应包含姓名列");
        AssertTrue(loaded.DataSet.Headers.Contains("卡号"), "示例应包含卡号列");
        AssertEqual(6, loaded.DataSet.Rows.Count);
        AssertEqual("张三", loaded.DataSet.Rows[0]["姓名"]);
        AssertTrue(new FormulaDataSetService(Path.Combine(directory, "formula-data")).Get(4) is not null, "示例数据应持久化");

        var rule = new GenerateReferenceRule
        {
            OppositeUsername = @"\\$姓名$\\",
            OppositeAccount = @"前缀\\^卡号^\\",
            Remark = "$公式外不应识别$"
        };
        var requiredColumns = FormulaExcelDependencyAnalyzer.GetRequiredColumns([rule]);
        AssertEqual(2, requiredColumns.Count);
        AssertTrue(requiredColumns.Contains("姓名"), "应识别独立随机 Excel 列");
        AssertTrue(requiredColumns.Contains("卡号"), "应识别同行关联 Excel 列");
    });
}

static void TestSaveAndIdempotency()
{
    WithTemporaryDirectory(directory =>
    {
        var repository = new InMemoryFlowRecordRepository(Path.Combine(directory, "flow-records.json"));
        var dataService = new FormulaDataSetService(Path.Combine(directory, "formula-data"));
        var saveService = new FlowRecordSaveService(
            repository,
            new FlowFormulaEvaluator(new SequenceRandomSource(5)),
            dataService);
        var bank = CreateBank(("对方账号", nameof(FlowRecord.OppositeAccount)));
        bank.Id = 8;
        var record = new FlowRecord { Index = 1, OppositeAccount = @"\\618580(13)\\" };

        var first = saveService.SaveAllAsync(bank, 99, [record]).GetAwaiter().GetResult();
        var firstValue = record.OppositeAccount;
        AssertEqual("6185805555555555555", firstValue);
        AssertEqual(1, first.FormulaSummary.ChangedFieldCount);

        var second = saveService.SaveAllAsync(bank, 99, [record]).GetAwaiter().GetResult();
        AssertEqual(firstValue, record.OppositeAccount);
        AssertEqual(0, second.FormulaSummary.ChangedFieldCount);

        var persisted = repository.ListExistingByUserAsync(bank, 99).GetAwaiter().GetResult();
        AssertEqual(firstValue, persisted.Single().OppositeAccount);
    });
}

static void TestAtomicSaveFailure()
{
    WithTemporaryDirectory(directory =>
    {
        var repository = new InMemoryFlowRecordRepository(Path.Combine(directory, "flow-records.json"));
        var saveService = new FlowRecordSaveService(
            repository,
            new FlowFormulaEvaluator(new SequenceRandomSource(0)),
            new FormulaDataSetService(Path.Combine(directory, "formula-data")));
        var bank = CreateBank(("对方账号", nameof(FlowRecord.OppositeAccount)));
        bank.Id = 8;
        var original = @"\\(5000)\\";
        var record = new FlowRecord { OppositeAccount = original };

        AssertThrows<FormulaEvaluationException>(() =>
            saveService.SaveAllAsync(bank, 100, [record]).GetAwaiter().GetResult());
        AssertEqual(original, record.OppositeAccount);
        AssertEqual(0, repository.ListExistingByUserAsync(bank, 100).GetAwaiter().GetResult().Count);
    });
}

static void TestFixedDateModes()
{
    AssertFixedDateOption("+", FixedDateRuleKind.Global);
    AssertFixedDateOption("*", FixedDateRuleKind.Monthly);
    AssertFixedDateOption("=", FixedDateRuleKind.Daily);
    AssertFixedDateOption("3-6", FixedDateRuleKind.DayRange, 3, 6);
    AssertFixedDateOption(" 6 - 3 ", FixedDateRuleKind.DayRange, 3, 6);
    AssertFixedDateOption("19", FixedDateRuleKind.FixedDay, 19, 19);
    AssertFixedDateOption("0", FixedDateRuleKind.Invalid);
    AssertFixedDateOption("32", FixedDateRuleKind.Invalid);
    AssertFixedDateOption("-", FixedDateRuleKind.Invalid);

    AssertEqual(5, new GenerateConstRule { FixDay = "+" }.FixType);
    AssertEqual(4, new GenerateConstRule { FixDay = "*" }.FixType);
    AssertEqual(3, new GenerateConstRule { FixDay = "=" }.FixType);
    AssertEqual(2, new GenerateConstRule { FixDay = "3-6" }.FixType);
    AssertEqual(1, new GenerateConstRule { FixDay = "19" }.FixType);
}

static void TestFixedDateRangeGeneration()
{
    var bank = CreateBank(
        ("记账时间", nameof(FlowRecord.AccountTime)),
        ("交易金额", nameof(FlowRecord.TradeMoney)),
        ("账户余额", nameof(FlowRecord.Balance)));
    bank.Id = 4;
    bank.Name = "固定日期测试银行";
    var rule = new GenerateConstRule
    {
        Id = 1,
        Index = 1,
        BankId = bank.Id,
        IsCheck = true,
        IncomeAttribute = "收入",
        MinMoney = 1,
        MaxMoney = 1,
        FloutLength = 0,
        StartDay = 9,
        EndDay = 17,
        TradeHoliday = true,
        TradeWeekend = true,
        FixDay = "3-6",
        ReCnt = "2"
    };
    var result = new FlowAutoGenerator().Generate(new FlowAutoGenerationRequest
    {
        Bank = bank,
        BankUser = new BankUser
        {
            Id = 902,
            BankId = bank.Id,
            BankName = bank.Name,
            AccountName = "固定日期测试",
            AccountNo = "1",
            AutoCalculateInterest = false
        },
        Config = new FlowGenerationConfig
        {
            StartTime = new DateTime(2025, 1, 1),
            EndTime = new DateTime(2025, 2, 28, 23, 59, 59),
            OpeningBalance = 0,
            AllInMoney = 4,
            LastMoney = 4,
            MinInMoneyMonth1 = 2,
            MaxInMoneyMonth1 = 2,
            MinOutMoneyMonth1 = 0,
            MaxOutMoneyMonth1 = 0,
            MinInMoneyMonth2 = 2,
            MaxInMoneyMonth2 = 2,
            MinOutMoneyMonth2 = 0,
            MaxOutMoneyMonth2 = 0
        },
        References = [],
        ConstItems = [rule]
    });

    var fixedRecords = result.Records
        .Where(record => record.ExtraFields.TryGetValue("__GeneratedSourceKind", out var sourceKind)
            && sourceKind == "Const")
        .ToList();
    AssertEqual(4, fixedRecords.Count);
    AssertTrue(fixedRecords.All(record => record.AccountTime?.Day is >= 3 and <= 6), "区间规则只能在每月 3～6 日内生成");
    AssertEqual(2, fixedRecords.Count(record => record.AccountTime?.Month == 1));
    AssertEqual(2, fixedRecords.Count(record => record.AccountTime?.Month == 2));
}

static void TestIcbcFormulaReferenceEndToEnd()
{
    AssertTrue(
        FlowGenerationSeedCatalog.TryCreateBankSeed(4, "工行", out var snapshot),
        "应能读取工商银行内置参照明细");
    var demo = snapshot.References.Single(rule =>
        rule.ExtraFields.TryGetValue("__BuiltInFormulaDemoVersion", out var version)
        && version == "1");
    demo.IsCheck = true;
    demo.IncomeAttribute = "收入";
    demo.PercentMonth = 1;
    demo.MinMoney = 10;
    demo.MaxMoney = 10;

    var bank = CreateIcbcFormulaTestBank();
    var request = new FlowAutoGenerationRequest
    {
        Bank = bank,
        BankUser = new BankUser
        {
            Id = 901,
            BankId = bank.Id,
            BankName = bank.Name,
            AccountName = "公式测试用户",
            AccountNo = "6212345678901234567",
            Currency = "RMB",
            OpeningBalance = 0,
            AutoCalculateInterest = false
        },
        Config = new FlowGenerationConfig
        {
            StartTime = new DateTime(2025, 6, 1),
            EndTime = new DateTime(2025, 6, 30, 23, 59, 59),
            OpeningBalance = 0,
            AllInMoney = 10,
            AllOutMoney = 0,
            LastMoney = 10,
            MinInMoneyMonth1 = 10,
            MaxInMoneyMonth1 = 10,
            MinOutMoneyMonth1 = 0,
            MaxOutMoneyMonth1 = 0,
            MinInMoneyMonth2 = 10,
            MaxInMoneyMonth2 = 10,
            MinOutMoneyMonth2 = 0,
            MaxOutMoneyMonth2 = 0
        },
        References = [demo],
        ConstItems = []
    };

    var generated = new FlowAutoGenerator().Generate(request);
    var demoRecords = generated.Records
        .Where(record => record.ExtraFields.TryGetValue("__GeneratedSourceIndex", out var sourceIndex)
            && sourceIndex == demo.Index.ToString())
        .ToList();
    AssertTrue(demoRecords.Count > 0, "工商银行公式参照明细应生成至少一条流水");

    var summary = new FlowFormulaEvaluator(new SequenceRandomSource(0, 1, 1, 0, 1))
        .Evaluate(bank, demoRecords, CreateDataSet());
    AssertFalse(summary.HasErrors, "工商银行公式参照明细不应产生解析错误");
    AssertEqual(0, summary.WarningCount);

    foreach (var record in demoRecords)
    {
        AssertMatches("^[a-z]{12}$", record.AppNum, "小写字母公式");
        AssertMatches("^[A-Z]{12}$", record.SequenceNum, "大写字母公式");
        AssertMatches("^[0-9A-Z]{12}$", record.Currency, "数字加大写字母公式");
        AssertMatches("^[0-9a-z]{12}$", record.CashCheck, "数字加小写字母公式");
        AssertMatches("^[0-9a-zA-Z]{12}$", record.TradeCode, "数字大小写混合公式");
        AssertMatches("^\\d{12}$", record.NoticeType, "纯数字公式");
        AssertMatches("^622230\\d{13}$", record.OppositeAccount, "带固定前缀的数字公式");
        AssertEqual(record.OppositeAccount, record.OppositeBank);
        AssertEqual($"公式功能验证-{record.AccountTime:yyyy-MM-dd HH:mm:ss}", record.TradeExplain);
        AssertTrue(record.OppositeUsername is "张一" or "李二", "Excel 独立随机公式结果不正确");
        AssertTrue(
            (record.Operator == "张一" && record.InterfacePage == "111")
            || (record.Operator == "李二" && record.InterfacePage == "222"),
            "Excel 同行关联公式必须保持姓名与卡号对应");
        AssertFalse(ContainsFormulaDelimiter(record), "公式参照明细保存前应全部解析完成");
    }
}

static void TestIcbcFormulaReferenceMigration()
{
    WithTemporaryDirectory(directory =>
    {
        var repository = new InMemoryFlowGenerationRepository(Path.Combine(directory, "generation.json"));
        var bank = new Bank { Id = 4, Name = "工行", Type = BankTypes.Personal };
        repository.SaveAsync(bank.Id, null, new FlowGenerationSnapshot
        {
            References =
            [
                new GenerateReferenceRule
                {
                    Id = 1,
                    Index = 1,
                    BankId = bank.Id,
                    OppositeUsername = "保留用户原规则"
                }
            ]
        }).GetAwaiter().GetResult();

        var migrated = repository.LoadAsync(bank, null).GetAwaiter().GetResult();
        AssertEqual(2, migrated.References.Count);
        AssertTrue(migrated.References.Any(rule => rule.OppositeUsername == "保留用户原规则"), "迁移不能覆盖用户原规则");
        AssertEqual(1, migrated.References.Count(IsFormulaDemoRule));

        var withoutDemo = new FlowGenerationSnapshot
        {
            AppliedMigrations = migrated.AppliedMigrations.ToList(),
            Config = migrated.Config.Clone(),
            References = migrated.References.Where(rule => !IsFormulaDemoRule(rule)).Select(rule => rule.Clone()).ToList(),
            ConstItems = migrated.ConstItems.Select(rule => rule.Clone()).ToList()
        };
        repository.SaveAsync(bank.Id, null, withoutDemo).GetAwaiter().GetResult();

        var afterUserDelete = new InMemoryFlowGenerationRepository(Path.Combine(directory, "generation.json"))
            .LoadAsync(bank, null)
            .GetAwaiter()
            .GetResult();
        AssertEqual(0, afterUserDelete.References.Count(IsFormulaDemoRule));
    });
}

static bool IsFormulaDemoRule(GenerateReferenceRule rule)
{
    return rule.ExtraFields.TryGetValue("__BuiltInFormulaDemoVersion", out var version)
        && version == "1";
}

static Bank CreateIcbcFormulaTestBank()
{
    var bank = new Bank { Id = 4, Name = "工行", Type = BankTypes.Personal };
    AddTextColumns(
        bank.FlowColumns,
        ("ID", nameof(FlowRecord.Index)),
        ("记账时间", nameof(FlowRecord.AccountTime)),
        ("交易金额", nameof(FlowRecord.TradeMoney)),
        ("账户余额", nameof(FlowRecord.Balance)),
        ("应用号", nameof(FlowRecord.AppNum)),
        ("序号", nameof(FlowRecord.SequenceNum)),
        ("币种", nameof(FlowRecord.Currency)),
        ("钞汇", nameof(FlowRecord.CashCheck)),
        ("交易代码", nameof(FlowRecord.TradeCode)),
        ("注释", nameof(FlowRecord.TradeExplain)),
        ("通知种类发行代", nameof(FlowRecord.NoticeType)),
        ("操作员", nameof(FlowRecord.Operator)),
        ("界面", nameof(FlowRecord.InterfacePage)),
        ("对方户名", nameof(FlowRecord.OppositeUsername)),
        ("对方账号", nameof(FlowRecord.OppositeAccount)),
        ("交易场所", nameof(FlowRecord.TradePlace)),
        ("对方开户行", nameof(FlowRecord.OppositeBank)));
    return bank;
}

static void AddTextColumns(List<ColumnDefinition> target, params (string Name, string Field)[] columns)
{
    foreach (var (name, field) in columns)
    {
        target.Add(new ColumnDefinition
        {
            Name = name,
            Field = field,
            Type = "Text"
        });
    }
}

static bool ContainsFormulaDelimiter(FlowRecord record)
{
    return new[]
    {
        record.AppNum,
        record.SequenceNum,
        record.Currency,
        record.CashCheck,
        record.TradeCode,
        record.TradeExplain,
        record.NoticeType,
        record.Operator,
        record.InterfacePage,
        record.OppositeUsername,
        record.OppositeAccount,
        record.OppositeBank
    }.Any(value => value?.Contains(@"\\", StringComparison.Ordinal) == true);
}

static void AssertFixedDateOption(
    string value,
    FixedDateRuleKind expectedKind,
    int expectedStartDay = 0,
    int expectedEndDay = 0)
{
    var option = FixedDateRuleParser.Parse(value);
    AssertEqual(expectedKind, option.Kind);
    AssertEqual(expectedStartDay, option.StartDay);
    AssertEqual(expectedEndDay, option.EndDay);
}

static void AssertMatches(string pattern, string? value, string formulaName)
{
    if (value is null || !System.Text.RegularExpressions.Regex.IsMatch(value, pattern))
    {
        throw new InvalidOperationException($"{formulaName}结果不匹配：{value ?? "<null>"}");
    }
}

static Bank CreateBank(params (string Name, string Field)[] columns)
{
    var bank = new Bank { Id = 1, Name = "测试银行", Type = BankTypes.Personal };
    foreach (var column in columns)
    {
        bank.FlowColumns.Add(new ColumnDefinition
        {
            Name = column.Name,
            Field = column.Field,
            Type = "Text"
        });
    }

    return bank;
}

static FormulaDataSet CreateDataSet()
{
    return new FormulaDataSet
    {
        FileName = "test.xlsx",
        Headers = ["姓", "名", "姓名", "卡号"],
        Rows =
        [
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["姓"] = "张",
                ["名"] = "一",
                ["姓名"] = "张一",
                ["卡号"] = "111"
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["姓"] = "李",
                ["名"] = "二",
                ["姓名"] = "李二",
                ["卡号"] = "222"
            }
        ]
    };
}

static void WriteWorkbook(string path)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    WriteEntry(archive, "[Content_Types].xml", """
        <?xml version="1.0" encoding="utf-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
          <Default Extension="xml" ContentType="application/xml" />
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" />
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml" />
        </Types>
        """);
    WriteEntry(archive, "_rels/.rels", """
        <?xml version="1.0" encoding="utf-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml" />
        </Relationships>
        """);
    WriteEntry(archive, "xl/workbook.xml", """
        <?xml version="1.0" encoding="utf-8"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1" /></sheets>
        </workbook>
        """);
    WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
        <?xml version="1.0" encoding="utf-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml" />
        </Relationships>
        """);
    WriteEntry(archive, "xl/worksheets/sheet1.xml", """
        <?xml version="1.0" encoding="utf-8"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
          <row r="1"><c r="A1" t="inlineStr"><is><t>姓</t></is></c><c r="B1" t="inlineStr"><is><t>名</t></is></c><c r="C1" t="inlineStr"><is><t>姓名</t></is></c><c r="D1" t="inlineStr"><is><t>卡号</t></is></c></row>
          <row r="2"><c r="A2" t="inlineStr"><is><t>张</t></is></c><c r="B2" t="inlineStr"><is><t>一</t></is></c><c r="C2" t="inlineStr"><is><t>张一</t></is></c><c r="D2" t="inlineStr"><is><t>111</t></is></c></row>
          <row r="3"><c r="A3" t="inlineStr"><is><t>李</t></is></c><c r="B3" t="inlineStr"><is><t>二</t></is></c><c r="C3" t="inlineStr"><is><t>李二</t></is></c><c r="D3" t="inlineStr"><is><t>222</t></is></c></row>
        </sheetData></worksheet>
        """);
}

static void WriteEntry(ZipArchive archive, string path, string content)
{
    var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
    using var stream = entry.Open();
    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
    writer.Write(content);
}

static void WithTemporaryDirectory(Action<string> action)
{
    var directory = Path.Combine(Path.GetTempPath(), $"speed-emulator-formula-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        action(directory);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected: {expected}; Actual: {actual}");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool condition, string message)
{
    AssertTrue(!condition, message);
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected exception: {typeof(TException).Name}");
}

file sealed class SequenceRandomSource(params int[] values) : IFormulaRandomSource
{
    private readonly int[] values = values.Length == 0 ? [0] : values;
    private int index;

    public int Next(int maxExclusive)
    {
        var value = values[Math.Min(index, values.Length - 1)];
        index++;
        return Math.Abs(value) % maxExclusive;
    }
}
