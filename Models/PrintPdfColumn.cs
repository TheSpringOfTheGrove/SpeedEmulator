namespace SpeedEmulator.Models;

public sealed class PrintPdfColumn
{
    public string Name { get; set; } = string.Empty;

    public string Field { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public double Width { get; set; } = 100;

    public double LineHeight { get; set; } = 18;

    public double FontSize { get; set; } = 8;

    public string FontFamily { get; set; } = string.Empty;

    public PrintPdfColumn Clone()
    {
        return new PrintPdfColumn
        {
            Name = Name,
            Field = Field,
            Type = Type,
            Width = Width,
            LineHeight = LineHeight,
            FontSize = FontSize,
            FontFamily = FontFamily
        };
    }
}

