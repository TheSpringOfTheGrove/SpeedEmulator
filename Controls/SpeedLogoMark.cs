using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpeedEmulator.Controls;

public sealed class SpeedLogoMark : Control
{
    private const double CanvasSize = 150d;

    protected override Size MeasureOverride(Size constraint)
    {
        return new Size(CanvasSize, CanvasSize);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var scale = Math.Min(RenderSize.Width / CanvasSize, RenderSize.Height / CanvasSize);
        var offsetX = (RenderSize.Width - CanvasSize * scale) / 2;
        var offsetY = (RenderSize.Height - CanvasSize * scale) / 2;

        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));

        DrawBackground(drawingContext);
        DrawLogoText(drawingContext, "极", new Rect(5, 13, 140, 61));
        DrawLogoText(drawingContext, "速", new Rect(5, 76, 140, 61));

        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static void DrawBackground(DrawingContext drawingContext)
    {
        var background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            [
                new GradientStop(Color.FromRgb(10, 38, 224), 0),
                new GradientStop(Color.FromRgb(2, 20, 199), 1)
            ]
        };

        drawingContext.DrawRectangle(background, null, new Rect(0, 0, CanvasSize, CanvasSize));
    }

    private static void DrawLogoText(DrawingContext drawingContext, string text, Rect target)
    {
        var geometry = BuildFittedTextGeometry(text, target);
        var whitePen = new Pen(Brushes.White, 7.8)
        {
            LineJoin = PenLineJoin.Round
        };
        var redBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0.28, 0),
            EndPoint = new Point(0.75, 1),
            GradientStops =
            [
                new GradientStop(Color.FromRgb(255, 74, 52), 0),
                new GradientStop(Color.FromRgb(244, 15, 15), 0.55),
                new GradientStop(Color.FromRgb(194, 0, 0), 1)
            ]
        };

        drawingContext.DrawGeometry(Brushes.White, whitePen, geometry);
        drawingContext.DrawGeometry(redBrush, null, geometry);
    }

    private static Geometry BuildFittedTextGeometry(string text, Rect target)
    {
        const double widthScale = 1.36;
        const double skewAngle = -14d;

        var typeface = new Typeface(
            new FontFamily("HYZhongHei, SimHei, Microsoft YaHei UI"),
            FontStyles.Italic,
            FontWeights.Black,
            FontStretches.Expanded);

        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            100,
            Brushes.Black,
            1.0);

        var geometry = formattedText.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;

        var styleTransform = new TransformGroup();
        styleTransform.Children.Add(new TranslateTransform(-bounds.Left, -bounds.Top));
        styleTransform.Children.Add(new ScaleTransform(widthScale, 1));
        styleTransform.Children.Add(new SkewTransform(skewAngle, 0));

        var styledGeometry = geometry.Clone();
        styledGeometry.Transform = styleTransform;
        var styledBounds = styledGeometry.Bounds;
        var scale = Math.Min(target.Width / styledBounds.Width, target.Height / styledBounds.Height);

        var finalTransform = new TransformGroup();
        finalTransform.Children.Add(styleTransform);
        finalTransform.Children.Add(new ScaleTransform(scale, scale));
        finalTransform.Children.Add(new TranslateTransform(
            target.Left + (target.Width - styledBounds.Width * scale) / 2 - styledBounds.Left * scale,
            target.Top + (target.Height - styledBounds.Height * scale) / 2 - styledBounds.Top * scale));
        geometry.Transform = finalTransform;

        return geometry;
    }
}
