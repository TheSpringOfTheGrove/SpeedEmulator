using System.Reflection;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public static class FormulaExcelDependencyAnalyzer
{
    private const string OuterDelimiter = "\\\\";

    private static readonly IReadOnlyList<PropertyInfo> StringProperties = typeof(FlowRuleBase)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead
            && property.PropertyType == typeof(string)
            && property.GetIndexParameters().Length == 0)
        .ToArray();

    public static IReadOnlyList<string> GetRequiredColumns(IEnumerable<FlowRuleBase> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            foreach (var property in StringProperties)
            {
                CollectFromValue((string?)property.GetValue(rule), result);
            }

            foreach (var value in rule.ExtraFields.Values)
            {
                CollectFromValue(value, result);
            }
        }

        return result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CollectFromValue(string? value, ISet<string> result)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var cursor = 0;
        while (cursor < value.Length)
        {
            var blockStart = value.IndexOf(OuterDelimiter, cursor, StringComparison.Ordinal);
            if (blockStart < 0)
            {
                return;
            }

            var contentStart = blockStart + OuterDelimiter.Length;
            var blockEnd = value.IndexOf(OuterDelimiter, contentStart, StringComparison.Ordinal);
            if (blockEnd < 0)
            {
                return;
            }

            CollectFromBlock(value.AsSpan(contentStart, blockEnd - contentStart), result);
            cursor = blockEnd + OuterDelimiter.Length;
        }
    }

    private static void CollectFromBlock(ReadOnlySpan<char> content, ISet<string> result)
    {
        for (var index = 0; index < content.Length; index++)
        {
            var delimiter = content[index];
            if (delimiter is not ('$' or '^'))
            {
                continue;
            }

            var relativeEnd = content[(index + 1)..].IndexOf(delimiter);
            if (relativeEnd < 0)
            {
                continue;
            }

            var columnName = content.Slice(index + 1, relativeEnd).Trim().ToString();
            if (!string.IsNullOrWhiteSpace(columnName))
            {
                result.Add(columnName);
            }

            index += relativeEnd + 1;
        }
    }
}
