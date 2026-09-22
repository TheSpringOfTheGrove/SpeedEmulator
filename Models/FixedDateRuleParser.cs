using System.Globalization;
using System.Text.RegularExpressions;

namespace SpeedEmulator.Models;

public enum FixedDateRuleKind
{
    Invalid = -1,
    FixedDay = 1,
    DayRange = 2,
    Daily = 3,
    Monthly = 4,
    Global = 5
}

public readonly record struct FixedDateRuleOption(
    FixedDateRuleKind Kind,
    int StartDay = 0,
    int EndDay = 0)
{
    public bool IsValid => Kind != FixedDateRuleKind.Invalid;
}

public static partial class FixedDateRuleParser
{
    [GeneratedRegex(@"^(?<start>\d{1,2})\s*-\s*(?<end>\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DayRangeRegex();

    public static FixedDateRuleOption Parse(string? value)
    {
        var token = value?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return new FixedDateRuleOption(FixedDateRuleKind.Invalid);
        }

        var symbolicKind = token switch
        {
            "+" => FixedDateRuleKind.Global,
            "*" => FixedDateRuleKind.Monthly,
            "=" => FixedDateRuleKind.Daily,
            _ => FixedDateRuleKind.Invalid
        };
        if (symbolicKind != FixedDateRuleKind.Invalid)
        {
            return new FixedDateRuleOption(symbolicKind);
        }

        var rangeMatch = DayRangeRegex().Match(token);
        if (rangeMatch.Success
            && TryParseDay(rangeMatch.Groups["start"].Value, out var startDay)
            && TryParseDay(rangeMatch.Groups["end"].Value, out var endDay))
        {
            return new FixedDateRuleOption(
                FixedDateRuleKind.DayRange,
                Math.Min(startDay, endDay),
                Math.Max(startDay, endDay));
        }

        return TryParseDay(token, out var day)
            ? new FixedDateRuleOption(FixedDateRuleKind.FixedDay, day, day)
            : new FixedDateRuleOption(FixedDateRuleKind.Invalid);
    }

    private static bool TryParseDay(string value, out int day)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out day)
            && day is >= 1 and <= 31;
    }
}
