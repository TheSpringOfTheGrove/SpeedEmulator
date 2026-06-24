using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SpeedEmulator.ViewModels;

namespace SpeedEmulator.Views;

public partial class FlowStatisticsWindow : Window
{
    private const int DefaultVisibleMonths = 12;
    private const int MinimumVisibleMonths = 3;
    private const double LeftPadding = 54;
    private const double RightPadding = 18;
    private const double TopPadding = 36;
    private const double BottomPadding = 34;
    private static readonly Brush IncomeFill = new SolidColorBrush(Color.FromRgb(51, 139, 255));
    private static readonly Brush ExpenseFill = new SolidColorBrush(Color.FromRgb(255, 70, 70));
    private static readonly Brush BarStroke = new SolidColorBrush(Color.FromRgb(0, 54, 255));
    private static readonly Brush GridLineBrush = new SolidColorBrush(Color.FromRgb(232, 232, 232));
    private int visibleStart;
    private int visibleCount;
    private int lastItemCount = -1;
    private bool updatingRangeBar;
    private bool showIncome = true;
    private bool showExpense = true;

    public FlowStatisticsWindow(FlowStatisticsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        UpdateLegendState();
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart();
    }

    private void ChartArea_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (DataContext is not FlowStatisticsViewModel viewModel || viewModel.Items.Count <= 1)
        {
            return;
        }

        EnsureVisibleRange(viewModel.Items.Count);
        var oldCount = visibleCount;
        var itemCount = viewModel.Items.Count;
        var minimum = Math.Min(MinimumVisibleMonths, itemCount);
        var nextCount = Math.Clamp(
            visibleCount + (e.Delta > 0 ? -1 : 1),
            minimum,
            itemCount);

        if (nextCount == oldCount)
        {
            return;
        }

        var center = visibleStart + oldCount / 2d;
        visibleCount = nextCount;
        visibleStart = ClampStart((int)Math.Round(center - visibleCount / 2d), itemCount);
        DrawChart();
        e.Handled = true;
    }

    private void ChartRangeBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingRangeBar || DataContext is not FlowStatisticsViewModel viewModel)
        {
            return;
        }

        visibleStart = ClampStart((int)Math.Round(e.NewValue), viewModel.Items.Count);
        DrawChart();
    }

    private void IncomeLegend_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        showIncome = !showIncome;
        UpdateLegendState();
        DrawChart();
    }

    private void ExpenseLegend_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        showExpense = !showExpense;
        UpdateLegendState();
        DrawChart();
    }

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        if (DataContext is not FlowStatisticsViewModel viewModel || !viewModel.HasData)
        {
            EmptyText.Visibility = Visibility.Visible;
            ChartRangeBar.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        EnsureVisibleRange(viewModel.Items.Count);
        UpdateRangeBar(viewModel.Items.Count);

        var width = Math.Max(ChartCanvas.ActualWidth, 1);
        var height = Math.Max(ChartCanvas.ActualHeight, 1);
        var plotWidth = Math.Max(width - LeftPadding - RightPadding, 1);
        var plotHeight = Math.Max(height - TopPadding - BottomPadding, 1);
        var visibleItems = viewModel.Items
            .Skip(visibleStart)
            .Take(visibleCount)
            .ToList();
        var axisMaximum = CalculateAxisMaximum(visibleItems.Max(item => Math.Max(
            showIncome ? item.Income : 0,
            showExpense ? item.Expense : 0)));

        DrawAxis(axisMaximum, plotWidth, plotHeight);
        DrawBars(visibleItems, axisMaximum, plotWidth, plotHeight);
    }

    private void DrawAxis(double axisMaximum, double plotWidth, double plotHeight)
    {
        const int tickCount = 5;
        for (var i = 0; i <= tickCount; i++)
        {
            var value = axisMaximum * i / tickCount;
            var y = TopPadding + plotHeight - plotHeight * i / tickCount;

            var line = new Line
            {
                X1 = LeftPadding,
                X2 = LeftPadding + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = GridLineBrush,
                StrokeThickness = 1
            };
            ChartCanvas.Children.Add(line);

            if (i == 0)
            {
                continue;
            }

            var label = new TextBlock
            {
                Text = FormatNumber(value),
                Foreground = Brushes.DimGray,
                FontSize = 14,
                Width = LeftPadding - 8,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 10);
            ChartCanvas.Children.Add(label);
        }
    }

    private void DrawBars(IReadOnlyList<FlowMonthlyStatistic> items, double axisMaximum, double plotWidth, double plotHeight)
    {
        var seriesCount = (showIncome ? 1 : 0) + (showExpense ? 1 : 0);
        if (seriesCount == 0)
        {
            return;
        }

        var groupWidth = plotWidth / items.Count;
        var barGap = seriesCount == 1 ? 0 : Math.Clamp(groupWidth * 0.08, 2, 8);
        var barWidth = seriesCount == 1
            ? Math.Clamp(groupWidth * 0.42, 5, 38)
            : Math.Clamp((groupWidth - barGap - 8) / 2, 4, 34);
        var monthStep = CalculateLabelStep(groupWidth, 58);
        var valueStep = CalculateLabelStep(groupWidth, 34);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var center = LeftPadding + groupWidth * i + groupWidth / 2;
            var firstLeft = seriesCount == 1
                ? center - barWidth / 2
                : center - barWidth - barGap / 2;
            var secondLeft = center + barGap / 2;
            var drawIndex = 0;
            var shouldShowLabel = ShouldShowIndexedLabel(i, items.Count, valueStep);
            var compactValueLabels = seriesCount > 1 && groupWidth < 58;
            var shouldShowIncomeValue = shouldShowLabel && (!compactValueLabels || item.Income >= item.Expense || !showExpense);
            var shouldShowExpenseValue = shouldShowLabel && (!compactValueLabels || item.Expense > item.Income || !showIncome);

            if (showIncome)
            {
                var left = drawIndex == 0 ? firstLeft : secondLeft;
                DrawBar(item.Income, axisMaximum, left, barWidth, plotHeight, IncomeFill);
                if (shouldShowIncomeValue)
                {
                    DrawValueLabel(item.Income, axisMaximum, left, barWidth, plotHeight);
                }

                drawIndex++;
            }

            if (showExpense)
            {
                var left = drawIndex == 0 ? firstLeft : secondLeft;
                DrawBar(item.Expense, axisMaximum, left, barWidth, plotHeight, ExpenseFill);
                if (shouldShowExpenseValue)
                {
                    DrawValueLabel(item.Expense, axisMaximum, left, barWidth, plotHeight);
                }
            }

            if (!ShouldShowIndexedLabel(i, items.Count, monthStep))
            {
                continue;
            }

            var monthLabel = new TextBlock
            {
                Text = item.Month,
                Foreground = Brushes.Black,
                FontSize = 11,
                Width = Math.Max(groupWidth, 54),
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(monthLabel, center - monthLabel.Width / 2);
            Canvas.SetTop(monthLabel, TopPadding + plotHeight + 8);
            ChartCanvas.Children.Add(monthLabel);
        }
    }

    private void UpdateLegendState()
    {
        IncomeLegend.Opacity = showIncome ? 1 : 0.35;
        ExpenseLegend.Opacity = showExpense ? 1 : 0.35;
        IncomeLegendText.Foreground = showIncome ? Brushes.Black : Brushes.Gray;
        ExpenseLegendText.Foreground = showExpense ? Brushes.Black : Brushes.Gray;
    }

    private void EnsureVisibleRange(int itemCount)
    {
        if (itemCount <= 0)
        {
            visibleStart = 0;
            visibleCount = 0;
            lastItemCount = itemCount;
            return;
        }

        if (itemCount != lastItemCount || visibleCount <= 0)
        {
            visibleStart = 0;
            visibleCount = Math.Min(DefaultVisibleMonths, itemCount);
            lastItemCount = itemCount;
        }

        visibleCount = Math.Clamp(visibleCount, Math.Min(MinimumVisibleMonths, itemCount), itemCount);
        visibleStart = ClampStart(visibleStart, itemCount);
    }

    private void UpdateRangeBar(int itemCount)
    {
        updatingRangeBar = true;
        try
        {
            ChartRangeBar.Visibility = itemCount > visibleCount ? Visibility.Visible : Visibility.Collapsed;
            ChartRangeBar.Maximum = Math.Max(0, itemCount - visibleCount);
            ChartRangeBar.ViewportSize = visibleCount;
            ChartRangeBar.LargeChange = Math.Max(1, visibleCount);
            ChartRangeBar.SmallChange = 1;
            ChartRangeBar.Value = visibleStart;
        }
        finally
        {
            updatingRangeBar = false;
        }
    }

    private int ClampStart(int start, int itemCount)
    {
        return Math.Clamp(start, 0, Math.Max(0, itemCount - visibleCount));
    }

    private void DrawBar(double value, double axisMaximum, double left, double width, double plotHeight, Brush fill)
    {
        var barHeight = axisMaximum <= 0 ? 0 : Math.Max(0, plotHeight * value / axisMaximum);
        var rect = new Rectangle
        {
            Width = width,
            Height = barHeight,
            Fill = fill,
            Stroke = BarStroke,
            StrokeThickness = 1,
            RadiusX = 3,
            RadiusY = 3
        };

        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, TopPadding + plotHeight - barHeight);
        ChartCanvas.Children.Add(rect);
    }

    private void DrawValueLabel(double value, double axisMaximum, double left, double barWidth, double plotHeight)
    {
        if (value <= 0)
        {
            return;
        }

        var barHeight = axisMaximum <= 0 ? 0 : Math.Max(0, plotHeight * value / axisMaximum);
        var label = new TextBlock
        {
            Text = FormatNumber(value),
            Foreground = Brushes.DimGray,
            FontSize = 10,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(15),
            Width = 60,
            TextAlignment = TextAlignment.Center
        };

        Canvas.SetLeft(label, left + barWidth / 2 - label.Width / 2);
        Canvas.SetTop(label, TopPadding + plotHeight - barHeight - 22);
        ChartCanvas.Children.Add(label);
    }

    private static string FormatNumber(double value)
    {
        return Math.Abs(value % 1) < 0.005
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static int CalculateLabelStep(double groupWidth, double minimumLabelWidth)
    {
        return Math.Max(1, (int)Math.Ceiling(minimumLabelWidth / Math.Max(groupWidth, 1)));
    }

    private static bool ShouldShowIndexedLabel(int index, int count, int step)
    {
        return index == 0 || index == count - 1 || index % step == 0;
    }

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
