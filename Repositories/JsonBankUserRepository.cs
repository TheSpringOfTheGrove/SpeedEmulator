using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public sealed class JsonBankUserRepository : IBankUserRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object syncRoot = new();
    private readonly string storagePath;
    private List<BankUser> users = [];
    private long nextId = 1000;
    private bool loaded;

    public JsonBankUserRepository()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "bank-users.json");
    }

    public Task<IReadOnlyList<BankUser>> ListByBankAsync(long bankId)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            var result = users
                .Where(user => user.BankId == bankId)
                .OrderBy(user => user.UserCode)
                .ThenBy(user => user.Id)
                .Select(user => user.Clone())
                .ToList();

            return Task.FromResult<IReadOnlyList<BankUser>>(result);
        }
    }

    public Task<BankUser> SaveAsync(BankUser user)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            var copy = user.Clone();
            var now = DateTime.Now;

            if (copy.Id <= 0)
            {
                copy.Id = nextId++;
                copy.CreatedAt = now;
            }

            var index = users.FindIndex(item => item.Id == copy.Id);
            if (index >= 0)
            {
                copy.CreatedAt = users[index].CreatedAt;
                users[index] = copy;
            }
            else
            {
                users.Add(copy);
            }

            copy.UpdatedAt = now;
            Persist();
            return Task.FromResult(copy.Clone());
        }
    }

    public Task DeleteAsync(long userId)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            users.RemoveAll(user => user.Id == userId);
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
                users = JsonSerializer.Deserialize<List<BankUser>>(json, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                users = [];
            }
        }

        if (users.Count == 0)
        {
            Seed();
            Persist();
        }

        nextId = Math.Max(1000, users.Select(user => user.Id).DefaultIfEmpty(999).Max() + 1);
        loaded = true;
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(storagePath, JsonSerializer.Serialize(users, JsonOptions));
    }

    private void Seed()
    {
        if (TryLoadPackagedSeed(out var packagedUsers))
        {
            users = packagedUsers;
            return;
        }

        var now = DateTime.Now;
        users =
        [
            new BankUser
            {
                Id = 1000,
                BankId = 1,
                BankName = "支付宝",
                UserCode = "ZFB-001",
                AccountName = "张三",
                AccountNo = "zhangsan@example.com",
                IdNumber = "510100199001011234",
                PhoneNumber = "13800000001",
                Balance = 23850.56m,
                OpeningBalance = 23850.56m,
                StartDate = new DateTime(2025, 4, 1, 13, 24, 27),
                EndDate = new DateTime(2026, 1, 1),
                TransactionType = "121",
                Currency = "RMB",
                ChapterCode = "SSSS",
                ChapterBranch = "SSSSS",
                ShouldPrintSeal = true,
                LoginPassword = "demo-login",
                PaymentPassword = "demo-pay",
                Remark = "个人支付宝样例",
                CreatedAt = now.AddDays(-6),
                UpdatedAt = now.AddDays(-1)
            },
            new BankUser
            {
                Id = 1001,
                BankId = 1,
                BankName = "支付宝",
                UserCode = "ZFB-002",
                AccountName = "李四",
                AccountNo = "13900000002",
                IdNumber = "510100199205051234",
                PhoneNumber = "13900000002",
                Balance = 8600m,
                OpeningBalance = 8600m,
                StartDate = new DateTime(2025, 4, 1, 13, 24, 27),
                EndDate = new DateTime(2026, 1, 1),
                TransactionType = "121",
                Currency = "RMB",
                ChapterCode = "SSSS",
                ChapterBranch = "SSSSS",
                LoginPassword = "demo-login",
                PaymentPassword = "demo-pay",
                Remark = "手机号登录",
                CreatedAt = now.AddDays(-4),
                UpdatedAt = now
            },
            new BankUser
            {
                Id = 1002,
                BankId = 2,
                BankName = "工行",
                UserCode = "ICBC-001",
                AccountName = "王五",
                AccountNo = "6222020200000000001",
                IdNumber = "510100198812121234",
                PhoneNumber = "13700000003",
                OpenBranch = "成都高新支行",
                Balance = 125000.18m,
                OpeningBalance = 125000.18m,
                StartDate = new DateTime(2025, 4, 1, 13, 24, 27),
                EndDate = new DateTime(2026, 1, 1),
                TransactionType = "121",
                Currency = "RMB",
                ChapterCode = "ICBC",
                ChapterBranch = "成都高新支行",
                LoginPassword = "demo-login",
                PaymentPassword = "demo-pay",
                UShieldNo = "ICBC-U-001",
                Remark = "一类卡",
                CreatedAt = now.AddDays(-8),
                UpdatedAt = now.AddHours(-3)
            },
            new BankUser
            {
                Id = 1003,
                BankId = 3,
                BankName = "农行",
                UserCode = "ABC-001",
                AccountName = "赵六",
                AccountNo = "6228480200000000001",
                IdNumber = "510100199603031234",
                PhoneNumber = "13600000004",
                OpenBranch = "成都锦江支行",
                Balance = 54320.72m,
                OpeningBalance = 54320.72m,
                StartDate = new DateTime(2025, 4, 1, 13, 24, 27),
                EndDate = new DateTime(2026, 1, 1),
                TransactionType = "121",
                Currency = "RMB",
                ChapterCode = "ABC",
                ChapterBranch = "成都锦江支行",
                LoginPassword = "demo-login",
                PaymentPassword = "demo-pay",
                UShieldNo = "ABC-K-001",
                Remark = "流水生成测试用户",
                CreatedAt = now.AddDays(-10),
                UpdatedAt = now.AddHours(-2)
            }
        ];
    }

    private static bool TryLoadPackagedSeed(out List<BankUser> packagedUsers)
    {
        packagedUsers = [];

        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "bank-users-seed.json"),
            Path.Combine(AppContext.BaseDirectory, "bank-users-seed.json")
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var seedUsers = JsonSerializer.Deserialize<List<BankUser>>(json, JsonOptions) ?? [];
                if (seedUsers.Count == 0)
                {
                    continue;
                }

                packagedUsers = seedUsers;
                return true;
            }
            catch (JsonException)
            {
                packagedUsers = [];
            }
            catch (IOException)
            {
                packagedUsers = [];
            }
        }

        return false;
    }
}
