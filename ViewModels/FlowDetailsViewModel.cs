using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;

namespace SpeedEmulator.ViewModels;

public sealed class FlowDetailsViewModel : ObservableObject
{
    private readonly IFlowRecordRepository repository;
    private readonly ITableExcelService tableExcelService;
    private readonly IBankUserRepository? bankUserRepository;
    private FlowRecord? selectedRecord;
    private double openingBalance;
    private bool autoCalculateInterest;
    private bool isBusy;
    private string statusMessage = "正在加载流水明细";

    public FlowDetailsViewModel(
        Bank bank,
        BankUser bankUser,
        IFlowRecordRepository repository,
        ITableExcelService tableExcelService,
        IBankUserRepository? bankUserRepository = null)
    {
        Bank = bank;
        BankUser = bankUser;
        this.repository = repository;
        this.tableExcelService = tableExcelService;
        this.bankUserRepository = bankUserRepository;
        openingBalance = (double)bankUser.OpeningBalance;
        autoCalculateInterest = bankUser.AutoCalculateInterest;

        AddRecordCommand = new RelayCommand(AddRecord);
        CopyRecordCommand = new RelayCommand(CopyRecord);
        InsertBlankRecordCommand = new RelayCommand(InsertBlankRecord);
        DeleteRecordCommand = new RelayCommand(DeleteRecord);
        MoveUpRecordCommand = new RelayCommand(MoveUpRecord);
        MoveDownRecordCommand = new RelayCommand(MoveDownRecord);
        ReSortByDateCommand = new RelayCommand(SortByDate);
        SaveAllRecordCommand = new AsyncRelayCommand(SaveAllAsync);
        PrintRecordCommand = new RelayCommand(() => MarkReserved("打印"));
        ImportRecordCommand = new RelayCommand(() => MarkReserved("导入xlsx"));
        ExportRecordCommand = new RelayCommand(() => MarkReserved("导出xlsx"));
        OpenFilterCommand = new RelayCommand(() => MarkReserved("开启筛选"));
        SetColumnFieldCommand = new RelayCommand(() => RequestOpenColumnSettings?.Invoke(this, EventArgs.Empty));
        ConvertFormulaCommand = new RelayCommand(() => MarkReserved("转换公式"));
        ShowStaticCommand = new RelayCommand(() => RequestOpenStatistics?.Invoke(this, EventArgs.Empty));
        ReComputeBalanceCommand = new AsyncRelayCommand(RecomputeBalanceAsync);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));
        ImportRecordCommand = new AsyncRelayCommand(ImportRecordsFromXlsxAsync);
        ExportRecordCommand = new RelayCommand(ExportRecordsToXlsx);
    }

    public event EventHandler? RequestClose;

    public event EventHandler? RequestOpenColumnSettings;

    public event Action<FlowRecord>? RequestScrollToRecord;

    public event EventHandler? RequestOpenStatistics;

    public Bank Bank { get; }

    public BankUser BankUser { get; }

    public string WindowTitle => $"流水页-版本({AppVersion.DisplayVersion})-{Bank.Name}";

    public ObservableCollection<FlowRecord> Records { get; } = [];

    public FlowRecord? SelectedRecord
    {
        get => selectedRecord;
        set => SetProperty(ref selectedRecord, value);
    }

    public double OpeningBalance
    {
        get => openingBalance;
        set => SetProperty(ref openingBalance, value);
    }

    public bool AutoCalculateInterest
    {
        get => autoCalculateInterest;
        set => SetProperty(ref autoCalculateInterest, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public int TotalCount => Records.Count;

    public double AllIncome => Records.Where(item => item.TradeMoney > 0).Sum(item => item.TradeMoney ?? 0);

    public double AllOut => Records.Where(item => item.TradeMoney < 0).Sum(item => item.TradeMoney ?? 0);

    public RelayCommand AddRecordCommand { get; }
    public RelayCommand CopyRecordCommand { get; }
    public RelayCommand InsertBlankRecordCommand { get; }
    public RelayCommand DeleteRecordCommand { get; }
    public RelayCommand MoveUpRecordCommand { get; }
    public RelayCommand MoveDownRecordCommand { get; }
    public RelayCommand ReSortByDateCommand { get; }
    public AsyncRelayCommand SaveAllRecordCommand { get; }
    public RelayCommand PrintRecordCommand { get; }
    public ICommand ImportRecordCommand { get; }
    public ICommand ExportRecordCommand { get; }
    public RelayCommand OpenFilterCommand { get; }
    public RelayCommand SetColumnFieldCommand { get; }
    public RelayCommand ConvertFormulaCommand { get; }
    public RelayCommand ShowStaticCommand { get; }
    public AsyncRelayCommand ReComputeBalanceCommand { get; }
    public RelayCommand CloseCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Records.Clear();
            var records = await repository.ListByUserAsync(Bank.Id, BankUser.Id);
            foreach (var item in records)
            {
                Records.Add(item);
            }

            Reindex();
            SelectedRecord = Records.FirstOrDefault();
            RefreshTotals();
            StatusMessage = $"已载入 {Records.Count} 条 {Bank.Name} 流水明细";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void NotifyColumnSettingsSaved()
    {
        StatusMessage = "字段设置已保存";
    }

    private void AddRecord()
    {
        var record = CreateDraftRecord();
        Records.Add(record);
        Reindex();
        SelectedRecord = record;
        RefreshTotals();
        StatusMessage = "已新增一条流水";
    }

    private void CopyRecord()
    {
        if (SelectedRecord is null)
        {
            StatusMessage = "请先选择要复制的流水。";
            return;
        }

        var copy = SelectedRecord.Clone();
        copy.Id = 0;
        copy.MoveFlag = false;
        InsertAfterSelected(copy);
        StatusMessage = "已复制选中流水";
    }

    private void InsertBlankRecord()
    {
        var record = CreateDraftRecord();
        record.ProductBrief = string.Empty;
        record.TradeMoney = null;
        record.Balance = null;
        InsertAfterSelected(record);
        StatusMessage = "已插入空行";
    }

    private void DeleteRecord()
    {
        if (SelectedRecord is null)
        {
            StatusMessage = "请先选择要删除的流水。";
            return;
        }

        var index = Records.IndexOf(SelectedRecord);
        Records.Remove(SelectedRecord);
        Reindex();
        SelectedRecord = Records.Count == 0 ? null : Records[Math.Clamp(index, 0, Records.Count - 1)];
        RefreshTotals();
        StatusMessage = "已删除选中流水";
    }

    private void MoveUpRecord()
    {
        if (SelectedRecord is null)
        {
            StatusMessage = "请先选择流水。";
            return;
        }

        var index = Records.IndexOf(SelectedRecord);
        if (index <= 0)
        {
            return;
        }

        Records.Move(index, index - 1);
        Reindex();
        StatusMessage = "已上移";
    }

    private void MoveDownRecord()
    {
        if (SelectedRecord is null)
        {
            StatusMessage = "请先选择流水。";
            return;
        }

        var index = Records.IndexOf(SelectedRecord);
        if (index < 0 || index >= Records.Count - 1)
        {
            return;
        }

        Records.Move(index, index + 1);
        Reindex();
        StatusMessage = "已下移";
    }

    private void SortByDate()
    {
        var selected = SelectedRecord;
        var sorted = Records
            .OrderBy(item => item.AccountTime ?? DateTime.MaxValue)
            .ToList();

        Records.Clear();
        foreach (var item in sorted)
        {
            Records.Add(item);
        }

        Reindex();
        SelectedRecord = selected is not null && Records.Contains(selected)
            ? selected
            : Records.FirstOrDefault();
        StatusMessage = "时间排序成功";
        MessageBox.Show("时间排序成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SaveAllAsync()
    {
        try
        {
            await repository.SaveAllAsync(Bank.Id, BankUser.Id, Records);
            StatusMessage = $"已保存全部流水：{Records.Count} 条";
            MessageBox.Show("保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            MessageBox.Show($"保存失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RecomputeBalanceAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            BankUser.OpeningBalance = (decimal)OpeningBalance;
            BankUser.AutoCalculateInterest = AutoCalculateInterest;

            var previewBalance = RoundMoney(OpeningBalance);
            for (var i = 0; i < Records.Count; i++)
            {
                var record = Records[i];
                if (!record.TradeMoney.HasValue)
                {
                    continue;
                }

                previewBalance = RoundMoney(previewBalance + RoundMoney(record.TradeMoney.Value));
                if (previewBalance < 0)
                {
                    SelectedRecord = record;
                    RequestScrollToRecord?.Invoke(record);
                    StatusMessage = $"第 {i + 1} 行余额为负，请调整金额或期初余额";
                    MessageBox.Show(
                        $"第 {i + 1} 行重新计算后余额为负：{previewBalance:N2}，请调整金额或期初余额。",
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            var balance = RoundMoney(OpeningBalance);
            foreach (var record in Records)
            {
                if (!record.TradeMoney.HasValue)
                {
                    continue;
                }

                var amount = RoundMoney(record.TradeMoney.Value);
                record.TradeMoney = amount;
                balance = RoundMoney(balance + amount);
                record.Balance = balance;
                record.BalanceAmount = balance;
                record.IncomeAttribute = amount >= 0 ? "收入" : "支出";
                record.CreditAmount = amount > 0 ? amount : null;
                record.DebitAmount = amount < 0 ? Math.Abs(amount) : null;
                record.IncomeFlag = amount >= 0 ? "收入" : "支出";
            }

            RefreshTotals();
            await repository.SaveAllAsync(Bank.Id, BankUser.Id, Records);
            StatusMessage = "重新计算成功";
            MessageBox.Show("重新计算成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"重新计算失败：{ex.Message}";
            MessageBox.Show($"重新计算失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InsertAfterSelected(FlowRecord record)
    {
        var index = SelectedRecord is null ? Records.Count - 1 : Records.IndexOf(SelectedRecord);
        Records.Insert(Math.Clamp(index + 1, 0, Records.Count), record);
        Reindex();
        SelectedRecord = record;
        RefreshTotals();
    }

    private FlowRecord CreateDraftRecord()
    {
        var last = Records.LastOrDefault();
        return new FlowRecord
        {
            BankId = Bank.Id,
            BankUserId = BankUser.Id,
            AccountTime = (last?.AccountTime ?? DateTime.Now).AddMinutes(1),
            TradeMoney = 0,
            Balance = last?.Balance ?? OpeningBalance,
            BalanceAmount = last?.Balance ?? OpeningBalance,
            IncomeAttribute = "收入",
            ProductBrief = "新增流水",
            CashCheck = "转账",
            TradeChannel = Bank.Name == "支付宝" ? "电子商务" : "柜面",
            Currency = "RMB",
            TradeCurrency = "RMB"
        };
    }

    private void Reindex()
    {
        for (var i = 0; i < Records.Count; i++)
        {
            Records[i].Index = i + 1;
        }

        RefreshTotals();
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(AllIncome));
        OnPropertyChanged(nameof(AllOut));
    }

    private async Task ImportRecordsFromXlsxAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var path = tableExcelService.PickImportFile();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var imported = tableExcelService.ImportFlowRecords(path, Bank, BankUser);
            if (imported.Count == 0)
            {
                MessageBox.Show("没有读取到可导入的流水数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var overwriteResult = MessageBox.Show(
                $"已读取 {imported.Count} 条流水数据。\n\n是否覆盖当前流水？\n是：覆盖；否：追加；取消：放弃导入。",
                "导入xlsx",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (overwriteResult == MessageBoxResult.Cancel)
            {
                return;
            }

            if (overwriteResult == MessageBoxResult.Yes)
            {
                Records.Clear();
            }

            foreach (var record in imported)
            {
                record.Id = 0;
                record.BankId = Bank.Id;
                record.BankUserId = BankUser.Id;
                Records.Add(record);
            }

            Reindex();
            SelectedRecord = Records.LastOrDefault();
            await repository.SaveAllAsync(Bank.Id, BankUser.Id, Records);
            if (bankUserRepository is not null)
            {
                await bankUserRepository.SaveAsync(BankUser);
            }

            StatusMessage = $"导入成功：{imported.Count} 条流水";
            MessageBox.Show($"导入成功：{imported.Count} 条", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
            MessageBox.Show($"导入失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ExportRecordsToXlsx()
    {
        try
        {
            var accountName = string.IsNullOrWhiteSpace(BankUser.AccountName) ? BankUser.AccountNo : BankUser.AccountName;
            var path = tableExcelService.PickExportFile($"{Bank.Name}-{accountName}.xlsx");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            tableExcelService.ExportFlowRecords(path, Records, Bank, BankUser);
            StatusMessage = $"导出成功：{path}";
            MessageBox.Show("导出成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.Message}";
            MessageBox.Show($"导出失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MarkReserved(string featureName)
    {
        StatusMessage = $"{featureName}入口已预留：{BankUser.AccountName}";
    }

    private static double RoundMoney(double value)
    {
        return Math.Round(value, 2);
    }
}
