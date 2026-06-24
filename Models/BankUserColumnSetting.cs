using SpeedEmulator.Infrastructure;

namespace SpeedEmulator.Models;

public sealed class BankUserColumnSetting : ObservableObject
{
    private int width = 100;
    private int order;
    private bool show = true;

    public string Name { get; set; } = string.Empty;

    public string Field { get; set; } = string.Empty;

    public string Type { get; set; } = "Text";

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

    public static BankUserColumnSetting FromColumn(ColumnDefinition column)
    {
        return new BankUserColumnSetting
        {
            Name = column.Name ?? string.Empty,
            Field = column.Field ?? string.Empty,
            Type = column.Type ?? "Text",
            Width = column.Width <= 0 ? 100 : column.Width,
            Order = column.Order,
            Show = column.Show
        };
    }

    public BankUserColumnSetting Clone()
    {
        return new BankUserColumnSetting
        {
            Name = Name,
            Field = Field,
            Type = Type,
            Width = Width,
            Order = Order,
            Show = Show
        };
    }

    public void Normalize()
    {
        if (Width <= 0)
        {
            Width = 100;
        }
    }
}
