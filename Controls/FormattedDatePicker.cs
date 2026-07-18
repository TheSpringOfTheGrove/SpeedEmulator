using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SpeedEmulator.Controls;

public sealed class FormattedDatePicker : UserControl
{
    private enum DateTimeStepPart
    {
        Year,
        Month,
        Day,
        Hour,
        Minute,
        Second
    }

    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(
            nameof(SelectedDate),
            typeof(DateTime?),
            typeof(FormattedDatePicker),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDateChanged));

    public static readonly DependencyProperty DisplayFormatProperty =
        DependencyProperty.Register(
            nameof(DisplayFormat),
            typeof(string),
            typeof(FormattedDatePicker),
            new PropertyMetadata("yyyy年MM月dd日 HH:mm:ss", OnDisplayFormatChanged));

    private static readonly string[] ParseFormats =
    [
        "yyyy年MM月dd日 HH:mm:ss",
        "yyyy年M月d日 H:mm:ss",
        "yyyy/M/d H:mm:ss",
        "yyyy/M/d",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd"
    ];

    private readonly TextBox textBox;
    private readonly TextBox hourBox;
    private readonly TextBox minuteBox;
    private readonly TextBox secondBox;
    private readonly Popup popup;
    private readonly System.Windows.Controls.Calendar calendar;
    private bool isSyncing;
    private bool isApplying;

    public FormattedDatePicker()
    {
        Focusable = true;

        textBox = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(4, 2, 4, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        textBox.LostKeyboardFocus += (_, _) => CommitText();
        textBox.KeyDown += TextBox_KeyDown;
        textBox.PreviewMouseWheel += TextBox_PreviewMouseWheel;

        var spinner = CreateSpinButtons();
        var dropDownButton = CreateDropDownButton();

        calendar = new System.Windows.Controls.Calendar();
        calendar.SelectedDatesChanged += Calendar_SelectedDatesChanged;

        hourBox = CreateTimeBox(DateTimeStepPart.Hour);
        minuteBox = CreateTimeBox(DateTimeStepPart.Minute);
        secondBox = CreateTimeBox(DateTimeStepPart.Second);

        var popupPanel = new StackPanel();
        popupPanel.Children.Add(calendar);
        popupPanel.Children.Add(CreateTimeEditor());

        popup = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
            PlacementTarget = this,
            StaysOpen = true,
            Child = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                BorderThickness = new Thickness(1),
                Child = popupPanel
            }
        };

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(197, 203, 205)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            CornerRadius = new CornerRadius(4)
        };
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(BorderBrush)) { Source = this });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(BorderThickness)) { Source = this });
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { Source = this });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(textBox);
        Grid.SetColumn(spinner, 1);
        grid.Children.Add(spinner);
        Grid.SetColumn(dropDownButton, 2);
        grid.Children.Add(dropDownButton);

        var host = new Grid();
        border.Child = grid;
        host.Children.Add(border);
        host.Children.Add(popup);
        Content = host;

        Loaded += (_, _) =>
        {
            EnsureDefaultDateTime();
            RefreshText();
        };
        Height = 28;
    }

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    public string DisplayFormat
    {
        get => (string)GetValue(DisplayFormatProperty);
        set => SetValue(DisplayFormatProperty, value);
    }

    public bool IsDropDownOpen => popup.IsOpen;

    private static void OnSelectedDateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is FormattedDatePicker picker)
        {
            picker.RefreshText();
        }
    }

    private static void OnDisplayFormatChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is FormattedDatePicker picker)
        {
            picker.RefreshText();
        }
    }

    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            TogglePopup();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            StepSelectedPart(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            StepSelectedPart(-1);
            e.Handled = true;
        }
    }

    private void TextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        StepSelectedPart(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Calendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isSyncing || calendar.SelectedDate is not { } selectedDate)
        {
            return;
        }

        var time = SelectedDate?.TimeOfDay ?? DateTime.Now.TimeOfDay;
        SelectedDate = selectedDate.Date + time;
        SyncTimeBoxes();
        RefreshText();
    }

    private void TogglePopup()
    {
        CommitText();
        EnsureDefaultDateTime();

        isSyncing = true;
        calendar.SelectedDate = SelectedDate?.Date;
        calendar.DisplayDate = SelectedDate?.Date ?? DateTime.Today;
        isSyncing = false;
        SyncTimeBoxes();

        popup.IsOpen = !popup.IsOpen;
    }

    private void CommitText()
    {
        if (string.IsNullOrWhiteSpace(textBox.Text))
        {
            SelectedDate = TrimToSecond(DateTime.Now);
            RefreshText();
            return;
        }

        if (TryParseDateTime(textBox.Text, out var parsed))
        {
            SelectedDate = TrimToSecond(parsed);
        }

        RefreshText();
    }

    private void RefreshText()
    {
        textBox.Text = SelectedDate?.ToString(DisplayFormat, CultureInfo.GetCultureInfo("zh-CN")) ?? string.Empty;
        SyncTimeBoxes();
    }

    private void EnsureDefaultDateTime()
    {
        if (SelectedDate is not null)
        {
            return;
        }

        SelectedDate = TrimToSecond(DateTime.Now);
        BindingOperations.GetBindingExpression(this, SelectedDateProperty)?.UpdateSource();
    }

    private Grid CreateSpinButtons()
    {
        var panel = new Grid
        {
            Width = 17,
            Background = new SolidColorBrush(Color.FromRgb(238, 238, 238))
        };
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var upButton = CreateStepButton(true);
        upButton.Click += (_, _) => StepSelectedPart(1);
        panel.Children.Add(upButton);

        var downButton = CreateStepButton(false);
        downButton.Click += (_, _) => StepSelectedPart(-1);
        Grid.SetRow(downButton, 1);
        panel.Children.Add(downButton);

        return panel;
    }

    private Button CreateDropDownButton()
    {
        var button = new Button
        {
            Content = CreateArrowIcon(false),
            Width = 18,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(160, 168, 172)),
            Background = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
            ToolTip = "选择日期"
        };
        button.Click += (_, _) => TogglePopup();
        return button;
    }

    private static RepeatButton CreateStepButton(bool isUp)
    {
        return new RepeatButton
        {
            Content = CreateArrowIcon(isUp),
            Delay = 350,
            Interval = 80,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1, isUp ? 0 : 1, 0, isUp ? 0.5 : 0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(160, 168, 172)),
            Background = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
            Focusable = false,
            IsTabStop = false
        };
    }

    private void StepSelectedPart(int direction)
    {
        if (hourBox.IsKeyboardFocusWithin)
        {
            StepTimeBoxPart(DateTimeStepPart.Hour, direction, hourBox);
            return;
        }

        if (minuteBox.IsKeyboardFocusWithin)
        {
            StepTimeBoxPart(DateTimeStepPart.Minute, direction, minuteBox);
            return;
        }

        if (secondBox.IsKeyboardFocusWithin)
        {
            StepTimeBoxPart(DateTimeStepPart.Second, direction, secondBox);
            return;
        }

        var part = GetStepPartFromTextSelection();
        CommitText();
        StepDateTimePart(part, direction);
        textBox.Focus();
        SelectTextGroup(part);
    }

    private DateTimeStepPart GetStepPartFromTextSelection()
    {
        var caret = Math.Clamp(
            textBox.SelectionLength > 0 ? textBox.SelectionStart : textBox.CaretIndex,
            0,
            (textBox.Text ?? string.Empty).Length);

        return GetStepPartFromCaret(caret);
    }

    private void StepTimeBoxPart(DateTimeStepPart part, int direction, TextBox box)
    {
        CommitTimeBoxes();
        StepDateTimePart(part, direction);
        box.Focus();
        box.SelectAll();
    }

    private void StepDateTimePart(DateTimeStepPart part, int direction)
    {
        var current = SelectedDate ?? TrimToSecond(DateTime.Now);
        var next = part switch
        {
            DateTimeStepPart.Year => current.AddYears(direction),
            DateTimeStepPart.Month => current.AddMonths(direction),
            DateTimeStepPart.Day => current.AddDays(direction),
            DateTimeStepPart.Hour => current.AddHours(direction),
            DateTimeStepPart.Minute => current.AddMinutes(direction),
            DateTimeStepPart.Second => current.AddSeconds(direction),
            _ => current
        };

        SelectedDate = TrimToSecond(next);
        RefreshText();
        BindingOperations.GetBindingExpression(this, SelectedDateProperty)?.UpdateSource();
    }

    private DateTimeStepPart GetStepPartFromCaret(int caret)
    {
        var text = textBox.Text ?? string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        var groupIndex = -1;

        for (var index = 0; index < text.Length;)
        {
            if (!char.IsDigit(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }

            groupIndex++;
            if (caret >= start && caret <= index)
            {
                return GroupIndexToStepPart(groupIndex);
            }
        }

        return DateTimeStepPart.Day;
    }

    private static DateTimeStepPart GroupIndexToStepPart(int groupIndex)
    {
        return groupIndex switch
        {
            0 => DateTimeStepPart.Year,
            1 => DateTimeStepPart.Month,
            2 => DateTimeStepPart.Day,
            3 => DateTimeStepPart.Hour,
            4 => DateTimeStepPart.Minute,
            5 => DateTimeStepPart.Second,
            _ => DateTimeStepPart.Day
        };
    }

    private static int StepPartToGroupIndex(DateTimeStepPart part)
    {
        return part switch
        {
            DateTimeStepPart.Year => 0,
            DateTimeStepPart.Month => 1,
            DateTimeStepPart.Day => 2,
            DateTimeStepPart.Hour => 3,
            DateTimeStepPart.Minute => 4,
            DateTimeStepPart.Second => 5,
            _ => 2
        };
    }

    private void SelectTextGroup(DateTimeStepPart part)
    {
        var targetGroupIndex = StepPartToGroupIndex(part);
        var text = textBox.Text ?? string.Empty;
        var groupIndex = -1;

        for (var index = 0; index < text.Length;)
        {
            if (!char.IsDigit(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }

            groupIndex++;
            if (groupIndex == targetGroupIndex)
            {
                textBox.Select(start, index - start);
                return;
            }
        }

        textBox.CaretIndex = Math.Min(text.Length, textBox.CaretIndex);
    }

    private UIElement CreateTimeEditor()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 4, 8, 8),
            VerticalAlignment = VerticalAlignment.Center
        };

        panel.Children.Add(new TextBlock
        {
            Text = "时间",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(35, 35, 35))
        });

        panel.Children.Add(hourBox);
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(minuteBox);
        panel.Children.Add(CreateSeparator());
        panel.Children.Add(secondBox);

        var applyButton = new Button
        {
            Content = "确定",
            Height = 24,
            MinWidth = 46,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(6, 0, 6, 0),
            ClickMode = ClickMode.Press,
            Focusable = false,
            IsTabStop = false
        };
        applyButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            ApplyAndClose();
            e.Handled = true;
        };
        applyButton.Click += (_, _) => ApplyAndClose();
        panel.Children.Add(applyButton);

        return panel;
    }

    private static TextBlock CreateSeparator()
    {
        return new TextBlock
        {
            Text = ":",
            Margin = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private TextBox CreateTimeBox(DateTimeStepPart part)
    {
        var box = new TextBox
        {
            Width = 28,
            Height = 24,
            MaxLength = 2,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };

        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.PreviewMouseWheel += (_, e) =>
        {
            StepTimeBoxPart(part, e.Delta > 0 ? 1 : -1, box);
            e.Handled = true;
        };
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (box.IsKeyboardFocusWithin)
            {
                return;
            }

            e.Handled = true;
            box.Focus();
            box.SelectAll();
        };
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(box, OnTimeBoxPaste);
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!isApplying)
            {
                CommitTimeBoxes();
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyAndClose();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                StepTimeBoxPart(part, 1, box);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                StepTimeBoxPart(part, -1, box);
                e.Handled = true;
            }
        };

        return box;
    }

    private static void OnTimeBoxPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text)
            && e.DataObject.GetData(DataFormats.Text) is string text
            && text.All(char.IsDigit))
        {
            return;
        }

        e.CancelCommand();
    }

    private void SyncTimeBoxes()
    {
        if (hourBox is null || minuteBox is null || secondBox is null)
        {
            return;
        }

        var value = SelectedDate ?? DateTime.Now;
        hourBox.Text = value.Hour.ToString("00", CultureInfo.InvariantCulture);
        minuteBox.Text = value.Minute.ToString("00", CultureInfo.InvariantCulture);
        secondBox.Text = value.Second.ToString("00", CultureInfo.InvariantCulture);
    }

    private void CommitTimeBoxes()
    {
        var current = SelectedDate ?? DateTime.Now;
        var hour = Clamp(ParsePart(hourBox.Text), 0, 23);
        var minute = Clamp(ParsePart(minuteBox.Text), 0, 59);
        var second = Clamp(ParsePart(secondBox.Text), 0, 59);

        SelectedDate = TrimToSecond(current.Date.Add(new TimeSpan(hour, minute, second)));
        RefreshText();
    }

    private void ApplyAndClose()
    {
        if (isApplying)
        {
            return;
        }

        isApplying = true;
        CommitTimeBoxes();
        BindingOperations.GetBindingExpression(this, SelectedDateProperty)?.UpdateSource();
        popup.IsOpen = false;
        Dispatcher.BeginInvoke(new Action(() => isApplying = false));
    }

    private static int ParsePart(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int Clamp(int value, int min, int max)
    {
        return Math.Min(Math.Max(value, min), max);
    }

    private static bool TryParseDateTime(string value, out DateTime parsed)
    {
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        return DateTime.TryParseExact(value, ParseFormats, culture, DateTimeStyles.None, out parsed)
            || DateTime.TryParse(value, culture, DateTimeStyles.None, out parsed);
    }

    private static DateTime TrimToSecond(DateTime value)
    {
        return new DateTime(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Kind);
    }

    private static UIElement CreateArrowIcon(bool isUp)
    {
        var points = isUp
            ? new PointCollection([new Point(4, 2), new Point(8, 7), new Point(0, 7)])
            : new PointCollection([new Point(0, 2), new Point(8, 2), new Point(4, 7)]);

        return new Polygon
        {
            Points = points,
            Fill = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
            Width = 8,
            Height = 9,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static UIElement CreateCalendarIcon()
    {
        var borderBrush = new SolidColorBrush(Color.FromRgb(58, 82, 99));
        var accentBrush = new SolidColorBrush(Color.FromRgb(17, 127, 120));

        var grid = new Grid
        {
            Width = 13,
            Height = 13,
            SnapsToDevicePixels = true
        };

        grid.Children.Add(new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1),
            Background = Brushes.White,
            Margin = new Thickness(0, 1, 0, 0)
        });

        grid.Children.Add(new Border
        {
            Height = 3,
            Margin = new Thickness(1, 2, 1, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = accentBrush
        });

        grid.Children.Add(new Rectangle
        {
            Width = 2,
            Height = 3,
            Fill = borderBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3, 0, 0, 0)
        });

        grid.Children.Add(new Rectangle
        {
            Width = 2,
            Height = 3,
            Fill = borderBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 3, 0)
        });

        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                grid.Children.Add(new Rectangle
                {
                    Width = 2,
                    Height = 2,
                    Fill = new SolidColorBrush(Color.FromRgb(106, 121, 130)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(3 + col * 3, 7 + row * 3, 0, 0)
                });
            }
        }

        return grid;
    }
}
