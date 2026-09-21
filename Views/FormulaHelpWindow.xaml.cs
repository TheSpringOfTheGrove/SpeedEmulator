using System.Windows;

namespace SpeedEmulator.Views;

public partial class FormulaHelpWindow : Window
{
    public FormulaHelpWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
