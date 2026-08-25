using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SpeedEmulator.Models;
using SpeedEmulator.Services;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class PdfImportPreviewWindow : Window
{
    private readonly PdfImportPreviewViewModel viewModel;

    public PdfImportPreviewWindow(PdfImportPreviewViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += ViewModel_RequestClose;
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.RequestClose -= ViewModel_RequestClose;
        base.OnClosed(e);
    }

    private void ViewModel_RequestClose(object? sender, DialogCloseRequestedEventArgs e)
    {
        DialogResult = e.DialogResult;
        Close();
    }

    private void IncomeDirectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FlowRecord record, Tag: string direction })
        {
            viewModel.ResolveIncomeDirection(record, direction);
        }
    }
}

public sealed class WechatDirectionPendingVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isPending = value is IReadOnlyDictionary<string, string> fields
            && fields.ContainsKey(WechatPdfDirectionRuleCatalog.UnresolvedDirectionField);
        var showPending = !string.Equals(System.Convert.ToString(parameter, culture), "Resolved", StringComparison.Ordinal);
        return isPending == showPending ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class WechatDirectionStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyDictionary<string, string> fields)
        {
            return false;
        }

        var state = System.Convert.ToString(parameter, culture);
        return state switch
        {
            "Pending" => fields.ContainsKey(WechatPdfDirectionRuleCatalog.UnresolvedDirectionField),
            "Resolved" => fields.ContainsKey(WechatPdfDirectionRuleCatalog.ManuallyResolvedDirectionField),
            _ => false
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
