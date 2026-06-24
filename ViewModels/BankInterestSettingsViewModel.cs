using System.Collections.ObjectModel;
using System.Windows;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;

namespace SpeedEmulator.ViewModels;

public sealed class BankInterestSettingsViewModel : ObservableObject
{
    private readonly Bank bank;
    private readonly IBankInterestSettingsRepository repository;
    private BankInterestSetting setting;
    private string statusMessage = "配置当前银行的结息日期、月份、时间范围、利率和结息流水字段。";

    public BankInterestSettingsViewModel(Bank bank, IBankInterestSettingsRepository repository)
    {
        this.bank = bank;
        this.repository = repository;
        setting = CreateDefaultSetting(bank);

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, new DialogCloseRequestedEventArgs(false)));
    }

    public event EventHandler<DialogCloseRequestedEventArgs>? RequestClose;

    public string WindowTitle => $"利息设置-{bank.Name}";

    public BankInterestSetting Setting
    {
        get => setting;
        private set
        {
            if (SetProperty(ref setting, value))
            {
                OnPropertyChanged(nameof(Fields));
            }
        }
    }

    public ObservableCollection<BankInterestFieldValue> Fields => Setting.Fields;

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public RelayCommand CancelCommand { get; }

    public async Task LoadAsync()
    {
        var stored = await repository.LoadAsync(bank.Id);
        Setting = MergeWithCurrentFlowColumns(stored ?? CreateDefaultSetting(bank));
    }

    private async Task SaveAsync()
    {
        try
        {
            Setting.BankId = bank.Id;
            Setting.BankName = bank.Name;
            Setting.BankType = bank.Type;
            await repository.SaveAsync(bank.Id, Setting);
            StatusMessage = "利息设置已保存";
            MessageBox.Show("保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            RequestClose?.Invoke(this, new DialogCloseRequestedEventArgs(true));
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            MessageBox.Show($"保存失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        await repository.DeleteAsync(bank.Id);
        Setting = MergeWithCurrentFlowColumns(CreateDefaultSetting(bank));
        StatusMessage = "利息设置已删除";
    }

    private BankInterestSetting MergeWithCurrentFlowColumns(BankInterestSetting source)
    {
        var valuesByField = source.Fields
            .Where(item => !string.IsNullOrWhiteSpace(item.Field))
            .GroupBy(item => item.Field, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

        return new BankInterestSetting
        {
            BankId = bank.Id,
            BankName = bank.Name,
            BankType = bank.Type,
            SettlementDay = source.SettlementDay,
            Months = source.Months,
            StartTime = source.StartTime,
            EndTime = source.EndTime,
            RatePercent = source.RatePercent,
            Fields = new ObservableCollection<BankInterestFieldValue>(
                CreateConfigurableFlowFields()
                    .Select(item =>
                    {
                        valuesByField.TryGetValue(item.Field, out var value);
                        item.Value = value ?? item.Value;
                        return item;
                    }))
        };
    }

    private IEnumerable<BankInterestFieldValue> CreateConfigurableFlowFields()
    {
        return bank.FlowColumns
            .Where(IsConfigurableFlowColumn)
            .GroupBy(column => column.Field!, StringComparer.Ordinal)
            .Select(group => group.OrderBy(column => column.Order).First())
            .OrderBy(column => column.Order)
            .Select(column => new BankInterestFieldValue
            {
                Name = column.Name ?? column.Field ?? string.Empty,
                Field = column.Field ?? string.Empty,
                Order = column.Order,
                Value = string.Empty
            });
    }

    private static bool IsConfigurableFlowColumn(ColumnDefinition column)
    {
        if (!column.Show)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(column.Field) || string.IsNullOrWhiteSpace(column.Name))
        {
            return false;
        }

        if (string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.Ordinal)
            || string.Equals(column.Field, nameof(FlowRecord.Id), StringComparison.Ordinal))
        {
            return false;
        }

        if (IsDateType(column.Type) || IsMoneyType(column.Type))
        {
            return false;
        }

        return !column.Name.Contains("金额", StringComparison.Ordinal)
            && !column.Name.Contains("余额", StringComparison.Ordinal)
            && !column.Name.Contains("日期", StringComparison.Ordinal)
            && !column.Name.Contains("时间", StringComparison.Ordinal);
    }

    private static bool IsDateType(string? type)
    {
        return string.Equals(type, "Date", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "DateTime", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMoneyType(string? type)
    {
        return string.Equals(type, "Money", StringComparison.OrdinalIgnoreCase);
    }

    private static BankInterestSetting CreateDefaultSetting(Bank bank)
    {
        var setting = new BankInterestSetting
        {
            BankId = bank.Id,
            BankName = bank.Name,
            BankType = bank.Type
        };

        if (IsAgriculturalBank(bank))
        {
            setting.SettlementDay = "21";
            setting.Months = "3;6;9;12";
            setting.StartTime = "0";
            setting.EndTime = "23";
            setting.RatePercent = "0.15";
            setting.Fields.Add(new BankInterestFieldValue
            {
                Name = "摘要",
                Field = nameof(FlowRecord.ProductBrief),
                Value = "结息"
            });
        }

        return setting;
    }

    private static bool IsAgriculturalBank(Bank bank)
    {
        return bank.Name.Contains("农行", StringComparison.Ordinal)
            || bank.Name.Contains("农业", StringComparison.Ordinal);
    }
}
