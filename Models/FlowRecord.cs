using SpeedEmulator.Infrastructure;
using System.Text.Json.Serialization;

namespace SpeedEmulator.Models;

public sealed class FlowRecord : ObservableObject
{
    private int index;
    private long id;
    private int replaceIndex;
    private long bankId;
    private long bankUserId;
    private bool moveFlag;
    private DateTime? accountTime;
    private double? tradeMoney;
    private double? balance;
    private string incomeAttribute = string.Empty;
    private string account = string.Empty;
    private string productName = string.Empty;
    private string productCode = string.Empty;
    private string productBrief = string.Empty;
    private string productType = string.Empty;
    private string serialNum = string.Empty;
    private string operatorName = string.Empty;
    private string operatorNum = string.Empty;
    private string oppositeAccount = string.Empty;
    private string oppositeUsername = string.Empty;
    private string oppositeBank = string.Empty;
    private string branchNum = string.Empty;
    private string usage = string.Empty;
    private string appNum = string.Empty;
    private string sequenceNum = string.Empty;
    private string currency = "RMB";
    private string cashCheck = string.Empty;
    private string tradeCode = string.Empty;
    private string tradeCurrency = string.Empty;
    private string remark = string.Empty;
    private string depositTerm = string.Empty;
    private string agreedTerm = string.Empty;
    private string noticeType = string.Empty;
    private string areaNum = string.Empty;
    private string netNum = string.Empty;
    private string interfacePage = string.Empty;
    private string tradePlace = string.Empty;
    private string tradeChannel = string.Empty;
    private string tradeChannelEn = string.Empty;
    private string tradeExplain = string.Empty;
    private string accountNum = string.Empty;
    private string subAccountNum = string.Empty;
    private string voucherType = string.Empty;
    private string voucherNum = string.Empty;
    private string logNum = string.Empty;
    private string merchantName = string.Empty;
    private string terminalNum = string.Empty;
    private string handleStatus = string.Empty;
    private string year = string.Empty;
    private string creditType = string.Empty;
    private double? creditAmount;
    private double? debitAmount;
    private double? balanceAmount;
    private string receiptNum = string.Empty;
    private string incomeFlag = string.Empty;
    private Dictionary<string, string> extraFields = [];

    public int Index { get => index; set => SetProperty(ref index, value); }
    public long Id { get => id; set => SetProperty(ref id, value); }
    public int ReplaceIndex { get => replaceIndex; set => SetProperty(ref replaceIndex, value); }
    public long BankId { get => bankId; set => SetProperty(ref bankId, value); }
    public long BankUserId { get => bankUserId; set => SetProperty(ref bankUserId, value); }
    public bool MoveFlag { get => moveFlag; set => SetProperty(ref moveFlag, value); }
    public DateTime? AccountTime { get => accountTime; set => SetProperty(ref accountTime, value); }
    public double? TradeMoney { get => tradeMoney; set => SetProperty(ref tradeMoney, value); }
    public double? Balance { get => balance; set => SetProperty(ref balance, value); }
    public string IncomeAttribute { get => incomeAttribute; set => SetProperty(ref incomeAttribute, value); }
    public string Account { get => account; set => SetProperty(ref account, value); }
    public string ProductName { get => productName; set => SetProperty(ref productName, value); }
    public string ProductCode { get => productCode; set => SetProperty(ref productCode, value); }
    public string ProductBrief { get => productBrief; set => SetProperty(ref productBrief, value); }
    public string ProductType { get => productType; set => SetProperty(ref productType, value); }
    public string SerialNum { get => serialNum; set => SetProperty(ref serialNum, value); }
    public string Operator { get => operatorName; set => SetProperty(ref operatorName, value); }
    public string OperatorNum { get => operatorNum; set => SetProperty(ref operatorNum, value); }
    public string OppositeAccount { get => oppositeAccount; set => SetProperty(ref oppositeAccount, value); }
    public string OppositeUsername { get => oppositeUsername; set => SetProperty(ref oppositeUsername, value); }
    public string OppositeBank { get => oppositeBank; set => SetProperty(ref oppositeBank, value); }
    public string BranchNum { get => branchNum; set => SetProperty(ref branchNum, value); }
    public string Usage { get => usage; set => SetProperty(ref usage, value); }
    public string AppNum { get => appNum; set => SetProperty(ref appNum, value); }
    public string SequenceNum { get => sequenceNum; set => SetProperty(ref sequenceNum, value); }
    public string Currency { get => currency; set => SetProperty(ref currency, value); }
    public string CashCheck { get => cashCheck; set => SetProperty(ref cashCheck, value); }
    public string TradeCode { get => tradeCode; set => SetProperty(ref tradeCode, value); }
    public string TradeCurrency { get => tradeCurrency; set => SetProperty(ref tradeCurrency, value); }
    public string Remark { get => remark; set => SetProperty(ref remark, value); }
    public string DepositTerm { get => depositTerm; set => SetProperty(ref depositTerm, value); }
    public string AgreedTerm { get => agreedTerm; set => SetProperty(ref agreedTerm, value); }
    public string NoticeType { get => noticeType; set => SetProperty(ref noticeType, value); }
    public string AreaNum { get => areaNum; set => SetProperty(ref areaNum, value); }
    public string NetNum { get => netNum; set => SetProperty(ref netNum, value); }
    public string InterfacePage { get => interfacePage; set => SetProperty(ref interfacePage, value); }
    public string TradePlace { get => tradePlace; set => SetProperty(ref tradePlace, value); }
    public string TradeChannel { get => tradeChannel; set => SetProperty(ref tradeChannel, value); }
    public string TradeChannelEn { get => tradeChannelEn; set => SetProperty(ref tradeChannelEn, value); }
    public string TradeExplain { get => tradeExplain; set => SetProperty(ref tradeExplain, value); }
    public string AccountNum { get => accountNum; set => SetProperty(ref accountNum, value); }
    public string SubAccountNum { get => subAccountNum; set => SetProperty(ref subAccountNum, value); }
    public string VoucherType { get => voucherType; set => SetProperty(ref voucherType, value); }
    public string VoucherNum { get => voucherNum; set => SetProperty(ref voucherNum, value); }
    public string LogNum { get => logNum; set => SetProperty(ref logNum, value); }
    public string MerchantName { get => merchantName; set => SetProperty(ref merchantName, value); }
    public string TerminalNum { get => terminalNum; set => SetProperty(ref terminalNum, value); }
    public string HandleStatus { get => handleStatus; set => SetProperty(ref handleStatus, value); }
    public string Year { get => year; set => SetProperty(ref year, value); }
    public string CreditType { get => creditType; set => SetProperty(ref creditType, value); }
    public double? CreditAmount { get => creditAmount; set => SetProperty(ref creditAmount, value); }
    public double? DebitAmount { get => debitAmount; set => SetProperty(ref debitAmount, value); }
    public double? BalanceAmount { get => balanceAmount; set => SetProperty(ref balanceAmount, value); }
    public string ReceiptNum { get => receiptNum; set => SetProperty(ref receiptNum, value); }
    public string IncomeFlag { get => incomeFlag; set => SetProperty(ref incomeFlag, value); }

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

    public FlowRecord Clone()
    {
        var copy = (FlowRecord)MemberwiseClone();
        copy.extraFields = ExtraFields.ToDictionary(item => item.Key, item => item.Value);
        return copy;
    }
}
