using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SpeedEmulator.Controls;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.ViewModels;
using ColumnDefinition = SpeedEmulator.Models.ColumnDefinition;

namespace SpeedEmulator.Views;

public partial class FlowDetailsWindow : Window
{
    private readonly FlowDetailsViewModel viewModel;
    private readonly IBankUserColumnSettingsRepository columnSettingsRepository;
    private FlowStatisticsWindow? statisticsWindow;

    public FlowDetailsWindow(
        FlowDetailsViewModel viewModel,
        IBankUserColumnSettingsRepository columnSettingsRepository)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.columnSettingsRepository = columnSettingsRepository;
        DataContext = viewModel;
        WindowState = WindowState.Maximized;
        viewModel.RequestClose += ViewModel_RequestClose;
        viewModel.RequestOpenColumnSettings += ViewModel_RequestOpenColumnSettings;
        viewModel.RequestScrollToRecord += ViewModel_RequestScrollToRecord;
        viewModel.RequestOpenStatistics += ViewModel_RequestOpenStatistics;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowState = WindowState.Maximized;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ApplyColumnSettingsAsync();
        BuildDynamicColumns(viewModel.Bank.FlowColumns);
        await viewModel.LoadAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.RequestClose -= ViewModel_RequestClose;
        viewModel.RequestOpenColumnSettings -= ViewModel_RequestOpenColumnSettings;
        viewModel.RequestScrollToRecord -= ViewModel_RequestScrollToRecord;
        viewModel.RequestOpenStatistics -= ViewModel_RequestOpenStatistics;
        statisticsWindow?.Close();
        statisticsWindow = null;
        base.OnClosed(e);
    }

    private void ViewModel_RequestClose(object? sender, EventArgs e)
    {
        Close();
    }

    private void ViewModel_RequestScrollToRecord(FlowRecord record)
    {
        FlowGrid.ScrollIntoView(record);
        FlowGrid.SelectedItem = record;
        FlowGrid.UpdateLayout();
    }

    private void ViewModel_RequestOpenStatistics(object? sender, EventArgs e)
    {
        if (statisticsWindow is not null)
        {
            statisticsWindow.Close();
            statisticsWindow = null;
        }

        statisticsWindow = new FlowStatisticsWindow(new FlowStatisticsViewModel(viewModel.Records))
        {
            Owner = this
        };
        statisticsWindow.Closed += (_, _) => statisticsWindow = null;

        statisticsWindow.Show();
    }

    private async void ViewModel_RequestOpenColumnSettings(object? sender, EventArgs e)
    {
        var savedSettings = await columnSettingsRepository.LoadAsync(viewModel.Bank.Id, ColumnSettingScopes.FlowDetails);
        var columns = GetFlowDetailsSettingColumns().ToList();
        var settingsViewModel = new BankUserColumnSettingsViewModel(
            viewModel.Bank,
            columnSettingsRepository,
            ColumnSettingScopes.FlowDetails,
            columns,
            savedSettings,
            $"{viewModel.Bank.Name}流水明细字段设置");

        var window = new BankUserColumnSettingsWindow(settingsViewModel)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            await ApplyColumnSettingsAsync();
            BuildDynamicColumns(viewModel.Bank.FlowColumns);
            viewModel.NotifyColumnSettingsSaved();
        }
    }

    private async Task ApplyColumnSettingsAsync()
    {
        var settings = await columnSettingsRepository.LoadAsync(viewModel.Bank.Id, ColumnSettingScopes.FlowDetails);
        ColumnSettingsApplier.Apply(viewModel.Bank.FlowColumns, settings);
    }

    private void BuildDynamicColumns(IEnumerable<ColumnDefinition> columns)
    {
        FlowGrid.Columns.Clear();

        foreach (var item in columns
                     .Select((column, index) => new { Column = column, Index = index })
                     .Where(item => item.Column.Show && !string.IsNullOrWhiteSpace(item.Column.Field))
                     .OrderBy(item => IsIdColumn(item.Column) ? int.MinValue : item.Column.Order)
                     .ThenBy(item => item.Index))
        {
            var column = item.Column;
            if (IsIdColumn(column))
            {
                FlowGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = column.Name,
                    HeaderStyle = CreateCenteredHeaderStyle(),
                    Binding = new Binding(nameof(FlowRecord.Index)) { Mode = BindingMode.OneWay },
                    Width = new DataGridLength(Math.Max(column.Width, 38)),
                    MinWidth = 38,
                    ElementStyle = CreateCenteredTextStyle(),
                    IsReadOnly = true
                });
                continue;
            }

            if (string.Equals(column.Type, "Date", StringComparison.OrdinalIgnoreCase)
                || string.Equals(column.Type, "DateTime", StringComparison.OrdinalIgnoreCase))
            {
                FlowGrid.Columns.Add(CreateDateColumn(column));
                continue;
            }

            var binding = new Binding(CreateBindingPath(column.Field!))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
            };

            if (string.Equals(column.Type, "Money", StringComparison.OrdinalIgnoreCase))
            {
                binding.StringFormat = "0.00";
            }

            var textColumn = new DataGridTextColumn
            {
                Header = column.Name,
                HeaderStyle = CreateLeftAlignedHeaderStyle(),
                Binding = binding,
                ElementStyle = CreateLeftAlignedTextStyle(),
                Width = new DataGridLength(Math.Max(column.Width, 60)),
                MinWidth = 50
            };

            if (string.Equals(column.Field, nameof(FlowRecord.TradeMoney), StringComparison.OrdinalIgnoreCase))
            {
                textColumn.ElementStyle = CreateTradeMoneyTextStyle(column.Field!);
            }

            FlowGrid.Columns.Add(textColumn);
        }
    }

    private IEnumerable<ColumnDefinition> GetFlowDetailsSettingColumns()
    {
        return viewModel.Bank.FlowColumns
            .Where(column => column.Show || column.Order < 900);
    }

    private static bool IsIdColumn(ColumnDefinition column)
    {
        return string.Equals(column.Name, "ID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.Field, nameof(FlowRecord.Index), StringComparison.OrdinalIgnoreCase);
    }

    private void FlowGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
            FlowGrid.SelectedItem = rowItem;
            FlowGrid.CurrentCell = new DataGridCellInfo(rowItem, cell.Column);
        }

        if (FindVisualParent<FormattedDatePicker>(source) is not null || cell.IsEditing)
        {
            return;
        }

        FlowGrid.BeginEdit(e);
    }

    private static DataGridTemplateColumn CreateDateColumn(ColumnDefinition column)
    {
        static FrameworkElementFactory CreateTextFactory(string field)
        {
            var binding = new Binding(CreateBindingPath(field))
            {
                Mode = BindingMode.OneWay,
                StringFormat = "yyyy-MM-dd HH:mm:ss"
            };

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, binding);
            text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Left);
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetValue(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0));
            return text;
        }

        static FrameworkElementFactory CreatePickerFactory(string field)
        {
            var binding = new Binding(CreateBindingPath(field))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };

            var picker = new FrameworkElementFactory(typeof(FormattedDatePicker));
            picker.SetBinding(FormattedDatePicker.SelectedDateProperty, binding);
            picker.SetValue(FormattedDatePicker.DisplayFormatProperty, "yyyy-MM-dd HH:mm:ss");
            picker.SetValue(FrameworkElement.HeightProperty, 28d);
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
            HeaderStyle = CreateLeftAlignedHeaderStyle(),
            CellTemplate = new DataTemplate { VisualTree = CreateTextFactory(column.Field!) },
            CellEditingTemplate = new DataTemplate { VisualTree = CreatePickerFactory(column.Field!) },
            Width = new DataGridLength(Math.Max(column.Width, 150)),
            MinWidth = 140,
            IsReadOnly = false
        };
    }

    private static Style CreateTradeMoneyTextStyle(string field)
    {
        var style = CreateLeftAlignedTextStyle();
        style.Setters.Add(new Setter(
            TextBlock.ForegroundProperty,
            new Binding(CreateBindingPath(field)) { Converter = new TradeMoneyForegroundConverter() }));
        return style;
    }

    private static string CreateBindingPath(string field)
    {
        if (field.StartsWith('[') && field.EndsWith(']'))
        {
            return field;
        }

        return typeof(FlowRecord).GetProperty(field) is null ? $"[{field}]" : field;
    }

    private static Style CreateLeftAlignedTextStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Left));
        style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        return style;
    }

    private static Style CreateCenteredTextStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        return style;
    }

    private static Style CreateLeftAlignedHeaderStyle()
    {
        var style = CreateBaseHeaderStyle();
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 0, 0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        return style;
    }

    private static Style CreateCenteredHeaderStyle()
    {
        var style = CreateBaseHeaderStyle();
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        return style;
    }

    private static Style CreateBaseHeaderStyle()
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.HeightProperty, 32d));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(237, 237, 237))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        return style;
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

    private sealed class TradeMoneyForegroundConverter : IValueConverter
    {
        private static readonly Brush IncomeBrush = Brushes.Red;
        private static readonly Brush OutBrush = Brushes.Green;
        private static readonly Brush NormalBrush = Brushes.Black;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is null)
            {
                return NormalBrush;
            }

            if (double.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                if (number > 0)
                {
                    return IncomeBrush;
                }

                if (number < 0)
                {
                    return OutBrush;
                }
            }

            return NormalBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
