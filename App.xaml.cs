using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace SpeedEmulator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"程序发生异常：{e.Exception.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            MessageBox.Show($"程序发生异常：{exception.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

