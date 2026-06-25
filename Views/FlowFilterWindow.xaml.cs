using System.Windows;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class FlowFilterWindow : Window
{
    public FlowFilterWindow(FlowFilterViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
