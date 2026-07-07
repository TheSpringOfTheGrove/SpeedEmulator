using SpeedEmulator.Models;
using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Repositories;

public sealed class InMemoryFlowRecordRepository : IFlowRecordRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly object syncRoot = new();
    private readonly Dictionary<string, List<FlowRecord>> recordsByUser = [];
    private readonly string storagePath;
    private long nextId = 1;
    private bool loaded;

    public InMemoryFlowRecordRepository()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "flow-records.json");
    }

    public Task<IReadOnlyList<FlowRecord>> ListByUserAsync(long bankId, long bankUserId)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            var key = CreateKey(bankId, bankUserId);
            if (!recordsByUser.TryGetValue(key, out var records))
            {
                records = CreateSeed(bankId, bankUserId);
                recordsByUser[key] = records;
                Persist();
            }

            return Task.FromResult<IReadOnlyList<FlowRecord>>(records.Select(item => item.Clone()).ToList());
        }
    }

    public Task SaveAllAsync(long bankId, long bankUserId, IEnumerable<FlowRecord> records)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            var normalized = records.Select(item => item.Clone()).ToList();
            foreach (var item in normalized)
            {
                if (item.Id <= 0)
                {
                    item.Id = nextId++;
                }

                item.BankId = bankId;
                item.BankUserId = bankUserId;
                RemoveInternalFields(item);
            }

            recordsByUser[CreateKey(bankId, bankUserId)] = normalized;
            Persist();
            return Task.CompletedTask;
        }
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
                var data = JsonSerializer.Deserialize<Dictionary<string, List<FlowRecord>>>(json, JsonOptions);
                if (data is not null)
                {
                    foreach (var item in data)
                    {
                        recordsByUser[item.Key] = item.Value.Select(record => record.Clone()).ToList();
                    }
                }
            }
            catch (JsonException)
            {
                recordsByUser.Clear();
            }
        }

        nextId = Math.Max(
            1,
            recordsByUser.Values
                .SelectMany(item => item)
                .Select(item => item.Id)
                .DefaultIfEmpty(0)
                .Max() + 1);
        loaded = true;
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        foreach (var record in recordsByUser.Values.SelectMany(item => item))
        {
            RemoveInternalFields(record);
        }

        File.WriteAllText(storagePath, JsonSerializer.Serialize(recordsByUser, JsonOptions));
    }

    private static void RemoveInternalFields(FlowRecord record)
    {
        foreach (var key in record.ExtraFields.Keys.Where(key => key.StartsWith("__", StringComparison.Ordinal)).ToList())
        {
            record.ExtraFields.Remove(key);
        }
    }

    private List<FlowRecord> CreateSeed(long bankId, long bankUserId)
    {
        var start = new DateTime(2024, 4, 29, 16, 18, 24);
        var balance = 30000d;
        var amounts = new[]
        {
            -171.00, -13.58, -3900.00, 5700.00, 75500.00,
            -33.38, -117.00, -67250.00, -106.00, -24.27,
            40790.00, 9600.00, -53700.00, -32.14, -30.54,
            39710.00, -46600.00, -35.00, -8000.00, -117.00
        };

        var briefs = new[]
        {
            "支付宝", "支付宝", "财付通", "抖音支付", "消费",
            "支付宝", "支付宝", "财付通", "支付宝", "支付宝",
            "消费", "抖音支付", "转存", "支付宝", "支付宝",
            "消费", "转存", "支付宝", "财付通", "支付宝"
        };

        var channels = new[] { "电子商务", "电子商务", "EPAY", "EPAY", "INET", "电子商务", "IBPS" };
        var places = new[] { "109999", "239999", "159999" };

        var records = new List<FlowRecord>();
        for (var index = 0; index < amounts.Length; index++)
        {
            balance += amounts[index];
            var time = start.AddDays(index).AddMinutes(index * 47);
            records.Add(new FlowRecord
            {
                Id = nextId++,
                Index = index + 1,
                BankId = bankId,
                BankUserId = bankUserId,
                AccountTime = time,
                TradeMoney = amounts[index],
                Balance = Math.Round(balance, 2),
                BalanceAmount = Math.Round(balance, 2),
                IncomeAttribute = amounts[index] >= 0 ? "收入" : "支出",
                ProductBrief = briefs[index],
                CashCheck = "转账",
                TradeChannel = channels[index % channels.Length],
                TradeExplain = channels[index % channels.Length],
                AreaNum = places[index % places.Length],
                LogNum = RandomDigits(index + 1, 10),
                SerialNum = RandomDigits(index + 31, 8),
                OppositeAccount = $"62{RandomDigits(index + 8, 14)}",
                OppositeUsername = index % 3 == 0 ? "张三" : index % 3 == 1 ? "李四" : "王五",
                OppositeBank = "示例银行",
                Usage = briefs[index],
                Currency = "RMB",
                TradeCurrency = "RMB",
                Remark = index % 4 == 0 ? "个人交易样例" : string.Empty,
                MerchantName = index % 5 == 0 ? RandomDigits(index + 100, 6) : string.Empty,
                IncomeFlag = amounts[index] >= 0 ? "C" : "D",
                CreditAmount = amounts[index] > 0 ? amounts[index] : null,
                DebitAmount = amounts[index] < 0 ? Math.Abs(amounts[index]) : null
            });
        }

        return records;
    }

    private static string CreateKey(long bankId, long bankUserId)
    {
        return $"{bankId}:{bankUserId}";
    }

    private static string RandomDigits(int seed, int length)
    {
        var random = new Random(seed * 7919);
        return string.Concat(Enumerable.Range(0, length).Select(_ => random.Next(0, 10).ToString()));
    }
}
