namespace SpeedEmulator.Models;

public sealed class PrintTemplate
{
    public long Id { get; set; }

    public long BankId { get; set; }

    public long VendorId { get; set; }

    public long VendorBankId { get; set; }

    public bool IsSystem { get; set; }

    public bool IsDeleted { get; set; }

    public string Name { get; set; } = string.Empty;

    public string PageSize { get; set; } = "A4Landscape";

    public int PageRows { get; set; }

    public string Remark { get; set; } = string.Empty;

    public string PdfData { get; set; } = string.Empty;

    public string QuestPdfLayoutData { get; set; } = string.Empty;

    public PrintPdfConfig Config { get; set; } = new();

    public string SystemText => IsSystem ? "是" : "否";

    public PrintTemplate Clone()
    {
        return new PrintTemplate
        {
            Id = Id,
            BankId = BankId,
            VendorId = VendorId,
            VendorBankId = VendorBankId,
            IsSystem = IsSystem,
            IsDeleted = IsDeleted,
            Name = Name,
            PageSize = PageSize,
            PageRows = PageRows,
            Remark = Remark,
            PdfData = PdfData,
            QuestPdfLayoutData = QuestPdfLayoutData,
            Config = Config.Clone()
        };
    }
}
