using System.Collections.ObjectModel;
using System.Windows;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;

namespace SpeedEmulator.ViewModels;

public sealed class FlowGenerationViewModel : ObservableObject
{
    private readonly IFlowGenerationRepository repository;
    private readonly IBankUserRepository bankUserRepository;
    private readonly IFlowRecordRepository flowRecordRepository;
    private readonly IBankInterestSettingsRepository interestSettingsRepository;
    private readonly IFlowRuleExcelService excelService;
    private readonly FlowAutoGenerator autoGenerator = new();
    private FlowGenerationConfig config = new();
    private GenerateReferenceRule? selectedReference;
    private GenerateConstRule? selectedConst;
    private MonthGenerateRule? selectedMonthDetail;
    private int selectedTabIndex;
    private string excelStatus = "未读取";
    private string statusMessage = "准备生成流水";
    private bool isBusy;
    private int generationProgress;

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
            var snapshot = await repository.LoadAsync(Bank.Id, BankUser?.Id);
            Config = snapshot.Config;
            ApplyBankUserValuesToConfig();

            References.Clear();
            foreach (var item in snapshot.References)
            {
                References.Add(item);
            }

            ConstItems.Clear();
            foreach (var item in snapshot.ConstItems)
            {
                ConstItems.Add(item);
            }

            SelectedReference = References.FirstOrDefault();
            SelectedConst = ConstItems.FirstOrDefault();
            SelectedMonthDetail = Config.MonthGenData.FirstOrDefault();
            StatusMessage = $"已载入参照明细 {References.Count} 条，固定日期增加项目 {ConstItems.Count} 条，月明细 {Config.MonthGenData.Count} 条";
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
            ConstItems.Add(item);
            SelectedConst = item;
            StatusMessage = "已新增固定日期增加项目";
            return;
        }

        var reference = GenerateReferenceRule.CreateDefault(Bank.Id);
        References.Add(reference);
        SelectedReference = reference;
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

            ConstItems.Remove(SelectedConst);
            SelectedConst = ConstItems.FirstOrDefault();
            StatusMessage = "已删除固定日期增加项目";
            return;
        }

        if (SelectedReference is null)
        {
            StatusMessage = "请先选择参照明细";
            return;
        }

        References.Remove(SelectedReference);
        SelectedReference = References.FirstOrDefault();
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
            References.Clear();
        }

        foreach (var item in imported)
        {
            References.Add(item);
        }

        SelectedReference = imported.FirstOrDefault() ?? References.FirstOrDefault();
        return imported.Count;
    }

    private int ImportConstItems(string path, bool replaceCurrentRows)
    {
        var imported = excelService.ImportConstItems(path, Bank.ConstColumns, Bank.Id);
        if (replaceCurrentRows)
        {
            ConstItems.Clear();
        }

        foreach (var item in imported)
        {
            ConstItems.Add(item);
        }

        SelectedConst = imported.FirstOrDefault() ?? ConstItems.FirstOrDefault();
        return imported.Count;
    }

    private async Task SaveCurrentSnapshotAsync()
    {
        var snapshot = new FlowGenerationSnapshot
        {
            Config = Config.Clone(),
            References = References.Select(item => item.Clone()).ToList(),
            ConstItems = ConstItems.Select(item => item.Clone()).ToList()
        };

        await repository.SaveAsync(Bank.Id, BankUser?.Id, snapshot);
        await SaveBankUserValuesAsync();
    }

    private void Compute()
    {
        var monthCount = CountCoveredMonths(Config.StartTime, Config.EndTime);
        var totalOutMoney = Config.OpeningBalance + Config.AllInMoney - Config.LastMoney;
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
            var interestSetting = await interestSettingsRepository.LoadAsync(Bank.Id);
            await Task.Yield();

            SetGenerationProgress(40, "正在生成临时流水");
            var result = GenerateWithCurrentConfig(BankUser, interestSetting, null);
            await Task.Yield();

            if (result.RequiresOpeningBalanceCorrection)
            {
                var prompt = result.MinimumBalance < -0.009d
                    ? $"生成流水中出现负数余额（最低余额 {result.MinimumBalance:N2}）。\n\n是否自动修正期初余额为 {result.RequiredOpeningBalance:N2}？"
                    : $"生成后的最后卡余额为 {result.FinalBalance:N2}，与配置的最后卡余额 {Config.LastMoney:N2} 不一致。\n\n是否自动修正期初余额为 {result.RequiredOpeningBalance:N2}？";

                var fixResult = MessageBox.Show(prompt, "修正负数余额", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (fixResult != MessageBoxResult.Yes)
                {
                    SetGenerationProgress(0, "已取消生成，未修正负数余额");
                    return;
                }

                Config.OpeningBalance = result.RequiredOpeningBalance;
                SetGenerationProgress(55, "正在修正期初余额和期末余额");
                result = CorrectGeneratedResult(BankUser, interestSetting, result, Config.OpeningBalance);
                if (!result.RequiresOpeningBalanceCorrection)
                {
                    await SaveBankUserValuesAsync();
                }

                await Task.Yield();
            }

            if (result.RequiresOpeningBalanceCorrection)
            {
                StatusMessage = "余额修正失败，请调整期初余额或金额配置后重试";
                MessageBox.Show("余额修正失败，请调整期初余额或金额配置后重试", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                GenerationProgress = 0;
                return;
            }

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

    private FlowAutoGenerationResult GenerateWithCurrentConfig(
        BankUser bankUser,
        BankInterestSetting? interestSetting,
        double? openingBalanceOverride)
    {
        return autoGenerator.Generate(new FlowAutoGenerationRequest
        {
            Bank = Bank,
            BankUser = bankUser,
            Config = Config,
            References = References.Select(item => item.Clone()).ToList(),
            ConstItems = ConstItems.Select(item => item.Clone()).ToList(),
            InterestSetting = interestSetting,
            OpeningBalanceOverride = openingBalanceOverride
        });
    }

    private FlowAutoGenerationResult CorrectGeneratedResult(
        BankUser bankUser,
        BankInterestSetting? interestSetting,
        FlowAutoGenerationResult result,
        double correctedOpeningBalance)
    {
        return autoGenerator.ApplyOpeningBalanceCorrection(
            CreateGenerationRequest(bankUser, interestSetting, correctedOpeningBalance),
            result,
            correctedOpeningBalance);
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
            Config = Config,
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
