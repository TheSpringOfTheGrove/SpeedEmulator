using System.Windows;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class MonthDetailSettingsWindow : Window
{
    public MonthDetailSettingsWindow(FlowGenerationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
