using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;
using SpeedEmulator.Repositories;
using SpeedEmulator.Services;
using SpeedEmulator.ViewModels;

internal static partial class Program
{
    private static void TestPrintTemplateSettings(List<Entry> catalog)
    {
        var directory = Path.Combine(Output, "template-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var abc = catalog.First(entry => entry.Bank.Id == 11).Bank;
        var abcTemplates = catalog.Where(entry => entry.Bank.Id == abc.Id).Select(entry => entry.Template).ToArray();
        var editable = abcTemplates.First(template => template.IsSystem && string.IsNullOrEmpty(template.PdfData));
        var tests = new (string Name, Action Run)[]
        {
            ("农行个人全部默认模板倒序，重新加载仍倒序", () =>
            {
                var path = Path.Combine(directory, "defaults.json");
                var templates = SettingsRepository(path).ListByBankAsync(abc).GetAwaiter().GetResult();
                RequireSettings(templates.Count == abcTemplates.Length && templates.All(template => template.Config.Descending), "农行默认排序或模板数量错误");
                RequireSettings(!File.Exists(path), "读取默认设置不能写入用户配置");
            }),
            ("更新后重新打开及重启仓储仍保留倒序，再改正序也保留", () =>
            {
                var path = Path.Combine(directory, "round-trip.json");
                var repository = SettingsRepository(path);
                var template = editable.Clone();
                foreach (var descending in new[] { true, false, true })
                {
                    UpdateSettings(repository, abc, template, descending);
                    template = SettingsRepository(path).ListByBankAsync(abc).GetAwaiter().GetResult().Single(item => item.Name == template.Name);
                    RequireSettings(template.Config.Descending == descending, "重开后丢失排序选择");
                    RequireSettings(new PrintTemplateSettingsViewModel(abc, template).Descending == descending, "设置窗口未回显保存的排序");
                    repository = SettingsRepository(path);
                }
                RequireSettings(!template.IsPageRowsOverride, "只修改排序不能启用每页行数覆盖");
            }),
            ("同一银行其他模板保持默认，不出现额外模板", () =>
            {
                var path = Path.Combine(directory, "isolated.json");
                UpdateSettings(SettingsRepository(path), abc, editable.Clone(), false);
                var templates = SettingsRepository(path).ListByBankAsync(abc).GetAwaiter().GetResult();
                RequireSettings(templates.Count == abcTemplates.Length, "设置记录被显示为模板");
                RequireSettings(templates.Where(item => item.Name != editable.Name).All(item => item.Config.Descending), "排序设置影响其他模板");
            }),
            ("页面行数为0的系统模板仍能单独保存排序", () =>
            {
                var path = Path.Combine(directory, "zero-rows.json");
                var template = abcTemplates.First(item => item.IsSystem && item.PageRows == 0).Clone();
                template.Config.Descending = false;
                template.DescendingOverride = false;
                SettingsRepository(path).SaveAsync(abc, template).GetAwaiter().GetResult();
                var reopened = SettingsRepository(path).ListByBankAsync(abc).GetAwaiter().GetResult().Single(item => item.Name == template.Name);
                RequireSettings(!reopened.Config.Descending && reopened.PageRows == 0 && !reopened.IsPageRowsOverride, "排序保存错误依赖有效页行数");
            }),
            ("历史每页行数设置不误判为显式正序", () =>
            {
                var path = Path.Combine(directory, "legacy.json");
                var legacy = editable.Clone();
                legacy.Id = 1;
                legacy.IsSystem = false;
                legacy.IsPageRowsOverride = true;
                legacy.PageRows = 19;
                legacy.Config = new PrintPdfConfig();
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<long, PrintTemplate[]> { [abc.Id] = [legacy] }, options)
                    .Replace("\"descendingOverride\":null,", string.Empty));
                var reopened = SettingsRepository(path).ListByBankAsync(abc).GetAwaiter().GetResult().Single(item => item.Name == legacy.Name);
                RequireSettings(reopened.PageRows == 19 && reopened.Config.RowCount == 19 && reopened.Config.Descending, "历史行数配置覆盖了新默认排序");
            }),
            ("行数与排序连续更新均持久化，保持同一条设置记录", () =>
            {
                var path = Path.Combine(directory, "rows-and-sort.json");
                var repository = SettingsRepository(path);
                var template = editable.Clone();
                var preview = SettingsPreview(abc, repository);
                var settings = new PrintTemplateSettingsViewModel(abc, template) { PageRows = 23, Descending = false };
                ApplySettingsUpdate(preview, template, settings);
                template = SettingsRepository(path).ListByBankAsync(abc).GetAwaiter().GetResult().Single(item => item.Name == template.Name);
                RequireSettings(template.PageRows == 23 && !template.Config.Descending && template.IsPageRowsOverride, "更新丢失行数或排序");
                template.PageRows = 24;
                SettingsPreview(abc, SettingsRepository(path)).SavePageRowsAsync(template).GetAwaiter().GetResult();
                var reopened = SettingsRepository(path).ListByBankAsync(abc).GetAwaiter().GetResult().Single(item => item.Name == template.Name);
                RequireSettings(reopened.PageRows == 24 && !reopened.Config.Descending, "修改行数覆盖了已保存排序");
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                RequireSettings(json.RootElement.GetProperty(abc.Id.ToString()).GetArrayLength() == 1, "同一模板产生重复设置");
            }),
            ("其他银行显式排序可保存，默认值及对公农行不被更改", () =>
            {
                var path = Path.Combine(directory, "other-bank.json");
                var entry = catalog.First(item => item.Bank.Id == 4 && item.Template.IsSystem && string.IsNullOrEmpty(item.Template.PdfData));
                var expected = !entry.Template.Config.Descending;
                UpdateSettings(SettingsRepository(path), entry.Bank, entry.Template.Clone(), expected);
                var reopened = SettingsRepository(path).ListByBankAsync(entry.Bank).GetAwaiter().GetResult().Single(item => item.Name == entry.Template.Name);
                RequireSettings(reopened.Config.Descending == expected, "其他银行无法保存显式排序");
                var corporate = catalog.First(item => item.Bank.Id == 105).Bank;
                var corporateTemplates = SettingsRepository(path).ListByBankAsync(corporate).GetAwaiter().GetResult();
                RequireSettings(corporateTemplates.All(item => item.Config.Descending == catalog.Single(entry =>
                    entry.Bank.Id == corporate.Id && entry.Template.Name == item.Name).Template.Config.Descending), "个人默认排序影响农行对公");
            }),
            ("自定义模板更新保存完整配置，系统模板复制后不会隐藏", () =>
            {
                var path = Path.Combine(directory, "custom.json");
                var template = editable.Clone();
                template.Id = 0;
                template.IsSystem = false;
                template.IsPageRowsOverride = true;
                template.DescendingOverride = true;
                template.Name += "-复制-排序测试";
                UpdateSettings(SettingsRepository(path), abc, template, false);
                var reopened = SettingsRepository(path).ListByBankAsync(abc).GetAwaiter().GetResult().Single(item => item.Name == template.Name);
                RequireSettings(!reopened.IsSystem && !reopened.Config.Descending && reopened.Config.Columns.Count > 0, "自定义模板隐藏或未保存");
                RequireSettings(!reopened.IsPageRowsOverride && reopened.DescendingOverride is null, "自定义模板错误携带系统设置标记");
            }),
            ("预览失败不落盘，不报告更新保存成功", () =>
            {
                var path = Path.Combine(directory, "failed-preview.json");
                var preview = SettingsPreview(abc, SettingsRepository(path), new SettingsPdfService { Fail = true });
                try
                {
                    ApplySettingsUpdate(preview, editable.Clone(), new PrintTemplateSettingsViewModel(abc, editable) { Descending = false });
                    throw new Exception("失败预览应向设置窗口报告错误");
                }
                catch (InvalidOperationException exception) when (exception.Message == "preview-test-failure") { }
                RequireSettings(!File.Exists(path) && !preview.IsBusy, "预览失败仍保存了配置或未释放状态");
            }),
            ("保存排序不修改源流水的时间、序号、金额和余额", () =>
            {
                var rows = Enumerable.Range(1, 3).Select(index => new FlowRecord
                {
                    Index = index, AccountTime = new DateTime(2026, 1, index), TradeMoney = index * 10, Balance = index * 100
                }).ToArray();
                var before = JsonSerializer.Serialize(rows);
                var service = new SettingsPdfService();
                var preview = new PrintPreviewViewModel(abc, new BankUser(), rows,
                    SettingsRepository(Path.Combine(directory, "source.json")), service);
                ApplySettingsUpdate(preview, editable.Clone(), new PrintTemplateSettingsViewModel(abc, editable) { Descending = true });
                RequireSettings(service.OrderedIndices.SequenceEqual(new[] { 3, 2, 1 }), "打印数据未按倒序准备");
                RequireSettings(JsonSerializer.Serialize(rows) == before, "排序保存修改了源流水");
                RequireSettings(preview.StatusMessage.Contains("已保存"), "更新完成未报告设置已保存");
            }),
            ("删除系统模板设置时同时清除排序覆盖", () =>
            {
                var path = Path.Combine(directory, "delete.json");
                var repository = SettingsRepository(path);
                var template = editable.Clone();
                UpdateSettings(repository, abc, template, false);
                repository.DeleteAsync(abc, template).GetAwaiter().GetResult();
                var reopened = SettingsRepository(path).ListByBankAsync(abc).GetAwaiter().GetResult().Single(item => item.Name == template.Name);
                RequireSettings(reopened.Config.Descending && !reopened.IsPageRowsOverride, "清理后仍残留排序设置");
            })
        };

        foreach (var (name, run) in tests)
        {
            run();
            Console.WriteLine("PASS " + name);
        }

        var baselinePath = Path.Combine(Output, "template-settings-baseline.json");
        if (File.Exists(baselinePath))
        {
            using var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath));
            RequireSettings(baseline.RootElement.GetArrayLength() == catalog.Count, "本次修改改变了模板总数");
            var checkedTemplates = 0;
            foreach (var original in baseline.RootElement.EnumerateArray())
            {
                var bankId = original.GetProperty("Id").GetInt64();
                var templateName = original.GetProperty("TemplateName").GetString();
                var entry = catalog.Single(item => item.Bank.Id == bankId && item.Template.Name == templateName);
                RequireSettings(entry.Template.PageRows == original.GetProperty("PageRows").GetInt32(), "本次修改改变默认页行数");
                if (bankId == abc.Id) continue;
                RequireSettings(entry.Template.Config.Descending == original.GetProperty("Descending").GetBoolean(), "其他银行默认排序改变：" + entry.Bank.Name + "/" + templateName);
                checkedTemplates++;
            }
            Console.WriteLine($"PASS {checkedTemplates}个其他银行模板默认排序及全部{catalog.Count}个模板行数与修改前一致");
        }
        Console.WriteLine($"{tests.Length}/{tests.Length} template-settings tests passed; artifacts: {directory}");
    }

    private static JsonPrintTemplateRepository SettingsRepository(string path)
    {
        var repository = new JsonPrintTemplateRepository();
        // Redirect before the first read: tests must never touch the user's real settings.
        typeof(JsonPrintTemplateRepository).GetField("storagePath", Instance)!.SetValue(repository, path);
        return repository;
    }

    private static PrintPreviewViewModel SettingsPreview(Bank bank, IPrintTemplateRepository repository, IPrintPdfService? service = null)
        => new(bank, new BankUser(), [], repository, service ?? new SettingsPdfService());

    private static void UpdateSettings(IPrintTemplateRepository repository, Bank bank, PrintTemplate template, bool descending)
        => ApplySettingsUpdate(SettingsPreview(bank, repository), template, new PrintTemplateSettingsViewModel(bank, template) { Descending = descending });

    private static void ApplySettingsUpdate(PrintPreviewViewModel preview, PrintTemplate template, PrintTemplateSettingsViewModel settings)
    {
        var task = (Task<bool>)typeof(PrintPreviewViewModel).GetMethod("UpdatePreviewFromSettingsAsync", Instance)!
            .Invoke(preview, [template, settings])!;
        RequireSettings(task.GetAwaiter().GetResult(), "更新未执行");
    }

    private static void RequireSettings(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SettingsPdfService : IPrintPdfService
    {
        public bool Fail { get; init; }
        public int[] OrderedIndices { get; private set; } = [];
        public Task<string> GeneratePreviewAsync(PrintRenderContext context)
        {
            if (Fail) throw new InvalidOperationException("preview-test-failure");
            OrderedIndices = context.ApplyTemplateRecordOrder().Records.Select(record => record.Index).ToArray();
            return Task.FromResult("preview-test-only.pdf");
        }
        public Task ExportAsync(PrintRenderContext context, string path) => Task.CompletedTask;
    }
}
