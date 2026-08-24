using System.Collections.ObjectModel;
using System.ComponentModel;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

namespace SpeedEmulator.ViewModels;

public sealed class PdfImportPreviewViewModel : ObservableObject
{
    private string statusMessage;

    public PdfImportPreviewViewModel(PdfImportResult result)
    {
        Result = result;
        statusMessage = string.Empty;

        IEnumerable<BankUser> previewUsers = result.Users.Count > 0
            ? result.Users
            : result.User is null ? Enumerable.Empty<BankUser>() : [result.User];
        foreach (var user in previewUsers)
        {
            Users.Add(user);
        }

        foreach (var record in result.FlowRecords)
        {
            FlowRecords.Add(record);
            record.PropertyChanged += FlowRecord_PropertyChanged;
        }

        foreach (var issue in result.Issues)
        {
            Issues.Add(issue);
        }

        ConfirmCommand = new RelayCommand(Confirm, () => CanImport);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, new DialogCloseRequestedEventArgs(false)));
        UpdateDirectionConfirmationState();
    }

    public event EventHandler<DialogCloseRequestedEventArgs>? RequestClose;

    public PdfImportResult Result { get; }

    public string WindowTitle => $"{Result.BankName} PDF导入预览";

    public string SourceFile => Result.SourcePath;

    public string Summary => Result.Summary;

    public string RawTextPreview => Result.RawTextPreview;

    public bool HasUsers => Users.Count > 0;

    public bool HasFlowRecords => FlowRecords.Count > 0;

    public bool HasIssues => Issues.Count > 0;

    public bool CanImport => Result.ImportedCount > 0
        && !Result.HasBlockingErrors
        && PendingIncomeDirectionCount == 0;

    public IReadOnlyList<string> IncomeDirectionOptions { get; } = ["收入", "支出"];

    public int PendingIncomeDirectionCount => FlowRecords.Count(record =>
        record.ExtraFields.ContainsKey(WechatPdfDirectionRuleCatalog.UnresolvedDirectionField));

    public ObservableCollection<BankUser> Users { get; } = [];

    public ObservableCollection<FlowRecord> FlowRecords { get; } = [];

    public ObservableCollection<PdfImportIssue> Issues { get; } = [];

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    private void Confirm()
    {
        if (!CanImport)
        {
            StatusMessage = "当前结果不能导入，请先处理错误。";
            return;
        }

        RequestClose?.Invoke(this, new DialogCloseRequestedEventArgs(true));
    }

    private void FlowRecord_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FlowRecord record
            || e.PropertyName != nameof(FlowRecord.IncomeFlag)
            || !record.ExtraFields.ContainsKey(WechatPdfDirectionRuleCatalog.UnresolvedDirectionField)
            || record.IncomeFlag is not ("收入" or "支出"))
        {
            return;
        }

        if (!PdfImportTabularMapper.TryParseDouble(record["金额"], out var rawAmount))
        {
            StatusMessage = $"预览第 {record.Index} 行的原始金额无法解析，暂时不能确认收支方向。";
            return;
        }

        var amount = Math.Abs(rawAmount);
        var isIncome = record.IncomeFlag == "收入";
        record.TradeMoney = isIncome ? amount : 0 - amount;
        record.CreditAmount = isIncome ? amount : null;
        record.DebitAmount = isIncome ? null : amount;

        var fields = new Dictionary<string, string>(record.ExtraFields);
        fields.Remove(WechatPdfDirectionRuleCatalog.UnresolvedDirectionField);
        record.ExtraFields = fields;

        var resolvedIssues = Result.Issues
            .Where(issue => issue.LineNumber == record.Index
                && issue.Message.Contains("无法确认收支方向", StringComparison.Ordinal))
            .ToList();
        foreach (var issue in resolvedIssues)
        {
            Result.Issues.Remove(issue);
            Issues.Remove(issue);
        }

        UpdateDirectionConfirmationState();
    }

    private void UpdateDirectionConfirmationState()
    {
        var pendingCount = PendingIncomeDirectionCount;
        StatusMessage = Result.HasBlockingErrors
            ? "存在阻断错误，请检查问题列表。"
            : pendingCount > 0
                ? $"还有 {pendingCount} 条流水无法自动确认收支方向，请在“收支”列手动选择。"
                : "请核对预览数据，确认无误后导入。";
        OnPropertyChanged(nameof(PendingIncomeDirectionCount));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(Summary));
        ConfirmCommand.RaiseCanExecuteChanged();
    }
}
