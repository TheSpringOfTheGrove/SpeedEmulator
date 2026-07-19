namespace SpeedEmulator.Models;

public enum PdfImportTarget
{
    BankUsers,
    FlowRecords,
    BankUserAndFlowRecords
}

public enum PdfImportIssueSeverity
{
    Info,
    Warning,
    Error
}

public sealed class PdfImportIssue
{
    public PdfImportIssueSeverity Severity { get; init; }

    public string Message { get; init; } = string.Empty;

    public int? PageNumber { get; init; }

    public int? LineNumber { get; init; }

    public string RawText { get; init; } = string.Empty;

    public bool IsError => Severity == PdfImportIssueSeverity.Error;
}

public sealed class PdfImportResult
{
    public string SourcePath { get; init; } = string.Empty;

    public string BankName { get; init; } = string.Empty;

    public PdfImportTarget Target { get; init; }

    public BankUser? User { get; set; }

    public List<BankUser> Users { get; init; } = [];

    public List<FlowRecord> FlowRecords { get; init; } = [];

    public List<PdfImportIssue> Issues { get; init; } = [];

    public string RawTextPreview { get; init; } = string.Empty;

    public int PageCount { get; init; }

    public bool HasBlockingErrors => Issues.Any(item => item.IsError);

    public int UserImportCount => Users.Count;

    public int ImportedCount => Target == PdfImportTarget.BankUsers ? Users.Count : FlowRecords.Count;

    public string TargetName => Target switch
    {
        PdfImportTarget.BankUsers => "用户信息",
        PdfImportTarget.BankUserAndFlowRecords => "用户信息和流水明细",
        _ => "流水明细"
    };

    public string Summary
    {
        get
        {
            var issueCount = Issues.Count;
            var errorCount = Issues.Count(item => item.Severity == PdfImportIssueSeverity.Error);
            var warningCount = Issues.Count(item => item.Severity == PdfImportIssueSeverity.Warning);
            if (Target == PdfImportTarget.BankUserAndFlowRecords)
            {
                return $"PDF 共 {PageCount} 页，识别到 {UserImportCount} 条用户信息、{FlowRecords.Count} 条流水明细，问题 {issueCount} 个（错误 {errorCount}，警告 {warningCount}）。";
            }

            return $"PDF 共 {PageCount} 页，识别到 {ImportedCount} 条{TargetName}，问题 {issueCount} 个（错误 {errorCount}，警告 {warningCount}）。";
        }
    }
}
