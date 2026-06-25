using SpeedEmulator.Infrastructure;

namespace SpeedEmulator.Models;

public sealed class FlowFilterCondition : ObservableObject
{
    private string fieldName = string.Empty;
    private string operatorName = string.Empty;
    private string value = string.Empty;

    public string FieldName
    {
        get => fieldName;
        set => SetProperty(ref fieldName, value);
    }

    public string OperatorName
    {
        get => operatorName;
        set => SetProperty(ref operatorName, value);
    }

    public string Value
    {
        get => value;
        set => SetProperty(ref this.value, value);
    }

    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(FieldName)
            && !string.IsNullOrWhiteSpace(OperatorName)
            && !string.IsNullOrWhiteSpace(Value);
    }

    public FlowFilterCondition Clone()
    {
        return new FlowFilterCondition
        {
            FieldName = FieldName,
            OperatorName = OperatorName,
            Value = Value
        };
    }
}
