using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public interface IFlowRecordRepository
{
    Task<IReadOnlyList<FlowRecord>> ListByUserAsync(Bank bank, long bankUserId);

    Task SaveAllAsync(long bankId, long bankUserId, IEnumerable<FlowRecord> records);
}
