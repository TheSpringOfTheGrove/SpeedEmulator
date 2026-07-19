using System.Windows;
using SpeedEmulator.Models;
using SpeedEmulator.ViewModels;
using SpeedEmulator.Views;

namespace SpeedEmulator.Services;

public interface IPdfImportPreviewDialogService
{
    bool Confirm(PdfImportResult result);
}

public sealed class PdfImportPreviewDialogService : IPdfImportPreviewDialogService
{
    public bool Confirm(PdfImportResult result)
    {
        var viewModel = new PdfImportPreviewViewModel(result);
        var window = new PdfImportPreviewWindow(viewModel)
        {
            Owner = GetActiveWindow()
        };

        return window.ShowDialog() == true;
    }

    private static Window? GetActiveWindow()
    {
        return Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive);
    }
}
