using System.Collections.ObjectModel;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;

namespace SpeedEmulator.ViewModels;

public sealed class PdfImportPreviewViewModel : ObservableObject
{
    private string statusMessage;

    public PdfImportPreviewViewModel(PdfImportResult result)
    {
        Result = result;
        statusMessage = result.HasBlockingErrors
            ? "存在阻断错误，请检查问题列表。"
            : "请核对预览数据，确认无误后导入。";

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
        }

        foreach (var issue in result.Issues)
        {
            Issues.Add(issue);
        }

        ConfirmCommand = new RelayCommand(Confirm, () => CanImport);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, new DialogCloseRequestedEventArgs(false)));
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

    public bool CanImport => Result.ImportedCount > 0 && !Result.HasBlockingErrors;

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
}
