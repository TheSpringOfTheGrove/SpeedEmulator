using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    // Closing a total must not consume the tail of the statement first. Visit
    // months in evenly spaced order, and dates within each month in the same
    // way. Each round takes at most one candidate from every available bucket.
    // Callers retain their precision/required-rule priorities using stable
    // OrderBy after this ordering, and keep their original budget window.
    private static List<FlowRecord> OrderAmountRepairCandidates(IEnumerable<FlowRecord> candidates)
        => OrderTimelineRepairCandidates(candidates, row => row.AccountTime);

    private static List<T> OrderTimelineRepairCandidates<T>(IEnumerable<T> candidates, Func<T, DateTime?> getTime)
    {
        var rows = candidates.ToList();
        if (rows.Count < 2) return rows;
        var dated = rows.Where(row => getTime(row).HasValue).ToList();
        if (dated.Count < 2) return rows;

        var months = dated.GroupBy(row => (getTime(row)!.Value.Year, getTime(row)!.Value.Month))
            .OrderBy(group => group.Key).Select(month =>
            {
                var days = month.GroupBy(row => getTime(row)!.Value.Date).OrderBy(group => group.Key)
                    .Select(day => new Queue<T>(day)).ToArray();
                return new Queue<T>(InterleaveTimelineBuckets(days));
            }).ToArray();
        var result = InterleaveTimelineBuckets(months);
        result.AddRange(rows.Where(row => !getTime(row).HasValue));
        return result;
    }

    private static List<T> InterleaveTimelineBuckets<T>(Queue<T>[] buckets)
    {
        var active = new Queue<int>(GetTimelineBucketOrder(buckets.Length));
        var result = new List<T>(buckets.Sum(bucket => bucket.Count));
        while (active.TryDequeue(out var index))
        {
            if (!buckets[index].TryDequeue(out var row)) continue;
            result.Add(row);
            if (buckets[index].Count > 0) active.Enqueue(index);
        }
        return result;
    }

    private static int[] GetTimelineBucketOrder(int count)
    {
        var result = new List<int>(count);
        var ranges = new Queue<(int Start, int End)>();
        if (count > 0) ranges.Enqueue((0, count));
        while (ranges.TryDequeue(out var range))
        {
            var middle = range.Start + (range.End - range.Start) / 2;
            result.Add(middle);
            if (middle > range.Start) ranges.Enqueue((range.Start, middle));
            if (middle + 1 < range.End) ranges.Enqueue((middle + 1, range.End));
        }
        return result.ToArray();
    }
}
