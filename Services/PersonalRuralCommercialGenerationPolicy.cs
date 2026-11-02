using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>Personal rural-commercial bank, total-income mode only.</summary>
public static class PersonalRuralCommercialGenerationPolicy
{
    public static bool AppliesTo(Bank bank, FlowGenerationConfig config) => config.SelectIndex == 0
        && bank.Type == BankTypes.Personal
        && (bank.Id == 3 || bank.Name.Trim() is "个人农商" or "农商个人" or "农村商业银行个人");
}
