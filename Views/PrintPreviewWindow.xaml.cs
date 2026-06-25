using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class PrintPreviewWindow : Window
{
    private readonly PrintPreviewViewModel viewModel;

    public PrintPreviewWindow(PrintPreviewViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RequestClose += ViewModel_RequestClose;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializePreviewAsync();
        await viewModel.LoadAsync();
        NavigateToPreviewPath();
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.RequestClose -= ViewModel_RequestClose;
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }

    private void ViewModel_RequestClose(object? sender, EventArgs e)
    {
        Close();
    }

    private async Task InitializePreviewAsync()
    {
        try
        {
            await PreviewWebView.EnsureCoreWebView2Async();
            if (PreviewWebView.CoreWebView2 is not null)
            {
                PreviewFallback.Visibility = Visibility.Collapsed;
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            PreviewFallback.Text = "未检测到 WebView2 Runtime，可使用“打开PDF”查看预览。";
            PreviewFallback.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            PreviewFallback.Text = $"PDF 预览初始化失败：{ex.Message}";
            PreviewFallback.Visibility = Visibility.Visible;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PrintPreviewViewModel.PreviewPath))
        {
            NavigateToPreviewPath();
        }
    }

    private void NavigateToPreviewPath()
    {
        var path = viewModel.PreviewPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            if (PreviewWebView.CoreWebView2 is null)
            {
                PreviewFallback.Text = path;
                PreviewFallback.Visibility = Visibility.Visible;
                return;
            }

            PreviewFallback.Visibility = Visibility.Collapsed;
            PreviewWebView.Source = new Uri(path);
        }
        catch (Exception ex)
        {
            PreviewFallback.Text = $"加载 PDF 失败：{ex.Message}";
            PreviewFallback.Visibility = Visibility.Visible;
        }
    }

    private void TemplateGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { } row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T target)
            {
                return target;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
