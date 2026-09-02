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
    private const string AccountExpiredMessage = "账号已过期，请联系系统管理员";
    private readonly IBankUserRepository bankUserRepository = new JsonBankUserRepository();
    private readonly IBankUserColumnSettingsRepository bankUserColumnSettingsRepository = new JsonBankUserColumnSettingsRepository();
    private readonly IBankInterestSettingsRepository bankInterestSettingsRepository = new JsonBankInterestSettingsRepository();
    private readonly IFlowGenerationRepository flowGenerationRepository = new InMemoryFlowGenerationRepository();
    private readonly IFlowRecordRepository flowRecordRepository = new InMemoryFlowRecordRepository();
    private readonly ITableExcelService tableExcelService = new TableExcelService();
    private readonly IPdfImportService pdfImportService = new PdfImportService();
    private readonly IPdfImportPreviewDialogService pdfImportPreviewDialogService = new PdfImportPreviewDialogService();
    private readonly IFrontApiClient frontApiClient;
    private readonly FrontSession session;
    private readonly IFrontTokenStore tokenStore = new FrontTokenStore();
    private readonly DispatcherTimer onlineTimer;
    private readonly DispatcherTimer accountExpirationTimer;
    private DateTime? scheduledAccountExpiresAt;
    private bool heartbeatRequestRunning;
    private bool sessionInvalidated;

    public MainWindow(FrontSession session, IFrontApiClient frontApiClient)
    {
        InitializeComponent();
        this.session = session;
        this.frontApiClient = frontApiClient;
        onlineTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        onlineTimer.Tick += OnlineTimer_Tick;
        accountExpirationTimer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        accountExpirationTimer.Tick += AccountExpirationTimer_Tick;
        Loaded += MainWindow_Loaded;
        DataContext = new MainViewModel(session, OpenBankUsersWindow, frontApiClient);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        onlineTimer.Start();
        _ = RefreshOnlineStatusAsync();
    }

    private async void OnlineTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshOnlineStatusAsync();
    }

    private async Task RefreshOnlineStatusAsync()
    {
        if (heartbeatRequestRunning || sessionInvalidated)
        {
            return;
        }

        heartbeatRequestRunning = true;
        try
        {
            var accountExpiresAt = await frontApiClient.SendOnlineHeartbeatAsync();
            ScheduleAccountExpiration(accountExpiresAt);
        }
        catch (FrontApiException ex) when (ex.InvalidatesSession)
        {
            InvalidateSession(ex.Message);
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

    private void ScheduleAccountExpiration(DateTime? accountExpiresAt)
    {
        accountExpirationTimer.Stop();
        scheduledAccountExpiresAt = accountExpiresAt;
        if (!accountExpiresAt.HasValue || sessionInvalidated)
        {
            return;
        }

        var remaining = accountExpiresAt.Value - DateTime.Now;
        if (remaining <= TimeSpan.Zero)
        {
            InvalidateSession(AccountExpiredMessage);
            return;
        }

        accountExpirationTimer.Interval = remaining < TimeSpan.FromMinutes(1)
            ? remaining
            : TimeSpan.FromMinutes(1);
        accountExpirationTimer.Start();
    }

    private void AccountExpirationTimer_Tick(object? sender, EventArgs e)
    {
        accountExpirationTimer.Stop();
        ScheduleAccountExpiration(scheduledAccountExpiresAt);
    }

    private void InvalidateSession(string message)
    {
        if (sessionInvalidated)
        {
            return;
        }

        sessionInvalidated = true;
        onlineTimer.Stop();
        accountExpirationTimer.Stop();
        tokenStore.Clear();
        session.Clear();

        var loginWindow = new LoginWindow();
        Application.Current.MainWindow = loginWindow;
        loginWindow.Show();
        Close();

        MessageBox.Show(
            loginWindow,
            string.IsNullOrWhiteSpace(message) ? "登录状态已失效，请重新登录。" : message,
            "登录提示",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OpenBankUsersWindow(Bank bank)
    {
        if (string.Equals(bank.Type, BankTypes.Corporate, StringComparison.Ordinal)
            && !session.CanUseCorporateBank)
        {
            return;
        }

        var viewModel = new BankUsersViewModel(
            bank,
            bankUserRepository,
            bankUserColumnSettingsRepository,
            frontApiClient,
            new ImageFilePickerService(),
            tableExcelService,
            flowRecordRepository,
            pdfImportService,
            pdfImportPreviewDialogService,
            session.CanUploadPdf);
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
        accountExpirationTimer.Stop();
        accountExpirationTimer.Tick -= AccountExpirationTimer_Tick;
        if (frontApiClient is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
