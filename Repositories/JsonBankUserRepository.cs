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

    public Task<IReadOnlyList<BankUser>> ListByBankAsync(Bank bank)
    {
        lock (syncRoot)
        {
            EnsureLoaded();
            var result = users
                .Where(user => user.BankId == bank.Id
                    || bank.AlternateIds.Contains(user.BankId)
                    || string.Equals(user.BankName?.Trim(), bank.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(user => user.UserCode)
                .ThenBy(user => user.Id)
                .Select(user =>
                {
                    var copy = user.Clone();
                    copy.BankId = bank.Id;
                    copy.BankName = bank.Name;
                    return copy;
                })
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

}
