namespace SpeedEmulator.Models;

public static class BankInterestSettingDefaults
{
    private const string DefaultSettlementDay = "21";
    private const string DefaultMonths = "3;6;9;12";
    private const string DefaultStartTime = "0";
    private const string DefaultEndTime = "23";
    private const string DefaultRatePercent = "0.15";

    public static BankInterestSetting CreateDefault(Bank bank)
    {
        var setting = new BankInterestSetting
        {
            BankId = bank.Id,
            BankName = bank.Name,
            BankType = bank.Type,
            SettlementDay = DefaultSettlementDay,
            Months = DefaultMonths,
            StartTime = DefaultStartTime,
            EndTime = DefaultEndTime,
            RatePercent = DefaultRatePercent
        };

        setting.Fields.Add(new BankInterestFieldValue
        {
            Name = "摘要",
            Field = nameof(FlowRecord.ProductBrief),
            Value = "结息"
        });

        return setting;
    }

    public static bool HasEffectiveConfig(BankInterestSetting? setting)
    {
        return setting is not null
            && !string.IsNullOrWhiteSpace(setting.SettlementDay)
            && !string.IsNullOrWhiteSpace(setting.Months)
            && !string.IsNullOrWhiteSpace(setting.RatePercent);
    }
}
