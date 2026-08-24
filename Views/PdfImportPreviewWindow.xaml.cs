using System.Globalization;
using System.Windows;
using System.Windows.Data;
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
