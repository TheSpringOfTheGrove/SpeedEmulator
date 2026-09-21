using System.Windows;
using System.Windows.Input;

namespace SpeedEmulator.Views;

public partial class FormulaHelpWindow : Window
{
    public FormulaHelpWindow()
    {
        InitializeComponent();
        FormulaGrid.ItemsSource = new[]
        {
            new FormulaHelpRow("1", "(n)", "n 位纯数字", @"\\(12)\\", "12 个随机数字"),
            new FormulaHelpRow("2", "{n}", "n 位小写字母", @"\\{12}\\", "12 个随机小写字母"),
            new FormulaHelpRow("3", "<n>", "n 位大写字母", @"\\<12>\\", "12 个随机大写字母"),
            new FormulaHelpRow("4", "[n]", "数字和大写字母", @"\\[12]\\", "12 位 0-9/A-Z"),
            new FormulaHelpRow("5", "@n@", "数字和小写字母", @"\\@12@\\", "12 位 0-9/a-z"),
            new FormulaHelpRow("6", "#n#", "数字、大小写字母", @"\\#12#\\", "12 位 0-9/a-z/A-Z"),
            new FormulaHelpRow("7", "%日期格式%", "按本条流水交易时间格式化", @"\\%yyyyMMdd%\\", "20250619"),
            new FormulaHelpRow("8", "$Excel列名$", "非空数据随机无放回，用完后重洗", @"\\$姓名$\\", "宋力（每轮随机且覆盖）"),
            new FormulaHelpRow("9", "^Excel列名^", "同一条流水固定取 Excel 同一行", @"\\^姓名^\\ / \\^卡号^\\", "对应的姓名和卡号"),
            new FormulaHelpRow("10", "&字段名&", "复制本条流水另一显示列", @"\\&对方账号&\\", "与对方账号一致")
        };
        DateFormatGrid.ItemsSource = new[]
        {
            new DateFormatRow("yyyy", "年（四位）", "2025"),
            new DateFormatRow("yy", "年（两位）", "25"),
            new DateFormatRow("MM", "月（两位）", "06"),
            new DateFormatRow("M", "月（不补零）", "6"),
            new DateFormatRow("dd", "日（两位）", "19"),
            new DateFormatRow("d", "日（不补零）", "19"),
            new DateFormatRow("HH", "小时（24 小时制）", "14"),
            new DateFormatRow("hh", "小时（12 小时制）", "02"),
            new DateFormatRow("mm", "分钟", "30"),
            new DateFormatRow("ss", "秒", "15"),
            new DateFormatRow("fff", "毫秒（三位）", "123"),
            new DateFormatRow("tt", "上午/下午", "PM"),
            new DateFormatRow("dddd", "星期几全称", "Thursday"),
            new DateFormatRow("ddd", "星期几简称", "Thu")
        };
        CombinationExampleGrid.ItemsSource = new[]
        {
            new CombinationExampleRow(@"Na\\[5]%yyyy-MM-dd HH:mm:ss%#2#\\d", "Na2341A2025-06-19 14:30:15xYd"),
            new CombinationExampleRow(@"\\$姓名$\\", "宋力（当前轮不重复）"),
            new CombinationExampleRow(@"\\^姓名^\\ + \\^卡号^\\", "同一 Excel 行中的姓名与卡号"),
            new CombinationExampleRow(@"\\622230(13)\\", "6222301653254896535"),
            new CombinationExampleRow(@"\\&对方账号&\\", "复制已生成的对方账号")
        };
        FixedDateGrid.ItemsSource = new[]
        {
            new FixedDateHelpRow("+", "全局模式", "整个账期内随机分布"),
            new FixedDateHelpRow("*", "整月模式", "每个月分别随机分布"),
            new FixedDateHelpRow("=", "每天模式", "每个可交易日生成"),
            new FixedDateHelpRow("3-6", "日期范围模式", "每月 3 日至 6 日内随机生成"),
            new FixedDateHelpRow("1～31 整数", "固定日模式", "每月指定日期；短月自动取月末")
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        HelpScrollViewer.ScrollToVerticalOffset(HelpScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private sealed record FormulaHelpRow(string Number, string Formula, string Meaning, string FullSyntax, string Example);

    private sealed record DateFormatRow(string Token, string Meaning, string Example);

    private sealed record CombinationExampleRow(string Input, string Output);

    private sealed record FixedDateHelpRow(string Symbol, string Meaning, string Range);
}
