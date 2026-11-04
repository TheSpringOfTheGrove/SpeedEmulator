using System.Globalization;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed partial class FlowAutoGenerator
{
    // Runs after cashflow, tail distribution, interest and number completion.
    // Full amounts use persisted neighbours across both directions. Cent tails
    // use the two-decimal subsequence, so integer rows cannot hide repeats.
    private static FlowAutoGenerationResult CompleteGeneratedAdjacentAmounts(
        FlowAutoGenerationRequest request, FlowAutoGenerationResult source, bool allowTimePermutation = true)
    {
        if (source.Records.Count < 2) return source;
        if (!HasVerifiedAmountDistribution(source))
        {
            source = CompleteGeneratedResultCore(request, source, generatedOnly: true);
            source = PrepareGeneratedTailDistribution(request, source);
        }
        var rows = source.Records;
        var referencePriority = UsesReferencePriority(request.Config, rows);
        var entries = rows.Select((row, index) => CreateAdjacentAmountEntry(request, row, index)).ToArray();
        var amounts = rows.Select(row => AdjacentAmountCents(row.TradeMoney ?? 0)).ToArray();
        // A pair touching a fixed/imported/settlement value is exempt. There may
        // be no legal way to change its neighbour (e.g. one funded income exactly
        // covering a fixed payment); the user explicitly preserves these values.
        var patterns = new AmountPatternIndex(entries);
        bool Conflict(int right) => patterns.Conflict(amounts, right);
        if (!Enumerable.Range(1, rows.Count - 1).Any(Conflict)) return StampGeneratedAmountPatterns(request, source);

        var working = rows.Select(row => row.Clone()).ToList();
        var groups = entries.OfType<AdjacentAmountEntry>().GroupBy(entry => entry.Group)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var balances = new CentTailBalanceHeadroom(rows.Select(row => row.Balance ?? source.OpeningBalance).ToArray());
        var random = new Random(CalculateBalanceInputSignature(rows) ^ 0x45A73);
        var changed = new HashSet<int>();
        var interestChecks = 0;
        var tailUsage = new int[100];
        foreach (var entry in entries.OfType<AdjacentAmountEntry>().Where(entry => entry.HasCentTail)) tailUsage[amounts[entry.Index] % 100]++;
        var lowCount = tailUsage.Skip(1).Take(9).Sum();
        var lowLimit = LowCentTailLimit(tailUsage.Sum());

        bool TryChange(AdjacentAmountEntry target, long next, AdjacentAmountEntry? partner = null, long peerNext = 0)
        {
            if (!target.Accepts(next) || partner is not null && !partner.Accepts(peerNext)) return false;
            var a = target.Index; var b = partner?.Index ?? a;
            var old = amounts[a]; var peerOld = amounts[b];
            if (old == next || partner is not null && target.Sign * (next - old) + partner.Sign * (peerNext - peerOld) != 0) return false;
            var nextLowCount = lowCount
                + (target.HasCentTail ? (IsLowCentTail(next) ? 1 : 0) - (IsLowCentTail(old) ? 1 : 0) : 0)
                + (partner?.HasCentTail == true ? (IsLowCentTail(peerNext) ? 1 : 0) - (IsLowCentTail(peerOld) ? 1 : 0) : 0);
            if (nextLowCount > lowLimit && nextLowCount > lowCount) return false;
            var edges = patterns.Edges(a, b);
            var before = edges.Count(Conflict);
            var previous = edges.ToDictionary(i => i, Conflict);
            amounts[a] = next;
            if (partner is not null) amounts[b] = peerNext;
            var improves = edges.Count(Conflict) < before && edges.All(i => previous[i] || !Conflict(i));
            amounts[a] = old; amounts[b] = peerOld;
            if (!improves) return false;
            var first = Math.Min(a, b); var last = partner is null ? rows.Count - 1 : Math.Max(a, b) - 1;
            var delta = target.Sign * (next - old) / 100d * (a <= b ? 1 : -1);
            if (!referencePriority && delta < 0 && balances.Minimum(first, last) + delta < -.009d) return false;
            ApplySignedAmount(working[a], target.Sign, next / 100d);
            if (partner is not null) ApplySignedAmount(working[b], partner.Sign, peerNext / 100d);
            var sameDay = partner is not null && target.Time.Date == partner.Time.Date;
            var allowed = true;
            if (!referencePriority && !sameDay && BankInterestPolicy.ShouldCalculate(request.Bank, request.BankUser))
            {
                // Verify rounding on a copy; never rewrite an existing settlement.
                var check = new CentTailEntry(a, target.Time, target.Sign, "", "", old, old, 0, long.MaxValue) { Cents = next };
                var checks = new List<CentTailEntry> { check };
                if (partner is not null) checks.Add(new CentTailEntry(b, partner.Time, partner.Sign, "", "", peerOld, peerOld, 0, long.MaxValue) { Cents = peerNext });
                allowed = interestChecks++ < 2048 && CentTailInterestUnchanged(request, source, working, checks);
            }
            if (!allowed)
            {
                ApplySignedAmount(working[a], target.Sign, old / 100d);
                if (partner is not null) ApplySignedAmount(working[b], partner.Sign, peerOld / 100d);
                return false;
            }
            amounts[a] = next; changed.Add(a);
            if (partner is not null) { amounts[b] = peerNext; changed.Add(b); }
            if (target.HasCentTail) { tailUsage[old % 100]--; tailUsage[next % 100]++; }
            if (partner?.HasCentTail == true) { tailUsage[peerOld % 100]--; tailUsage[peerNext % 100]++; }
            lowCount = nextLowCount;
            balances.Add(first, last, delta);
            return true;
        }

        bool Repair(AdjacentAmountEntry target)
        {
            var peers = groups[target.Group];
            var position = Array.IndexOf(peers, target);
            // Nearby and evenly spread partners keep the pass bounded on large statements.
            var indices = new HashSet<int>();
            for (var offset = 1; offset <= 32; offset++)
            {
                if (position - offset >= 0) indices.Add(position - offset);
                if (position + offset < peers.Length) indices.Add(position + offset);
            }
            for (var sample = 0; sample < 32; sample++) indices.Add((int)((long)sample * (peers.Length - 1) / 31));
            var targetSource = rows[target.Index].ExtraFields;
            var partners = indices.Where(i => i != position).Select(i => peers[i])
                .OrderBy(peer => rows[peer.Index].ExtraFields.GetValueOrDefault(SourceKindField) == targetSource.GetValueOrDefault(SourceKindField)
                    && rows[peer.Index].ExtraFields.GetValueOrDefault(SourceIndexField) == targetSource.GetValueOrDefault(SourceIndexField) ? 0 : 1)
                .ThenBy(peer => peer.Time.Date == target.Time.Date ? 0 : 1).ThenBy(peer => Math.Abs(peer.Index - target.Index)).ToArray();
            // Preserve the existing amount distribution whenever a swap suffices.
            foreach (var partner in partners)
                if (TryChange(target, amounts[partner.Index], partner, amounts[target.Index])) return true;
            foreach (var partner in partners)
            {
                var step = Math.Max(target.Unit, partner.Unit);
                var current = amounts[target.Index];
                foreach (var next in PatternAmountCandidates(target, current, step, random, tailUsage))
                    if (TryChange(target, next, partner, amounts[partner.Index] - (next - current))) return true;
            }
            // Total-income mode treats ordinary variable spending as affordable
            // samples, not a hard exact expense budget. A smaller legal debit is
            // a last resort; explicit monthly budgets retain their exact totals.
            if (request.Config.SelectIndex == 0 && (referencePriority || target.Sign < 0 && ReferenceNaturalGenerationPolicy.AppliesTo(request.Bank, request.Config))
                && ResolveRecordSourceRule(request, rows[target.Index]) is GenerateReferenceRule)
                foreach (var next in PatternAmountCandidates(target, amounts[target.Index], target.Unit, random, tailUsage)
                             .Where(next => referencePriority || next < amounts[target.Index]))
                    if (TryChange(target, next)) return true;
            return false;
        }

        for (var pass = 0; pass < 3; pass++)
        {
            var progress = false;
            for (var i = 1; i < rows.Count; i++)
            {
                if (!Conflict(i)) continue;
                if (entries[i] is { } right && Repair(right) || entries[patterns.LeftIndex(amounts, i)] is { } left && Repair(left)) progress = true;
            }
            if (!progress) break;
        }
        var unresolved = Enumerable.Range(1, rows.Count - 1).FirstOrDefault(Conflict);
        if (unresolved > 0 && allowTimePermutation)
        {
            // A narrow range can be pinned at its maximum by the total budget.
            // Preserve those amounts and instead permute legally movable times.
            var intermediate = CreateAdjacentAmountResult(request, source, working.Select(row => row.Clone()).ToList(), changed, amounts);
            var permuted = PermuteAdjacentAmountTimes(request, intermediate);
            if (!ReferenceEquals(intermediate, permuted))
                return CompleteGeneratedAdjacentAmounts(request, permuted, allowTimePermutation: false);
        }
        if (unresolved > 0 && request.Config.SelectIndex == 0)
        {
            // Keep the established decreasing pass first. If rows are instead
            // pinned at their legal minima, increase one income and one expense
            // by the same legal step. Both directions preserve net cash, opening
            // and ending; explicit monthly totals never use this fallback.
            foreach (var direction in new[] { -1L, 1L })
            for (var pass = 0; pass < 3; pass++)
            {
                var progress = false;
                for (var i = 1; i < rows.Count; i++)
                {
                    if (!Conflict(i)) continue;
                    foreach (var target in new[] { entries[i], entries[patterns.LeftIndex(amounts, i)] }.OfType<AdjacentAmountEntry>())
                    {
                        var peers = entries.OfType<AdjacentAmountEntry>().Where(peer => peer.Sign != target.Sign && peer.Account == target.Account)
                            .OrderBy(peer => Math.Abs(peer.Index - target.Index)).Take(128).ToArray();
                        var repaired = false;
                        foreach (var peer in peers)
                        {
                            // Equal changes to an adjacent opposite pair leave
                            // that pair equal and causes a second repair. Prefer
                            // a nonadjacent partner that solves it in one move.
                            if (Math.Abs(peer.Index - target.Index) == 1 && amounts[peer.Index] == amounts[target.Index]) continue;
                            var step = Math.Max(target.Unit, peer.Unit);
                            for (var offset = 1; offset <= (step >= 100 ? 2 : 12); offset++)
                            {
                                var delta = direction * step * offset;
                                if (!TryChange(target, amounts[target.Index] + delta, peer, amounts[peer.Index] + delta)) continue;
                                repaired = true; break;
                            }
                            if (repaired) break;
                        }
                        if (!repaired) continue;
                        progress = true; break;
                    }
                }
                if (!progress) break;
            }
            unresolved = Enumerable.Range(1, rows.Count - 1).FirstOrDefault(Conflict);
        }
        if (unresolved > 0)
            return StampGeneratedAmountPatterns(request, CreateAdjacentAmountResult(request, source, working, changed, amounts));
        if (changed.Count == 0) return StampGeneratedAmountPatterns(request, source);
        var result = CreateAdjacentAmountResult(request, source, working, changed, amounts);
        // Number completion may reorder equal timestamps; verify both rules again.
        return StampGeneratedAmountPatterns(request, result);
    }

    private static FlowAutoGenerationResult CreateAdjacentAmountResult(FlowAutoGenerationRequest request,
        FlowAutoGenerationResult source, List<FlowRecord> working, IEnumerable<int> changed, long[] amounts)
    {
        foreach (var index in changed)
        {
            // Sampling strata are internal hints, not additional user bounds.
            // Keep later CCB correction passes aware of the accepted final value.
            foreach (var (key, lower) in new[] { (CcbStratumMin, true), (CcbStratumMax, false) })
                if (working[index].ExtraFields.TryGetValue(key, out var value) && TryParseDouble(value, out var bound))
                    working[index].ExtraFields[key] = (lower ? Math.Min(bound, amounts[index] / 100d) : Math.Max(bound, amounts[index] / 100d))
                        .ToString("R", CultureInfo.InvariantCulture);
            RefreshCashflowDerivedFields(request, working[index], source.Records[index].AccountTime!.Value, source.Records[index].TradeMoney ?? 0);
        }
        ApplyBalances(working, source.OpeningBalance);
        var result = CompleteAlipayResult(request, CreateAutomaticBalanceResult(working, source.OpeningBalance));
        return UsesReferencePriority(request.Config, working)
            ? EnsureAutomaticOpeningBalance(request, result, recalculateInterest: true) : result;
    }

    private static FlowAutoGenerationResult PermuteAdjacentAmountTimes(FlowAutoGenerationRequest request, FlowAutoGenerationResult source,
        bool preserveMonths = false)
    {
        var rows = source.Records.Select(row => row.Clone()).ToList();
        var amounts = rows.Select(row => AdjacentAmountCents(row.TradeMoney ?? 0)).ToArray();
        var entries = rows.Select((row, i) => CreateAdjacentAmountEntry(request, row, i)).ToArray();
        var mutable = entries.Select(entry => entry is not null).ToArray();
        var patterns = new AmountPatternIndex(entries);
        bool Conflict(int i) => patterns.Conflict(amounts, i);
        var balances = new CentTailBalanceHeadroom(rows.Select(row => row.Balance ?? source.OpeningBalance).ToArray());
        var start = request.Config.StartTime.Date; var end = NormalizeEndDate(request.Config.EndTime);
        if (end < start) (start, end) = (request.Config.EndTime.Date, NormalizeEndDate(request.Config.StartTime));
        var checks = 0; var changed = false;
        bool Allows(FlowRecord row, DateTime time)
        {
            var window = ResolveCashflowWindow(request, row, start, end);
            if (!window.Mutable || window.Rule is not { } rule || time < window.Start || time > window.End) return false;
            var from = Math.Clamp(rule.StartDay ?? 9, 0, 23); var to = Math.Clamp(rule.EndDay ?? 17, 0, 23);
            if (to < from) (from, to) = (to, from);
            return time.Hour >= from && time.Hour <= to && (rule is not GenerateReferenceRule
                || (ConstructionBankGenerationPolicy.AppliesTo(request.Bank)
                    ? ConstructionBankGenerationPolicy.IsReferenceDateAllowed(time, rule)
                    : ReferenceNaturalGenerationPolicy.IsReferenceDateAllowed(time, rule)));
        }
        bool TrySwap(int a, int b)
        {
            if (a == b || !mutable[b] || amounts[a] == amounts[b] || patterns.HasTail[a] != patterns.HasTail[b]) return false;
            var left = rows[a]; var right = rows[b]; var at = left.AccountTime!.Value; var bt = right.AccountTime!.Value;
            if (preserveMonths && BudgetMonth(at) != BudgetMonth(bt)) return false;
            if (request.Config.SelectIndex == 2 && !request.Config.MonthGenData
                    .All(period => (at >= period.StartTime && at <= NormalizeEndDate(period.EndTime))
                        == (bt >= period.StartTime && bt <= NormalizeEndDate(period.EndTime)))) return false;
            if (!Allows(left, bt) || !Allows(right, at)) return false;
            var edges = patterns.Edges(a, b);
            var before = edges.ToDictionary(i => i, Conflict);
            (amounts[a], amounts[b]) = (amounts[b], amounts[a]);
            var improves = edges.Count(Conflict) < before.Count(item => item.Value) && edges.All(i => before[i] || !Conflict(i));
            (amounts[a], amounts[b]) = (amounts[b], amounts[a]);
            if (!improves) return false;
            var delta = ((right.TradeMoney ?? 0) - (left.TradeMoney ?? 0)) * (a < b ? 1 : -1);
            var first = Math.Min(a, b); var last = Math.Max(a, b) - 1;
            if (delta < 0 && balances.Minimum(first, last) + delta < -.009d) return false;
            left.AccountTime = bt; right.AccountTime = at; (rows[a], rows[b]) = (right, left);
            if (at.Date != bt.Date && BankInterestPolicy.ShouldCalculate(request.Bank, request.BankUser))
            {
                // Signed position deltas describe a time swap even across the
                // two directions; the actual records retain their amounts/signs.
                var firstChange = new CentTailEntry(a, at, 1, "", "", 0, 0, 0, long.MaxValue)
                    { Cents = (long)Math.Round(((right.TradeMoney ?? 0) - (left.TradeMoney ?? 0)) * 100) };
                var secondChange = new CentTailEntry(b, bt, 1, "", "", 0, 0, 0, long.MaxValue) { Cents = -firstChange.Cents };
                if (checks++ >= 1024 || !CentTailInterestUnchanged(request, source, rows, [firstChange, secondChange]))
                {
                    (rows[a], rows[b]) = (left, right); left.AccountTime = at; right.AccountTime = bt; return false;
                }
            }
            RefreshCashflowDerivedFields(request, left, at, left.TradeMoney ?? 0);
            RefreshCashflowDerivedFields(request, right, bt, right.TradeMoney ?? 0);
            (amounts[a], amounts[b]) = (amounts[b], amounts[a]); balances.Add(first, last, delta);
            changed = true; return true;
        }
        for (var pass = 0; pass < 3; pass++)
        {
            var progress = false;
            for (var i = 1; i < rows.Count; i++)
            {
                if (!Conflict(i)) continue;
                foreach (var target in new[] { i, patterns.LeftIndex(amounts, i) })
                {
                    var partners = new HashSet<int>();
                    for (var offset = 1; offset <= 64; offset++)
                    { if (target - offset >= 0) partners.Add(target - offset); if (target + offset < rows.Count) partners.Add(target + offset); }
                    for (var sample = 0; sample < 64; sample++) partners.Add((int)((long)sample * (rows.Count - 1) / 63));
                    if (!partners.OrderBy(b => rows[b].AccountTime!.Value.Date == rows[target].AccountTime!.Value.Date ? 0 : 1)
                            .ThenBy(b => Math.Abs(b - target)).Any(b => TrySwap(target, b))) continue;
                    progress = true; break;
                }
            }
            if (!progress) break;
        }
        if (!changed) return source;
        ApplyBalances(rows, source.OpeningBalance);
        return CompleteAlipayResult(request, CreateAutomaticBalanceResult(rows, source.OpeningBalance));
    }

    private static AdjacentAmountEntry? CreateAdjacentAmountEntry(FlowAutoGenerationRequest request, FlowRecord row, int index)
    {
        if (row.Id != 0 || row.IsDocumentImported || IsSystemInterestRecord(row) || row.AccountTime is not { } time
            || row.BankId != request.Bank.Id || row.BankUserId != request.BankUser.Id
            || ResolveRecordSourceRule(request, row) is not { } rule || rule.MinMoney == rule.MaxMoney
            || !double.IsFinite(row.TradeMoney ?? double.NaN) || Math.Abs(row.TradeMoney ?? 0) < .009d) return null;
        var bounds = GetRecordAmountBounds(row);
        if (!double.IsFinite(bounds.Max) || bounds.Max > long.MaxValue / 400d) return null;
        var minimum = bounds.Min; var maximum = bounds.Max;
        var first = FindValidAmountAtOrAbove(minimum, maximum, bounds);
        if (!first.HasValue || !FindValidAmountAtOrAbove(first.Value + bounds.Unit, maximum, bounds).HasValue) return null;
        var sign = row.TradeMoney > 0 ? 1 : -1;
        // A monthly occurrence count does not impose a monthly amount budget
        // in total-income mode. One variable row per month must also
        // be able to exchange equal amounts across months.
        var totalOnly = request.Config.SelectIndex == 0;
        var period = totalOnly ? "" : time.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (request.Config.SelectIndex == 2) period += ":" + string.Join(',', request.Config.MonthGenData.Select((p, i) => (p, i))
            .Where(item => time >= item.p.StartTime && time <= NormalizeEndDate(item.p.EndTime)).Select(item => item.i));
        return new AdjacentAmountEntry(index, time, sign, row.Account ?? "", period, bounds,
            AdjacentAmountCents(minimum), AdjacentAmountCents(maximum), Math.Max(1, AdjacentAmountCents(bounds.Unit)));
    }

    private static IEnumerable<long> AdjacentAmountCandidates(AdjacentAmountEntry entry, long current, long step, Random random)
    {
        for (var offset = 1; offset <= 12; offset++)
        {
            yield return current + offset * step;
            yield return current - offset * step;
        }
        var first = (entry.Minimum + step - 1) / step; var last = entry.Maximum / step;
        if (last < first) yield break;
        // Interior/full-range choices also handle legal narrow precision bands.
        for (var sample = 0; sample < 16; sample++) yield return random.NextInt64(first, last + 1) * step;
    }

    private static long AdjacentAmountCents(double amount) => double.IsFinite(amount) && Math.Abs(amount) < long.MaxValue / 200d
        ? (long)Math.Round(Math.Abs(amount) * 100d, MidpointRounding.AwayFromZero) : 0;

    private sealed record AdjacentAmountEntry(int Index, DateTime Time, int Sign, string Account, string Period,
        AmountBounds Bounds, long Minimum, long Maximum, long Unit)
    {
        public bool HasCentTail => Bounds.Precision == 2 && Unit == 1;
        public (int Sign, string Account, string Period) Group => (Sign, Account, Period);
        public bool Accepts(long cents) => cents > 0 && cents >= Minimum && cents <= Maximum
            && IsReferenceNaturalLegalAmount(cents / 100d, Bounds);
    }
}
