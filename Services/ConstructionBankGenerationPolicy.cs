using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>The exclusive-date interpretation belongs to personal CCB references only.</summary>
public static class ConstructionBankGenerationPolicy
{
    public static bool AppliesTo(Bank bank) => bank.Type == BankTypes.Personal && bank.Name == "建行";

    public static bool IsReferenceDateAllowed(DateTime date, FlowRuleBase rule)
    {
        var holiday = IsHoliday(date);
        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (rule.TradeWeekend == true || rule.TradeHoliday == true)
            return rule.TradeWeekend == true && weekend || rule.TradeHoliday == true && holiday;
        return !weekend && !holiday;
    }

    public static bool IsHoliday(DateTime date)
    {
        if (!Holidays.TryGetValue(date.Year, out var dates))
            throw new InvalidOperationException($"建行尚未收录{date.Year}年节假日日历，无法准确执行参照明细日期限制，请补齐日历后生成。");
        return dates.Contains(DateOnly.FromDateTime(date));
    }

    // Official annual notices, including the announced consecutive rest periods:
    // 2024: https://app.www.gov.cn/govdata/gov/202310/25/508678/article.html
    // 2025: https://www.forestry.gov.cn/c/www/szxx/594663.jhtml
    // 2026: https://www.beijing.gov.cn/cs/gncs/zcwj/202603/t20260327_4568275.html
    // A Saturday/Sunday remains a weekend for this checkbox, even on a makeup-work day.
    private static readonly Dictionary<int, HashSet<DateOnly>> Holidays = new()
    {
        [2024] = CreateYear(2024, "01-01", "02-10:02-17", "04-04:04-06", "05-01:05-05", "06-08:06-10", "09-15:09-17", "10-01:10-07"),
        [2025] = CreateYear(2025, "01-01", "01-28:02-04", "04-04:04-06", "05-01:05-05", "05-31:06-02", "10-01:10-08"),
        [2026] = CreateYear(2026, "01-01:01-03", "02-15:02-23", "04-04:04-06", "05-01:05-05", "06-19:06-21", "09-25:09-27", "10-01:10-07")
    };

    private static HashSet<DateOnly> CreateYear(int year, params string[] ranges)
    {
        var result = new HashSet<DateOnly>();
        foreach (var range in ranges)
        {
            var ends = range.Split(':');
            var start = DateOnly.ParseExact($"{year}-{ends[0]}", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var end = DateOnly.ParseExact($"{year}-{ends[^1]}", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            for (var day = start; day <= end; day = day.AddDays(1)) result.Add(day);
        }
        return result;
    }
}
