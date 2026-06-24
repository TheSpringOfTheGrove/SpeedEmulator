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
    private long nextReferenceId = 1;
    private long nextConstId = 1;

    public InMemoryFlowGenerationRepository()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "flow-generation-config.json");

        LoadFromDisk();
    }

    public Task<FlowGenerationSnapshot> LoadAsync(long bankId, long? bankUserId)
    {
        lock (syncRoot)
        {
            var key = CreateKey(bankId, bankUserId);
            if (!snapshots.TryGetValue(key, out var snapshot))
            {
                snapshot = CreateSeed(bankId);
                snapshots[key] = Clone(snapshot);
                SaveToDisk();
            }
            else if (TryRefreshStaleSeed(bankId, snapshot, out var refreshedSnapshot))
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

    private FlowGenerationSnapshot CreateSeed(long bankId)
    {
        if (FlowGenerationSeedCatalog.TryCreateSeed(bankId, out var seededSnapshot))
        {
            return seededSnapshot;
        }

        var config = new FlowGenerationConfig
        {
            StartTime = new DateTime(2023, 4, 1),
            EndTime = new DateTime(2026, 1, 1),
            AllInMoney = 30000,
            LastMoney = 10000,
            MinInMoneyMonth1 = 3000,
            MaxInMoneyMonth1 = 3000,
            MinOutMoneyMonth1 = 2000,
            MaxOutMoneyMonth1 = 2000
        };

        var references = new[]
        {
            CreateReference(bankId, "收入", "48549466", "124556", null),
            CreateReference(bankId, "收入", "45656562", "65544", null),
            CreateReference(bankId, "收入", "9897978", "22233", null),
            CreateReference(bankId, "收入", "11363163", "4455", null),
            CreateReference(bankId, "收入", "9989895", "565665", null),
            CreateReference(bankId, "支出", "45121323", "8855", null)
        };

        var constItems = new[]
        {
            CreateConst(bankId, "收入", "5", "1", "固定工资入账", "660001", "SALARY-001"),
            CreateConst(bankId, "支出", "15", "1", "固定生活缴费", "660002", "BILL-015"),
            CreateConst(bankId, "支出", "25", "1", "固定转账支出", "660003", "TRANSFER-025")
        };

        return new FlowGenerationSnapshot
        {
            Config = config,
            References = references.ToList(),
            ConstItems = constItems.ToList()
        };
    }

    private static bool TryRefreshStaleSeed(long bankId, FlowGenerationSnapshot snapshot, out FlowGenerationSnapshot refreshedSnapshot)
    {
        refreshedSnapshot = new FlowGenerationSnapshot();

        if (!FlowGenerationSeedCatalog.TryCreateBankSeed(bankId, out var packagedSeed))
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

    private GenerateReferenceRule CreateReference(long bankId, string incomeAttribute, string serialNum, string merchantOrderNo, string? remark)
    {
        var rule = GenerateReferenceRule.CreateDefault(bankId);
        rule.Id = nextReferenceId++;
        rule.IsCheck = true;
        rule.IncomeAttribute = incomeAttribute;
        rule.MinMoney = 10;
        rule.MaxMoney = 1000;
        rule.SerialNum = serialNum;
        rule.MerchantName = merchantOrderNo;
        rule.Remark = remark;
        rule.TradeChannel = string.Empty;
        rule.OppositeUsername = string.Empty;
        rule.IncomeType = string.Empty;
        return rule;
    }

    private GenerateConstRule CreateConst(
        long bankId,
        string incomeAttribute,
        string fixDay,
        string reCnt,
        string remark,
        string serialNum,
        string merchantOrderNo)
    {
        var rule = GenerateConstRule.CreateDefault(bankId);
        rule.Id = nextConstId++;
        rule.IsCheck = true;
        rule.IncomeAttribute = incomeAttribute;
        rule.MinMoney = 10;
        rule.MaxMoney = 1000;
        rule.FixDay = fixDay;
        rule.ReCnt = reCnt;
        rule.SerialNum = serialNum;
        rule.MerchantName = merchantOrderNo;
        rule.Remark = remark;
        return rule;
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

            nextReferenceId = snapshots.Values
                .SelectMany(item => item.References)
                .Select(item => item.Id)
                .DefaultIfEmpty(0)
                .Max() + 1;

            nextConstId = snapshots.Values
                .SelectMany(item => item.ConstItems)
                .Select(item => item.Id)
                .DefaultIfEmpty(0)
                .Max() + 1;
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
        if (TryRefreshStaleSeed(bankId, first, out _) && !TryRefreshStaleSeed(bankId, second, out _))
        {
            return Clone(second);
        }

        if (!TryRefreshStaleSeed(bankId, first, out _) && TryRefreshStaleSeed(bankId, second, out _))
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
