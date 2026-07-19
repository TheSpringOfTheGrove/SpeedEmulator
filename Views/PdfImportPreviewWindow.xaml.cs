using System.Windows;
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
