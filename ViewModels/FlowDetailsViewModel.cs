using System.Collections.ObjectModel;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
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
    private readonly IPdfImportService pdfImportService;
    private readonly IPdfImportPreviewDialogService pdfImportPreviewDialogService;
    private readonly List<FlowRecord> allRecords = [];
    private List<FlowFilterCondition> activeFilterConditions = [];
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
        IBankUserRepository? bankUserRepository = null,
        IPdfImportService? pdfImportService = null,
        IPdfImportPreviewDialogService? pdfImportPreviewDialogService = null)
    {
        Bank = bank;
        BankUser = bankUser;
        this.repository = repository;
        this.tableExcelService = tableExcelService;
        this.bankUserRepository = bankUserRepository;
        this.pdfImportService = pdfImportService ?? new PdfImportService();
        this.pdfImportPreviewDialogService = pdfImportPreviewDialogService ?? new PdfImportPreviewDialogService();
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
        PrintRecordCommand = new RelayCommand(OpenPrintPreview);
        ImportRecordCommand = new RelayCommand(() => MarkReserved("导入xlsx"));
        ExportRecordCommand = new RelayCommand(() => MarkReserved("导出xlsx"));
        OpenFilterCommand = new RelayCommand(() => RequestOpenFilter?.Invoke(this, EventArgs.Empty));
        SetColumnFieldCommand = new RelayCommand(() => RequestOpenColumnSettings?.Invoke(this, EventArgs.Empty));
        ConvertFormulaCommand = new RelayCommand(() => MarkReserved("转换公式"));
        ShowStaticCommand = new RelayCommand(() => RequestOpenStatistics?.Invoke(this, EventArgs.Empty));
        ReComputeBalanceCommand = new AsyncRelayCommand(RecomputeBalanceAsync);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));
        ImportRecordCommand = new AsyncRelayCommand(ImportRecordsFromXlsxAsync);
        ExportRecordCommand = new RelayCommand(ExportRecordsToXlsx);
        ImportPdfRecordCommand = new AsyncRelayCommand(ImportRecordsFromPdfAsync);
    }

    public event EventHandler? RequestClose;

    public event EventHandler? RequestOpenColumnSettings;

    public event Action<FlowRecord>? RequestScrollToRecord;

    public event EventHandler? RequestOpenStatistics;

    public event EventHandler? RequestOpenFilter;

    public event EventHandler? RequestOpenPrintPreview;

    public Bank Bank { get; }

    public BankUser BankUser { get; }

    public string WindowTitle => $"流水页-版本({AppVersion.DisplayVersion})-{Bank.Name}";

    public BulkObservableCollection<FlowRecord> Records { get; } = [];

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
    public ICommand ImportPdfRecordCommand { get; }
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
            allRecords.Clear();
            activeFilterConditions = [];
            var records = await repository.ListByUserAsync(Bank.Id, BankUser.Id);
            foreach (var item in records)
            {
                allRecords.Add(item);
            }

            RefreshDisplay();
            SelectedRecord = Records.FirstOrDefault();
            RefreshTotals();
            StatusMessage = $"已载入 {Records.Count} 条 {Bank.Name} 流水明细";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public IReadOnlyList<FlowRecord> GetPrintRecords()
    {
        return Records.Select(item => item.Clone()).ToList();
    }

    public void NotifyColumnSettingsSaved()
    {
        StatusMessage = "字段设置已保存";
    }

    private void AddRecord()
    {
        var record = CreateDraftRecord();
        allRecords.Add(record);
        RefreshDisplay(record);
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

    private void DeleteRecord(object? parameter)
    {
        var selectedRecords = GetSelectedRecords(parameter);
        if (selectedRecords.Count == 0 && SelectedRecord is not null)
        {
            selectedRecords.Add(SelectedRecord);
        }

        if (selectedRecords.Count == 0)
        {
            StatusMessage = "请先选择要删除的流水。";
            return;
        }

        var index = selectedRecords
            .Select(record => Records.IndexOf(record))
            .Where(item => item >= 0)
            .DefaultIfEmpty(0)
            .Min();

        foreach (var record in selectedRecords)
        {
            allRecords.Remove(record);
        }

        RefreshDisplay();
        SelectedRecord = Records.Count == 0 ? null : Records[Math.Clamp(index, 0, Records.Count - 1)];
        RefreshTotals();
        StatusMessage = selectedRecords.Count == 1
            ? "已删除选中流水"
            : $"已删除选中流水 {selectedRecords.Count} 条";
    }

    private static List<FlowRecord> GetSelectedRecords(object? parameter)
    {
        if (parameter is not IList selectedItems)
        {
            return [];
        }

        return selectedItems
            .OfType<FlowRecord>()
            .Distinct()
            .ToList();
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

        MoveInMaster(SelectedRecord, -1);
        RefreshDisplay(SelectedRecord);
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

        MoveInMaster(SelectedRecord, 1);
        RefreshDisplay(SelectedRecord);
        StatusMessage = "已下移";
    }

    private void SortByDate()
    {
        var selected = SelectedRecord;
        var sorted = allRecords
            .OrderBy(item => item.AccountTime ?? DateTime.MaxValue)
            .ToList();

        allRecords.Clear();
        allRecords.AddRange(sorted);
        RefreshDisplay(selected);
        SelectedRecord = selected is not null && Records.Contains(selected)
            ? selected
            : Records.FirstOrDefault();
        StatusMessage = "时间排序成功";
        MessageBox.Show("时间排序成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public async Task SaveAllAsync()
    {
        try
        {
            var selected = SelectedRecord;
            ReindexAllRecords();
            await repository.SaveAllAsync(Bank.Id, BankUser.Id, allRecords);
            RefreshDisplay(selected);
            StatusMessage = $"已保存全部流水：{allRecords.Count} 条";
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
            BankUser.AutoCalculateInterest = AutoCalculateInterest;
            var currentOpeningBalance = RoundMoney(OpeningBalance);
            OpeningBalance = currentOpeningBalance;
            BankUser.OpeningBalance = (decimal)currentOpeningBalance;

            RecalculateRecordBalances(currentOpeningBalance);
            var negativeBalance = FindFirstNegativeBalance();
            NegativeBalanceRepairResult? repairResult = null;
            if (negativeBalance is not null)
            {
                var choice = MessageBox.Show(
                    $"检测到第 {negativeBalance.Value.Index + 1} 行余额存在负值 " +
                    $"{negativeBalance.Value.Balance:N2}，是否自动处理？\n\n" +
                    "确定：保持期初余额不变，自动调整其它流出流水。\n" +
                    "取消：只重新计算，保留负余额。",
                    "提示",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information);

                if (choice == MessageBoxResult.OK)
                {
                    repairResult = RepairNegativeBalancesByAdjustingOutflows(currentOpeningBalance);
                    RecalculateRecordBalances(currentOpeningBalance);
                }
            }

            var selected = repairResult is not null
                ? repairResult.Value.LastAdjustedRecord ?? SelectedRecord
                : negativeBalance?.Record ?? SelectedRecord;

            ReindexAllRecords();
            await repository.SaveAllAsync(Bank.Id, BankUser.Id, allRecords);
            if (bankUserRepository is not null)
            {
                await bankUserRepository.SaveAsync(BankUser);
            }

            RefreshDisplay(selected);
            if (repairResult is not null)
            {
                if (repairResult.Value.LastAdjustedRecord is not null)
                {
                    RequestScrollToRecord?.Invoke(repairResult.Value.LastAdjustedRecord);
                }

                StatusMessage =
                    $"重新计算成功，期初余额保持 {currentOpeningBalance:N2}，" +
                    $"已调整 {repairResult.Value.AdjustedCount} 条流出流水";
                MessageBox.Show(
                    $"负余额已自动处理。\n\n" +
                    $"期初余额保持为 {currentOpeningBalance:N2}，未作修改。\n" +
                    $"共调整 {repairResult.Value.AdjustedCount} 条流出流水，" +
                    $"流出合计减少 {repairResult.Value.ReducedAmount:N2}。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (negativeBalance is not null)
            {
                if (negativeBalance.Value.Record is not null)
                {
                    RequestScrollToRecord?.Invoke(negativeBalance.Value.Record);
                }

                StatusMessage =
                    $"重新计算完成，第 {negativeBalance.Value.Index + 1} 行负余额已保留";
            }
            else
            {
                StatusMessage = "重新计算成功";
                MessageBox.Show("重新计算成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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

    private NegativeBalanceRepairResult RepairNegativeBalancesByAdjustingOutflows(double openingBalance)
    {
        var runningBalance = RoundMoney(openingBalance);
        var adjustedRecords = new HashSet<FlowRecord>();
        var recordsToRemove = new HashSet<FlowRecord>();
        var reducedAmount = 0d;
        FlowRecord? lastAdjustedRecord = null;

        for (var index = 0; index < allRecords.Count; index++)
        {
            var record = allRecords[index];
            var amount = RoundMoney(record.TradeMoney ?? 0);
            runningBalance = RoundMoney(runningBalance + amount);
            if (runningBalance >= -0.009d)
            {
                continue;
            }

            var needed = RoundMoney(Math.Abs(runningBalance) + 0.01d);
            var candidates = allRecords
                .Take(index + 1)
                .Where(item => !recordsToRemove.Contains(item) && (item.TradeMoney ?? 0) < -0.009d)
                .OrderBy(IsInterestRecord)
                .ThenByDescending(item => Math.Abs(item.TradeMoney ?? 0))
                .ThenByDescending(item => item.Index)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (needed <= 0.009d)
                {
                    break;
                }

                var expense = RoundMoney(Math.Abs(candidate.TradeMoney ?? 0));
                if (expense <= 0.009d)
                {
                    continue;
                }

                var reducibleWhileKeepingRecord = RoundMoney(Math.Max(0, expense - 0.01d));
                if (needed <= reducibleWhileKeepingRecord + 0.009d)
                {
                    var reduction = Math.Min(needed, reducibleWhileKeepingRecord);
                    SetSignedAmount(candidate, -RoundMoney(expense - reduction));
                    runningBalance = RoundMoney(runningBalance + reduction);
                    reducedAmount = RoundMoney(reducedAmount + reduction);
                    adjustedRecords.Add(candidate);
                    lastAdjustedRecord = candidate;
                    needed = RoundMoney(needed - reduction);
                    continue;
                }

                SetSignedAmount(candidate, 0);
                recordsToRemove.Add(candidate);
                runningBalance = RoundMoney(runningBalance + expense);
                reducedAmount = RoundMoney(reducedAmount + expense);
                adjustedRecords.Add(candidate);
                lastAdjustedRecord = candidate;
                needed = RoundMoney(Math.Max(0, needed - expense));
            }
        }

        if (recordsToRemove.Count > 0)
        {
            allRecords.RemoveAll(recordsToRemove.Contains);
            if (lastAdjustedRecord is not null && recordsToRemove.Contains(lastAdjustedRecord))
            {
                lastAdjustedRecord = allRecords.LastOrDefault();
            }
        }

        return new NegativeBalanceRepairResult(adjustedRecords.Count, reducedAmount, lastAdjustedRecord);
    }

    private void RecalculateRecordBalances(double openingBalance)
    {
        var balance = RoundMoney(openingBalance);
        foreach (var record in allRecords)
        {
            if (record.TradeMoney.HasValue)
            {
                var amount = RoundMoney(record.TradeMoney.Value);
                SetSignedAmount(record, amount);
                balance = RoundMoney(balance + amount);
            }

            record.Balance = balance;
            record.BalanceAmount = balance;
        }
    }

    private NegativeBalanceInfo? FindFirstNegativeBalance()
    {
        for (var index = 0; index < allRecords.Count; index++)
        {
            var record = allRecords[index];
            if ((record.Balance ?? 0) < -0.009d)
            {
                return new NegativeBalanceInfo(index, record.Balance ?? 0, record);
            }
        }

        return null;
    }

    private static void SetSignedAmount(FlowRecord record, double amount)
    {
        var rounded = RoundMoney(amount);
        record.TradeMoney = rounded;
        record.IncomeAttribute = rounded >= 0 ? "收入" : "支出";
        record.CreditAmount = rounded > 0 ? rounded : null;
        record.DebitAmount = rounded < 0 ? Math.Abs(rounded) : null;
        record.IncomeFlag = rounded >= 0 ? "收入" : "支出";
    }

    private static bool IsInterestRecord(FlowRecord record)
    {
        return new[]
        {
            record.ProductBrief,
            record.Remark,
            record.TradeExplain,
            record.Usage
        }.Any(value => value?.Contains("利息", StringComparison.Ordinal) == true
                       || value?.Contains("结息", StringComparison.Ordinal) == true);
    }

    private readonly record struct NegativeBalanceInfo(int Index, double Balance, FlowRecord Record);

    private readonly record struct NegativeBalanceRepairResult(
        int AdjustedCount,
        double ReducedAmount,
        FlowRecord? LastAdjustedRecord);

    private void InsertAfterSelected(FlowRecord record)
    {
        var index = SelectedRecord is null ? allRecords.Count - 1 : allRecords.IndexOf(SelectedRecord);
        allRecords.Insert(Math.Clamp(index + 1, 0, allRecords.Count), record);
        RefreshDisplay(record);
    }

    private FlowRecord CreateDraftRecord()
    {
        var last = allRecords.LastOrDefault();
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

    public IReadOnlyList<string> GetFilterFieldNames()
    {
        return GetFilterFields()
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void ApplyFilters(IEnumerable<FlowFilterCondition> conditions)
    {
        activeFilterConditions = conditions
            .Where(item => item.IsValid())
            .Select(item => item.Clone())
            .ToList();

        RefreshDisplay(SelectedRecord);
        StatusMessage = activeFilterConditions.Count == 0
            ? "已显示全部流水"
            : $"已筛选流水：{Records.Count} 条";
    }

    public void ClearFilters()
    {
        if (activeFilterConditions.Count == 0)
        {
            return;
        }

        activeFilterConditions = [];
        RefreshDisplay(SelectedRecord);
        StatusMessage = "已关闭筛选";
    }

    public int ReplaceCurrentFilterValues(string fieldName, string source, string target)
    {
        var count = 0;
        foreach (var record in Records)
        {
            if (TryReplaceFieldValue(record, fieldName, source, target))
            {
                count++;
            }
        }

        StatusMessage = $"已替换 {count} 条流水";
        return count;
    }

    private IReadOnlyList<FlowFilterField> GetFilterFields()
    {
        var fields = new List<FlowFilterField>
        {
            new("ID", nameof(FlowRecord.Index))
        };

        foreach (var column in Bank.FlowColumns)
        {
            if (string.IsNullOrWhiteSpace(column.Name) || string.IsNullOrWhiteSpace(column.Field))
            {
                continue;
            }

            if (fields.Any(item => string.Equals(item.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            fields.Add(new FlowFilterField(column.Name, column.Field));
        }

        return fields;
    }

    private void RefreshDisplay(FlowRecord? preferredRecord = null)
    {
        var previous = preferredRecord ?? SelectedRecord;
        var source = activeFilterConditions.Count == 0
            ? allRecords
            : allRecords.Where(MatchesActiveFilters).ToList();

        Records.ReplaceAll(source);

        Reindex();
        SelectedRecord = previous is not null && Records.Contains(previous)
            ? previous
            : null;
    }

    private bool MatchesActiveFilters(FlowRecord record)
    {
        return activeFilterConditions.All(condition => MatchesCondition(record, condition));
    }

    private bool MatchesCondition(FlowRecord record, FlowFilterCondition condition)
    {
        var value = GetFieldValue(record, condition.FieldName);
        var compareText = condition.Value.Trim();
        var actualText = FormatFilterValue(value);

        return condition.OperatorName switch
        {
            "等于" => CompareValue(value, compareText) == 0,
            "不等于" => CompareValue(value, compareText) != 0,
            "模糊匹配" => actualText.Contains(compareText, StringComparison.OrdinalIgnoreCase),
            "以开头" => actualText.StartsWith(compareText, StringComparison.OrdinalIgnoreCase),
            "以结尾" => actualText.EndsWith(compareText, StringComparison.OrdinalIgnoreCase),
            "属于" => SplitConditionValues(compareText).Any(item => string.Equals(actualText, item, StringComparison.OrdinalIgnoreCase)),
            "大于" => CompareValue(value, compareText) > 0,
            "绝对值大于" => TryParseDouble(value, out var actualAbsGreater) && TryParseDouble(compareText, out var compareAbsGreater) && Math.Abs(actualAbsGreater) > Math.Abs(compareAbsGreater),
            "小于" => CompareValue(value, compareText) < 0,
            "绝对值小于" => TryParseDouble(value, out var actualAbsLess) && TryParseDouble(compareText, out var compareAbsLess) && Math.Abs(actualAbsLess) < Math.Abs(compareAbsLess),
            _ => false
        };
    }

    private object? GetFieldValue(FlowRecord record, string fieldName)
    {
        var field = ResolveFilterField(fieldName)?.Field ?? fieldName;
        if (string.Equals(field, nameof(FlowRecord.Index), StringComparison.OrdinalIgnoreCase))
        {
            return record.Index;
        }

        var extraField = NormalizeExtraFieldName(field);
        if (extraField is not null)
        {
            return record[extraField];
        }

        var property = typeof(FlowRecord).GetProperty(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return property is null ? record[field] : property.GetValue(record);
    }

    private bool TryReplaceFieldValue(FlowRecord record, string fieldName, string source, string target)
    {
        var field = ResolveFilterField(fieldName)?.Field ?? fieldName;
        var extraField = NormalizeExtraFieldName(field);
        if (extraField is not null)
        {
            var oldExtraValue = record[extraField];
            if (!oldExtraValue.Contains(source, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            record[extraField] = ReplaceIgnoreCase(oldExtraValue, source, target);
            return true;
        }

        var property = typeof(FlowRecord).GetProperty(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        if (property is null || property.PropertyType != typeof(string) || !property.CanRead || !property.CanWrite)
        {
            return false;
        }

        if (property.GetValue(record) is not string oldValue || !oldValue.Contains(source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        property.SetValue(record, ReplaceIgnoreCase(oldValue, source, target));
        return true;
    }

    private FlowFilterField? ResolveFilterField(string fieldName)
    {
        return GetFilterFields().FirstOrDefault(item => string.Equals(item.Name, fieldName, StringComparison.OrdinalIgnoreCase));
    }

    private void MoveInMaster(FlowRecord record, int offset)
    {
        var index = allRecords.IndexOf(record);
        if (index < 0)
        {
            return;
        }

        var nextIndex = index + offset;
        if (nextIndex < 0 || nextIndex >= allRecords.Count)
        {
            return;
        }

        allRecords.RemoveAt(index);
        allRecords.Insert(nextIndex, record);
    }

    private void ReindexAllRecords()
    {
        for (var i = 0; i < allRecords.Count; i++)
        {
            allRecords[i].Index = i + 1;
        }
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
                allRecords.Clear();
            }

            FlowRecord? lastImported = null;
            foreach (var record in imported)
            {
                record.Id = 0;
                record.BankId = Bank.Id;
                record.BankUserId = BankUser.Id;
                allRecords.Add(record);
                lastImported = record;
            }

            ReindexAllRecords();
            await repository.SaveAllAsync(Bank.Id, BankUser.Id, allRecords);
            if (bankUserRepository is not null)
            {
                await bankUserRepository.SaveAsync(BankUser);
            }

            RefreshDisplay(lastImported);
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

    private async Task ImportRecordsFromPdfAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!EnsurePdfImportSupported())
        {
            return;
        }

        var path = pdfImportService.PickImportFile();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "正在解析 PDF 用户信息和流水，请稍候...";
            var result = await pdfImportService.ImportBankUserAndFlowRecordsAsync(path, Bank, BankUser);
            if (!pdfImportPreviewDialogService.Confirm(result))
            {
                StatusMessage = result.HasBlockingErrors ? "PDF 导入存在错误，已取消保存。" : "已取消 PDF 导入。";
                return;
            }

            var imported = result.FlowRecords;
            if (imported.Count == 0)
            {
                MessageBox.Show("没有读取到可导入的流水数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var overwriteResult = MessageBox.Show(
                $"已读取 {imported.Count} 条流水数据。\n\n是否覆盖当前流水？\n是：覆盖；否：追加；取消：放弃导入。",
                "导入PDF",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (overwriteResult == MessageBoxResult.Cancel)
            {
                StatusMessage = "已取消 PDF 导入。";
                return;
            }

            if (overwriteResult == MessageBoxResult.Yes)
            {
                allRecords.Clear();
            }

            if (result.User is not null)
            {
                ApplyImportedUserInfo(BankUser, result.User);
                OpeningBalance = (double)BankUser.OpeningBalance;
                AutoCalculateInterest = BankUser.AutoCalculateInterest;
            }

            FlowRecord? lastImported = null;
            foreach (var record in imported)
            {
                record.Id = 0;
                record.BankId = Bank.Id;
                record.BankUserId = BankUser.Id;
                allRecords.Add(record);
                lastImported = record;
            }

            ReindexAllRecords();
            await repository.SaveAllAsync(Bank.Id, BankUser.Id, allRecords);
            if (bankUserRepository is not null)
            {
                await bankUserRepository.SaveAsync(BankUser);
            }

            RefreshDisplay(lastImported);
            StatusMessage = $"PDF导入成功：{imported.Count} 条流水";
            MessageBox.Show($"PDF导入成功：{imported.Count} 条", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = ex.Message;
            MessageBox.Show(ex.Message, "导入PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"PDF导入失败：{ex.Message}";
            MessageBox.Show($"PDF导入失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool EnsurePdfImportSupported()
    {
        if (pdfImportService.IsBankSupported(Bank))
        {
            return true;
        }

        var message = pdfImportService.GetUnsupportedBankMessage(Bank);
        StatusMessage = message;
        MessageBox.Show(message, "导入PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private static void ApplyImportedUserInfo(BankUser target, BankUser source)
    {
        target.UserCode = UseImported(source.UserCode, target.UserCode);
        target.AccountName = UseImported(source.AccountName, target.AccountName);
        target.AccountNo = UseImported(source.AccountNo, target.AccountNo);
        target.CardNo = UseImported(source.CardNo, target.CardNo);
        target.IdNumber = UseImported(source.IdNumber, target.IdNumber);
        target.PhoneNumber = UseImported(source.PhoneNumber, target.PhoneNumber);
        target.OpenBranch = UseImported(source.OpenBranch, target.OpenBranch);
        target.Balance = source.Balance != 0 ? source.Balance : target.Balance;
        target.StartDate = source.StartDate;
        target.EndDate = source.EndDate;
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

    private static int CompareValue(object? actualValue, string compareText)
    {
        if (TryParseDouble(actualValue, out var actualNumber) && TryParseDouble(compareText, out var compareNumber))
        {
            return actualNumber.CompareTo(compareNumber);
        }

        if (TryParseDateTime(actualValue, out var actualDate) && TryParseDateTime(compareText, out var compareDate))
        {
            return actualDate.CompareTo(compareDate);
        }

        return string.Compare(
            FormatFilterValue(actualValue),
            compareText,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatFilterValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            double number => number.ToString("0.##", CultureInfo.InvariantCulture),
            decimal number => number.ToString("0.##", CultureInfo.InvariantCulture),
            float number => number.ToString("0.##", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static bool TryParseDouble(object? value, out double number)
    {
        if (value is double doubleValue)
        {
            number = doubleValue;
            return true;
        }

        if (value is decimal decimalValue)
        {
            number = (double)decimalValue;
            return true;
        }

        if (value is int intValue)
        {
            number = intValue;
            return true;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim().Replace(",", string.Empty);
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out number)
            || double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out number);
    }

    private static bool TryParseDateTime(object? value, out DateTime dateTime)
    {
        if (value is DateTime typedDateTime)
        {
            dateTime = typedDateTime;
            return true;
        }

        var text = Convert.ToString(value, CultureInfo.CurrentCulture);
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out dateTime)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime);
    }

    private static IEnumerable<string> SplitConditionValues(string value)
    {
        return value
            .Split([',', ';', '，', '；', ':', '：', '|', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? NormalizeExtraFieldName(string field)
    {
        if (field.StartsWith('[') && field.EndsWith(']') && field.Length > 2)
        {
            return field[1..^1];
        }

        return null;
    }

    private static string ReplaceIgnoreCase(string text, string source, string target)
    {
        var index = text.IndexOf(source, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text;
        }

        var result = text;
        while (index >= 0)
        {
            result = result.Remove(index, source.Length).Insert(index, target);
            index = result.IndexOf(source, index + target.Length, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private void MarkReserved(string featureName)
    {
        StatusMessage = $"{featureName}入口已预留：{BankUser.AccountName}";
    }

    private void OpenPrintPreview()
    {
        StatusMessage = $"正在打开 {BankUser.AccountName} 的打印页面";
        RequestOpenPrintPreview?.Invoke(this, EventArgs.Empty);
    }

    private static double RoundMoney(double value)
    {
        return Math.Round(value, 2);
    }
}
