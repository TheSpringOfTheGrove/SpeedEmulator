using System.Windows;
using System.Windows.Input;
using SpeedEmulator.Models;
using SpeedEmulator.Services;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class LoginWindow : Window
{
    private readonly FrontSession session;
    private readonly IFrontApiClient frontApiClient;
    private readonly IFrontTokenStore tokenStore;
    private readonly LoginViewModel viewModel;
    private bool handedOffClient;

    public LoginWindow()
    {
        InitializeComponent();
        session = new FrontSession();
        tokenStore = new FrontTokenStore();
        frontApiClient = new FrontApiClient(session);
        viewModel = new LoginViewModel(
            () => PasswordInput.Password,
            OpenMainWindow,
            session,
            frontApiClient,
            new MachineIdService(),
            new NetworkLocationService(),
            new LoginCredentialStore(),
            tokenStore,
            ShowAuthenticationMessage);
        DataContext = viewModel;
        PasswordInput.Password = viewModel.InitialPassword;
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && viewModel.LoginCommand.CanExecute(null))
        {
            viewModel.LoginCommand.Execute(null);
        }
    }

    private void OpenMainWindow(FrontSession authenticatedSession)
    {
        handedOffClient = true;
        var mainWindow = new MainWindow(authenticatedSession, frontApiClient);
        Application.Current.MainWindow = mainWindow;
        mainWindow.Show();
        Close();
    }

    private void ShowAuthenticationMessage(string message)
    {
        MessageBox.Show(this, message, "登录提示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!handedOffClient && frontApiClient is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
