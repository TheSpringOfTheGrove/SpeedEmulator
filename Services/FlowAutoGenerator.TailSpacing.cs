using System.Globalization;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    private const string TailSpacingSignature = "__GeneratedTailSpacingSignature";

    private static FlowAutoGenerationResult CompleteGeneratedTailSpacing(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source, bool preserveAmounts = false)
    {
        var entries = CreateCentTailEntries(request, source.Records);
        if (entries.Count < 2) return source;
        var signature = CalculateBalanceInputSignature(source.Records).ToString(CultureInfo.InvariantCulture);
        if (source.Records[entries[0].Index].ExtraFields.GetValueOrDefault(TailSpacingSignature) == signature
            || CountTailSpacingCollisions(entries) == 0) return source;
        if (preserveAmounts)
        {
            if (TryPermuteTailSpacing(request, source, entries, sameDayOnly: false, out var permuted)) return permuted;
            return TryPermuteTailSpacing(request, source, CreateCentTailEntries(request, source.Records), sameDayOnly: true, out permuted) ? permuted : source;
        }
        if (TryPlanTailRounds(request, source, entries, sameDayOnly: false, out var result)) return result;
        entries = CreateCentTailEntries(request, source.Records);
        if (TryCompleteTailSpacing(request, source, entries, sameDayOnly: false, checkEachMove: false, out result)) return result;
        entries = CreateCentTailEntries(request, source.Records);
        if (TryPlanTailRounds(request, source, entries, sameDayOnly: true, out result)) return result;
        entries = CreateCentTailEntries(request, source.Records);
        if (TryCompleteTailSpacing(request, source, entries, sameDayOnly: true, checkEachMove: false, out result)) return result;
        entries = CreateCentTailEntries(request, source.Records);
        return TryCompleteTailSpacing(request, source, entries, sameDayOnly: false, checkEachMove: true, out result) ? result : source;
    }

    private static bool TryPermuteTailSpacing(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source, List<CentTailEntry> entries,
        bool sameDayOnly, out FlowAutoGenerationResult result)
    {
        // A time-only repair retains each source rule's amount multiset. Swap
        // existing amounts only when both rows accept them; never reshape a CCB
        // distribution or turn a date repair into another amount allocation.
        var before = CountTailSpacingCollisions(entries);
        var best = before;
        var checks = 0;
        var collisions = GetTailSpacingCollisionIndices(entries);
        var groups = entries.GroupBy(entry => (entry.Group, Source: source.Records[entry.Index].ExtraFields.GetValueOrDefault(SourceIndexField)))
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.Index).ToArray());
        foreach (var rows in groups.Values)
        for (var position = 0; position < rows.Length && checks < 128; position++)
        {
            var target = rows[position];
            if (best == 0) break;
            if (!collisions.Contains(target.Index)) continue;
            foreach (var partner in GetCentTailPartners(rows, position, sameDayOnly))
            {
                if (checks >= 128) break;
                if (target.Tail == partner.Tail || !target.Accepts(partner.Cents) || !partner.Accepts(target.Cents)) continue;
                checks++;
                (target.Cents, partner.Cents) = (partner.Cents, target.Cents);
                var score = CountTailSpacingCollisions(entries);
                if (score < best) { best = score; break; }
                (target.Cents, partner.Cents) = (partner.Cents, target.Cents);
            }
        }
        return TryCreateTailSpacingResult(request, source, entries, before, out result);
    }

    private static bool TryPlanTailRounds(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source, List<CentTailEntry> entries,
        bool sameDayOnly, out FlowAutoGenerationResult result)
    {
        result = source;
        var before = CountTailSpacingCollisions(entries);
        var states = CreateTailRoundStates(entries);
        var random = new Random(CalculateBalanceInputSignature(source.Records) ^ 0x6C3E1);
        foreach (var entry in entries.OrderBy(entry => entry.Time).ThenBy(entry => entry.Index))
        {
            var state = states[(entry.Sign, entry.Group.Account)];
            var tails = !state.IsRepeat(entry.Tail) ? new[] { entry.Tail }
                : state.Available.OrderBy(tail => state.Used[tail]).ThenBy(tail => state.Usage[tail])
                    .ThenBy(tail => state.LastPosition[tail]).ThenBy(_ => random.Next()).ToArray();
            foreach (var tail in tails)
            {
                var next = TailSpacingCandidates(entry, tail).OrderBy(value => Math.Abs(value - entry.Cents)).FirstOrDefault();
                if (next <= 0) continue;
                entry.Cents = next;
                break;
            }
            state.Register(entry.Tail);
        }
        var peerPositions = entries.GroupBy(entry => (entry.Sign, entry.Group.Account)).Select(group => group.OrderBy(entry => entry.Time).ThenBy(entry => entry.Index).ToArray())
            .SelectMany(rows => rows.Select((entry, position) => (entry.Index, Rows: rows, Position: position)))
            .ToDictionary(item => item.Index);
        foreach (var group in entries.GroupBy(entry => (entry.Group, Day: sameDayOnly ? entry.Time.Date : (DateTime?)null)))
        {
            var rows = group.OrderBy(entry => entry.Index).ToList();
            var difference = rows.Sum(entry => entry.OriginalCents - entry.Cents);
            var residue = difference % 100;
            if (residue != 0)
            {
                // Complete rounds need not have the same cent sum as a fixed
                // budget. Put its unavoidable residue where it creates the fewest
                // nearby repeats; never change the requested total to fit a round.
                var candidates = rows.SelectMany(entry => new[] { residue, residue - Math.Sign(residue) * 100 }
                        .Select(delta => (Entry: entry, Amount: entry.Cents + delta)))
                    .Where(item => item.Entry.Accepts(item.Amount) && IsLegalCycleTail((int)(item.Amount % 100)))
                    .Select(item => (item.Entry, item.Amount,
                        Penalty: TailSpacingReplacementPenalty(peerPositions[item.Entry.Index].Rows,
                            peerPositions[item.Entry.Index].Position, (int)(item.Amount % 100))))
                    .OrderBy(item => item.Penalty).ThenByDescending(item => item.Entry.Index).ToList();
                if (candidates.Count > 0) candidates[0].Entry.Cents = candidates[0].Amount;
            }
            difference = rows.Sum(entry => entry.OriginalCents - entry.Cents);
            if (difference % 100 == 0)
            {
                foreach (var entry in OrderTimelineRepairCandidates(rows, entry => entry.Time))
                {
                    if (difference == 0) break;
                    var delta = Math.Sign(difference) * 100;
                    if (!entry.Accepts(entry.Cents + delta)) continue;
                    entry.Cents += delta;
                    difference -= delta;
                }
            }
            if (difference == 0) continue;
            foreach (var entry in rows) entry.Cents = entry.OriginalCents;
        }
        return TryCreateTailSpacingResult(request, source, entries, before, out result);
    }

    private static IEnumerable<long> TailSpacingCandidates(CentTailEntry entry, int tail)
    {
        var whole = entry.Cents - entry.Tail;
        foreach (var amount in new[] { whole + tail, whole + tail - 100, whole + tail + 100 })
            if (entry.Accepts(amount)) yield return amount;
    }

    private static int TailSpacingReplacementPenalty(CentTailEntry[] peers, int position, int tail)
    {
        var penalty = 0;
        for (var i = Math.Max(0, position - 16); i <= Math.Min(peers.Length - 1, position + 16); i++)
        {
            if (i == position || peers[i].Tail != tail) continue;
            penalty += 1000 / Math.Abs(i - position);
        }
        // Prefer to leave earlier complete rounds intact when repairing the final
        // residue. The last partial/full round is where the checksum is known.
        penalty += position / 81 < (peers.Length - 1) / 81 ? 10000 : 0;
        return penalty;
    }

    private static bool TryCompleteTailSpacing(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source, List<CentTailEntry> entries,
        bool sameDayOnly, bool checkEachMove, out FlowAutoGenerationResult result)
    {
        result = source;
        var beforeCollisions = CountTailSpacingCollisions(entries);
        var states = CreateTailRoundStates(entries);
        var groups = entries.GroupBy(entry => entry.Group).ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.Index).ToArray());
        var positions = groups.Values.SelectMany(rows => rows.Select((entry, position) => (entry.Index, Position: position)))
            .ToDictionary(item => item.Index, item => item.Position);
        var balances = new CentTailBalanceHeadroom(source.Records.Select(row => row.Balance ?? source.OpeningBalance).ToArray());
        var balanceFloor = Math.Max(0, source.MinimumBalance);
        var working = checkEachMove ? source.Records.Select(row => row.Clone()).ToList() : null;
        var moves = 0;
        var interestChecks = 0;
        var random = new Random(CalculateBalanceInputSignature(source.Records) ^ 0x71A1C);
        foreach (var target in entries.OrderBy(entry => entry.Time).ThenBy(entry => entry.Index))
        {
            var state = states[(target.Sign, target.Group.Account)];
            if (moves < CentTailMaximumMoves && state.IsRepeat(target.Tail))
            {
                var partners = GetCentTailPartners(groups[target.Group], positions[target.Index], sameDayOnly)
                    .Where(partner => partner.Index > target.Index).Take(48).ToArray();
                var tails = state.Available.Where(tail => !state.Used[tail])
                    .OrderBy(tail => state.Usage[tail]).ThenBy(tail => state.LastPosition[tail]).ThenBy(_ => random.Next()).ToArray();
                var applied = false;
                foreach (var tail in tails)
                {
                    if (tail == target.Tail) continue;
                    var forward = tail - target.Tail;
                    foreach (var delta in new[] { forward, forward - Math.Sign(forward) * 100 })
                    {
                        var nextTarget = target.Cents + delta;
                        if (!target.Accepts(nextTarget)) continue;
                        foreach (var partner in partners)
                        {
                            var nextPartner = partner.Cents - delta;
                            var partnerTail = (int)(nextPartner % 100);
                            if (!partner.Accepts(nextPartner) || !IsLegalCycleTail(partnerTail)) continue;
                            var change = target.Sign * delta / 100d;
                            var first = target.Index;
                            var last = partner.Index - 1;
                            if (change < 0 && balances.Minimum(first, last) + change < balanceFloor - 0.000001d) continue;
                            var oldTarget = target.Cents;
                            var oldPartner = partner.Cents;
                            target.Cents = nextTarget;
                            partner.Cents = nextPartner;
                            if (checkEachMove)
                            {
                                ApplySignedAmount(working![target.Index], target.Sign, nextTarget / 100d);
                                ApplySignedAmount(working[partner.Index], partner.Sign, nextPartner / 100d);
                                var crossDay = target.Time.Date != partner.Time.Date;
                                var allowed = !crossDay || interestChecks++ < 256
                                    && CentTailInterestUnchanged(request, source, working, entries.Where(entry => entry.Cents != entry.OriginalCents).ToArray());
                                if (!allowed)
                                {
                                    target.Cents = oldTarget;
                                    partner.Cents = oldPartner;
                                    ApplySignedAmount(working[target.Index], target.Sign, oldTarget / 100d);
                                    ApplySignedAmount(working[partner.Index], partner.Sign, oldPartner / 100d);
                                    continue;
                                }
                            }
                            balances.Add(first, last, change);
                            moves++;
                            applied = true;
                            break;
                        }
                        if (applied) break;
                    }
                    if (applied) break;
                }
            }
            state.Register(target.Tail);
        }
        return TryCreateTailSpacingResult(request, source, entries, beforeCollisions, out result);
    }

    private static bool TryCreateTailSpacingResult(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source, List<CentTailEntry> entries,
        int beforeCollisions, out FlowAutoGenerationResult result)
    {
        result = source;
        var changed = entries.Where(entry => entry.Cents != entry.OriginalCents).ToArray();
        if (changed.Length == 0 || CountTailSpacingCollisions(entries) >= beforeCollisions) return false;
        var records = source.Records.Select(row => row.Clone()).ToList();
        foreach (var entry in changed)
        {
            var row = records[entry.Index];
            ApplySignedAmount(row, entry.Sign, entry.Cents / 100d);
            row.ExtraFields[CentTailBaseline] = entry.BaselineCents.ToString(CultureInfo.InvariantCulture);
            row.ExtraFields[CentTailLast] = entry.Cents.ToString(CultureInfo.InvariantCulture);
        }
        if (!CentTailInterestUnchanged(request, source, records, changed)) return false;
        ApplyBalances(records, source.OpeningBalance);
        var proposed = CompleteAlipayResult(request, CreateCentTailResult(request, source, records));
        if (SumIncome(proposed.Records) != SumIncome(source.Records) || SumExpense(proposed.Records) != SumExpense(source.Records)
            || Math.Abs(proposed.FinalBalance - source.FinalBalance) > 0.009d
            || proposed.MinimumBalance < source.MinimumBalance - 0.009d
            || CountTailSpacingCollisions(CreateCentTailEntries(request, proposed.Records)) >= beforeCollisions) return false;
        var anchor = CreateCentTailEntries(request, proposed.Records)[0];
        proposed.Records[anchor.Index].ExtraFields[TailSpacingSignature] = CalculateBalanceInputSignature(proposed.Records).ToString(CultureInfo.InvariantCulture);
        result = proposed;
        return true;
    }

    private static bool IsLegalCycleTail(int tail) => tail is >= 11 and <= 99 && tail % 10 != 0;

    private static Dictionary<(int Sign, string Account), TailRoundState> CreateTailRoundStates(List<CentTailEntry> entries)
    {
        return entries.GroupBy(entry => (entry.Sign, entry.Group.Account)).ToDictionary(group => group.Key, group =>
        {
            var available = new HashSet<int>();
            foreach (var entry in group)
            {
                if (available.Count == 81) break;
                for (var tail = 11; tail <= 99; tail++)
                {
                    if (!IsLegalCycleTail(tail) || available.Contains(tail)) continue;
                    var delta = tail - entry.Tail;
                    if (entry.Accepts(entry.Cents + delta) || entry.Accepts(entry.Cents + delta - Math.Sign(delta) * 100)) available.Add(tail);
                }
            }
            return new TailRoundState(available.Order().ToArray());
        });
    }

    private static int CountTailSpacingCollisions(List<CentTailEntry> entries)
    {
        // A nearby repeat matters more than a distant round/checksum exception.
        var states = CreateTailRoundStates(entries);
        var score = 0;
        foreach (var entry in entries.OrderBy(entry => entry.Time).ThenBy(entry => entry.Index))
        {
            var state = states[(entry.Sign, entry.Group.Account)];
            if (state.Used[entry.Tail]) score++;
            if (state.IsRecent(entry.Tail)) score += 100;
            state.Register(entry.Tail);
        }
        return score;
    }

    private static HashSet<int> GetTailSpacingCollisionIndices(List<CentTailEntry> entries)
    {
        var states = CreateTailRoundStates(entries);
        var collisions = new HashSet<int>();
        foreach (var entry in entries.OrderBy(entry => entry.Time).ThenBy(entry => entry.Index))
        {
            var state = states[(entry.Sign, entry.Group.Account)];
            if (state.IsRepeat(entry.Tail)) collisions.Add(entry.Index);
            state.Register(entry.Tail);
        }
        return collisions;
    }

    private sealed class TailRoundState(int[] available)
    {
        public int[] Available { get; } = available;
        public bool[] Used { get; } = new bool[100];
        public int[] Usage { get; } = new int[100];
        public int[] LastPosition { get; } = Enumerable.Repeat(-1000000, 100).ToArray();
        private int position;
        private int usedCount;
        public bool IsRecent(int tail) => position - LastPosition[tail] < Math.Min(16, Math.Max(1, Available.Length / 2));
        public bool IsRepeat(int tail) => Used[tail] || IsRecent(tail);
        public void Register(int tail)
        {
            if (!Used[tail]) { Used[tail] = true; usedCount++; }
            Usage[tail]++;
            LastPosition[tail] = position++;
            if (usedCount < Available.Length) return;
            Array.Clear(Used);
            usedCount = 0;
        }
    }
}
