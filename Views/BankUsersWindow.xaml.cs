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
using SpeedEmulator.Services;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class BankUsersWindow : Window
{
    private static readonly IValueConverter ExtraDateFieldConverter = new ExtraDateStringConverter();

    private readonly BankUsersViewModel viewModel;
    private readonly IBankUserRepository bankUserRepository;
    private readonly IBankUserColumnSettingsRepository columnSettingsRepository;
    private readonly IBankInterestSettingsRepository interestSettingsRepository;
    private readonly IFlowGenerationRepository flowGenerationRepository;
    private readonly IFlowRecordRepository flowRecordRepository;
    private readonly ITableExcelService tableExcelService;
    private readonly IPrintTemplateRepository printTemplateRepository = new JsonPrintTemplateRepository();
    private readonly IPrintPdfService printPdfService = new ZhenchengPrintBridgeService();

    public BankUsersWindow(
        BankUsersViewModel viewModel,
        IBankUserRepository bankUserRepository,
        IBankUserColumnSettingsRepository columnSettingsRepository,
        IBankInterestSettingsRepository interestSettingsRepository,
        IFlowGenerationRepository flowGenerationRepository,
        IFlowRecordRepository flowRecordRepository,
        ITableExcelService tableExcelService)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.bankUserRepository = bankUserRepository;
        this.columnSettingsRepository = columnSettingsRepository;
        this.interestSettingsRepository = interestSettingsRepository;
        this.flowGenerationRepository = flowGenerationRepository;
        this.flowRecordRepository = flowRecordRepository;
        this.tableExcelService = tableExcelService;
        DataContext = viewModel;
        viewModel.RequestClose += ViewModel_RequestClose;
        viewModel.RequestOpenAutoGenerateFlow += ViewModel_RequestOpenAutoGenerateFlow;
        viewModel.RequestOpenFlowDetails += ViewModel_RequestOpenFlowDetails;
        viewModel.RequestOpenPrintPreview += ViewModel_RequestOpenPrintPreview;
        viewModel.RequestOpenColumnSettings += ViewModel_RequestOpenColumnSettings;
        viewModel.RequestOpenInterestSettings += ViewModel_RequestOpenInterestSettings;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await viewModel.ApplyColumnSettingsAsync();
        BuildDynamicColumns(viewModel.Bank);
        BuildEditorFields(viewModel.Bank);
        await viewModel.LoadAsync();
        UsersGrid.SelectedIndex = -1;
        UsersGrid.CurrentCell = default;
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.RequestClose -= ViewModel_RequestClose;
        viewModel.RequestOpenAutoGenerateFlow -= ViewModel_RequestOpenAutoGenerateFlow;
        viewModel.RequestOpenFlowDetails -= ViewModel_RequestOpenFlowDetails;
        viewModel.RequestOpenPrintPreview -= ViewModel_RequestOpenPrintPreview;
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

            var window = new FlowGenerationWindow(autoGenerateViewModel, columnSettingsRepository, interestSettingsRepository, flowRecordRepository, bankUserRepository, tableExcelService)
            {
                Owner = this
            };

            WindowNavigation.ShowAsCurrent(this, window);
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

        var flowDetailsViewModel = new FlowDetailsViewModel(viewModel.Bank, targetUser, flowRecordRepository, tableExcelService, bankUserRepository);
        var window = new FlowDetailsWindow(flowDetailsViewModel, columnSettingsRepository)
        {
            Owner = this
        };

        WindowNavigation.ShowAsCurrent(this, window);
    }

    private async void ViewModel_RequestOpenPrintPreview(object? sender, EventArgs e)
    {
        if (viewModel.SelectedUser is not { } targetUser)
        {
            return;
        }

        try
        {
            var records = await flowRecordRepository.ListByUserAsync(viewModel.Bank.Id, targetUser.Id);
            var printViewModel = new PrintPreviewViewModel(
                viewModel.Bank,
                targetUser,
                records.Select(item => item.Clone()).ToList(),
                printTemplateRepository,
                printPdfService);
            var window = new PrintPreviewWindow(printViewModel)
            {
                Owner = this
            };

            WindowNavigation.ShowAsCurrent(this, window);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开打印页面失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            BuildEditorFields(viewModel.Bank);
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

    private void BuildEditorFields(Bank bank)
    {
        EditorFieldsPanel.Children.Clear();

        foreach (var item in bank.Columns
                     .Select((column, index) => new { Column = column, Index = index })
                     .Where(item => !IsIdColumn(item.Column) && !string.IsNullOrWhiteSpace(item.Column.Field))
                     .OrderBy(item => item.Column.Order)
                     .ThenBy(item => item.Index))
        {
            EditorFieldsPanel.Children.Add(CreateEditorRow(item.Column));
        }
    }

    private Grid CreateEditorRow(SpeedEmulator.Models.ColumnDefinition column)
    {
        var columnName = column.Name ?? string.Empty;
        var labelWidth = columnName.Length >= 5 ? 92d : 66d;
        var row = new Grid
        {
            Style = TryFindResource("FormRowStyle") as Style
        };
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(labelWidth) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = columnName,
            Width = labelWidth,
            Style = TryFindResource("FieldLabelStyle") as Style
        };
        row.Children.Add(label);

        var editor = CreateEditorControl(column);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);

        return row;
    }

    private FrameworkElement CreateEditorControl(SpeedEmulator.Models.ColumnDefinition column)
    {
        var bindingPath = CreateEditorBindingPath(column.Field!);
        if (string.Equals(column.Type, "Boolean", StringComparison.OrdinalIgnoreCase))
        {
            var checkBox = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            checkBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(bindingPath)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            return checkBox;
        }

        if (string.Equals(column.Type, "Date", StringComparison.OrdinalIgnoreCase))
        {
            var picker = new FormattedDatePicker
            {
                Style = TryFindResource("EditorDateStyle") as Style
            };
            var dateBinding = new Binding(bindingPath)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
            ApplyExtraDateFieldConverter(dateBinding, column.Field!);
            picker.SetBinding(FormattedDatePicker.SelectedDateProperty, dateBinding);
            return picker;
        }

        var textBox = new TextBox
        {
            Style = TryFindResource("EditorTextBoxStyle") as Style
        };
        var binding = new Binding(bindingPath)
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = string.Equals(column.Type, "Money", StringComparison.OrdinalIgnoreCase)
                ? UpdateSourceTrigger.LostFocus
                : UpdateSourceTrigger.PropertyChanged
        };
        if (string.Equals(column.Type, "Money", StringComparison.OrdinalIgnoreCase))
        {
            binding.StringFormat = "F2";
        }

        textBox.SetBinding(TextBox.TextProperty, binding);
        return textBox;
    }

    private void UsersGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = (DependencyObject)e.OriginalSource;
        if (FindVisualParent<FormattedDatePicker>(source) is not null)
        {
            return;
        }

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

        if (cell.IsEditing)
        {
            return;
        }

        UsersGrid.BeginEdit(e);
        SelectEditingTextBoxAsync(cell);
    }

    private void UsersGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (FindVisualChild<FormattedDatePicker>(e.EditingElement)?.IsDropDownOpen == true)
        {
            e.Cancel = true;
        }
    }

    private void UsersGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (FindVisualChild<FormattedDatePicker>(e.Row)?.IsDropDownOpen == true)
        {
            e.Cancel = true;
        }
    }

    private static DataGridTemplateColumn CreateDateColumn(SpeedEmulator.Models.ColumnDefinition column)
    {
        static FrameworkElementFactory CreateTextFactory(string field)
        {
            var binding = new Binding(CreateBindingPath(field))
            {
                Mode = BindingMode.OneWay,
                StringFormat = "yyyy-MM-dd HH:mm:ss"
            };
            ApplyExtraDateFieldConverter(binding, field);

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, binding);
            text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Left);
            text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetValue(TextBlock.PaddingProperty, new Thickness(8, 0, 0, 0));
            return text;
        }

        static FrameworkElementFactory CreatePickerFactory(string field)
        {
            var bindingPath = CreateBindingPath(field);
            var binding = new Binding(bindingPath)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
            ApplyExtraDateFieldConverter(binding, field);

            var picker = new FrameworkElementFactory(typeof(FormattedDatePicker));
            picker.SetBinding(FormattedDatePicker.SelectedDateProperty, binding);
            picker.SetValue(FormattedDatePicker.DisplayFormatProperty, "yyyy-MM-dd HH:mm:ss");
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
            CellTemplate = new DataTemplate { VisualTree = CreateTextFactory(column.Field!) },
            CellEditingTemplate = new DataTemplate { VisualTree = CreatePickerFactory(column.Field!) },
            Width = new DataGridLength(Math.Max(column.Width, 180)),
            MinWidth = 170,
            IsReadOnly = false
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

    private static T? FindVisualChild<T>(DependencyObject? parent)
        where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed)
            {
                return typed;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void SelectEditingTextBoxAsync(DataGridCell cell)
    {
        cell.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FindVisualChild<TextBox>(cell) is not { } textBox)
            {
                return;
            }

            textBox.Focus();
            textBox.SelectAll();
        }));
    }

    private static string CreateBindingPath(string field)
    {
        if (field.StartsWith('[') && field.EndsWith(']'))
        {
            return field;
        }

        return typeof(BankUser).GetProperty(field) is null ? $"[{field}]" : field;
    }

    private static void ApplyExtraDateFieldConverter(Binding binding, string field)
    {
        if (IsExtraFieldPath(field))
        {
            binding.Converter = ExtraDateFieldConverter;
        }
    }

    private static bool IsExtraFieldPath(string field)
    {
        return field.StartsWith('[') && field.EndsWith(']');
    }

    private static string CreateEditorBindingPath(string field)
    {
        var path = CreateBindingPath(field);
        return path.StartsWith('[') ? $"EditableUser{path}" : $"EditableUser.{path}";
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

    private sealed class ExtraDateStringConverter : IValueConverter
    {
        private static readonly string[] ParseFormats =
        [
            "yyyy年MM月dd日 HH:mm:ss",
            "yyyy年M月d日 H:mm:ss",
            "yyyy/M/d H:mm:ss",
            "yyyy/M/d",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd"
        ];

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime dateTime)
            {
                return dateTime;
            }

            if (value is string text && TryParseDateTime(text, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is DateTime dateTime
                ? dateTime.ToString("yyyy年MM月dd日 HH:mm:ss", CultureInfo.GetCultureInfo("zh-CN"))
                : string.Empty;
        }

        private static bool TryParseDateTime(string value, out DateTime parsed)
        {
            var culture = CultureInfo.GetCultureInfo("zh-CN");
            return DateTime.TryParseExact(value, ParseFormats, culture, DateTimeStyles.None, out parsed)
                || DateTime.TryParse(value, culture, DateTimeStyles.None, out parsed);
        }
    }
}
