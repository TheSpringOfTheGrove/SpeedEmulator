using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed class PrintRenderContext
{
    public required Bank Bank { get; init; }

    public required BankUser BankUser { get; init; }

    public required IReadOnlyList<FlowRecord> Records { get; init; }

    public required PrintTemplate Template { get; init; }

    public PrintRenderContext ApplyTemplateRecordOrder()
    {
        var orderedRecords = Template.Config.Descending
            ? Records
                .OrderByDescending(record => record.AccountTime ?? DateTime.MinValue)
                .ThenByDescending(record => record.Index)
                .ToArray()
            : Records
                .OrderBy(record => record.AccountTime ?? DateTime.MinValue)
                .ThenBy(record => record.Index)
                .ToArray();

        return new PrintRenderContext
        {
            Bank = Bank,
            BankUser = BankUser,
            Records = orderedRecords,
            Template = Template
        };
    }
}
