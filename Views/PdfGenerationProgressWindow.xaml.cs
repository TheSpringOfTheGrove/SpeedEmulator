using System.ComponentModel;
using System.Windows;

namespace SpeedEmulator.Views;

public partial class PdfGenerationProgressWindow : Window
{
    private bool allowClose;

    public PdfGenerationProgressWindow()
    {
        InitializeComponent();
    }

    public void CloseAfterComplete()
    {
        allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
