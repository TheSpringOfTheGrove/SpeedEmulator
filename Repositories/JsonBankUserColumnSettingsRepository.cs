using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public sealed class JsonBankUserColumnSettingsRepository : IBankUserColumnSettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object syncRoot = new();
    private readonly string storagePath;
    private Dictionary<string, List<BankUserColumnSetting>> settingsByBank = [];
    private bool loaded;

    public JsonBankUserColumnSettingsRepository()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "bank-user-column-settings.json");
    }

    public Task<IReadOnlyList<BankUserColumnSetting>> LoadAsync(long bankId)
    {
        return LoadAsync(bankId, ColumnSettingScopes.BankUsers);
    }

    public Task<IReadOnlyList<BankUserColumnSetting>> LoadAsync(long bankId, string scope)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            var key = CreateKey(bankId, scope);
            if (!settingsByBank.TryGetValue(key, out var settings))
            {
                return Task.FromResult<IReadOnlyList<BankUserColumnSetting>>([]);
            }

            return Task.FromResult<IReadOnlyList<BankUserColumnSetting>>(
                settings.Select(item => item.Clone()).ToList());
        }
    }

    public Task SaveAsync(long bankId, IEnumerable<BankUserColumnSetting> settings)
    {
        return SaveAsync(bankId, ColumnSettingScopes.BankUsers, settings);
    }

    public Task SaveAsync(long bankId, string scope, IEnumerable<BankUserColumnSetting> settings)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            settingsByBank[CreateKey(bankId, scope)] = settings
                .Where(item => !string.IsNullOrWhiteSpace(item.Field))
                .Select(item =>
                {
                    var copy = item.Clone();
                    copy.Normalize();
                    return copy;
                })
                .ToList();

            Persist();
            return Task.CompletedTask;
        }
    }

    private static string CreateKey(long bankId, string scope)
    {
        if (string.Equals(scope, ColumnSettingScopes.BankUsers, StringComparison.Ordinal))
        {
            return bankId.ToString();
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            return bankId.ToString();
        }

        return $"{bankId}:{scope}";
    }

    private static string CreateKey(long bankId)
    {
        return bankId.ToString();
    }

    private void EnsureLoaded()
    {
        if (loaded)
        {
            return;
        }

        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(storagePath))
        {
            try
            {
                var json = File.ReadAllText(storagePath);
                settingsByBank = JsonSerializer.Deserialize<Dictionary<string, List<BankUserColumnSetting>>>(json, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                settingsByBank = [];
            }
        }

        loaded = true;
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(storagePath, JsonSerializer.Serialize(settingsByBank, JsonOptions));
    }
}
