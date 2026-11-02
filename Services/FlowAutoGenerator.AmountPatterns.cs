using System.Globalization;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private const string AmountPatternSignature = "__GeneratedAmountPatternSignature";
    private const string TailOriginIndex = "__GeneratedFinalTailOrigin";

    private static string CalculateAmountPatternSignature(IReadOnlyList<FlowRecord> records)
    {
        // Interest/opening corrections do not invalidate an unchanged ordinary
        // sequence. Otherwise a settlement-only change would redistribute valid
        // amounts and rewrite their cache metadata on every correction.
        var hash = new HashCode();
        foreach (var row in records)
        {
            if (row.Id != 0 || row.IsDocumentImported || IsSystemInterestRecord(row)) continue;
            hash.Add(row.AccountTime?.Ticks ?? 0L);
            hash.Add(RoundMoney(row.TradeMoney ?? 0));
            hash.Add(row.ExtraFields.GetValueOrDefault(SourceKindField), StringComparer.Ordinal);
            hash.Add(row.ExtraFields.GetValueOrDefault(SourceIndexField), StringComparer.Ordinal);
        }
        return hash.ToHashCode().ToString(CultureInfo.InvariantCulture);
    }

    private static bool HasVerifiedAmountPatterns(FlowAutoGenerationResult result)
    {
        var signature = CalculateAmountPatternSignature(result.Records);
        return result.Records.Any(row => row.ExtraFields.GetValueOrDefault(AmountPatternSignature) == signature);
    }

    private static FlowAutoGenerationResult PrepareGeneratedTailDistribution(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source)
    {
        var entries = source.Records.Select((row, index) => CreateAdjacentAmountEntry(request, row, index)).ToArray();
        var tails = entries.OfType<AdjacentAmountEntry>().Where(entry => entry.HasCentTail).ToArray();
        if (tails.Length < 2) return source;
        var signature = CalculateAmountPatternSignature(source.Records);
        if (source.Records[tails[0].Index].ExtraFields.GetValueOrDefault(AmountPatternSignature) == signature) return source;
        var amounts = source.Records.Select(row => AdjacentAmountCents(row.TradeMoney ?? 0)).ToArray();
        var patterns = new AmountPatternIndex(entries);
        var cap = Math.Max(3, (int)Math.Ceiling(tails.Length / 81d) + 1);
        if (!tails.GroupBy(entry => amounts[entry.Index] % 100).Any(group => group.Count() > cap)
            && !tails.Any(entry => patterns.TailConflict(amounts, entry.Index))
            && tails.Count(entry => IsLowCentTail(amounts[entry.Index])) <= LowCentTailLimit(tails.Length)) return source;

        // CCB bands describe the initial sampling distribution. They must not
        // pin dozens of independently adjustable rows at a band's .11 boundary.
        // Allow a sub-yuan final movement inside the original user range, while
        // preserving exact income/expense totals, settlements and all dates.
        var records = source.Records.Select(row => row.Clone()).ToList();
        var protectedKinds = new Dictionary<int, string>();
        var originalMarkers = new Dictionary<int, string>();
        for (var index = 0; index < records.Count; index++)
        {
            if (records[index].ExtraFields.TryGetValue(TailOriginIndex, out var marker)) originalMarkers[index] = marker;
            records[index].ExtraFields[TailOriginIndex] = index.ToString(CultureInfo.InvariantCulture);
            if (entries[index]?.HasCentTail != true && records[index].ExtraFields.Remove(SourceKindField, out var kind))
                protectedKinds[index] = kind;
        }
        foreach (var entry in tails)
        {
            var row = records[entry.Index];
            row.ExtraFields.Remove(TailSpacingSignature);
            if (row.ExtraFields.ContainsKey(CcbStratumMin))
                row.ExtraFields[CcbStratumMin] = Math.Max(entry.Bounds.Min, amounts[entry.Index] / 100d - .99d).ToString("R", CultureInfo.InvariantCulture);
            if (row.ExtraFields.ContainsKey(CcbStratumMax))
                row.ExtraFields[CcbStratumMax] = Math.Min(entry.Bounds.Max, amounts[entry.Index] / 100d + .99d).ToString("R", CultureInfo.InvariantCulture);
        }
        var relaxed = CreateAutomaticBalanceResult(records, source.OpeningBalance);
        var result = CompleteGeneratedTailSpacing(request, CompleteGeneratedResultCore(request, relaxed, generatedOnly: true));
        var changed = false;
        // Serial completion can reorder tied timestamps; retain provenance by
        // row identity rather than restoring protected metadata by list position.
        foreach (var row in result.Records)
        {
            var index = int.Parse(row.ExtraFields[TailOriginIndex], CultureInfo.InvariantCulture);
            row.ExtraFields.Remove(TailOriginIndex);
            if (originalMarkers.TryGetValue(index, out var marker)) row.ExtraFields[TailOriginIndex] = marker;
            if (protectedKinds.TryGetValue(index, out var kind)) row.ExtraFields[SourceKindField] = kind;
            if (row.TradeMoney == source.Records[index].TradeMoney) continue;
            changed = true;
            RefreshCashflowDerivedFields(request, row, source.Records[index].AccountTime!.Value, source.Records[index].TradeMoney ?? 0);
        }
        if (!changed) return source;
        ApplyBalances(result.Records, source.OpeningBalance);
        return CompleteAlipayResult(request, CreateAutomaticBalanceResult(result.Records, source.OpeningBalance));
    }

    private static FlowAutoGenerationResult StampGeneratedAmountPatterns(FlowAutoGenerationRequest request, FlowAutoGenerationResult result)
    {
        var entries = result.Records.Select((row, index) => CreateAdjacentAmountEntry(request, row, index)).ToArray();
        var amounts = result.Records.Select(row => AdjacentAmountCents(row.TradeMoney ?? 0)).ToArray();
        var patterns = new AmountPatternIndex(entries);
        for (var right = 1; right < amounts.Length; right++)
        {
            if (!patterns.Conflict(amounts, right)) continue;
            var left = patterns.LeftIndex(amounts, right);
            if (patterns.TailConflict(amounts, right) && IsLowCentTail(amounts[left]) && IsLowCentTail(amounts[right]))
                throw new InvalidOperationException($"相邻小数尾数校验未通过：第{left + 1}、{right + 1}条普通两位小数连续出现 .01～.09。请检查可用金额范围及收支约束。固定金额、结息及导入原值保留。");
            if (patterns.TailConflict(amounts, right))
                throw new InvalidOperationException($"相邻小数尾数校验未通过：第{left + 1}、{right + 1}条普通两位小数的尾数均为.{amounts[right] % 100:00}。当前金额区间、精度和收支约束未找到合法分配，不能返回重复尾数的数据。");
            throw new InvalidOperationException($"相邻金额校验未通过：第{left + 1}、{right + 1}条金额绝对值均为{amounts[right] / 100d:0.00}，请检查金额范围、精度及收支约束。固定金额、结息及导入原值保留。");
        }
        var anchor = entries.OfType<AdjacentAmountEntry>().FirstOrDefault(entry => entry.HasCentTail)
            ?? entries.OfType<AdjacentAmountEntry>().FirstOrDefault();
        if (anchor is not null)
            result.Records[anchor.Index].ExtraFields[AmountPatternSignature] = CalculateAmountPatternSignature(result.Records);
        return result;
    }

    private static IEnumerable<long> PatternAmountCandidates(AdjacentAmountEntry entry, long current, long step,
        Random random, int[] usage)
    {
        if (!entry.HasCentTail || step != 1) return AdjacentAmountCandidates(entry, current, step, random);
        var whole = current - current % 100;
        return Enumerable.Range(1, 99).Where(tail => tail % 10 != 0)
            .SelectMany(tail => new[] { whole + tail, whole - 100 + tail, whole + 100 + tail })
            .Where(next => next != current && Math.Abs(next - current) <= 99 && entry.Accepts(next))
            .Select(next => (Next: next, Tie: random.Next()))
            .OrderBy(item => IsLowCentTail(item.Next) ? 1 : 0)
            .ThenBy(item => usage[item.Next % 100]).ThenBy(item => Math.Abs(item.Next - current)).ThenBy(item => item.Tie)
            .Select(item => item.Next);
    }

    private sealed class AmountPatternIndex
    {
        private readonly bool[] mutable;
        public bool[] HasTail { get; }
        public int[] PreviousTail { get; }
        private readonly int[] nextTail;
        public AmountPatternIndex(IReadOnlyList<AdjacentAmountEntry?> entries)
        {
            mutable = entries.Select(entry => entry is not null).ToArray();
            HasTail = entries.Select(entry => entry?.HasCentTail == true).ToArray();
            PreviousTail = Enumerable.Repeat(-1, entries.Count).ToArray();
            nextTail = Enumerable.Repeat(-1, entries.Count).ToArray();
            var previous = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                if (!HasTail[i]) continue;
                PreviousTail[i] = previous;
                if (previous >= 0) nextTail[previous] = i;
                previous = i;
            }
        }
        // Integers/one-decimal rows do not hide a repeated hundredths tail.
        // Fixed, imported, persisted and settlement amounts are not candidates.
        public bool TailConflict(long[] amounts, int right) => right >= 0 && HasTail[right] && PreviousTail[right] >= 0
            && (amounts[right] % 100 == amounts[PreviousTail[right]] % 100
                || IsLowCentTail(amounts[right]) && IsLowCentTail(amounts[PreviousTail[right]]));
        public bool Conflict(long[] amounts, int right) => right > 0 && right < amounts.Length
            && (TailConflict(amounts, right) || mutable[right] && mutable[right - 1] && amounts[right] > 0 && amounts[right] == amounts[right - 1]);
        public int LeftIndex(long[] amounts, int right) => TailConflict(amounts, right) ? PreviousTail[right] : right - 1;
        public int[] Edges(int a, int b) => new[] { a, a + 1, b, b + 1, nextTail[a], nextTail[b] }
            .Where(index => index > 0 && index < mutable.Length).Distinct().ToArray();
    }
}
