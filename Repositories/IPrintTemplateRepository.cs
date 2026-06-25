using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public interface IPrintTemplateRepository
{
    Task<IReadOnlyList<PrintTemplate>> ListByBankAsync(Bank bank);

    Task SaveAsync(Bank bank, PrintTemplate template);

    Task DeleteAsync(long bankId, long templateId);
}

