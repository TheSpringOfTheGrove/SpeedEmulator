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
    public static bool IsReferenceDateAllowed(DateTime date, FlowRuleBase rule)
    {
        if (rule.TradeWeekend == false && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        if (rule.TradeHoliday != false) return true;
        // Retain weekday generation outside the bundled years. A missing future
        // rest-day notice must not make existing configurations unusable.
        return !Holidays.TryGetValue(date.Year, out var dates) || !dates.Contains(DateOnly.FromDateTime(date));
    }

    // Rest periods already used by the project's 2024–2026 calendar. Keep these
    // permissive checkbox semantics independent of CCB's exclusive-date policy.
    private static readonly Dictionary<int, HashSet<DateOnly>> Holidays = new()
    {
        [2024] = CreateYear(2024, "01-01", "02-10:02-17", "04-04:04-06", "05-01:05-05", "06-08:06-10", "09-15:09-17", "10-01:10-07"),
        [2025] = CreateYear(2025, "01-01", "01-28:02-04", "04-04:04-06", "05-01:05-05", "05-31:06-02", "10-01:10-08"),
        [2026] = CreateYear(2026, "01-01:01-03", "02-15:02-23", "04-04:04-06", "05-01:05-05", "06-19:06-21", "09-25:09-27", "10-01:10-07")
    };

    private static HashSet<DateOnly> CreateYear(int year, params string[] ranges)
    {
        var dates = new HashSet<DateOnly>();
        foreach (var range in ranges)
        {
            var ends = range.Split(':');
            var start = DateOnly.ParseExact($"{year}-{ends[0]}", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var end = DateOnly.ParseExact($"{year}-{ends[^1]}", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            for (var day = start; day <= end; day = day.AddDays(1)) dates.Add(day);
        }
        return dates;
    }
}
