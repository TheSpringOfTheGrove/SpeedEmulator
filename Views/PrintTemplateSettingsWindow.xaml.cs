using System.Windows;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class PrintTemplateSettingsWindow : Window
{
    public PrintTemplateSettingsWindow(PrintTemplateSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
