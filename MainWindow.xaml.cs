using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;
using SpeedEmulator.Views;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator;

public partial class MainWindow : Window
{
    private readonly IBankUserRepository bankUserRepository = new JsonBankUserRepository();
    private readonly IBankUserColumnSettingsRepository bankUserColumnSettingsRepository = new JsonBankUserColumnSettingsRepository();
    private readonly IBankInterestSettingsRepository bankInterestSettingsRepository = new JsonBankInterestSettingsRepository();
    private readonly IFlowGenerationRepository flowGenerationRepository = new InMemoryFlowGenerationRepository();
    private readonly IFlowRecordRepository flowRecordRepository = new InMemoryFlowRecordRepository();
    private readonly ITableExcelService tableExcelService = new TableExcelService();
    private readonly IPdfImportService pdfImportService = new PdfImportService();
    private readonly IPdfImportPreviewDialogService pdfImportPreviewDialogService = new PdfImportPreviewDialogService();
    private readonly IFrontApiClient frontApiClient;
    private readonly DispatcherTimer onlineTimer;
    private bool heartbeatRequestRunning;

    public MainWindow(FrontSession session, IFrontApiClient frontApiClient)
    {
        InitializeComponent();
        this.frontApiClient = frontApiClient;
        onlineTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        onlineTimer.Tick += OnlineTimer_Tick;
        Loaded += MainWindow_Loaded;
        DataContext = new MainViewModel(session, OpenBankUsersWindow, frontApiClient);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        onlineTimer.Start();
    }

    private async void OnlineTimer_Tick(object? sender, EventArgs e)
    {
        if (heartbeatRequestRunning)
        {
            return;
        }

        heartbeatRequestRunning = true;
        try
        {
            await frontApiClient.SendOnlineHeartbeatAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Online heartbeat failed: {ex.Message}");
        }
        finally
        {
            heartbeatRequestRunning = false;
        }
    }

    private void OpenBankUsersWindow(Bank bank)
    {
        var viewModel = new BankUsersViewModel(
            bank,
            bankUserRepository,
            bankUserColumnSettingsRepository,
            frontApiClient,
            new ImageFilePickerService(),
            tableExcelService,
            flowRecordRepository,
            pdfImportService,
            pdfImportPreviewDialogService);
        var window = new BankUsersWindow(viewModel, bankUserRepository, bankUserColumnSettingsRepository, bankInterestSettingsRepository, flowGenerationRepository, flowRecordRepository, tableExcelService, pdfImportService, pdfImportPreviewDialogService)
        {
            Owner = this
        };

        WindowNavigation.ShowAsCurrent(this, window);
    }

    protected override void OnClosed(EventArgs e)
    {
        onlineTimer.Stop();
        onlineTimer.Tick -= OnlineTimer_Tick;
        if (frontApiClient is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
