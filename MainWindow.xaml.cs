using System.Windows;
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
    private readonly IFrontApiClient frontApiClient;

    public MainWindow(FrontSession session, IFrontApiClient frontApiClient)
    {
        InitializeComponent();
        this.frontApiClient = frontApiClient;
        DataContext = new MainViewModel(session, OpenBankUsersWindow, frontApiClient);
    }

    private void OpenBankUsersWindow(Bank bank)
    {
        var viewModel = new BankUsersViewModel(
            bank,
            bankUserRepository,
            bankUserColumnSettingsRepository,
            frontApiClient,
            new ImageFilePickerService());
        var window = new BankUsersWindow(viewModel, bankUserRepository, bankUserColumnSettingsRepository, bankInterestSettingsRepository, flowGenerationRepository, flowRecordRepository)
        {
            Owner = this
        };

        window.ShowDialog();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (frontApiClient is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
