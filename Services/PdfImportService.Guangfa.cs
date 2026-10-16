using System.Globalization;
using System.Text.RegularExpressions;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class PdfImportService
{
    // 商户列可以为空，不能根据剩余文字的顺序把摘要/附言左移进商户列。
    // 边界按此版式实际列宽设置，尤其账号可延伸到 x=392，不能按表头中心等分。
    private static readonly PdfPositionedColumnSpec[] CgbPersonalColumns =
    [
        new("DateTime", 30, 103), new("Currency", 103, 132),
        new("Amount", 132, 183), new("Balance", 183, 248),
        new("OppositeName", 248, 318), new("OppositeAccount", 318, 395),
        new("Merchant", 395, 444), new("Summary", 444, 498), new("Remark", 498, 570)
    ];

    private static readonly PdfPositionedColumnSpec[] CgbPrintedPersonalColumns =
    [
        new("DateTime", 35, 105), new("Currency", 105, 155),
        new("Amount", 155, 205), new("Balance", 205, 265),
        new("OppositeName", 265, 341), new("OppositeAccount", 341, 420),
        new("Merchant", 420, 464), new("Summary", 464, 516), new("Remark", 516, 570)
    ];

    private static bool TryParseCgbPositionedRecords(
        Bank bank, BankUser user, PdfExtractedDocument document, out List<FlowRecord> records)
    {
        records = [];
        var rows = new List<PdfPositionedRow>();
        foreach (var page in document.Words.GroupBy(word => word.PageNumber))
        {
            var merchant = page.FirstOrDefault(word => word.Text == "商户名称"
                && (Math.Abs(GetHorizontalCenter(word) - 419.5) < 5 || Math.Abs(GetHorizontalCenter(word) - 444) < 5));
            if (merchant is null) return false;
            var originalLayout = Math.Abs(GetHorizontalCenter(merchant) - 419.5) < 5;
            var summaryCenter = originalLayout ? 466.2 : 490;
            var remarkCenter = originalLayout ? 526.7 : 535.5;
            if (!page.Any(word => word.Text == "摘要" && Math.Abs(word.Top - merchant.Top) < 3
                    && Math.Abs(GetHorizontalCenter(word) - summaryCenter) < 5)
                || !page.Any(word => word.Text == "附言" && Math.Abs(word.Top - merchant.Top) < 3
                    && Math.Abs(GetHorizontalCenter(word) - remarkCenter) < 5))
            {
                // 其他版式继续走原专用解析器，避免使用错误列宽。
                return false;
            }
            rows.AddRange(BuildPositionedRowsWithTopLead(page.ToArray(),
                originalLayout ? CgbPersonalColumns : CgbPrintedPersonalColumns,
                word => Math.Abs(word.Top - merchant.Top) < 3,
                text => text.StartsWith("日期:", StringComparison.Ordinal) || text.StartsWith("日期：", StringComparison.Ordinal)
                    || text.Contains("广发银行股份有限公司", StringComparison.Ordinal)
                    || Regex.IsMatch(text, @"^第\d+页"),
                @"^\d{4}-\d{2}-\d{2}$", 85, 0, 6, joinCellWords: JoinCgbCellWords));
        }
        if (rows.Count == 0) return false;

        foreach (var row in rows)
        {
            var dateTime = Regex.Match(Regex.Replace(GetPositionedCell(row, "DateTime"), @"\s+", ""),
                @"^(?<date>\d{4}-\d{2}-\d{2})(?<time>\d{2}:\d{2}:\d{2})$");
            var timestamp = dateTime.Success
                ? ParseDateTimeOrNull($"{dateTime.Groups["date"].Value} {dateTime.Groups["time"].Value}") : null;
            var amountText = GetPositionedCell(row, "Amount");
            var balanceText = GetPositionedCell(row, "Balance");
            var amount = ParseDoubleOrNull(amountText);
            var balance = ParseDoubleOrNull(balanceText);
            if (!timestamp.HasValue || !amount.HasValue || !balance.HasValue)
            {
                records.Clear();
                return false;
            }

            var record = new FlowRecord
            {
                BankId = bank.Id, BankUserId = user.Id, Account = FirstNotBlank(user.AccountNo, user.CardNo),
                AccountTime = timestamp, Currency = GetPositionedCell(row, "Currency"),
                TradeMoney = amount, Balance = balance,
                OppositeUsername = GetPositionedCell(row, "OppositeName"),
                OppositeAccount = GetPositionedCell(row, "OppositeAccount"),
                SubAccountNum = GetPositionedCell(row, "Merchant"),
                ProductBrief = GetPositionedCell(row, "Summary"),
                ProductName = GetPositionedCell(row, "Summary"),
                TradeChannel = GetPositionedCell(row, "Summary"), Remark = GetPositionedCell(row, "Remark")
            };
            ApplySignedAmountColumns(record, amount);
            SetFlowRaw(record, "交易日期", timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            SetFlowRaw(record, "币种", record.Currency);
            SetFlowRaw(record, "发生额", amountText);
            SetFlowRaw(record, "交易金额", amountText);
            SetFlowRaw(record, "活期余额", balanceText);
            SetFlowRaw(record, "账户余额", balanceText);
            SetFlowRaw(record, "对手户名", record.OppositeUsername);
            SetFlowRaw(record, "对方姓名", record.OppositeUsername);
            SetFlowRaw(record, "对手账号", record.OppositeAccount);
            SetFlowRaw(record, "对方账号", record.OppositeAccount);
            SetFlowRaw(record, "分户序号", record.SubAccountNum);
            SetFlowRaw(record, "商户名称", record.SubAccountNum);
            SetFlowRaw(record, "摘要", record.ProductBrief);
            SetFlowRaw(record, "交易渠道", record.TradeChannel);
            SetFlowRaw(record, "附言", record.Remark);
            records.Add(record);
        }
        return true;
    }

    private static string JoinCgbCellWords(IEnumerable<PdfTextWord> words)
    {
        // 日期和时间同一基线的字形边界可相差零点几像素，不能仅按 Top 排序，
        // 否则会拼成“11:01:2026-09-2840”；先按行聚合，再按横坐标读取。
        var lines = new List<List<PdfTextWord>>();
        foreach (var word in words.OrderBy(word => word.Top).ThenBy(word => word.Left))
        {
            if (lines.Count == 0 || word.Top - lines[^1][0].Top > 2) lines.Add([]);
            lines[^1].Add(word);
        }
        return CleanPdfValue(string.Concat(lines.SelectMany(line => line.OrderBy(word => word.Left)).Select(word => word.Text)));
    }
}
