using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;

namespace SpeedEmulator.ViewModels;

public sealed class FlowStatisticsViewModel : ObservableObject
{
    public FlowStatisticsViewModel(IEnumerable<FlowRecord> records)
    {
        Items = records
            .Select((record, sourceOrder) => new { Record = record, SourceOrder = sourceOrder })
            .Where(item => item.Record.AccountTime.HasValue)
            .GroupBy(item => new DateTime(
                item.Record.AccountTime!.Value.Year,
                item.Record.AccountTime.Value.Month,
                1))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var lastRecord = group
                    .OrderBy(item => item.Record.AccountTime)
                    .ThenBy(item => item.SourceOrder)
                    .Last()
                    .Record;
                return new FlowMonthlyStatistic(
                    group.Key.ToString("yyyy年MM"),
                    Math.Round(group.Where(item => item.Record.TradeMoney > 0).Sum(item => item.Record.TradeMoney ?? 0), 2),
                    Math.Round(group.Where(item => item.Record.TradeMoney < 0).Sum(item => 0 - (item.Record.TradeMoney ?? 0)), 2),
                    Math.Round(lastRecord.Balance ?? lastRecord.BalanceAmount ?? 0, 2));
            })
            .ToList();

        var maxValue = Items.Count == 0
            ? 1
            : Items.Max(item => Math.Max(item.Income, item.Expense));
        AxisMaximum = CalculateAxisMaximum(maxValue);
    }

    public string WindowTitle => "水晶报表";

    public IReadOnlyList<FlowMonthlyStatistic> Items { get; }

    public double AxisMaximum { get; }

    public bool HasData => Items.Count > 0;

    private static double CalculateAxisMaximum(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized <= 1
            ? 1
            : normalized <= 2
                ? 2
                : normalized <= 5
                    ? 5
                    : 10;

        return nice * magnitude;
    }
}

public sealed record FlowMonthlyStatistic(string Month, double Income, double Expense, double Balance);
