using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

namespace SpeedEmulator.ViewModels;

public sealed class PrintTemplateSettingsViewModel : ObservableObject
{
    private readonly Bank bank;
    private readonly PrintTemplate workingTemplate;
    private readonly Func<PrintTemplateSettingsViewModel, Task>? updatePreviewAsync;
    private readonly Func<PrintTemplateSettingsViewModel, Task>? directOpenAsync;
    private readonly Func<PrintTemplateSettingsViewModel, Task>? saveAsync;

    public PrintTemplateSettingsViewModel(
        Bank bank,
        PrintTemplate template,
        Func<PrintTemplateSettingsViewModel, Task>? updatePreviewAsync = null,
        Func<PrintTemplateSettingsViewModel, Task>? directOpenAsync = null,
        Func<PrintTemplateSettingsViewModel, Task>? saveAsync = null)
    {
        this.bank = bank;
        workingTemplate = template.Clone();
        this.updatePreviewAsync = updatePreviewAsync;
        this.directOpenAsync = directOpenAsync;
        this.saveAsync = saveAsync;
        EnsureConfig();
        LoadColumns(workingTemplate.Config.Columns);

        RefreshCommand = new AsyncRelayCommand(UpdatePreviewAsync);
        DirectOpenCommand = new AsyncRelayCommand(DirectOpenAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public string WindowTitle => $"{bank.Name}({bank.GetBankType()})-PDF模板设置";

    public ICommand RefreshCommand { get; }

    public ICommand DirectOpenCommand { get; }

    public ICommand SaveCommand { get; }

    public ObservableCollection<PrintPdfColumnSettingItem> Columns { get; } = [];

    public double MarginLeft
    {
        get => workingTemplate.Config.MarginLeft;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.MarginLeft, value))
            {
                workingTemplate.Config.MarginLeft = value;
                OnPropertyChanged();
            }
        }
    }

    public double MarginTop
    {
        get => workingTemplate.Config.MarginTop;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.MarginTop, value))
            {
                workingTemplate.Config.MarginTop = value;
                OnPropertyChanged();
            }
        }
    }

    public double MarginRight
    {
        get => workingTemplate.Config.MarginRight;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.MarginRight, value))
            {
                workingTemplate.Config.MarginRight = value;
                OnPropertyChanged();
            }
        }
    }

    public double MarginBottom
    {
        get => workingTemplate.Config.MarginBottom;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.MarginBottom, value))
            {
                workingTemplate.Config.MarginBottom = value;
                OnPropertyChanged();
            }
        }
    }

    public string FontFamily
    {
        get => workingTemplate.Config.FontFamily;
        set
        {
            var next = value ?? string.Empty;
            if (workingTemplate.Config.FontFamily != next)
            {
                workingTemplate.Config.FontFamily = next;
                OnPropertyChanged();
            }
        }
    }

    public double TabSize
    {
        get => workingTemplate.Config.TabSize;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.TabSize, value))
            {
                workingTemplate.Config.TabSize = value;
                OnPropertyChanged();
            }
        }
    }

    public double ColumnMinHeight
    {
        get => workingTemplate.Config.ColumnMinHeight;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.ColumnMinHeight, value))
            {
                workingTemplate.Config.ColumnMinHeight = value;
                OnPropertyChanged();
            }
        }
    }

    public int PageRows
    {
        get => workingTemplate.PageRows;
        set
        {
            if (workingTemplate.PageRows != value)
            {
                workingTemplate.PageRows = value;
                workingTemplate.Config.RowCount = value;
                OnPropertyChanged();
            }
        }
    }

    public bool Descending
    {
        get => workingTemplate.Config.Descending;
        set
        {
            if (workingTemplate.Config.Descending != value)
            {
                workingTemplate.Config.Descending = value;
                OnPropertyChanged();
            }
        }
    }

    public double FirstPageOffset
    {
        get => workingTemplate.Config.FirstPageOffset;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.FirstPageOffset, value))
            {
                workingTemplate.Config.FirstPageOffset = value;
                OnPropertyChanged();
            }
        }
    }

    public double SealLeft
    {
        get => workingTemplate.Config.SealLeft;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.SealLeft, value))
            {
                workingTemplate.Config.SealLeft = value;
                OnPropertyChanged();
            }
        }
    }

    public double SealTop
    {
        get => workingTemplate.Config.SealTop;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.SealTop, value))
            {
                workingTemplate.Config.SealTop = value;
                OnPropertyChanged();
            }
        }
    }

    public double SealRight
    {
        get => workingTemplate.Config.SealRight;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.SealRight, value))
            {
                workingTemplate.Config.SealRight = value;
                OnPropertyChanged();
            }
        }
    }

    public double SealBottom
    {
        get => workingTemplate.Config.SealBottom;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.SealBottom, value))
            {
                workingTemplate.Config.SealBottom = value;
                OnPropertyChanged();
            }
        }
    }

    public double SealWidth
    {
        get => workingTemplate.Config.SealWidth;
        set
        {
            if (!NearlyEquals(workingTemplate.Config.SealWidth, value))
            {
                workingTemplate.Config.SealWidth = value;
                OnPropertyChanged();
            }
        }
    }

    public void ApplyTo(PrintTemplate template)
    {
        if (template.IsSystem && !string.IsNullOrWhiteSpace(template.PdfData))
        {
            throw new InvalidOperationException("当前模板不支持参数设置，请选择可设置模板或复制成自定义模板。");
        }

        workingTemplate.Config.Columns = Columns.Select(item => item.ToColumn()).ToList();
        workingTemplate.Config.RowCount = workingTemplate.PageRows;
        template.PageRows = workingTemplate.PageRows;
        template.PageSize = workingTemplate.PageSize;
        template.Config = workingTemplate.Config.Clone();
        template.QuestPdfLayoutData = string.Empty;
        PrintTemplateQuestPdfConversionService.UpdateLayoutSnapshot(template);
    }

    private async Task UpdatePreviewAsync()
    {
        if (updatePreviewAsync is null)
        {
            MessageBox.Show("当前模板不能更新预览", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await updatePreviewAsync(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"更新预览失败：{GetFriendlyExceptionMessage(ex)}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DirectOpenAsync()
    {
        if (directOpenAsync is null)
        {
            MessageBox.Show("当前模板不能直接打开", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await directOpenAsync(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开预览失败：{GetFriendlyExceptionMessage(ex)}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveAsync()
    {
        if (saveAsync is null)
        {
            MessageBox.Show("当前模板不能保存设置", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await saveAsync(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存设置失败：{GetFriendlyExceptionMessage(ex)}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EnsureConfig()
    {
        var defaultConfig = PrintTemplateQuestPdfConversionService.CreateDefaultConfig(bank, workingTemplate);
        if (workingTemplate.Config.Columns.Count == 0)
        {
            workingTemplate.Config = defaultConfig;
        }
    }

    private void LoadColumns(IEnumerable<PrintPdfColumn> columns)
    {
        Columns.Clear();
        foreach (var column in columns)
        {
            Columns.Add(new PrintPdfColumnSettingItem(column));
        }
    }

    private static bool NearlyEquals(double left, double right)
    {
        return Math.Abs(left - right) < 0.0001;
    }

    private static string GetFriendlyExceptionMessage(Exception ex)
    {
        var baseException = ex.GetBaseException();
        return string.IsNullOrWhiteSpace(baseException.Message)
            ? ex.Message
            : baseException.Message;
    }
}

public sealed class PrintPdfColumnSettingItem : ObservableObject
{
    private string name;
    private string field;
    private string type;
    private double width;
    private double fontSize;
    private string fontFamily;
    private double lineHeight;

    public PrintPdfColumnSettingItem(PrintPdfColumn column)
    {
        name = column.Name;
        field = column.Field;
        type = column.Type;
        width = column.Width;
        fontSize = column.FontSize;
        fontFamily = string.IsNullOrWhiteSpace(column.FontFamily) ? "Microsoft YaHei" : column.FontFamily;
        lineHeight = column.LineHeight <= 0 ? 1 : column.LineHeight;
    }

    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    public string Field
    {
        get => field;
        set => SetProperty(ref field, value);
    }

    public string Type
    {
        get => type;
        set => SetProperty(ref type, value);
    }

    public double Width
    {
        get => width;
        set => SetProperty(ref width, value);
    }

    public double FontSize
    {
        get => fontSize;
        set => SetProperty(ref fontSize, value);
    }

    public string FontFamily
    {
        get => fontFamily;
        set => SetProperty(ref fontFamily, value);
    }

    public double LineHeight
    {
        get => lineHeight;
        set => SetProperty(ref lineHeight, value);
    }

    public PrintPdfColumn ToColumn()
    {
        return new PrintPdfColumn
        {
            Name = Name,
            Field = Field,
            Type = Type,
            Width = Width,
            FontSize = FontSize,
            FontFamily = FontFamily,
            LineHeight = LineHeight
        };
    }
}
