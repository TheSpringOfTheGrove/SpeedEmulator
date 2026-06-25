using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

namespace SpeedEmulator.ViewModels;

public sealed class FlowFilterViewModel : ObservableObject
{
    private readonly FlowDetailsViewModel flowDetailsViewModel;
    private string replaceFieldName = string.Empty;
    private string replaceSource = string.Empty;
    private string replaceTarget = string.Empty;
    private string statusMessage = "新增筛选条件后点击搜索。";

    public FlowFilterViewModel(FlowDetailsViewModel flowDetailsViewModel)
    {
        this.flowDetailsViewModel = flowDetailsViewModel;
        AvailableFields = flowDetailsViewModel.GetFilterFieldNames();
        AvailableOperators =
        [
            "等于",
            "不等于",
            "模糊匹配",
            "以开头",
            "以结尾",
            "属于",
            "大于",
            "绝对值大于",
            "小于",
            "绝对值小于"
        ];

        Filters.Add(new FlowFilterCondition());
        NewFilterCommand = new RelayCommand(() => Filters.Add(new FlowFilterCondition()));
        RemoveConditionCommand = new RelayCommand(RemoveCondition);
        SearchConditionCommand = new RelayCommand(Search);
        ReplaceTableCommand = new RelayCommand(Replace);
        SaveCommand = new AsyncRelayCommand(flowDetailsViewModel.SaveAllAsync);
    }

    public string WindowTitle => $"筛选设置-版本({AppVersion.DisplayVersion})-{flowDetailsViewModel.Bank.Name}";

    public ObservableCollection<FlowFilterCondition> Filters { get; } = [];

    public IReadOnlyList<string> AvailableFields { get; }

    public IReadOnlyList<string> AvailableOperators { get; }

    public string ReplaceFieldName
    {
        get => replaceFieldName;
        set => SetProperty(ref replaceFieldName, value);
    }

    public string ReplaceSource
    {
        get => replaceSource;
        set => SetProperty(ref replaceSource, value);
    }

    public string ReplaceTarget
    {
        get => replaceTarget;
        set => SetProperty(ref replaceTarget, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public RelayCommand NewFilterCommand { get; }

    public RelayCommand RemoveConditionCommand { get; }

    public RelayCommand SearchConditionCommand { get; }

    public RelayCommand ReplaceTableCommand { get; }

    public ICommand SaveCommand { get; }

    private void RemoveCondition(object? parameter)
    {
        if (parameter is FlowFilterCondition condition && Filters.Contains(condition))
        {
            Filters.Remove(condition);
        }
    }

    private void Search()
    {
        var validConditions = Filters.Where(item => item.IsValid()).ToList();
        flowDetailsViewModel.ApplyFilters(validConditions);
        StatusMessage = validConditions.Count == 0
            ? "没有有效筛选条件，已显示全部流水。"
            : $"已按 {validConditions.Count} 个条件筛选。";
    }

    private void Replace()
    {
        if (string.IsNullOrWhiteSpace(ReplaceFieldName))
        {
            MessageBox.Show("请选择替换字段。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrEmpty(ReplaceSource))
        {
            MessageBox.Show("请输入要替换的原内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var count = flowDetailsViewModel.ReplaceCurrentFilterValues(ReplaceFieldName, ReplaceSource, ReplaceTarget);
        StatusMessage = $"已替换 {count} 处内容。";
    }
}
