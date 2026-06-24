using System.Collections.ObjectModel;
using System.Windows;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;

namespace SpeedEmulator.ViewModels;

public sealed class BankUsersViewModel : ObservableObject
{
    private readonly IBankUserRepository repository;
    private readonly IBankUserColumnSettingsRepository columnSettingsRepository;
    private readonly IFrontApiClient frontApiClient;
    private readonly IImageFilePickerService imageFilePickerService;
    private BankUser editableUser;
    private BankUser? selectedUser;
    private bool isNewRecord = true;
    private bool isBusy;
    private string editorMode = "新增";
    private string statusMessage;
    private long draftId = -1;

    public BankUsersViewModel(
        Bank bank,
        IBankUserRepository repository,
        IBankUserColumnSettingsRepository columnSettingsRepository,
        IFrontApiClient frontApiClient,
        IImageFilePickerService imageFilePickerService)
    {
        Bank = bank;
        this.repository = repository;
        this.columnSettingsRepository = columnSettingsRepository;
        this.frontApiClient = frontApiClient;
        this.imageFilePickerService = imageFilePickerService;
        editableUser = BankUser.CreateDraft(bank);
        statusMessage = $"正在维护 {bank.Name} 用户资料";

        NewCommand = new RelayCommand(StartNew);
        EditCommand = new RelayCommand(OpenFlowDetails);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        CopyCommand = new RelayCommand(CopySelected);
        PrintCommand = new RelayCommand(() => MarkReserved("打印"));
        ImportXlsxCommand = new RelayCommand(() => MarkReserved("导入 xlsx"));
        ExportXlsxCommand = new RelayCommand(() => MarkReserved("导出 xlsx"));
        AutoGenerateFlowCommand = new RelayCommand(OpenAutoGenerateFlow);
        SetColumnsCommand = new RelayCommand(() => RequestOpenColumnSettings?.Invoke(this, EventArgs.Empty));
        SetInterestCommand = new RelayCommand(() => RequestOpenInterestSettings?.Invoke(this, EventArgs.Empty));
        MergeFlowCommand = new RelayCommand(() => MarkReserved("合并流水"));
        SetSealImageCommand = new RelayCommand(SelectSealImage);
        CopySealPathCommand = new RelayCommand(CopySealPath);
        ClearSealCommand = new RelayCommand(ClearSealPath);
        BackCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? RequestClose;

    public event EventHandler? RequestOpenAutoGenerateFlow;

    public event EventHandler? RequestOpenFlowDetails;

    public event EventHandler? RequestOpenColumnSettings;

    public event EventHandler? RequestOpenInterestSettings;

    public Bank Bank { get; }

    public string WindowTitle => $"流水主页界面-版本({AppVersion.DisplayVersion})-{Bank.Name}{Bank.Type}";

    public string AccountFieldLabel => Bank.Name == "支付宝" ? "支付宝账户" : "银行账户";

    public BankUser? FlowGenerationTargetUser => SelectedUser;

    public BankUser? FlowDetailsTargetUser => SelectedUser;

    public ObservableCollection<BankUser> Users { get; } = [];

    public BankUser EditableUser
    {
        get => editableUser;
        private set => SetProperty(ref editableUser, value);
    }

    public BankUser? SelectedUser
    {
        get => selectedUser;
        set
        {
            if (SetProperty(ref selectedUser, value) && value is not null)
            {
                LoadEditor(value, value.Id <= 0);
            }
        }
    }

    public bool IsNewRecord
    {
        get => isNewRecord;
        private set => SetProperty(ref isNewRecord, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public string EditorMode
    {
        get => editorMode;
        private set => SetProperty(ref editorMode, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public RelayCommand NewCommand { get; }

    public RelayCommand EditCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public RelayCommand CopyCommand { get; }

    public RelayCommand PrintCommand { get; }

    public RelayCommand ImportXlsxCommand { get; }

    public RelayCommand ExportXlsxCommand { get; }

    public RelayCommand AutoGenerateFlowCommand { get; }

    public RelayCommand SetColumnsCommand { get; }

    public RelayCommand SetInterestCommand { get; }

    public RelayCommand MergeFlowCommand { get; }

    public RelayCommand SetSealImageCommand { get; }

    public RelayCommand CopySealPathCommand { get; }

    public RelayCommand ClearSealCommand { get; }

    public RelayCommand BackCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            Users.Clear();
            var users = await repository.ListByBankAsync(Bank.Id);
            foreach (var user in users)
            {
                Users.Add(user);
            }

            if (Users.Count > 0)
            {
                SelectedUser = Users[0];
                StatusMessage = $"已载入 {Users.Count} 个 {Bank.Name} 用户";
            }
            else
            {
                StartNew();
                StatusMessage = $"{Bank.Name} 暂无用户，已进入新增模式";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ApplyColumnSettingsAsync()
    {
        var settings = await columnSettingsRepository.LoadAsync(Bank.Id);
        ApplyColumnSettings(settings);
    }

    public void NotifyColumnSettingsSaved()
    {
        StatusMessage = "字段设置已保存";
    }

    public void NotifyInterestSettingsSaved()
    {
        StatusMessage = "利息设置已保存";
    }

    private void ApplyColumnSettings(IReadOnlyList<BankUserColumnSetting> settings)
    {
        var settingsByField = settings
            .Where(item => !string.IsNullOrWhiteSpace(item.Field))
            .GroupBy(item => item.Field, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var column in Bank.Columns)
        {
            if (IsIdColumn(column))
            {
                column.Show = true;
                column.Order = -1;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(column.Field) && settingsByField.TryGetValue(column.Field, out var setting))
            {
                column.Width = setting.Width <= 0 ? 100 : setting.Width;
                column.Order = setting.Order;
                column.Show = setting.Show;
                continue;
            }

            column.Width = 100;
            column.Order = 0;
            column.Show = true;
        }
    }

    private static bool IsIdColumn(ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase);
    }

    private void StartNew()
    {
        var draft = BankUser.CreateDraft(Bank);
        draft.Id = draftId--;
        draft.UserCode = string.Empty;
        Users.Add(draft);
        SelectedUser = draft;
        StatusMessage = $"正在新增 {Bank.Name} 用户";
    }

    private void EditSelected()
    {
        if (SelectedUser is null)
        {
            StatusMessage = "请先在右侧列表中选择一个用户。";
            return;
        }

        LoadEditor(SelectedUser, false);
        StatusMessage = $"正在编辑 {SelectedUser.AccountName}";
    }

    private void OpenFlowDetails()
    {
        if (SelectedUser is null)
        {
            StatusMessage = "请先在右侧列表中选择一个用户。";
            return;
        }

        LoadEditor(SelectedUser, false);
        StatusMessage = $"正在打开 {SelectedUser.AccountName} 的流水明细。";
        RequestOpenFlowDetails?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var target = EditableUser;
        if (string.IsNullOrWhiteSpace(target.AccountName) || string.IsNullOrWhiteSpace(target.AccountNo))
        {
            StatusMessage = "请至少填写户名和账号/卡号。";
            return;
        }

        IsBusy = true;
        try
        {
            var originalId = target.Id;
            target.BankId = Bank.Id;
            target.BankName = Bank.Name;

            var localSaved = await repository.SaveAsync(target);
            ReplaceUserInList(target, originalId, localSaved);
            StatusMessage = "保存成功";
            _ = SyncBackendUserAsync(localSaved);
            MessageBox.Show("保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            MessageBox.Show($"保存失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (SelectedUser is null)
        {
            StatusMessage = "请选择一个用户。";
            MessageBox.Show("请选择一个用户", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var deletedName = SelectedUser.AccountName;
            var removedIndex = Users.IndexOf(SelectedUser);
            await repository.DeleteAsync(SelectedUser.Id);
            Users.Remove(SelectedUser);

            if (Users.Count > 0)
            {
                SelectedUser = Users[Math.Min(Math.Max(removedIndex, 0), Users.Count - 1)];
            }
            else
            {
                SelectedUser = null;
                LoadEditor(BankUser.CreateDraft(Bank), true);
            }

            StatusMessage = $"已删除 {deletedName}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopySelected()
    {
        if (SelectedUser is null)
        {
            StatusMessage = "请先选择要复制的用户。";
            return;
        }

        var copy = SelectedUser.Clone();
        copy.Id = draftId--;
        copy.BackendId = 0;
        copy.UserCode = $"{SelectedUser.UserCode}-COPY";
        copy.AccountName = $"{SelectedUser.AccountName}-副本";
        Users.Add(copy);
        SelectedUser = copy;
        StatusMessage = $"已复制 {SelectedUser.AccountName} 到列表底部，保存后同步后台。";
    }

    private void OpenAutoGenerateFlow()
    {
        if (SelectedUser is null)
        {
            StatusMessage = "请选择用户";
            MessageBox.Show("请选择用户", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusMessage = $"正在打开 {Bank.Name} 自动生成流水页面：{SelectedUser.AccountName}";
        RequestOpenAutoGenerateFlow?.Invoke(this, EventArgs.Empty);
    }

    private void LoadEditor(BankUser source, bool isNew)
    {
        EditableUser = source;
        IsNewRecord = isNew;
        EditorMode = isNew ? "新增" : "编辑";
    }

    private void ReplaceUserInList(BankUser previous, long previousId, BankUser next)
    {
        var index = Users.IndexOf(previous);
        if (index < 0)
        {
            index = Users.ToList().FindIndex(user => user.Id == previousId);
        }

        if (index < 0)
        {
            index = Users.ToList().FindIndex(user => user.Id == next.Id);
        }

        if (index >= 0)
        {
            Users[index] = next;
        }
        else
        {
            Users.Add(next);
        }

        SelectedUser = next;
        LoadEditor(next, false);
    }

    private async Task SyncBackendUserAsync(BankUser localSaved)
    {
        try
        {
            var backendSaved = await frontApiClient.SaveBankUserAsync(Bank, localSaved);
            var current = Users.FirstOrDefault(user => ReferenceEquals(user, localSaved) || user.Id == localSaved.Id);
            if (current is not null && backendSaved.BackendId > 0)
            {
                current.BackendId = backendSaved.BackendId;
                var snapshot = localSaved.Clone();
                snapshot.BackendId = backendSaved.BackendId;
                await repository.SaveAsync(snapshot);
            }
        }
        catch
        {
            // Backend sync is best-effort. Local data is already saved, so there is nothing to show here.
        }
    }

    private void MarkReserved(string featureName)
    {
        var target = SelectedUser?.AccountName ?? EditableUser.AccountName;
        var suffix = string.IsNullOrWhiteSpace(target) ? string.Empty : $"：{target}";
        StatusMessage = $"{featureName}入口已预留{suffix}";
    }

    private void CopySealPath()
    {
        if (string.IsNullOrWhiteSpace(EditableUser.SealImagePath))
        {
            StatusMessage = "当前没有印章图片路径。";
            return;
        }

        Clipboard.SetText(EditableUser.SealImagePath);
        StatusMessage = "印章图片路径已复制。";
    }

    private void SelectSealImage()
    {
        var imagePath = imageFilePickerService.PickSealImagePath();
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        EditableUser.SealImagePath = imagePath;
        StatusMessage = "已选择印章图片。";
    }

    private void ClearSealPath()
    {
        EditableUser.SealImagePath = string.Empty;
        StatusMessage = "已清除印章图片路径。";
    }
}
