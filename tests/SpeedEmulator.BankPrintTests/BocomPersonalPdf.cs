using System.Collections;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

internal static partial class Program
{
    private static void TestBocomPersonalPdf(List<Entry> catalog, string[] args)
    {
        var bank = catalog.First(entry => entry.Bank.Name == "交行").Bank;
        var service = typeof(PdfImportService);
        if (service.GetMethod("FindTemplate", Static)!.Invoke(null, [bank]) is not null)
        {
            throw new Exception("交行个人 PDF 尚未批准对外使用，不应出现在 PDF 导入入口。");
        }

        var documentType = service.GetNestedType("PdfExtractedDocument", System.Reflection.BindingFlags.NonPublic)!;
        var wordType = service.GetNestedType("PdfTextWord", System.Reflection.BindingFlags.NonPublic)!;
        var lineType = service.GetNestedType("PdfTextLine", System.Reflection.BindingFlags.NonPublic)!;
        var document = Activator.CreateInstance(documentType)!;
        var words = (IList)documentType.GetProperty("Words")!.GetValue(document)!;
        var lines = (IList)documentType.GetProperty("Lines")!.GetValue(document)!;
        lines.Add(Activator.CreateInstance(lineType, [1, 1,
            "交通银行个人客户交易清单 账号/卡号：6222620610030834899 户名：测试客户 查询起日：2025-09-03 查询止日：2026-09-03 打印时间：2026-09-03 11:26:02 柜员号：EBB0001 币种：人民币"]));

        Add("交易日期", 42, 72, 100); Add("交易地点", 120, 150, 100); Add("交易类型", 260, 290, 100);
        Add("借贷状态", 332, 362, 100); Add("交易金额", 385, 415, 100); Add("余额", 490, 510, 100);
        Add("2025-09-03", 42, 92, 125); Add("抖音支付科技有限公司", 112, 220, 125); Add("网上支付", 255, 300, 125);
        Add("D", 340, 346, 125); Add("33.40", 395, 425, 125); Add("8976.32", 495, 530, 125);
        Add("2025-09-04", 42, 92, 151); Add("支付平台", 112, 160, 151); Add("跨行汇款", 255, 300, 151);
        Add("C", 340, 346, 151); Add("88383.00", 395, 435, 151); Add("97359.32", 495, 535, 151);

        var user = new BankUser { Id = 990200 };
        var result = new PdfImportResult();
        var parsed = (bool)service.GetMethod("ParseBocomPersonalPdf", Static)!.Invoke(null, [bank, user, document, result])!;
        if (!parsed) throw new Exception("交行个人 PDF 用户信息未解析");
        Equal("6222620610030834899", user.AccountNo, "交行账号卡号");
        Equal("测试客户", user.AccountName, "交行户名");
        Equal("EBB0001", user["查询柜员"], "交行查询柜员");
        Equal("2026-09-03 11:26:02", user["查询时间"], "交行查询时间");
        Equal("2", result.FlowRecords.Count.ToString(), "交行坐标解析条数");
        Equal("-33.4", result.FlowRecords[0].TradeMoney?.ToString(System.Globalization.CultureInfo.InvariantCulture), "D 借记金额为负数");
        Equal("支出", result.FlowRecords[0].IncomeAttribute, "D 借记收入支出");
        Equal("88383", result.FlowRecords[1].TradeMoney?.ToString(System.Globalization.CultureInfo.InvariantCulture), "C 贷记金额为正数");
        Equal("收入", result.FlowRecords[1].IncomeAttribute, "C 贷记收入支出");
        Equal("支付平台", result.FlowRecords[1].TradePlace, "交易地点独立保存");
        Equal("跨行汇款", result.FlowRecords[1].ProductName, "交易类型独立保存");

        var source = args.FirstOrDefault(arg => arg.StartsWith("--bocom-source-pdf="))?["--bocom-source-pdf=".Length..];
        if (!string.IsNullOrWhiteSpace(source))
        {
            // The parser is retained for further samples, but the public PDF-import
            // template is intentionally not registered yet. Exercise the dormant
            // parser directly so this regression test does not enable the entry.
            var extracted = service.GetMethod("ExtractDocument", Static)!.Invoke(null, [source, true])!;
            var sourceUser = new BankUser { Id = 990200 };
            var sourceResult = new PdfImportResult();
            var sourceParsed = (bool)service.GetMethod("ParseBocomPersonalPdf", Static)!
                .Invoke(null, [bank, sourceUser, extracted, sourceResult])!;
            if (sourceResult.FlowRecords.Count < 100)
            {
                throw new Exception($"交行标准 PDF 导入条数异常：{sourceResult.FlowRecords.Count}");
            }

            if (sourceResult.FlowRecords.Any(row => row.TradeMoney is null || row.Balance is null)
                || !sourceResult.FlowRecords.Any(row => row.TradeMoney < 0)
                || !sourceResult.FlowRecords.Any(row => row.TradeMoney > 0))
            {
                throw new Exception("交行标准 PDF 的金额、余额或借贷方向未完整解析");
            }

            if (!sourceParsed
                || string.IsNullOrWhiteSpace(sourceUser.AccountNo)
                || string.IsNullOrWhiteSpace(sourceUser.AccountName)
                || sourceUser.StartDate == default
                || sourceUser.EndDate == default
                || string.IsNullOrWhiteSpace(sourceUser["查询时间"]))
            {
                throw new Exception($"交行标准 PDF 的账号、户名、查询时间或查询起止日未完整解析：账号={sourceUser.AccountNo.Length}，户名={sourceUser.AccountName.Length}，查询时间={sourceUser["查询时间"]}，起日={sourceUser.StartDate:O}，止日={sourceUser.EndDate:O}");
            }

            Equal("2026-09-03 11:26:02", sourceUser["查询时间"], "交行标准 PDF 查询时间");

            Console.WriteLine($"PASS 交行标准 PDF：{sourceResult.FlowRecords.Count} 条，借贷方向、余额和用户信息均已解析。");
        }

        Console.WriteLine("PASS 交行个人 PDF 坐标列、D/C 正负金额及用户字段映射。");

        void Add(string text, double left, double right, double top) =>
            words.Add(Activator.CreateInstance(wordType, [1, text, left, right, top, top + 7]));
    }
}
