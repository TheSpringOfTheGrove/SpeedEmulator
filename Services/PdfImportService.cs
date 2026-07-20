using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SpeedEmulator.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace SpeedEmulator.Services;

public interface IPdfImportService
{
    bool IsBankSupported(Bank bank);

    string GetUnsupportedBankMessage(Bank bank);

    string? PickImportFile();

    Task<PdfImportResult> ImportBankUsersAsync(string path, Bank bank, CancellationToken cancellationToken = default);

    Task<PdfImportResult> ImportFlowRecordsAsync(string path, Bank bank, BankUser bankUser, CancellationToken cancellationToken = default);

    Task<PdfImportResult> ImportBankUserAndFlowRecordsAsync(string path, Bank bank, BankUser bankUser, CancellationToken cancellationToken = default);
}

public sealed class PdfImportService : IPdfImportService
{
    private const int MaxRawTextPreviewLength = 6000;
    private static readonly BankPdfTemplateDefinition[] SupportedTemplates =
    [
        new("中行", "中国银行个人交易流水 PDF", ["中国银行交易流水明细清单", "借记卡号", "客户姓名", "记账日期"]),
        new("工行", "中国工商银行个人借记账户历史明细 PDF", ["中国工商银行借记账户历史明细", "卡号", "户名", "收入/支出金额"]),
        new("建行", "中国建设银行个人活期账户交易明细 PDF", ["中国建设银行个人活期账户全部交易明细", "卡号/账号", "客户名称", "交易金额"]),
        new("民生", "中国民生银行个人账户对账单 PDF", ["个人账户对账单", "中国民生银行", "客户姓名", "客户账号"]),
        new("农行", "中国农业银行个人账户活期交易明细 PDF", ["中国农业银行账户活期交易明细清单", "户名", "账户", "电子流水号"]),
        new("平安", "平安银行个人账户交易明细清单 PDF", ["平安银行个人账户交易明细清单", "Transaction Details List of Personal Account", "交易对手信息"]),
        new("浦发", "上海浦东发展银行个人客户交易流水专用回单 PDF", ["上海浦东发展银行个人客户交易流水专用回单", "Transaction Statement of Shanghai Pudong Development Bank", "对手账号"]),
        new("兴业", "兴业银行个人交易流水 PDF", ["交易时间", "Transaction Time", "交易用途", "对方账户/对方银行"]),
        new("邮政", "中国邮政储蓄银行借记账户历史明细 PDF", ["中国邮政储蓄银行借记账户历史明细", "卡号/账号", "外部系统流水"]),
        new("中信", "中信银行个人账户交易明细 PDF", ["账户交易明细", "Transaction details", "交易日期", "账户余额"]),
        new("光大", "中国光大银行账户明细查询清单 PDF", ["中国光大银行账户明细查询清单", "Transaction Statement of China Everbright Bank", "客户账号", "交易日期"]),
        new("广发", "广发银行个人账户交易流水证明 PDF", ["个人账户交易流水证明", "广发银行股份有限公司", "交易时间", "交易金额"]),
        new("华夏", "华夏银行个人账户交易明细 PDF", ["记账日期", "Accounting Date", "对方卡/账号", "华夏银行股份有"]),
        new("微信", "微信支付交易明细证明 PDF", ["微信支付交易明细证明", "微信号", "交易明细对应时间段", "交易单号"]),
        new("支付宝", "支付宝交易流水证明 PDF", ["支付宝支付科技有限公司", "交易流水证明", "支付宝账号", "交易时间段"]),
        new("浦发对公", "上海浦东发展银行对公电子对账单 PDF", ["上海浦东发展银行电子对账单", "客户名称", "Account Number", "交易流水号"]),
        new("兴业对公", "兴业银行对公交易明细对账单 PDF", ["兴业银行", "交易明细对账单", "活期账号", "对方账号"]),
        new("招行对公", "招商银行对公账务明细清单 PDF", ["账务明细清单", "Statement Of Account", "开户银行", "账户名称"]),
        new("中行对公", "中国银行对公账户对账单 PDF", ["Account No.", "Account Name", "开户行", "借方发生额"]),
        new("中信对公", "中信银行对公账户交易明细 PDF", ["账户交易明细", "查询周期", "账户：", "柜员交易号"]),
        new("工行对公", "中国工商银行对公账户明细清单 PDF", ["中国工商银行账户明细清单", "本方账号户名", "对方账号", "借贷标志"]),
        new("光大对公", "中国光大银行对公账户对账单 PDF", ["中国光大银行对公账户对账单", "账户名称", "借/贷", "流水号"]),
        new("建行对公", "中国建设银行对公账户明细信息 PDF", ["中国建设银行账户明细信息", "本方户名", "账户明细编号", "交易流水号"]),
        new("交行对公", "交通银行对公明细对账单 PDF", ["交通银行", "明细对账单", "会计日期", "对方账号"]),
        new("民生对公", "中国民生银行对公单位账户对账单 PDF", ["单位账户对账单", "客户名称", "客户账号", "对方户名/账号"]),
        new("农行对公", "中国农业银行对公账户明细 PDF", ["账户明细", "户名", "收入金额", "对方开户行"])
    ];

    private static readonly HuaxiaColumnSpec[] HuaxiaPersonalElectronicColumns =
    [
        new("Date", 30, 76),
        new("Summary", 76, 124),
        new("Amount", 124, 176),
        new("Balance", 176, 218),
        new("TradeUnit", 218, 264),
        new("OppositeName", 264, 342),
        new("OppositeAccount", 342, 416),
        new("OppositeBank", 416, 496),
        new("Remark", 496, 570)
    ];

    private static readonly PdfPositionedColumnSpec[] SpdbCorporateColumns =
    [
        new("Date", 18, 82),
        new("Serial", 82, 165),
        new("Debit", 165, 266),
        new("Credit", 266, 356),
        new("Balance", 356, 442),
        new("OppositeBank", 442, 555),
        new("OppositeName", 555, 640),
        new("Summary", 640, 730),
        new("Remark", 730, 830)
    ];

    private static readonly PdfPositionedColumnSpec[] CiticCorporateColumns =
    [
        new("Date", 20, 82),
        new("TellerSerial", 82, 155),
        new("Summary", 155, 255),
        new("OppositeAccount", 255, 350),
        new("OppositeName", 350, 510),
        new("FundBook", 510, 610),
        new("Debit", 610, 690),
        new("Credit", 690, 760),
        new("Balance", 760, 835)
    ];

    private static readonly PdfPositionedColumnSpec[] IcbcCorporateColumns =
    [
        new("OppositeAccount", 35, 112),
        new("Date", 112, 143),
        new("Time", 143, 172),
        new("Direction", 172, 225),
        new("OppositeName", 225, 305),
        new("OppositeBankNo", 305, 365),
        new("Usage", 365, 430),
        new("Summary", 430, 496),
        new("Remark", 496, 555),
        new("Balance", 555, 620),
        new("Amount", 620, 675),
        new("PostingDate", 675, 744),
        new("AvailableBalance", 744, 810)
    ];

    private static readonly PdfPositionedColumnSpec[] CcbCorporateColumns =
    [
        new("Account", 0, 48),
        new("DateTime", 48, 82),
        new("Debit", 82, 116),
        new("Credit", 116, 146),
        new("Balance", 146, 182),
        new("Currency", 182, 212),
        new("OppositeName", 212, 247),
        new("OppositeAccount", 247, 285),
        new("OppositeBank", 285, 316),
        new("PostingDate", 316, 352),
        new("Summary", 352, 382),
        new("Remark", 382, 415),
        new("Serial", 415, 455),
        new("EnterpriseSerial", 455, 485),
        new("VoucherType", 485, 516),
        new("VoucherNum", 516, 555),
        new("Medium", 555, 590)
    ];

    private static readonly PdfPositionedColumnSpec[] BocomCorporateColumns =
    [
        new("Index", 18, 42),
        new("AccountingDate", 42, 82),
        new("TradeDate", 82, 122),
        new("TradeName", 122, 166),
        new("VoucherType", 166, 212),
        new("VoucherNum", 212, 260),
        new("Debit", 260, 324),
        new("Credit", 324, 382),
        new("Balance", 382, 466),
        new("CardNo", 466, 516),
        new("TradePlace", 516, 564),
        new("OppositeAccount", 564, 610),
        new("OppositeName", 610, 658),
        new("OppositeBank", 658, 706),
        new("Summary", 706, 756),
        new("Serial", 756, 832)
    ];

    private static readonly PdfPositionedColumnSpec[] CmbcCorporateColumns =
    [
        new("DateTime", 18, 58),
        new("Summary", 58, 198),
        new("VoucherType", 198, 232),
        new("VoucherNum", 232, 270),
        new("Debit", 300, 352),
        new("Credit", 378, 426),
        new("Balance", 456, 498),
        new("Serial", 498, 542),
        new("Counterparty", 542, 705),
        new("OppositeBank", 705, 832)
    ];

    private static readonly PdfPositionedColumnSpec[] AbcCorporateColumns =
    [
        new("DateTime", 20, 82),
        new("Income", 92, 152),
        new("Expense", 162, 222),
        new("Balance", 232, 292),
        new("OppositeAccount", 292, 370),
        new("OppositeName", 370, 438),
        new("OppositeBank", 438, 516),
        new("Summary", 516, 580)
    ];

    public bool IsBankSupported(Bank bank)
    {
        return FindTemplate(bank) is not null;
    }

    public string GetUnsupportedBankMessage(Bank bank)
    {
        return CreateUnsupportedBankMessage(bank);
    }

    public string? PickImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public Task<PdfImportResult> ImportBankUsersAsync(string path, Bank bank, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var template = GetTemplateOrThrow(bank);
            cancellationToken.ThrowIfCancellationRequested();
            var document = ExtractDocument(path);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTemplateOrThrow(bank, template, document);
            return ParseSpecializedBankPdf(path, bank, BankUser.CreateDraft(bank), document, PdfImportTarget.BankUsers);
        }, cancellationToken);
    }

    public Task<PdfImportResult> ImportFlowRecordsAsync(
        string path,
        Bank bank,
        BankUser bankUser,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var template = GetTemplateOrThrow(bank);
            cancellationToken.ThrowIfCancellationRequested();
            var document = ExtractDocument(path);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTemplateOrThrow(bank, template, document);
            return ParseSpecializedBankPdf(path, bank, bankUser.Clone(), document, PdfImportTarget.FlowRecords);
        }, cancellationToken);
    }

    public Task<PdfImportResult> ImportBankUserAndFlowRecordsAsync(
        string path,
        Bank bank,
        BankUser bankUser,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var template = GetTemplateOrThrow(bank);
            cancellationToken.ThrowIfCancellationRequested();
            var document = ExtractDocument(path);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTemplateOrThrow(bank, template, document);
            return ParseSpecializedBankPdf(path, bank, bankUser.Clone(), document, PdfImportTarget.BankUserAndFlowRecords);
        }, cancellationToken);
    }

    private static BankPdfTemplateDefinition GetTemplateOrThrow(Bank bank)
    {
        return FindTemplate(bank)
            ?? throw new InvalidDataException(CreateUnsupportedBankMessage(bank));
    }

    private static string CreateUnsupportedBankMessage(Bank bank)
    {
        var bankName = string.IsNullOrWhiteSpace(bank.Name)
            ? "当前银行"
            : $"{bank.Name}（{bank.GetBankType()}）";
        return $"{bankName}的 PDF 导入功能正在开发中，请先使用已实现的银行模板。";
    }

    private static BankPdfTemplateDefinition? FindTemplate(Bank bank)
    {
        if (!string.Equals(bank.Type, BankTypes.Personal, StringComparison.Ordinal)
            && !string.Equals(bank.Type, BankTypes.Corporate, StringComparison.Ordinal))
        {
            return null;
        }

        return SupportedTemplates.FirstOrDefault(item =>
            string.Equals(item.BankName, bank.Name?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateTemplateOrThrow(
        Bank bank,
        BankPdfTemplateDefinition template,
        PdfExtractedDocument document)
    {
        var signatureText = BuildTemplateSignatureText(document);
        if (template.RequiredKeywords.All(item => signatureText.Contains(NormalizeTemplateSignatureText(item), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new InvalidDataException($"当前选择的 PDF 不是{bank.Name}{bank.GetBankType()} PDF 模板，请选择正确的 {template.DisplayName} 后再导入。");
    }

    private static string BuildTemplateSignatureText(PdfExtractedDocument document)
    {
        return NormalizeTemplateSignatureText(string.Join("\n", document.Lines
            .Take(160)
            .Select(item => item.Text)));
    }

    private static string NormalizeTemplateSignatureText(string value)
    {
        return string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));
    }

    private static PdfExtractedDocument ExtractDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("PDF 文件路径为空。");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("PDF 文件不存在。", path);
        }

        var extracted = new PdfExtractedDocument();

        try
        {
            using var document = PdfDocument.Open(path, new ParsingOptions { Passwords = [""] });
            extracted.PageCount = document.NumberOfPages;

            foreach (var page in document.GetPages())
            {
                string text;
                try
                {
                    text = ContentOrderTextExtractor.GetText(page);
                }
                catch (Exception ex)
                {
                    text = page.Text;
                    extracted.Issues.Add(new PdfImportIssue
                    {
                        Severity = PdfImportIssueSeverity.Warning,
                        PageNumber = page.Number,
                        Message = $"第 {page.Number} 页按阅读顺序提取失败，已尝试使用原始文本顺序：{ex.Message}"
                    });
                }

                var pageLines = SplitLines(text).ToList();
                for (var index = 0; index < pageLines.Count; index++)
                {
                    extracted.Lines.Add(new PdfTextLine(page.Number, index + 1, pageLines[index]));
                }

                try
                {
                    foreach (var word in page.GetWords())
                    {
                        var bounds = word.BoundingBox;
                        extracted.Words.Add(new PdfTextWord(
                            page.Number,
                            word.Text,
                            bounds.Left,
                            bounds.Right,
                            page.Height - bounds.Top,
                            page.Height - bounds.Bottom));
                    }
                }
                catch (Exception ex)
                {
                    extracted.Issues.Add(new PdfImportIssue
                    {
                        Severity = PdfImportIssueSeverity.Warning,
                        PageNumber = page.Number,
                        Message = $"第 {page.Number} 页按坐标提取文字失败，已继续使用普通文本解析：{ex.Message}"
                    });
                }
            }
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new InvalidDataException("PDF 文件已加密，当前无法无密码读取文字。请提供无密码版本或该 PDF 的打开密码后再导入。", ex);
        }
        catch (Exception ex) when (ex is not InvalidDataException and not FileNotFoundException)
        {
            throw new InvalidDataException($"PDF 读取失败：{ex.Message}", ex);
        }

        if (extracted.Lines.Count == 0)
        {
            extracted.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Error,
                Message = "没有从 PDF 中提取到文字。当前版本先支持文字型 PDF，扫描图片型 PDF 需要后续增加 OCR。"
            });
        }

        return extracted;
    }

    private static PdfImportResult ParseSpecializedBankPdf(
        string path,
        Bank bank,
        BankUser bankUser,
        PdfExtractedDocument document,
        PdfImportTarget target)
    {
        var result = CreateResult(path, bank, target, document, bankUser);
        var parsedUser = bank.Name switch
        {
            "中行" => ParseBocPdf(bank, bankUser, document, result),
            "工行" => ParseIcbcPdf(bank, bankUser, document, result),
            "建行" => ParseCcbPdf(bank, bankUser, document, result),
            "民生" => ParseCmbcPdf(bank, bankUser, document, result),
            "农行" => ParseAbcPdf(bank, bankUser, document, result),
            "平安" => ParsePingAnPdf(bank, bankUser, document, result),
            "浦发" => ParseSpdbPdf(bank, bankUser, document, result),
            "兴业" => ParseCibPdf(bank, bankUser, document, result),
            "邮政" => ParsePsbcPdf(bank, bankUser, document, result),
            "中信" => ParseCiticPdf(bank, bankUser, document, result),
            "光大" => ParseEverbrightPdf(bank, bankUser, document, result),
            "广发" => ParseCgbPdf(bank, bankUser, document, result),
            "华夏" => ParseHuaxiaPdf(bank, bankUser, document, result),
            "微信" => ParseWechatPdf(bank, bankUser, document, result),
            "支付宝" => ParseAlipayPdf(bank, bankUser, document, result),
            "浦发对公" => ParseSpdbCorporatePdf(bank, bankUser, document, result),
            "兴业对公" => ParseCibCorporatePdf(bank, bankUser, document, result),
            "招行对公" => ParseCmbCorporatePdf(bank, bankUser, document, result),
            "中行对公" => ParseBocCorporatePdf(bank, bankUser, document, result),
            "中信对公" => ParseCiticCorporatePdf(bank, bankUser, document, result),
            "工行对公" => ParseIcbcCorporatePdf(bank, bankUser, document, result),
            "光大对公" => ParseEverbrightCorporatePdf(bank, bankUser, document, result),
            "建行对公" => ParseCcbCorporatePdf(bank, bankUser, document, result),
            "交行对公" => ParseBocomCorporatePdf(bank, bankUser, document, result),
            "民生对公" => ParseCmbcCorporatePdf(bank, bankUser, document, result),
            "农行对公" => ParseAbcCorporatePdf(bank, bankUser, document, result),
            _ => false
        };

        NormalizeImportedUser(bankUser, bank);
        if (parsedUser && HasUsefulUserData(bankUser))
        {
            result.Users.Add(bankUser);
        }
        else
        {
            result.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Info,
                Message = "PDF 中未识别到完整用户信息，将保留当前用户资料。"
            });
        }

        foreach (var record in result.FlowRecords)
        {
            PdfImportTabularMapper.NormalizeImportedFlowRecord(record, bank, bankUser);
        }

        ReindexFlowRecords(result.FlowRecords);
        if (target == PdfImportTarget.BankUsers && result.Users.Count == 0 && !result.HasBlockingErrors)
        {
            result.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Error,
                Message = $"没有识别到可导入的用户信息。请确认 PDF 是当前银行的{bank.GetBankType()}流水模板。"
            });
        }

        if (target != PdfImportTarget.BankUsers && result.FlowRecords.Count == 0 && !result.HasBlockingErrors)
        {
            result.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Error,
                Message = $"已识别为{bank.Name}{bank.GetBankType()} PDF 模板，但没有解析到可导入的流水明细。"
            });
        }

        return result;
    }

    private static bool ParseBocPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var text = line.Text;
            var rangeMatch = Regex.Match(text, @"交易区间：\s*(?<start>\d{4}-\d{2}-\d{2})\s*至\s*(?<end>\d{4}-\d{2}-\d{2})\s+客户姓名：\s*(?<name>\S+)");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "起始日期", rangeMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", rangeMatch.Groups["end"].Value);
                SetUserNamed(user, bank, "客户姓名", rangeMatch.Groups["name"].Value);
                parsedUser = true;
            }

            var cardMatch = Regex.Match(text, @"借记卡号：\s*(?<card>\S+)");
            if (cardMatch.Success)
            {
                SetUserNamed(user, bank, "借记卡号", cardMatch.Groups["card"].Value);
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"账号：\s*(?<account>\S+).*按收支筛选：\s*(?<income>\S+).*按币种筛选：\s*(?<currency>\S+).*打印时间：\s*(?<print>.+)$");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "按收支筛选", accountMatch.Groups["income"].Value);
                SetUserNamed(user, bank, "按货币筛选", accountMatch.Groups["currency"].Value);
                SetUserNamed(user, bank, "打印日期", accountMatch.Groups["print"].Value.Trim());
                parsedUser = true;
            }
        }

        foreach (var group in GroupLinesByStart(lines, IsBocRecordStart, IsBocIgnoredLine))
        {
            if (TryParseBocRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该中行流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseIcbcPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var match = Regex.Match(line.Text, @"卡号\s+(?<card>\S+)\s+户名：(?<name>\S+)\s+起止日期：(?<start>\d{4}-\d{2}-\d{2})\s*[—-]\s*(?<end>\d{4}-\d{2}-\d{2})");
            if (!match.Success)
            {
                continue;
            }

            SetUserNamed(user, bank, "卡号", match.Groups["card"].Value);
            SetUserNamed(user, bank, "户名", match.Groups["name"].Value);
            SetUserNamed(user, bank, "起始日期", match.Groups["start"].Value);
            SetUserNamed(user, bank, "截止日期", match.Groups["end"].Value);
            parsedUser = true;
            break;
        }

        foreach (var group in GroupIcbcRecords(lines))
        {
            if (TryParseIcbcRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该工行流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseCcbPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var match = Regex.Match(line.Text, @"卡号/账号:(?<card>\S+)\s+客户名称:(?<name>\S+)\s+币别:(?<currency>\S+)\s+钞汇:(?<cash>\S+)\s+起止日期:(?<start>\d{8})-(?<end>\d{8})");
            if (!match.Success)
            {
                continue;
            }

            SetUserNamed(user, bank, "卡号", match.Groups["card"].Value);
            SetUserNamed(user, bank, "户名", match.Groups["name"].Value);
            SetUserNamed(user, bank, "起始日期", match.Groups["start"].Value);
            SetUserNamed(user, bank, "终止日期", match.Groups["end"].Value);
            SetUserNamed(user, bank, "币种", match.Groups["currency"].Value);
            user["钞汇"] = CleanPdfValue(match.Groups["cash"].Value);
            parsedUser = true;
        }

        foreach (var line in lines.TakeLast(160))
        {
            var printMatch = Regex.Match(line.Text, @"生成时间：\s*(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
            if (printMatch.Success)
            {
                SetUserNamed(user, bank, "打印时间", printMatch.Groups["time"].Value);
                break;
            }
        }

        foreach (var group in GroupCcbRecords(lines))
        {
            if (TryParseCcbRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该建行流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseCmbcPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var headerMatch = Regex.Match(line.Text, @"客户姓名:(?<name>\S+)\s+客户账号:(?<card>\S+)\s+产品名称:(?<product>\S+)\s+币\s*种:(?<currency>\S+)\s+钞汇标志:(?<cash>\S+)");
            if (headerMatch.Success)
            {
                SetUserNamed(user, bank, "姓名", headerMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "卡号", headerMatch.Groups["card"].Value);
                SetUserNamed(user, bank, "币种", headerMatch.Groups["currency"].Value);
                SetUserNamed(user, bank, "钞汇标志", headerMatch.Groups["cash"].Value);
                user["产品名称"] = CleanPdfValue(headerMatch.Groups["product"].Value);
                parsedUser = true;
            }

            var accountMatch = Regex.Match(line.Text, @"开户机构:(?<branch>.+?)\s+账户账号:(?<account>\S+)\s+起止日期:(?<start>\d{4}/\d{2}/\d{2})-(?<end>\d{4}/\d{2}/\d{2}).*证件号码:(?<id>\S+)");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "开户机构", accountMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "账户账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "起始日期", accountMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", accountMatch.Groups["end"].Value);
                SetUserNamed(user, bank, "证件号", accountMatch.Groups["id"].Value);
                parsedUser = true;
            }
        }

        foreach (var line in lines.TakeLast(160))
        {
            var printMatch = Regex.Match(line.Text, @"打印时间:(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
            if (printMatch.Success)
            {
                SetUserNamed(user, bank, "打印日期", printMatch.Groups["time"].Value);
                break;
            }
        }

        foreach (var group in GroupCmbcRecords(lines))
        {
            if (TryParseCmbcRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该民生流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseAbcPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var nameMatch = Regex.Match(line.Text, @"户名：(?<name>\S+)\s+账户：(?<account>\S+)");
            if (nameMatch.Success)
            {
                SetUserNamed(user, bank, "姓名", nameMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "卡号", nameMatch.Groups["account"].Value);
                parsedUser = true;
            }

            var currencyMatch = Regex.Match(line.Text, @"币种：(?<currency>\S+)\s+汇钞标识：(?<cash>\S+)");
            if (currencyMatch.Success)
            {
                SetUserNamed(user, bank, "货币", currencyMatch.Groups["currency"].Value);
                user["汇钞标识"] = CleanPdfValue(currencyMatch.Groups["cash"].Value);
                parsedUser = true;
            }

            var rangeMatch = Regex.Match(line.Text, @"起止日期：(?<start>\d{8})-(?<end>\d{8})\s+电子流水号：(?<serial>\S+)");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "开始日期", rangeMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "结束日期", rangeMatch.Groups["end"].Value);
                SetUserNamed(user, bank, "流水号", rangeMatch.Groups["serial"].Value);
                parsedUser = true;
            }
        }

        foreach (var group in GroupLinesByStart(lines, IsAbcRecordStart, IsAbcIgnoredLine))
        {
            if (TryParseAbcRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该农行流水行未能按专用模板解析。");
            }
        }

        if (lines.Any(item => item.Text.Contains("可能导致数据缺失", StringComparison.Ordinal)))
        {
            result.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Warning,
                Message = "农行 PDF 原文提示该交易明细可能存在数据缺失，请在预览中重点核对流水完整性。"
            });
        }

        return parsedUser;
    }

    private static bool ParsePingAnPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines)
        {
            var text = CleanPdfValue(line.Text);
            var listMatch = Regex.Match(text, @"^(?<list>JYLS\S+)\s+(?<print>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})$");
            if (listMatch.Success)
            {
                SetUserNamed(user, bank, "清单编号", listMatch.Groups["list"].Value);
                SetUserNamed(user, bank, "打印日期", listMatch.Groups["print"].Value);
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"^(?<name>\S+)\s+(?<account>\d{10,})\s+(?<deposit>\S+)$");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "户名", accountMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "卡号", accountMatch.Groups["account"].Value);
                user["存款类型"] = CleanPdfValue(accountMatch.Groups["deposit"].Value);
                parsedUser = true;
            }

            var branchMatch = Regex.Match(text, @"^(?<currency>RMB|人民币)\s+(?<open>.+?)\s+(?<accept>平安银行.+)$");
            if (branchMatch.Success)
            {
                SetUserNamed(user, bank, "币种", branchMatch.Groups["currency"].Value);
                SetUserNamed(user, bank, "开户网点", branchMatch.Groups["open"].Value);
                SetUserNamed(user, bank, "受理行", branchMatch.Groups["accept"].Value);
                SetUserNamed(user, bank, "打印网点", branchMatch.Groups["accept"].Value);
                parsedUser = true;
            }

            var rangeMatch = Regex.Match(text, @"^(?<start>\d{8})\s*-\s*(?<end>\d{8})\s+(?<scope>.+)$");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "起始日期", rangeMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", rangeMatch.Groups["end"].Value);
                user["明细范围"] = CleanPdfValue(rangeMatch.Groups["scope"].Value);
                parsedUser = true;
            }
        }

        foreach (var group in GroupLinesByStart(lines, IsPingAnRecordStart, IsPingAnIgnoredLine))
        {
            if (TryParsePingAnRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该平安流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseSpdbPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var text = CleanPdfValue(line.Text);
            var accountMatch = Regex.Match(text, @"户名:\s*(?<name>.+?)\s+账号:\s*(?<account>\S+)\s+起止日期:\s*(?<start>\d{8})-(?<end>\d{8})");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "姓名", accountMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "卡号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "主卡卡号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "起始日期", accountMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", accountMatch.Groups["end"].Value);
                parsedUser = true;
            }

            var currencyMatch = Regex.Match(text, @"币种:\s*(?<currency>\S+)\s+钞汇标志:\s*(?<cash>\S+)\s+交易类型:\s*(?<type>\S+)");
            if (currencyMatch.Success)
            {
                SetUserNamed(user, bank, "币种", currencyMatch.Groups["currency"].Value);
                SetUserNamed(user, bank, "钞汇", currencyMatch.Groups["cash"].Value);
                user["交易类型"] = CleanPdfValue(currencyMatch.Groups["type"].Value);
                parsedUser = true;
            }
        }

        foreach (var group in GroupLinesByStart(lines, IsSpdbRecordStart, IsSpdbIgnoredLine))
        {
            if (TryParseSpdbRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该浦发流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseCibPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines)
        {
            var text = CleanPdfValue(line.Text);
            var rangeMatch = Regex.Match(text, @"^(?<start>\d{4}-\d{2}-\d{2})-(?<end>\d{4}-\d{2}-\d{2})$");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "开始日期", rangeMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "结束日期", rangeMatch.Groups["end"].Value);
                parsedUser = true;
            }

            var nameMatch = Regex.Match(text, @"^户\s*名：(?<name>.+)$");
            if (nameMatch.Success)
            {
                SetUserNamed(user, bank, "姓名", nameMatch.Groups["name"].Value);
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"^账号：(?<account>\S+)$");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "卡号", accountMatch.Groups["account"].Value);
                parsedUser = true;
            }

            var currencyMatch = Regex.Match(text, @"^币\s*种：(?<currency>.+)$");
            if (currencyMatch.Success)
            {
                SetUserNamed(user, bank, "币种", currencyMatch.Groups["currency"].Value);
                parsedUser = true;
            }

            var typeMatch = Regex.Match(text, @"^账户类型：(?<type>.+)$");
            if (typeMatch.Success)
            {
                user["账户类型"] = CleanPdfValue(typeMatch.Groups["type"].Value);
                parsedUser = true;
            }

            var printMatch = Regex.Match(text, @"^打印日期：(?<print>\d{4}-\d{2}-\d{2}\s+[\d.:]+)$");
            if (printMatch.Success)
            {
                SetUserNamed(user, bank, "打印日期", NormalizeBrokenDateTime(printMatch.Groups["print"].Value));
                parsedUser = true;
            }
        }

        foreach (var group in GroupLinesByStart(lines, IsCibRecordStart, IsCibIgnoredLine))
        {
            if (TryParseCibRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该兴业流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParsePsbcPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var text = CleanPdfValue(line.Text);
            var match = Regex.Match(text, @"卡号/账号：(?<account>\S+)\s+开户行名称：(?<branch>.+?)\s+户名：(?<name>\S+)\s+起止日期：(?<start>\d{4}年\d{2}月\d{2}日)-(?<end>\d{4}年\d{2}月\d{2}日)");
            if (!match.Success)
            {
                continue;
            }

            SetUserNamed(user, bank, "卡号", match.Groups["account"].Value);
            SetUserNamed(user, bank, "户名", match.Groups["name"].Value);
            SetUserNamed(user, bank, "开户机构名称", match.Groups["branch"].Value);
            SetUserNamed(user, bank, "打印机构", match.Groups["branch"].Value);
            user.OpenBranch = CleanPdfValue(match.Groups["branch"].Value);
            SetUserNamed(user, bank, "开始日期", match.Groups["start"].Value);
            SetUserNamed(user, bank, "结束日期", match.Groups["end"].Value);
            parsedUser = true;
            break;
        }

        foreach (var group in GroupPsbcRecords(lines))
        {
            if (TryParsePsbcRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该邮政流水行未能按专用模板解析。");
            }
        }

        if (lines.Any(item => item.Text.Contains("可能导致数据缺失", StringComparison.Ordinal)))
        {
            result.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Warning,
                Message = "邮政 PDF 原文提示该交易明细可能存在数据缺失，请在预览中重点核对流水完整性。"
            });
        }

        return parsedUser;
    }

    private static bool ParseCiticPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(120).Concat(lines.TakeLast(80)))
        {
            var text = CleanPdfValue(line.Text);
            var nameMatch = Regex.Match(text, @"户名：(?<name>.+?)\s+证件类型：(?<type>.+?)\s+证件号码：(?<id>\S+)");
            if (nameMatch.Success)
            {
                SetUserNamed(user, bank, "客户姓名", nameMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "证件编号", nameMatch.Groups["id"].Value);
                user["证件类型"] = CleanPdfValue(nameMatch.Groups["type"].Value);
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"账号：(?<account>\S+)\s+时间段：(?<start>\d{8})-(?<end>\d{8})\s+开立日期：(?<print>\d{4}-\d{2}-\d{2})");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "起始日期", accountMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", accountMatch.Groups["end"].Value);
                SetUserNamed(user, bank, "打印日期", accountMatch.Groups["print"].Value);
                user["开立日期"] = CleanPdfValue(accountMatch.Groups["print"].Value);
                parsedUser = true;
            }

            var currencyMatch = Regex.Match(text, @"查询最低限额：(?<min>\S+)\s+币种：(?<currency>\S+)");
            if (currencyMatch.Success)
            {
                SetUserNamed(user, bank, "币种", currencyMatch.Groups["currency"].Value);
                user["查询最低限额"] = CleanPdfValue(currencyMatch.Groups["min"].Value);
                parsedUser = true;
            }

            var verifyMatch = Regex.Match(text, @"验证码：(?<code>\S+)");
            if (verifyMatch.Success)
            {
                SetUserNamed(user, bank, "验证码", verifyMatch.Groups["code"].Value);
                parsedUser = true;
            }
        }

        foreach (var line in lines)
        {
            if (TryParseCiticRecord(line, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
        }

        InferCiticMoneyDirections(result.FlowRecords);
        return parsedUser;
    }

    private static bool ParseEverbrightPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var text = CleanPdfValue(line.Text);
            var nameMatch = Regex.Match(text, @"客户姓名：(?<name>.+)$");
            if (nameMatch.Success)
            {
                SetUserNamed(user, bank, "姓名", nameMatch.Groups["name"].Value);
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"发卡/折机构：(?<branch>.+?)\s+客户账号：(?<account>\S+)");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "发卡行", accountMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "开户行", accountMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "卡号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "客户账号", accountMatch.Groups["account"].Value);
                parsedUser = true;
            }

            var dateMatch = Regex.Match(text, @"下载日期：(?<print>\d{8}\s+\d{2}:\d{2}:\d{2})\s+对账日期：(?<start>\d{8})-(?<end>\d{8})");
            if (dateMatch.Success)
            {
                SetUserNamed(user, bank, "打印日期", dateMatch.Groups["print"].Value);
                SetUserNamed(user, bank, "开始对账日期", dateMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "结束对账日期", dateMatch.Groups["end"].Value);
                parsedUser = true;
            }

            var currencyMatch = Regex.Match(text, @"币种：(?<currency>\S+)\s+钞汇：(?<cash>\S+)");
            if (currencyMatch.Success)
            {
                SetUserNamed(user, bank, "币种", currencyMatch.Groups["currency"].Value);
                SetUserNamed(user, bank, "钞汇标志", currencyMatch.Groups["cash"].Value);
                parsedUser = true;
            }
        }

        foreach (var line in lines)
        {
            if (TryParseEverbrightRecord(line, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
        }

        InferDescendingBalanceMoneyDirections(result.FlowRecords);
        return parsedUser;
    }

    private static bool ParseCgbPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines)
        {
            var text = CleanPdfValue(line.Text);
            var accountMatch = Regex.Match(text, @"账号:\s*(?<account>\S+)\s+户名:\s*(?<name>.+?)\s+起止日期:\s*(?<start>\d{4}-\d{2}-\d{2})\s+至\s+(?<end>\d{4}-\d{2}-\d{2})");
            if (!accountMatch.Success)
            {
                continue;
            }

            SetUserNamed(user, bank, "账号卡号", accountMatch.Groups["account"].Value);
            SetUserNamed(user, bank, "客户姓名", accountMatch.Groups["name"].Value);
            SetUserNamed(user, bank, "开始日期", accountMatch.Groups["start"].Value);
            SetUserNamed(user, bank, "结束日期", accountMatch.Groups["end"].Value);
            parsedUser = true;
            break;
        }

        foreach (var group in GroupLinesByStart(lines, IsCgbRecordStart, IsCgbIgnoredLine))
        {
            if (TryParseCgbRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该广发流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseHuaxiaPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = ApplyHuaxiaHeaderUserInfo(bank, user, document.Lines) > 0;
        var lines = document.Lines.ToList();

        var positionedRecords = ParseHuaxiaPositionedRecords(bank, user, document).ToList();
        if (positionedRecords.Count > 0)
        {
            result.FlowRecords.AddRange(positionedRecords);
        }
        else
        {
            foreach (var group in GroupLinesByStart(lines, IsHuaxiaRecordStart, IsHuaxiaIgnoredLine))
            {
                if (TryParseHuaxiaRecord(group, bank, user, out var record))
                {
                    result.FlowRecords.Add(record);
                }
                else
                {
                    AddParseWarning(result, group, "该华夏流水行未能按专用模板解析。");
                }
            }
        }

        if (result.FlowRecords.Count > 0)
        {
            var minDate = result.FlowRecords
                .Where(item => item.AccountTime.HasValue)
                .Select(item => item.AccountTime!.Value.Date)
                .DefaultIfEmpty()
                .Min();
            var maxDate = result.FlowRecords
                .Where(item => item.AccountTime.HasValue)
                .Select(item => item.AccountTime!.Value.Date)
                .DefaultIfEmpty()
                .Max();
            if (minDate != default)
            {
                if (!HasUserRawField(user, "开始日期", "起始日期"))
                {
                    SetUserNamed(user, bank, "开始日期", minDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                parsedUser = true;
            }

            if (maxDate != default)
            {
                if (!HasUserRawField(user, "结束日期", "终止日期", "截止日期"))
                {
                    SetUserNamed(user, bank, "结束日期", maxDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                parsedUser = true;
            }
        }

        return parsedUser;
    }

    private static int ApplyHuaxiaHeaderUserInfo(Bank bank, BankUser user, IEnumerable<PdfTextLine> lines)
    {
        var matched = 0;
        foreach (var line in lines)
        {
            var text = CleanPdfValue(line.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var rangeMatch = Regex.Match(text, @"查询起止日期[:：](?<start>\d{8})至(?<end>\d{8})\s+户名[:：](?<name>.+?)\s+明细范围[:：](?<scope>.+)$");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "开始日期", FormatCompactDate(rangeMatch.Groups["start"].Value));
                SetUserNamed(user, bank, "结束日期", FormatCompactDate(rangeMatch.Groups["end"].Value));
                SetUserNamed(user, bank, "户名", rangeMatch.Groups["name"].Value);
                user["明细范围"] = CleanPdfValue(rangeMatch.Groups["scope"].Value);
                matched += 4;
                continue;
            }

            var splitRangeMatch = Regex.Match(text, @"^查询起止日期[:：](?<start>\d{8})至(?<end>\d{8})$");
            if (splitRangeMatch.Success)
            {
                SetUserNamed(user, bank, "开始日期", FormatCompactDate(splitRangeMatch.Groups["start"].Value));
                SetUserNamed(user, bank, "结束日期", FormatCompactDate(splitRangeMatch.Groups["end"].Value));
                matched += 2;
                continue;
            }

            var splitNameMatch = Regex.Match(text, @"^户名[:：](?<name>.+)$");
            if (splitNameMatch.Success)
            {
                SetUserNamed(user, bank, "户名", splitNameMatch.Groups["name"].Value);
                matched++;
                continue;
            }

            var splitScopeMatch = Regex.Match(text, @"^明细范围[:：](?<scope>.+)$");
            if (splitScopeMatch.Success)
            {
                user["明细范围"] = CleanPdfValue(splitScopeMatch.Groups["scope"].Value);
                matched++;
                continue;
            }

            var accountMatch = Regex.Match(text, @"账号[:：](?<account>\S+)\s+卡号[:：](?<card>\S+)\s+流水编码[:：](?<code>\S+)");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "卡号", accountMatch.Groups["card"].Value);
                user["流水编码"] = CleanPdfValue(accountMatch.Groups["code"].Value);
                matched += 3;
                continue;
            }

            var splitAccountMatch = Regex.Match(text, @"^账号[:：](?<account>\S+)$");
            if (splitAccountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", splitAccountMatch.Groups["account"].Value);
                matched++;
                continue;
            }

            var splitCardMatch = Regex.Match(text, @"^卡号[:：](?<card>\S+)$");
            if (splitCardMatch.Success)
            {
                SetUserNamed(user, bank, "卡号", splitCardMatch.Groups["card"].Value);
                matched++;
                continue;
            }

            var splitCodeMatch = Regex.Match(text, @"^流水编码[:：](?<code>\S+)$");
            if (splitCodeMatch.Success)
            {
                user["流水编码"] = CleanPdfValue(splitCodeMatch.Groups["code"].Value);
                matched++;
                continue;
            }

            var typeMatch = Regex.Match(text, @"交易类型[:：](?<type>\S+)\s+币种[:：](?<currency>\S+)\s+金额区间[:：](?<range>\S+)");
            if (typeMatch.Success)
            {
                SetUserNamed(user, bank, "交易类型", typeMatch.Groups["type"].Value);
                SetUserNamed(user, bank, "币种", typeMatch.Groups["currency"].Value);
                user["金额区间"] = CleanPdfValue(typeMatch.Groups["range"].Value);
                matched += 3;
                continue;
            }

            var splitTypeMatch = Regex.Match(text, @"^交易类型[:：](?<type>\S+)$");
            if (splitTypeMatch.Success)
            {
                SetUserNamed(user, bank, "交易类型", splitTypeMatch.Groups["type"].Value);
                matched++;
                continue;
            }

            var splitCurrencyMatch = Regex.Match(text, @"^币种[:：](?<currency>\S+)$");
            if (splitCurrencyMatch.Success)
            {
                SetUserNamed(user, bank, "币种", splitCurrencyMatch.Groups["currency"].Value);
                matched++;
                continue;
            }

            var splitAmountRangeMatch = Regex.Match(text, @"^金额区间[:：](?<range>\S+)$");
            if (splitAmountRangeMatch.Success)
            {
                user["金额区间"] = CleanPdfValue(splitAmountRangeMatch.Groups["range"].Value);
                matched++;
                continue;
            }

            var totalMatch = Regex.Match(text, @"收入合计金额[:：](?<income>[+-]?\d[\d,]*\.\d{2})\s+支出合计金额[:：](?<expense>[+-]?\d[\d,]*\.\d{2})\s+交易合计笔数[:：](?<count>\d+)");
            if (totalMatch.Success)
            {
                user["收入合计金额"] = CleanPdfValue(totalMatch.Groups["income"].Value);
                user["支出合计金额"] = CleanPdfValue(totalMatch.Groups["expense"].Value);
                user["交易合计笔数"] = CleanPdfValue(totalMatch.Groups["count"].Value);
                matched += 3;
                continue;
            }

            var splitIncomeTotalMatch = Regex.Match(text, @"^收入合计金额[:：](?<income>[+-]?\d[\d,]*\.\d{2})$");
            if (splitIncomeTotalMatch.Success)
            {
                user["收入合计金额"] = CleanPdfValue(splitIncomeTotalMatch.Groups["income"].Value);
                matched++;
                continue;
            }

            var splitExpenseTotalMatch = Regex.Match(text, @"^支出合计金额[:：](?<expense>[+-]?\d[\d,]*\.\d{2})$");
            if (splitExpenseTotalMatch.Success)
            {
                user["支出合计金额"] = CleanPdfValue(splitExpenseTotalMatch.Groups["expense"].Value);
                matched++;
                continue;
            }

            var splitCountMatch = Regex.Match(text, @"^交易合计笔数[:：](?<count>\d+)$");
            if (splitCountMatch.Success)
            {
                user["交易合计笔数"] = CleanPdfValue(splitCountMatch.Groups["count"].Value);
                matched++;
                continue;
            }

            var printMatch = Regex.Match(text, @"查询时间[:：](?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
            if (printMatch.Success)
            {
                SetUserNamed(user, bank, "打印日期", printMatch.Groups["time"].Value);
                user["查询时间"] = CleanPdfValue(printMatch.Groups["time"].Value);
                matched += 2;
            }
        }

        return matched;
    }

    private static IEnumerable<FlowRecord> ParseHuaxiaPositionedRecords(Bank bank, BankUser user, PdfExtractedDocument document)
    {
        foreach (var row in BuildHuaxiaPositionedRows(document.Words))
        {
            if (TryParseHuaxiaPositionedRecord(row, bank, user, out var record))
            {
                yield return record;
            }
        }
    }

    private static IReadOnlyList<HuaxiaPositionedRow> BuildHuaxiaPositionedRows(IReadOnlyList<PdfTextWord> words)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var rows = new List<HuaxiaPositionedRow>();
        foreach (var pageGroup in words.GroupBy(item => item.PageNumber).OrderBy(group => group.Key))
        {
            var pageWords = pageGroup
                .Select(item => item with { Text = CleanPdfValue(item.Text) })
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .OrderBy(item => item.Top)
                .ThenBy(item => item.Left)
                .ToList();
            var headerBottom = pageWords
                .Where(IsHuaxiaTableHeaderWord)
                .Select(item => item.Bottom)
                .DefaultIfEmpty(218)
                .Max() + 2;
            var footerTop = pageWords
                .Where(item => item.Top > headerBottom && IsHuaxiaTableFooterWord(item.Text))
                .Select(item => item.Top)
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            var dateWords = pageWords
                .Where(item => item.Top > headerBottom
                    && item.Top < footerTop
                    && item.Left < 90
                    && Regex.IsMatch(item.Text, @"^\d{4}-\d{2}-\d{2}$"))
                .OrderBy(item => item.Top)
                .ThenBy(item => item.Left)
                .ToList();

            for (var index = 0; index < dateWords.Count; index++)
            {
                var currentDate = dateWords[index];
                var bandTop = index == 0
                    ? headerBottom
                    : (dateWords[index - 1].Top + currentDate.Top) / 2d;
                var bandBottom = index + 1 < dateWords.Count
                    ? (currentDate.Top + dateWords[index + 1].Top) / 2d
                    : footerTop;

                var rowWords = pageWords
                    .Where(item => item.Top >= bandTop && item.Top < bandBottom)
                    .OrderBy(item => item.Top)
                    .ThenBy(item => item.Left)
                    .ToList();
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var column in HuaxiaPersonalElectronicColumns)
                {
                    var columnWords = rowWords
                        .Where(item => GetHorizontalCenter(item) >= column.Left && GetHorizontalCenter(item) < column.Right)
                        .ToList();
                    var value = JoinHuaxiaCellWords(columnWords);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        cells[column.Key] = value;
                    }
                }

                rows.Add(new HuaxiaPositionedRow(pageGroup.Key, currentDate.Top, cells));
            }
        }

        return rows;
    }

    private static bool TryParseHuaxiaPositionedRecord(
        HuaxiaPositionedRow row,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var date = GetHuaxiaCell(row, "Date");
        var amountText = GetHuaxiaCell(row, "Amount");
        var balanceText = GetHuaxiaCell(row, "Balance");
        var amount = ParseDoubleOrNull(amountText);
        if (!Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$") || !amount.HasValue)
        {
            return false;
        }

        var summary = GetHuaxiaCell(row, "Summary");
        var tradeUnit = GetHuaxiaCell(row, "TradeUnit");
        var oppositeName = GetHuaxiaCell(row, "OppositeName");
        var oppositeAccount = GetHuaxiaCell(row, "OppositeAccount");
        var oppositeBank = GetHuaxiaCell(row, "OppositeBank");
        var remark = GetHuaxiaCell(row, "Remark");

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(date);
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.TradeMoney = amount;
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.TradePlace = tradeUnit;
        record.NetNum = tradeUnit;
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        record.OppositeBank = oppositeBank;
        record.Remark = remark;
        record.TradeExplain = remark;
        ApplySignedAmountColumns(record, amount);

        SetFlowRaw(record, "卡号", record.Account);
        SetFlowRaw(record, "记账日期", date);
        SetFlowRaw(record, "交易日期", date);
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "交易金额", amountText);
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "账户余额", balanceText);
        SetFlowRaw(record, "交易机构", tradeUnit);
        SetFlowRaw(record, "商户网点号及名", tradeUnit);
        SetFlowRaw(record, "对方姓名", oppositeName);
        SetFlowRaw(record, "对方户名", oppositeName);
        SetFlowRaw(record, "对方卡/账号", oppositeAccount);
        SetFlowRaw(record, "对方卡号账号", oppositeAccount);
        SetFlowRaw(record, "对方账号", oppositeAccount);
        SetFlowRaw(record, "对方开户行", oppositeBank);
        SetFlowRaw(record, "对方银行", oppositeBank);
        SetFlowRaw(record, "附言", remark);
        return true;
    }

    private static string GetHuaxiaCell(HuaxiaPositionedRow row, string key)
    {
        return row.Cells.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static double GetHorizontalCenter(PdfTextWord word)
    {
        return (word.Left + word.Right) / 2d;
    }

    private static bool IsHuaxiaTableHeaderWord(PdfTextWord word)
    {
        return word.Text is "记账日期" or "摘要" or "交易金额" or "余额" or "交易机构" or "对方姓名" or "对方卡/账号" or "对方开户行" or "附言"
            or "Accounting" or "Description" or "Transaction" or "Amount" or "Balance" or "Unit" or "Counterparty" or "Card/Account" or "No." or "Bank" or "Remarks";
    }

    private static bool IsHuaxiaTableFooterWord(string text)
    {
        return text.StartsWith("查询时间", StringComparison.Ordinal)
            || text.StartsWith("Query", StringComparison.Ordinal)
            || text.StartsWith("1.", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^总\d+页$")
            || Regex.IsMatch(text, @"^第\d+页$");
    }

    private static string JoinHuaxiaCellWords(IEnumerable<PdfTextWord> words)
    {
        return CleanPdfValue(string.Concat(words
            .OrderBy(item => item.Top)
            .ThenBy(item => item.Left)
            .Select(item => item.Text)));
    }

    private static string FormatCompactDate(string value)
    {
        var text = CleanPdfValue(value);
        return DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : text;
    }

    private static bool HasUserRawField(BankUser user, params string[] fieldNames)
    {
        return fieldNames.Any(fieldName =>
            user.ExtraFields.TryGetValue(fieldName, out var value) && !string.IsNullOrWhiteSpace(value));
    }

    private static bool ParseWechatPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(120))
        {
            var text = CleanPdfValue(line.Text);
            var codeMatch = Regex.Match(text, @"编号\s*:?\s*(?<code>\S+)");
            if (codeMatch.Success)
            {
                SetUserNamed(user, bank, "编号", codeMatch.Groups["code"].Value);
                parsedUser = true;
            }

            var identityMatch = Regex.Match(text, @"兹证明：(?<name>.+?)（居民身份证：(?<id>[^）]+)），在其微信号：(?<account>.+?)中的");
            if (identityMatch.Success)
            {
                SetUserNamed(user, bank, "姓名", identityMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "身份证", identityMatch.Groups["id"].Value);
                SetUserNamed(user, bank, "微信号", identityMatch.Groups["account"].Value);
                parsedUser = true;
            }

            var currencyMatch = Regex.Match(text, @"币种\s*:\s*(?<currency>[^/]+)");
            if (currencyMatch.Success)
            {
                SetUserNamed(user, bank, "币种", currencyMatch.Groups["currency"].Value);
                parsedUser = true;
            }

            var rangeMatch = Regex.Match(text, @"交易明细对应时间段\s*(?<start>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})至(?<end>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "起始日期", rangeMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", rangeMatch.Groups["end"].Value);
                parsedUser = true;
            }
        }

        foreach (var group in GroupWechatRecords(lines))
        {
            if (TryParseWechatRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else if (GroupContainsMoney(group))
            {
                AddParseWarning(result, group, "该微信流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseAlipayPdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(120))
        {
            var text = CleanPdfValue(line.Text);
            var codeMatch = Regex.Match(text, @"编号:\s*(?<code>\S+)");
            if (codeMatch.Success)
            {
                SetUserNamed(user, bank, "编号", codeMatch.Groups["code"].Value);
                parsedUser = true;
            }

            var identityMatch = Regex.Match(text, @"兹证明:(?<name>.+?)\(证件号码:(?<id>[^)]+)\)在其支付宝账号(?<account>\S+)中");
            if (identityMatch.Success)
            {
                SetUserNamed(user, bank, "姓名", identityMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "身份证", identityMatch.Groups["id"].Value);
                SetUserNamed(user, bank, "支付宝账户", identityMatch.Groups["account"].Value);
                parsedUser = true;
            }

            var currencyMatch = Regex.Match(text, @"币种：(?<currency>[^/]+)");
            if (currencyMatch.Success)
            {
                SetUserNamed(user, bank, "币种", currencyMatch.Groups["currency"].Value);
                parsedUser = true;
            }

            var rangeMatch = Regex.Match(text, @"交易时间段：(?<start>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\s+至\s+(?<end>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "起始日期", rangeMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", rangeMatch.Groups["end"].Value);
                parsedUser = true;
            }

            var typeMatch = Regex.Match(text, @"交易类型：(?<type>.+)$");
            if (typeMatch.Success)
            {
                SetUserNamed(user, bank, "交易类型", typeMatch.Groups["type"].Value);
                parsedUser = true;
            }
        }

        foreach (var group in GroupAlipayRecords(lines))
        {
            if (TryParseAlipayRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else
            {
                AddParseWarning(result, group, "该支付宝流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseSpdbCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(180))
        {
            var text = CleanPdfValue(line.Text);
            var customerMatch = Regex.Match(text, @"客户名称\s+Customer\s+Name\s+(?<name>.+)$");
            if (customerMatch.Success)
            {
                SetUserNamed(user, bank, "客户户名", customerMatch.Groups["name"].Value);
                parsedUser = true;
            }

            var accountNameMatch = Regex.Match(text, @"^(?<name>.+?)账户名称\s+Account\s+Name$");
            if (accountNameMatch.Success)
            {
                SetUserNamed(user, bank, "客户户名", accountNameMatch.Groups["name"].Value);
                parsedUser = true;
            }

            var customerNumberMatch = Regex.Match(text, @"客户号\s+Customer\s+Number\s+(?<customer>\S+)");
            if (customerNumberMatch.Success)
            {
                user["客户号"] = CleanPdfValue(customerNumberMatch.Groups["customer"].Value);
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"账号\s+Account\s+Number\s+(?<account>\d+)");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                parsedUser = true;
            }

            var rangeMatch = Regex.Match(text, @"账单统计日期\s+Start\s+Time\s+&\s+End\s+Time\s+(?<start>\d{4}/\d{2}/\d{2})\s+-\s+(?<end>\d{4}/\d{2}/\d{2})\s+开户行\s+The\s+Bank\s+of\s+Account\s+Opening\s+(?<branch>.+?)(?:\s+Page\s+\d+\s+of\s+\d+)?$");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "起始日期", rangeMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", rangeMatch.Groups["end"].Value);
                SetUserNamed(user, bank, "开户行", rangeMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "年份", rangeMatch.Groups["start"].Value[..4]);
                parsedUser = true;
            }

            var currencyMatch = Regex.Match(text, @"账单币种\s+Currency\s+(?<currency>.+?)(?:\s+CNY)?$");
            if (currencyMatch.Success)
            {
                SetUserNamed(user, bank, "币种", NormalizeCurrencyText(currencyMatch.Groups["currency"].Value));
                parsedUser = true;
            }

            var openingBalanceMatch = Regex.Match(text, @"期初余额\s+Opening\s+Balance\s+(?<balance>[+-]?\d[\d,]*\.\d{2})");
            if (openingBalanceMatch.Success && decimal.TryParse(openingBalanceMatch.Groups["balance"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var openingBalance))
            {
                user.OpeningBalance = openingBalance;
                parsedUser = true;
            }
        }

        var positionedRecords = new List<FlowRecord>();
        foreach (var row in BuildPositionedRows(
            document.Words,
            SpdbCorporateColumns,
            IsSpdbCorporateHeaderWord,
            IsSpdbCorporateFooterWord,
            @"^\d{4}/\d{2}/\d{2}$",
            90,
            250))
        {
            if (TryParseSpdbCorporateRecord(row, bank, user, out var record))
            {
                positionedRecords.Add(record);
            }
        }

        var textRecords = new List<FlowRecord>();
        double? previousSpdbBalance = user.OpeningBalance != 0 ? (double)user.OpeningBalance : null;
        foreach (var group in GroupLinesByStart(lines, IsSpdbCorporateTextRecordStart, IsSpdbCorporateTextIgnoredLine))
        {
            if (TryParseSpdbCorporateTextRecord(group, bank, user, previousSpdbBalance, out var record))
            {
                textRecords.Add(record);
                previousSpdbBalance = record.Balance ?? previousSpdbBalance;
            }
        }

        if (textRecords.Count > positionedRecords.Count)
        {
            MergeSpdbCorporatePositionedRecords(textRecords, positionedRecords);
            result.FlowRecords.AddRange(textRecords);
        }
        else
        {
            result.FlowRecords.AddRange(positionedRecords);
        }

        InferSpdbCorporateMoneyDirections(result.FlowRecords);
        return parsedUser;
    }

    private static bool ParseCibCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(120))
        {
            var text = CleanPdfValue(line.Text);
            var rangeMatch = Regex.Match(text, @"打印日期:\s*(?<print>\d{4}-\d{2}-\d{2})\s+本期时间范围:\s*(?<start>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\s+至\s+(?<end>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "打印时间", rangeMatch.Groups["print"].Value);
                SetUserNamed(user, bank, "起始日期", rangeMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", rangeMatch.Groups["end"].Value);
                SetUserNamed(user, bank, "年份", rangeMatch.Groups["start"].Value[..4]);
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"活期账号:\s*(?<account>\d+)\s+币种:\s*(?<currency>\S+)");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "币种", NormalizeCurrencyText(accountMatch.Groups["currency"].Value));
                parsedUser = true;
            }

            var nameMatch = Regex.Match(text, @"户名:\s*(?<name>.+?)\s+开户行:\s*(?<branch>.+)$");
            if (nameMatch.Success)
            {
                SetUserNamed(user, bank, "户名", nameMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "开户行", nameMatch.Groups["branch"].Value);
                parsedUser = true;
            }
        }

        foreach (var group in GroupLinesByStart(lines, IsCibCorporateTextRecordStart, IsCibCorporateTextIgnoredLine))
        {
            if (TryParseCibCorporateTextRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else if (GroupContainsMoney(group))
            {
                AddParseWarning(result, group, "该兴业对公流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseCmbCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(120))
        {
            var text = CleanPdfValue(line.Text);
            var branchMatch = Regex.Match(text, @"开户银行:\s*(?<branch>.+?)\s+账单所属期间:\s*(?<start>\d{8})\s+(?<end>\d{8})");
            if (branchMatch.Success)
            {
                SetUserNamed(user, bank, "网点名称", branchMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "起始日期", FormatCompactDate(branchMatch.Groups["start"].Value));
                SetUserNamed(user, bank, "截止日期", FormatCompactDate(branchMatch.Groups["end"].Value));
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"账号:\s*(?<account>\d+)\s+货币:\s*(?<currency>\S+)");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "户口号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "账号序号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "币种", NormalizeCurrencyText(accountMatch.Groups["currency"].Value));
                parsedUser = true;
            }

            var nameMatch = Regex.Match(text, @"账户名称:\s*(?<name>.+?)\s+上页余额:\s*(?<balance>[+-]?\d[\d,]*\.\d{2})");
            if (nameMatch.Success)
            {
                SetUserNamed(user, bank, "户口名称", nameMatch.Groups["name"].Value);
                if (decimal.TryParse(nameMatch.Groups["balance"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var openingBalance))
                {
                    user.OpeningBalance = openingBalance;
                }

                parsedUser = true;
            }
        }

        foreach (var group in GroupCmbCorporateRecords(lines))
        {
            if (TryParseCmbCorporateRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else if (GroupContainsMoney(group))
            {
                AddParseWarning(result, group, "该招行对公流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseBocCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(160))
        {
            var text = CleanPdfValue(line.Text);
            var accountMatch = Regex.Match(text, @"账号\s+(?<account>\S+)\s+账户名称\s+(?<name>.+?)\s+开户行\s+(?<branch>.+?)\s+起始日期(?<start>\d{8})");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "账户名称", accountMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "开户行", accountMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "起始日期", FormatCompactDate(accountMatch.Groups["start"].Value));
                parsedUser = true;
            }

            var endMatch = Regex.Match(text, @"截止日期\s+(?<end>\d{8})");
            if (endMatch.Success)
            {
                SetUserNamed(user, bank, "截止日期", FormatCompactDate(endMatch.Groups["end"].Value));
                parsedUser = true;
            }

            var currencyMatch = Regex.Match(text, @"币种\s+(?<currency>\S+)");
            if (currencyMatch.Success)
            {
                SetUserNamed(user, bank, "币种", NormalizeCurrencyText(currencyMatch.Groups["currency"].Value));
                parsedUser = true;
            }

            var openingBalanceMatch = Regex.Match(text, @"承前页余额\s+(?<balance>[+-]?\d[\d,]*\.\d{2})");
            if (openingBalanceMatch.Success && decimal.TryParse(openingBalanceMatch.Groups["balance"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var openingBalance))
            {
                user.OpeningBalance = openingBalance;
                parsedUser = true;
            }
        }

        foreach (var row in BuildBocCorporateRows(lines))
        {
            if (TryParseBocCorporateRecord(row, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
        }

        return parsedUser;
    }

    private static bool ParseCiticCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(120))
        {
            var text = CleanPdfValue(line.Text);
            var rangeMatch = Regex.Match(text, @"查询周期：(?<start>\d{4}-\d{2}-\d{2})\s+[—-]+\s+(?<end>\d{4}-\d{2}-\d{2})\s+币种：(?<currency>\S+)\s+查询时间：(?<print>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "起始日期", rangeMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "截止日期", rangeMatch.Groups["end"].Value);
                SetUserNamed(user, bank, "币种", NormalizeCurrencyText(rangeMatch.Groups["currency"].Value));
                SetUserNamed(user, bank, "制表日期", rangeMatch.Groups["print"].Value);
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"账户：(?<account>[^/]+)/(?<name>.+)$");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "户名", accountMatch.Groups["name"].Value);
                parsedUser = true;
            }
        }

        foreach (var row in BuildPositionedRows(
            document.Words,
            CiticCorporateColumns,
            IsCiticCorporateHeaderWord,
            IsCiticCorporateFooterWord,
            @"^\d{4}-\d{2}-\d{2}$",
            90,
            168))
        {
            if (TryParseCiticCorporateRecord(row, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
        }

        return parsedUser;
    }

    private static bool ParseIcbcCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var text = CleanPdfValue(line.Text);
            var accountMatch = Regex.Match(text, @"账号：\s*(?<account>\S+)\s+币种：\s*(?<currency>\S+)");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "币种", NormalizeCurrencyText(accountMatch.Groups["currency"].Value));
                parsedUser = true;
            }

            var userMatch = Regex.Match(text, @"本方账号户名：\s*(?<name>.+?)\s+本方账号开户行：\s*(?<branch>.+?)\s+时间范围：\s*(?<start>\d{8})\s*-\s*(?<end>\d{8})");
            if (userMatch.Success)
            {
                SetUserNamed(user, bank, "户名", userMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "章内支行", userMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "开始日期", FormatCompactDate(userMatch.Groups["start"].Value));
                SetUserNamed(user, bank, "结束日期", FormatCompactDate(userMatch.Groups["end"].Value));
                SetUserNamed(user, bank, "交易日期", FormatCompactDate(userMatch.Groups["start"].Value));
                parsedUser = true;
            }
        }

        foreach (var row in BuildPositionedRowsWithTopLead(
            document.Words,
            IcbcCorporateColumns,
            IsIcbcCorporateHeaderWord,
            IsIcbcCorporateFooterWord,
            @"^\d{6,}$",
            112,
            65,
            12))
        {
            if (TryParseIcbcCorporateRecord(row, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
        }

        return parsedUser;
    }

    private static bool ParseEverbrightCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var text = CleanPdfValue(line.Text);
            var rangeMatch = Regex.Match(text, @"查询日期：(?<print>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})\s+交易日期：(?<start>\d{8})-(?<end>\d{8})");
            if (rangeMatch.Success)
            {
                SetUserNamed(user, bank, "打印时间", rangeMatch.Groups["print"].Value);
                SetUserNamed(user, bank, "起始日期", FormatCompactDate(rangeMatch.Groups["start"].Value));
                SetUserNamed(user, bank, "结束日期", FormatCompactDate(rangeMatch.Groups["end"].Value));
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"账号：(?<account>\S+)\s+账户名称：(?<name>.+)$");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "账户名", accountMatch.Groups["name"].Value);
                parsedUser = true;
            }
        }

        foreach (var group in GroupEverbrightCorporateRecords(lines))
        {
            if (TryParseEverbrightCorporateRecord(group, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
            else if (GroupContainsMoney(group))
            {
                AddParseWarning(result, group, "该光大对公流水行未能按专用模板解析。");
            }
        }

        return parsedUser;
    }

    private static bool ParseCcbCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(80))
        {
            var text = CleanPdfValue(line.Text);
            var headerMatch = Regex.Match(text, @"本方户名：(?<name>.+?)\s+查询时间段：(?<start>\d{8})\s*-\s*(?<end>\d{8})\s+打印日期：(?<print>.+)$");
            if (headerMatch.Success)
            {
                SetUserNamed(user, bank, "账户名称", headerMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "开始日期", FormatCompactDate(headerMatch.Groups["start"].Value));
                SetUserNamed(user, bank, "结束日期", FormatCompactDate(headerMatch.Groups["end"].Value));
                SetUserNamed(user, bank, "打印日期", headerMatch.Groups["print"].Value);
                parsedUser = true;
            }
        }

        foreach (var row in BuildPositionedRowsWithTopLead(
            document.Words,
            CcbCorporateColumns,
            IsCcbCorporateHeaderWord,
            IsCcbCorporateFooterWord,
            @"^\d{8}$",
            45,
            98,
            10))
        {
            if (TryParseCcbCorporateRecord(row, bank, user, out var record))
            {
                if (string.IsNullOrWhiteSpace(user.AccountNo) && !string.IsNullOrWhiteSpace(record.Account))
                {
                    SetUserNamed(user, bank, "账号", record.Account);
                }

                result.FlowRecords.Add(record);
            }
        }

        return parsedUser;
    }

    private static bool ParseBocomCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        var bocomYear = string.Empty;
        var bocomMonth = string.Empty;
        for (var index = 0; index < Math.Min(lines.Count, 90); index++)
        {
            var line = lines[index];
            var text = CleanPdfValue(line.Text);
            var branchMatch = Regex.Match(text, @"开户机构：(?<branch>.+?)\s+币种：(?<currency>\S+)\s+年份：(?<year>\d{4})\s+月份：(?<month>\d{2})");
            if (branchMatch.Success)
            {
                SetUserNamed(user, bank, "支行名称", branchMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "分行", branchMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "年份", branchMatch.Groups["year"].Value);
                SetUserNamed(user, bank, "开始日期", $"{branchMatch.Groups["year"].Value}-{branchMatch.Groups["month"].Value}-01");
                SetUserNamed(user, bank, "结束日期", $"{branchMatch.Groups["year"].Value}-{branchMatch.Groups["month"].Value}-01");
                SetUserNamed(user, bank, "机构", "交通银行");
                user.Currency = NormalizeCurrencyText(branchMatch.Groups["currency"].Value);
                parsedUser = true;
            }

            if (text.StartsWith("开户机构：", StringComparison.Ordinal))
            {
                var branch = CleanPdfValue(text["开户机构：".Length..]);
                SetUserNamed(user, bank, "支行名称", branch);
                SetUserNamed(user, bank, "分行", branch);
                user["开户机构"] = branch;
                parsedUser = true;
            }

            var accountMatch = Regex.Match(text, @"账号：\s*(?<account>\S+)\s+户名：\s*(?<name>.+)$");
            if (accountMatch.Success)
            {
                SetUserNamed(user, bank, "账号", accountMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "单位名称", accountMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "存款人名称", accountMatch.Groups["name"].Value);
                user.AccountNo = CleanPdfValue(accountMatch.Groups["account"].Value);
                user.AccountName = CleanPdfValue(accountMatch.Groups["name"].Value);
                parsedUser = true;
            }

            if (text == "账号：" && index + 1 < lines.Count)
            {
                for (var nextIndex = index + 1; nextIndex < Math.Min(lines.Count, index + 5); nextIndex++)
                {
                    var nextText = CleanPdfValue(lines[nextIndex].Text);
                    var nextAccountMatch = Regex.Match(nextText, @"^(?<account>\d+)\s+(?<name>.+)$");
                    if (!nextAccountMatch.Success)
                    {
                        continue;
                    }

                    SetUserNamed(user, bank, "账号", nextAccountMatch.Groups["account"].Value);
                    SetUserNamed(user, bank, "单位名称", nextAccountMatch.Groups["name"].Value);
                    SetUserNamed(user, bank, "存款人名称", nextAccountMatch.Groups["name"].Value);
                    user.AccountNo = CleanPdfValue(nextAccountMatch.Groups["account"].Value);
                    user.AccountName = CleanPdfValue(nextAccountMatch.Groups["name"].Value);
                    parsedUser = true;
                    break;
                }
            }

            var yearMatch = Regex.Match(text, @"(?<year>20\d{2})\s*(?<currency>人民币|RMB)");
            if (yearMatch.Success)
            {
                bocomYear = yearMatch.Groups["year"].Value;
                SetUserNamed(user, bank, "年份", bocomYear);
                user.Currency = NormalizeCurrencyText(yearMatch.Groups["currency"].Value);
                parsedUser = true;
            }

            var monthMatch = Regex.Match(text, @"(?<month>\d{2})月份：?");
            if (monthMatch.Success)
            {
                bocomMonth = monthMatch.Groups["month"].Value;
                parsedUser = true;
            }
        }

        if (Regex.IsMatch(bocomYear, @"^\d{4}$") && Regex.IsMatch(bocomMonth, @"^\d{2}$"))
        {
            var startText = $"{bocomYear}-{bocomMonth}-01";
            SetUserNamed(user, bank, "开始日期", startText);
            if (DateTime.TryParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
            {
                SetUserNamed(user, bank, "结束日期", startDate.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }

        foreach (var row in BuildPositionedRowsWithTopLead(
            document.Words,
            BocomCorporateColumns,
            IsBocomCorporateHeaderWord,
            IsBocomCorporateFooterWord,
            @"^\d+$",
            42,
            126,
            8))
        {
            if (TryParseBocomCorporateRecord(row, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
        }

        return parsedUser;
    }

    private static bool ParseCmbcCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(90))
        {
            var text = CleanPdfValue(line.Text);
            var printMatch = Regex.Match(text, @"打印渠道:(?<channel>\S+)\s+打印柜员:\s*(?<operator>\S*)\s+打印时间:(?<print>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
            if (printMatch.Success)
            {
                SetUserNamed(user, bank, "打印日期", printMatch.Groups["print"].Value);
                SetUserNamed(user, bank, "打印柜员", printMatch.Groups["operator"].Value);
                user["打印渠道"] = CleanPdfValue(printMatch.Groups["channel"].Value);
                parsedUser = true;
            }

            var customerMatch = Regex.Match(text, @"客户名称:\s*(?<name>.+?)\s+客户账号:(?<account>\S+)\s+币种:(?<currency>\S+)");
            if (customerMatch.Success)
            {
                SetUserNamed(user, bank, "客户名称", customerMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "客户账号", customerMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "账户账号", customerMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "币种", NormalizeCurrencyText(customerMatch.Groups["currency"].Value));
                parsedUser = true;
            }

            var branchMatch = Regex.Match(text, @"开户机构:\s*(?<branch>.+?)\s+起止日期:(?<start>\d{4}/\d{2}/\d{2})-(?<end>\d{4}/\d{2}/\d{2})");
            if (branchMatch.Success)
            {
                SetUserNamed(user, bank, "开户机构", branchMatch.Groups["branch"].Value);
                SetUserNamed(user, bank, "起始日期", branchMatch.Groups["start"].Value);
                SetUserNamed(user, bank, "终止日期", branchMatch.Groups["end"].Value);
                parsedUser = true;
            }
        }

        foreach (var row in BuildPositionedRowsWithTopLead(
            document.Words,
            CmbcCorporateColumns,
            IsCmbcCorporateHeaderWord,
            IsCmbcCorporateFooterWord,
            @"^\d{4}/\d{2}/\d{2}$",
            62,
            104,
            4))
        {
            if (TryParseCmbcCorporateRecord(row, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
        }

        return parsedUser;
    }

    private static bool ParseAbcCorporatePdf(Bank bank, BankUser user, PdfExtractedDocument document, PdfImportResult result)
    {
        var parsedUser = false;
        var lines = document.Lines.ToList();
        foreach (var line in lines.Take(60))
        {
            var text = CleanPdfValue(line.Text);
            var headerMatch = Regex.Match(text, @"账号:(?<account>\S+)\s+户名:(?<name>.+?)\s+币种:(?<currency>\S+)\s+起止日期:\s*(?<start>\d{4}年\d{2}月\d{2}日)\s*-\s*(?<end>\d{4}年\d{2}月\d{2}日)");
            if (headerMatch.Success)
            {
                SetUserNamed(user, bank, "账号", headerMatch.Groups["account"].Value);
                SetUserNamed(user, bank, "户名", headerMatch.Groups["name"].Value);
                SetUserNamed(user, bank, "币种", NormalizeCurrencyText(headerMatch.Groups["currency"].Value));
                SetUserNamed(user, bank, "起始日期", NormalizeChineseDate(headerMatch.Groups["start"].Value));
                SetUserNamed(user, bank, "终止日期", NormalizeChineseDate(headerMatch.Groups["end"].Value));
                parsedUser = true;
            }
        }

        foreach (var row in BuildPositionedRowsWithTopLead(
            document.Words,
            AbcCorporateColumns,
            IsAbcCorporateHeaderWord,
            IsAbcCorporateFooterWord,
            @"^\d{4}-\d{2}-\d{2}$",
            82,
            72,
            14))
        {
            if (TryParseAbcCorporateRecord(row, bank, user, out var record))
            {
                result.FlowRecords.Add(record);
            }
        }

        return parsedUser;
    }

    private static bool TryParseIcbcCorporateRecord(
        PdfPositionedRow row,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var date = GetPositionedCell(row, "Date");
        var time = GetPositionedCell(row, "Time");
        var amountText = GetPositionedCell(row, "Amount");
        var balanceText = GetPositionedCell(row, "Balance");
        var direction = GetPositionedCell(row, "Direction");
        if (!Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$")
            || !Regex.IsMatch(time, @"^\d{2}:\d{2}:\d{2}$")
            || string.IsNullOrWhiteSpace(amountText))
        {
            return false;
        }

        var summary = FirstNotBlank(GetPositionedCell(row, "Summary"), GetPositionedCell(row, "Usage"), GetPositionedCell(row, "Remark"));
        var usage = GetPositionedCell(row, "Usage");
        var remark = GetPositionedCell(row, "Remark");
        var oppositeName = GetPositionedCell(row, "OppositeName");
        var oppositeAccount = GetPositionedCell(row, "OppositeAccount");
        var oppositeBankNo = GetPositionedCell(row, "OppositeBankNo");

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull($"{date} {time}");
        record.ProductType = summary;
        record.ProductName = summary;
        record.ProductBrief = summary;
        record.Usage = usage;
        record.Remark = remark;
        record.TradeExplain = remark;
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        record.OppositeBank = oppositeBankNo;
        record.NetNum = oppositeBankNo;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        ApplyCorporateDebitCredit(
            record,
            direction.Contains("借", StringComparison.Ordinal) ? amountText : string.Empty,
            direction.Contains("贷", StringComparison.Ordinal) ? amountText : string.Empty,
            "借方发生额",
            "贷方发生额",
            direction);

        SetFlowRaw(record, "日期", $"{date} {time}");
        SetFlowRaw(record, "交易时间", $"{date} {time}");
        SetFlowRaw(record, "借贷标志", direction);
        SetFlowRaw(record, "业务产品种类", summary);
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "记账信息", GetPositionedCell(row, "PostingDate"));
        SetFlowRaw(record, "入账日期", GetPositionedCell(row, "PostingDate"));
        SetFlowRaw(record, "对方户名", oppositeName);
        SetFlowRaw(record, "对方单位", oppositeName);
        SetFlowRaw(record, "对方账号", oppositeAccount);
        SetFlowRaw(record, "对方行号", oppositeBankNo);
        SetFlowRaw(record, "用途", usage);
        SetFlowRaw(record, "附言", remark);
        SetFlowRaw(record, "截止可用余额", GetPositionedCell(row, "AvailableBalance"));
        return true;
    }

    private static IReadOnlyList<IReadOnlyList<PdfTextLine>> GroupEverbrightCorporateRecords(IReadOnlyList<PdfTextLine> lines)
    {
        var result = new List<IReadOnlyList<PdfTextLine>>();
        var current = new List<PdfTextLine>();
        foreach (var line in lines)
        {
            var text = CleanPdfValue(line.Text);
            if (IsEverbrightCorporateIgnoredLine(text))
            {
                continue;
            }

            if (Regex.IsMatch(text, @"^\d+\s+\d{8}\s+\d{6}\s+(?:借方|贷方)\s+[-+]?\d[\d,]*\.\d{2}\s+[-+]?\d[\d,]*\.\d{2}"))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                }

                current = [line];
                continue;
            }

            if (current.Count > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static bool TryParseEverbrightCorporateRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        if (group.Count == 0)
        {
            return false;
        }

        var firstLine = CleanPdfValue(group[0].Text);
        var match = Regex.Match(
            firstLine,
            @"^(?<index>\d+)\s+(?<date>\d{8})\s+(?<time>\d{6})\s+(?<direction>借方|贷方)\s+(?<amount>[-+]?\d[\d,]*\.\d{2})\s+(?<balance>[-+]?\d[\d,]*\.\d{2})(?:\s+(?<tail>.*))?$");
        if (!match.Success)
        {
            return false;
        }

        var parts = new List<string>();
        AddEverbrightCorporatePart(parts, match.Groups["tail"].Value);
        parts.AddRange(group.Skip(1).Select(item => CleanPdfValue(item.Text)).Where(item => !string.IsNullOrWhiteSpace(item)));

        var oppositeAccountParts = new List<string>();
        while (parts.Count > 0 && TryTakeLeadingEverbrightNumber(parts[0], oppositeAccountParts.Count > 0, out var numberPart, out var remainder))
        {
            oppositeAccountParts.Add(numberPart);
            parts.RemoveAt(0);
            AddEverbrightCorporatePart(parts, remainder, insertAtStart: true);
        }

        var voucher = string.Empty;
        var summary = string.Empty;
        var serial = string.Empty;
        var oppositeName = string.Empty;
        var voucherIndex = parts.FindIndex(item => Regex.IsMatch(item, @"^\d{3}\s+.+\s+[0-9A-Za-z]{5,}$"));
        if (voucherIndex >= 0)
        {
            var voucherLine = parts[voucherIndex];
            var voucherMatch = Regex.Match(voucherLine, @"^(?<voucher>\d{3})\s+(?<summary>.+?)\s+(?<serial>[0-9A-Za-z]{5,})$");
            if (voucherMatch.Success)
            {
                voucher = voucherMatch.Groups["voucher"].Value;
                summary = voucherMatch.Groups["summary"].Value;
                serial = voucherMatch.Groups["serial"].Value;
            }

            foreach (var tail in parts.Skip(voucherIndex + 1))
            {
                if (Regex.IsMatch(tail, @"^[0-9A-Za-z]{4,}$"))
                {
                    serial += tail;
                }
                else
                {
                    summary = AppendPdfCellText(summary, tail);
                }
            }

            oppositeName = CollapseChineseSeparatedWords(string.Join(' ', parts.Take(voucherIndex)));
        }
        else
        {
            while (parts.Count > 0 && Regex.IsMatch(parts[^1], @"^[0-9A-Za-z]{4,}$"))
            {
                serial = string.Concat(parts[^1], serial);
                parts.RemoveAt(parts.Count - 1);
            }

            var summaryLineIndex = parts.FindLastIndex(item => Regex.IsMatch(item, @"^.+\s+[0-9A-Za-z]{5,}$"));
            if (summaryLineIndex >= 0)
            {
                var summaryLineMatch = Regex.Match(parts[summaryLineIndex], @"^(?<before>.+?)\s+(?<serial>[0-9A-Za-z]{5,})$");
                if (summaryLineMatch.Success)
                {
                    serial = string.Concat(summaryLineMatch.Groups["serial"].Value, serial);
                    var before = CleanPdfValue(summaryLineMatch.Groups["before"].Value);
                    var nameParts = parts.Take(summaryLineIndex).ToList();
                    if (nameParts.Count == 0)
                    {
                        var beforeTokens = SplitWords(before);
                        if (beforeTokens.Count >= 2)
                        {
                            oppositeName = beforeTokens[0];
                            summary = CollapseChineseSeparatedWords(string.Join(' ', beforeTokens.Skip(1)));
                        }
                        else
                        {
                            summary = before;
                        }
                    }
                    else
                    {
                        oppositeName = CollapseChineseSeparatedWords(string.Join(' ', nameParts));
                        summary = before;
                    }

                    parts = [];
                }
            }

            if (parts.Count > 0)
            {
                oppositeName = parts[0];
                summary = CollapseChineseSeparatedWords(string.Join(' ', parts.Skip(1)));
            }
        }

        var date = FormatCompactDate(match.Groups["date"].Value);
        var time = FormatCompactTime(match.Groups["time"].Value);
        var direction = match.Groups["direction"].Value;
        var amountText = match.Groups["amount"].Value;
        var balanceText = match.Groups["balance"].Value;
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull($"{date} {time}");
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.Remark = summary;
        record.TradeExplain = summary;
        record.VoucherNum = voucher;
        record.SerialNum = serial;
        record.LogNum = serial;
        record.OppositeAccount = CleanPdfValue(string.Concat(oppositeAccountParts));
        record.OppositeUsername = CollapseChineseSeparatedWords(oppositeName);
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        ApplyCorporateDebitCredit(
            record,
            direction.Contains("借", StringComparison.Ordinal) ? amountText : string.Empty,
            direction.Contains("贷", StringComparison.Ordinal) ? amountText : string.Empty,
            "借方发生额",
            "贷方发生额",
            direction);

        SetFlowRaw(record, "序号", match.Groups["index"].Value);
        SetFlowRaw(record, "交易日期", date);
        SetFlowRaw(record, "时间", time);
        SetFlowRaw(record, "借/贷", direction);
        SetFlowRaw(record, "交易金额", amountText);
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "账户余额", balanceText);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对方名称", record.OppositeUsername);
        SetFlowRaw(record, "凭证号", voucher);
        SetFlowRaw(record, "凭证序号", voucher);
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "流水号", serial);
        SetFlowRaw(record, "交易柜员号", serial);
        return true;
    }

    private static bool TryParseCcbCorporateRecord(
        PdfPositionedRow row,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var account = GetPositionedCell(row, "Account");
        var dateTime = NormalizeCompactDateTimeCell(GetPositionedCell(row, "DateTime"));
        var debitText = GetPositionedCell(row, "Debit");
        var creditText = GetPositionedCell(row, "Credit");
        var balanceText = GetPositionedCell(row, "Balance");
        if (string.IsNullOrWhiteSpace(account)
            || !Regex.IsMatch(dateTime, @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}$")
            || (string.IsNullOrWhiteSpace(debitText) && string.IsNullOrWhiteSpace(creditText)))
        {
            return false;
        }

        var summary = GetPositionedCell(row, "Summary");
        var remark = GetPositionedCell(row, "Remark");
        var serial = GetPositionedCell(row, "Serial");
        var voucherType = GetPositionedCell(row, "VoucherType");
        var voucherNum = GetPositionedCell(row, "VoucherNum");
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = account;
        record.AccountTime = ParseDateTimeOrNull(dateTime);
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.ProductType = summary;
        record.Remark = remark;
        record.TradeExplain = remark;
        record.SerialNum = serial;
        record.LogNum = serial;
        record.SequenceNum = serial;
        record.VoucherType = voucherType;
        record.VoucherNum = voucherNum;
        record.Currency = NormalizeCurrencyText(FirstNotBlank(GetPositionedCell(row, "Currency"), user.Currency, "人民币"));
        record.OppositeUsername = GetPositionedCell(row, "OppositeName");
        record.OppositeAccount = GetPositionedCell(row, "OppositeAccount");
        record.OppositeBank = GetPositionedCell(row, "OppositeBank");
        record.TradeChannel = GetPositionedCell(row, "Medium");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        ApplyCorporateDebitCredit(record, debitText, creditText, "借方", "贷方");

        SetFlowRaw(record, "日期", dateTime);
        SetFlowRaw(record, "交易时间", dateTime);
        SetFlowRaw(record, "记账日期", GetPositionedCell(row, "PostingDate"));
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "备注", remark);
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "交易流水号", serial);
        SetFlowRaw(record, "账户明细编号-交易流水号", serial);
        SetFlowRaw(record, "企业流水号", GetPositionedCell(row, "EnterpriseSerial"));
        SetFlowRaw(record, "凭证种类", voucherType);
        SetFlowRaw(record, "凭证号码", voucherNum);
        SetFlowRaw(record, "凭证号", voucherNum);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方行名", record.OppositeBank);
        SetFlowRaw(record, "对方开户机构", record.OppositeBank);
        SetFlowRaw(record, "交易渠道", record.TradeChannel);
        SetFlowRaw(record, "交易介质编号", record.TradeChannel);
        return true;
    }

    private static bool TryParseBocomCorporateRecord(
        PdfPositionedRow row,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var index = GetPositionedCell(row, "Index");
        var accountingDate = FormatCompactDate(GetPositionedCell(row, "AccountingDate"));
        var tradeDate = FormatCompactDate(GetPositionedCell(row, "TradeDate"));
        var debitText = GetPositionedCell(row, "Debit");
        var creditText = GetPositionedCell(row, "Credit");
        var balanceText = GetPositionedCell(row, "Balance");
        if (!Regex.IsMatch(index, @"^\d+$")
            || !Regex.IsMatch(tradeDate, @"^\d{4}-\d{2}-\d{2}$")
            || (string.IsNullOrWhiteSpace(debitText) && string.IsNullOrWhiteSpace(creditText)))
        {
            return false;
        }

        var tradeName = GetPositionedCell(row, "TradeName");
        var summary = GetPositionedCell(row, "Summary");
        var serial = GetPositionedCell(row, "Serial");
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(tradeDate);
        record.ProductName = tradeName;
        record.ProductBrief = FirstNotBlank(summary, tradeName);
        record.ProductType = tradeName;
        record.VoucherType = GetPositionedCell(row, "VoucherType");
        record.VoucherNum = GetPositionedCell(row, "VoucherNum");
        record.SerialNum = serial;
        record.LogNum = serial;
        record.OppositeAccount = GetPositionedCell(row, "OppositeAccount");
        record.OppositeUsername = GetPositionedCell(row, "OppositeName");
        record.OppositeBank = GetPositionedCell(row, "OppositeBank");
        record.AccountNum = GetPositionedCell(row, "CardNo");
        record.TradePlace = GetPositionedCell(row, "TradePlace");
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        ApplyCorporateDebitCredit(record, debitText, creditText, "借方", "贷方");

        SetFlowRaw(record, "序号", index);
        SetFlowRaw(record, "日期", tradeDate);
        SetFlowRaw(record, "会计日期", accountingDate);
        SetFlowRaw(record, "交易日期", tradeDate);
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "凭证种类", record.VoucherType);
        SetFlowRaw(record, "凭证号码", record.VoucherNum);
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "流水号", serial);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "交易名称", tradeName);
        SetFlowRaw(record, "卡号", record.AccountNum);
        SetFlowRaw(record, "交易地点", record.TradePlace);
        SetFlowRaw(record, "对方行名", record.OppositeBank);
        return true;
    }

    private static bool TryParseCmbcCorporateRecord(
        PdfPositionedRow row,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var dateTime = NormalizeSlashDateTimeCell(GetPositionedCell(row, "DateTime"));
        var debitText = GetPositionedCell(row, "Debit");
        var creditText = GetPositionedCell(row, "Credit");
        var balanceText = GetPositionedCell(row, "Balance");
        if (!Regex.IsMatch(dateTime, @"^\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}$")
            || (string.IsNullOrWhiteSpace(debitText) && string.IsNullOrWhiteSpace(creditText)))
        {
            return false;
        }

        var counterparty = GetPositionedCell(row, "Counterparty");
        var (oppositeName, oppositeAccount) = SplitCounterpartyNameAndAccount(counterparty);
        var summary = GetPositionedCell(row, "Summary");
        var serial = GetPositionedCell(row, "Serial");
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(dateTime);
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.ProductType = summary;
        record.VoucherType = GetPositionedCell(row, "VoucherType");
        record.VoucherNum = GetPositionedCell(row, "VoucherNum");
        record.SerialNum = serial;
        record.LogNum = serial;
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        record.OppositeBank = GetPositionedCell(row, "OppositeBank");
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        ApplyCorporateDebitCredit(record, debitText, creditText, "借方发生额", "贷方发生额");

        SetFlowRaw(record, "交易日期", dateTime);
        SetFlowRaw(record, "交易时间", dateTime);
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "凭证类型", record.VoucherType);
        SetFlowRaw(record, "凭证号码", record.VoucherNum);
        SetFlowRaw(record, "交易金额", FormatMoney(Math.Abs(record.TradeMoney ?? 0)));
        SetFlowRaw(record, "账户余额", balanceText);
        SetFlowRaw(record, "流水号", serial);
        SetFlowRaw(record, "对方帐号", oppositeAccount);
        SetFlowRaw(record, "对方账号", oppositeAccount);
        SetFlowRaw(record, "对方户名", oppositeName);
        SetFlowRaw(record, "对方户名/账号", counterparty);
        SetFlowRaw(record, "对方行名", record.OppositeBank);
        return true;
    }

    private static bool TryParseAbcCorporateRecord(
        PdfPositionedRow row,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var dateTime = NormalizeDashDateTimeCell(GetPositionedCell(row, "DateTime"));
        var incomeText = GetPositionedCell(row, "Income");
        var expenseText = GetPositionedCell(row, "Expense");
        var balanceText = GetPositionedCell(row, "Balance");
        if (!Regex.IsMatch(dateTime, @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}$")
            || (string.IsNullOrWhiteSpace(incomeText) && string.IsNullOrWhiteSpace(expenseText)))
        {
            return false;
        }

        var summary = GetPositionedCell(row, "Summary");
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(dateTime);
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.ProductType = summary;
        record.Remark = summary;
        record.TradeExplain = summary;
        record.OppositeAccount = GetPositionedCell(row, "OppositeAccount");
        record.OppositeUsername = GetPositionedCell(row, "OppositeName");
        record.OppositeBank = GetPositionedCell(row, "OppositeBank");
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        ApplyCorporateDebitCredit(record, expenseText, incomeText, "支出金额", "收入金额");

        SetFlowRaw(record, "日期", dateTime);
        SetFlowRaw(record, "交易时间", dateTime);
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "交易金额", FormatMoney(Math.Abs(record.TradeMoney ?? 0)));
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "附言", summary);
        SetFlowRaw(record, "对方名称", record.OppositeUsername);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "交易对手账号", record.OppositeAccount);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方开户行", record.OppositeBank);
        return true;
    }

    private static IReadOnlyList<PdfPositionedRow> BuildPositionedRowsWithTopLead(
        IReadOnlyList<PdfTextWord> words,
        IReadOnlyList<PdfPositionedColumnSpec> columns,
        Func<PdfTextWord, bool> isHeaderWord,
        Func<string, bool> isFooterWord,
        string rowStartPattern,
        double rowStartLeftMax,
        double defaultHeaderBottom,
        double rowTopLead)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var rows = new List<PdfPositionedRow>();
        foreach (var pageGroup in words.GroupBy(item => item.PageNumber).OrderBy(group => group.Key))
        {
            var pageWords = pageGroup
                .Select(item => item with { Text = CleanPdfValue(item.Text) })
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .OrderBy(item => item.Top)
                .ThenBy(item => item.Left)
                .ToList();

            var headerBottom = Math.Max(
                defaultHeaderBottom,
                pageWords
                    .Where(isHeaderWord)
                    .Select(item => item.Bottom)
                    .DefaultIfEmpty(defaultHeaderBottom)
                    .Max()) + 0.5;
            var footerTop = pageWords
                .Where(item => item.Top > headerBottom && isFooterWord(item.Text))
                .Select(item => item.Top)
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            var rowStartWords = pageWords
                .Where(item => item.Top > headerBottom
                    && item.Top < footerTop
                    && item.Left < rowStartLeftMax
                    && Regex.IsMatch(item.Text, rowStartPattern))
                .OrderBy(item => item.Top)
                .ThenBy(item => item.Left)
                .ToList();
            var minimumStartGap = Math.Max(6d, rowTopLead * 1.5d);
            rowStartWords = rowStartWords
                .Aggregate(new List<PdfTextWord>(), (items, word) =>
                {
                    if (items.Count == 0 || word.Top - items[^1].Top >= minimumStartGap)
                    {
                        items.Add(word);
                    }

                    return items;
                });

            for (var index = 0; index < rowStartWords.Count; index++)
            {
                var currentStart = rowStartWords[index];
                var bandTop = Math.Max(headerBottom, currentStart.Top - rowTopLead);
                var bandBottom = index + 1 < rowStartWords.Count
                    ? Math.Max(currentStart.Top, rowStartWords[index + 1].Top - rowTopLead)
                    : footerTop;

                var rowWords = pageWords
                    .Where(item => item.Top >= bandTop && item.Top < bandBottom)
                    .OrderBy(item => item.Top)
                    .ThenBy(item => item.Left)
                    .ToList();
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var column in columns)
                {
                    var columnWords = rowWords
                        .Where(item => GetHorizontalCenter(item) >= column.Left && GetHorizontalCenter(item) < column.Right)
                        .ToList();
                    var value = JoinPositionedCellWords(columnWords);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        cells[column.Key] = value;
                    }
                }

                rows.Add(new PdfPositionedRow(pageGroup.Key, currentStart.Top, cells));
            }
        }

        return rows;
    }

    private static bool IsIcbcCorporateHeaderWord(PdfTextWord word)
    {
        return word.Text is "对方账号" or "交易时间" or "借贷标志" or "对方单位" or "对方行号" or "用途" or "摘要" or "附言" or "余额" or "发生额" or "入账日期" or "可用额度";
    }

    private static bool IsIcbcCorporateFooterWord(string text)
    {
        return text.StartsWith("重要提示", StringComparison.Ordinal)
            || text.StartsWith("若与实际交易不符", StringComparison.Ordinal)
            || text.StartsWith("第", StringComparison.Ordinal);
    }

    private static bool IsEverbrightCorporateIgnoredLine(string text)
    {
        var value = CleanPdfValue(text);
        return IsCommonIgnoredLine(value)
            || value.Contains("中国光大银行对公账户对账单", StringComparison.Ordinal)
            || value.StartsWith("查询日期：", StringComparison.Ordinal)
            || value.StartsWith("账号：", StringComparison.Ordinal)
            || value.StartsWith("借方发生额：", StringComparison.Ordinal)
            || value.StartsWith("借方笔数：", StringComparison.Ordinal)
            || value.StartsWith("银行盖章", StringComparison.Ordinal)
            || value.StartsWith("序号 交易日期", StringComparison.Ordinal)
            || value.StartsWith("第", StringComparison.Ordinal);
    }

    private static bool IsCcbCorporateHeaderWord(PdfTextWord word)
    {
        return word.Text is "账号" or "交易时" or "借方发" or "贷方发" or "余额" or "币种" or "对方户" or "对方账" or "对方开" or "记账日" or "摘要" or "备注" or "账户明" or "企业流" or "凭证种" or "凭证号" or "交易介";
    }

    private static bool IsCcbCorporateFooterWord(string text)
    {
        return text.StartsWith("第", StringComparison.Ordinal)
            || text.StartsWith("中国建设银行", StringComparison.Ordinal)
            || text.StartsWith("温馨提示", StringComparison.Ordinal);
    }

    private static bool IsBocomCorporateHeaderWord(PdfTextWord word)
    {
        return word.Text is "序号" or "会计日期" or "交易日期" or "交易名称" or "凭证种类" or "凭证号码" or "借方发生额" or "贷方发生额" or "余额" or "卡号" or "交易地点" or "对方账号" or "对方户名" or "对方行名" or "摘要" or "流水号";
    }

    private static bool IsBocomCorporateFooterWord(string text)
    {
        return text.StartsWith("本月第", StringComparison.Ordinal)
            || text.StartsWith("第", StringComparison.Ordinal)
            || text.StartsWith("交通银行重庆市分行", StringComparison.Ordinal);
    }

    private static bool IsCmbcCorporateHeaderWord(PdfTextWord word)
    {
        return word.Text is "交易时间" or "摘要" or "凭证类型" or "凭证号码" or "借方发生额" or "贷方发生额" or "账户余额" or "流水号" or "对方户名/账号" or "对方行名";
    }

    private static bool IsCmbcCorporateFooterWord(string text)
    {
        return text.StartsWith("打印渠道:", StringComparison.Ordinal)
            || text.StartsWith("单位账户对账单", StringComparison.Ordinal)
            || text.StartsWith("客户名称:", StringComparison.Ordinal)
            || text.StartsWith("开户机构:", StringComparison.Ordinal)
            || text.StartsWith("第", StringComparison.Ordinal)
            || text.StartsWith("__", StringComparison.Ordinal);
    }

    private static bool IsAbcCorporateHeaderWord(PdfTextWord word)
    {
        return word.Text is "交易时间" or "收入金额" or "支出金额" or "账户余额" or "对方账号" or "对方户名" or "对方开户行" or "摘要";
    }

    private static bool IsAbcCorporateFooterWord(string text)
    {
        return text.StartsWith("第", StringComparison.Ordinal)
            || text.Contains("账户明细", StringComparison.Ordinal)
            || text.StartsWith("账号:", StringComparison.Ordinal);
    }

    private static void AddEverbrightCorporatePart(IList<string> parts, string value, bool insertAtStart = false)
    {
        var cleaned = CleanPdfValue(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        if (insertAtStart)
        {
            parts.Insert(0, cleaned);
        }
        else
        {
            parts.Add(cleaned);
        }
    }

    private static bool TryTakeLeadingEverbrightNumber(string value, bool allowShortContinuation, out string numberPart, out string remainder)
    {
        numberPart = string.Empty;
        remainder = string.Empty;
        var text = CleanPdfValue(value);
        var match = Regex.Match(text, @"^(?<number>\d+)(?:\s+(?<rest>.*))?$");
        if (!match.Success)
        {
            return false;
        }

        if (match.Groups["number"].Value.Length < 3 && !allowShortContinuation)
        {
            return false;
        }

        numberPart = match.Groups["number"].Value;
        remainder = match.Groups["rest"].Value;
        return true;
    }

    private static string FormatCompactTime(string value)
    {
        var digits = Regex.Replace(CleanPdfValue(value), @"\D", string.Empty);
        return digits.Length >= 6
            ? $"{digits[..2]}:{digits.Substring(2, 2)}:{digits.Substring(4, 2)}"
            : CleanPdfValue(value);
    }

    private static string NormalizeCompactDateTimeCell(string value)
    {
        var text = CleanPdfValue(value);
        var digits = Regex.Replace(text, @"[^\d]", string.Empty);
        if (digits.Length < 14)
        {
            return text;
        }

        return $"{FormatCompactDate(digits[..8])} {FormatCompactTime(digits.Substring(8, 6))}";
    }

    private static string NormalizeSlashDateTimeCell(string value)
    {
        var text = CleanPdfValue(value);
        var match = Regex.Match(text, @"(?<date>\d{4}/\d{2}/\d{2})(?<time>\d{2}:\d{2}:\d{2})");
        return match.Success ? $"{match.Groups["date"].Value} {match.Groups["time"].Value}" : text;
    }

    private static string NormalizeDashDateTimeCell(string value)
    {
        var text = CleanPdfValue(value);
        var match = Regex.Match(text, @"(?<date>\d{4}-\d{2}-\d{2})(?<time>\d{2}:\d{2}:\d{2})");
        return match.Success ? $"{match.Groups["date"].Value} {match.Groups["time"].Value}" : text;
    }

    private static string NormalizeChineseDate(string value)
    {
        var text = CleanPdfValue(value);
        return DateTime.TryParseExact(text, "yyyy年MM月dd日", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : text;
    }

    private static (string Name, string Account) SplitCounterpartyNameAndAccount(string value)
    {
        var text = CleanPdfValue(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        var separatorIndex = text.LastIndexOf('/');
        if (separatorIndex >= 0)
        {
            return (
                CollapseChineseSeparatedWords(text[..separatorIndex]),
                CleanPdfValue(text[(separatorIndex + 1)..]));
        }

        var accountMatch = Regex.Match(text, @"(?<account>\d{8,})$");
        if (accountMatch.Success)
        {
            var account = accountMatch.Groups["account"].Value;
            return (
                CollapseChineseSeparatedWords(text[..^account.Length]),
                account);
        }

        return (CollapseChineseSeparatedWords(text), string.Empty);
    }

    private static bool TryParseSpdbCorporateRecord(
        PdfPositionedRow row,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var date = GetPositionedCell(row, "Date");
        var debitText = GetPositionedCell(row, "Debit");
        var creditText = GetPositionedCell(row, "Credit");
        var balanceText = GetPositionedCell(row, "Balance");
        if (!Regex.IsMatch(date, @"^\d{4}/\d{2}/\d{2}$"))
        {
            return false;
        }

        var serial = GetPositionedCell(row, "Serial");
        var summary = GetPositionedCell(row, "Summary");
        var remark = GetPositionedCell(row, "Remark");
        var oppositeBank = GetPositionedCell(row, "OppositeBank");
        var oppositeName = GetPositionedCell(row, "OppositeName");
        var fallbackAmountText = string.Empty;
        if (string.IsNullOrWhiteSpace(debitText) && string.IsNullOrWhiteSpace(creditText))
        {
            fallbackAmountText = ExtractSpdbCorporateFallbackAmount(
                ref summary,
                ref remark,
                ref oppositeName,
                ref oppositeBank);
            if (string.IsNullOrWhiteSpace(fallbackAmountText))
            {
                return false;
            }
        }

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(date);
        record.SerialNum = serial;
        record.LogNum = serial;
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.Remark = remark;
        record.TradeExplain = remark;
        record.OppositeUsername = oppositeName;
        record.OppositeBank = oppositeBank;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        var signedAmount = ApplyCorporateDebitCredit(record, debitText, creditText, "借方", "贷方");
        if (!signedAmount.HasValue)
        {
            record.TradeMoney = ParseDoubleOrNull(fallbackAmountText);
        }

        SetFlowRaw(record, "日期", date);
        SetFlowRaw(record, "柜员流水", serial);
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "交易金额", fallbackAmountText);
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "对方户名", oppositeName);
        SetFlowRaw(record, "对手机构", oppositeBank);
        SetFlowRaw(record, "对方开户行", oppositeBank);
        SetFlowRaw(record, "备注", remark);
        SetFlowRaw(record, "业务产品种类", summary);
        return true;
    }

    private static bool TryParseSpdbCorporateTextRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        double? previousBalance,
        out FlowRecord record)
    {
        record = new FlowRecord();
        if (group.Count == 0)
        {
            return false;
        }

        var firstLine = CleanPdfValue(group[0].Text);
        var startMatch = Regex.Match(firstLine, @"^(?<date>\d{4}/\d{2}/\d{2})\s+(?<serial>[0-9A-Za-z]+)\s+(?<balance>[+\-−]?\d[\d,]*\.\d{2})(?:\s+(?<tail>.*))?$");
        if (!startMatch.Success)
        {
            return false;
        }

        var detailText = CleanPdfValue(string.Join(' ', new[] { startMatch.Groups["tail"].Value }
            .Concat(group.Skip(1).Select(item => item.Text))));
        var balanceText = startMatch.Groups["balance"].Value.Replace('−', '-');
        var balance = ParseDoubleOrNull(balanceText);
        var amountText = SelectSpdbCorporateAmountText(detailText, previousBalance, balance);
        if (string.IsNullOrWhiteSpace(amountText))
        {
            return false;
        }

        var detailWithoutAmount = RemoveLastMoneyToken(detailText, amountText);
        var (oppositeBank, oppositeName, summary, remark) = SplitSpdbCorporateTextDetail(detailWithoutAmount);
        var date = startMatch.Groups["date"].Value;
        var serial = startMatch.Groups["serial"].Value;
        var amount = ParseDoubleOrNull(amountText);

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(date);
        record.SerialNum = serial;
        record.LogNum = serial;
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.Remark = remark;
        record.TradeExplain = remark;
        record.OppositeUsername = oppositeName;
        record.OppositeBank = oppositeBank;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.TradeMoney = amount.HasValue ? Math.Abs(amount.Value) : null;
        record.Balance = balance;
        record.BalanceAmount = record.Balance;

        SetFlowRaw(record, "日期", date);
        SetFlowRaw(record, "柜员流水", serial);
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "交易金额", amountText);
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "对方户名", oppositeName);
        SetFlowRaw(record, "对手机构", oppositeBank);
        SetFlowRaw(record, "对方开户行", oppositeBank);
        SetFlowRaw(record, "备注", remark);
        SetFlowRaw(record, "业务产品种类", summary);
        return true;
    }

    private static bool IsSpdbCorporateTextRecordStart(string text)
    {
        return Regex.IsMatch(CleanPdfValue(text), @"^\d{4}/\d{2}/\d{2}\s+[0-9A-Za-z]+\s+[+\-−]?\d[\d,]*\.\d{2}");
    }

    private static bool IsSpdbCorporateTextIgnoredLine(string text)
    {
        var value = CleanPdfValue(text);
        return IsCommonIgnoredLine(value)
            || value.Contains("上海浦东发展银行电子对账单", StringComparison.Ordinal)
            || value.Contains("Shanghai Pudong Development Bank", StringComparison.Ordinal)
            || value.StartsWith("账单统计日期", StringComparison.Ordinal)
            || value.StartsWith("客户名称", StringComparison.Ordinal)
            || value.Contains("账户名称 Account Name", StringComparison.Ordinal)
            || value.StartsWith("账号 Account Number", StringComparison.Ordinal)
            || value.StartsWith("客户号 Customer Number", StringComparison.Ordinal)
            || value.StartsWith("账单币种", StringComparison.Ordinal)
            || value.StartsWith("金额单位", StringComparison.Ordinal)
            || value.StartsWith("第", StringComparison.Ordinal)
            || value.StartsWith("Page", StringComparison.Ordinal)
            || value.StartsWith("期末余额", StringComparison.Ordinal)
            || value.StartsWith("Ending Balance", StringComparison.Ordinal)
            || Regex.IsMatch(value, @"^\d{4}/\d{2}/\d{2}$")
            || value.Contains("Remarks:", StringComparison.Ordinal)
            || value.Contains("To ensure", StringComparison.Ordinal)
            || value.Contains("funds,please", StringComparison.OrdinalIgnoreCase)
            || value.Contains("transaction date", StringComparison.OrdinalIgnoreCase)
            || value.Contains("recorded by the bank", StringComparison.OrdinalIgnoreCase)
            || value.Contains("There is a possibility", StringComparison.Ordinal)
            || value.Contains("reconciliation for daily", StringComparison.OrdinalIgnoreCase)
            || value.Contains("actual payment", StringComparison.OrdinalIgnoreCase)
            || value.Contains("query interval", StringComparison.OrdinalIgnoreCase)
            || value.Contains("transaction details within", StringComparison.OrdinalIgnoreCase)
            || value.Contains("years can be queried", StringComparison.OrdinalIgnoreCase)
            || value.Contains("95528", StringComparison.Ordinal)
            || value.Contains("SPDB Enterprise", StringComparison.Ordinal)
            || value.Contains("Statement Generation Date", StringComparison.Ordinal)
            || value.Contains("http://cor.spdb", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("2U35CJLE", StringComparison.Ordinal)
            || value.StartsWith("汇总", StringComparison.Ordinal)
            || value.StartsWith("The Total", StringComparison.Ordinal)
            || value.StartsWith("Total number", StringComparison.Ordinal)
            || value is "交易日期" or "Transaction" or "Date" or "交易流水号" or "Serial Number" or "借方" or "Debit" or "发生额" or "Transaction Amount" or "贷方" or "Credit" or "账户余额" or "Account Balance" or "交易对手信息" or "Counterparty Information" or "对手机构" or "Counterparty" or "Institution" or "对手名称" or "Counterparty Name" or "摘要代码" or "Abstract Code" or "备注" or "Description";
    }

    private static (string OppositeBank, string OppositeName, string Summary, string Remark) SplitSpdbCorporateTextDetail(string detail)
    {
        var text = CleanPdfValue(detail);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var summaryCandidates = new[]
        {
            "跨行转账(手机同城)",
            "电子渠道转账",
            "互联汇出",
            "互联汇入",
            "缴税"
        };
        var summaryIndex = -1;
        var summary = string.Empty;
        foreach (var candidate in summaryCandidates)
        {
            var index = text.LastIndexOf(candidate, StringComparison.Ordinal);
            if (index > summaryIndex)
            {
                summaryIndex = index;
                summary = candidate;
            }
        }

        var beforeSummary = text;
        var remark = string.Empty;
        if (summaryIndex >= 0)
        {
            beforeSummary = CleanPdfValue(text[..summaryIndex]);
            var afterSummary = CleanPdfValue(text[(summaryIndex + summary.Length)..]);
            if ((summary is "互联汇出" or "互联汇入") && !string.IsNullOrWhiteSpace(afterSummary))
            {
                var tokenMatch = Regex.Match(afterSummary, @"^(?<tail>\S+)(?:\s+(?<remark>.*))?$");
                summary = string.Concat(summary, tokenMatch.Groups["tail"].Value);
                remark = CleanPdfValue(tokenMatch.Groups["remark"].Value);
            }
            else
            {
                remark = afterSummary;
            }
        }
        else
        {
            var tokens = SplitWords(text).ToList();
            summary = tokens.Count > 0 ? tokens[^1] : string.Empty;
            beforeSummary = tokens.Count > 1 ? CleanPdfValue(string.Join(' ', tokens.Take(tokens.Count - 1))) : string.Empty;
        }

        var (oppositeBank, oppositeName) = SplitSpdbCorporateTextCounterparty(beforeSummary);
        return (oppositeBank, oppositeName, summary, remark);
    }

    private static (string OppositeBank, string OppositeName) SplitSpdbCorporateTextCounterparty(string value)
    {
        var text = CleanPdfValue(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        var tokens = SplitWords(text).ToList();
        if (tokens.Count <= 1)
        {
            return (string.Empty, text);
        }

        for (var index = 0; index < tokens.Count - 1; index++)
        {
            var prefix = CollapseChineseSeparatedWords(string.Join(' ', tokens.Take(index + 1)));
            if (prefix.EndsWith("支行", StringComparison.Ordinal)
                || prefix.EndsWith("分行营业部", StringComparison.Ordinal)
                || prefix.EndsWith("营业部", StringComparison.Ordinal)
                || prefix.EndsWith("有限公司", StringComparison.Ordinal)
                || prefix.EndsWith("银行", StringComparison.Ordinal))
            {
                return (
                    prefix,
                    CollapseChineseSeparatedWords(string.Join(' ', tokens.Skip(index + 1))));
            }
        }

        return (
            CollapseChineseSeparatedWords(tokens[0]),
            CollapseChineseSeparatedWords(string.Join(' ', tokens.Skip(1))));
    }

    private static void MergeSpdbCorporatePositionedRecords(IList<FlowRecord> targetRecords, IReadOnlyList<FlowRecord> positionedRecords)
    {
        var positionedByKey = positionedRecords
            .GroupBy(CreateSpdbCorporateRecordKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<FlowRecord>(group), StringComparer.Ordinal);

        foreach (var target in targetRecords)
        {
            var key = CreateSpdbCorporateRecordKey(target);
            if (!positionedByKey.TryGetValue(key, out var candidates) || candidates.Count == 0)
            {
                continue;
            }

            var source = candidates.Dequeue();
            target.TradeMoney = source.TradeMoney ?? target.TradeMoney;
            target.DebitAmount = source.DebitAmount ?? target.DebitAmount;
            target.CreditAmount = source.CreditAmount ?? target.CreditAmount;
            target.IncomeAttribute = FirstNotBlank(source.IncomeAttribute, target.IncomeAttribute);
            target.IncomeFlag = FirstNotBlank(source.IncomeFlag, target.IncomeFlag);
            target.ProductBrief = FirstNotBlank(source.ProductBrief, target.ProductBrief);
            target.ProductName = FirstNotBlank(source.ProductName, target.ProductName);
            target.OppositeUsername = FirstNotBlank(source.OppositeUsername, target.OppositeUsername);
            target.OppositeBank = FirstNotBlank(source.OppositeBank, target.OppositeBank);
            target.Remark = FirstNotBlank(source.Remark, target.Remark);
            target.TradeExplain = FirstNotBlank(source.TradeExplain, target.TradeExplain);

            foreach (var item in source.ExtraFields)
            {
                if (!string.IsNullOrWhiteSpace(item.Value))
                {
                    target[item.Key] = item.Value;
                }
            }
        }
    }

    private static string CreateSpdbCorporateRecordKey(FlowRecord record)
    {
        var date = record.AccountTime?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        var balance = record.Balance.HasValue ? FormatMoney(record.Balance.Value) : string.Empty;
        var amount = record.TradeMoney.HasValue ? FormatMoney(Math.Abs(record.TradeMoney.Value)) : string.Empty;
        return string.Join('|', date, record.SerialNum, balance, amount);
    }

    private static bool TryParseCibCorporateTextRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var lines = group.Select(item => CleanPdfValue(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        if (lines.Count < 2)
        {
            return false;
        }

        var startMatch = Regex.Match(lines[0], @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<hour>\d{2}):$");
        var detailMatch = Regex.Match(lines[1], @"^(?<minuteSecond>\d{2}:\d{2})\s+(?<detail>.+)$");
        if (!startMatch.Success || !detailMatch.Success)
        {
            return false;
        }

        var detailTokens = SplitWords(detailMatch.Groups["detail"].Value).ToList();
        var moneyIndexes = detailTokens
            .Select((value, index) => new { value, index })
            .Where(item => IsPdfMoneyToken(item.value))
            .Select(item => item.index)
            .ToList();
        if (moneyIndexes.Count < 3)
        {
            return false;
        }

        var debitIndex = moneyIndexes[0];
        var creditIndex = moneyIndexes[1];
        var balanceIndex = moneyIndexes[2];
        var directionIndex = detailTokens.FindIndex(item => item is "支出" or "收入");
        if (debitIndex <= 0 || directionIndex < creditIndex || directionIndex > balanceIndex)
        {
            return false;
        }

        var beforeAmount = detailTokens.Take(debitIndex).ToList();
        var summary = CollapseChineseSeparatedWords(beforeAmount.Count > 0 ? beforeAmount[0] : string.Empty);
        var voucher = beforeAmount.Count > 1 ? CleanPdfValue(string.Concat(beforeAmount.Skip(1))) : string.Empty;
        var accountParts = new List<string>();
        var nameParts = new List<string>();
        foreach (var token in detailTokens.Skip(balanceIndex + 1))
        {
            if (Regex.IsMatch(token, @"^\d+$") && nameParts.Count == 0)
            {
                accountParts.Add(token);
            }
            else
            {
                nameParts.Add(token);
            }
        }

        foreach (var line in lines.Skip(2))
        {
            if (Regex.IsMatch(line, @"^\d+$") && nameParts.Count == 0)
            {
                accountParts.Add(line);
            }
            else
            {
                nameParts.Add(line);
            }
        }

        var date = startMatch.Groups["date"].Value;
        var time = $"{startMatch.Groups["hour"].Value}:{detailMatch.Groups["minuteSecond"].Value}";
        var debitText = detailTokens[debitIndex];
        var creditText = detailTokens[creditIndex];
        var direction = detailTokens[directionIndex];
        var balanceText = detailTokens[balanceIndex];
        var oppositeAccount = CleanPdfValue(string.Concat(accountParts));
        var oppositeName = CollapseChineseSeparatedWords(string.Join(' ', nameParts));

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull($"{date} {time}");
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.VoucherNum = voucher;
        record.SequenceNum = voucher;
        record.OppositeAccount = oppositeAccount;
        record.OppositeUsername = oppositeName;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        ApplyCorporateDebitCredit(record, debitText, creditText, "借方发生额", "贷方发生额", direction);

        SetFlowRaw(record, "交易日期", record.AccountTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? $"{date} {time}");
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "凭证号", voucher);
        SetFlowRaw(record, "账户余额", balanceText);
        SetFlowRaw(record, "交易对手名称", oppositeName);
        SetFlowRaw(record, "对方账号", oppositeAccount);
        SetFlowRaw(record, "凭证种类", direction);
        SetFlowRaw(record, "业务产品种类", summary);
        return true;
    }

    private static bool TryParseCmbCorporateRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var lines = group
            .Select(item => CleanPdfValue(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        var joinedText = JoinGroupText(group);
        if (Regex.IsMatch(joinedText, @"^\d{8}\s+税款\s+", RegexOptions.CultureInvariant)
            && TryParseCmbCorporateTaxRecord(joinedText, bank, user, out record))
        {
            return true;
        }

        if (TryParseCmbCorporateFlatRecord(joinedText, bank, user, out record))
        {
            return true;
        }

        var dateLineIndex = lines.FindIndex(item => Regex.IsMatch(item, @"^\d{8}\s+"));
        if (dateLineIndex < 0)
        {
            return false;
        }

        var mainTokens = SplitWords(lines[dateLineIndex]).ToList();
        if (mainTokens.Count < 5 || !Regex.IsMatch(mainTokens[0], @"^\d{8}$"))
        {
            return false;
        }

        var moneyIndexes = mainTokens
            .Select((value, index) => new { value, index })
            .Where(item => IsPdfMoneyToken(item.value))
            .Select(item => item.index)
            .ToList();
        if (moneyIndexes.Count < 2)
        {
            return false;
        }

        var amountIndex = moneyIndexes[^2];
        var balanceIndex = moneyIndexes[^1];
        if (amountIndex < 2 || balanceIndex <= amountIndex)
        {
            return false;
        }

        var date = FormatCompactDate(mainTokens[0]);
        var productType = mainTokens[1];
        var beforeAmount = mainTokens.Skip(2).Take(amountIndex - 2).ToList();
        var serial = string.Empty;
        if (beforeAmount.Count > 0 && IsCorporateSerialToken(beforeAmount[0]))
        {
            serial = beforeAmount[0];
            beforeAmount.RemoveAt(0);
        }

        var summary = beforeAmount.Count > 0
            ? CollapseChineseSeparatedWords(string.Join(' ', beforeAmount))
            : productType;
        var amountText = mainTokens[amountIndex];
        var amount = ParseDoubleOrNull(amountText);
        var balanceText = mainTokens[balanceIndex];
        var oppositeName = CollapseChineseSeparatedWords(string.Join(' ', mainTokens.Skip(balanceIndex + 1)));
        var extraLines = lines.Take(dateLineIndex)
            .Concat(lines.Skip(dateLineIndex + 1))
            .ToList();
        var remark = CleanPdfValue(string.Join(' ', extraLines));

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(date);
        record.ProductType = productType;
        record.ProductName = productType;
        record.ProductBrief = summary;
        record.SerialNum = serial;
        record.VoucherNum = serial;
        record.TradeMoney = amount;
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        record.OppositeUsername = oppositeName;
        record.Remark = remark;
        record.TradeExplain = remark;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        ApplySignedAmountColumns(record, amount);

        SetFlowRaw(record, "记帐日期", date);
        SetFlowRaw(record, "帐户代码", record.Account);
        SetFlowRaw(record, "货币", record.Currency);
        SetFlowRaw(record, "交易金额", amountText);
        SetFlowRaw(record, "联机余额", balanceText);
        SetFlowRaw(record, "交易流水号", serial);
        SetFlowRaw(record, "摘要代码", summary);
        SetFlowRaw(record, "对手户名", oppositeName);
        SetFlowRaw(record, "记帐时间", date);
        SetFlowRaw(record, "业务类型", productType);
        SetFlowRaw(record, "备注", remark);
        return true;
    }

    private static bool TryParseCmbCorporateTaxRecord(
        string text,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var match = Regex.Match(
            CleanPdfValue(text),
            @"^(?<date>\d{8})\s+(?<type>税款)\s+(?<middle>.+?)\s+(?<amount>[+\-−]?\d[\d,]*\.\d{2})\s+(?<balance>[+\-−]?\d[\d,]*\.\d{2})(?:\s+(?<opposite>.+))?$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        var middleTokens = SplitWords(match.Groups["middle"].Value).ToList();
        var serialParts = new List<string>();
        while (middleTokens.Count > 0 && Regex.IsMatch(middleTokens[0], @"^\d+$"))
        {
            serialParts.Add(middleTokens[0]);
            middleTokens.RemoveAt(0);
        }

        var date = FormatCompactDate(match.Groups["date"].Value);
        var productType = match.Groups["type"].Value;
        var serial = CleanPdfValue(string.Concat(serialParts));
        var summary = middleTokens.Count > 0
            ? CollapseChineseSeparatedWords(string.Join(' ', middleTokens))
            : productType;
        var amountText = match.Groups["amount"].Value.Replace('−', '-');
        var balanceText = match.Groups["balance"].Value.Replace('−', '-');
        var amount = ParseDoubleOrNull(amountText);
        var oppositeName = CleanPdfValue(match.Groups["opposite"].Value);

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(date);
        record.ProductType = productType;
        record.ProductName = productType;
        record.ProductBrief = summary;
        record.SerialNum = serial;
        record.VoucherNum = serial;
        record.TradeMoney = amount;
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        record.OppositeUsername = oppositeName;
        record.Remark = summary;
        record.TradeExplain = summary;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        ApplySignedAmountColumns(record, amount);

        SetFlowRaw(record, "记帐日期", date);
        SetFlowRaw(record, "帐户代码", record.Account);
        SetFlowRaw(record, "货币", record.Currency);
        SetFlowRaw(record, "交易金额", amountText);
        SetFlowRaw(record, "联机余额", balanceText);
        SetFlowRaw(record, "交易流水号", serial);
        SetFlowRaw(record, "摘要代码", summary);
        SetFlowRaw(record, "对手户名", oppositeName);
        SetFlowRaw(record, "记帐时间", date);
        SetFlowRaw(record, "业务类型", productType);
        SetFlowRaw(record, "备注", summary);
        return true;
    }

    private static bool TryParseCmbCorporateFlatRecord(
        string text,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var match = Regex.Match(
            CleanPdfValue(text),
            @"^(?<date>\d{8})\s+(?<type>\S+)(?:\s+(?<middle>.*?))?\s+(?<amount>[+\-−]?\d[\d,]*\.\d{2})\s+(?<balance>[+\-−]?\d[\d,]*\.\d{2})(?:\s+(?<opposite>.+))?$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        var middleTokens = SplitWords(match.Groups["middle"].Value).ToList();
        var serial = string.Empty;
        if (middleTokens.Count > 0 && IsCorporateSerialToken(middleTokens[0]))
        {
            serial = middleTokens[0];
            middleTokens.RemoveAt(0);
        }

        var date = FormatCompactDate(match.Groups["date"].Value);
        var productType = match.Groups["type"].Value;
        var summary = middleTokens.Count > 0
            ? CollapseChineseSeparatedWords(string.Join(' ', middleTokens))
            : productType;
        var amountText = match.Groups["amount"].Value.Replace('−', '-');
        var balanceText = match.Groups["balance"].Value.Replace('−', '-');
        var amount = ParseDoubleOrNull(amountText);
        var oppositeName = CleanPdfValue(match.Groups["opposite"].Value);

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(date);
        record.ProductType = productType;
        record.ProductName = productType;
        record.ProductBrief = summary;
        record.SerialNum = serial;
        record.VoucherNum = serial;
        record.TradeMoney = amount;
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        record.OppositeUsername = oppositeName;
        record.Remark = summary == productType ? string.Empty : summary;
        record.TradeExplain = record.Remark;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        ApplySignedAmountColumns(record, amount);

        SetFlowRaw(record, "记帐日期", date);
        SetFlowRaw(record, "帐户代码", record.Account);
        SetFlowRaw(record, "货币", record.Currency);
        SetFlowRaw(record, "交易金额", amountText);
        SetFlowRaw(record, "联机余额", balanceText);
        SetFlowRaw(record, "交易流水号", serial);
        SetFlowRaw(record, "摘要代码", summary);
        SetFlowRaw(record, "对手户名", oppositeName);
        SetFlowRaw(record, "记帐时间", date);
        SetFlowRaw(record, "业务类型", productType);
        SetFlowRaw(record, "备注", record.Remark);
        return true;
    }

    private static bool TryParseBocCorporateRecord(
        PdfTableRow row,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var sequence = GetTableCell(row, 0);
        var accountDate = FormatShortBankDate(GetTableCell(row, 1));
        var interestDate = FormatShortBankDate(GetTableCell(row, 2));
        var tradeType = GetTableCell(row, 3);
        var voucher = GetTableCell(row, 4);
        var detail = GetTableCell(row, 5);
        var debitText = GetTableCell(row, 6);
        var creditText = GetTableCell(row, 7);
        var balanceText = GetTableCell(row, 8);
        var reference = GetTableCell(row, 9);
        var note = GetTableCell(row, 10);
        if (!Regex.IsMatch(sequence, @"^\d+$") || !Regex.IsMatch(accountDate, @"^\d{4}-\d{2}-\d{2}$"))
        {
            return false;
        }

        var (oppositeName, oppositeBank) = SplitBocCorporateCounterparty(note);
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(accountDate);
        record.SequenceNum = sequence;
        record.ProductType = tradeType;
        record.ProductName = tradeType;
        record.ProductBrief = detail;
        record.VoucherType = voucher;
        record.VoucherNum = voucher;
        record.SerialNum = reference;
        record.LogNum = reference;
        record.Usage = detail;
        record.Remark = note;
        record.TradeExplain = note;
        record.OppositeUsername = oppositeName;
        record.OppositeBank = oppositeBank;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        ApplyCorporateDebitCredit(record, debitText, creditText, "借方发生额", "贷方发生额");

        SetFlowRaw(record, "序号", sequence);
        SetFlowRaw(record, "记账日", accountDate);
        SetFlowRaw(record, "起息日", interestDate);
        SetFlowRaw(record, "交易类型", tradeType);
        SetFlowRaw(record, "凭证", voucher);
        SetFlowRaw(record, "凭证号码业务编号用途摘要", detail);
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "机构柜员流水", reference);
        SetFlowRaw(record, "用途", detail);
        SetFlowRaw(record, "对方户名", oppositeName);
        SetFlowRaw(record, "对方开户行", oppositeBank);
        SetFlowRaw(record, "备注", note);
        return true;
    }

    private static bool TryParseCiticCorporateRecord(
        PdfPositionedRow row,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var date = GetPositionedCell(row, "Date");
        var debitText = GetPositionedCell(row, "Debit");
        var creditText = GetPositionedCell(row, "Credit");
        var balanceText = GetPositionedCell(row, "Balance");
        if (!Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$")
            || (string.IsNullOrWhiteSpace(debitText) && string.IsNullOrWhiteSpace(creditText)))
        {
            return false;
        }

        var tellerSerial = GetPositionedCell(row, "TellerSerial");
        var summary = GetPositionedCell(row, "Summary");
        var oppositeAccount = GetPositionedCell(row, "OppositeAccount");
        var oppositeName = GetPositionedCell(row, "OppositeName");
        var fundBook = GetPositionedCell(row, "FundBook");

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(date);
        record.SerialNum = tellerSerial;
        record.LogNum = tellerSerial;
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.OppositeAccount = oppositeAccount;
        record.OppositeUsername = oppositeName;
        record.ProductType = fundBook;
        record.Currency = FirstNotBlank(user.Currency, "人民币");
        record.Balance = ParseDoubleOrNull(balanceText);
        record.BalanceAmount = record.Balance;
        ApplyCorporateDebitCredit(record, debitText, creditText, "借方", "贷方");

        SetFlowRaw(record, "交易日期", date);
        SetFlowRaw(record, "摘要", summary);
        SetFlowRaw(record, "柜员交易号", tellerSerial);
        SetFlowRaw(record, "核心流水号", tellerSerial);
        SetFlowRaw(record, "余额", balanceText);
        SetFlowRaw(record, "对方户名", oppositeName);
        SetFlowRaw(record, "对方账号", oppositeAccount);
        SetFlowRaw(record, "动账资金分簿", fundBook);
        return true;
    }

    private static bool TryParseBocRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var text = JoinGroupText(group);
        var match = Regex.Match(text, @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+(?<currency>\S+)\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})\s+(?<rest>.+)$");
        if (!match.Success)
        {
            return false;
        }

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = user.AccountNo;
        record.AccountTime = ParseDateTimeOrNull($"{match.Groups["date"].Value} {match.Groups["time"].Value}");
        record.Currency = CleanPdfValue(match.Groups["currency"].Value);
        record.TradeMoney = ParseDoubleOrNull(match.Groups["amount"].Value);
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);

        var rest = SplitWords(match.Groups["rest"].Value);
        if (rest.Count < 2)
        {
            return false;
        }

        record.ProductName = CleanPdfValue(rest[0]);
        record.ProductBrief = record.ProductName;
        record.TradeChannel = CleanPdfValue(rest[1]);
        var remainder = rest.Skip(2).ToList();
        var accountIndex = FindLastCounterpartyAccountIndex(remainder);
        if (accountIndex >= 0)
        {
            record.OppositeAccount = CleanPdfValue(remainder[accountIndex]);
            record.OppositeBank = CleanPdfValue(string.Join(' ', remainder.Skip(accountIndex + 1)));
            var beforeAccount = remainder.Take(accountIndex).ToList();
            SplitBocBeforeCounterparty(beforeAccount, out var tradePlace, out var remark, out var oppositeName);
            record.TradePlace = tradePlace;
            record.Remark = remark;
            record.OppositeUsername = oppositeName;
        }
        else
        {
            record.Remark = CleanPdfValue(string.Join(' ', remainder));
        }

        SetFlowRaw(record, "记账日期", match.Groups["date"].Value);
        SetFlowRaw(record, "记账时间", match.Groups["time"].Value);
        SetFlowRaw(record, "币别", record.Currency);
        SetFlowRaw(record, "金额", match.Groups["amount"].Value);
        SetFlowRaw(record, "余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "交易名称", record.ProductName);
        SetFlowRaw(record, "渠道", record.TradeChannel);
        SetFlowRaw(record, "网点名称", record.TradePlace);
        SetFlowRaw(record, "附言", record.Remark);
        SetFlowRaw(record, "对方账户名", record.OppositeUsername);
        SetFlowRaw(record, "对方卡号账号", record.OppositeAccount);
        SetFlowRaw(record, "对方开户行", record.OppositeBank);
        return true;
    }

    private static bool TryParseIcbcRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        if (group.Count == 0)
        {
            return false;
        }

        var date = group[0].Text.Trim();
        var text = JoinGroupText(group.Skip(1));
        var match = Regex.Match(text, @"^(?<time>\d{2}:\d{2}:\d{2})\s+(?<account>\d+)\s+(?<store>\S+)\s+(?<seq>\S+)\s+(?<currency>\S+)\s+(?<cash>\S+)\s+(?<summary>.+?)\s+(?<area>\d{4})\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})\s+(?<channel>.+)$");
        if (!match.Success)
        {
            return false;
        }

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = CleanPdfValue(match.Groups["account"].Value);
        record.AccountTime = ParseDateTimeOrNull($"{date} {match.Groups["time"].Value}");
        record.ProductType = CleanPdfValue(match.Groups["store"].Value);
        record.SequenceNum = CleanPdfValue(match.Groups["seq"].Value);
        record.Currency = CleanPdfValue(match.Groups["currency"].Value);
        record.CashCheck = CleanPdfValue(match.Groups["cash"].Value);
        record.ProductBrief = CleanPdfValue(match.Groups["summary"].Value);
        record.TradeExplain = record.ProductBrief;
        record.AreaNum = CleanPdfValue(match.Groups["area"].Value);
        record.TradeMoney = ParseDoubleOrNull(match.Groups["amount"].Value);
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);
        record.TradeChannel = CleanPdfValue(match.Groups["channel"].Value);
        record.InterfacePage = record.TradeChannel;

        SetFlowRaw(record, "工作日期", date);
        SetFlowRaw(record, "帐号", record.Account);
        SetFlowRaw(record, "储种", record.ProductType);
        SetFlowRaw(record, "序号", record.SequenceNum);
        SetFlowRaw(record, "币种", record.Currency);
        SetFlowRaw(record, "钞汇", record.CashCheck);
        SetFlowRaw(record, "摘要", record.ProductBrief);
        SetFlowRaw(record, "注释", record.TradeExplain);
        SetFlowRaw(record, "地区", record.AreaNum);
        SetFlowRaw(record, "收入/支出金额", match.Groups["amount"].Value);
        SetFlowRaw(record, "发生额", match.Groups["amount"].Value);
        SetFlowRaw(record, "余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "渠道", record.TradeChannel);
        SetFlowRaw(record, "界面", record.InterfacePage);
        return true;
    }

    private static bool TryParseCcbRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var startIndex = Enumerable.Range(0, group.Count)
            .FirstOrDefault(index => IsCcbAnyRecordStart(group, index), -1);
        if (startIndex < 0)
        {
            return false;
        }

        var prefix = JoinGroupText(group.Take(startIndex));
        var text = JoinGroupText(group.Skip(startIndex));
        var match = Regex.Match(text, @"^(?<seq>\d+)\s+(?<summary>.+?)\s+(?<date>\d{8})\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})(?:\s+(?<rest>.*))?$");
        var hasSummaryColumn = match.Success;
        if (!match.Success)
        {
            match = Regex.Match(text, @"^(?<seq>\d+)\s+(?<date>\d{8})\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})(?:\s+(?<rest>.*))?$");
        }

        if (!match.Success)
        {
            return false;
        }

        var rest = FirstNotBlank(prefix, match.Groups["rest"].Value) == prefix
            ? CleanPdfValue($"{prefix} {match.Groups["rest"].Value}")
            : CleanPdfValue(match.Groups["rest"].Value);
        var summary = hasSummaryColumn
            ? CleanCcbSummary(match.Groups["summary"].Value)
            : FirstNotBlank(ExtractCcbPrefixSummary(prefix), ExtractCcbRestSummary(match.Groups["rest"].Value));
        var (place, oppositeAccount, oppositeName) = SplitCcbPlaceAndCounterparty(rest);
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.CardNo, user.AccountNo);
        record.SequenceNum = CleanPdfValue(match.Groups["seq"].Value);
        record.ProductBrief = summary;
        record.AccountTime = ParseDateTimeOrNull(match.Groups["date"].Value);
        record.TradeMoney = ParseDoubleOrNull(match.Groups["amount"].Value);
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);
        record.TradePlace = CleanPdfValue(place);
        record.Remark = record.TradePlace;
        record.OppositeAccount = CleanPdfValue(oppositeAccount);
        record.OppositeUsername = CleanPdfValue(oppositeName);

        SetFlowRaw(record, "序号", record.SequenceNum);
        SetFlowRaw(record, "摘要", record.ProductBrief);
        SetFlowRaw(record, "交易日期", match.Groups["date"].Value);
        SetFlowRaw(record, "交易金额", match.Groups["amount"].Value);
        SetFlowRaw(record, "帐户余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "账户余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "交易地点/附言", record.TradePlace);
        SetFlowRaw(record, "商户网点号及名称", record.TradePlace);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        return true;
    }

    private static bool TryParseCmbcRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        if (group.Count == 0)
        {
            return false;
        }

        var first = group[0].Text.Trim();
        var text = JoinGroupText(group.Skip(1));
        var match = Regex.Match(text, @"^(?<date>\d{4}/\d{2}/\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+(?<summary>.+?)\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})\s+(?<cash>\S+)(?:\s+(?<channel>\S+))?(?:\s+(?<net>\S+))?(?:\s+(?<after>.*))?$");
        if (!match.Success)
        {
            return false;
        }

        var (oppositeName, oppositeAccount, oppositeBank) = SplitNameAccountBank(match.Groups["after"].Value);
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.CardNo, user.AccountNo);
        record.VoucherType = "卡";
        record.VoucherNum = CleanPdfValue(first);
        record.AccountTime = ParseDateTimeOrNull($"{match.Groups["date"].Value} {match.Groups["time"].Value}");
        record.ProductBrief = CleanPdfValue(match.Groups["summary"].Value);
        record.TradeMoney = ParseDoubleOrNull(match.Groups["amount"].Value);
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);
        record.CashCheck = CleanPdfValue(match.Groups["cash"].Value);
        record.TradeChannel = CleanPdfValue(match.Groups["channel"].Value);
        record.NetNum = CleanPdfValue(match.Groups["net"].Value);
        record.OppositeUsername = CleanPdfValue(oppositeName);
        record.OppositeAccount = CleanPdfValue(oppositeAccount);
        record.OppositeBank = CleanPdfValue(oppositeBank);

        SetFlowRaw(record, "凭证类型", record.VoucherType);
        SetFlowRaw(record, "凭证号码", record.VoucherNum);
        SetFlowRaw(record, "交易时间", $"{match.Groups["date"].Value} {match.Groups["time"].Value}");
        SetFlowRaw(record, "摘要信息", record.ProductBrief);
        SetFlowRaw(record, "摘要", record.ProductBrief);
        SetFlowRaw(record, "交易金额", match.Groups["amount"].Value);
        SetFlowRaw(record, "账户余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "现转标志", record.CashCheck);
        SetFlowRaw(record, "交易渠道", record.TradeChannel);
        SetFlowRaw(record, "交易机构", record.NetNum);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方行名", record.OppositeBank);
        return true;
    }

    private static bool TryParseAbcRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var text = JoinGroupText(group);
        var match = Regex.Match(text, @"^(?<date>\d{8})\s+(?<time>\d{6})\s+(?<summary>\S+)\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})(?:\s+(?<rest>.*))?$");
        if (!match.Success)
        {
            return false;
        }

        var (oppositeName, logNumber, channel, remark) = SplitAbcRemainder(match.Groups["rest"].Value);
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.CardNo, user.AccountNo);
        record.AccountTime = ParseDateTimeOrNull($"{match.Groups["date"].Value} {match.Groups["time"].Value}");
        record.ProductBrief = CleanPdfValue(match.Groups["summary"].Value);
        record.TradeMoney = ParseDoubleOrNull(match.Groups["amount"].Value);
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);
        record.OppositeUsername = CleanPdfValue(oppositeName);
        record.LogNum = CleanPdfValue(logNumber);
        record.TradeChannel = CleanPdfValue(channel);
        record.Remark = CleanPdfValue(remark);
        record.TradeExplain = record.Remark;

        SetFlowRaw(record, "日期", match.Groups["date"].Value);
        SetFlowRaw(record, "交易时间", match.Groups["time"].Value);
        SetFlowRaw(record, "摘要", record.ProductBrief);
        SetFlowRaw(record, "交易金额", match.Groups["amount"].Value);
        SetFlowRaw(record, "本次余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "对手信息", record.OppositeUsername);
        SetFlowRaw(record, "户名", record.OppositeUsername);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方开户行", record.OppositeBank);
        SetFlowRaw(record, "日志号", record.LogNum);
        SetFlowRaw(record, "交易渠道", record.TradeChannel);
        SetFlowRaw(record, "交易附言", record.Remark);
        SetFlowRaw(record, "附言", record.Remark);
        return true;
    }

    private static bool TryParsePingAnRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var text = JoinGroupText(group);
        var match = Regex.Match(text, @"^(?<seq>\d+)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<amount>[+-]?\d[\d,]*(?:\.\d+)?)\s+(?<balance>[+-]?\d[\d,]*(?:\.\d+)?)\s+(?<rest>.+)$");
        if (!match.Success)
        {
            return false;
        }

        var tokens = SplitWords(match.Groups["rest"].Value).ToList();
        if (tokens.Count < 2)
        {
            return false;
        }

        var tradePlace = CleanPdfValue(tokens[0]);
        var summaryIndex = 1;
        var summary = CleanPdfValue(tokens[summaryIndex]);
        if (summary == "银联贷记交易入" && summaryIndex + 1 < tokens.Count && tokens[summaryIndex + 1] == "账")
        {
            summary = "银联贷记交易入账";
            summaryIndex++;
        }

        var afterSummary = tokens.Skip(summaryIndex + 1).ToList();
        var (note, oppositeName, oppositeAccount, oppositeBank) = SplitPingAnCounterparty(afterSummary);
        var amount = ParseDoubleOrNull(match.Groups["amount"].Value);

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.SequenceNum = CleanPdfValue(match.Groups["seq"].Value);
        record.AccountTime = ParseDateTimeOrNull(match.Groups["date"].Value);
        record.TradeMoney = amount;
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);
        record.TradePlace = tradePlace;
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.Remark = note;
        record.TradeExplain = note;
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        record.OppositeBank = oppositeBank;
        ApplySignedAmountColumns(record, amount);

        SetFlowRaw(record, "交易日期", match.Groups["date"].Value);
        SetFlowRaw(record, "交易网点", record.TradePlace);
        SetFlowRaw(record, "交易地点", record.TradePlace);
        SetFlowRaw(record, "起息日", match.Groups["date"].Value);
        SetFlowRaw(record, "摘要", record.ProductBrief);
        SetFlowRaw(record, "摘要2", record.Remark);
        SetFlowRaw(record, "摘要3", record.OppositeUsername);
        SetFlowRaw(record, "借方发生额", amount is < 0 ? FormatMoney(Math.Abs(amount.Value)) : string.Empty);
        SetFlowRaw(record, "贷方发生额", amount is > 0 ? FormatMoney(amount.Value) : string.Empty);
        SetFlowRaw(record, "账户余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "传票号码", record.SequenceNum);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方开户行", record.OppositeBank);
        SetFlowRaw(record, "留言", record.Remark);
        SetFlowRaw(record, "流水号", record.SequenceNum);
        return true;
    }

    private static bool TryParseSpdbRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        if (group.Count < 3)
        {
            return false;
        }

        var startMatch = Regex.Match(group[0].Text.Trim(), @"^(?<date>\d{8})\s+(?<time>\d{6})\s+(?<account>\d+)$");
        if (!startMatch.Success)
        {
            return false;
        }

        var index = 1;
        var account = CleanPdfValue(startMatch.Groups["account"].Value);
        while (index < group.Count && Regex.IsMatch(group[index].Text.Trim(), @"^\d+$") && account.Length < 16)
        {
            account = string.Concat(account, group[index].Text.Trim());
            index++;
        }

        var beforeAmount = new List<string>();
        var serialParts = new List<string>();
        Match? amountMatch = null;
        var amountLineIndex = -1;
        for (; index < group.Count; index++)
        {
            var lineText = CleanPdfValue(group[index].Text);
            var match = Regex.Match(lineText, @"^(?:(?<before>.+?)\s+)?(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})(?:\s+(?<after>.*))?$");
            if (match.Success)
            {
                amountMatch = match;
                amountLineIndex = index;
                var before = CleanPdfValue(match.Groups["before"].Value);
                if (!string.IsNullOrWhiteSpace(before))
                {
                    beforeAmount.Add(before);
                }

                break;
            }

            if (Regex.IsMatch(lineText, @"^\d+$"))
            {
                serialParts.Add(lineText);
            }
            else
            {
                beforeAmount.Add(lineText);
            }
        }

        if (amountMatch is null || amountLineIndex < 0)
        {
            return false;
        }

        var transactionName = CollapseChineseSeparatedWords(string.Join(' ', beforeAmount));
        if (string.IsNullOrWhiteSpace(transactionName))
        {
            return false;
        }

        var afterAmount = CleanPdfValue(amountMatch.Groups["after"].Value);
        var afterLines = group.Skip(amountLineIndex + 1)
            .Select(item => item.Text)
            .ToList();
        var (oppositeName, oppositeAccount, summary) = SplitSpdbCounterparty(afterAmount, afterLines);
        var amount = ParseDoubleOrNull(amountMatch.Groups["amount"].Value);

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = account;
        record.AccountTime = ParseDateTimeOrNull($"{startMatch.Groups["date"].Value} {startMatch.Groups["time"].Value}");
        record.ProductBrief = transactionName;
        record.ProductName = transactionName;
        record.TradeMoney = amount;
        record.Balance = ParseDoubleOrNull(amountMatch.Groups["balance"].Value);
        record.SerialNum = string.Concat(serialParts);
        record.LogNum = record.SerialNum;
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        record.Remark = summary;
        record.TradeExplain = summary;
        ApplySignedAmountColumns(record, amount);

        SetFlowRaw(record, "交易账户", record.Account);
        SetFlowRaw(record, "交易账号", record.Account);
        SetFlowRaw(record, "交易日期", $"{startMatch.Groups["date"].Value} {startMatch.Groups["time"].Value}");
        SetFlowRaw(record, "入账日期", startMatch.Groups["date"].Value);
        SetFlowRaw(record, "交易摘要", record.ProductBrief);
        SetFlowRaw(record, "交易名称", record.ProductBrief);
        SetFlowRaw(record, "交易金额", amountMatch.Groups["amount"].Value);
        SetFlowRaw(record, "帐户余额", amountMatch.Groups["balance"].Value);
        SetFlowRaw(record, "账户余额", amountMatch.Groups["balance"].Value);
        SetFlowRaw(record, "柜员流水", record.SerialNum);
        SetFlowRaw(record, "流水号", record.SerialNum);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对手姓名", record.OppositeUsername);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对手账号", record.OppositeAccount);
        SetFlowRaw(record, "备注", record.Remark);
        SetFlowRaw(record, "交易说明", record.TradeExplain);
        return true;
    }

    private static bool TryParseCibRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var text = JoinGroupText(group);
        var headMatch = Regex.Match(text, @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>.*?)\s+(?<accounting>\d{8})\s+(?<after>.+)$");
        if (!headMatch.Success)
        {
            return false;
        }

        var timeDigits = Regex.Replace(headMatch.Groups["time"].Value, @"\D", string.Empty);
        if (timeDigits.Length < 6)
        {
            return false;
        }

        var time = $"{timeDigits[..2]}:{timeDigits.Substring(2, 2)}:{timeDigits.Substring(4, 2)}";
        var detail = headMatch.Groups["after"].Value;
        var detailMatch = Regex.Match(detail, @"^(?<summary>.+?)\s+(?<direction>[支收])\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})(?:\s+(?<tail>.*))?$");
        if (!detailMatch.Success)
        {
            return false;
        }

        var direction = CleanPdfValue(detailMatch.Groups["direction"].Value);
        var parsedAmount = ParseDoubleOrNull(detailMatch.Groups["amount"].Value);
        if (!parsedAmount.HasValue)
        {
            return false;
        }

        var signedAmount = direction == "支"
            ? 0 - Math.Abs(parsedAmount.Value)
            : Math.Abs(parsedAmount.Value);
        var (usage, oppositeName, oppositeAccount, oppositeBank) = SplitCibTail(detailMatch.Groups["tail"].Value);
        var summary = CollapseChineseSeparatedWords(detailMatch.Groups["summary"].Value);

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull($"{headMatch.Groups["date"].Value} {time}");
        record.ProductName = summary;
        record.ProductBrief = summary;
        record.IncomeAttribute = direction == "支" ? "支出" : "收入";
        record.TradeMoney = signedAmount;
        record.Balance = ParseDoubleOrNull(detailMatch.Groups["balance"].Value);
        record.Usage = usage;
        record.Remark = usage;
        record.TradeExplain = usage;
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        record.OppositeBank = oppositeBank;
        ApplySignedAmountColumns(record, signedAmount);

        SetFlowRaw(record, "交易日期", $"{headMatch.Groups["date"].Value} {time}");
        SetFlowRaw(record, "记账日期", headMatch.Groups["accounting"].Value);
        SetFlowRaw(record, "交易种类", record.ProductBrief);
        SetFlowRaw(record, "交易金额", FormatMoney(signedAmount));
        SetFlowRaw(record, "交易后余额", detailMatch.Groups["balance"].Value);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方银行", record.OppositeBank);
        SetFlowRaw(record, "用途", record.Usage);
        SetFlowRaw(record, "备注", record.Remark);
        return true;
    }

    private static bool TryParsePsbcRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        if (group.Count < 3)
        {
            return false;
        }

        var date = CleanPdfValue(group[0].Text);
        var time = CleanPdfValue(group[1].Text);
        var detail = JoinGroupText(group.Skip(2));
        var match = Regex.Match(detail, @"^(?<sub>\S+)\s+(?<store>\S+)\s+(?<currency>\S+)\s+(?<cash>\S+)\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})\s+(?<tail>.+)$");
        if (!match.Success)
        {
            return false;
        }

        var tailTokens = SplitWords(match.Groups["tail"].Value).ToList();
        var accountIndex = FindFirstCounterpartyAccountIndex(tailTokens);
        var oppositeName = string.Empty;
        var oppositeAccount = string.Empty;
        var summary = string.Empty;
        var channel = string.Empty;
        var externalSerial = string.Empty;
        if (accountIndex >= 0)
        {
            oppositeName = CollapseChineseSeparatedWords(string.Join(' ', tailTokens.Take(accountIndex)));
            oppositeAccount = CleanPdfValue(tailTokens[accountIndex]);
            var afterAccount = tailTokens.Skip(accountIndex + 1).ToList();
            summary = afterAccount.Count > 0 ? CleanPdfValue(afterAccount[0]) : string.Empty;
            channel = afterAccount.Count > 1 ? CleanPdfValue(afterAccount[1]) : string.Empty;
            externalSerial = CleanPdfValue(string.Join(' ', afterAccount.Skip(2)));
        }
        else
        {
            summary = CollapseChineseSeparatedWords(string.Join(' ', tailTokens));
        }

        var amount = ParseDoubleOrNull(match.Groups["amount"].Value);
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.CardNo, user.AccountNo);
        record.AccountNum = CleanPdfValue(match.Groups["sub"].Value);
        record.ProductType = CleanPdfValue(match.Groups["store"].Value);
        record.Currency = CleanPdfValue(match.Groups["currency"].Value);
        record.CashCheck = CleanPdfValue(match.Groups["cash"].Value);
        record.AccountTime = ParseDateTimeOrNull($"{date} {time}");
        record.TradeMoney = amount;
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.TradeChannel = channel;
        record.Remark = summary;
        record.TradeExplain = summary;
        record.SerialNum = externalSerial;
        record.ReceiptNum = externalSerial;
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        ApplySignedAmountColumns(record, amount);

        SetFlowRaw(record, "交易日期", $"{date} {time}");
        SetFlowRaw(record, "交易类型", record.ProductBrief);
        SetFlowRaw(record, "交易币种", record.Currency);
        SetFlowRaw(record, "交易金额", match.Groups["amount"].Value);
        SetFlowRaw(record, "账户余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "对手方户名", record.OppositeUsername);
        SetFlowRaw(record, "对手方账户", record.OppositeAccount);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "摘要", record.Remark);
        SetFlowRaw(record, "附言", record.Remark);
        SetFlowRaw(record, "交易渠道", record.TradeChannel);
        SetFlowRaw(record, "交易方式", record.TradeChannel);
        SetFlowRaw(record, "钞汇", record.CashCheck);
        SetFlowRaw(record, "外部系统流水", record.SerialNum);
        return true;
    }

    private static bool TryParseCiticRecord(
        PdfTextLine line,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var text = CleanPdfValue(line.Text);
        var match = Regex.Match(text, @"^(?<date>\d{8})\s+(?<currency>[A-Za-z]+)\s+(?<amount>\d[\d,]*\.\d{2})\s+(?<balanceCurrency>[A-Za-z]+)\s+(?<balance>\d[\d,]*\.\d{2})\s+(?<summary>\S+)(?:\s+(?<rest>.*))?$");
        if (!match.Success)
        {
            return false;
        }

        var restTokens = SplitWords(match.Groups["rest"].Value).ToList();
        var oppositeAccount = restTokens.Count > 0 && IsCounterpartyAccountToken(restTokens[0])
            ? CleanPdfValue(restTokens[0])
            : string.Empty;
        var oppositeName = oppositeAccount.Length > 0
            ? CollapseChineseSeparatedWords(string.Join(' ', restTokens.Skip(1)))
            : CollapseChineseSeparatedWords(string.Join(' ', restTokens));

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(match.Groups["date"].Value);
        record.Currency = CleanPdfValue(match.Groups["currency"].Value);
        record.TradeMoney = ParseDoubleOrNull(match.Groups["amount"].Value);
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);
        record.ProductBrief = CleanPdfValue(match.Groups["summary"].Value);
        record.ProductName = record.ProductBrief;
        record.OppositeAccount = oppositeAccount;
        record.OppositeUsername = oppositeName;

        SetFlowRaw(record, "账号", record.Account);
        SetFlowRaw(record, "交易日期", match.Groups["date"].Value);
        SetFlowRaw(record, "账户余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "交易摘要", record.ProductBrief);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        return true;
    }

    private static bool TryParseEverbrightRecord(
        PdfTextLine line,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var text = CleanPdfValue(line.Text);
        var match = Regex.Match(text, @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})\s+(?<tail>.+)$");
        if (!match.Success)
        {
            return false;
        }

        var (oppositeName, oppositeAccount, summary) = SplitEverbrightTail(match.Groups["tail"].Value);
        var amount = ParseDoubleOrNull(match.Groups["amount"].Value);
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.CardNo, user.AccountNo);
        record.AccountTime = ParseDateTimeOrNull(match.Groups["date"].Value);
        record.TradeMoney = amount.HasValue ? Math.Abs(amount.Value) : null;
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.Remark = summary;
        record.TradeExplain = summary;

        SetFlowRaw(record, "卡号", record.Account);
        SetFlowRaw(record, "交易日期", match.Groups["date"].Value);
        SetFlowRaw(record, "交易金额", match.Groups["amount"].Value);
        SetFlowRaw(record, "账户余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "对手信息", $"{record.OppositeUsername} {record.OppositeAccount}".Trim());
        SetFlowRaw(record, "对方名称", record.OppositeUsername);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "摘要", record.ProductBrief);
        SetFlowRaw(record, "交易摘要", record.ProductBrief);
        return true;
    }

    private static bool TryParseCgbRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        if (group.Count == 0)
        {
            return false;
        }

        var dateTimeText = string.Empty;
        var currencyText = string.Empty;
        var amountText = string.Empty;
        var balanceText = string.Empty;
        var tailSegments = new List<string>();

        var fullLineMatch = Regex.Match(CleanPdfValue(group[0].Text), @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})\s+(?<currency>\S+)\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})(?:\s+(?<tail>.*))?$");
        if (fullLineMatch.Success)
        {
            dateTimeText = $"{fullLineMatch.Groups["date"].Value} {fullLineMatch.Groups["time"].Value}";
            currencyText = fullLineMatch.Groups["currency"].Value;
            amountText = fullLineMatch.Groups["amount"].Value;
            balanceText = fullLineMatch.Groups["balance"].Value;
            var inlineTail = CleanPdfValue(fullLineMatch.Groups["tail"].Value);
            if (!string.IsNullOrWhiteSpace(inlineTail))
            {
                tailSegments.Add(inlineTail);
            }

            tailSegments.AddRange(group.Skip(1).Select(item => item.Text));
        }
        else
        {
            if (group.Count < 2)
            {
                return false;
            }

            var startMatch = Regex.Match(CleanPdfValue(group[0].Text), @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:?)$");
            if (!startMatch.Success)
            {
                return false;
            }

            var detailMatch = Regex.Match(CleanPdfValue(group[1].Text), @"^(?<second>\d{2})\s+(?<currency>\S+)\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})(?:\s+(?<tail>.*))?$");
            if (!detailMatch.Success)
            {
                return false;
            }

            dateTimeText = $"{startMatch.Groups["date"].Value} {startMatch.Groups["time"].Value}{detailMatch.Groups["second"].Value}";
            currencyText = detailMatch.Groups["currency"].Value;
            amountText = detailMatch.Groups["amount"].Value;
            balanceText = detailMatch.Groups["balance"].Value;
            var inlineTail = CleanPdfValue(detailMatch.Groups["tail"].Value);
            if (!string.IsNullOrWhiteSpace(inlineTail))
            {
                tailSegments.Add(inlineTail);
            }

            tailSegments.AddRange(group.Skip(2).Select(item => item.Text));
        }

        var (oppositeName, oppositeAccount, merchantName, summary, remark) = SplitCgbTail(tailSegments);
        var amount = ParseDoubleOrNull(amountText);
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.Currency = CleanPdfValue(currencyText);
        record.AccountTime = ParseDateTimeOrNull(dateTimeText);
        record.TradeMoney = amount;
        record.Balance = ParseDoubleOrNull(balanceText);
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        record.MerchantName = merchantName;
        record.ProductBrief = summary;
        record.ProductName = summary;
        record.TradeChannel = summary;
        record.Remark = remark;
        record.TradeExplain = remark;
        ApplySignedAmountColumns(record, amount);

        SetFlowRaw(record, "交易日期", record.AccountTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? dateTimeText);
        SetFlowRaw(record, "币种", record.Currency);
        SetFlowRaw(record, "发生额", amountText);
        SetFlowRaw(record, "交易金额", amountText);
        SetFlowRaw(record, "活期余额", balanceText);
        SetFlowRaw(record, "账户余额", balanceText);
        SetFlowRaw(record, "对手户名", record.OppositeUsername);
        SetFlowRaw(record, "对方姓名", record.OppositeUsername);
        SetFlowRaw(record, "对手账号", record.OppositeAccount);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "商户名称", record.MerchantName);
        SetFlowRaw(record, "摘要", record.ProductBrief);
        SetFlowRaw(record, "交易渠道", record.TradeChannel);
        SetFlowRaw(record, "附言", record.Remark);
        return true;
    }

    private static bool TryParseHuaxiaRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var text = JoinGroupText(group);
        var match = Regex.Match(text, @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<summary>.+?)\s+(?<amount>[+-]?\d[\d,]*\.\d{2})\s+(?<balance>[+-]?\d[\d,]*\.\d{2})(?:\s+(?<tail>.*))?$");
        if (!match.Success)
        {
            return false;
        }

        var (tradeUnit, oppositeName, oppositeAccount, oppositeBank, remark) = SplitHuaxiaTail(match.Groups["tail"].Value);
        var amount = ParseDoubleOrNull(match.Groups["amount"].Value);
        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull(match.Groups["date"].Value);
        record.ProductBrief = CollapseChineseSeparatedWords(match.Groups["summary"].Value);
        record.ProductName = record.ProductBrief;
        record.TradeMoney = amount;
        record.Balance = ParseDoubleOrNull(match.Groups["balance"].Value);
        record.TradePlace = tradeUnit;
        record.NetNum = tradeUnit;
        record.OppositeUsername = oppositeName;
        record.OppositeAccount = oppositeAccount;
        record.OppositeBank = oppositeBank;
        record.Remark = remark;
        record.TradeExplain = remark;
        ApplySignedAmountColumns(record, amount);

        SetFlowRaw(record, "记账日期", match.Groups["date"].Value);
        SetFlowRaw(record, "交易日期", match.Groups["date"].Value);
        SetFlowRaw(record, "摘要", record.ProductBrief);
        SetFlowRaw(record, "交易金额", match.Groups["amount"].Value);
        SetFlowRaw(record, "余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "账户余额", match.Groups["balance"].Value);
        SetFlowRaw(record, "交易机构", record.TradePlace);
        SetFlowRaw(record, "对方姓名", record.OppositeUsername);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "对方卡/账号", record.OppositeAccount);
        SetFlowRaw(record, "对方账号", record.OppositeAccount);
        SetFlowRaw(record, "对方开户行", record.OppositeBank);
        SetFlowRaw(record, "附言", record.Remark);
        return true;
    }

    private static bool TryParseWechatRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var lines = group.Select(item => CleanPdfValue(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        if (lines.Count < 5 || !Regex.IsMatch(lines[0], @"^\d{16,}$") || !Regex.IsMatch(lines[1], @"^\d+$"))
        {
            return false;
        }

        var date = lines[2];
        var time = lines[3];
        if (!Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$") || !Regex.IsMatch(time, @"^\d{2}:\d{2}:\d{2}$"))
        {
            return false;
        }

        var tokens = lines.Skip(4).SelectMany(SplitWords).ToList();
        var directionIndex = tokens.FindIndex(IsWechatDirectionToken);
        if (directionIndex <= 0)
        {
            return false;
        }

        var type = CollapseChineseSeparatedWords(string.Join(' ', tokens.Take(directionIndex)));
        var direction = CleanPdfValue(tokens[directionIndex]);
        var afterDirection = tokens.Skip(directionIndex + 1).ToList();
        var amountIndex = afterDirection.FindIndex(item => ParseDoubleOrNull(item).HasValue);
        if (amountIndex < 0)
        {
            return false;
        }

        var paymentMethod = CollapseChineseSeparatedWords(string.Join(' ', afterDirection.Take(amountIndex)));
        var rawAmount = afterDirection[amountIndex];
        var afterAmount = afterDirection.Skip(amountIndex + 1).ToList();
        var (counterparty, merchantOrder) = SplitWechatCounterpartyAndMerchant(afterAmount);
        var parsedAmount = ParseDoubleOrNull(rawAmount);
        var signedAmount = ApplyPaymentDirection(parsedAmount, direction);

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.SerialNum = string.Concat(lines[0], lines[1]);
        record.AccountTime = ParseDateTimeOrNull($"{date} {time}");
        record.ProductBrief = type;
        record.ProductName = type;
        record.IncomeAttribute = direction;
        record.IncomeFlag = direction;
        record.CashCheck = paymentMethod;
        record.TradeChannel = paymentMethod;
        record.TradeMoney = signedAmount;
        record.OppositeUsername = counterparty;
        record.MerchantName = merchantOrder;
        ApplySignedAmountColumns(record, signedAmount);

        SetFlowRaw(record, "交易单号", record.SerialNum);
        SetFlowRaw(record, "流水号", record.SerialNum);
        SetFlowRaw(record, "交易时间", record.AccountTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? $"{date} {time}");
        SetFlowRaw(record, "交易类型", record.ProductBrief);
        SetFlowRaw(record, "收/支/其他", direction);
        SetFlowRaw(record, "收入支出", direction);
        SetFlowRaw(record, "交易方式", record.CashCheck);
        SetFlowRaw(record, "金额", rawAmount);
        SetFlowRaw(record, "交易对方", record.OppositeUsername);
        SetFlowRaw(record, "对方户名", record.OppositeUsername);
        SetFlowRaw(record, "商户单号", record.MerchantName);
        return true;
    }

    private static bool TryParseAlipayRecord(
        IReadOnlyList<PdfTextLine> group,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        var lines = group.Select(item => CleanPdfValue(item.Text))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();

        if (lines.Count < 5)
        {
            return TryParseSingleLineAlipayRecord(lines, bank, user, out record);
        }

        var direction = string.Empty;
        var firstRest = string.Empty;
        var cursor = 1;
        var firstMatch = Regex.Match(lines[0], @"^(?<direction>收入|支出|不计收支)\s+(?<rest>.+)$");
        if (firstMatch.Success)
        {
            direction = firstMatch.Groups["direction"].Value;
            firstRest = firstMatch.Groups["rest"].Value;
        }
        else if (lines[0] == "不计" && lines.Count > 1 && lines[1] == "收支")
        {
            direction = "不计收支";
            firstRest = lines.Count > 2 ? lines[2] : string.Empty;
            cursor = 3;
        }
        else
        {
            return TryParseSingleLineAlipayRecord(lines, bank, user, out record);
        }

        var dateIndex = FindAlipayDateLineIndex(lines);
        if (dateIndex < 0 || dateIndex + 1 >= lines.Count)
        {
            return TryParseSingleLineAlipayRecord(lines, bank, user, out record);
        }

        var amountIndex = FindAlipayAmountLineIndex(lines, cursor - 1, dateIndex);
        if (amountIndex < 0)
        {
            return TryParseSingleLineAlipayRecord(lines, bank, user, out record);
        }

        var amountMatch = Regex.Match(lines[amountIndex], @"^(?:(?<payment>.+?)\s+)?(?<amount>\d[\d,]*\.\d{2})\s+(?<order>\S+)(?:\s+(?<tail>.*))?$");
        if (!amountMatch.Success)
        {
            return TryParseSingleLineAlipayRecord(lines, bank, user, out record);
        }

        var (counterparty, product) = SplitAlipayCounterpartyAndProduct(firstRest, lines.Skip(cursor).Take(amountIndex - cursor));
        var paymentMethod = CleanPdfValue(amountMatch.Groups["payment"].Value);
        var orderTail = new List<string> { amountMatch.Groups["order"].Value };
        var inlineTail = CleanPdfValue(amountMatch.Groups["tail"].Value);
        if (!string.IsNullOrWhiteSpace(inlineTail))
        {
            orderTail.AddRange(SplitWords(inlineTail));
        }

        orderTail.AddRange(lines.Skip(amountIndex + 1).Take(dateIndex - amountIndex - 1).SelectMany(SplitWords));
        var (tradeOrder, merchantOrder) = SplitAlipayOrderParts(orderTail);
        var parsedAmount = ParseDoubleOrNull(amountMatch.Groups["amount"].Value);
        var signedAmount = ApplyPaymentDirection(parsedAmount, direction);

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull($"{lines[dateIndex]} {lines[dateIndex + 1]}");
        record.SerialNum = tradeOrder;
        record.ProductBrief = product;
        record.ProductName = product;
        record.TradeChannel = paymentMethod;
        record.CashCheck = paymentMethod;
        record.OppositeUsername = counterparty;
        record.MerchantName = merchantOrder;
        record.TradeMoney = signedAmount;
        record.IncomeAttribute = direction;
        record.IncomeFlag = direction;
        record.Usage = direction;
        ApplySignedAmountColumns(record, signedAmount);

        if (direction == "不计收支")
        {
            record.CreditAmount = null;
            record.DebitAmount = null;
            record.IncomeAttribute = direction;
            record.IncomeFlag = direction;
        }

        SetFlowRaw(record, "商品说明", record.ProductBrief);
        SetFlowRaw(record, "交易单号", record.SerialNum);
        SetFlowRaw(record, "流水号", record.SerialNum);
        SetFlowRaw(record, "时间", record.AccountTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? $"{lines[dateIndex]} {lines[dateIndex + 1]}");
        SetFlowRaw(record, "交易时间", record.AccountTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? $"{lines[dateIndex]} {lines[dateIndex + 1]}");
        SetFlowRaw(record, "收入", direction == "收入" ? amountMatch.Groups["amount"].Value : string.Empty);
        SetFlowRaw(record, "支出", direction == "支出" ? amountMatch.Groups["amount"].Value : string.Empty);
        SetFlowRaw(record, "金额", amountMatch.Groups["amount"].Value);
        SetFlowRaw(record, "资金渠道", record.TradeChannel);
        SetFlowRaw(record, "收/付款方式", record.TradeChannel);
        SetFlowRaw(record, "交易对方", record.OppositeUsername);
        SetFlowRaw(record, "商家订单号", record.MerchantName);
        SetFlowRaw(record, "收支", direction);
        SetFlowRaw(record, "支付宝分类", direction);
        return true;
    }

    private static IReadOnlyList<PdfPositionedRow> BuildPositionedRows(
        IReadOnlyList<PdfTextWord> words,
        IReadOnlyList<PdfPositionedColumnSpec> columns,
        Func<PdfTextWord, bool> isHeaderWord,
        Func<string, bool> isFooterWord,
        string rowStartPattern,
        double rowStartLeftMax,
        double defaultHeaderBottom)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var rows = new List<PdfPositionedRow>();
        foreach (var pageGroup in words.GroupBy(item => item.PageNumber).OrderBy(group => group.Key))
        {
            var pageWords = pageGroup
                .Select(item => item with { Text = CleanPdfValue(item.Text) })
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .OrderBy(item => item.Top)
                .ThenBy(item => item.Left)
                .ToList();

            var headerBottom = Math.Max(
                defaultHeaderBottom,
                pageWords
                    .Where(isHeaderWord)
                    .Select(item => item.Bottom)
                    .DefaultIfEmpty(defaultHeaderBottom)
                    .Max()) + 0.5;
            var footerTop = pageWords
                .Where(item => item.Top > headerBottom && isFooterWord(item.Text))
                .Select(item => item.Top)
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            var rowStartWords = pageWords
                .Where(item => item.Top > headerBottom
                    && item.Top < footerTop
                    && item.Left < rowStartLeftMax
                    && Regex.IsMatch(item.Text, rowStartPattern))
                .OrderBy(item => item.Top)
                .ThenBy(item => item.Left)
                .ToList();

            for (var index = 0; index < rowStartWords.Count; index++)
            {
                var currentStart = rowStartWords[index];
                var bandTop = index == 0
                    ? headerBottom
                    : (rowStartWords[index - 1].Top + currentStart.Top) / 2d;
                var bandBottom = index + 1 < rowStartWords.Count
                    ? (currentStart.Top + rowStartWords[index + 1].Top) / 2d
                    : footerTop;

                var rowWords = pageWords
                    .Where(item => item.Top >= bandTop && item.Top < bandBottom)
                    .OrderBy(item => item.Top)
                    .ThenBy(item => item.Left)
                    .ToList();
                var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var column in columns)
                {
                    var columnWords = rowWords
                        .Where(item => GetHorizontalCenter(item) >= column.Left && GetHorizontalCenter(item) < column.Right)
                        .ToList();
                    var value = JoinPositionedCellWords(columnWords);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        cells[column.Key] = value;
                    }
                }

                rows.Add(new PdfPositionedRow(pageGroup.Key, currentStart.Top, cells));
            }
        }

        return rows;
    }

    private static bool IsCibCorporateTextRecordStart(string text)
    {
        return Regex.IsMatch(CleanPdfValue(text), @"^\d{4}-\d{2}-\d{2}\s+\d{2}:$");
    }

    private static bool IsCibCorporateTextIgnoredLine(string text)
    {
        var value = CleanPdfValue(text);
        return IsCommonIgnoredLine(value)
            || value.StartsWith("页号:", StringComparison.Ordinal)
            || value.Contains("兴业银行", StringComparison.Ordinal)
            || value.Contains("交易明细对账单", StringComparison.Ordinal)
            || value.StartsWith("打印日期:", StringComparison.Ordinal)
            || value.StartsWith("活期账号:", StringComparison.Ordinal)
            || value.StartsWith("户名:", StringComparison.Ordinal)
            || value.StartsWith("期末账户余额:", StringComparison.Ordinal)
            || value.StartsWith("交易时间 摘要", StringComparison.Ordinal)
            || value.StartsWith("本期总笔数", StringComparison.Ordinal)
            || value.StartsWith("本期总收入金额", StringComparison.Ordinal)
            || value.StartsWith("本期总支出金额", StringComparison.Ordinal)
            || value.StartsWith("重要提示", StringComparison.Ordinal)
            || value.StartsWith("elect_sign", StringComparison.Ordinal);
    }

    private static string JoinPositionedCellWords(IEnumerable<PdfTextWord> words)
    {
        return CleanPdfValue(string.Concat(words
            .OrderBy(item => item.Top)
            .ThenBy(item => item.Left)
            .Select(item => item.Text)));
    }

    private static string GetPositionedCell(PdfPositionedRow row, string key)
    {
        return row.Cells.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static bool IsSpdbCorporateHeaderWord(PdfTextWord word)
    {
        return word.Text is "交易日期" or "Transaction" or "交易流水号" or "Teller's" or "借方" or "Debit" or "贷方" or "Credit" or "账户余额" or "Balance" or "对手机构" or "对手名称" or "摘要代码" or "备注";
    }

    private static bool IsSpdbCorporateFooterWord(string text)
    {
        return text.StartsWith("Page", StringComparison.Ordinal)
            || text.StartsWith("本页小计", StringComparison.Ordinal)
            || text.StartsWith("期末余额", StringComparison.Ordinal)
            || text.StartsWith("Ending", StringComparison.Ordinal);
    }

    private static bool IsCiticCorporateHeaderWord(PdfTextWord word)
    {
        return word.Text is "交易日期" or "柜员交易号" or "摘要信息" or "对方账号" or "对方账户名称" or "动账资金分簿" or "借方发生额" or "贷方发生额" or "余额";
    }

    private static bool IsCiticCorporateFooterWord(string text)
    {
        return text.StartsWith("打印时间", StringComparison.Ordinal)
            || text.StartsWith("第", StringComparison.Ordinal)
            || text.StartsWith("中信银行", StringComparison.Ordinal);
    }

    private static IReadOnlyList<IReadOnlyList<PdfTextLine>> GroupCmbCorporateRecords(IReadOnlyList<PdfTextLine> lines)
    {
        var result = new List<IReadOnlyList<PdfTextLine>>();
        var current = new List<PdfTextLine>();
        var pendingPrefix = new List<PdfTextLine>();
        var filtered = lines
            .Where(line => !IsCmbCorporateIgnoredLine(line.Text))
            .ToList();

        for (var index = 0; index < filtered.Count; index++)
        {
            var line = filtered[index];
            var text = CleanPdfValue(line.Text);
            var nextText = index + 1 < filtered.Count ? CleanPdfValue(filtered[index + 1].Text) : string.Empty;
            if (Regex.IsMatch(text, @"^\d{8}\s+"))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                }

                current = [];
                if (pendingPrefix.Count > 0)
                {
                    current.AddRange(pendingPrefix);
                    pendingPrefix = [];
                }

                current.Add(line);
                continue;
            }

            if (IsCmbCorporateLeadingContinuation(text) && Regex.IsMatch(nextText, @"^\d{8}\s+"))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                    current = [];
                }

                pendingPrefix = [line];
                continue;
            }

            if (current.Count > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static bool IsCmbCorporateIgnoredLine(string text)
    {
        var value = CleanPdfValue(text);
        return IsCommonIgnoredLine(value)
            || value.Contains("招商银行", StringComparison.Ordinal)
            || value.Contains("账务明细清单", StringComparison.Ordinal)
            || value.Contains("Statement Of Account", StringComparison.Ordinal)
            || value.StartsWith("开户银行:", StringComparison.Ordinal)
            || value.StartsWith("账号:", StringComparison.Ordinal)
            || value.StartsWith("账户名称:", StringComparison.Ordinal)
            || value.StartsWith("日期 业务类型", StringComparison.Ordinal)
            || value.StartsWith("Date Business Type", StringComparison.Ordinal)
            || value.StartsWith("--------------------------------------------------------------------------------", StringComparison.Ordinal);
    }

    private static bool IsCmbCorporateLeadingContinuation(string text)
    {
        var value = CleanPdfValue(text);
        return Regex.IsMatch(value, @"^[A-Z0-9:._-]{12,}")
            || value.Contains("TX:", StringComparison.Ordinal)
            || value.Contains("实缴税", StringComparison.Ordinal);
    }

    private static IReadOnlyList<PdfTableRow> BuildBocCorporateRows(IReadOnlyList<PdfTextLine> lines)
    {
        var result = new List<PdfTableRow>();
        Dictionary<int, string>? currentCells = null;
        PdfTextLine? currentStartLine = null;

        foreach (var line in lines)
        {
            var text = CleanPdfValue(line.Text);
            if (!text.StartsWith('|'))
            {
                continue;
            }

            var cells = SplitPipeTableCells(text);
            if (cells.Count < 8 || IsBocCorporateTableHeader(cells))
            {
                continue;
            }

            if (Regex.IsMatch(cells[0], @"^\d+$"))
            {
                if (currentCells is not null && currentStartLine is not null)
                {
                    result.Add(new PdfTableRow(currentStartLine.PageNumber, currentStartLine.LineNumber, string.Empty, currentCells));
                }

                currentCells = cells.Select((value, index) => new { value, index })
                    .ToDictionary(item => item.index, item => item.value);
                currentStartLine = line;
                continue;
            }

            if (currentCells is not null)
            {
                for (var index = 0; index < cells.Count; index++)
                {
                    if (!string.IsNullOrWhiteSpace(cells[index]))
                    {
                        currentCells[index] = AppendPdfCellText(currentCells.TryGetValue(index, out var oldValue) ? oldValue : string.Empty, cells[index]);
                    }
                }
            }
        }

        if (currentCells is not null && currentStartLine is not null)
        {
            result.Add(new PdfTableRow(currentStartLine.PageNumber, currentStartLine.LineNumber, string.Empty, currentCells));
        }

        return result;
    }

    private static IReadOnlyList<string> SplitPipeTableCells(string text)
    {
        var value = CleanPdfValue(text).Trim();
        if (value.StartsWith('|'))
        {
            value = value[1..];
        }

        if (value.EndsWith('|'))
        {
            value = value[..^1];
        }

        return value.Split('|')
            .Select(CleanPdfValue)
            .ToList();
    }

    private static bool IsBocCorporateTableHeader(IReadOnlyList<string> cells)
    {
        return cells.Count == 0
            || cells[0] is "序号" or "No."
            || cells.Any(item => item.Contains("借方发生额", StringComparison.Ordinal) || item.Contains("贷方发生额", StringComparison.Ordinal))
            || cells.All(item => string.IsNullOrWhiteSpace(item) || Regex.IsMatch(item, @"^-+$"));
    }

    private static string AppendPdfCellText(string current, string value)
    {
        var left = CleanPdfValue(current);
        var right = CleanPdfValue(value);
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }

        return CleanPdfValue(string.Concat(left, right));
    }

    private static string GetTableCell(PdfTableRow row, int index)
    {
        return row.Cells.TryGetValue(index, out var value) ? CleanPdfValue(value) : string.Empty;
    }

    private static double? ApplyCorporateDebitCredit(
        FlowRecord record,
        string debitText,
        string creditText,
        string debitColumnName,
        string creditColumnName,
        string direction = "")
    {
        var debit = ParseDoubleOrNull(debitText);
        var credit = ParseDoubleOrNull(creditText);
        double? signedAmount = null;
        if (direction.Contains("支", StringComparison.Ordinal) && debit.HasValue)
        {
            signedAmount = 0 - Math.Abs(debit.Value);
        }
        else if (direction.Contains("收", StringComparison.Ordinal) && credit.HasValue)
        {
            signedAmount = Math.Abs(credit.Value);
        }
        else if (credit.HasValue && Math.Abs(credit.Value) > 0.005)
        {
            signedAmount = Math.Abs(credit.Value);
        }
        else if (debit.HasValue && Math.Abs(debit.Value) > 0.005)
        {
            signedAmount = 0 - Math.Abs(debit.Value);
        }
        else if (credit.HasValue && !debit.HasValue)
        {
            signedAmount = Math.Abs(credit.Value);
        }
        else if (debit.HasValue && !credit.HasValue)
        {
            signedAmount = 0 - Math.Abs(debit.Value);
        }

        record.TradeMoney = signedAmount;
        ApplySignedAmountColumns(record, signedAmount);
        SetFlowRaw(record, debitColumnName, debitText);
        SetFlowRaw(record, creditColumnName, creditText);
        return signedAmount;
    }

    private static void InferSpdbCorporateMoneyDirections(IList<FlowRecord> records)
    {
        double? previousBalance = null;
        foreach (var record in records)
        {
            if (!record.TradeMoney.HasValue)
            {
                previousBalance = record.Balance ?? previousBalance;
                continue;
            }

            if (record.DebitAmount.HasValue || record.CreditAmount.HasValue)
            {
                previousBalance = record.Balance ?? previousBalance;
                continue;
            }

            var amount = Math.Abs(record.TradeMoney.Value);
            var signedAmount = amount;
            if (previousBalance.HasValue && record.Balance.HasValue)
            {
                var delta = Math.Round(record.Balance.Value - previousBalance.Value, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(Math.Abs(delta) - amount) <= 0.02)
                {
                    signedAmount = delta;
                }
            }

            record.TradeMoney = signedAmount;
            ApplySignedAmountColumns(record, signedAmount);
            SetFlowRaw(record, "借方", signedAmount < 0 ? FormatMoney(Math.Abs(signedAmount)) : string.Empty);
            SetFlowRaw(record, "贷方", signedAmount > 0 ? FormatMoney(signedAmount) : string.Empty);
            previousBalance = record.Balance ?? previousBalance;
        }
    }

    private static string ExtractSpdbCorporateFallbackAmount(
        ref string summary,
        ref string remark,
        ref string oppositeName,
        ref string oppositeBank)
    {
        var fields = new[]
        {
            ("Remark", remark),
            ("Summary", summary),
            ("OppositeName", oppositeName),
            ("OppositeBank", oppositeBank)
        };

        foreach (var (field, value) in fields)
        {
            var amount = ExtractLastMoneyToken(value);
            if (string.IsNullOrWhiteSpace(amount))
            {
                continue;
            }

            var cleaned = RemoveLastMoneyToken(value, amount);
            switch (field)
            {
                case "Remark":
                    remark = cleaned;
                    break;
                case "Summary":
                    summary = cleaned;
                    break;
                case "OppositeName":
                    oppositeName = cleaned;
                    break;
                case "OppositeBank":
                    oppositeBank = cleaned;
                    break;
            }

            return amount;
        }

        return string.Empty;
    }

    private static string SelectSpdbCorporateAmountText(string value, double? previousBalance, double? balance)
    {
        var candidates = ExtractSpdbCorporateAmountCandidates(value);
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        if (previousBalance.HasValue && balance.HasValue)
        {
            var expectedAmount = Math.Abs(Math.Round(balance.Value - previousBalance.Value, 2, MidpointRounding.AwayFromZero));
            var matched = candidates
                .Select(item => new { Text = item, Amount = ParseDoubleOrNull(item) })
                .Where(item => item.Amount.HasValue)
                .FirstOrDefault(item => Math.Abs(Math.Abs(item.Amount!.Value) - expectedAmount) <= 0.02);
            if (matched is not null)
            {
                return matched.Text;
            }
        }

        return candidates[0];
    }

    private static IReadOnlyList<string> ExtractSpdbCorporateAmountCandidates(string value)
    {
        var result = new List<string>();
        foreach (Match match in Regex.Matches(CleanPdfValue(value), @"[+\-−]?\d[\d,]*\.\d{2}"))
        {
            var text = match.Value.Replace('−', '-');
            AddSpdbCorporateAmountCandidate(result, text);

            var unsigned = text.TrimStart('+', '-');
            var sign = text.StartsWith("-", StringComparison.Ordinal) ? "-" : string.Empty;
            var decimalIndex = unsigned.LastIndexOf('.');
            var commaIndex = unsigned.LastIndexOf(',');
            if (decimalIndex < 0)
            {
                continue;
            }

            if (commaIndex <= 0 || commaIndex > decimalIndex)
            {
                AddSpdbCorporateUnseparatedAmountCandidates(result, sign, unsigned);
                continue;
            }

            var beforeLastComma = unsigned[..commaIndex];
            if (beforeLastComma.Contains(",", StringComparison.Ordinal) || beforeLastComma.Length <= 3)
            {
                AddSpdbCorporateUnseparatedAmountCandidates(result, sign, unsigned);
                continue;
            }

            var afterLastComma = unsigned[commaIndex..];
            for (var length = 1; length <= 3 && length <= beforeLastComma.Length; length++)
            {
                var prefix = beforeLastComma[^length..].TrimStart('0');
                if (string.IsNullOrWhiteSpace(prefix))
                {
                    continue;
                }

                AddSpdbCorporateAmountCandidate(result, string.Concat(sign, prefix, afterLastComma));
            }
        }

        return result;
    }

    private static void AddSpdbCorporateUnseparatedAmountCandidates(ICollection<string> result, string sign, string unsigned)
    {
        if (unsigned.Contains(",", StringComparison.Ordinal))
        {
            return;
        }

        var decimalIndex = unsigned.LastIndexOf('.');
        if (decimalIndex <= 0)
        {
            return;
        }

        var integerPart = unsigned[..decimalIndex];
        if (integerPart.Length <= 4)
        {
            return;
        }

        var decimalPart = unsigned[decimalIndex..];
        for (var length = 1; length <= 8 && length <= integerPart.Length; length++)
        {
            var prefix = integerPart[^length..].TrimStart('0');
            if (string.IsNullOrWhiteSpace(prefix))
            {
                continue;
            }

            AddSpdbCorporateAmountCandidate(result, string.Concat(sign, prefix, decimalPart));
        }
    }

    private static void AddSpdbCorporateAmountCandidate(ICollection<string> result, string value)
    {
        var text = CleanPdfValue(value);
        if (string.IsNullOrWhiteSpace(text) || result.Contains(text))
        {
            return;
        }

        result.Add(text);
    }

    private static string ExtractLastMoneyToken(string value)
    {
        var matches = Regex.Matches(CleanPdfValue(value), @"[+\-−]?\d[\d,]*\.\d{2}");
        return matches.Count == 0 ? string.Empty : matches[^1].Value.Replace('−', '-');
    }

    private static string RemoveLastMoneyToken(string value, string amountText)
    {
        var text = CleanPdfValue(value);
        var index = text.LastIndexOf(amountText, StringComparison.Ordinal);
        if (index < 0 && amountText.Contains('-', StringComparison.Ordinal))
        {
            index = text.LastIndexOf(amountText.Replace('-', '−'), StringComparison.Ordinal);
        }

        return index < 0
            ? text
            : CleanPdfValue(text.Remove(index, amountText.Length));
    }

    private static string NormalizeCurrencyText(string value)
    {
        var text = CleanPdfValue(value);
        if (text.Contains("人民币", StringComparison.Ordinal) || text.Contains("CNY", StringComparison.OrdinalIgnoreCase))
        {
            return "人民币";
        }

        return text;
    }

    private static string FormatShortBankDate(string value)
    {
        var text = CleanPdfValue(value);
        return DateTime.TryParseExact(text, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : text;
    }

    private static bool IsCorporateSerialToken(string value)
    {
        var text = CleanPdfValue(value);
        return text.Length >= 4
            && Regex.IsMatch(text, @"^[0-9A-Za-z._:-]+$")
            && !Regex.IsMatch(text, @"^[+-]?\d[\d,]*\.\d{2}$");
    }

    private static bool IsPdfMoneyToken(string value)
    {
        return Regex.IsMatch(CleanPdfValue(value), @"^[+-]?\d[\d,]*\.\d{2}$");
    }

    private static (string OppositeName, string OppositeBank) SplitBocCorporateCounterparty(string value)
    {
        var text = CleanPdfValue(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        var separatorIndex = text.IndexOf('/');
        if (separatorIndex > 0 && separatorIndex < text.Length - 1)
        {
            return (
                CleanPdfValue(text[..separatorIndex]),
                CleanPdfValue(text[(separatorIndex + 1)..]));
        }

        return (text, string.Empty);
    }

    private static IReadOnlyList<IReadOnlyList<PdfTextLine>> GroupLinesByStart(
        IReadOnlyList<PdfTextLine> lines,
        Func<string, bool> isStart,
        Func<string, bool> isIgnored)
    {
        var result = new List<IReadOnlyList<PdfTextLine>>();
        var current = new List<PdfTextLine>();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (isIgnored(text))
            {
                continue;
            }

            if (isStart(text))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                }

                current = [line];
                continue;
            }

            if (current.Count > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<PdfTextLine>> GroupIcbcRecords(IReadOnlyList<PdfTextLine> lines)
    {
        var result = new List<IReadOnlyList<PdfTextLine>>();
        var current = new List<PdfTextLine>();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (IsIcbcIgnoredLine(text))
            {
                continue;
            }

            if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$"))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                }

                current = [line];
                continue;
            }

            if (current.Count > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<PdfTextLine>> GroupCcbRecords(IReadOnlyList<PdfTextLine> lines)
    {
        var filtered = lines
            .Where(line => !IsCcbIgnoredLine(line.Text.Trim()))
            .ToList();
        var result = new List<IReadOnlyList<PdfTextLine>>();
        var current = new List<PdfTextLine>();
        var pendingPrefix = new List<PdfTextLine>();

        for (var index = 0; index < filtered.Count; index++)
        {
            var line = filtered[index];
            var text = line.Text.Trim();
            var nextText = index + 1 < filtered.Count ? filtered[index + 1].Text.Trim() : string.Empty;
            if (IsCcbRecordStart(text) || IsCcbRecordStartWithoutSummary(text) || IsCcbSplitRecordStart(text, nextText))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                }

                current = [];
                if (pendingPrefix.Count > 0)
                {
                    current.AddRange(pendingPrefix);
                    pendingPrefix = [];
                }

                current.Add(line);
                continue;
            }

            if (IsCcbLeadingLineForSummary(text) && IsCcbRecordStartWithoutSummary(nextText))
            {
                pendingPrefix = [line];
                continue;
            }

            if (current.Count > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<PdfTextLine>> GroupCmbcRecords(IReadOnlyList<PdfTextLine> lines)
    {
        var result = new List<IReadOnlyList<PdfTextLine>>();
        var current = new List<PdfTextLine>();
        var voucherParts = new List<string>();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (IsCmbcIgnoredLine(text))
            {
                continue;
            }

            var voucherMatch = Regex.Match(text, @"^卡\s+(?<part>\d+)$");
            if (voucherMatch.Success)
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                    current = [];
                }

                voucherParts = [voucherMatch.Groups["part"].Value];
                continue;
            }

            if (voucherParts.Count == 1 && Regex.IsMatch(text, @"^\d{4,}$"))
            {
                voucherParts.Add(text);
                continue;
            }

            if (Regex.IsMatch(text, @"^\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2}\s+"))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                }

                var voucherText = voucherParts.Count > 0
                    ? new PdfTextLine(line.PageNumber, line.LineNumber, string.Concat(voucherParts))
                    : new PdfTextLine(line.PageNumber, line.LineNumber, string.Empty);
                current = [voucherText, line];
                voucherParts = [];
                continue;
            }

            if (current.Count > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<PdfTextLine>> GroupPsbcRecords(IReadOnlyList<PdfTextLine> lines)
    {
        var result = new List<IReadOnlyList<PdfTextLine>>();
        var current = new List<PdfTextLine>();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (IsPsbcIgnoredLine(text))
            {
                continue;
            }

            if (IsPsbcRecordStart(text))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                }

                current = [line];
                continue;
            }

            if (current.Count > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<PdfTextLine>> GroupWechatRecords(IReadOnlyList<PdfTextLine> lines)
    {
        var result = new List<IReadOnlyList<PdfTextLine>>();
        var current = new List<PdfTextLine>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (IsWechatRecordStartAt(lines, index))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                }

                current = [lines[index]];
                continue;
            }

            var text = CleanPdfValue(lines[index].Text);
            if (current.Count > 0 && IsWechatFooterStartLine(text))
            {
                result.Add(current);
                current = [];
                continue;
            }

            if (current.Count > 0 && !IsWechatIgnoredLine(text))
            {
                current.Add(lines[index]);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<PdfTextLine>> GroupAlipayRecords(IReadOnlyList<PdfTextLine> lines)
    {
        var result = new List<IReadOnlyList<PdfTextLine>>();
        var current = new List<PdfTextLine>();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (IsAlipayIgnoredLine(text))
            {
                continue;
            }

            if (IsAlipayRecordStart(text))
            {
                if (current.Count > 0)
                {
                    result.Add(current);
                }

                current = [line];
                continue;
            }

            if (current.Count > 0)
            {
                current.Add(line);
            }
        }

        if (current.Count > 0)
        {
            result.Add(current);
        }

        return result;
    }

    private static bool IsBocRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\s+");
    }

    private static bool IsCcbRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^\d+\s+.+?\s+\d{8}\s+[+-]?\d[\d,]*\.\d{2}\s+[+-]?\d[\d,]*\.\d{2}(?:\s+.*)?$");
    }

    private static bool IsAbcRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^\d{8}\s+\d{6}\s+");
    }

    private static bool IsPingAnRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^\d+\s+\d{4}-\d{2}-\d{2}\s+[+-]?\d[\d,]*(?:\.\d+)?\s+[+-]?\d[\d,]*(?:\.\d+)?\s+");
    }

    private static bool IsSpdbRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^\d{8}\s+\d{6}\s+\d{8,}$");
    }

    private static bool IsCibRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}\s+\d{2}");
    }

    private static bool IsPsbcRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$");
    }

    private static bool IsCgbRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:?$")
            || Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\s+");
    }

    private static bool IsHuaxiaRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}\s+");
    }

    private static bool IsWechatRecordStartAt(IReadOnlyList<PdfTextLine> lines, int index)
    {
        if (index + 3 >= lines.Count)
        {
            return false;
        }

        var first = CleanPdfValue(lines[index].Text);
        var second = CleanPdfValue(lines[index + 1].Text);
        var date = CleanPdfValue(lines[index + 2].Text);
        var time = CleanPdfValue(lines[index + 3].Text);
        return Regex.IsMatch(first, @"^\d{16,}$")
            && Regex.IsMatch(second, @"^\d{6,}$")
            && Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$")
            && Regex.IsMatch(time, @"^\d{2}:\d{2}:\d{2}$");
    }

    private static bool IsAlipayRecordStart(string text)
    {
        return Regex.IsMatch(text, @"^(收入|支出|不计收支)\s+")
            || text == "不计";
    }

    private static bool IsCcbRecordStartWithoutSummary(string text)
    {
        return Regex.IsMatch(text, @"^\d+\s+\d{8}\s+[+-]?\d[\d,]*\.\d{2}\s+[+-]?\d[\d,]*\.\d{2}(?:\s+.*)?$");
    }

    private static bool IsCcbSplitRecordStart(string text, string nextText)
    {
        return Regex.IsMatch(text, @"^\d+\s+\S+")
            && !Regex.IsMatch(text, @"\d{8}\s+[+-]?\d[\d,]*\.\d{2}")
            && Regex.IsMatch(nextText, @"^\S+\s+\d{8}\s+[+-]?\d[\d,]*\.\d{2}\s+[+-]?\d[\d,]*\.\d{2}(?:\s+.*)?$");
    }

    private static bool IsCcbAnyRecordStart(IReadOnlyList<PdfTextLine> group, int index)
    {
        var text = group[index].Text.Trim();
        var nextText = index + 1 < group.Count ? group[index + 1].Text.Trim() : string.Empty;
        return IsCcbRecordStart(text)
            || IsCcbRecordStartWithoutSummary(text)
            || IsCcbSplitRecordStart(text, nextText);
    }

    private static bool IsCcbLeadingLineForSummary(string text)
    {
        return text.Contains('/', StringComparison.Ordinal)
            && Regex.IsMatch(text, @"\d{6,}.*\/|\/.*\d{6,}");
    }

    private static bool IsBocIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("中国银行交易流水明细清单", StringComparison.Ordinal)
            || text.Contains("交易区间：", StringComparison.Ordinal)
            || text.Contains("借记卡号：", StringComparison.Ordinal)
            || text.Contains("按收支筛选：", StringComparison.Ordinal)
            || text.Contains("记账日期", StringComparison.Ordinal)
            || text.Contains("温馨提示", StringComparison.Ordinal);
    }

    private static bool IsIcbcIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("中国工商银行借记账户历史明细", StringComparison.Ordinal)
            || text.Contains("请扫描二维码", StringComparison.Ordinal)
            || text.Contains("识别明细真伪", StringComparison.Ordinal)
            || text.Contains("卡号", StringComparison.Ordinal)
            || text.Contains("交易日期", StringComparison.Ordinal)
            || text.Contains("本页支出算术合计", StringComparison.Ordinal)
            || text.Contains("本页收入算术合计", StringComparison.Ordinal)
            || text.Contains("本页交易笔数", StringComparison.Ordinal)
            || text.Contains("下单时间", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^中国工商银行\s+\d+\s+\d{4}-\d{2}-\d{2}")
            || (text.Contains("共", StringComparison.Ordinal) && text.Contains("页", StringComparison.Ordinal));
    }

    private static bool IsCcbIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("中国建设银行个人活期账户全部交易明细", StringComparison.Ordinal)
            || text.Contains("卡号/账号:", StringComparison.Ordinal)
            || text.Contains("当前时间段收支金额合计", StringComparison.Ordinal)
            || text.Contains("序号 摘要", StringComparison.Ordinal)
            || text.Contains("生成时间：", StringComparison.Ordinal)
            || text.Contains("温馨提示", StringComparison.Ordinal);
    }

    private static bool IsCmbcIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("个人账户对账单", StringComparison.Ordinal)
            || text.Contains("客户姓名:", StringComparison.Ordinal)
            || text.Contains("开户机构:", StringComparison.Ordinal)
            || text.Contains("凭证类型", StringComparison.Ordinal)
            || text.Contains("打印渠道:", StringComparison.Ordinal)
            || text.Contains("交易总额", StringComparison.Ordinal);
    }

    private static bool IsAbcIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("中国农业银行账户活期交易明细清单", StringComparison.Ordinal)
            || text.Contains("户名：", StringComparison.Ordinal)
            || text.Contains("币种：", StringComparison.Ordinal)
            || text.Contains("起止日期：", StringComparison.Ordinal)
            || text.Contains("交易日期 交易时间", StringComparison.Ordinal)
            || text.Contains("明细内容仅供参考", StringComparison.Ordinal);
    }

    private static bool IsPingAnIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text == "PAB"
            || text == "*"
            || text.Contains("清单编号", StringComparison.Ordinal)
            || text.Contains("List Number", StringComparison.Ordinal)
            || text.Contains("平安银行个人账户交易明细清单", StringComparison.Ordinal)
            || text.Contains("Transaction Details List of Personal Account", StringComparison.Ordinal)
            || text.Contains("序号No.", StringComparison.Ordinal)
            || text.Contains("余额Balance", StringComparison.Ordinal)
            || text.Contains("交易对手信息", StringComparison.Ordinal)
            || text.Contains("Counterparty", StringComparison.Ordinal)
            || text.Contains("Information", StringComparison.Ordinal)
            || text.Contains("当前第", StringComparison.Ordinal)
            || text.Contains("Page ", StringComparison.Ordinal)
            || text.Contains("银行签章", StringComparison.Ordinal)
            || text.Contains("Account Name:", StringComparison.Ordinal)
            || text.Contains("A/C No:", StringComparison.Ordinal)
            || text.Contains("Deposit Type:", StringComparison.Ordinal)
            || text.Contains("Currency:", StringComparison.Ordinal)
            || text.Contains("Sub branch:", StringComparison.Ordinal)
            || text.Contains("Operator Sub branch:", StringComparison.Ordinal)
            || text.Contains("Transaction Starting-Ending Date:", StringComparison.Ordinal)
            || text.Contains("Details Scope:", StringComparison.Ordinal)
            || text.Contains("户名：", StringComparison.Ordinal)
            || text.Contains("卡号/账号：", StringComparison.Ordinal)
            || text.Contains("存款类型：", StringComparison.Ordinal)
            || text.Contains("币种：", StringComparison.Ordinal)
            || text.Contains("开户行：", StringComparison.Ordinal)
            || text.Contains("受理行：", StringComparison.Ordinal)
            || text.Contains("交易起止日期：", StringComparison.Ordinal)
            || text.Contains("明细范围：", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^JYLS\S+\s+\d{4}-\d{2}-\d{2}")
            || Regex.IsMatch(text, @"^\S+\s+\d{10,}\s+活期$")
            || Regex.IsMatch(text, @"^(RMB|人民币)\s+.*银行")
            || Regex.IsMatch(text, @"^\d{8}\s*-\s*\d{8}\s+全部交易$")
            || text == "平安银行总行";
    }

    private static bool IsSpdbIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("上海浦东发展银行个人客户交易流水专用回单", StringComparison.Ordinal)
            || text.Contains("Transaction Statement of Shanghai Pudong Development Bank", StringComparison.Ordinal)
            || text.Contains("户名:", StringComparison.Ordinal)
            || text.Contains("Name:", StringComparison.Ordinal)
            || text.Contains("币种:", StringComparison.Ordinal)
            || text.Contains("Currency:", StringComparison.Ordinal)
            || text.Contains("交易日期", StringComparison.Ordinal)
            || text.Contains("Date", StringComparison.Ordinal)
            || text.Contains("交易时间", StringComparison.Ordinal)
            || text.Contains("Time", StringComparison.Ordinal)
            || text.Contains("交易账号", StringComparison.Ordinal)
            || text.Contains("Transaction", StringComparison.Ordinal)
            || text.Contains("Account", StringComparison.Ordinal)
            || text.Contains("交易名称", StringComparison.Ordinal)
            || text.Contains("Name", StringComparison.Ordinal)
            || text.Contains("交易金额", StringComparison.Ordinal)
            || text.Contains("Amount", StringComparison.Ordinal)
            || text.Contains("账户余额", StringComparison.Ordinal)
            || text.Contains("Balance", StringComparison.Ordinal)
            || text.Contains("对手姓名", StringComparison.Ordinal)
            || text.Contains("Counter", StringComparison.Ordinal)
            || text.Contains("Party", StringComparison.Ordinal)
            || text.Contains("对手账号", StringComparison.Ordinal)
            || text.Contains("Opponent", StringComparison.Ordinal)
            || text.Contains("交易摘要", StringComparison.Ordinal)
            || text.Contains("Summary", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^第\d+页/共\d+页")
            || Regex.IsMatch(text, @"^Page\s+\d+\s+of\s+\d+$", RegexOptions.IgnoreCase);
    }

    private static bool IsCibIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("Transaction Time", StringComparison.Ordinal)
            || text.Contains("Accounting Date", StringComparison.Ordinal)
            || text.Contains("Transaction Type", StringComparison.Ordinal)
            || text.Contains("Expenditure", StringComparison.Ordinal)
            || text.Contains("Income", StringComparison.Ordinal)
            || text.Contains("Transaction Amount", StringComparison.Ordinal)
            || text.Contains("Account Balance", StringComparison.Ordinal)
            || text.Contains("Transaction Usage", StringComparison.Ordinal)
            || text.Contains("Counterparty", StringComparison.Ordinal)
            || text.Contains("交易时间", StringComparison.Ordinal)
            || text.Contains("记账日期", StringComparison.Ordinal)
            || text.Contains("交易金额", StringComparison.Ordinal)
            || text.Contains("账户余额", StringComparison.Ordinal)
            || text.Contains("交易用途", StringComparison.Ordinal)
            || text.Contains("对方账户/对方银行", StringComparison.Ordinal)
            || text.Contains("兴业银行交易流水", StringComparison.Ordinal)
            || text.Contains("Industrial Bank Transaction Details", StringComparison.Ordinal)
            || text.Contains("Account Name:", StringComparison.Ordinal)
            || text.Contains("Account No.:", StringComparison.Ordinal)
            || text.Contains("Currency:", StringComparison.Ordinal)
            || text.Contains("Account Type:", StringComparison.Ordinal)
            || text.Contains("Income and Expenditure Categories:", StringComparison.Ordinal)
            || text.Contains("Transfer Amount Range:", StringComparison.Ordinal)
            || text.Contains("Use/Remark:", StringComparison.Ordinal)
            || text.Contains("Print Time:", StringComparison.Ordinal)
            || text.Contains("户 名：", StringComparison.Ordinal)
            || text.Contains("账号：", StringComparison.Ordinal)
            || text.Contains("币 种：", StringComparison.Ordinal)
            || text.Contains("账户类型：", StringComparison.Ordinal)
            || text.Contains("收支类别：", StringComparison.Ordinal)
            || text.Contains("转账金额区间：", StringComparison.Ordinal)
            || text.Contains("对方户名：", StringComparison.Ordinal)
            || text.Contains("对方账号：", StringComparison.Ordinal)
            || text.Contains("交易类型：", StringComparison.Ordinal)
            || text.Contains("用途/备注：", StringComparison.Ordinal)
            || text.Contains("打印日期：", StringComparison.Ordinal)
            || text.Contains("说明：", StringComparison.Ordinal)
            || text.Contains("交易明细涉及您的个人隐私", StringComparison.Ordinal)
            || text.Contains("交易明细内容仅供个人参考", StringComparison.Ordinal)
            || text.Contains("sealPos", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^(支出|收入)\s+¥")
            || Regex.IsMatch(text, @"^兴业银行\s+\d{4}-\d{2}-\d{2}")
            || Regex.IsMatch(text, @"^行\s+\d{4}-\d{2}-\d{2}")
            || Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}-\d{4}-\d{2}-\d{2}$");
    }

    private static bool IsPsbcIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("中国邮政储蓄银行借记账户历史明细", StringComparison.Ordinal)
            || text.Contains("卡号/账号：", StringComparison.Ordinal)
            || text.Contains("交易时间 子账号", StringComparison.Ordinal)
            || text.Contains("温馨提示", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^第\s*\d+\s*页\s*/\s*共\s*\d+\s*页$");
    }

    private static bool IsCgbIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("广发银行股份有限公司", StringComparison.Ordinal)
            || text.Contains("个人账户交易流水证明", StringComparison.Ordinal)
            || text.Contains("交易时间 币种", StringComparison.Ordinal)
            || text.Contains("账号:", StringComparison.Ordinal)
            || text.Contains("户名:", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^日期：\d{4}-\d{2}-\d{2}")
            || Regex.IsMatch(text, @"^第\s*\d+\s*页");
    }

    private static bool IsHuaxiaIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("Accounting Date", StringComparison.Ordinal)
            || text.Contains("Description", StringComparison.Ordinal)
            || text.Contains("Transaction Amount", StringComparison.Ordinal)
            || text.Contains("Transaction Unit", StringComparison.Ordinal)
            || text.Contains("Counterparty", StringComparison.Ordinal)
            || text.Contains("Remarks", StringComparison.Ordinal)
            || text is "记账日期" or "摘要" or "交易金额" or "余额" or "交易机构" or "对方姓名" or "对方卡/账号" or "对方开户行" or "附言"
            || text.Contains("根据信息保护", StringComparison.Ordinal)
            || text.Contains("流水中手机号", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^总\d+页\s+第\d+页$")
            || Regex.IsMatch(text, @"^第\d+页\s*/\s*共\d+页$");
    }

    private static bool IsWechatIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || IsWechatFooterLine(text)
            || text.Contains("微信支付交易明细证明", StringComparison.Ordinal)
            || text.Contains("交易单号", StringComparison.Ordinal)
            || text.Contains("具体交易明细", StringComparison.Ordinal)
            || text.Contains("交易明细对应时间段", StringComparison.Ordinal)
            || text.Contains("兹证明", StringComparison.Ordinal)
            || text.Contains("币种", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^编号\s*:?");
    }

    private static bool IsWechatFooterStartLine(string text)
    {
        var value = CleanPdfValue(text);
        return value.StartsWith("\u8BF4\u660E", StringComparison.Ordinal)
            || value.StartsWith("\u8BF4\u660E\uFF1A", StringComparison.Ordinal);
    }

    private static bool IsWechatFooterLine(string text)
    {
        var value = CleanPdfValue(text);
        if (IsWechatFooterStartLine(value))
        {
            return true;
        }

        if (Regex.IsMatch(value, @"^\d+\.\s*")
            && (value.Contains("\u5FAE\u4FE1\u652F\u4ED8\u4EA4\u6613\u660E\u7EC6\u8BC1\u660E", StringComparison.Ordinal)
                || value.Contains("\u96F6\u94B1", StringComparison.Ordinal)
                || value.Contains("\u96F6\u94B1\u901A", StringComparison.Ordinal)
                || value.Contains("\u7EA2\u5305", StringComparison.Ordinal)
                || value.Contains("\u8F6C\u8D26", StringComparison.Ordinal)
                || value.Contains("\u4E8C\u7EF4\u7801", StringComparison.Ordinal)
                || value.Contains("\u4EA4\u6613\u5BF9\u65B9", StringComparison.Ordinal)
                || value.Contains("\u6536\u5165", StringComparison.Ordinal)
                || value.Contains("\u652F\u51FA", StringComparison.Ordinal)
                || value.Contains("\u8D22\u4ED8\u901A", StringComparison.Ordinal)))
        {
            return true;
        }

        return value is "\u8D22\u4ED8\u901A\u652F\u4ED8\u79D1\u6280\u6709\u9650\u516C\u53F8" or "\u76D6\u7AE0"
            || value.Contains("\u8D22\u4ED8\u901A\u652F\u4ED8\u79D1\u6280\u6709\u9650\u516C\u53F8\u76D6\u7AE0", StringComparison.Ordinal);
    }

    private static bool IsAlipayIgnoredLine(string text)
    {
        return IsCommonIgnoredLine(text)
            || text.Contains("支付宝支付科技有限公司", StringComparison.Ordinal)
            || text.Contains("交易流水证明", StringComparison.Ordinal)
            || text.Contains("兹证明", StringComparison.Ordinal)
            || text.Contains("币种：", StringComparison.Ordinal)
            || text.Contains("交易时间段：", StringComparison.Ordinal)
            || text.Contains("交易类型：", StringComparison.Ordinal)
            || text.Contains("收/支 交易对方", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^编号:");
    }

    private static bool IsCommonIgnoredLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return text.Contains("END", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, @"^[-—_=\s]+$")
            || Regex.IsMatch(text, @"^第\s*\d+\s*页")
            || Regex.IsMatch(text, @"^-?\s*第\d+页/共\d+页\s*-?$")
            || Regex.IsMatch(text, @"^页数[:：]");
    }

    private static string JoinGroupText(IEnumerable<PdfTextLine> group)
    {
        return Regex.Replace(string.Join(' ', group.Select(item => item.Text.Trim())), @"\s+", " ").Trim();
    }

    private static IReadOnlyList<string> SplitWords(string text)
    {
        return Regex.Split(text.Trim(), @"\s+")
            .Select(CleanPdfValue)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static string CleanPdfValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = Regex.Replace(value.Replace('\u00a0', ' ').Replace('\0', '.').Trim(), @"\s+", " ");
        return Regex.IsMatch(text, @"^[-—_\s]+$") ? string.Empty : text;
    }

    private static DateTime? ParseDateTimeOrNull(string value)
    {
        return PdfImportTabularMapper.TryParseDateTime(value, out var dateTime) ? dateTime : null;
    }

    private static double? ParseDoubleOrNull(string value)
    {
        return PdfImportTabularMapper.TryParseDouble(value, out var number) ? number : null;
    }

    private static int FindLastCounterpartyAccountIndex(IReadOnlyList<string> tokens)
    {
        for (var index = tokens.Count - 1; index >= 0; index--)
        {
            var token = tokens[index];
            var plain = token.Replace("*", string.Empty, StringComparison.Ordinal)
                .Replace("/", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal);

            if (plain.Length >= 8 && Regex.IsMatch(token, @"^[0-9A-Za-z*\/-]+$"))
            {
                return index;
            }
        }

        return -1;
    }

    private static void SplitBocBeforeCounterparty(
        IReadOnlyList<string> tokens,
        out string tradePlace,
        out string remark,
        out string oppositeName)
    {
        tradePlace = string.Empty;
        remark = string.Empty;
        oppositeName = string.Empty;

        if (tokens.Count == 0)
        {
            return;
        }

        var dashIndexes = tokens
            .Select((value, index) => new { value, index })
            .Where(item => string.IsNullOrWhiteSpace(CleanPdfValue(item.value)))
            .Select(item => item.index)
            .ToList();

        if (dashIndexes.Count >= 2)
        {
            tradePlace = CleanPdfValue(string.Join(' ', tokens.Take(dashIndexes[0])));
            remark = CleanPdfValue(string.Join(' ', tokens.Skip(dashIndexes[0] + 1).Take(dashIndexes[1] - dashIndexes[0] - 1)));
            oppositeName = CleanPdfValue(string.Join(' ', tokens.Skip(dashIndexes[1] + 1)));
            return;
        }

        if (dashIndexes.Count == 1)
        {
            var dashIndex = dashIndexes[0];
            tradePlace = CleanPdfValue(string.Join(' ', tokens.Take(dashIndex)));
            var afterDash = tokens.Skip(dashIndex + 1)
                .Select(CleanPdfValue)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();

            if (afterDash.Count >= 2)
            {
                remark = afterDash[0];
                oppositeName = CleanPdfValue(string.Join(' ', afterDash.Skip(1)));
                return;
            }

            if (afterDash.Count == 1)
            {
                oppositeName = afterDash[0];
                return;
            }
        }

        var cleanedTokens = tokens
            .Select(CleanPdfValue)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        if (cleanedTokens.Count == 0)
        {
            return;
        }

        if (IsBocTradePlaceToken(cleanedTokens[0]))
        {
            var placeEnd = 0;
            while (placeEnd + 1 < cleanedTokens.Count && IsBocTradePlaceContinuation(cleanedTokens[placeEnd + 1]))
            {
                placeEnd++;
            }

            tradePlace = CleanPdfValue(string.Join(' ', cleanedTokens.Take(placeEnd + 1)));
            SplitBocRemarkAndOpposite(cleanedTokens.Skip(placeEnd + 1).ToList(), requireRemarkCue: true, out remark, out oppositeName);
            return;
        }

        SplitBocRemarkAndOpposite(cleanedTokens, requireRemarkCue: false, out remark, out oppositeName);
    }

    private static void SplitBocRemarkAndOpposite(
        IReadOnlyList<string> tokens,
        bool requireRemarkCue,
        out string remark,
        out string oppositeName)
    {
        remark = string.Empty;
        oppositeName = string.Empty;
        if (tokens.Count == 0)
        {
            return;
        }

        if (tokens.Count == 1)
        {
            oppositeName = tokens[0];
            return;
        }

        if (!requireRemarkCue || IsBocLikelyRemark(tokens[0]))
        {
            remark = tokens[0];
            oppositeName = CleanPdfValue(string.Join(' ', tokens.Skip(1)));
            return;
        }

        oppositeName = CleanPdfValue(string.Join(' ', tokens));
    }

    private static bool IsBocTradePlaceToken(string value)
    {
        return value.Contains("银行", StringComparison.Ordinal)
            || value.Contains("支行", StringComparison.Ordinal)
            || value.Contains("分行", StringComparison.Ordinal)
            || value.Contains("营业部", StringComparison.Ordinal);
    }

    private static bool IsBocTradePlaceContinuation(string value)
    {
        return value.Contains("营业部", StringComparison.Ordinal)
            || value.Contains("支行", StringComparison.Ordinal)
            || value.Contains("分行", StringComparison.Ordinal);
    }

    private static bool IsBocLikelyRemark(string value)
    {
        return value.Contains("转账", StringComparison.Ordinal)
            || value.Contains("付款", StringComparison.Ordinal)
            || value.Contains("财付通", StringComparison.Ordinal)
            || value.Contains("微信", StringComparison.Ordinal)
            || value.Contains("支付宝", StringComparison.Ordinal);
    }

    private static (string Place, string OppositeAccount, string OppositeName) SplitCcbPlaceAndCounterparty(string rest)
    {
        var text = CleanPdfValue(rest);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var slashIndex = text.LastIndexOf('/');
        if (slashIndex <= 0 || slashIndex >= text.Length - 1)
        {
            return (text, string.Empty, string.Empty);
        }

        var beforeSlash = text[..slashIndex].Trim();
        var oppositeName = text[(slashIndex + 1)..].Trim();
        var accountMatch = Regex.Match(beforeSlash, @"(?<account>[0-9A-Za-z*]{4,})$");
        if (!accountMatch.Success)
        {
            return (beforeSlash, string.Empty, oppositeName);
        }

        var place = beforeSlash[..accountMatch.Index].Trim();
        return (CleanPdfValue(place), CleanPdfValue(accountMatch.Groups["account"].Value), CleanPdfValue(oppositeName));
    }

    private static string ExtractCcbPrefixSummary(string prefix)
    {
        var text = CleanPdfValue(prefix);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var match = Regex.Match(text, @"^(?<summary>.+?)\s+[0-9A-Za-z*]{6,}\/");
        return match.Success ? CleanPdfValue(match.Groups["summary"].Value) : text;
    }

    private static string ExtractCcbRestSummary(string rest)
    {
        var text = CleanPdfValue(rest);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var accountIndex = Regex.Match(text, @"[0-9A-Za-z*]{6,}\/");
        return accountIndex.Success
            ? CleanPdfValue(text[..accountIndex.Index])
            : text;
    }

    private static string CleanCcbSummary(string value)
    {
        var text = CleanPdfValue(value);
        return Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", string.Empty);
    }

    private static (string Name, string Account, string Bank) SplitNameAccountBank(string rest)
    {
        var text = CleanPdfValue(rest);
        var slashIndex = text.IndexOf('/');
        if (slashIndex > 0 && slashIndex < text.Length - 1)
        {
            var name = text[..slashIndex].Trim();
            var afterSlash = SplitWords(text[(slashIndex + 1)..]).ToList();
            if (afterSlash.Count == 0)
            {
                return (CleanPdfValue(name), string.Empty, string.Empty);
            }

            var slashAccount = afterSlash[0];
            var slashBankStart = 1;
            if (slashAccount.EndsWith("-", StringComparison.Ordinal) && afterSlash.Count > 1 && Regex.IsMatch(afterSlash[1], @"^\d+$"))
            {
                slashAccount = string.Concat(slashAccount, afterSlash[1]);
                slashBankStart = 2;
            }

            return (
                CleanPdfValue(name),
                CleanPdfValue(slashAccount),
                CleanPdfValue(string.Join(' ', afterSlash.Skip(slashBankStart))));
        }

        var tokens = SplitWords(rest).ToList();
        if (tokens.Count == 0)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var accountIndex = FindLastCounterpartyAccountIndex(tokens);
        if (accountIndex < 0)
        {
            return (CleanPdfValue(string.Join(' ', tokens)), string.Empty, string.Empty);
        }

        var account = tokens[accountIndex];
        var bankStart = accountIndex + 1;
        if (account.EndsWith("-", StringComparison.Ordinal) && accountIndex + 1 < tokens.Count && Regex.IsMatch(tokens[accountIndex + 1], @"^\d+$"))
        {
            account = string.Concat(account, tokens[accountIndex + 1]);
            bankStart = accountIndex + 2;
        }

        return (
            CleanPdfValue(string.Join(' ', tokens.Take(accountIndex))),
            CleanPdfValue(account),
            CleanPdfValue(string.Join(' ', tokens.Skip(bankStart))));
    }

    private static (string OppositeName, string LogNumber, string Channel, string Remark) SplitAbcRemainder(string rest)
    {
        var tokens = SplitWords(rest).ToList();
        if (tokens.Count == 0)
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var logIndex = tokens.FindIndex(item => Regex.IsMatch(item, @"^[A-Za-z]?\d{6,}$"));
        if (logIndex < 0)
        {
            return (CleanPdfValue(string.Join(' ', tokens)), string.Empty, string.Empty, string.Empty);
        }

        var channelIndex = logIndex + 1;
        return (
            CleanPdfValue(string.Join(' ', tokens.Take(logIndex))),
            CleanPdfValue(tokens[logIndex]),
            channelIndex < tokens.Count ? CleanPdfValue(tokens[channelIndex]) : string.Empty,
            CleanPdfValue(string.Join(' ', tokens.Skip(channelIndex + 1))));
    }

    private static (string Note, string OppositeName, string OppositeAccount, string OppositeBank) SplitPingAnCounterparty(IReadOnlyList<string> tokens)
    {
        var cleanedTokens = tokens
            .Select(CleanPdfValue)
            .Where(item => !string.IsNullOrWhiteSpace(item) && item != "/")
            .ToList();
        if (cleanedTokens.Count == 0)
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var originalText = NormalizeCounterpartyText(string.Join(' ', tokens));
        var noteSeparatorIndex = originalText.IndexOf('/');
        if (noteSeparatorIndex > 0 && noteSeparatorIndex < originalText.Length - 1)
        {
            var note = originalText[..noteSeparatorIndex].Trim('/');
            var counterparty = originalText[(noteSeparatorIndex + 1)..].Trim('/');
            var (slashName, slashAccount) = SplitTrailingNameAccount(counterparty);
            return (CleanPdfValue(note), slashName, slashAccount, string.Empty);
        }

        if (cleanedTokens.Count > 1 && IsPingAnNoteToken(cleanedTokens[0]))
        {
            var note = CleanPdfValue(cleanedTokens[0]);
            var counterparty = NormalizeCounterpartyText(string.Join(' ', cleanedTokens.Skip(1)));
            var (name, account) = SplitTrailingNameAccount(counterparty);
            return (note, name, account, string.Empty);
        }

        var lastPair = SplitTrailingNameAccount(NormalizeCounterpartyText(cleanedTokens[^1]));
        if (!string.IsNullOrWhiteSpace(lastPair.Account) && cleanedTokens.Count > 1)
        {
            return (
                CollapseChineseSeparatedWords(string.Join(' ', cleanedTokens.Take(cleanedTokens.Count - 1))),
                lastPair.Name,
                lastPair.Account,
                string.Empty);
        }

        var allCounterparty = NormalizeCounterpartyText(string.Join(' ', cleanedTokens));
        var (fullName, fullAccount) = SplitTrailingNameAccount(allCounterparty);
        return (string.Empty, fullName, fullAccount, string.Empty);
    }

    private static bool IsPingAnNoteToken(string value)
    {
        return value is "抖音支付" or "财付通" or "支付宝" or "批量还款" or "支付-协议支付" or "支付-退货交易";
    }

    private static (string OppositeName, string OppositeAccount, string Summary) SplitSpdbCounterparty(
        string afterAmount,
        IReadOnlyList<string> afterLines)
    {
        var nameParts = new List<string>();
        var accountParts = new List<string>();
        var summaryParts = new List<string>();
        var accountStarted = false;
        var summaryStarted = false;

        AddSpdbCounterpartySegment(afterAmount);
        foreach (var line in afterLines)
        {
            AddSpdbCounterpartySegment(line);
        }

        return (
            CollapseChineseSeparatedWords(string.Join(' ', nameParts)),
            CleanPdfValue(string.Concat(accountParts)),
            CollapseChineseSeparatedWords(string.Join(' ', summaryParts)));

        void AddSpdbCounterpartySegment(string raw)
        {
            var segment = CleanPdfValue(raw);
            if (string.IsNullOrWhiteSpace(segment))
            {
                return;
            }

            if (summaryStarted)
            {
                summaryParts.Add(segment);
                return;
            }

            var inlineAccountMatch = Regex.Match(segment, @"^(?<name>.+?)\s+(?<account>\d{3,})$");
            if (!accountStarted && inlineAccountMatch.Success)
            {
                nameParts.Add(inlineAccountMatch.Groups["name"].Value);
                accountParts.Add(inlineAccountMatch.Groups["account"].Value);
                accountStarted = true;
                return;
            }

            if (Regex.IsMatch(segment, @"^\d+$"))
            {
                accountParts.Add(segment);
                accountStarted = true;
                return;
            }

            if (accountStarted)
            {
                summaryParts.Add(segment);
                summaryStarted = true;
                return;
            }

            nameParts.Add(segment);
        }
    }

    private static string NormalizeCounterpartyText(string value)
    {
        return CollapseChineseSeparatedWords(value).Trim('-', '/');
    }

    private static (string Name, string Account) SplitTrailingNameAccount(string value)
    {
        var text = NormalizeCounterpartyText(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        var hyphenMatch = Regex.Match(text, @"^(?<name>.+)-(?<account>[0-9A-Za-z]{5,})$");
        if (hyphenMatch.Success)
        {
            return (
                CleanPdfValue(hyphenMatch.Groups["name"].Value.Trim('-', '/')),
                CleanPdfValue(hyphenMatch.Groups["account"].Value));
        }

        var spaceMatch = Regex.Match(text, @"^(?<name>.+?)\s+(?<account>[0-9A-Za-z]{5,})$");
        if (spaceMatch.Success)
        {
            return (
                CleanPdfValue(spaceMatch.Groups["name"].Value.Trim('-', '/')),
                CleanPdfValue(spaceMatch.Groups["account"].Value));
        }

        return (CleanPdfValue(text.Trim('-', '/')), string.Empty);
    }

    private static (string Usage, string OppositeName, string OppositeAccount, string OppositeBank) SplitCibTail(string tail)
    {
        var tokens = SplitWords(tail).ToList();
        if (tokens.Count == 0)
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty);
        }

        var accountIndex = FindLastCounterpartyAccountIndex(tokens);
        if (accountIndex < 0)
        {
            return (CollapseChineseSeparatedWords(string.Join(' ', tokens)), string.Empty, string.Empty, string.Empty);
        }

        var beforeAccount = CollapseChineseSeparatedWords(string.Join(' ', tokens.Take(accountIndex)));
        var account = CleanPdfValue(tokens[accountIndex]);
        var bank = CollapseChineseSeparatedWords(string.Join(' ', tokens.Skip(accountIndex + 1)));
        var (usage, name) = SplitCibUsageAndName(beforeAccount);
        return (usage, name, account, bank);
    }

    private static (string Usage, string Name) SplitCibUsageAndName(string value)
    {
        var text = CollapseChineseSeparatedWords(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        foreach (var cue in new[] { "扫码支付收单资金汇总清算", "收单手续费费用", "精灵信使服务收入" })
        {
            var cueIndex = text.LastIndexOf(cue, StringComparison.Ordinal);
            if (cueIndex > 0)
            {
                return (
                    CleanPdfValue(text[..cueIndex]),
                    CleanPdfValue(text[cueIndex..]));
            }
        }

        return (string.Empty, text);
    }

    private static (string OppositeName, string OppositeAccount, string Summary) SplitEverbrightTail(string tail)
    {
        var tokens = SplitWords(tail).ToList();
        var accountIndex = FindFirstCounterpartyAccountIndex(tokens);
        if (accountIndex < 0)
        {
            return (string.Empty, string.Empty, CollapseChineseSeparatedWords(string.Join(' ', tokens)));
        }

        return (
            CollapseChineseSeparatedWords(string.Join(' ', tokens.Take(accountIndex))),
            CleanPdfValue(tokens[accountIndex]),
            CollapseChineseSeparatedWords(string.Join(' ', tokens.Skip(accountIndex + 1))));
    }

    private static (string OppositeName, string OppositeAccount, string MerchantName, string Summary, string Remark) SplitCgbTail(IEnumerable<string> segments)
    {
        var tokens = segments.SelectMany(SplitWords).ToList();
        var accountIndex = FindFirstCounterpartyAccountIndex(tokens);
        if (accountIndex < 0)
        {
            var text = CollapseChineseSeparatedWords(string.Join(' ', tokens));
            return (string.Empty, string.Empty, string.Empty, text, string.Empty);
        }

        var oppositeName = CollapseChineseSeparatedWords(string.Join(' ', tokens.Take(accountIndex)));
        var oppositeAccount = CleanPdfValue(tokens[accountIndex]);
        var afterAccount = tokens.Skip(accountIndex + 1).ToList();
        var summaryIndex = afterAccount.FindIndex(IsCgbSummaryToken);
        if (summaryIndex < 0)
        {
            return (
                oppositeName,
                oppositeAccount,
                CollapseChineseSeparatedWords(string.Join(' ', afterAccount)),
                string.Empty,
                string.Empty);
        }

        return (
            oppositeName,
            oppositeAccount,
            CollapseChineseSeparatedWords(string.Join(' ', afterAccount.Take(summaryIndex))),
            CleanPdfValue(afterAccount[summaryIndex]),
            CollapseChineseSeparatedWords(string.Join(' ', afterAccount.Skip(summaryIndex + 1))));
    }

    private static bool IsCgbSummaryToken(string value)
    {
        return value is "银联入账" or "入金" or "网银入账" or "快捷支付" or "结息转入"
            or "行内转入" or "行内转出" or "转账" or "消费" or "退货";
    }

    private static (string TradeUnit, string OppositeName, string OppositeAccount, string OppositeBank, string Remark) SplitHuaxiaTail(string tail)
    {
        var tokens = SplitWords(tail).ToList();
        var accountIndex = FindFirstCounterpartyAccountIndex(tokens);
        if (accountIndex < 0)
        {
            return (string.Empty, CollapseChineseSeparatedWords(string.Join(' ', tokens)), string.Empty, string.Empty, string.Empty);
        }

        var (tradeUnit, oppositeName) = SplitHuaxiaUnitAndName(tokens.Take(accountIndex).ToList());
        var (oppositeBank, remark) = SplitHuaxiaBankAndRemark(tokens.Skip(accountIndex + 1).ToList());
        return (tradeUnit, oppositeName, CleanPdfValue(tokens[accountIndex]), oppositeBank, remark);
    }

    private static (string TradeUnit, string OppositeName) SplitHuaxiaUnitAndName(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var markerIndex = -1;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (IsHuaxiaTradeUnitEndToken(tokens[index]))
            {
                markerIndex = index;
            }
        }

        if (markerIndex >= 0 && markerIndex < tokens.Count - 1)
        {
            return (
                CollapseChineseSeparatedWords(string.Join(' ', tokens.Take(markerIndex + 1))),
                CollapseChineseSeparatedWords(string.Join(' ', tokens.Skip(markerIndex + 1))));
        }

        return (string.Empty, CollapseChineseSeparatedWords(string.Join(' ', tokens)));
    }

    private static bool IsHuaxiaTradeUnitEndToken(string value)
    {
        return value.Contains("支行", StringComparison.Ordinal)
            || value.Contains("分行", StringComparison.Ordinal)
            || value.Contains("营业部", StringComparison.Ordinal)
            || value.Contains("清算业务组", StringComparison.Ordinal);
    }

    private static (string OppositeBank, string Remark) SplitHuaxiaBankAndRemark(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var cueIndex = -1;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (IsHuaxiaRemarkToken(tokens[index]))
            {
                cueIndex = index;
                break;
            }
        }

        if (cueIndex >= 0)
        {
            return (
                CollapseChineseSeparatedWords(string.Join(' ', tokens.Take(cueIndex))),
                CollapseChineseSeparatedWords(string.Join(' ', tokens.Skip(cueIndex))));
        }

        return (CollapseChineseSeparatedWords(string.Join(' ', tokens)), string.Empty);
    }

    private static bool IsHuaxiaRemarkToken(string value)
    {
        return value is "跨行转出" or "转账" or "银联无卡支付" or "网联平台协议支付业务";
    }

    private static bool IsWechatDirectionToken(string value)
    {
        return value is "收入" or "支出" or "其他" or "不计收支";
    }

    private static (string Counterparty, string MerchantOrder) SplitWechatCounterpartyAndMerchant(IReadOnlyList<string> tokens)
    {
        var cleanedTokens = tokens
            .Select(CleanPdfValue)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        if (cleanedTokens.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var slashIndex = cleanedTokens.IndexOf("/");
        if (slashIndex >= 0)
        {
            return (
                CollapseChineseSeparatedWords(string.Join(' ', cleanedTokens.Take(slashIndex))),
                "/");
        }

        var merchantStart = cleanedTokens.FindIndex(item => Regex.IsMatch(item, @"^\d{8,}$"));
        if (merchantStart <= 0)
        {
            return (CollapseChineseSeparatedWords(string.Join(' ', cleanedTokens)), string.Empty);
        }

        var merchantTokens = cleanedTokens
            .Skip(merchantStart)
            .TakeWhile(IsWechatMerchantOrderToken)
            .ToList();

        return (
            CollapseChineseSeparatedWords(string.Join(' ', cleanedTokens.Take(merchantStart))),
            string.Concat(merchantTokens));
    }

    private static bool IsWechatMerchantOrderToken(string value)
    {
        var text = CleanPdfValue(value);
        return text == "/" || Regex.IsMatch(text, @"^[A-Za-z0-9][A-Za-z0-9_./-]*$");
    }

    private static (string Counterparty, string Product) SplitAlipayCounterpartyAndProduct(string firstRest, IEnumerable<string> continuationLines)
    {
        var firstTokens = SplitWords(firstRest).ToList();
        if (firstTokens.Count == 0)
        {
            return (string.Empty, CollapseChineseSeparatedWords(string.Join(' ', continuationLines)));
        }

        var counterpartyParts = new List<string> { firstTokens[0] };
        var productParts = new List<string>();
        if (firstTokens.Count > 1)
        {
            productParts.Add(string.Join(' ', firstTokens.Skip(1)));
        }

        foreach (var line in continuationLines.Select(CleanPdfValue).Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            if (ShouldAppendAlipayCounterparty(counterpartyParts, productParts, line))
            {
                counterpartyParts.Add(line);
            }
            else
            {
                productParts.Add(line);
            }
        }

        return (
            CollapseChineseSeparatedWords(string.Join(' ', counterpartyParts)),
            CollapseChineseSeparatedWords(string.Join(' ', productParts)));
    }

    private static bool TryParseSingleLineAlipayRecord(
        IReadOnlyList<string> lines,
        Bank bank,
        BankUser user,
        out FlowRecord record)
    {
        record = new FlowRecord();
        if (lines.Count is 0 or > 18)
        {
            return false;
        }

        var tokens = SplitWords(string.Join(' ', lines)).ToList();
        if (tokens.Count < 8 || !TryGetAlipayCompactDirection(tokens, out var direction, out var cursor))
        {
            return false;
        }

        var dateIndex = FindAlipayCompactDateIndex(tokens);
        if (dateIndex < 0 || dateIndex + 1 >= tokens.Count || !Regex.IsMatch(tokens[dateIndex + 1], @"^\d{2}:\d{2}:\d{2}$"))
        {
            return false;
        }

        var amountIndex = FindAlipayCompactAmountIndex(tokens, cursor, dateIndex);
        if (amountIndex <= cursor || amountIndex + 1 >= dateIndex)
        {
            return false;
        }

        var beforePayment = tokens.Skip(cursor).Take(amountIndex - cursor).ToList();
        if (beforePayment.Count == 0)
        {
            return false;
        }

        var (counterparty, product, paymentMethod) = SplitAlipayCompactBeforeAmount(beforePayment);
        var orderParts = tokens.Skip(amountIndex + 1).Take(dateIndex - amountIndex - 1).ToList();
        var (tradeOrder, merchantOrder) = SplitAlipayOrderParts(orderParts);
        var parsedAmount = ParseDoubleOrNull(tokens[amountIndex]);
        var signedAmount = ApplyPaymentDirection(parsedAmount, direction);

        record.BankId = bank.Id;
        record.BankUserId = user.Id;
        record.Account = FirstNotBlank(user.AccountNo, user.CardNo);
        record.AccountTime = ParseDateTimeOrNull($"{tokens[dateIndex]} {tokens[dateIndex + 1]}");
        record.SerialNum = tradeOrder;
        record.ProductBrief = product;
        record.ProductName = product;
        record.TradeChannel = paymentMethod;
        record.CashCheck = paymentMethod;
        record.OppositeUsername = counterparty;
        record.MerchantName = merchantOrder;
        record.TradeMoney = signedAmount;
        record.IncomeAttribute = direction;
        record.IncomeFlag = direction;
        record.Usage = direction;
        ApplySignedAmountColumns(record, signedAmount);

        if (direction == "不计收支")
        {
            record.CreditAmount = null;
            record.DebitAmount = null;
            record.IncomeAttribute = direction;
            record.IncomeFlag = direction;
        }

        SetFlowRaw(record, "商品说明", record.ProductBrief);
        SetFlowRaw(record, "交易单号", record.SerialNum);
        SetFlowRaw(record, "流水号", record.SerialNum);
        SetFlowRaw(record, "时间", record.AccountTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? $"{tokens[dateIndex]} {tokens[dateIndex + 1]}");
        SetFlowRaw(record, "交易时间", record.AccountTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? $"{tokens[dateIndex]} {tokens[dateIndex + 1]}");
        SetFlowRaw(record, "收入", direction == "收入" ? tokens[amountIndex] : string.Empty);
        SetFlowRaw(record, "支出", direction == "支出" ? tokens[amountIndex] : string.Empty);
        SetFlowRaw(record, "金额", tokens[amountIndex]);
        SetFlowRaw(record, "资金渠道", record.TradeChannel);
        SetFlowRaw(record, "收/付款方式", record.TradeChannel);
        SetFlowRaw(record, "交易对方", record.OppositeUsername);
        SetFlowRaw(record, "收支", direction);
        SetFlowRaw(record, "支付宝分类", direction);
        return true;
    }

    private static bool TryGetAlipayCompactDirection(IReadOnlyList<string> tokens, out string direction, out int cursor)
    {
        direction = string.Empty;
        cursor = 0;
        if (tokens.Count == 0)
        {
            return false;
        }

        if (tokens[0] is "收入" or "支出" or "不计收支")
        {
            direction = tokens[0];
            cursor = 1;
            return true;
        }

        if (tokens.Count > 1 && tokens[0] == "不计" && tokens[1] == "收支")
        {
            direction = "不计收支";
            cursor = 2;
            return true;
        }

        return false;
    }

    private static int FindAlipayCompactDateIndex(IReadOnlyList<string> tokens)
    {
        for (var index = tokens.Count - 2; index >= 0; index--)
        {
            if (Regex.IsMatch(tokens[index], @"^\d{4}-\d{2}-\d{2}$")
                && Regex.IsMatch(tokens[index + 1], @"^\d{2}:\d{2}:\d{2}$"))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindAlipayCompactAmountIndex(IReadOnlyList<string> tokens, int startIndex, int endIndex)
    {
        for (var index = endIndex - 1; index >= Math.Max(0, startIndex); index--)
        {
            if (Regex.IsMatch(tokens[index], @"^\d[\d,]*\.\d{2}$"))
            {
                return index;
            }
        }

        return -1;
    }

    private static (string Counterparty, string Product, string PaymentMethod) SplitAlipayCompactBeforeAmount(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var paymentStart = FindAlipayCompactPaymentStartIndex(tokens);
        if (paymentStart < 0 && tokens.Count > 1)
        {
            paymentStart = tokens.Count - 1;
        }

        var detailTokens = paymentStart < 0
            ? tokens.ToList()
            : tokens.Take(paymentStart).ToList();
        var paymentTokens = paymentStart < 0
            ? new List<string>()
            : tokens.Skip(paymentStart).ToList();

        if (detailTokens.Count == 0 && paymentTokens.Count > 0)
        {
            detailTokens.Add(paymentTokens[0]);
            paymentTokens.RemoveAt(0);
        }

        var (counterparty, product) = SplitAlipayCounterpartyAndProduct(
            detailTokens[0],
            detailTokens.Skip(1));

        return (
            counterparty,
            product,
            CollapseChineseSeparatedWords(string.Join(' ', paymentTokens)));
    }

    private static int FindAlipayCompactPaymentStartIndex(IReadOnlyList<string> tokens)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (IsAlipayPaymentStartToken(tokens[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsAlipayPaymentStartToken(string value)
    {
        var text = CleanPdfValue(value);
        return text.Contains("账户余额", StringComparison.Ordinal)
            || text.Contains("余额宝", StringComparison.Ordinal)
            || text.Contains("花呗", StringComparison.Ordinal)
            || text.Contains("银行储蓄", StringComparison.Ordinal)
            || text.Contains("银行信用", StringComparison.Ordinal)
            || text.Contains("储蓄卡", StringComparison.Ordinal)
            || text.Contains("信用卡", StringComparison.Ordinal)
            || text.Contains("银行卡", StringComparison.Ordinal);
    }

    private static bool ShouldAppendAlipayCounterparty(IReadOnlyList<string> counterpartyParts, IReadOnlyList<string> productParts, string line)
    {
        if (productParts.Count > 0)
        {
            return false;
        }

        var current = CollapseChineseSeparatedWords(string.Join(' ', counterpartyParts));
        var value = CleanPdfValue(line);
        if ((current.EndsWith("资金担保", StringComparison.Ordinal) && value == "户")
            || (current.EndsWith("公", StringComparison.Ordinal) && value == "司")
            || (current.EndsWith("有限公", StringComparison.Ordinal) && value == "司"))
        {
            return true;
        }

        return !current.EndsWith("公司", StringComparison.Ordinal)
            && (value.Contains("公司", StringComparison.Ordinal)
                || value.Contains("有限公司", StringComparison.Ordinal)
                || value.Contains("分公司", StringComparison.Ordinal)
                || value.Contains("商务", StringComparison.Ordinal)
                || value.Contains("科技", StringComparison.Ordinal));
    }

    private static int FindAlipayDateLineIndex(IReadOnlyList<string> lines)
    {
        for (var index = lines.Count - 2; index >= 0; index--)
        {
            if (Regex.IsMatch(lines[index], @"^\d{4}-\d{2}-\d{2}$")
                && Regex.IsMatch(lines[index + 1], @"^\d{2}:\d{2}:\d{2}$"))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindAlipayAmountLineIndex(IReadOnlyList<string> lines, int startIndex, int endIndex)
    {
        for (var index = Math.Max(0, startIndex); index < endIndex; index++)
        {
            if (Regex.IsMatch(lines[index], @"^(?:.+?\s+)?\d[\d,]*\.\d{2}\s+\S+"))
            {
                return index;
            }
        }

        return -1;
    }

    private static (string TradeOrder, string MerchantOrder) SplitAlipayOrderParts(IReadOnlyList<string> parts)
    {
        var cleanedParts = parts
            .Select(CleanPdfValue)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
        if (cleanedParts.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var merchantStart = cleanedParts.FindIndex(IsAlipayMerchantOrderStart);
        if (merchantStart < 0)
        {
            return (string.Concat(cleanedParts), string.Empty);
        }

        return (
            string.Concat(cleanedParts.Take(merchantStart)),
            string.Concat(cleanedParts.Skip(merchantStart)));
    }

    private static bool IsAlipayMerchantOrderStart(string value)
    {
        return value.StartsWith("T", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("C-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("F-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("NGF", StringComparison.OrdinalIgnoreCase);
    }

    private static double? ApplyPaymentDirection(double? amount, string direction)
    {
        if (!amount.HasValue)
        {
            return null;
        }

        return direction switch
        {
            "支出" => 0 - Math.Abs(amount.Value),
            "收入" => Math.Abs(amount.Value),
            _ => Math.Abs(amount.Value)
        };
    }

    private static int FindFirstCounterpartyAccountIndex(IReadOnlyList<string> tokens)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (IsCounterpartyAccountToken(tokens[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsCounterpartyAccountToken(string token)
    {
        var plain = CleanPdfValue(token)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return plain.Length >= 6 && Regex.IsMatch(CleanPdfValue(token), @"^[0-9A-Za-z*\/-]+$");
    }

    private static string CollapseChineseSeparatedWords(string value)
    {
        var text = CleanPdfValue(value);
        text = Regex.Replace(text, @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", string.Empty);
        text = Regex.Replace(text, @"\s*-\s*", "-");
        text = Regex.Replace(text, @"\s*/\s*", "/");
        text = Regex.Replace(text, @"(?<=\d)\s+(?=\d)", string.Empty);
        return CleanPdfValue(text);
    }

    private static string NormalizeBrokenDateTime(string value)
    {
        var text = CleanPdfValue(value);
        var match = Regex.Match(text, @"^(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>[\d.:]+)$");
        if (!match.Success)
        {
            return text;
        }

        var digits = Regex.Replace(match.Groups["time"].Value, @"\D", string.Empty);
        if (digits.Length < 6)
        {
            return text;
        }

        return $"{match.Groups["date"].Value} {digits[..2]}:{digits.Substring(2, 2)}:{digits.Substring(4, 2)}";
    }

    private static void InferCiticMoneyDirections(IList<FlowRecord> records)
    {
        double? previousBalance = null;
        foreach (var record in records)
        {
            if (!record.TradeMoney.HasValue)
            {
                previousBalance = record.Balance ?? previousBalance;
                continue;
            }

            var amount = Math.Abs(record.TradeMoney.Value);
            var signedAmount = amount;
            if (previousBalance.HasValue && record.Balance.HasValue)
            {
                var delta = Math.Round(record.Balance.Value - previousBalance.Value, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(Math.Abs(delta) - amount) <= 0.02)
                {
                    signedAmount = delta;
                }
            }
            else if (record.Balance.HasValue && Math.Abs(record.Balance.Value - amount) <= 0.02)
            {
                signedAmount = amount;
            }

            record.TradeMoney = signedAmount;
            ApplySignedAmountColumns(record, signedAmount);
            SetFlowRaw(record, "支取金额", signedAmount < 0 ? FormatMoney(Math.Abs(signedAmount)) : string.Empty);
            SetFlowRaw(record, "支出金额", signedAmount < 0 ? FormatMoney(Math.Abs(signedAmount)) : string.Empty);
            SetFlowRaw(record, "存入金额", signedAmount > 0 ? FormatMoney(signedAmount) : string.Empty);
            SetFlowRaw(record, "收入金额", signedAmount > 0 ? FormatMoney(signedAmount) : string.Empty);
            previousBalance = record.Balance ?? previousBalance;
        }
    }

    private static void InferDescendingBalanceMoneyDirections(IList<FlowRecord> records)
    {
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (!record.TradeMoney.HasValue)
            {
                continue;
            }

            var amount = Math.Abs(record.TradeMoney.Value);
            var signedAmount = amount;
            var nextOlderBalance = index + 1 < records.Count ? records[index + 1].Balance : null;
            if (record.Balance.HasValue && nextOlderBalance.HasValue)
            {
                var delta = Math.Round(record.Balance.Value - nextOlderBalance.Value, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(Math.Abs(delta) - amount) <= 0.02)
                {
                    signedAmount = delta;
                }
            }
            else if (record.Balance.HasValue && Math.Abs(record.Balance.Value - amount) <= 0.02)
            {
                signedAmount = amount;
            }

            record.TradeMoney = signedAmount;
            ApplySignedAmountColumns(record, signedAmount);
            SetFlowRaw(record, "支出金额", signedAmount < 0 ? FormatMoney(Math.Abs(signedAmount)) : string.Empty);
            SetFlowRaw(record, "存入金额", signedAmount > 0 ? FormatMoney(signedAmount) : string.Empty);
        }
    }

    private static void ApplySignedAmountColumns(FlowRecord record, double? amount)
    {
        if (!amount.HasValue)
        {
            return;
        }

        if (amount.Value > 0)
        {
            record.CreditAmount = Math.Abs(amount.Value);
            record.IncomeAttribute = "收入";
            record.IncomeFlag = "收入";
        }
        else if (amount.Value < 0)
        {
            record.DebitAmount = Math.Abs(amount.Value);
            record.IncomeAttribute = "支出";
            record.IncomeFlag = "支出";
        }
    }

    private static string FormatMoney(double value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static void SetUserNamed(BankUser user, Bank bank, string columnName, string value)
    {
        var cleaned = CleanPdfValue(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        var column = FindColumn(bank.Columns, columnName)
            ?? CreateBankUserColumn(columnName);
        if (column is not null)
        {
            PdfImportTabularMapper.SetEntityValue(user, column, cleaned);
        }

        user[columnName] = cleaned;
    }

    private static void SetFlowRaw(FlowRecord record, string columnName, string value)
    {
        var cleaned = CleanPdfValue(value);
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            record[columnName] = cleaned;
        }
    }

    private static bool GroupContainsMoney(IEnumerable<PdfTextLine> group)
    {
        return group.Any(line => Regex.IsMatch(line.Text, @"\d[\d,]*\.\d{2}"));
    }

    private static ColumnDefinition? FindColumn(IEnumerable<ColumnDefinition> columns, string columnName)
    {
        var normalized = PdfImportTabularMapper.NormalizeHeader(columnName);
        return columns.FirstOrDefault(column =>
            PdfImportTabularMapper.NormalizeHeader(column.Name ?? string.Empty) == normalized);
    }

    private static ColumnDefinition? CreateBankUserColumn(string columnName)
    {
        var (field, type) = ExcelColumnFieldResolver.ResolveBankUserField(columnName);
        if (string.IsNullOrWhiteSpace(field))
        {
            return null;
        }

        return new ColumnDefinition
        {
            Name = columnName,
            Field = field,
            Type = type,
            Width = ExcelColumnFieldResolver.GetBankUserColumnWidth(columnName, type),
            Show = true
        };
    }

    private static void AddParseWarning(PdfImportResult result, IReadOnlyList<PdfTextLine> group, string message)
    {
        if (group.Count == 0 || result.Issues.Count(item => item.Severity == PdfImportIssueSeverity.Warning) >= 20)
        {
            return;
        }

        var line = group[0];
        result.Issues.Add(new PdfImportIssue
        {
            Severity = PdfImportIssueSeverity.Warning,
            PageNumber = line.PageNumber,
            LineNumber = line.LineNumber,
            Message = message,
            RawText = JoinGroupText(group)
        });
    }

    private static string FirstNotBlank(params string?[] values)
    {
        return values.Select(CleanPdfValue).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
    }



    private static PdfImportResult ParseBankUsers(
        string path,
        Bank bank,
        PdfExtractedDocument document,
        bool requireUserData = true)
    {
        var rows = BuildRows(document.Lines);
        var columns = PdfImportTabularMapper.GetBankUserColumns(bank);
        var result = CreateResult(path, bank, PdfImportTarget.BankUsers, document);

        var rowMaps = rows.Select(item => item.Cells).ToList();
        var headerIndex = PdfImportTabularMapper.FindHeaderRow(rowMaps, columns);
        if (headerIndex >= 0)
        {
            var headerRow = rows[headerIndex];
            var importColumns = PdfImportTabularMapper.BuildImportUserColumns(headerRow.Cells, bank).ToList();
            var headerMap = PdfImportTabularMapper.CreateHeaderMap(headerRow.Cells, importColumns, ignoreIdColumn: true);
            foreach (var row in rows.Skip(headerIndex + 1))
            {
                if (!PdfImportTabularMapper.ContainsRowData(row.Cells, headerMap.Keys) || IsFooterLine(row.RawText))
                {
                    continue;
                }

                var user = BankUser.CreateDraft(bank);
                foreach (var (columnIndex, column) in headerMap)
                {
                    if (row.Cells.TryGetValue(columnIndex, out var rawValue))
                    {
                        PdfImportTabularMapper.SetEntityValue(user, column, rawValue);
                    }
                }

                NormalizeImportedUser(user, bank);
                if (HasUsefulUserData(user))
                {
                    result.Users.Add(user);
                }
            }
        }

        if (result.Users.Count == 0)
        {
            var user = BankUser.CreateDraft(bank);
            var matched = ApplyUserInfoFromKeyValues(document.Lines, bank, user);
            NormalizeImportedUser(user, bank);
            if (matched > 0 && HasUsefulUserData(user))
            {
                result.Users.Add(user);
                result.Issues.Add(new PdfImportIssue
                {
                    Severity = PdfImportIssueSeverity.Info,
                    Message = "未识别到用户表格，已按 PDF 中的标签字段生成 1 条用户信息。"
                });
            }
        }

        if (requireUserData && result.Users.Count == 0 && !result.HasBlockingErrors)
        {
            result.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Error,
                Message = "没有识别到可导入的用户信息。请确认 PDF 是文字型，并包含当前银行用户字段表头或标签。"
            });
        }

        return result;
    }

    private static PdfImportResult ParseFlowRecords(
        string path,
        Bank bank,
        BankUser bankUser,
        PdfExtractedDocument document,
        PdfImportTarget target = PdfImportTarget.FlowRecords)
    {
        var rows = BuildRows(document.Lines);
        var columns = PdfImportTabularMapper.GetFlowExportColumns(bank);
        var result = CreateResult(path, bank, target, document, bankUser);

        var rowMaps = rows.Select(item => item.Cells).ToList();
        var headerIndex = PdfImportTabularMapper.FindHeaderRow(rowMaps, columns, searchLimit: 60);
        if (headerIndex < 0)
        {
            result.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Error,
                Message = $"未找到 {bank.Name} 流水明细表头。当前通用解析器会匹配现有导出字段，后续可根据标准 PDF 增加银行专用解析器。",
                RawText = FirstTextPreview(document.Lines)
            });
            return result;
        }

        var headerRow = rows[headerIndex];
        var importColumns = PdfImportTabularMapper.BuildImportFlowColumns(headerRow.Cells, bank).ToList();
        var headerMap = PdfImportTabularMapper.CreateHeaderMap(headerRow.Cells, importColumns, ignoreIdColumn: true);
        var userMatched = ApplyUserInfoFromKeyValues(document.Lines.Where(item => IsBeforeRow(item, headerRow)), bank, bankUser);
        NormalizeImportedUser(bankUser, bank);
        if (userMatched > 0 && HasUsefulUserData(bankUser))
        {
            result.Users.Add(bankUser);
        }

        if (userMatched == 0)
        {
            result.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Info,
                Message = "PDF 表头前未识别到用户信息标签，将保留当前用户资料。"
            });
        }

        var warningCount = 0;
        FlowRecord? previousRecord = null;
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            if (IsFooterLine(row.RawText) || IsRepeatedHeader(row.Cells, headerMap))
            {
                continue;
            }

            if (!PdfImportTabularMapper.ContainsRowData(row.Cells, headerMap.Keys))
            {
                continue;
            }

            var record = new FlowRecord
            {
                BankId = bank.Id,
                BankUserId = bankUser.Id
            };

            foreach (var (columnIndex, column) in headerMap)
            {
                if (row.Cells.TryGetValue(columnIndex, out var rawValue))
                {
                    PdfImportTabularMapper.SetEntityValue(record, column, rawValue);
                }
            }

            MergeDateAndTimeFromExtraCells(record, row.Cells.Values);
            PdfImportTabularMapper.NormalizeImportedFlowRecord(record, bank, bankUser);

            if (!HasUsefulFlowData(record))
            {
                continue;
            }

            if (!record.AccountTime.HasValue && previousRecord?.AccountTime is { } previousTime)
            {
                record.AccountTime = previousTime;
            }

            if (!record.TradeMoney.HasValue && warningCount < 8)
            {
                result.Issues.Add(new PdfImportIssue
                {
                    Severity = PdfImportIssueSeverity.Warning,
                    PageNumber = row.PageNumber,
                    LineNumber = row.LineNumber,
                    Message = "该行未识别到交易金额，已保留其它字段供预览确认。",
                    RawText = row.RawText
                });
                warningCount++;
            }

            result.FlowRecords.Add(record);
            previousRecord = record;
        }

        ReindexFlowRecords(result.FlowRecords);
        if (result.FlowRecords.Count == 0 && !result.HasBlockingErrors)
        {
            result.Issues.Add(new PdfImportIssue
            {
                Severity = PdfImportIssueSeverity.Error,
                PageNumber = headerRow.PageNumber,
                LineNumber = headerRow.LineNumber,
                Message = "已找到流水表头，但没有识别到可导入的流水行。请提供该银行标准 PDF 后补充专用解析规则。",
                RawText = headerRow.RawText
            });
        }

        return result;
    }

    private static PdfImportResult CreateResult(
        string path,
        Bank bank,
        PdfImportTarget target,
        PdfExtractedDocument document,
        BankUser? user = null)
    {
        return new PdfImportResult
        {
            SourcePath = path,
            BankName = bank.Name,
            Target = target,
            User = user,
            PageCount = document.PageCount,
            RawTextPreview = BuildRawTextPreview(document.Lines),
            Issues = document.Issues.ToList()
        };
    }

    private static IReadOnlyList<PdfTableRow> BuildRows(IEnumerable<PdfTextLine> lines)
    {
        var rows = new List<PdfTableRow>();
        foreach (var line in lines)
        {
            var cells = SplitTableLine(line.Text);
            if (cells.Count == 0)
            {
                continue;
            }

            rows.Add(new PdfTableRow(
                line.PageNumber,
                line.LineNumber,
                line.Text,
                cells.Select((value, index) => new { value, index })
                    .ToDictionary(item => item.index + 1, item => item.value)));
        }

        return rows;
    }

    private static IReadOnlyList<string> SplitTableLine(string line)
    {
        var normalized = NormalizeLine(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var cells = Regex.Split(normalized, @"\t+|[|｜]+|\s{2,}|　+")
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();

        if (cells.Count <= 1 && normalized.Contains(' '))
        {
            cells = Regex.Split(normalized, @"\s+")
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

        return cells.Count == 0 ? [normalized] : cells;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(NormalizeLine)
            .Where(item => !string.IsNullOrWhiteSpace(item));
    }

    private static string NormalizeLine(string line)
    {
        return Regex.Replace((line ?? string.Empty).Replace('\u00a0', ' ').Replace('\0', '.').Trim(), @"[ ]{2,}", "  ");
    }

    private static int ApplyUserInfoFromKeyValues(IEnumerable<PdfTextLine> lines, Bank bank, BankUser user)
    {
        var columns = PdfImportTabularMapper.GetBankUserColumns(bank);
        var labels = columns
            .Select(item => item.Name ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var matched = 0;

        foreach (var column in columns)
        {
            if (string.IsNullOrWhiteSpace(column.Name))
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (!TryExtractLabeledValue(line.Text, column.Name, labels, out var value))
                {
                    continue;
                }

                PdfImportTabularMapper.SetEntityValue(user, column, value);
                matched++;
                break;
            }
        }

        return matched;
    }

    private static bool TryExtractLabeledValue(
        string line,
        string label,
        IReadOnlyCollection<string> labels,
        out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var index = line.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var rest = line[(index + label.Length)..].TrimStart(' ', '\t', ':', '：', '-', '—');
        if (string.IsNullOrWhiteSpace(rest))
        {
            return false;
        }

        value = TrimAtNextKnownLabel(rest, labels.Where(item => !string.Equals(item, label, StringComparison.Ordinal)).ToList())
            .Trim(' ', '\t', ':', '：', '-', '—');
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string TrimAtNextKnownLabel(string value, IReadOnlyCollection<string> labels)
    {
        var stopIndex = value.Length;
        foreach (var label in labels)
        {
            var index = value.IndexOf(label, StringComparison.Ordinal);
            if (index > 0 && index < stopIndex)
            {
                stopIndex = index;
            }
        }

        return value[..stopIndex].Trim();
    }

    private static void NormalizeImportedUser(BankUser user, Bank bank)
    {
        user.BankId = bank.Id;
        user.BankName = bank.Name;
        if (string.IsNullOrWhiteSpace(user.UserCode))
        {
            user.UserCode = $"{bank.Name}-{DateTime.Now:HHmmss}";
        }

        if (string.IsNullOrWhiteSpace(user.Currency))
        {
            user.Currency = "RMB";
        }

        if (user.OpeningBalance == 0 && user.Balance != 0)
        {
            user.OpeningBalance = user.Balance;
        }

        if (string.IsNullOrWhiteSpace(user.AccountNo) && !string.IsNullOrWhiteSpace(user.CardNo))
        {
            user.AccountNo = user.CardNo.Trim();
        }
    }

    private static bool HasUsefulUserData(BankUser user)
    {
        return !string.IsNullOrWhiteSpace(user.AccountName)
            || !string.IsNullOrWhiteSpace(user.AccountNo)
            || !string.IsNullOrWhiteSpace(user.CardNo)
            || !string.IsNullOrWhiteSpace(user.IdNumber);
    }

    private static void MergeImportedUserInfo(BankUser target, BankUser source)
    {
        target.UserCode = UseImported(source.UserCode, target.UserCode);
        target.AccountName = UseImported(source.AccountName, target.AccountName);
        target.AccountNo = UseImported(source.AccountNo, target.AccountNo);
        target.CardNo = UseImported(source.CardNo, target.CardNo);
        target.IdNumber = UseImported(source.IdNumber, target.IdNumber);
        target.PhoneNumber = UseImported(source.PhoneNumber, target.PhoneNumber);
        target.OpenBranch = UseImported(source.OpenBranch, target.OpenBranch);
        target.Balance = source.Balance != 0 ? source.Balance : target.Balance;
        target.TransactionType = UseImported(source.TransactionType, target.TransactionType);
        target.Currency = UseImported(source.Currency, target.Currency);
        target.ChapterCode = UseImported(source.ChapterCode, target.ChapterCode);
        target.ChapterBranch = UseImported(source.ChapterBranch, target.ChapterBranch);
        target.ShouldPrintSeal = source.ShouldPrintSeal || target.ShouldPrintSeal;
        target.OpeningBalance = source.OpeningBalance != 0 ? source.OpeningBalance : target.OpeningBalance;
        target.AutoCalculateInterest = source.AutoCalculateInterest || target.AutoCalculateInterest;
        target.Remark = UseImported(source.Remark, target.Remark);

        foreach (var item in source.ExtraFields)
        {
            if (!string.IsNullOrWhiteSpace(item.Value))
            {
                target[item.Key] = item.Value;
            }
        }
    }

    private static string UseImported(string imported, string current)
    {
        return string.IsNullOrWhiteSpace(imported) ? current : imported.Trim();
    }

    private static void AddIssueIfMissing(ICollection<PdfImportIssue> issues, PdfImportIssue issue)
    {
        if (issues.Any(item => item.Severity == issue.Severity
            && item.PageNumber == issue.PageNumber
            && item.LineNumber == issue.LineNumber
            && string.Equals(item.Message, issue.Message, StringComparison.Ordinal)
            && string.Equals(item.RawText, issue.RawText, StringComparison.Ordinal)))
        {
            return;
        }

        issues.Add(issue);
    }

    private static bool HasUsefulFlowData(FlowRecord record)
    {
        return record.AccountTime.HasValue
            || record.TradeMoney.HasValue
            || record.CreditAmount.HasValue
            || record.DebitAmount.HasValue
            || record.Balance.HasValue
            || !string.IsNullOrWhiteSpace(record.ProductBrief)
            || !string.IsNullOrWhiteSpace(record.SerialNum)
            || !string.IsNullOrWhiteSpace(record.OppositeAccount)
            || !string.IsNullOrWhiteSpace(record.OppositeUsername);
    }

    private static void MergeDateAndTimeFromExtraCells(FlowRecord record, IEnumerable<string> values)
    {
        if (record.AccountTime.HasValue)
        {
            return;
        }

        var valueList = values.Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        for (var index = 0; index < valueList.Count - 1; index++)
        {
            var candidate = $"{valueList[index]} {valueList[index + 1]}";
            if (PdfImportTabularMapper.TryParseDateTime(candidate, out var dateTime))
            {
                record.AccountTime = dateTime;
                return;
            }
        }
    }

    private static bool IsFooterLine(string line)
    {
        var normalized = PdfImportTabularMapper.NormalizeHeader(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        return normalized.Contains("合计", StringComparison.Ordinal)
            || normalized.Contains("总计", StringComparison.Ordinal)
            || normalized.Contains("本页小计", StringComparison.Ordinal)
            || normalized.Contains("制表", StringComparison.Ordinal)
            || normalized.Contains("打印时间", StringComparison.Ordinal)
            || normalized.Contains("第页", StringComparison.Ordinal);
    }

    private static bool IsRepeatedHeader(
        IReadOnlyDictionary<int, string> row,
        IReadOnlyDictionary<int, ColumnDefinition> headerMap)
    {
        var matched = 0;
        foreach (var (columnIndex, column) in headerMap)
        {
            if (row.TryGetValue(columnIndex, out var value)
                && PdfImportTabularMapper.NormalizeHeader(value) == PdfImportTabularMapper.NormalizeHeader(column.Name ?? string.Empty))
            {
                matched++;
            }
        }

        return matched >= Math.Min(3, headerMap.Count);
    }

    private static bool IsBeforeRow(PdfTextLine line, PdfTableRow row)
    {
        return line.PageNumber < row.PageNumber
            || (line.PageNumber == row.PageNumber && line.LineNumber < row.LineNumber);
    }

    private static void ReindexFlowRecords(IList<FlowRecord> records)
    {
        for (var index = 0; index < records.Count; index++)
        {
            records[index].Index = index + 1;
        }
    }

    private static string FirstTextPreview(IEnumerable<PdfTextLine> lines)
    {
        return lines.FirstOrDefault()?.Text ?? string.Empty;
    }

    private static string BuildRawTextPreview(IEnumerable<PdfTextLine> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line.Text);
            if (builder.Length >= MaxRawTextPreviewLength)
            {
                break;
            }
        }

        var text = builder.ToString();
        return text.Length <= MaxRawTextPreviewLength ? text : text[..MaxRawTextPreviewLength];
    }

    private sealed class PdfExtractedDocument
    {
        public int PageCount { get; set; }

        public List<PdfTextLine> Lines { get; } = [];

        public List<PdfTextWord> Words { get; } = [];

        public List<PdfImportIssue> Issues { get; } = [];
    }

    private sealed record BankPdfTemplateDefinition(
        string BankName,
        string DisplayName,
        IReadOnlyList<string> RequiredKeywords);

    private sealed record HuaxiaColumnSpec(string Key, double Left, double Right);

    private sealed record HuaxiaPositionedRow(int PageNumber, double Top, Dictionary<string, string> Cells);

    private sealed record PdfPositionedColumnSpec(string Key, double Left, double Right);

    private sealed record PdfPositionedRow(int PageNumber, double Top, Dictionary<string, string> Cells);

    private sealed record PdfTextLine(int PageNumber, int LineNumber, string Text);

    private sealed record PdfTextWord(int PageNumber, string Text, double Left, double Right, double Top, double Bottom);

    private sealed record PdfTableRow(int PageNumber, int LineNumber, string RawText, Dictionary<int, string> Cells);
}
