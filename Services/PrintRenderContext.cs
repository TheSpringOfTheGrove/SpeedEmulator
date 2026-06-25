using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed class PrintRenderContext
{
    public required Bank Bank { get; init; }

    public required BankUser BankUser { get; init; }

    public required IReadOnlyList<FlowRecord> Records { get; init; }

    public required PrintTemplate Template { get; init; }
}

