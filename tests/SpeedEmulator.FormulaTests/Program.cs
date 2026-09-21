using System.IO.Compression;
using System.Text;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;

var tests = new (string Name, Action Run)[]
{
    ("六类随机公式、日期和外层定界符", TestRandomAndDateFormulas),
    ("Excel 独立随机与同行关联", TestExcelFormulas),
    ("流水显示列复制", TestFlowColumnCopy),
    ("自定义字段公式与字段复制", TestCustomFieldFormula),
    ("缺失 Excel 列兼容并告警", TestMissingExcelColumn),
    ("未闭合和超长公式阻止保存", TestInvalidFormulas),
    ("Excel 导入、持久化和清空", TestFormulaDataSetImport),
    ("统一保存和二次保存幂等", TestSaveAndIdempotency),
    ("公式错误时保存保持原子性", TestAtomicSaveFailure)
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
