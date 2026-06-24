using SpeedEmulator.Infrastructure;

namespace SpeedEmulator.Models;

public sealed class BankInterestFieldValue : ObservableObject
{
    private string value = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Field { get; set; } = string.Empty;

    public int Order { get; set; }

    public string Value
    {
        get => value;
        set => SetProperty(ref this.value, value ?? string.Empty);
    }

    public BankInterestFieldValue Clone()
    {
        return new BankInterestFieldValue
        {
            Name = Name,
            Field = Field,
            Order = Order,
            Value = Value
        };
    }
}
