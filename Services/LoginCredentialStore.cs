using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Services;

public sealed record LoginCredentials(string Account, string Password);

public interface ILoginCredentialStore
{
    LoginCredentials? Load();

    void Save(string account, string password);

    void Clear();
}

public sealed class LoginCredentialStore : ILoginCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string storagePath;

    public LoginCredentialStore()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "login-credentials.json");
    }

    public LoginCredentials? Load()
    {
        try
        {
            if (!File.Exists(storagePath))
            {
                return null;
            }

            var credentials = JsonSerializer.Deserialize<LoginCredentials>(File.ReadAllText(storagePath));
            if (credentials is null ||
                string.IsNullOrWhiteSpace(credentials.Account) ||
                string.IsNullOrWhiteSpace(credentials.Password))
            {
                return null;
            }

            return credentials;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string account, string password)
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var credentials = new LoginCredentials(account.Trim(), password);
        File.WriteAllText(storagePath, JsonSerializer.Serialize(credentials, JsonOptions));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(storagePath))
            {
                File.Delete(storagePath);
            }
        }
        catch
        {
            // Login should not fail because the optional local credential cache cannot be cleared.
        }
    }
}
