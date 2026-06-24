using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public interface IBankUserRepository
{
    Task<IReadOnlyList<BankUser>> ListByBankAsync(long bankId);

    Task<BankUser> SaveAsync(BankUser user);

    Task DeleteAsync(long userId);
}
