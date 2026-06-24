using SpeedEmulator.Infrastructure;

namespace SpeedEmulator.Models;

public sealed class ColumnDefinition : ObservableObject
{
    private int width = 100;
    private int order;
    private bool show = true;

    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? Field { get; init; }

    public int Width
    {
        get => width;
        set => SetProperty(ref width, value);
    }

    public int Order
    {
        get => order;
        set => SetProperty(ref order, value);
    }

    public bool Show
    {
        get => show;
        set => SetProperty(ref show, value);
    }
}
