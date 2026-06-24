using System.Collections.ObjectModel;
using System.Windows;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;

namespace SpeedEmulator.ViewModels;

public sealed class FlowDetailsViewModel : ObservableObject
{
    private readonly IFlowRecordRepository repository;
    private FlowRecord? selectedRecord;
    private double openingBalance;
    private bool autoCalculateInterest;
    private bool isBusy;
    private string statusMessage = "正在加载流水明细";

    public FlowDetailsViewModel(Bank bank, BankUser bankUser, IFlowRecordRepository repository)
    {
        Bank = bank;
        BankUser = bankUser;
        this.repository = repository;
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
        ShowStaticCommand = new RelayCommand(() => MarkReserved("查看统计"));
        ReComputeBalanceCommand = new RelayCommand(RecomputeBalance);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? RequestClose;

    public event EventHandler? RequestOpenColumnSettings;

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
    public RelayCommand ImportRecordCommand { get; }
    public RelayCommand ExportRecordCommand { get; }
    public RelayCommand OpenFilterCommand { get; }
    public RelayCommand SetColumnFieldCommand { get; }
    public RelayCommand ConvertFormulaCommand { get; }
    public RelayCommand ShowStaticCommand { get; }
    public RelayCommand ReComputeBalanceCommand { get; }
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
            .ThenBy(item => item.Id)
            .ToList();

        Records.Clear();
        foreach (var item in sorted)
        {
            Records.Add(item);
        }

        Reindex();
        SelectedRecord = selected;
        StatusMessage = "已按记账时间排序";
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

    private void RecomputeBalance()
    {
        var balance = OpeningBalance;
        foreach (var record in Records.OrderBy(item => item.AccountTime ?? DateTime.MaxValue))
        {
            if (record.TradeMoney.HasValue)
            {
                balance += record.TradeMoney.Value;
                record.Balance = Math.Round(balance, 2);
                record.BalanceAmount = record.Balance;
                record.IncomeAttribute = record.TradeMoney.Value >= 0 ? "收入" : "支出";
            }
        }

        RefreshTotals();
        StatusMessage = $"余额已重算，期末余额 {balance:N2}";
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

    private void MarkReserved(string featureName)
    {
        StatusMessage = $"{featureName}入口已预留：{BankUser.AccountName}";
    }
}
