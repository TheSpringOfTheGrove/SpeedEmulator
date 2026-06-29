namespace SpeedEmulator.Models;

public sealed class PrintPdfConfig
{
    public string Name { get; set; } = string.Empty;

    public string Desc { get; set; } = string.Empty;

    public int RowCount { get; set; } = 28;

    public double MarginLeft { get; set; } = 18;

    public double MarginTop { get; set; } = 16;

    public double MarginRight { get; set; } = 18;

    public double MarginBottom { get; set; } = 16;

    public string FontFamily { get; set; } = "Microsoft YaHei";

    public double TabSize { get; set; }

    public double HeaderFontSize { get; set; } = 9;

    public double BodyFontSize { get; set; } = 8;

    public double ColumnMinHeight { get; set; } = 18;

    public bool Descending { get; set; }

    public double FirstPageOffset { get; set; }

    public double SealLeft { get; set; }

    public double SealTop { get; set; }

    public double SealRight { get; set; } = 70;

    public double SealBottom { get; set; }

    public double SealWidth { get; set; } = 110;

    public List<PrintPdfColumn> Columns { get; set; } = [];

    public PrintPdfConfig Clone()
    {
        return new PrintPdfConfig
        {
            Name = Name,
            Desc = Desc,
            RowCount = RowCount,
            MarginLeft = MarginLeft,
            MarginTop = MarginTop,
            MarginRight = MarginRight,
            MarginBottom = MarginBottom,
            FontFamily = FontFamily,
            TabSize = TabSize,
            HeaderFontSize = HeaderFontSize,
            BodyFontSize = BodyFontSize,
            ColumnMinHeight = ColumnMinHeight,
            Descending = Descending,
            FirstPageOffset = FirstPageOffset,
            SealLeft = SealLeft,
            SealTop = SealTop,
            SealRight = SealRight,
            SealBottom = SealBottom,
            SealWidth = SealWidth,
            Columns = Columns.Select(item => item.Clone()).ToList()
        };
    }
}
