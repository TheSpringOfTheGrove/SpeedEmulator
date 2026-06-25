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

        var button = new Button
        {
            Content = CreateCalendarIcon(),
            Width = 22,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(160, 168, 172)),
            Background = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
            ToolTip = "选择日期"
        };
        button.Click += (_, _) => TogglePopup();

        calendar = new System.Windows.Controls.Calendar();
        calendar.SelectedDatesChanged += Calendar_SelectedDatesChanged;

        hourBox = CreateTimeBox();
        minuteBox = CreateTimeBox();
        secondBox = CreateTimeBox();

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
        grid.Children.Add(textBox);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        var host = new Grid();
        border.Child = grid;
        host.Children.Add(border);
        host.Children.Add(popup);
        Content = host;

        Loaded += (_, _) => RefreshText();
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
    }

    private void Calendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isSyncing || calendar.SelectedDate is not { } selectedDate)
        {
            return;
        }

        var time = SelectedDate?.TimeOfDay ?? TimeSpan.Zero;
        SelectedDate = selectedDate.Date + time;
        SyncTimeBoxes();
        RefreshText();
    }

    private void TogglePopup()
    {
        CommitText();

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
            RefreshText();
            return;
        }

        if (TryParseDateTime(textBox.Text, out var parsed))
        {
            SelectedDate = parsed;
        }

        RefreshText();
    }

    private void RefreshText()
    {
        textBox.Text = SelectedDate?.ToString(DisplayFormat, CultureInfo.GetCultureInfo("zh-CN")) ?? string.Empty;
        SyncTimeBoxes();
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

    private TextBox CreateTimeBox()
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

        var value = SelectedDate ?? DateTime.Today;
        hourBox.Text = value.Hour.ToString("00", CultureInfo.InvariantCulture);
        minuteBox.Text = value.Minute.ToString("00", CultureInfo.InvariantCulture);
        secondBox.Text = value.Second.ToString("00", CultureInfo.InvariantCulture);
    }

    private void CommitTimeBoxes()
    {
        var current = SelectedDate ?? DateTime.Today;
        var hour = Clamp(ParsePart(hourBox.Text), 0, 23);
        var minute = Clamp(ParsePart(minuteBox.Text), 0, 59);
        var second = Clamp(ParsePart(secondBox.Text), 0, 59);

        SelectedDate = current.Date.Add(new TimeSpan(hour, minute, second));
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
