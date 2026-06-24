using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public interface IBankUserColumnSettingsRepository
{
    Task<IReadOnlyList<BankUserColumnSetting>> LoadAsync(long bankId);

    Task<IReadOnlyList<BankUserColumnSetting>> LoadAsync(long bankId, string scope);

    Task SaveAsync(long bankId, IEnumerable<BankUserColumnSetting> settings);

    Task SaveAsync(long bankId, string scope, IEnumerable<BankUserColumnSetting> settings);
}
