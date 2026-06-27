using SpeedEmulator.Models;

namespace SpeedEmulator.Repositories;

public interface IFlowGenerationRepository
{
    Task<FlowGenerationSnapshot> LoadAsync(Bank bank, long? bankUserId);

    Task SaveAsync(long bankId, long? bankUserId, FlowGenerationSnapshot snapshot);
}
