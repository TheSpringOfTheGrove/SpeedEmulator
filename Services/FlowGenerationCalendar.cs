using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>Shared opt-in calendar for ordinary reference transactions at every bank.</summary>
public static class FlowGenerationCalendar
{
    public static bool IsDateAllowed(DateTime date, FlowRuleBase? rule)
    {
        // Fixed-date additions and dates without a reference source do not use
        // the reference checkboxes. Null checkbox values display as unchecked.
        if (rule is null || rule is GenerateConstRule) return true;
        if (rule.TradeWeekend != true && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        return rule.TradeHoliday == true || !IsHoliday(date);
    }

    public static bool IsHoliday(DateTime date) =>
        Holidays.TryGetValue(date.Year, out var dates) && dates.Contains(DateOnly.FromDateTime(date));

    public static bool HasHolidayCalendar(int year) => Holidays.ContainsKey(year);

    // Reuse the project's existing rest periods. Missing future calendars do
    // not invent holidays or stop generation; the weekend rule still applies.
    // Saturdays/Sundays remain weekends even on a makeup-work day.
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
