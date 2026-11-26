using System.IO;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

internal static partial class Program
{
    private static void TestConstructionBankCorporatePdfImports(List<Entry> catalog)
    {
        var paperPath = Environment.GetEnvironmentVariable("SPEEDEMULATOR_CCB_PAPER_PDF");
        var detailPath = Environment.GetEnvironmentVariable("SPEEDEMULATOR_CCB_DETAIL_PDF");
        var secondDetailPath = Environment.GetEnvironmentVariable("SPEEDEMULATOR_CCB_DETAIL_PDF_2");
        if (string.IsNullOrWhiteSpace(paperPath) || !File.Exists(paperPath)
            || string.IsNullOrWhiteSpace(detailPath) || !File.Exists(detailPath))
        {
            throw new InvalidOperationException(
                "Set SPEEDEMULATOR_CCB_PAPER_PDF and SPEEDEMULATOR_CCB_DETAIL_PDF to the two CCB corporate fixtures.");
        }

        var bank = catalog.First(entry => entry.Bank.Name == "建行对公").Bank;
        var service = new PdfImportService();
        if (!service.IsBankSupported(bank))
        {
            throw new InvalidOperationException("建行对公 PDF 导入未开放");
        }

        var paper = service.ImportBankUserAndFlowRecordsAsync(
            paperPath!,
            bank,
            BankUser.CreateDraft(bank)).GetAwaiter().GetResult();
        CheckCcbImport(!paper.HasBlockingErrors, "纸质版不得有阻断错误");
        CheckCcbImport(paper.FlowRecords.Count == 12, "纸质版应导入 12 条");
        CheckCcbImport(paper.FlowRecords.Count(item => item.IncomeAttribute == "支出") == 4, "纸质版支出应为 4 条");
        CheckCcbImport(paper.FlowRecords.Count(item => item.IncomeAttribute == "收入") == 8, "纸质版收入应为 8 条");
        CheckCcbImport(paper.FlowRecords.All(item => item.AccountTime?.TimeOfDay == TimeSpan.Zero), "纸质版不得编造交易时间");
        CheckCcbImport(paper.FlowRecords.All(item => !string.IsNullOrWhiteSpace(item.SerialNum)), "纸质版流水号应完整");
        CheckCcbImport(paper.User is { AccountNo.Length: > 0, AccountName.Length: > 0 }, "纸质版用户信息应完整");

        var detail = service.ImportBankUserAndFlowRecordsAsync(
            detailPath!,
            bank,
            BankUser.CreateDraft(bank)).GetAwaiter().GetResult();
        CheckCcbImport(!detail.HasBlockingErrors, "账户明细信息不得有阻断错误");
        CheckCcbImport(detail.FlowRecords.Count == 165, "账户明细信息应导入 165 条");
        CheckCcbImport(detail.FlowRecords.Count(item => item.IncomeAttribute == "支出") == 112, "账户明细信息支出应为 112 条");
        CheckCcbImport(detail.FlowRecords.Count(item => item.IncomeAttribute == "收入") == 53, "账户明细信息收入应为 53 条");
        CheckCcbImport(detail.FlowRecords.All(item => !item.SerialNum.Contains('-', StringComparison.Ordinal)), "交易流水号不得包含账户明细编号");
        var sourceNullSerialRows = detail.FlowRecords
            .Where(item => item.ExtraFields.TryGetValue("账户明细编号-交易流水号", out var value)
                && value.EndsWith("-null", StringComparison.OrdinalIgnoreCase))
            .ToList();
        CheckCcbImport(sourceNullSerialRows.Count > 0, "测试样本应包含 null 流水号");
        CheckCcbImport(sourceNullSerialRows.All(item => string.IsNullOrWhiteSpace(item.SerialNum)), "源 PDF 的 null 流水号应保留为空");
        CheckCcbImport(detail.FlowRecords.Count(item => string.IsNullOrWhiteSpace(item.SerialNum)) == sourceNullSerialRows.Count, "不得额外丢失交易流水号");
        var wrappedBankRow = detail.FlowRecords.Single(item =>
            item.ExtraFields.TryGetValue("账户明细编号", out var value) && value == "991");
        CheckCcbImport(wrappedBankRow.OppositeBank.StartsWith("中国建设银行", StringComparison.Ordinal), "多行对方开户机构不得截断");
        CheckCcbImport(detail.User is { AccountNo.Length: > 0, AccountName.Length: > 0 }, "账户明细信息用户信息应完整");
        CheckCcbImport(detail.User?.ChapterBranch == "北京郁花园支行", "账户明细信息章内支行应从印章图片识别");
        CheckCcbImport(detail.User?.ChapterCode == "FBWKST631341", "账户明细信息章内编码应从印章图片识别");
        CheckCcbBalances(detail.User!, detail.FlowRecords, "第一份账户明细信息");

        if (!string.IsNullOrWhiteSpace(secondDetailPath) && File.Exists(secondDetailPath))
        {
            var secondDetail = service.ImportBankUserAndFlowRecordsAsync(
                secondDetailPath!,
                bank,
                BankUser.CreateDraft(bank)).GetAwaiter().GetResult();
            CheckCcbImport(!secondDetail.HasBlockingErrors, "第二份账户明细信息不得有阻断错误");
            CheckCcbImport(secondDetail.FlowRecords.Count == 123, "第二份账户明细信息应导入 123 条");
            CheckCcbImport(secondDetail.FlowRecords.Count(item => item.IncomeAttribute == "支出") == 88, "第二份账户明细信息支出应为 88 条");
            CheckCcbImport(secondDetail.FlowRecords.Count(item => item.IncomeAttribute == "收入") == 35, "第二份账户明细信息收入应为 35 条");
            var reversedDebits = secondDetail.FlowRecords
                .Where(item => item["借方"].StartsWith("-", StringComparison.Ordinal))
                .ToList();
            CheckCcbImport(reversedDebits.Count == 4, "第二份账户明细信息应包含 4 条借方冲正");
            CheckCcbImport(reversedDebits.All(item => item.TradeMoney > 0 && item.IncomeAttribute == "收入"), "负借方应按冲正收入导入");
            CheckCcbImport(secondDetail.User?.ChapterBranch == "青岛市北支行", "第二份账户明细信息章内支行应从印章图片识别");
            CheckCcbImport(secondDetail.User?.ChapterCode == "AGKWPU390159", "第二份账户明细信息章内编码应从印章图片识别");
            CheckCcbBalances(secondDetail.User!, secondDetail.FlowRecords, "第二份账户明细信息");
        }

        var unsupportedPath = Environment.GetEnvironmentVariable("SPEEDEMULATOR_CCB_UNSUPPORTED_PDF");
        if (!string.IsNullOrWhiteSpace(unsupportedPath) && File.Exists(unsupportedPath))
        {
            try
            {
                _ = service.ImportBankUserAndFlowRecordsAsync(
                    unsupportedPath!,
                    bank,
                    BankUser.CreateDraft(bank)).GetAwaiter().GetResult();
                throw new InvalidOperationException("不支持的建行对公 PDF 格式未被拒绝");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("格式不支持", StringComparison.Ordinal))
            {
            }
        }

        Console.WriteLine("PASS CCB corporate PDF imports: paper 12 rows, account detail 165/123 rows, balance order, reversed debits, stamp OCR, strict signatures");
    }

    private static void CheckCcbBalances(BankUser user, IReadOnlyList<FlowRecord> rows, string fixtureName)
    {
        var previousBalance = (double)user.OpeningBalance;
        foreach (var row in rows)
        {
            CheckCcbImport(row.Balance.HasValue, $"{fixtureName}第 {row.Index} 条余额不能为空");
            var balanceChange = Math.Round(row.Balance!.Value - previousBalance, 2, MidpointRounding.AwayFromZero);
            var signedAmount = Math.Round(row.TradeMoney ?? 0d, 2, MidpointRounding.AwayFromZero);
            CheckCcbImport(Math.Abs(balanceChange - signedAmount) <= 0.015d, $"{fixtureName}第 {row.Index} 条金额与余额不连续");
            previousBalance = row.Balance.Value;
        }
    }

    private static void CheckCcbImport(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
