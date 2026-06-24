using SpeedEmulator.Infrastructure;
using System.Text.Json.Serialization;

namespace SpeedEmulator.Models;

public sealed class BankUser : ObservableObject
{
    private long id;
    private long backendId;
    private long bankId;
    private string bankName = string.Empty;
    private string userCode = string.Empty;
    private string accountName = string.Empty;
    private string accountNo = string.Empty;
    private string idNumber = string.Empty;
    private string phoneNumber = string.Empty;
    private string openBranch = string.Empty;
    private decimal balance;
    private string loginPassword = string.Empty;
    private string paymentPassword = string.Empty;
    private string uShieldNo = string.Empty;
    private string remark = string.Empty;
    private DateTime startDate = DateTime.Today;
    private DateTime endDate = DateTime.Today.AddYears(1);
    private string transactionType = string.Empty;
    private string currency = "RMB";
    private string chapterCode = string.Empty;
    private string chapterBranch = string.Empty;
    private bool shouldPrintSeal;
    private decimal openingBalance;
    private bool autoCalculateInterest;
    private string sealImagePath = string.Empty;
    private Dictionary<string, string> extraFields = [];
    private DateTime createdAt = DateTime.Now;
    private DateTime updatedAt = DateTime.Now;

    public long Id
    {
        get => id;
        set => SetProperty(ref id, value);
    }

    public long BackendId
    {
        get => backendId;
        set => SetProperty(ref backendId, value);
    }

    public long BankId
    {
        get => bankId;
        set => SetProperty(ref bankId, value);
    }

    public string BankName
    {
        get => bankName;
        set => SetProperty(ref bankName, value);
    }

    public string UserCode
    {
        get => userCode;
        set => SetProperty(ref userCode, value);
    }

    public string AccountName
    {
        get => accountName;
        set => SetProperty(ref accountName, value);
    }

    public string AccountNo
    {
        get => accountNo;
        set => SetProperty(ref accountNo, value);
    }

    public string IdNumber
    {
        get => idNumber;
        set => SetProperty(ref idNumber, value);
    }

    public string PhoneNumber
    {
        get => phoneNumber;
        set => SetProperty(ref phoneNumber, value);
    }

    public string OpenBranch
    {
        get => openBranch;
        set => SetProperty(ref openBranch, value);
    }

    public decimal Balance
    {
        get => balance;
        set => SetProperty(ref balance, value);
    }

    public string LoginPassword
    {
        get => loginPassword;
        set => SetProperty(ref loginPassword, value);
    }

    public string PaymentPassword
    {
        get => paymentPassword;
        set => SetProperty(ref paymentPassword, value);
    }

    public string UShieldNo
    {
        get => uShieldNo;
        set => SetProperty(ref uShieldNo, value);
    }

    public string Remark
    {
        get => remark;
        set => SetProperty(ref remark, value);
    }

    public DateTime StartDate
    {
        get => startDate;
        set => SetProperty(ref startDate, value);
    }

    public DateTime EndDate
    {
        get => endDate;
        set => SetProperty(ref endDate, value);
    }

    public string TransactionType
    {
        get => transactionType;
        set => SetProperty(ref transactionType, value);
    }

    public string Currency
    {
        get => currency;
        set => SetProperty(ref currency, value);
    }

    public string ChapterCode
    {
        get => chapterCode;
        set => SetProperty(ref chapterCode, value);
    }

    public string ChapterBranch
    {
        get => chapterBranch;
        set => SetProperty(ref chapterBranch, value);
    }

    public bool ShouldPrintSeal
    {
        get => shouldPrintSeal;
        set => SetProperty(ref shouldPrintSeal, value);
    }

    public decimal OpeningBalance
    {
        get => openingBalance;
        set => SetProperty(ref openingBalance, value);
    }

    public bool AutoCalculateInterest
    {
        get => autoCalculateInterest;
        set => SetProperty(ref autoCalculateInterest, value);
    }

    public string SealImagePath
    {
        get => sealImagePath;
        set => SetProperty(ref sealImagePath, value);
    }

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

    public DateTime CreatedAt
    {
        get => createdAt;
        set => SetProperty(ref createdAt, value);
    }

    public DateTime UpdatedAt
    {
        get => updatedAt;
        set => SetProperty(ref updatedAt, value);
    }

    public static BankUser CreateDraft(Bank bank)
    {
        return new BankUser
        {
            BankId = bank.Id,
            BankName = bank.Name,
            UserCode = $"{bank.Name}-{DateTime.Now:HHmmss}",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddYears(1),
            Currency = "RMB",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
    }

    public BankUser Clone()
    {
        return new BankUser
        {
            Id = Id,
            BackendId = BackendId,
            BankId = BankId,
            BankName = BankName,
            UserCode = UserCode,
            AccountName = AccountName,
            AccountNo = AccountNo,
            IdNumber = IdNumber,
            PhoneNumber = PhoneNumber,
            OpenBranch = OpenBranch,
            Balance = Balance,
            LoginPassword = LoginPassword,
            PaymentPassword = PaymentPassword,
            UShieldNo = UShieldNo,
            Remark = Remark,
            StartDate = StartDate,
            EndDate = EndDate,
            TransactionType = TransactionType,
            Currency = Currency,
            ChapterCode = ChapterCode,
            ChapterBranch = ChapterBranch,
            ShouldPrintSeal = ShouldPrintSeal,
            OpeningBalance = OpeningBalance,
            AutoCalculateInterest = AutoCalculateInterest,
            SealImagePath = SealImagePath,
            ExtraFields = ExtraFields.ToDictionary(item => item.Key, item => item.Value),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }
}
