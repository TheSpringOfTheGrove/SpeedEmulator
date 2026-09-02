using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public interface IBankUserRepository
{
    Task<IReadOnlyList<BankUser>> ListByBankAsync(Bank bank);

    Task<BankUser> SaveAsync(BankUser user);

    Task DeleteAsync(long userId);
}
