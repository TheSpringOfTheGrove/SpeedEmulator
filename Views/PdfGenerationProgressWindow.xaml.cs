using System.Windows;

namespace SpeedEmulator.Views;

public partial class PdfGenerationProgressWindow : Window
{
    public PdfGenerationProgressWindow()
    {
        InitializeComponent();
    }

    public void CloseAfterComplete()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(CloseAfterComplete);
            return;
        }

        if (IsLoaded)
        {
            Close();
        }
    }
}
