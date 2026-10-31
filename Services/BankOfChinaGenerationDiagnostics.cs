using System.Globalization;
using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

internal static class BankOfChinaGenerationDiagnostics
{
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly object FileLock = new();

    internal static Scope? Begin(string operation, FlowAutoGenerationRequest request)
    {
        if (!BankOfChinaGenerationBudget.AppliesTo(request.Bank)) return null;
        var directory = Environment.GetEnvironmentVariable("SPEEDEMULATOR_BOC_LOG_DIR");
        // Test/embedded hosts opt in with an isolated directory. Normal desktop
        // runs persist diagnostics without writing any customer identity fields.
        if (string.IsNullOrWhiteSpace(directory) && System.Windows.Application.Current is null) return null;
        try
        {
            directory = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeedEmulator", "logs")
                : Path.GetFullPath(directory);
            var scope = new Scope(Path.Combine(directory, "generation-boc.log"), Current.Value);
            Current.Value = scope;
            Write("start", new { operation, BankId = request.Bank.Id, UserId = request.BankUser.Id,
                request.Config.StartTime, request.Config.EndTime, request.Config.AllInMoney, request.Config.LastMoney,
                request.Config.OpeningBalance, request.Config.SelectIndex,
                SelectedReferences = request.References.Count(rule => rule.IsCheck), SelectedFixedItems = request.ConstItems.Count(rule => rule.IsCheck) });
            return scope;
        }
        catch { return null; }
    }

    internal static void Seed(int seed) => Write("seed", new { seed });
    internal static void Stage(string stage, IEnumerable<FlowRecord> records)
    {
        if (Current.Value is not null) Write(stage, Analyze(records));
    }
    internal static void Closing(string phase, IEnumerable<FlowRecord> records, bool isIncome, double target, double before, double after)
    {
        if (Current.Value is not null) Write("closing", new { phase, isIncome, target, before, after,
            remaining = Math.Round(target - after, 2), amounts = Analyze(records) });
    }
    internal static void Result(FlowAutoGenerationResult result)
    {
        if (Current.Value is null) return;
        var amounts = Analyze(result.Records);
        Write(amounts.Concentrated ? "result-tail-concentrated" : "result", new { result.OpeningBalance, result.IncomeTotal, result.ExpenseTotal,
            result.FinalBalance, result.MinimumBalance, amounts });
    }
    internal static void Failure(Exception exception) => Write("failure", new { type = exception.GetType().Name });

    internal static TailStatistics Analyze(IEnumerable<FlowRecord> records)
    {
        var count = 0; var p2 = 0; var atMinimum = 0; var total = 0d; var minimumTotal = 0d;
        var tails = new int[100];
        var dayCounts = new Dictionary<(DateTime Day, int Sign), int>();
        var dayTails = new Dictionary<(DateTime Day, int Sign, int Tail), int>();
        foreach (var row in records)
        {
            count++;
            if (row.IsDocumentImported || FlowGeneratedRowKinds.IsSystemInterest(row)
                || row.ExtraFields.GetValueOrDefault("__GeneratedSourceKind") != "Reference"
                || row.ExtraFields.GetValueOrDefault("__GeneratedAmountPrecision") != "2") continue;
            if (!double.TryParse(row.ExtraFields.GetValueOrDefault("__GeneratedAmountMin"), NumberStyles.Float, CultureInfo.InvariantCulture, out var min)
                || !double.TryParse(row.ExtraFields.GetValueOrDefault("__GeneratedAmountMax"), NumberStyles.Float, CultureInfo.InvariantCulture, out var max)
                || max <= min + 0.009d || !double.IsFinite(row.TradeMoney ?? double.NaN)) continue;
            var amount = Math.Abs(row.TradeMoney!.Value);
            p2++; total += amount; minimumTotal += min;
            if (Math.Abs(amount - min) < 0.000001d) atMinimum++;
            var tail = (int)Math.Round((amount % 1d) * 100, MidpointRounding.AwayFromZero) % 100;
            tails[tail]++;
            if (row.AccountTime is { } time)
            {
                var sign = Math.Sign(row.TradeMoney.Value);
                var day = (time.Date, sign);
                dayCounts[day] = dayCounts.GetValueOrDefault(day) + 1;
                var key = (time.Date, sign, tail);
                dayTails[key] = dayTails.GetValueOrDefault(key) + 1;
            }
        }
        var maximum = tails.Max();
        var maximumOnDay = dayTails.Values.DefaultIfEmpty().Max();
        var dayConcentrated = dayTails.Any(pair => pair.Value > Math.Max(2,
            Math.Ceiling(dayCounts[(pair.Key.Day, pair.Key.Sign)] / 81d) + 1));
        return new(count, p2, tails[11], atMinimum, tails.Count(value => value > 0), maximum,
            maximumOnDay, Math.Round(total - minimumTotal, 2), dayConcentrated
            || p2 >= 20 && maximum > Math.Ceiling(p2 / 81d) + 3);
    }

    private static void Write(string stage, object data)
    {
        var scope = Current.Value;
        if (scope is null) return;
        try
        {
            var line = JsonSerializer.Serialize(new { time = DateTimeOffset.Now, runId = scope.RunId, stage, data }) + Environment.NewLine;
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scope.Path)!);
                if (File.Exists(scope.Path) && new FileInfo(scope.Path).Length > 4 * 1024 * 1024)
                    File.Move(scope.Path, scope.Path + ".previous", overwrite: true);
                File.AppendAllText(scope.Path, line);
            }
        }
        catch { /* Diagnostics must never replace a generation result or error. */ }
    }

    internal sealed record TailStatistics(int Rows, int TwoDecimalRows, int Tail11, int AtMinimum, int DistinctTails,
        int MaximumTailCount, int MaximumTailOnSameDay, double BudgetAboveMinimum, bool Concentrated);

    internal sealed class Scope(string path, Scope? previous) : IDisposable
    {
        internal string RunId { get; } = Guid.NewGuid().ToString("N");
        internal string Path { get; } = path;
        public void Dispose() => Current.Value = previous;
    }
}
