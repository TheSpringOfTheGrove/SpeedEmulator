using SpeedEmulator.Models;
using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Repositories;

public sealed class InMemoryFlowGenerationRepository : IFlowGenerationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object syncRoot = new();
    private readonly Dictionary<string, FlowGenerationSnapshot> snapshots = [];
    private readonly string storagePath;

    public InMemoryFlowGenerationRepository()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "flow-generation-config.json");

        LoadFromDisk();
    }

    public Task<FlowGenerationSnapshot> LoadAsync(Bank bank, long? bankUserId)
    {
        lock (syncRoot)
        {
            var bankId = bank.Id;
            var key = CreateKey(bankId, bankUserId);
            if (!snapshots.TryGetValue(key, out var snapshot))
            {
                snapshot = CreateSeed(bank);
                snapshots[key] = Clone(snapshot);
                SaveToDisk();
            }
            else if (TryRefreshStaleSeed(bank, snapshot, out var refreshedSnapshot))
            {
                snapshot = refreshedSnapshot;
                snapshots[key] = Clone(snapshot);
                SaveToDisk();
            }

            return Task.FromResult(Clone(snapshot));
        }
    }

    public Task SaveAsync(long bankId, long? bankUserId, FlowGenerationSnapshot snapshot)
    {
        lock (syncRoot)
        {
            snapshots[CreateKey(bankId, bankUserId)] = Clone(snapshot);
            SaveToDisk();
            return Task.CompletedTask;
        }
    }

    private static FlowGenerationSnapshot CreateSeed(Bank bank)
    {
        if (FlowGenerationSeedCatalog.TryCreateSeed(bank.Id, bank.Name, out var seededSnapshot))
        {
            return seededSnapshot;
        }

        throw new InvalidOperationException($"缺少 {bank.Name} 的内置参照明细/固定日期增加项目数据，请检查安装包 Data\\zhencheng-flow-generation-seed.json。");
    }

    private static bool TryRefreshStaleSeed(Bank bank, FlowGenerationSnapshot snapshot, out FlowGenerationSnapshot refreshedSnapshot)
    {
        refreshedSnapshot = new FlowGenerationSnapshot();

        if (!FlowGenerationSeedCatalog.TryCreateBankSeed(bank.Id, bank.Name, out var packagedSeed))
        {
            return false;
        }

        if (packagedSeed.References.Count == 0 && packagedSeed.ConstItems.Count == 0)
        {
            return false;
        }

        if (!IsEmptySnapshot(snapshot) && !IsLegacyDefaultSnapshot(snapshot))
        {
            return false;
        }

        refreshedSnapshot = new FlowGenerationSnapshot
        {
            Config = snapshot.Config.Clone(),
            References = packagedSeed.References.Select(item => item.Clone()).ToList(),
            ConstItems = packagedSeed.ConstItems.Select(item => item.Clone()).ToList()
        };

        return true;
    }

    private static bool IsEmptySnapshot(FlowGenerationSnapshot snapshot)
    {
        return snapshot.References.Count == 0 && snapshot.ConstItems.Count == 0;
    }

    private static bool IsLegacyDefaultSnapshot(FlowGenerationSnapshot snapshot)
    {
        if (snapshot.References.Count != 6 || snapshot.ConstItems.Count != 3)
        {
            return false;
        }

        var serials = snapshot.References
            .Select(item => item.SerialNum)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

        return serials.SequenceEqual([
            "48549466",
            "45656562",
            "9897978",
            "11363163",
            "9989895",
            "45121323"
        ]);
    }

    private static FlowGenerationSnapshot Clone(FlowGenerationSnapshot snapshot)
    {
        return new FlowGenerationSnapshot
        {
            Config = snapshot.Config.Clone(),
            References = snapshot.References.Select(item => item.Clone()).ToList(),
            ConstItems = snapshot.ConstItems.Select(item => item.Clone()).ToList()
        };
    }

    private static string CreateKey(long bankId, long? bankUserId)
    {
        return CreateBankKey(bankId);
    }

    private static string CreateBankKey(long bankId)
    {
        return $"{bankId}:bank";
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(storagePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(storagePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, FlowGenerationSnapshot>>(json, JsonOptions);
            if (data is null)
            {
                return;
            }

            var shouldSaveMigratedData = false;
            foreach (var item in data)
            {
                var bankId = GetBankIdFromKey(item.Key);
                if (bankId is null)
                {
                    shouldSaveMigratedData = true;
                    continue;
                }

                var bankKey = CreateBankKey(bankId.Value);
                if (!string.Equals(bankKey, item.Key, StringComparison.Ordinal))
                {
                    shouldSaveMigratedData = true;
                }

                if (snapshots.TryGetValue(bankKey, out var existing))
                {
                    snapshots[bankKey] = SelectPreferredSnapshot(bankId.Value, existing, item.Value);
                    shouldSaveMigratedData = true;
                    continue;
                }

                snapshots[bankKey] = Clone(item.Value);
            }

            if (shouldSaveMigratedData)
            {
                SaveToDisk();
            }

        }
        catch
        {
            snapshots.Clear();
        }
    }

    private static long? GetBankIdFromKey(string key)
    {
        var separator = key.IndexOf(':', StringComparison.Ordinal);
        var bankIdText = separator >= 0 ? key[..separator] : key;
        return long.TryParse(bankIdText, out var bankId) ? bankId : null;
    }

    private static FlowGenerationSnapshot SelectPreferredSnapshot(
        long bankId,
        FlowGenerationSnapshot first,
        FlowGenerationSnapshot second)
    {
        if (IsLegacyDefaultSnapshot(first) && !IsLegacyDefaultSnapshot(second))
        {
            return Clone(second);
        }

        if (!IsLegacyDefaultSnapshot(first) && IsLegacyDefaultSnapshot(second))
        {
            return Clone(first);
        }

        var firstCount = first.References.Count + first.ConstItems.Count;
        var secondCount = second.References.Count + second.ConstItems.Count;
        return secondCount > firstCount ? Clone(second) : Clone(first);
    }

    private void SaveToDisk()
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var data = snapshots.ToDictionary(item => item.Key, item => Clone(item.Value));
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(storagePath, json);
    }
}
