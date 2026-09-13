using System.Collections.ObjectModel;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;

namespace SpeedEmulator.ViewModels;

public sealed class MergeUserFlowsDialogViewModel : ObservableObject
{
    private MergeUserFlowTargetOption? selectedTarget;
    private bool clearSourceUsersAndFlows;

    public MergeUserFlowsDialogViewModel(IEnumerable<(BankUser User, int RecordCount)> selectedUsers)
    {
        var users = selectedUsers
            .Where(item => item.User is not null)
            .GroupBy(item => item.User.Id)
            .Select(group => group.First())
            .ToList();

        foreach (var item in users)
        {
            SelectedUsers.Add(new MergeUserFlowTargetOption(item.User, item.RecordCount));
        }

        selectedTarget = SelectedUsers.FirstOrDefault();
        ConfirmCommand = new RelayCommand(Confirm, () => SelectedTarget is not null);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, new DialogCloseRequestedEventArgs(false)));
    }

    public event EventHandler<DialogCloseRequestedEventArgs>? RequestClose;

    public ObservableCollection<MergeUserFlowTargetOption> SelectedUsers { get; } = [];

    public MergeUserFlowTargetOption? SelectedTarget
    {
        get => selectedTarget;
        set
        {
            if (SetProperty(ref selectedTarget, value))
            {
                ConfirmCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(TargetUser));
                OnPropertyChanged(nameof(MergeSummary));
            }
        }
    }

    public BankUser? TargetUser => SelectedTarget?.User;

    public bool ClearSourceUsersAndFlows
    {
        get => clearSourceUsersAndFlows;
        set
        {
            if (SetProperty(ref clearSourceUsersAndFlows, value))
            {
                OnPropertyChanged(nameof(SourceRetentionText));
            }
        }
    }

    public string MergeSummary
    {
        get
        {
            if (SelectedTarget is null)
            {
                return "请选择合并目标用户。";
            }

            var totalRecords = SelectedUsers.Sum(item => item.RecordCount);
            var sourceRecords = totalRecords - SelectedTarget.RecordCount;
            return $"合并前：目标用户 {SelectedTarget.RecordCount} 条，其他选中用户 {sourceRecords} 条；合并后：目标用户 {totalRecords} 条。";
        }
    }

    public string SourceRetentionText => ClearSourceUsersAndFlows
        ? "已开启：合并完成后将清空非目标用户的流水，并删除这些用户。"
        : "未开启：合并后非目标用户及其原有流水都会保留。";

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    private void Confirm()
    {
        if (SelectedTarget is null)
        {
            return;
        }

        RequestClose?.Invoke(this, new DialogCloseRequestedEventArgs(true));
    }
}

public sealed class MergeUserFlowTargetOption
{
    public MergeUserFlowTargetOption(BankUser user, int recordCount)
    {
        User = user;
        RecordCount = recordCount;
        var account = string.IsNullOrWhiteSpace(user.AccountNo) ? user.CardNo : user.AccountNo;
        var userName = string.IsNullOrWhiteSpace(account)
            ? user.AccountName
            : $"{user.AccountName}（{account}）";
        DisplayName = $"{userName} - {recordCount} 条流水";
    }

    public BankUser User { get; }

    public int RecordCount { get; }

    public string DisplayName { get; }
}
