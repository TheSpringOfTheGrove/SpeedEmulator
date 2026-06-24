using SpeedEmulator.Infrastructure;

namespace SpeedEmulator.Models;

public sealed class Bank : ObservableObject
{
    private long id;
    private string code = string.Empty;
    private string name = string.Empty;
    private string type = string.Empty;
    private double? rate;
    private bool isReadConfigExcel;

    public long Id
    {
        get => id;
        set => SetProperty(ref id, value);
    }

    public string Code
    {
        get => code;
        set => SetProperty(ref code, value);
    }

    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    public double? Rate
    {
        get => rate;
        set => SetProperty(ref rate, value);
    }

    public string Type
    {
        get => type;
        set => SetProperty(ref type, value);
    }

    public List<ColumnDefinition> Columns { get; } = [];

    public List<ColumnDefinition> ReferenceColumns { get; } = [];

    public List<ColumnDefinition> ConstColumns { get; } = [];

    public List<ColumnDefinition> FlowColumns { get; } = [];

    public bool IsReadConfigExcel
    {
        get => isReadConfigExcel;
        set => SetProperty(ref isReadConfigExcel, value);
    }

    public string GetBankType()
    {
        return Type == BankTypes.Personal ? "个人" : Type == BankTypes.Corporate ? "对公" : Type;
    }
}

public static class BankTypes
{
    public const string Personal = "个人";
    public const string Corporate = "对公";
    public const string Local = "地方";
    public const string Receipt = "凭条";
}
