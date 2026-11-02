using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>Personal ICBC, total-income allocation only. Other banks and monthly modes retain their paths.</summary>
public static class IcbcGenerationPolicy
{
    public static bool AppliesTo(Bank bank, FlowGenerationConfig config) => config.SelectIndex == 0
        && bank.Type == BankTypes.Personal
        && (bank.Id == 4 || bank.Name.Trim() is "工行" or "工商银行" or "中国工商银行");

    public static bool IsReferenceDateAllowed(DateTime date, FlowRuleBase rule) =>
        ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(date, rule);
}
