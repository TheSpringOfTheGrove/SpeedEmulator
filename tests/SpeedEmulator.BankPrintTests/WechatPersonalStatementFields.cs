using System.Collections;
using SpeedEmulator.Models;

internal static partial class Program
{
    private static void TestWechatPersonalStatementFields(List<Entry> catalog)
    {
        var entries = catalog
            .Where(entry => entry.Bank.Name == "微信"
                && entry.Template.Name.Contains("微信", StringComparison.Ordinal)
                && entry.Template.Name.Contains("个人版", StringComparison.Ordinal))
            .ToArray();
        if (entries.Length == 0)
        {
            throw new Exception("缺少微信个人版模板");
        }

        foreach (var entry in entries)
        foreach (var exporter in new[] { "VendorBridge", "DefaultQuestPdfExporter", "DefaultStimulsoftExporter" })
        {
            var row = new FlowRecord
            {
                Id = 1,
                Index = 1,
                BankUserId = 990200,
                AccountTime = new DateTime(2026, 6, 2, 22, 13, 33),
                TradeMoney = -10.10,
                Balance = 90.90,
                ProductName = "商户消费",
                ProductBrief = "支出",
                IncomeAttribute = "支出",
                CashCheck = "零钱",
                SerialNum = "4200010000000001"
            };
            var context = Context(entry, row);
            var target = ((IList)Convert(context, exporter))[0]!;

            Equal("商户消费", Value(target, "ProductType"), entry.Template.Name + "/" + exporter + "/交易类型取明细交易类型");
            Equal("支出", Value(target, "Usage"), entry.Template.Name + "/" + exporter + "/收支其他保持收支方向");
        }

        Console.WriteLine($"PASS 微信个人版交易类型与收支其他分列：{entries.Length} 个模板。");
    }
}
