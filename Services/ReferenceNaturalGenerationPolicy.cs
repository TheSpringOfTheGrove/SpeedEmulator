using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>Total-income rollout for banks with reproduced amount/count defects.</summary>
public static class ReferenceNaturalGenerationPolicy
{
    public static bool AppliesTo(Bank bank, FlowGenerationConfig config) =>
        IcbcGenerationPolicy.AppliesTo(bank, config) || PersonalRuralCommercialGenerationPolicy.AppliesTo(bank, config)
        || CiticGenerationPolicy.AppliesTo(bank, config)
        || config.SelectIndex == 0 && (bank.Type == BankTypes.Personal
            && bank.Id is 1 or 5 or 6 or 7 or 9 or 10 or 11 or 12 or 13 or 15 or 16
            || bank.Type == BankTypes.Corporate && bank.Id is 104 or 107 or 113);

    // These checkboxes permit trading on those days; they do not request that
    // rejected monthly attempts be retried on a different date.
    public static bool IsReferenceDateAllowed(DateTime date, FlowRuleBase rule) =>
        FlowGenerationCalendar.IsDateAllowed(date, rule);
}
