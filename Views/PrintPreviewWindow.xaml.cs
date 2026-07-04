using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class PrintPreviewWindow : Window
{
    private readonly PrintPreviewViewModel viewModel;
    private bool previewInitialized;
    private Task? previewInitializationTask;

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
        await viewModel.LoadAsync();
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

    private Task EnsurePreviewInitializedAsync()
    {
        if (previewInitialized)
        {
            return Task.CompletedTask;
        }

        previewInitializationTask ??= InitializePreviewAsync();
        return previewInitializationTask;
    }

    private async Task InitializePreviewAsync()
    {
        try
        {
            await PreviewWebView.EnsureCoreWebView2Async();
            previewInitialized = PreviewWebView.CoreWebView2 is not null;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            PreviewFallback.Text = "未检测到 WebView2 Runtime，可使用“打开PDF”查看预览。";
            PreviewFallback.Visibility = Visibility.Visible;
            previewInitializationTask = null;
        }
        catch (Exception ex)
        {
            PreviewFallback.Text = $"PDF 预览初始化失败：{ex.Message}";
            PreviewFallback.Visibility = Visibility.Visible;
            previewInitializationTask = null;
        }
    }

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PrintPreviewViewModel.PreviewPath))
        {
            await NavigateToPreviewPathAsync();
        }
    }

    private async Task NavigateToPreviewPathAsync()
    {
        var path = viewModel.PreviewPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            PreviewFallback.Text = "正在加载 PDF 预览...";
            PreviewFallback.Visibility = Visibility.Visible;

            await EnsurePreviewInitializedAsync();

            if (PreviewWebView.CoreWebView2 is null)
            {
                PreviewFallback.Text = path;
                PreviewFallback.Visibility = Visibility.Visible;
                return;
            }

            PreviewWebView.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
            PreviewFallback.Visibility = Visibility.Collapsed;
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

    private async void TemplateGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row)
        {
            return;
        }

        if (sender is DataGrid grid)
        {
            grid.SelectedItem = row.Item;
            if (grid.Columns.Count > 0)
            {
                grid.CurrentCell = new DataGridCellInfo(row.Item, grid.Columns[0]);
            }
        }

        row.IsSelected = true;
        row.Focus();

        if (viewModel.GeneratePreviewCommand.CanExecute(null))
        {
            await GeneratePreviewWithProgressAsync();
        }
    }

    private async Task GeneratePreviewWithProgressAsync()
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        var progressWindow = new PdfGenerationProgressWindow
        {
            Owner = this
        };
        var previousCursor = Mouse.OverrideCursor;

        try
        {
            TemplateGrid.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;
            progressWindow.Show();

            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await viewModel.GeneratePreviewForSelectedTemplateAsync();
        }
        finally
        {
            Mouse.OverrideCursor = previousCursor;
            TemplateGrid.IsEnabled = true;
            if (progressWindow.IsVisible)
            {
                progressWindow.CloseAfterComplete();
            }
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
