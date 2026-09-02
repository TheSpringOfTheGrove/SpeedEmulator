using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public interface IFlowRecordRepository
{
    Task<IReadOnlyList<FlowRecord>> ListByUserAsync(Bank bank, long bankUserId);

    Task SaveAllAsync(long bankId, long bankUserId, IEnumerable<FlowRecord> records);

    Task MoveUserRecordsAsync(long bankId, long sourceBankUserId, long targetBankUserId);

    Task<int> RecoverTemporaryUserRecordsAsync(long bankId, IReadOnlyList<BankUser> users);
}
