using System.Text.RegularExpressions;
using System.Windows.Input;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

namespace SpeedEmulator.ViewModels;

public sealed class LoginViewModel : ObservableObject
{
    private const string AccountExpiredMessage = "账号已过期，请联系系统管理员";
    private readonly Func<string> getPassword;
    private readonly Action<FrontSession> onLoginSucceeded;
    private readonly Action<string> showAuthenticationMessage;
    private readonly FrontSession session;
    private readonly IFrontApiClient frontApiClient;
    private readonly IMachineIdService machineIdService;
    private readonly INetworkLocationService networkLocationService;
    private readonly ILoginCredentialStore credentialStore;
    private readonly IFrontTokenStore tokenStore;
    private string email = string.Empty;
    private bool remindMe;
    private bool logining;
    private string statusMessage = "输入邮箱/手机号和密码后登录";

    public LoginViewModel(
        Func<string> getPassword,
        Action<FrontSession> onLoginSucceeded,
        FrontSession session,
        IFrontApiClient frontApiClient,
        IMachineIdService machineIdService,
        INetworkLocationService networkLocationService,
        ILoginCredentialStore credentialStore,
        IFrontTokenStore tokenStore,
        Action<string> showAuthenticationMessage)
    {
        this.getPassword = getPassword;
        this.onLoginSucceeded = onLoginSucceeded;
        this.session = session;
        this.frontApiClient = frontApiClient;
        this.machineIdService = machineIdService;
        this.networkLocationService = networkLocationService;
        this.credentialStore = credentialStore;
        this.tokenStore = tokenStore;
        this.showAuthenticationMessage = showAuthenticationMessage;

        var credentials = credentialStore.Load();
        if (credentials is not null)
        {
            email = credentials.Account;
            InitialPassword = credentials.Password;
            remindMe = true;
        }

        LoginCommand = new AsyncRelayCommand(LoginAsync, CanLogin);
    }

    public string Email
    {
        get => email;
        set
        {
            if (SetProperty(ref email, value))
            {
                RaiseCommandState();
            }
        }
    }

    public bool RemindMe
    {
        get => remindMe;
        set => SetProperty(ref remindMe, value);
    }

    public bool Logining
    {
        get => logining;
        private set
        {
            if (SetProperty(ref logining, value))
            {
                RaiseCommandState();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string InitialPassword { get; } = string.Empty;

    public ICommand LoginCommand { get; }

    private bool CanLogin()
    {
        return !Logining && !string.IsNullOrWhiteSpace(Email);
    }

    private async Task LoginAsync()
    {
        var account = Email.Trim();
        var password = getPassword();

        if (!IsEmailOrPhone(account))
        {
            StatusMessage = "账号需为邮箱或手机号。";
            return;
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
        {
            StatusMessage = "密码长度不能少于 6 位。";
            return;
        }

        Logining = true;
        StatusMessage = "正在连接后台授权服务...";

        try
        {
            var machineCode = machineIdService.GetMachineCode();
            var location = await networkLocationService.GetCurrentAsync();
            var session = await frontApiClient.LoginAsync(
                account,
                password,
                machineCode,
                location.NetworkIp,
                location.LoginRegion);
            tokenStore.Save(session);
            var name = string.IsNullOrWhiteSpace(session.DisplayName) ? session.Account : session.DisplayName;
            StatusMessage = $"{name} 登录成功，正在进入首页...";
            if (RemindMe)
            {
                credentialStore.Save(account, password);
            }
            else
            {
                credentialStore.Clear();
            }

            onLoginSucceeded(session);
        }
        catch (FrontApiException ex)
        {
            StatusMessage = ex.Message;
            if (string.Equals(ex.Message, AccountExpiredMessage, StringComparison.Ordinal))
            {
                tokenStore.Clear();
                session.Clear();
                showAuthenticationMessage(ex.Message);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"登录失败：{ex.Message}";
        }
        finally
        {
            Logining = false;
        }
    }

    private static bool IsEmailOrPhone(string value)
    {
        if (Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            return true;
        }

        return Regex.IsMatch(value, @"^1[3-9]\d{9}$");
    }

    private void RaiseCommandState()
    {
        if (LoginCommand is AsyncRelayCommand command)
        {
            command.RaiseCanExecuteChanged();
        }
    }
}
