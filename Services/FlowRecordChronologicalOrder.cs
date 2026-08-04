using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>
/// Keeps imported statement rows in the same persistence order used by the flow generator.
/// </summary>
internal static class FlowRecordChronologicalOrder
{
    public static void SortInPlace(List<FlowRecord> records)
    {
        if (records.Count < 2)
        {
            return;
        }

        var ordered = records
            .Select((record, originalIndex) => new { record, originalIndex })
            .OrderBy(item => item.record.AccountTime)
            .ThenBy(item => item.record.SerialNum, StringComparer.Ordinal)
            .ThenBy(item => item.originalIndex)
            .Select(item => item.record)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            records[index] = ordered[index];
        }
    }

    public static bool IsInGeneratedOrder(IReadOnlyList<FlowRecord> records)
    {
        for (var index = 1; index < records.Count; index++)
        {
            if (Compare(records[index - 1], records[index]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int Compare(FlowRecord left, FlowRecord right)
    {
        var result = Nullable.Compare(left.AccountTime, right.AccountTime);
        return result != 0 ? result : string.CompareOrdinal(left.SerialNum, right.SerialNum);
    }
}
