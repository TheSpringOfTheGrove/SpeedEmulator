using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>Personal CCB routing; its reference calendar is shared with every other bank.</summary>
public static class ConstructionBankGenerationPolicy
{
    public static bool AppliesTo(Bank bank) => bank.Type == BankTypes.Personal && bank.Name == "建行";

    public static bool IsReferenceDateAllowed(DateTime date, FlowRuleBase rule) =>
        FlowGenerationCalendar.IsDateAllowed(date, rule);

    public static bool IsHoliday(DateTime date)
    {
        if (!FlowGenerationCalendar.HasHolidayCalendar(date.Year))
            throw new InvalidOperationException($"建行尚未收录{date.Year}年节假日日历，无法准确执行参照明细日期限制，请补齐日历后生成。");
        return FlowGenerationCalendar.IsHoliday(date);
    }
}
