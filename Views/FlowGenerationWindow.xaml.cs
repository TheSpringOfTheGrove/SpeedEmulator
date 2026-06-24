using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class FlowGenerationWindow : Window
{
    private readonly FlowGenerationViewModel viewModel;
    private readonly IBankUserColumnSettingsRepository columnSettingsRepository;
    private readonly IBankInterestSettingsRepository interestSettingsRepository;
    private readonly IFlowRecordRepository flowRecordRepository;

    public FlowGenerationWindow(
        FlowGenerationViewModel viewModel,
        IBankUserColumnSettingsRepository columnSettingsRepository,
        IBankInterestSettingsRepository interestSettingsRepository,
        IFlowRecordRepository flowRecordRepository)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.columnSettingsRepository = columnSettingsRepository;
        this.interestSettingsRepository = interestSettingsRepository;
        this.flowRecordRepository = flowRecordRepository;
        DataContext = viewModel;
        viewModel.RequestClose += ViewModel_RequestClose;
        viewModel.RequestOpenMonthDetails += ViewModel_RequestOpenMonthDetails;
        viewModel.RequestOpenColumnSettings += ViewModel_RequestOpenColumnSettings;
        viewModel.RequestOpenInterestSettings += ViewModel_RequestOpenInterestSettings;
        viewModel.RequestOpenGeneratedFlowDetails += ViewModel_RequestOpenGeneratedFlowDetails;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ApplyColumnSettingsAsync(ColumnSettingScopes.FlowReference, viewModel.Bank.ReferenceColumns);
        await ApplyColumnSettingsAsync(ColumnSettingScopes.FlowConst, viewModel.Bank.ConstColumns);
        BuildDynamicColumns(ReferenceGrid, viewModel.Bank.ReferenceColumns);
        BuildDynamicColumns(ConstGrid, viewModel.Bank.ConstColumns);
        await viewModel.LoadAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.RequestClose -= ViewModel_RequestClose;
        viewModel.RequestOpenMonthDetails -= ViewModel_RequestOpenMonthDetails;
        viewModel.RequestOpenColumnSettings -= ViewModel_RequestOpenColumnSettings;
        viewModel.RequestOpenInterestSettings -= ViewModel_RequestOpenInterestSettings;
        viewModel.RequestOpenGeneratedFlowDetails -= ViewModel_RequestOpenGeneratedFlowDetails;
        base.OnClosed(e);
    }

    private void ViewModel_RequestClose(object? sender, EventArgs e)
    {
        Close();
    }

    private void ViewModel_RequestOpenMonthDetails(object? sender, EventArgs e)
    {
        var window = new MonthDetailSettingsWindow(viewModel)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private async void ViewModel_RequestOpenColumnSettings(object? sender, EventArgs e)
    {
        var isConstTab = viewModel.SelectedTabIndex == 1;
        var scope = isConstTab ? ColumnSettingScopes.FlowConst : ColumnSettingScopes.FlowReference;
        var columns = isConstTab ? viewModel.Bank.ConstColumns : viewModel.Bank.ReferenceColumns;
        var grid = isConstTab ? ConstGrid : ReferenceGrid;
        var sectionName = isConstTab ? "固定日期增加项目" : "参照明细";
        var savedSettings = await columnSettingsRepository.LoadAsync(viewModel.Bank.Id, scope);
        var settingsViewModel = new BankUserColumnSettingsViewModel(
            viewModel.Bank,
            columnSettingsRepository,
            scope,
            columns,
            savedSettings,
            $"{viewModel.Bank.Name}{sectionName}字段设置");

        var window = new BankUserColumnSettingsWindow(settingsViewModel)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            await ApplyColumnSettingsAsync(scope, columns);
            BuildDynamicColumns(grid, columns);
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

    private void ViewModel_RequestOpenGeneratedFlowDetails(object? sender, EventArgs e)
    {
        if (viewModel.BankUser is null)
        {
            return;
        }

        var flowDetailsViewModel = new FlowDetailsViewModel(viewModel.Bank, viewModel.BankUser, flowRecordRepository);
        var window = new FlowDetailsWindow(flowDetailsViewModel, columnSettingsRepository)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    private async Task ApplyColumnSettingsAsync(string scope, IEnumerable<SpeedEmulator.Models.ColumnDefinition> columns)
    {
        var settings = await columnSettingsRepository.LoadAsync(viewModel.Bank.Id, scope);
        ColumnSettingsApplier.Apply(columns, settings);
    }

    private static void BuildDynamicColumns(DataGrid grid, IEnumerable<SpeedEmulator.Models.ColumnDefinition> columns)
    {
        grid.Columns.Clear();

        foreach (var item in columns
                     .Select((column, index) => new { Column = column, Index = index })
                     .Where(item => item.Column.Show && !string.IsNullOrWhiteSpace(item.Column.Field))
                     .OrderBy(item => IsIdColumn(item.Column) ? int.MinValue : item.Column.Order)
                     .ThenBy(item => item.Index))
        {
            var column = item.Column;
            if (string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase))
            {
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = column.Name,
                    HeaderStyle = CreateCenteredHeaderStyle(),
                    Binding = new Binding()
                    {
                        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1),
                        Converter = new RowNumberConverter(),
                        Mode = BindingMode.OneWay
                    },
                    Width = new DataGridLength(Math.Max(column.Width, 40)),
                    MinWidth = 40,
                    ElementStyle = CreateCenteredTextElementStyle(),
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
                grid.Columns.Add(new DataGridCheckBoxColumn
                {
                    Header = column.Name,
                    Binding = binding,
                    Width = new DataGridLength(Math.Max(column.Width, 54)),
                    MinWidth = 54,
                    ElementStyle = CreateCheckBoxElementStyle(),
                    EditingElementStyle = CreateCheckBoxElementStyle()
                });

                continue;
            }

            if (string.Equals(column.Type, "Money", StringComparison.OrdinalIgnoreCase))
            {
                binding.StringFormat = "N2";
            }

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Name,
                Binding = binding,
                Width = new DataGridLength(Math.Max(column.Width, 60)),
                MinWidth = 60,
                ElementStyle = CreateTextElementStyle(column),
                EditingElementStyle = CreateTextEditingElementStyle()
            });
        }
    }

    private void RulesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

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
            grid.SelectedItem = rowItem;
            grid.CurrentCell = new DataGridCellInfo(rowItem, cell.Column);
        }

        if (cell.IsEditing || FindVisualParent<TextBox>(source) is not null)
        {
            return;
        }

        grid.BeginEdit(e);
    }

    private static Style CreateTextElementStyle(SpeedEmulator.Models.ColumnDefinition column)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Left));
        style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Black));

        if (ShouldUseIncomeExpenseBrush(column))
        {
            style.Triggers.Add(new DataTrigger
            {
                Binding = new Binding(nameof(FlowRuleBase.IncomeAttribute)),
                Value = "收入",
                Setters = { new Setter(TextBlock.ForegroundProperty, Brushes.Red) }
            });
            style.Triggers.Add(new DataTrigger
            {
                Binding = new Binding(nameof(FlowRuleBase.IncomeAttribute)),
                Value = "支出",
                Setters = { new Setter(TextBlock.ForegroundProperty, Brushes.Green) }
            });
        }

        style.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(nameof(DataGridCell.IsSelected))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1)
            },
            Value = true,
            Setters = { new Setter(TextBlock.ForegroundProperty, Brushes.White) }
        });

        return style;
    }

    private static Style CreateCenteredHeaderStyle()
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.HeightProperty, 36d));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(242, 242, 242))));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(215, 215, 215))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        return style;
    }

    private static Style CreateCenteredTextElementStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Black));
        style.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(nameof(DataGridCell.IsSelected))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1)
            },
            Value = true,
            Setters = { new Setter(TextBlock.ForegroundProperty, Brushes.White) }
        });

        return style;
    }

    private static Style CreateTextEditingElementStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(TextBox.TextAlignmentProperty, TextAlignment.Left));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 2, 0)));
        return style;
    }

    private static Style CreateCheckBoxElementStyle()
    {
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0)));
        return style;
    }

    private static bool ShouldUseIncomeExpenseBrush(SpeedEmulator.Models.ColumnDefinition column)
    {
        return column.Field is nameof(FlowRuleBase.IncomeAttribute)
            or nameof(FlowRuleBase.MinMoney)
            or nameof(FlowRuleBase.MaxMoney)
            or nameof(FlowRuleBase.FloutLength)
            or nameof(FlowRuleBase.StartDay)
            or nameof(FlowRuleBase.EndDay)
            or nameof(GenerateReferenceRule.PercentMonth);
    }

    private static bool IsIdColumn(SpeedEmulator.Models.ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase);
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

        return typeof(FlowRuleBase).GetProperty(field) is not null
            || typeof(GenerateReferenceRule).GetProperty(field) is not null
            || typeof(GenerateConstRule).GetProperty(field) is not null
                ? field
                : $"[{field}]";
    }

    private sealed class RowNumberConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is DataGridRow row ? row.GetIndex() + 1 : string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
