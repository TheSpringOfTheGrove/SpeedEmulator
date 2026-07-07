using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;

namespace SpeedEmulator.ViewModels;

public sealed class FlowGenerationViewModel : ObservableObject
{
    private const double FinalBalanceTolerance = 1000.009d;

    private readonly IFlowGenerationRepository repository;
    private readonly IBankUserRepository bankUserRepository;
    private readonly IFlowRecordRepository flowRecordRepository;
    private readonly IBankInterestSettingsRepository interestSettingsRepository;
    private readonly IFlowRuleExcelService excelService;
    private readonly FlowAutoGenerator flowAutoGenerator = new();
    private FlowGenerationConfig config = new();
    private GenerateReferenceRule? selectedReference;
    private GenerateConstRule? selectedConst;
    private MonthGenerateRule? selectedMonthDetail;
    private int selectedTabIndex;
    private string excelStatus = "未读取";
    private string statusMessage = "准备生成流水";
    private bool isBusy;
    private int generationProgress;
    private bool isReorderingRules;

    public FlowGenerationViewModel(
        Bank bank,
        BankUser? bankUser,
        IFlowGenerationRepository repository,
        IBankUserRepository bankUserRepository,
        IFlowRecordRepository flowRecordRepository,
        IBankInterestSettingsRepository interestSettingsRepository,
        IFlowRuleExcelService excelService)
    {
        Bank = bank;
        BankUser = bankUser;
        this.repository = repository;
        this.bankUserRepository = bankUserRepository;
        this.flowRecordRepository = flowRecordRepository;
        this.interestSettingsRepository = interestSettingsRepository;
        this.excelService = excelService;

        NewCommand = new RelayCommand(AddCurrentRule);
        DeleteCommand = new RelayCommand(DeleteCurrentRule);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        SetInterestCommand = new RelayCommand(() => RequestOpenInterestSettings?.Invoke(this, EventArgs.Empty));
        SetColumnsCommand = new RelayCommand(() => RequestOpenColumnSettings?.Invoke(this, EventArgs.Empty));
        ImportExcelCommand = new AsyncRelayCommand(ImportExcelAsync);
        ExportExcelCommand = new AsyncRelayCommand(ExportExcelAsync);
        ReadExcelCommand = new RelayCommand(ReadExcel);
        ClearExcelCommand = new RelayCommand(ClearExcel);
        ComputeCommand = new RelayCommand(Compute);
        StartGenerateCommand = new AsyncRelayCommand(StartGenerateAsync);
        OpenMonthDetailSettingsCommand = new RelayCommand(() => RequestOpenMonthDetails?.Invoke(this, EventArgs.Empty));
        AddMonthDetailCommand = new RelayCommand(AddMonthDetail);
        DeleteMonthDetailCommand = new RelayCommand(DeleteMonthDetail);
        ClearMonthDetailsCommand = new RelayCommand(ClearMonthDetails);
        BackCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? RequestClose;

    public event EventHandler? RequestOpenMonthDetails;

    public event EventHandler? RequestOpenColumnSettings;

    public event EventHandler? RequestOpenInterestSettings;

    public event EventHandler? RequestOpenGeneratedFlowDetails;

    public Bank Bank { get; }

    public BankUser? BankUser { get; }

    public string WindowTitle => $"流水自动生成页面-版本({AppVersion.DisplayVersion})-{Bank.Name}";

    public string BankUserName => BankUser?.AccountName ?? "未选择用户";

    public FlowGenerationConfig Config
    {
        get => config;
        private set => SetProperty(ref config, value);
    }

    public ObservableCollection<GenerateReferenceRule> References { get; } = [];

    public ObservableCollection<GenerateConstRule> ConstItems { get; } = [];

    public GenerateReferenceRule? SelectedReference
    {
        get => selectedReference;
        set => SetProperty(ref selectedReference, value);
    }

    public GenerateConstRule? SelectedConst
    {
        get => selectedConst;
        set => SetProperty(ref selectedConst, value);
    }

    public MonthGenerateRule? SelectedMonthDetail
    {
        get => selectedMonthDetail;
        set => SetProperty(ref selectedMonthDetail, value);
    }

    public int SelectedTabIndex
    {
        get => selectedTabIndex;
        set => SetProperty(ref selectedTabIndex, value);
    }

    public string ExcelStatus
    {
        get => excelStatus;
        private set => SetProperty(ref excelStatus, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public int GenerationProgress
    {
        get => generationProgress;
        private set => SetProperty(ref generationProgress, value);
    }

    public RelayCommand NewCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand SetInterestCommand { get; }

    public RelayCommand SetColumnsCommand { get; }

    public AsyncRelayCommand ImportExcelCommand { get; }

    public AsyncRelayCommand ExportExcelCommand { get; }

    public RelayCommand ReadExcelCommand { get; }

    public RelayCommand ClearExcelCommand { get; }

    public RelayCommand ComputeCommand { get; }

    public AsyncRelayCommand StartGenerateCommand { get; }

    public RelayCommand OpenMonthDetailSettingsCommand { get; }

    public RelayCommand AddMonthDetailCommand { get; }

    public RelayCommand DeleteMonthDetailCommand { get; }

    public RelayCommand ClearMonthDetailsCommand { get; }

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
            var snapshot = await repository.LoadAsync(Bank, BankUser?.Id);
            Config = snapshot.Config;
            ApplyBankUserValuesToConfig();

            ClearReferenceRules();
            foreach (var item in snapshot.References)
            {
                AddReferenceRule(item);
            }

            ClearConstRules();
            foreach (var item in snapshot.ConstItems)
            {
                AddConstRule(item);
            }

            ApplyCheckedFirstOrder(References);
            ApplyCheckedFirstOrder(ConstItems);
            NormalizeRuleIndexes();

            SelectedReference = References.FirstOrDefault();
            SelectedConst = ConstItems.FirstOrDefault();
            SelectedMonthDetail = Config.MonthGenData.FirstOrDefault();
            StatusMessage = $"已载入参照明细 {References.Count} 条，固定日期增加项目 {ConstItems.Count} 条，月明细 {Config.MonthGenData.Count} 条";
        }
        catch (Exception ex)
        {
            ClearReferenceRules();
            ClearConstRules();
            SelectedReference = null;
            SelectedConst = null;
            SelectedMonthDetail = null;
            StatusMessage = ex.Message;
            MessageBox.Show(ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    public void NotifyInterestSettingsSaved()
    {
        StatusMessage = "利息设置已保存";
    }

    private void AddCurrentRule()
    {
        if (SelectedTabIndex == 1)
        {
            var item = GenerateConstRule.CreateDefault(Bank.Id);
            AddConstRule(item);
            SelectedConst = item;
            NormalizeRuleIndexes();
            StatusMessage = "已新增固定日期增加项目";
            return;
        }

        var reference = GenerateReferenceRule.CreateDefault(Bank.Id);
        AddReferenceRule(reference);
        SelectedReference = reference;
        NormalizeRuleIndexes();
        StatusMessage = "已新增参照明细";
    }

    private void DeleteCurrentRule()
    {
        if (SelectedTabIndex == 1)
        {
            if (SelectedConst is null)
            {
                StatusMessage = "请先选择固定日期增加项目";
                return;
            }

            RemoveConstRule(SelectedConst);
            SelectedConst = ConstItems.FirstOrDefault();
            NormalizeRuleIndexes();
            StatusMessage = "已删除固定日期增加项目";
            return;
        }

        if (SelectedReference is null)
        {
            StatusMessage = "请先选择参照明细";
            return;
        }

        RemoveReferenceRule(SelectedReference);
        SelectedReference = References.FirstOrDefault();
        NormalizeRuleIndexes();
        StatusMessage = "已删除参照明细";
    }

    private async Task SaveAsync()
    {
        try
        {
            await SaveCurrentSnapshotAsync();
            StatusMessage = "配置已保存，下次进入自动读取";
            MessageBox.Show("保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            MessageBox.Show($"保存失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ImportExcelAsync()
    {
        var path = excelService.PickImportFile();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var importMode = MessageBox.Show(
            "是否覆盖现有数据？\n\n是：覆盖当前列表\n否：追加到当前列表\n取消：放弃导入",
            "导入EXCEL",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (importMode == MessageBoxResult.Cancel)
        {
            return;
        }

        try
        {
            var replaceCurrentRows = importMode == MessageBoxResult.Yes;
            var importedCount = SelectedTabIndex == 1
                ? ImportConstItems(path, replaceCurrentRows)
                : ImportReferences(path, replaceCurrentRows);

            if (importedCount <= 0)
            {
                StatusMessage = "未读取到可导入的数据";
                MessageBox.Show("未读取到可导入的数据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await SaveCurrentSnapshotAsync();
            StatusMessage = $"导入成功，已保存 {importedCount} 条数据";
            MessageBox.Show($"导入成功，已保存 {importedCount} 条数据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
            MessageBox.Show($"导入失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Task ExportExcelAsync()
    {
        var isConstTab = SelectedTabIndex == 1;
        var sectionName = isConstTab ? "固定日期增加项目" : "参照明细";
        var path = excelService.PickExportFile($"{Bank.Name}-{sectionName}.xlsx");
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (isConstTab)
            {
                excelService.ExportConstItems(path, ConstItems, Bank.ConstColumns);
            }
            else
            {
                excelService.ExportReferences(path, References, Bank.ReferenceColumns);
            }

            StatusMessage = $"导出成功：{path}";
            MessageBox.Show($"导出成功：\n{path}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.Message}";
            MessageBox.Show($"导出失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        return Task.CompletedTask;
    }

    private int ImportReferences(string path, bool replaceCurrentRows)
    {
        var imported = excelService.ImportReferences(path, Bank.ReferenceColumns, Bank.Id);
        if (replaceCurrentRows)
        {
            ClearReferenceRules();
        }

        foreach (var item in imported)
        {
            AddReferenceRule(item);
        }

        ApplyCheckedFirstOrder(References);
        NormalizeRuleIndexes();
        SelectedReference = imported.FirstOrDefault() ?? References.FirstOrDefault();
        return imported.Count;
    }

    private int ImportConstItems(string path, bool replaceCurrentRows)
    {
        var imported = excelService.ImportConstItems(path, Bank.ConstColumns, Bank.Id);
        if (replaceCurrentRows)
        {
            ClearConstRules();
        }

        foreach (var item in imported)
        {
            AddConstRule(item);
        }

        ApplyCheckedFirstOrder(ConstItems);
        NormalizeRuleIndexes();
        SelectedConst = imported.FirstOrDefault() ?? ConstItems.FirstOrDefault();
        return imported.Count;
    }

    private async Task SaveCurrentSnapshotAsync()
    {
        NormalizeRuleIndexes();
        var snapshot = new FlowGenerationSnapshot
        {
            Config = Config.Clone(),
            References = References.Select(item => item.Clone()).ToList(),
            ConstItems = ConstItems.Select(item => item.Clone()).ToList()
        };

        await repository.SaveAsync(Bank.Id, BankUser?.Id, snapshot);
        await SaveBankUserValuesAsync();
    }

    private void AddReferenceRule(GenerateReferenceRule item)
    {
        item.PropertyChanged -= Rule_PropertyChanged;
        item.PropertyChanged += Rule_PropertyChanged;
        References.Add(item);
    }

    private void AddConstRule(GenerateConstRule item)
    {
        item.PropertyChanged -= Rule_PropertyChanged;
        item.PropertyChanged += Rule_PropertyChanged;
        ConstItems.Add(item);
    }

    private void RemoveReferenceRule(GenerateReferenceRule item)
    {
        item.PropertyChanged -= Rule_PropertyChanged;
        References.Remove(item);
    }

    private void RemoveConstRule(GenerateConstRule item)
    {
        item.PropertyChanged -= Rule_PropertyChanged;
        ConstItems.Remove(item);
    }

    private void ClearReferenceRules()
    {
        foreach (var item in References)
        {
            item.PropertyChanged -= Rule_PropertyChanged;
        }

        References.Clear();
    }

    private void ClearConstRules()
    {
        foreach (var item in ConstItems)
        {
            item.PropertyChanged -= Rule_PropertyChanged;
        }

        ConstItems.Clear();
    }

    private void Rule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (isReorderingRules || e.PropertyName != nameof(FlowRuleBase.IsCheck))
        {
            return;
        }

        switch (sender)
        {
            case GenerateReferenceRule reference:
                MoveRuleAfterCheckChange(References, reference);
                SelectedReference = reference;
                break;
            case GenerateConstRule constRule:
                MoveRuleAfterCheckChange(ConstItems, constRule);
                SelectedConst = constRule;
                break;
        }
    }

    private void MoveRuleAfterCheckChange<T>(ObservableCollection<T> rules, T changedRule)
        where T : FlowRuleBase
    {
        var oldIndex = rules.IndexOf(changedRule);
        if (oldIndex < 0)
        {
            return;
        }

        var newIndex = changedRule.IsCheck
            ? 0
            : rules.Where(item => !ReferenceEquals(item, changedRule)).Count(item => item.IsCheck);

        if (oldIndex == newIndex)
        {
            return;
        }

        try
        {
            isReorderingRules = true;
            rules.Move(oldIndex, newIndex);
        }
        finally
        {
            isReorderingRules = false;
        }

        NormalizeRuleIndexes();
    }

    private void ApplyCheckedFirstOrder<T>(ObservableCollection<T> rules)
        where T : FlowRuleBase
    {
        var orderedRules = rules
            .Select((rule, index) => new { Rule = rule, Index = index })
            .OrderByDescending(item => item.Rule.IsCheck)
            .ThenBy(item => item.Index)
            .Select(item => item.Rule)
            .ToList();

        try
        {
            isReorderingRules = true;
            for (var targetIndex = 0; targetIndex < orderedRules.Count; targetIndex++)
            {
                var currentIndex = rules.IndexOf(orderedRules[targetIndex]);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                {
                    rules.Move(currentIndex, targetIndex);
                }
            }
        }
        finally
        {
            isReorderingRules = false;
        }
    }

    private void NormalizeRuleIndexes()
    {
        for (var index = 0; index < References.Count; index++)
        {
            References[index].Index = index + 1;
        }

        for (var index = 0; index < ConstItems.Count; index++)
        {
            ConstItems[index].Index = index + 1;
        }
    }

    private void Compute()
    {
        var monthCount = CountCoveredMonths(Config.StartTime, Config.EndTime);
        var totalOutMoney = Math.Max(0, Config.AllInMoney - Config.LastMoney);
        var averageInMoney = Config.AllInMoney / monthCount;
        var averageOutMoney = totalOutMoney / monthCount;

        Config.AllOutMoney = RoundMoney(totalOutMoney);
        Config.MinInMoneyMonth1 = RoundMoney(averageInMoney * 0.45);
        Config.MaxInMoneyMonth1 = RoundMoney(averageInMoney * 1.8);
        Config.MinOutMoneyMonth1 = RoundMoney(averageOutMoney * 0.45);
        Config.MaxOutMoneyMonth1 = RoundMoney(averageOutMoney * 1.8);
        Config.MinInMoneyMonth2 = Config.MinInMoneyMonth1;
        Config.MaxInMoneyMonth2 = Config.MaxInMoneyMonth1;
        Config.MinOutMoneyMonth2 = Config.MinOutMoneyMonth1;
        Config.MaxOutMoneyMonth2 = Config.MaxOutMoneyMonth1;

        StatusMessage = $"计算完成：月份数 {monthCount}，总支出 {Config.AllOutMoney:N2}";
    }

    private void ApplyBankUserValuesToConfig()
    {
        if (BankUser is null)
        {
            return;
        }

        Config.StartTime = BankUser.StartDate;
        Config.EndTime = BankUser.EndDate;
        Config.OpeningBalance = Convert.ToDouble(BankUser.OpeningBalance);
    }

    private async Task SaveBankUserValuesAsync()
    {
        if (BankUser is null)
        {
            return;
        }

        BankUser.StartDate = Config.StartTime;
        BankUser.EndDate = Config.EndTime;
        BankUser.OpeningBalance = Convert.ToDecimal(Config.OpeningBalance);
        BankUser.BankId = Bank.Id;
        BankUser.BankName = Bank.Name;
        await bankUserRepository.SaveAsync(BankUser);
    }

    private static int CountCoveredMonths(DateTime startDate, DateTime endDate)
    {
        var start = startDate <= endDate ? startDate : endDate;
        var end = endDate >= startDate ? endDate : startDate;
        return ((end.Year - start.Year) * 12) + end.Month - start.Month + 1;
    }

    private static double RoundMoney(double value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private void ReadExcel()
    {
        ExcelStatus = "已读取";
        StatusMessage = "读取Excel文件入口已预留，当前使用内存样例数据";
    }

    private void ClearExcel()
    {
        ExcelStatus = "未读取";
        StatusMessage = "已清空Excel读取状态";
    }

    private async Task StartGenerateAsync()
    {
        if (BankUser is null)
        {
            StatusMessage = "请选择用户后再生成流水";
            MessageBox.Show("请选择用户", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedReferences = References.Where(item => item.IsCheck).ToList();
        var selectedConstItems = ConstItems.Where(item => item.IsCheck).ToList();
        if (selectedReferences.Count == 0 && selectedConstItems.Count == 0)
        {
            StatusMessage = "请至少勾选一条参照明细或固定日期增加项目";
            MessageBox.Show("请至少勾选一条参照明细或固定日期增加项目", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Config.EndTime < Config.StartTime)
        {
            StatusMessage = "结束日期不能早于开始日期";
            MessageBox.Show("结束日期不能早于开始日期", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        GenerationProgress = 0;

        try
        {
            SetGenerationProgress(5, "正在保存当前配置");
            await SaveCurrentSnapshotAsync();
            await Task.Yield();

            SetGenerationProgress(12, "正在检查已有流水");
            var existingRecords = await flowRecordRepository.ListByUserAsync(Bank.Id, BankUser.Id);
            if (existingRecords.Count > 0)
            {
                var overwriteResult = MessageBox.Show(
                    $"当前用户已有 {existingRecords.Count} 条流水，是否覆盖并重新生成？",
                    "开始生成流水",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (overwriteResult != MessageBoxResult.Yes)
                {
                    SetGenerationProgress(0, "已取消生成");
                    return;
                }
            }

            SetGenerationProgress(25, "正在读取结息配置");
            var interestSetting = await LoadInterestSettingForGenerationAsync();
            if (BankUser.AutoCalculateInterest && !BankInterestSettingDefaults.HasEffectiveConfig(interestSetting))
            {
                SetGenerationProgress(0, "请先设置利息配置");
                MessageBox.Show("当前用户已勾选自动计算利息，请先设置利息配置", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await Task.Yield();

            SetGenerationProgress(40, "正在生成流水");
            var result = await GenerateValidResultOnceAsync(BankUser, interestSetting);
            await Task.Yield();

            if (result.MinimumBalance < -0.009d)
            {
                var correctionResult = MessageBox.Show(
                    $"生成过程中出现负余额，最低余额 {result.MinimumBalance:N2}。\n\n是否执行负余额修正？\n系统会优先调整流水时间和可选支出，不修改期初余额。\n\n选择“否”才会保留负余额并继续保存。",
                    "提示",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (correctionResult == MessageBoxResult.Yes)
                {
                    result = CorrectGeneratedResult(BankUser, interestSetting, result);
                    StatusMessage = "已完成负余额修正。";
                }
                else
                {
                    StatusMessage = "用户选择保留负余额，已按原生成结果保存。";
                }
            }
            else if (result.RequiresOpeningBalanceCorrection)
            {
                StatusMessage = "生成结果已自动放行保存，请在流水明细中查看余额。";
            }

            SyncOpeningBalanceFromResult(result);

            SetGenerationProgress(72, "正在保存生成流水");
            await flowRecordRepository.SaveAllAsync(Bank.Id, BankUser.Id, result.Records);
            await SaveBankUserValuesAsync();
            await Task.Yield();

            SetGenerationProgress(92, "正在准备流水明细页面");
            StatusMessage = $"生成完成：{result.Records.Count} 条，收入合计 {result.IncomeTotal:N2}，支出合计 {result.ExpenseTotal:N2}，期末余额 {result.FinalBalance:N2}";
            MessageBox.Show(
                $"生成成功\n\n流水条数：{result.Records.Count}",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            SetGenerationProgress(100, "生成完成，正在打开流水明细");
            RequestOpenGeneratedFlowDetails?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            GenerationProgress = 0;
            StatusMessage = $"生成失败：{ex.Message}";
            MessageBox.Show($"生成失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private GenerationAttempt GenerateWithRequest(FlowAutoGenerationRequest request)
    {
        return new GenerationAttempt(
            flowAutoGenerator.Generate(request),
            "已按本地复刻规则生成流水",
            null);
    }

    private async Task<FlowAutoGenerationResult> GenerateValidResultOnceAsync(
        BankUser bankUser,
        BankInterestSetting? interestSetting)
    {
        var request = CreateGenerationRequest(bankUser, interestSetting, null);
        var attempt = await Task.Run(() => GenerateWithRequest(request));
        if (!string.IsNullOrWhiteSpace(attempt.Message))
        {
            StatusMessage = attempt.Message;
        }

        if (attempt.Result is null)
        {
            throw new InvalidOperationException(attempt.ErrorMessage ?? "生成流水失败。");
        }

        var result = attempt.Result;
        if (IsGenerationResultAccepted(result, bankUser, interestSetting, out var reason))
        {
            return result;
        }

        StatusMessage = $"生成结果已自动放行：{reason}";
        return result;
    }

    private sealed record GenerationAttempt(
        FlowAutoGenerationResult? Result,
        string? Message,
        string? ErrorMessage);

    private bool IsGenerationResultAccepted(
        FlowAutoGenerationResult result,
        BankUser bankUser,
        BankInterestSetting? interestSetting,
        out string reason)
    {
        var finalBalanceTarget = result.OpeningBalance + Config.LastMoney;
        if (Math.Abs(result.FinalBalance - finalBalanceTarget) > FinalBalanceTolerance)
        {
            reason = $"最后余额偏差超过 1000，目标 {finalBalanceTarget:N2}，实际 {result.FinalBalance:N2}";
            return false;
        }

        if (result.MinimumBalance < -0.009d)
        {
            reason = $"生成过程中出现负余额，最低余额 {result.MinimumBalance:N2}";
            return false;
        }

        if (!IsInterestResultAccepted(result, bankUser, interestSetting, out reason))
        {
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool IsInterestResultAccepted(
        FlowAutoGenerationResult result,
        BankUser bankUser,
        BankInterestSetting? interestSetting,
        out string reason)
    {
        if (!bankUser.AutoCalculateInterest)
        {
            reason = string.Empty;
            return true;
        }

        var expectedCount = CountExpectedInterestRows(interestSetting);
        var interestBrief = ResolveInterestBrief(interestSetting);
        var rows = result.Records
            .Where(item => IsSettlementInterestRecord(item, interestBrief))
            .ToList();

        if (rows.Count < expectedCount)
        {
            reason = $"结息流水数量不对，应有 {expectedCount} 条，实际 {rows.Count} 条";
            return false;
        }

        foreach (var row in rows)
        {
            if ((row.TradeMoney ?? 0) <= 0.009d)
            {
                reason = $"结息金额不对，出现 {row.TradeMoney.GetValueOrDefault():N2}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(row.Account)
                || string.IsNullOrWhiteSpace(row.ProductBrief)
                || string.IsNullOrWhiteSpace(row.ProductName)
                || string.IsNullOrWhiteSpace(row.CashCheck)
                || string.IsNullOrWhiteSpace(row.Usage)
                || string.IsNullOrWhiteSpace(row.TradeExplain)
                || string.IsNullOrWhiteSpace(row.Remark)
                || string.IsNullOrWhiteSpace(row.SerialNum)
                || !row.Balance.HasValue)
            {
                reason = "结息流水字段不完整";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private int CountExpectedInterestRows(BankInterestSetting? interestSetting)
    {
        var months = ParseInterestMonths(interestSetting?.Months).ToList();
        if (months.Count == 0)
        {
            months.AddRange([3, 6, 9, 12]);
        }

        var day = Math.Clamp(ParseInterestInt(interestSetting?.SettlementDay, 21), 1, 31);
        var start = Config.StartTime;
        var end = Config.EndTime.TimeOfDay == TimeSpan.Zero
            ? Config.EndTime.Date.AddDays(1).AddTicks(-1)
            : Config.EndTime;
        var count = 0;
        for (var year = start.Year; year <= end.Year; year++)
        {
            foreach (var month in months)
            {
                var settlementDay = Math.Min(day, DateTime.DaysInMonth(year, month));
                var settlementDate = new DateTime(year, month, settlementDay);
                if (settlementDate >= start.Date && settlementDate <= end)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static IEnumerable<int> ParseInterestMonths(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var token in value.Split([',', ';', ' ', '|', '/', '\\', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var parsed) && parsed is >= 1 and <= 12)
            {
                yield return parsed;
            }
        }
    }

    private static int ParseInterestInt(string? value, int defaultValue)
    {
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static string ResolveInterestBrief(BankInterestSetting? interestSetting)
    {
        return FirstNonEmpty(
            interestSetting?.Fields.FirstOrDefault(item =>
                string.Equals(item.Field, nameof(FlowRecord.ProductBrief), StringComparison.Ordinal))?.Value,
            "结息");
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return string.Empty;
    }

    private static bool IsSettlementInterestRecord(FlowRecord record, string interestBrief)
    {
        var text = string.Join('|',
            record.ProductBrief,
            record.ProductName,
            record.Remark,
            record.Usage,
            record.TradeExplain);

        return !text.Contains("利息税", StringComparison.Ordinal)
            && (text.Contains(interestBrief, StringComparison.Ordinal)
                || text.Contains("结息", StringComparison.Ordinal));
    }

    private async Task<BankInterestSetting?> LoadInterestSettingForGenerationAsync()
    {
        var stored = await interestSettingsRepository.LoadAsync(Bank.Id);
        if (BankInterestSettingDefaults.HasEffectiveConfig(stored))
        {
            return stored;
        }

        var defaultSetting = BankInterestSettingDefaults.CreateDefault(Bank);
        return BankInterestSettingDefaults.HasEffectiveConfig(defaultSetting)
            ? defaultSetting
            : stored;
    }

    private FlowAutoGenerationResult CorrectGeneratedResult(
        BankUser bankUser,
        BankInterestSetting? interestSetting,
        FlowAutoGenerationResult result)
    {
        var request = CreateGenerationRequest(bankUser, interestSetting, null);
        return flowAutoGenerator.ApplyNegativeBalanceCorrection(request, result);
    }

    private void SyncOpeningBalanceFromResult(FlowAutoGenerationResult result)
    {
        Config.OpeningBalance = result.OpeningBalance;
        if (BankUser is not null)
        {
            BankUser.OpeningBalance = Convert.ToDecimal(result.OpeningBalance);
        }
    }

    private FlowAutoGenerationRequest CreateGenerationRequest(
        BankUser bankUser,
        BankInterestSetting? interestSetting,
        double? openingBalanceOverride)
    {
        return new FlowAutoGenerationRequest
        {
            Bank = Bank,
            BankUser = bankUser,
            Config = Config.Clone(),
            References = References.Select(item => item.Clone()).ToList(),
            ConstItems = ConstItems.Select(item => item.Clone()).ToList(),
            InterestSetting = interestSetting,
            OpeningBalanceOverride = openingBalanceOverride
        };
    }

    private void SetGenerationProgress(int progress, string message)
    {
        GenerationProgress = progress;
        StatusMessage = message;
    }

    private void AddMonthDetail()
    {
        var startTime = Config.MonthGenData.LastOrDefault()?.EndTime.AddDays(1) ?? Config.StartTime;
        if (startTime > Config.EndTime)
        {
            startTime = Config.EndTime;
        }

        var endTime = new DateTime(startTime.Year, startTime.Month, DateTime.DaysInMonth(startTime.Year, startTime.Month));
        if (endTime > Config.EndTime)
        {
            endTime = Config.EndTime;
        }

        var item = new MonthGenerateRule
        {
            StartTime = startTime.Date,
            EndTime = endTime.Date,
            InMoney = Config.MinInMoneyMonth2,
            OutMoney = Config.MinOutMoneyMonth2
        };

        Config.MonthGenData.Add(item);
        SelectedMonthDetail = item;
        StatusMessage = "已新增按月明细";
    }

    private void DeleteMonthDetail(object? parameter)
    {
        var item = parameter as MonthGenerateRule ?? SelectedMonthDetail;
        if (item is null)
        {
            StatusMessage = "请先选择按月明细";
            return;
        }

        Config.MonthGenData.Remove(item);
        SelectedMonthDetail = Config.MonthGenData.FirstOrDefault();
        StatusMessage = "已删除按月明细";
    }

    private void ClearMonthDetails()
    {
        Config.MonthGenData.Clear();
        SelectedMonthDetail = null;
        StatusMessage = "已清空按月明细";
    }

    private void MarkReserved(string featureName)
    {
        StatusMessage = $"{featureName}入口已预留";
    }
}
