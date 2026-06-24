using System.Collections.ObjectModel;
using System.Windows;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;

namespace SpeedEmulator.ViewModels;

public sealed class BankUserColumnSettingsViewModel : ObservableObject
{
    private readonly Bank bank;
    private readonly IBankUserColumnSettingsRepository repository;
    private readonly string scope;
    private readonly string windowTitle;
    private string statusMessage = "可设置列表字段的列宽、顺序和显示状态。";

    public BankUserColumnSettingsViewModel(Bank bank, IBankUserColumnSettingsRepository repository)
        : this(bank, repository, ColumnSettingScopes.BankUsers, bank.Columns, [], $"{bank.Name} 字段设置")
    {
    }

    public BankUserColumnSettingsViewModel(
        Bank bank,
        IBankUserColumnSettingsRepository repository,
        string scope,
        IEnumerable<ColumnDefinition> columns,
        string windowTitle)
        : this(bank, repository, scope, columns, [], windowTitle)
    {
    }

    public BankUserColumnSettingsViewModel(
        Bank bank,
        IBankUserColumnSettingsRepository repository,
        string scope,
        IEnumerable<ColumnDefinition> columns,
        IReadOnlyList<BankUserColumnSetting> savedSettings,
        string windowTitle)
    {
        this.bank = bank;
        this.repository = repository;
        this.scope = scope;
        this.windowTitle = windowTitle;

        var settingsByField = savedSettings
            .Where(item => !string.IsNullOrWhiteSpace(item.Field))
            .GroupBy(item => item.Field, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var column in columns.Where(column => !IsIdColumn(column)))
        {
            Columns.Add(CreateSetting(column, settingsByField));
        }

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, new DialogCloseRequestedEventArgs(false)));
    }

    public event EventHandler<DialogCloseRequestedEventArgs>? RequestClose;

    public string WindowTitle => windowTitle;

    public ObservableCollection<BankUserColumnSetting> Columns { get; } = [];

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        try
        {
            foreach (var column in Columns)
            {
                column.Normalize();
            }

            await repository.SaveAsync(bank.Id, scope, Columns);
            StatusMessage = "字段设置已保存";
            MessageBox.Show("保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            RequestClose?.Invoke(this, new DialogCloseRequestedEventArgs(true));
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            MessageBox.Show($"保存失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsIdColumn(ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase);
    }

    private static BankUserColumnSetting CreateSetting(
        ColumnDefinition column,
        IReadOnlyDictionary<string, BankUserColumnSetting> settingsByField)
    {
        if (!string.IsNullOrWhiteSpace(column.Field) && settingsByField.TryGetValue(column.Field, out var saved))
        {
            var copy = saved.Clone();
            copy.Name = column.Name ?? copy.Name;
            copy.Field = column.Field;
            copy.Type = column.Type ?? copy.Type;
            copy.Normalize();
            return copy;
        }

        return new BankUserColumnSetting
        {
            Name = column.Name ?? string.Empty,
            Field = column.Field ?? string.Empty,
            Type = column.Type ?? "Text",
            Width = 100,
            Order = 0,
            Show = true
        };
    }
}
