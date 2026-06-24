using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public sealed class InMemoryBankUserRepository : IBankUserRepository
{
    private readonly object syncRoot = new();
    private readonly List<BankUser> users = [];
    private long nextId = 1000;

    public InMemoryBankUserRepository()
    {
        Seed();
    }

    public Task<IReadOnlyList<BankUser>> ListByBankAsync(long bankId)
    {
        lock (syncRoot)
        {
            var result = users
                .Where(user => user.BankId == bankId)
                .OrderBy(user => user.UserCode)
                .Select(user => user.Clone())
                .ToList();

            return Task.FromResult<IReadOnlyList<BankUser>>(result);
        }
    }

    public Task<BankUser> SaveAsync(BankUser user)
    {
        lock (syncRoot)
        {
            var copy = user.Clone();
            var now = DateTime.Now;

            if (copy.Id <= 0)
            {
                copy.Id = nextId++;
                copy.CreatedAt = now;
                users.Add(copy);
            }
            else
            {
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
            }

            copy.UpdatedAt = now;
            return Task.FromResult(copy.Clone());
        }
    }

    public Task DeleteAsync(long userId)
    {
        lock (syncRoot)
        {
            users.RemoveAll(user => user.Id == userId);
            return Task.CompletedTask;
        }
    }

    private void Seed()
    {
        var now = DateTime.Now;

        users.AddRange(
        [
            new BankUser
            {
                Id = nextId++,
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
                Id = nextId++,
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
                Id = nextId++,
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
                Id = nextId++,
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
        ]);
    }
}
