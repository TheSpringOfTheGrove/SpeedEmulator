using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public sealed class JsonBankInterestSettingsRepository : IBankInterestSettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object syncRoot = new();
    private readonly string storagePath;
    private Dictionary<string, BankInterestSetting> settingsByBank = [];
    private bool loaded;

    public JsonBankInterestSettingsRepository()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "bank-interest-settings.json");
    }

    public Task<BankInterestSetting?> LoadAsync(long bankId)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            return Task.FromResult(settingsByBank.TryGetValue(CreateKey(bankId), out var setting)
                ? setting.Clone()
                : null);
        }
    }

    public Task SaveAsync(long bankId, BankInterestSetting setting)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            settingsByBank[CreateKey(bankId)] = setting.Clone();
            Persist();
            return Task.CompletedTask;
        }
    }

    public Task DeleteAsync(long bankId)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            settingsByBank.Remove(CreateKey(bankId));
            Persist();
            return Task.CompletedTask;
        }
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
                settingsByBank = JsonSerializer.Deserialize<Dictionary<string, BankInterestSetting>>(json, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                settingsByBank = [];
            }
        }

        MergePackagedSeedSettings();
        loaded = true;
    }

    private void MergePackagedSeedSettings()
    {
        foreach (var (key, setting) in LoadPackagedSeedSettings())
        {
            settingsByBank.TryAdd(key, setting.Clone());
        }
    }

    private static Dictionary<string, BankInterestSetting> LoadPackagedSeedSettings()
    {
        foreach (var path in GetPackagedSeedPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, BankInterestSetting>>(json, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return [];
    }

    private static IEnumerable<string> GetPackagedSeedPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Data", "bank-interest-settings-seed.json");
        yield return Path.Combine(AppContext.BaseDirectory, "bank-interest-settings-seed.json");
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
