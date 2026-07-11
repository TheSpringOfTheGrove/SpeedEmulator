using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using SpeedEmulator.Infrastructure;

namespace SpeedEmulator.Models;

public sealed class FlowGenerationConfig : ObservableObject
{
    private int selectIndex;
    private DateTime startTime = DateTime.Now;
    private DateTime endTime = DateTime.Now;
    private double openingBalance;
    private double allInMoney = 30000;
    private double allOutMoney;
    private double lastMoney = 10000;
    private double minInMoneyMonth1 = 3000;
    private double maxInMoneyMonth1 = 3000;
    private double minOutMoneyMonth1 = 2000;
    private double maxOutMoneyMonth1 = 2000;
    private double minInMoneyMonth2 = 3000;
    private double maxInMoneyMonth2 = 3000;
    private double minOutMoneyMonth2 = 2000;
    private double maxOutMoneyMonth2 = 2000;

    public int SelectIndex
    {
        get => selectIndex;
        set => SetProperty(ref selectIndex, value);
    }

    public DateTime StartTime
    {
        get => startTime;
        set => SetProperty(ref startTime, value);
    }

    public DateTime EndTime
    {
        get => endTime;
        set => SetProperty(ref endTime, value);
    }

    public double OpeningBalance
    {
        get => openingBalance;
        set => SetProperty(ref openingBalance, value);
    }

    public double AllInMoney
    {
        get => allInMoney;
        set => SetProperty(ref allInMoney, value);
    }

    public double AllOutMoney
    {
        get => allOutMoney;
        set => SetProperty(ref allOutMoney, value);
    }

    public double LastMoney
    {
        get => lastMoney;
        set => SetProperty(ref lastMoney, value);
    }

    public double MinInMoneyMonth1
    {
        get => minInMoneyMonth1;
        set => SetProperty(ref minInMoneyMonth1, value);
    }

    public double MaxInMoneyMonth1
    {
        get => maxInMoneyMonth1;
        set => SetProperty(ref maxInMoneyMonth1, value);
    }

    public double MinOutMoneyMonth1
    {
        get => minOutMoneyMonth1;
        set => SetProperty(ref minOutMoneyMonth1, value);
    }

    public double MaxOutMoneyMonth1
    {
        get => maxOutMoneyMonth1;
        set => SetProperty(ref maxOutMoneyMonth1, value);
    }

    public double MinInMoneyMonth2
    {
        get => minInMoneyMonth2;
        set => SetProperty(ref minInMoneyMonth2, value);
    }

    public double MaxInMoneyMonth2
    {
        get => maxInMoneyMonth2;
        set => SetProperty(ref maxInMoneyMonth2, value);
    }

    public double MinOutMoneyMonth2
    {
        get => minOutMoneyMonth2;
        set => SetProperty(ref minOutMoneyMonth2, value);
    }

    public double MaxOutMoneyMonth2
    {
        get => maxOutMoneyMonth2;
        set => SetProperty(ref maxOutMoneyMonth2, value);
    }

    public ObservableCollection<MonthGenerateRule> MonthGenData { get; set; } = [];

    public FlowGenerationConfig Clone()
    {
        var copy = new FlowGenerationConfig
        {
            SelectIndex = SelectIndex,
            StartTime = StartTime,
            EndTime = EndTime,
            OpeningBalance = OpeningBalance,
            AllInMoney = AllInMoney,
            AllOutMoney = AllOutMoney,
            LastMoney = LastMoney,
            MinInMoneyMonth1 = MinInMoneyMonth1,
            MaxInMoneyMonth1 = MaxInMoneyMonth1,
            MinOutMoneyMonth1 = MinOutMoneyMonth1,
            MaxOutMoneyMonth1 = MaxOutMoneyMonth1,
            MinInMoneyMonth2 = MinInMoneyMonth2,
            MaxInMoneyMonth2 = MaxInMoneyMonth2,
            MinOutMoneyMonth2 = MinOutMoneyMonth2,
            MaxOutMoneyMonth2 = MaxOutMoneyMonth2
        };

        foreach (var item in MonthGenData)
        {
            copy.MonthGenData.Add(item.Clone());
        }

        return copy;
    }
}

public sealed class MonthGenerateRule : ObservableObject
{
    private DateTime startTime = DateTime.Now;
    private DateTime endTime = DateTime.Now;
    private double inMoney;
    private double outMoney;

    public DateTime StartTime
    {
        get => startTime;
        set => SetProperty(ref startTime, value);
    }

    public DateTime EndTime
    {
        get => endTime;
        set => SetProperty(ref endTime, value);
    }

    public double InMoney
    {
        get => inMoney;
        set => SetProperty(ref inMoney, value);
    }

    public double OutMoney
    {
        get => outMoney;
        set => SetProperty(ref outMoney, value);
    }

    public MonthGenerateRule Clone()
    {
        return new MonthGenerateRule
        {
            StartTime = StartTime,
            EndTime = EndTime,
            InMoney = InMoney,
            OutMoney = OutMoney
        };
    }
}

public abstract class FlowRuleBase : ObservableObject
{
    private Dictionary<string, string> extraFields = [];
    private bool isCheck;
    private int index;

    public int Index
    {
        get => index;
        set => SetProperty(ref index, value);
    }

    public bool IsCheck
    {
        get => isCheck;
        set => SetProperty(ref isCheck, value);
    }

    public long Id { get; set; }

    public long BankId { get; set; }

    public string? IncomeAttribute { get; set; }

    public double? MinMoney { get; set; }

    public double? MaxMoney { get; set; }

    public int? FloutLength { get; set; }

    public int? StartDay { get; set; }

    public int? EndDay { get; set; }

    public bool? TradeHoliday { get; set; }

    public bool? TradeWeekend { get; set; }

    public string? IncomeType { get; set; }

    public string? CrossBankBrief { get; set; }

    public double? CrossBankRate { get; set; }

    public double? CrossBankMin { get; set; }

    public double? CrossBankMax { get; set; }

    public string? OffSiteBankBrief { get; set; }

    public double? OffSiteBankRate { get; set; }

    public double? OffSiteBankMin { get; set; }

    public double? OffSiteBankMax { get; set; }

    public string? Account { get; set; }

    public string? ProductName { get; set; }

    public string? ProductCode { get; set; }

    public string? ProductBrief { get; set; }

    public string? ProductType { get; set; }

    public string? SerialNum { get; set; }

    public string? Operator { get; set; }

    public string? OperatorNum { get; set; }

    public string? OppositeAccount { get; set; }

    public string? OppositeUsername { get; set; }

    public string? OppositeBank { get; set; }

    public string? BranchNum { get; set; }

    public string? Usage { get; set; }

    public string? AppNum { get; set; }

    public string? SequenceNum { get; set; }

    public string? Currency { get; set; }

    public string? CashCheck { get; set; }

    public string? TradeCode { get; set; }

    public string? TradeCurrency { get; set; }

    public string? Remark { get; set; }

    public string? DepositTerm { get; set; }

    public string? AgreedTerm { get; set; }

    public string? NoticeType { get; set; }

    public string? AreaNum { get; set; }

    public string? NetNum { get; set; }

    public string? InterfacePage { get; set; }

    public string? TradePlace { get; set; }

    public string? TradeChannel { get; set; }

    public string? TradeChannelEn { get; set; }

    public string? TradeExplain { get; set; }

    public string? AccountNum { get; set; }

    public string? SubAccountNum { get; set; }

    public string? VoucherType { get; set; }

    public string? VoucherNum { get; set; }

    public string? LogNum { get; set; }

    public string? MerchantName { get; set; }

    public string? TerminalNum { get; set; }

    public string? HandleStatus { get; set; }

    public string? Year { get; set; }

    public double? CreditAmount { get; set; }

    public double? DebitAmount { get; set; }

    public double? BalanceAmount { get; set; }

    public string? ReceiptNum { get; set; }

    public Dictionary<string, string> ExtraFields
    {
        get => extraFields;
        set
        {
            if (SetProperty(ref extraFields, value ?? []))
            {
                OnPropertyChanged("Item[]");
            }
        }
    }

    [JsonIgnore]
    public string this[string fieldName]
    {
        get
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return string.Empty;
            }

            return ExtraFields.TryGetValue(fieldName, out var value) ? value : string.Empty;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return;
            }

            var normalized = value ?? string.Empty;
            if (ExtraFields.TryGetValue(fieldName, out var oldValue) && oldValue == normalized)
            {
                return;
            }

            ExtraFields[fieldName] = normalized;
            OnPropertyChanged($"Item[{fieldName}]");
            OnPropertyChanged("Item[]");
            OnPropertyChanged(nameof(ExtraFields));
        }
    }

    protected void CopyBaseTo(FlowRuleBase target)
    {
        target.Index = Index;
        target.IsCheck = IsCheck;
        target.Id = Id;
        target.BankId = BankId;
        target.IncomeAttribute = IncomeAttribute;
        target.MinMoney = MinMoney;
        target.MaxMoney = MaxMoney;
        target.FloutLength = FloutLength;
        target.StartDay = StartDay;
        target.EndDay = EndDay;
        target.TradeHoliday = TradeHoliday;
        target.TradeWeekend = TradeWeekend;
        target.IncomeType = IncomeType;
        target.CrossBankBrief = CrossBankBrief;
        target.CrossBankRate = CrossBankRate;
        target.CrossBankMin = CrossBankMin;
        target.CrossBankMax = CrossBankMax;
        target.OffSiteBankBrief = OffSiteBankBrief;
        target.OffSiteBankRate = OffSiteBankRate;
        target.OffSiteBankMin = OffSiteBankMin;
        target.OffSiteBankMax = OffSiteBankMax;
        target.Account = Account;
        target.ProductName = ProductName;
        target.ProductCode = ProductCode;
        target.ProductBrief = ProductBrief;
        target.ProductType = ProductType;
        target.SerialNum = SerialNum;
        target.Operator = Operator;
        target.OperatorNum = OperatorNum;
        target.OppositeAccount = OppositeAccount;
        target.OppositeUsername = OppositeUsername;
        target.OppositeBank = OppositeBank;
        target.BranchNum = BranchNum;
        target.Usage = Usage;
        target.AppNum = AppNum;
        target.SequenceNum = SequenceNum;
        target.Currency = Currency;
        target.CashCheck = CashCheck;
        target.TradeCode = TradeCode;
        target.TradeCurrency = TradeCurrency;
        target.Remark = Remark;
        target.DepositTerm = DepositTerm;
        target.AgreedTerm = AgreedTerm;
        target.NoticeType = NoticeType;
        target.AreaNum = AreaNum;
        target.NetNum = NetNum;
        target.InterfacePage = InterfacePage;
        target.TradePlace = TradePlace;
        target.TradeChannel = TradeChannel;
        target.TradeChannelEn = TradeChannelEn;
        target.TradeExplain = TradeExplain;
        target.AccountNum = AccountNum;
        target.SubAccountNum = SubAccountNum;
        target.VoucherType = VoucherType;
        target.VoucherNum = VoucherNum;
        target.LogNum = LogNum;
        target.MerchantName = MerchantName;
        target.TerminalNum = TerminalNum;
        target.HandleStatus = HandleStatus;
        target.Year = Year;
        target.CreditAmount = CreditAmount;
        target.DebitAmount = DebitAmount;
        target.BalanceAmount = BalanceAmount;
        target.ReceiptNum = ReceiptNum;
        target.ExtraFields = ExtraFields.ToDictionary(item => item.Key, item => item.Value);
    }
}

public sealed class GenerateReferenceRule : FlowRuleBase
{
    public int? PercentMonth { get; set; }

    public GenerateReferenceRule Clone()
    {
        var copy = new GenerateReferenceRule
        {
            PercentMonth = PercentMonth
        };

        CopyBaseTo(copy);
        return copy;
    }

    public static GenerateReferenceRule CreateDefault(long bankId)
    {
        return new GenerateReferenceRule
        {
            BankId = bankId,
            IsCheck = false,
            IncomeAttribute = "收入",
            MinMoney = 10,
            MaxMoney = 1000,
            TradeHoliday = false,
            TradeWeekend = false,
            CrossBankRate = 0,
            CrossBankMin = 0,
            CrossBankMax = 0,
            OffSiteBankRate = 0,
            OffSiteBankMin = 0,
            OffSiteBankMax = 0
        };
    }
}

public sealed class GenerateConstRule : FlowRuleBase
{
    public string? FixDay { get; set; }

    public string? ReCnt { get; set; }

    public int FixType
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FixDay))
            {
                return -1;
            }

            if (FixDay.Contains(','))
            {
                return 2;
            }

            if (int.TryParse(FixDay, out _))
            {
                return 1;
            }

            return 0;
        }
    }

    public GenerateConstRule Clone()
    {
        var copy = new GenerateConstRule
        {
            FixDay = FixDay,
            ReCnt = ReCnt
        };

        CopyBaseTo(copy);
        return copy;
    }

    public static GenerateConstRule CreateDefault(long bankId)
    {
        return new GenerateConstRule
        {
            BankId = bankId,
            IsCheck = false,
            IncomeAttribute = "收入",
            MinMoney = 10,
            MaxMoney = 1000,
            TradeHoliday = false,
            TradeWeekend = false
        };
    }
}

public sealed class FlowGenerationSnapshot
{
    public FlowGenerationConfig Config { get; init; } = new();

    public List<GenerateReferenceRule> References { get; init; } = [];

    public List<GenerateConstRule> ConstItems { get; init; } = [];
}
