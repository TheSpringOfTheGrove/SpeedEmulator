using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SpeedEmulator.Controls;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class BankUsersWindow : Window
{
    private readonly BankUsersViewModel viewModel;
    private readonly IBankUserRepository bankUserRepository;
    private readonly IBankUserColumnSettingsRepository columnSettingsRepository;
    private readonly IBankInterestSettingsRepository interestSettingsRepository;
    private readonly IFlowGenerationRepository flowGenerationRepository;
    private readonly IFlowRecordRepository flowRecordRepository;

    public BankUsersWindow(
        BankUsersViewModel viewModel,
        IBankUserRepository bankUserRepository,
        IBankUserColumnSettingsRepository columnSettingsRepository,
        IBankInterestSettingsRepository interestSettingsRepository,
        IFlowGenerationRepository flowGenerationRepository,
        IFlowRecordRepository flowRecordRepository)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.bankUserRepository = bankUserRepository;
        this.columnSettingsRepository = columnSettingsRepository;
        this.interestSettingsRepository = interestSettingsRepository;
        this.flowGenerationRepository = flowGenerationRepository;
        this.flowRecordRepository = flowRecordRepository;
        DataContext = viewModel;
        viewModel.RequestClose += ViewModel_RequestClose;
        viewModel.RequestOpenAutoGenerateFlow += ViewModel_RequestOpenAutoGenerateFlow;
        viewModel.RequestOpenFlowDetails += ViewModel_RequestOpenFlowDetails;
        viewModel.RequestOpenColumnSettings += ViewModel_RequestOpenColumnSettings;
        viewModel.RequestOpenInterestSettings += ViewModel_RequestOpenInterestSettings;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await viewModel.ApplyColumnSettingsAsync();
        BuildDynamicColumns(viewModel.Bank);
        await viewModel.LoadAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.RequestClose -= ViewModel_RequestClose;
        viewModel.RequestOpenAutoGenerateFlow -= ViewModel_RequestOpenAutoGenerateFlow;
        viewModel.RequestOpenFlowDetails -= ViewModel_RequestOpenFlowDetails;
        viewModel.RequestOpenColumnSettings -= ViewModel_RequestOpenColumnSettings;
        viewModel.RequestOpenInterestSettings -= ViewModel_RequestOpenInterestSettings;
        base.OnClosed(e);
    }

    private void ViewModel_RequestClose(object? sender, EventArgs e)
    {
        Close();
    }

    private void ViewModel_RequestOpenAutoGenerateFlow(object? sender, EventArgs e)
    {
        try
        {
            var autoGenerateViewModel = new FlowGenerationViewModel(
                viewModel.Bank,
                viewModel.FlowGenerationTargetUser,
                flowGenerationRepository,
                bankUserRepository,
                flowRecordRepository,
                interestSettingsRepository,
                new FlowRuleExcelService());

            var window = new FlowGenerationWindow(autoGenerateViewModel, columnSettingsRepository, interestSettingsRepository, flowRecordRepository)
            {
                Owner = this
            };

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开自动生成流水页面失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ViewModel_RequestOpenFlowDetails(object? sender, EventArgs e)
    {
        if (viewModel.FlowDetailsTargetUser is not { } targetUser)
        {
            return;
        }

        var flowDetailsViewModel = new FlowDetailsViewModel(viewModel.Bank, targetUser, flowRecordRepository);
        var window = new FlowDetailsWindow(flowDetailsViewModel, columnSettingsRepository)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private async void ViewModel_RequestOpenColumnSettings(object? sender, EventArgs e)
    {
        var savedSettings = await columnSettingsRepository.LoadAsync(viewModel.Bank.Id, ColumnSettingScopes.BankUsers);
        var settingsViewModel = new BankUserColumnSettingsViewModel(
            viewModel.Bank,
            columnSettingsRepository,
            ColumnSettingScopes.BankUsers,
            viewModel.Bank.Columns,
            savedSettings,
            $"{viewModel.Bank.Name} 字段设置");
        var window = new BankUserColumnSettingsWindow(settingsViewModel)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            await viewModel.ApplyColumnSettingsAsync();
            BuildDynamicColumns(viewModel.Bank);
            viewModel.NotifyColumnSettingsSaved();
        }
    }

    private async void ViewModel_RequestOpenInterestSettings(object? sender, EventArgs e)
    {
        var settingsViewModel = new BankInterestSettingsViewModel(viewModel.Bank, interestSettingsRepository);
        await settingsViewModel.LoadAsync();

        var window = new BankInterestSettingsWindow(settingsViewModel)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            viewModel.NotifyInterestSettingsSaved();
        }
    }

    private void BuildDynamicColumns(Bank bank)
    {
        UsersGrid.Columns.Clear();

        foreach (var item in bank.Columns
                     .Select((column, index) => new { Column = column, Index = index })
                     .Where(item => item.Column.Show && !string.IsNullOrWhiteSpace(item.Column.Field))
                     .OrderBy(item => IsIdColumn(item.Column) ? int.MinValue : item.Column.Order)
                     .ThenBy(item => item.Index))
        {
            var column = item.Column;
            if (string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase))
            {
                UsersGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = column.Name,
                    Binding = new Binding()
                    {
                        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1),
                        Converter = new RowNumberConverter(),
                        Mode = BindingMode.OneWay
                    },
                    Width = new DataGridLength(Math.Max(column.Width, 40)),
                    MinWidth = 40,
                    IsReadOnly = true
                });

                continue;
            }

            var binding = new Binding(CreateBindingPath(column.Field!))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };

            if (string.Equals(column.Type, "Boolean", StringComparison.OrdinalIgnoreCase))
            {
                UsersGrid.Columns.Add(new DataGridCheckBoxColumn
                {
                    Header = column.Name,
                    Binding = binding,
                    IsReadOnly = false,
                    Width = new DataGridLength(Math.Max(column.Width, 60)),
                    MinWidth = 60
                });

                continue;
            }

            if (string.Equals(column.Type, "Money", StringComparison.OrdinalIgnoreCase))
            {
                binding.StringFormat = "N2";
            }
            else if (string.Equals(column.Type, "Date", StringComparison.OrdinalIgnoreCase))
            {
                UsersGrid.Columns.Add(CreateDateColumn(column));
                continue;
            }

            UsersGrid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Name,
                Binding = binding,
                IsReadOnly = false,
                Width = new DataGridLength(Math.Max(column.Width, 60)),
                MinWidth = 60
            });
        }
    }

    private void UsersGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = (DependencyObject)e.OriginalSource;
        var cell = FindVisualParent<DataGridCell>(source);
        if (cell is null)
        {
            return;
        }

        if (!cell.IsFocused)
        {
            cell.Focus();
        }

        if (FindVisualParent<DataGridRow>(cell)?.Item is { } rowItem && cell.Column is not null)
        {
            UsersGrid.SelectedItem = rowItem;
            UsersGrid.CurrentCell = new DataGridCellInfo(rowItem, cell.Column);
        }

        if (FindVisualParent<FormattedDatePicker>(source) is not null || cell.IsEditing)
        {
            return;
        }

        UsersGrid.BeginEdit(e);
    }

    private static DataGridTemplateColumn CreateDateColumn(SpeedEmulator.Models.ColumnDefinition column)
    {
        static FrameworkElementFactory CreatePickerFactory(string field)
        {
            var binding = new Binding(CreateBindingPath(field))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };

            var picker = new FrameworkElementFactory(typeof(FormattedDatePicker));
            picker.SetBinding(FormattedDatePicker.SelectedDateProperty, binding);
            picker.SetValue(FrameworkElement.HeightProperty, 26d);
            picker.SetValue(Control.FontSizeProperty, 13d);
            picker.SetValue(Control.PaddingProperty, new Thickness(4, 0, 4, 0));
            picker.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            picker.SetValue(Control.BorderBrushProperty, Brushes.Transparent);
            picker.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            return picker;
        }

        return new DataGridTemplateColumn
        {
            Header = column.Name,
            CellTemplate = new DataTemplate { VisualTree = CreatePickerFactory(column.Field!) },
            CellEditingTemplate = new DataTemplate { VisualTree = CreatePickerFactory(column.Field!) },
            Width = new DataGridLength(Math.Max(column.Width, 120)),
            MinWidth = 120,
            IsReadOnly = true
        };
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typed)
            {
                return typed;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private static string CreateBindingPath(string field)
    {
        if (field.StartsWith('[') && field.EndsWith(']'))
        {
            return field;
        }

        return typeof(BankUser).GetProperty(field) is null ? $"[{field}]" : field;
    }

    private static bool IsIdColumn(SpeedEmulator.Models.ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RowNumberConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is DataGridRow row ? row.GetIndex() + 1 : string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
