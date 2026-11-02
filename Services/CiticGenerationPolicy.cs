using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>Personal CITIC, total-income mode only.</summary>
public static class CiticGenerationPolicy
{
    public static bool AppliesTo(Bank bank, FlowGenerationConfig config) => config.SelectIndex == 0
        && bank.Type == BankTypes.Personal
        && (bank.Id == 17 || bank.Name.Trim() is "中信" or "中信银行");
}
