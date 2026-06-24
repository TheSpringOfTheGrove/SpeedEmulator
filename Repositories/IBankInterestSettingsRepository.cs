using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public interface IBankInterestSettingsRepository
{
    Task<BankInterestSetting?> LoadAsync(long bankId);

    Task SaveAsync(long bankId, BankInterestSetting setting);

    Task DeleteAsync(long bankId);
}
