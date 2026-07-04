using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;
using SpeedEmulator.Views;

namespace SpeedEmulator.ViewModels;

public sealed class PrintPreviewViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions TemplateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IPrintTemplateRepository templateRepository;
    private readonly IPrintPdfService printPdfService;
    private readonly IReadOnlyList<FlowRecord> records;
    private PrintTemplate? selectedTemplate;
    private string previewPath = string.Empty;
    private string statusMessage = "正在准备打印模板";
    private bool isBusy;
    private bool pendingPreviewRefresh;
    private bool suppressAutoPreview;

    public PrintPreviewViewModel(
        Bank bank,
        BankUser bankUser,
        IReadOnlyList<FlowRecord> records,
        IPrintTemplateRepository templateRepository,
        IPrintPdfService printPdfService)
    {
        Bank = bank;
        BankUser = bankUser;
        this.records = records;
        this.templateRepository = templateRepository;
        this.printPdfService = printPdfService;

        GeneratePreviewCommand = new AsyncRelayCommand(GeneratePreviewAsync);
        OpenPdfCommand = new RelayCommand(OpenPdf);
        PrintPdfCommand = new RelayCommand(PrintPdf);
        ExportPdfCommand = new AsyncRelayCommand(ExportPdfAsync);
        BackCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));

        NewTemplateCommand = new AsyncRelayCommand(NewTemplateAsync);
        CopyTemplateCommand = new AsyncRelayCommand(CopyTemplateAsync);
        SettingTemplateCommand = new AsyncRelayCommand(OpenTemplateEditorAsync);
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync);
        ImportTemplateCommand = new AsyncRelayCommand(ImportTemplateAsync);
        ExportTemplateCommand = new AsyncRelayCommand(ExportTemplateAsync);
        DeleteTemplateCommand = new AsyncRelayCommand(DeleteTemplateAsync);
    }

    public event EventHandler? RequestClose;

    public Bank Bank { get; }

    public BankUser BankUser { get; }

    public string WindowTitle => $"打印模板-{Bank.Name}-{BankUser.AccountName}";

    public ObservableCollection<PrintTemplate> Templates { get; } = [];

    public PrintTemplate? SelectedTemplate
    {
        get => selectedTemplate;
        set
        {
            if (SetProperty(ref selectedTemplate, value))
            {
                var wasSuppressingAutoPreview = suppressAutoPreview;
                suppressAutoPreview = true;
                StatusMessage = value is null ? "请选择打印模板" : $"当前模板：{value.Name}";
                if (value is not null && !suppressAutoPreview)
                {
                    if (IsBusy)
                    {
                        pendingPreviewRefresh = true;
                    }
                    else
                    {
                        _ = GeneratePreviewAsync();
                    }
                }

                suppressAutoPreview = wasSuppressingAutoPreview;
                PreviewPath = string.Empty;
                pendingPreviewRefresh = false;
                StatusMessage = value is null
                    ? "\u8bf7\u9009\u62e9\u6253\u5370\u6a21\u677f"
                    : $"\u5f53\u524d\u6a21\u677f\uff1a{value.Name}\uff0c\u53cc\u51fb\u751f\u6210\u9884\u89c8";
            }
        }
    }

    public string PreviewPath
    {
        get => previewPath;
        private set => SetProperty(ref previewPath, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public int RecordCount => records.Count;

    public ICommand GeneratePreviewCommand { get; }

    public RelayCommand OpenPdfCommand { get; }

    public RelayCommand PrintPdfCommand { get; }

    public ICommand ExportPdfCommand { get; }

    public RelayCommand BackCommand { get; }

    public ICommand NewTemplateCommand { get; }

    public ICommand CopyTemplateCommand { get; }

    public ICommand SettingTemplateCommand { get; }

    public ICommand SaveTemplateCommand { get; }

    public ICommand ImportTemplateCommand { get; }

    public ICommand ExportTemplateCommand { get; }

    public ICommand DeleteTemplateCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        suppressAutoPreview = true;
        try
        {
            await ReloadTemplatesAsync(selectFallback: false);
            if (Templates.Count == 0)
            {
                StatusMessage = "没有可用打印模板";
            }
            else
            {
                StatusMessage = "\u8bf7\u9009\u62e9\u6253\u5370\u6a21\u677f";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载打印模板失败：{GetFriendlyExceptionMessage(ex)}";
            MessageBox.Show(StatusMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            suppressAutoPreview = false;
            IsBusy = false;
        }
    }

    private async Task GeneratePreviewAsync()
    {
        if (SelectedTemplate is null)
        {
            MessageBox.Show("请选择打印模板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        var template = SelectedTemplate;
        IsBusy = true;
        try
        {
            StatusMessage = $"\u6b63\u5728\u751f\u6210 PDF \u9884\u89c8\uff1a{template.Name}";
            PreviewPath = string.Empty;
            await EnsureQuestPdfLayoutAsync(template);
            PreviewPath = await printPdfService.GeneratePreviewAsync(CreateContext(template));
            OnPropertyChanged(nameof(PreviewPath));
            StatusMessage = $"预览已生成：{PreviewPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"生成预览失败：{GetFriendlyExceptionMessage(ex)}";
            MessageBox.Show(StatusMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }

        if (pendingPreviewRefresh)
        {
            pendingPreviewRefresh = false;
            await GeneratePreviewAsync();
        }
    }

    private void OpenPdf()
    {
        if (string.IsNullOrWhiteSpace(PreviewPath))
        {
            MessageBox.Show("请先生成预览", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (printPdfService is QuestPdfPrintService)
            {
                QuestPdfPrintService.OpenPdf(PreviewPath);
                return;
            }

            Process.Start(new ProcessStartInfo(PreviewPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开PDF失败：{GetFriendlyExceptionMessage(ex)}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PrintPdf()
    {
        if (string.IsNullOrWhiteSpace(PreviewPath))
        {
            MessageBox.Show("请先生成预览", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(PreviewPath)
            {
                UseShellExecute = true,
                Verb = "print",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打印失败：{GetFriendlyExceptionMessage(ex)}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportPdfAsync()
    {
        if (SelectedTemplate is null)
        {
            MessageBox.Show("请选择打印模板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出PDF",
            Filter = "PDF文件 (*.pdf)|*.pdf",
            FileName = $"{Bank.Name}-{BankUser.AccountName}.pdf",
            AddExtension = true,
            DefaultExt = ".pdf",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await EnsureQuestPdfLayoutAsync(SelectedTemplate);
            await printPdfService.ExportAsync(CreateContext(SelectedTemplate), dialog.FileName);
            PreviewPath = dialog.FileName;
            StatusMessage = $"导出成功：{dialog.FileName}";
            MessageBox.Show("导出成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{GetFriendlyExceptionMessage(ex)}";
            MessageBox.Show(StatusMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task NewTemplateAsync()
    {
        var defaultName = CreateUniqueTemplateName($"{Bank.Name}{Bank.GetBankType()}自定义模板");
        var name = PromptText("新增模板", "模板名字", defaultName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var template = CreateBlankTemplate();
        if (!TryCreateBlankEditableTemplate(template))
        {
            MessageBox.Show("没有可用的模板，无法新增可编辑模板。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        template.Id = 0;
        template.BankId = Bank.Id;
        template.IsSystem = false;
        template.Name = CreateUniqueTemplateName(name.Trim());
        await EnsureQuestPdfLayoutAsync(template);
        await templateRepository.SaveAsync(Bank, template);
        await ReloadTemplatesAsync(template.Name);
        MessageBox.Show("新增模板成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task CopyTemplateAsync()
    {
        if (!EnsureTemplateSelected())
        {
            return;
        }

        var selectedTemplate = SelectedTemplate!;
        var template = selectedTemplate.Clone();
        if (ShouldRefreshBlankEditableShell(template))
        {
            template.PdfData = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(template.PdfData) && !TryHydrateTemplateData(template))
        {
            var source = GetTemplateSource(selectedTemplate);
            if (source is not null && !string.IsNullOrWhiteSpace(source.PdfData))
            {
                template = source.Clone();
                template.Name = selectedTemplate.Name;
                template.PageRows = selectedTemplate.PageRows;
                template.Remark = selectedTemplate.Remark;
                template.Config = selectedTemplate.Config.Clone();
            }
            else if (CanCopyAsConfigTemplate(template))
            {
                template.PdfData = string.Empty;
                template.QuestPdfLayoutData = string.Empty;
            }
            else
            {
                MessageBox.Show("当前模板缺少可复制的模板数据，请先选择一个可复制模板或导入模板。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        template.Id = 0;
        template.BankId = Bank.Id;
        template.IsSystem = false;
        template.Name = CreateUniqueTemplateName($"{template.Name}-复制");
        await EnsureQuestPdfLayoutAsync(template);

        await templateRepository.SaveAsync(Bank, template);
        await ReloadTemplatesAsync(template.Name);
        MessageBox.Show("复制模板成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task OpenTemplateEditorAsync()
    {
        if (!EnsureTemplateSelected())
        {
            return;
        }

        var selectedTemplate = SelectedTemplate!;
        if (!IsSavedQuestPdfConfigTemplate(selectedTemplate))
        {
            TryHydrateTemplateData(selectedTemplate, requirePdfData: false);
        }

        if (ShouldOpenTemplateDesigner(selectedTemplate))
        {
            await OpenTemplateDesignerAsync();
            return;
        }

        await OpenTemplateSettingsAsync();
    }

    private async Task OpenTemplateSettingsAsync()
    {
        if (!EnsureTemplateSelected())
        {
            return;
        }

        var selectedTemplate = SelectedTemplate!;
        if (IsTemplateSettingsReadonly(selectedTemplate))
        {
            MessageBox.Show("当前模板不支持参数设置，请选择可设置模板或复制成自定义模板。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            TryHydrateTemplateData(selectedTemplate, requirePdfData: false);
            await EnsureQuestPdfLayoutAsync(selectedTemplate);

            var viewModel = new PrintTemplateSettingsViewModel(
                Bank,
                selectedTemplate,
                async settings => await UpdatePreviewFromSettingsAsync(selectedTemplate, settings),
                async settings =>
                {
                    if (await UpdatePreviewFromSettingsAsync(selectedTemplate, settings) && !string.IsNullOrWhiteSpace(PreviewPath))
                    {
                        OpenPdf();
                    }
                },
                async settings => await SaveSettingsTemplateAsync(selectedTemplate, settings));
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
            var window = new PrintTemplateSettingsWindow(viewModel)
            {
                Owner = owner,
                Left = 60,
                Top = 20
            };

            window.Show();
        }
        catch (Exception ex)
        {
            StatusMessage = $"设置模板失败：{GetFriendlyExceptionMessage(ex)}";
            MessageBox.Show(StatusMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> UpdatePreviewFromSettingsAsync(PrintTemplate template, PrintTemplateSettingsViewModel settings)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            settings.ApplyTo(template);
            PreviewPath = await printPdfService.GeneratePreviewAsync(CreateContext(template));
            OnPropertyChanged(nameof(PreviewPath));
            StatusMessage = $"预览已生成：{PreviewPath}";
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveSettingsTemplateAsync(PrintTemplate template, PrintTemplateSettingsViewModel settings)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (template.IsSystem || template.Id <= 0)
            {
                MessageBox.Show("系统模板不能保存，请先复制成自定义模板。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            settings.ApplyTo(template);
            await templateRepository.SaveAsync(Bank, template);
            StatusMessage = $"模板已设置：{template.Name}";
            MessageBox.Show("保存成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenTemplateDesignerAsync()
    {
        if (!EnsureTemplateSelected())
        {
            return;
        }

        if (SelectedTemplate!.IsSystem)
        {
            MessageBox.Show("系统模板不能设置，请先复制成自定义模板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!await EnsureTemplateHasPdfDataAsync(SelectedTemplate))
        {
            MessageBox.Show("当前模板不是可编辑模板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedTemplate.PdfData))
        {
            MessageBox.Show("当前模板不是可编辑模板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (printPdfService is not ZhenchengPrintBridgeService zhenchengPrintBridgeService)
        {
            MessageBox.Show("当前打印服务不支持模板设计器", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var before = TemplateSnapshot.From(SelectedTemplate);
            zhenchengPrintBridgeService.OpenTemplateDesigner(SelectedTemplate);
            if (string.IsNullOrWhiteSpace(SelectedTemplate.PdfData) || ShouldRefreshBlankEditableShell(SelectedTemplate))
            {
                before.Restore(SelectedTemplate);
                MessageBox.Show("模板未保存：设计器返回了空白模板数据，请重新打开模板后再试。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedTemplate.QuestPdfLayoutData = string.Empty;
            var after = TemplateSnapshot.From(SelectedTemplate);
            if (!before.Equals(after))
            {
                await templateRepository.SaveAsync(Bank, SelectedTemplate);
            }
            suppressAutoPreview = true;
            try
            {
                await ReloadTemplatesAsync(SelectedTemplate.Name, SelectedTemplate.Id);
            }
            finally
            {
                suppressAutoPreview = false;
            }

            PreviewPath = string.Empty;
            StatusMessage = $"模板已设置：{SelectedTemplate.Name}";
            MessageBox.Show("设置模板成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"设置模板失败：{GetFriendlyExceptionMessage(ex)}";
            MessageBox.Show(StatusMessage, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }

    }

    private static bool IsTemplateSettingsReadonly(PrintTemplate template)
    {
        return template.IsSystem && !string.IsNullOrWhiteSpace(template.PdfData);
    }

    private static bool ShouldOpenTemplateDesigner(PrintTemplate template)
    {
        return !string.IsNullOrWhiteSpace(template.PdfData);
    }

    private async Task SaveTemplateAsync()
    {
        if (!EnsureTemplateSelected())
        {
            return;
        }

        if (SelectedTemplate!.IsSystem || SelectedTemplate.Id <= 0)
        {
            MessageBox.Show("系统模板不能保存，请先复制成自定义模板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await EnsureQuestPdfLayoutAsync(SelectedTemplate);
        await templateRepository.SaveAsync(Bank, SelectedTemplate);
        await ReloadTemplatesAsync(SelectedTemplate.Name);
        MessageBox.Show("保存模板成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ImportTemplateAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入模板",
            Filter = "极速财务加密模板 (*.jstpl)|*.jstpl|旧版明文模板 (*.json)|*.json|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var imported = await EncryptedPrintTemplatePackageService.ImportAsync(dialog.FileName, TemplateJsonOptions);
            if (imported is null)
            {
                MessageBox.Show("模板文件无法读取", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (imported.BankId > 0 && imported.BankId != Bank.Id)
            {
                MessageBox.Show("模板银行不匹配，不能导入", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            imported.Id = 0;
            imported.BankId = Bank.Id;
            imported.IsSystem = false;
            imported.IsDeleted = false;
            imported.Name = CreateUniqueImportedTemplateName(string.IsNullOrWhiteSpace(imported.Name)
                ? $"{Bank.Name}{Bank.GetBankType()}导入模板"
                : imported.Name.Trim());
            await EnsureQuestPdfLayoutAsync(imported);
            await templateRepository.SaveAsync(Bank, imported);
            await ReloadTemplatesAsync(imported.Name);
            MessageBox.Show("导入模板成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入模板失败：{GetFriendlyExceptionMessage(ex)}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportTemplateAsync()
    {
        if (!EnsureTemplateSelected())
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出模板",
            Filter = "极速财务加密模板 (*.jstpl)|*.jstpl|所有文件 (*.*)|*.*",
            FileName = $"{SelectedTemplate!.Name}.jstpl",
            AddExtension = true,
            DefaultExt = ".jstpl",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await EnsureQuestPdfLayoutAsync(SelectedTemplate);
            await EncryptedPrintTemplatePackageService.ExportAsync(SelectedTemplate, dialog.FileName, TemplateJsonOptions);
            MessageBox.Show("导出模板成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出模板失败：{GetFriendlyExceptionMessage(ex)}", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteTemplateAsync()
    {
        if (!EnsureTemplateSelected())
        {
            return;
        }

        if (SelectedTemplate!.IsSystem)
        {
            MessageBox.Show("系统模板不能删除", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show("确认删除当前模板？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await templateRepository.DeleteAsync(Bank, SelectedTemplate);
        await ReloadTemplatesAsync();
        MessageBox.Show("删除模板成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ReloadTemplatesAsync(string? preferredName = null, long? preferredId = null, bool selectFallback = true)
    {
        Templates.Clear();
        var templates = await templateRepository.ListByBankAsync(Bank);
        foreach (var template in templates)
        {
            Templates.Add(template);
        }

        SelectedTemplate = preferredId is not null
            ? Templates.FirstOrDefault(item => item.Id == preferredId.Value)
            : null;

        SelectedTemplate ??= !string.IsNullOrWhiteSpace(preferredName)
            ? Templates.FirstOrDefault(item => string.Equals(item.Name, preferredName, StringComparison.Ordinal))
            : null;

        if (selectFallback)
        {
            SelectedTemplate ??= Templates.FirstOrDefault();
        }
    }

    private bool EnsureTemplateSelected()
    {
        if (SelectedTemplate is not null)
        {
            return true;
        }

        MessageBox.Show("请选择打印模板", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private string CreateUniqueTemplateName(string baseName)
    {
        var normalized = string.IsNullOrWhiteSpace(baseName) ? "自定义模板" : baseName.Trim();
        if (Templates.All(item => !string.Equals(item.Name, normalized, StringComparison.Ordinal)))
        {
            return normalized;
        }

        for (var index = 1; index < 1000; index++)
        {
            var candidate = $"{normalized}{index}";
            if (Templates.All(item => !string.Equals(item.Name, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        return $"{normalized}-{DateTime.Now:yyyyMMddHHmmss}";
    }

    private string CreateUniqueImportedTemplateName(string baseName)
    {
        var normalized = string.IsNullOrWhiteSpace(baseName) ? "导入模板" : baseName.Trim();
        var first = $"{normalized}-导入";
        if (Templates.All(item => !string.Equals(item.Name, first, StringComparison.Ordinal)))
        {
            return first;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{normalized}-导入{index}";
            if (Templates.All(item => !string.Equals(item.Name, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        return $"{normalized}-导入-{DateTime.Now:yyyyMMddHHmmss}";
    }

    private bool TryCreateBlankEditableTemplate(PrintTemplate template)
    {
        return printPdfService is ZhenchengPrintBridgeService zhenchengPrintBridgeService
            && zhenchengPrintBridgeService.TryCreateBlankTemplate(template);
    }

    private bool TryHydrateTemplateData(PrintTemplate template, bool requirePdfData = true)
    {
        return printPdfService is ZhenchengPrintBridgeService zhenchengPrintBridgeService
            && zhenchengPrintBridgeService.TryHydrateTemplate(CreateContext(template), template, requirePdfData);
    }

    private async Task EnsureQuestPdfLayoutAsync(PrintTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        if (template.IsSystem)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(template.PdfData))
        {
            return;
        }

        if (!PrintTemplateQuestPdfConversionService.EnsureConverted(Bank, template))
        {
            return;
        }

        if (template.Id > 0)
        {
            await templateRepository.SaveAsync(Bank, template);
        }
    }

    private static bool IsSavedQuestPdfConfigTemplate(PrintTemplate template)
    {
        return template.Id > 0
            && template.VendorId <= 0
            && string.IsNullOrWhiteSpace(template.PdfData)
            && template.Config.Columns.Count > 0;
    }

    private async Task<bool> EnsureTemplateHasPdfDataAsync(PrintTemplate? template)
    {
        if (template is null)
        {
            return true;
        }

        if (ShouldRefreshBlankEditableShell(template))
        {
            template.PdfData = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(template.PdfData))
        {
            return true;
        }

        if (template.IsSystem)
        {
            return false;
        }

        if (TryHydrateTemplateData(template))
        {
            await EnsureQuestPdfLayoutAsync(template);
            if (template.Id > 0)
            {
                await templateRepository.SaveAsync(Bank, template);
            }

            return true;
        }

        var source = GetTemplateSource(template);
        if (source is null || string.IsNullOrWhiteSpace(source.PdfData))
        {
            return false;
        }

        template.PageSize = source.PageSize;
        template.PageRows = source.PageRows;
        template.Remark = source.Remark;
        template.PdfData = source.PdfData;
        template.QuestPdfLayoutData = source.QuestPdfLayoutData;
        template.VendorId = source.VendorId;
        template.VendorBankId = source.VendorBankId;
        template.Config = source.Config.Clone();
        await EnsureQuestPdfLayoutAsync(template);

        if (template.Id > 0)
        {
            await templateRepository.SaveAsync(Bank, template);
        }

        return true;
    }

    private static bool ShouldRefreshBlankEditableShell(PrintTemplate template)
    {
        if (!IsDerivedTemplateName(template.Name) || string.IsNullOrWhiteSpace(template.PdfData))
        {
            return false;
        }

        var data = template.PdfData;
        return data.Length < 12_000
            && data.Contains("<BusinessObjects isList=\"true\" count=\"0\"", StringComparison.Ordinal)
            && data.Contains("<Components isList=\"true\" count=\"0\"", StringComparison.Ordinal)
            && !data.Contains("StiText", StringComparison.Ordinal)
            && !data.Contains("DataBand", StringComparison.Ordinal);
    }

    private static bool IsDerivedTemplateName(string name)
    {
        return name.Contains("-复制", StringComparison.Ordinal)
            || name.Contains("_复制", StringComparison.Ordinal)
            || name.Contains(" 复制", StringComparison.Ordinal)
            || name.Contains("-导入", StringComparison.Ordinal)
            || name.Contains("-改", StringComparison.Ordinal)
            || name.Contains("_改", StringComparison.Ordinal)
            || name.Contains(" 改", StringComparison.Ordinal);
    }

    private bool CanCopyAsConfigTemplate(PrintTemplate template)
    {
        return template.Config.Columns.Count > 0
            || Bank.FlowColumns.Any(item => item.Show && !string.IsNullOrWhiteSpace(item.Field));
    }

    private PrintTemplate? GetTemplateSource(PrintTemplate? preferred)
    {
        if (preferred is not null
            && !string.IsNullOrWhiteSpace(preferred.PdfData)
            && !ShouldRefreshBlankEditableShell(preferred))
        {
            return preferred;
        }

        var candidates = Templates
            .Where(item =>
                !ReferenceEquals(item, preferred)
                && !string.IsNullOrWhiteSpace(item.PdfData)
                && !ShouldRefreshBlankEditableShell(item))
            .ToList();

        if (preferred is not null && preferred.PageRows > 0)
        {
            var sameRows = candidates.FirstOrDefault(item => item.PageRows == preferred.PageRows);
            if (sameRows is not null)
            {
                return sameRows;
            }
        }

        return candidates.FirstOrDefault(item => item.IsSystem)
            ?? candidates.FirstOrDefault();
    }

    private PrintTemplate CreateBlankTemplate()
    {
        return new PrintTemplate
        {
            Id = 0,
            BankId = Bank.Id,
            IsSystem = false,
            Name = $"{Bank.Name}{Bank.GetBankType()}自定义模板",
            PageSize = "A4Portrait",
            PageRows = 0,
            Remark = string.Empty,
            Config = new PrintPdfConfig()
        };
    }

    private static string? PromptText(string title, string label, string defaultValue)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        var window = new Window
        {
            Title = title,
            Width = 360,
            Height = 150,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner
        };
        var input = new TextBox
        {
            Text = defaultValue,
            MinWidth = 260,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var buttons = CreatePromptButtons(input);
        var panel = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        Grid.SetRow(input, 1);
        Grid.SetRow(buttons, 3);
        window.Content = panel;
        input.SelectAll();
        input.Focus();
        return window.ShowDialog() == true ? input.Text.Trim() : null;

        UIElement CreatePromptButtons(TextBox textBox)
        {
            var ok = new Button
            {
                Content = "确定",
                Width = 76,
                Height = 28,
                IsDefault = true,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var cancel = new Button
            {
                Content = "取消",
                Width = 76,
                Height = 28,
                IsCancel = true
            };
            ok.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(textBox.Text))
                {
                    MessageBox.Show("模板名字不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                window.DialogResult = true;
            };

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { ok, cancel }
            };
        }
    }

    private static string GetFriendlyExceptionMessage(Exception ex)
    {
        var current = ex;
        while (current is TargetInvocationException && current.InnerException is not null)
        {
            current = current.InnerException;
        }

        var baseException = current.GetBaseException();
        var message = string.IsNullOrWhiteSpace(baseException.Message)
            ? current.Message
            : baseException.Message;
        var diagnosticMessage = ZhenchengPrintBridgeService.GetPrintDiagnosticMessageForUi(ex);
        return string.IsNullOrWhiteSpace(diagnosticMessage)
            ? message
            : $"{message}{Environment.NewLine}{diagnosticMessage}";
    }

    private PrintRenderContext CreateContext(PrintTemplate template)
    {
        return new PrintRenderContext
        {
            Bank = Bank,
            BankUser = BankUser,
            Records = records,
            Template = template
        };
    }

    private sealed record TemplateSnapshot(string Name, int PageRows, string Remark, string PdfData, string QuestPdfLayoutData)
    {
        public static TemplateSnapshot From(PrintTemplate template)
        {
            return new TemplateSnapshot(template.Name, template.PageRows, template.Remark, template.PdfData, template.QuestPdfLayoutData);
        }

        public void Restore(PrintTemplate template)
        {
            template.Name = Name;
            template.PageRows = PageRows;
            template.Remark = Remark;
            template.PdfData = PdfData;
            template.QuestPdfLayoutData = QuestPdfLayoutData;
        }
    }
}
