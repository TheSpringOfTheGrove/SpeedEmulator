using System.Collections.ObjectModel;
using SpeedEmulator.Infrastructure;

namespace SpeedEmulator.Models;

public sealed class BankInterestSetting : ObservableObject
{
    private string settlementDay = string.Empty;
    private string months = string.Empty;
    private string startTime = string.Empty;
    private string endTime = string.Empty;
    private string ratePercent = string.Empty;

    public long BankId { get; set; }

    public string BankName { get; set; } = string.Empty;

    public string BankType { get; set; } = string.Empty;

    public string SettlementDay
    {
        get => settlementDay;
        set => SetProperty(ref settlementDay, value ?? string.Empty);
    }

    public string Months
    {
        get => months;
        set => SetProperty(ref months, value ?? string.Empty);
    }

    public string StartTime
    {
        get => startTime;
        set => SetProperty(ref startTime, value ?? string.Empty);
    }

    public string EndTime
    {
        get => endTime;
        set => SetProperty(ref endTime, value ?? string.Empty);
    }

    public string RatePercent
    {
        get => ratePercent;
        set => SetProperty(ref ratePercent, value ?? string.Empty);
    }

    public ObservableCollection<BankInterestFieldValue> Fields { get; set; } = [];

    public BankInterestSetting Clone()
    {
        return new BankInterestSetting
        {
            BankId = BankId,
            BankName = BankName,
            BankType = BankType,
            SettlementDay = SettlementDay,
            Months = Months,
            StartTime = StartTime,
            EndTime = EndTime,
            RatePercent = RatePercent,
            Fields = new ObservableCollection<BankInterestFieldValue>(
                Fields.Select(item => item.Clone()))
        };
    }
}
