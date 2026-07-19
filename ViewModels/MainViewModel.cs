using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using SpeedEmulator.Infrastructure;
using SpeedEmulator.Models;
using SpeedEmulator.Services;

namespace SpeedEmulator.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const string DefaultContactText = "客服联系方式\nQQ号：2295429421\n没加好友的加下QQ好友，有问题或者高低配文字咨询客服。";
    private const string DefaultAnnouncementText = "各位老板、发大财；有什么需要优化的地方，请添加客服反馈！";

    private readonly FrontSession session;
    private readonly Action<Bank>? openBankUsers;
    private readonly IFrontApiClient? frontApiClient;
    private Bank? selectedBank;
    private string statusMessage = "请选择银行进入用户列表";
    private string contactText = DefaultContactText;
    private string announcementText = DefaultAnnouncementText;

    public MainViewModel(FrontSession session, Action<Bank>? openBankUsers = null, IFrontApiClient? frontApiClient = null)
    {
        this.session = session;
        this.openBankUsers = openBankUsers;
        this.frontApiClient = frontApiClient;
        ExeId = session.MachineCode;
        OpenBankCommand = new RelayCommand(OpenBank);
        ShowAppSettingCommand = new RelayCommand(() => StatusMessage = "设置入口已预留：授权、公告、客服、素材路径。");

        SeedBanks();
        _ = LoadAnnouncementAsync();
    }

    public string ExeId { get; }

    public string WindowTitle => $"极速财务软件-版本({AppVersion.DisplayVersion})";

    public string CurrentYear => DateTime.Now.Year.ToString();

    public string AccountDisplay => string.IsNullOrWhiteSpace(session.DisplayName) ? session.Account : session.DisplayName;

    public ObservableCollection<Bank> GeRenBanks { get; } = [];

    public ObservableCollection<Bank> DuiGongBanks { get; } = [];

    public ObservableCollection<Bank> DiFangBanks { get; } = [];

    public ObservableCollection<Bank> ReceiptBanks { get; } = [];

    public ICommand OpenBankCommand { get; }

    public ICommand ShowAppSettingCommand { get; }

    public Bank? SelectedBank
    {
        get => selectedBank;
        private set => SetProperty(ref selectedBank, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string ContactText
    {
        get => contactText;
        private set => SetProperty(ref contactText, value);
    }

    public string AnnouncementText
    {
        get => announcementText;
        private set => SetProperty(ref announcementText, value);
    }

    public int TotalBankCount => GeRenBanks.Count + DuiGongBanks.Count + DiFangBanks.Count + ReceiptBanks.Count;

    private async Task LoadAnnouncementAsync()
    {
        if (frontApiClient is null)
        {
            return;
        }

        try
        {
            var announcement = await frontApiClient.GetAnnouncementAsync();
            ContactText = UseDefaultWhenBlank(announcement.ContactText, DefaultContactText);
            AnnouncementText = UseDefaultWhenBlank(announcement.AnnouncementText, DefaultAnnouncementText);
        }
        catch (Exception ex)
        {
            ContactText = DefaultContactText;
            AnnouncementText = DefaultAnnouncementText;
            Debug.WriteLine($"Load announcement failed: {ex.Message}");
        }
    }

    private static string UseDefaultWhenBlank(string? value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private void OpenBank(object? parameter)
    {
        if (parameter is not Bank bank)
        {
            return;
        }

        SelectedBank = bank;
        StatusMessage = $"已选择：{bank.Name}（{bank.GetBankType()}），正在打开用户列表。";
        openBankUsers?.Invoke(bank);
    }

    private void SeedBanks()
    {
        var knownBanks = CreateKnownBanks();
        foreach (var bank in knownBanks)
        {
            AddAuthorizedBank(bank);
        }

        StatusMessage = $"{AccountDisplay} 已登录，已加载全部银行 {TotalBankCount} 个。";

        OnPropertyChanged(nameof(TotalBankCount));
    }

    private static List<Bank> CreateKnownBanks()
    {
        var banks = new List<Bank>();

        AddRange(banks, BankTypes.Personal,
            "支付宝", "微信", "个人农商",
            "工行", "光大", "广发", "华夏", "建行", "交行", "民生",
            "农行", "平安", "浦发", "兴业", "邮政", "招行", "中信", "中行");

        AddRange(banks, BankTypes.Corporate,
            "对公农商", "民生对公", "中信对公", "工行对公", "农行对公", "中行对公", "光大对公", "平安对公", "广发对公", "浦发对公", "华夏对公", "兴业对公", "建行对公", "邮政对公", "交行对公", "招行对公");

        return banks;
    }

    private void AddAuthorizedBank(Bank bank)
    {
        switch (bank.Type)
        {
            case BankTypes.Corporate:
                DuiGongBanks.Add(bank);
                break;
            case BankTypes.Local:
                DiFangBanks.Add(bank);
                break;
            case BankTypes.Receipt:
                ReceiptBanks.Add(bank);
                break;
            default:
                GeRenBanks.Add(bank);
                break;
        }
    }

    private static void AddRange(ICollection<Bank> target, string type, params string[] names)
    {
        var startId = type switch
        {
            BankTypes.Personal => 1,
            BankTypes.Corporate => 101,
            BankTypes.Local => 201,
            _ => 301
        };

        for (var index = 0; index < names.Length; index++)
        {
            target.Add(CreateBank(startId + index, names[index], type, index % 3 == 0));
        }
    }

    private static Bank CreateBank(long id, string name, string type, bool isReadConfigExcel)
    {
        var bank = new Bank
        {
            Id = id,
            Code = BuildBankCode(type, name),
            Name = name,
            Type = type,
            Rate = 0,
            IsReadConfigExcel = isReadConfigExcel
        };

        ConfigureBankColumns(bank);
        return bank;
    }

    private static long CreateAuthorizedBankId(AuthorizedBankInfo authorizedBank, string type)
    {
        var raw = $"{type}|{authorizedBank.Code}|{authorizedBank.Name}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var value = BitConverter.ToUInt32(hash, 0);
        var typeBase = type switch
        {
            BankTypes.Personal => 10_000,
            BankTypes.Corporate => 20_000,
            BankTypes.Local => 30_000,
            BankTypes.Receipt => 40_000,
            _ => 90_000
        };

        return typeBase + value % 8_000;
    }

    private static string NormalizeBankType(string? category)
    {
        return category?.Trim() switch
        {
            BankTypes.Personal => BankTypes.Personal,
            BankTypes.Corporate => BankTypes.Corporate,
            BankTypes.Local => BankTypes.Local,
            BankTypes.Receipt => BankTypes.Receipt,
            _ => BankTypes.Personal
        };
    }

    private static string BuildBankCode(string type, string name)
    {
        var prefix = type switch
        {
            BankTypes.Personal => "P",
            BankTypes.Corporate => "C",
            BankTypes.Local => "L",
            _ => "R"
        };

        return $"{prefix}_{JavaStringHashCode(name):X}";
    }

    private static int JavaStringHashCode(string value)
    {
        unchecked
        {
            var hash = 0;
            foreach (var ch in value)
            {
                hash = (31 * hash) + ch;
            }

            return hash;
        }
    }

    private static void ConfigureBankColumns(Bank bank)
    {
        bank.Columns.Clear();

        if (BankUserColumnCatalog.TryGetColumns(bank.Name, out var configuredColumns))
        {
            AddColumns(bank, CreateBankUserColumns(bank.Name, configuredColumns).ToArray());
        }
        else
        {
            AddDefaultBankUserColumns(bank);
        }

        ConfigureFlowGenerationColumns(bank);
    }

    private static IEnumerable<ColumnDefinition> CreateBankUserColumns(string bankName, IReadOnlyList<string> columnNames)
    {
        var usedFixedFields = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < columnNames.Count; index++)
        {
            var columnName = columnNames[index];
            var (field, type) = ResolveBankUserField(columnName);
            if (field is null || ShouldUseExtraField(field, usedFixedFields))
            {
                field = CreateExtraFieldPath(bankName, columnName, index);
                type = InferExtraBankUserFieldType(columnName);
            }

            yield return Column((index + 1) * 10, columnName, field, GetBankUserColumnWidth(columnName, type), type);
        }
    }

    private static bool ShouldUseExtraField(string field, HashSet<string> usedFixedFields)
    {
        if (field == nameof(BankUser.Id))
        {
            return false;
        }

        return !usedFixedFields.Add(field);
    }

    private static (string? Field, string Type) ResolveBankUserField(string columnName)
    {
        var normalizedName = NormalizeColumnName(columnName);
        if (IsCardNumberColumn(normalizedName))
        {
            return (nameof(BankUser.CardNo), "Text");
        }

        if (IsAccountNumberColumn(normalizedName))
        {
            return (nameof(BankUser.AccountNo), "Text");
        }

        return columnName switch
        {
            "ID" => (nameof(BankUser.Id), "Text"),
            "起始日期" or "开始日期" or "开始对账日期" or "查询起日" or "起息日期" => (nameof(BankUser.StartDate), "Date"),
            "终止日期" or "结束日期" or "截止日期" or "结束对账日期" or "查询止日" => (nameof(BankUser.EndDate), "Date"),
            "支付宝账户" or "微信号" or "账号" or "卡号" or "卡号账户" or "账号卡号" or "客户账号" or "户口号" or "账户账号" or "客户账口" or "度号" => (nameof(BankUser.AccountNo), "Text"),
            "姓名" or "户名" or "客户姓名" or "账户名称" or "户口名称" or "客户名称" or "公司名称" or "单位名称" or "账户名" or "客户户名" or "存款人名称" => (nameof(BankUser.AccountName), "Text"),
            "身份证" or "身份证号" or "证件号" or "证件号码" or "证件编号" => (nameof(BankUser.IdNumber), "Text"),
            "编号" or "序号" => (nameof(BankUser.UserCode), "Text"),
            "开户行" or "开户机构" or "开户网点" => (nameof(BankUser.OpenBranch), "Text"),
            "余额" => (nameof(BankUser.Balance), "Money"),
            "交易类型" => (nameof(BankUser.TransactionType), "Text"),
            "币种" or "货币" or "币别" => (nameof(BankUser.Currency), "Text"),
            "章内编码" => (nameof(BankUser.ChapterCode), "Text"),
            "章内支行" => (nameof(BankUser.ChapterBranch), "Text"),
            "是否打印章" => (nameof(BankUser.ShouldPrintSeal), "Boolean"),
            "备注" => (nameof(BankUser.Remark), "Text"),
            "期初余额" or "期初余颜" => (nameof(BankUser.OpeningBalance), "Money"),
            "自动计算利息" => (nameof(BankUser.AutoCalculateInterest), "Boolean"),
            _ => (null, "Text")
        };
    }

    private static string NormalizeColumnName(string value)
    {
        return string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character)));
    }

    private static bool IsCardNumberColumn(string value)
    {
        return value is "\u5361\u53f7" or "\u501f\u8bb0\u5361\u53f7" or "\u6253\u5370\u5361\u53f7" or "\u4e3b\u5361\u5361\u53f7"
            || (value.Contains("\u5361\u53f7", StringComparison.Ordinal) && !value.Contains("\u8d26\u53f7", StringComparison.Ordinal) && !value.Contains("\u5e10\u53f7", StringComparison.Ordinal));
    }

    private static bool IsAccountNumberColumn(string value)
    {
        return value is "\u652f\u4ed8\u5b9d\u8d26\u6237" or "\u5fae\u4fe1\u53f7" or "\u8d26\u53f7" or "\u5e10\u53f7" or "\u8d26\u53f7\u5361\u53f7"
            or "\u5361\u53f7\u8d26\u6237" or "\u5ba2\u6237\u8d26\u53f7" or "\u6237\u53e3\u53f7" or "\u8d26\u6237\u8d26\u53f7" or "\u8d26\u6237\u53f7"
            or "\u5ba2\u6237\u8d26\u53e3" or "\u5ba2\u6237\u6237\u53e3" or "\u5ea6\u53f7";
    }

    private static string InferExtraBankUserFieldType(string columnName)
    {
        return IsDateLikeBankUserColumn(columnName) ? "Date" : "Text";
    }

    private static bool IsDateLikeBankUserColumn(string columnName)
    {
        return columnName.Contains("日期", StringComparison.Ordinal)
            || columnName.Contains("时间", StringComparison.Ordinal)
            || columnName.EndsWith("日", StringComparison.Ordinal);
    }

    private static string CreateExtraFieldPath(string bankName, string columnName, int columnIndex)
    {
        var raw = $"{bankName}|{columnIndex}|{columnName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"[UserField_{Convert.ToHexString(hash)[..12]}]";
    }

    private static int GetBankUserColumnWidth(string columnName, string type)
    {
        if (columnName == "ID")
        {
            return 48;
        }

        if (string.Equals(type, "Boolean", StringComparison.OrdinalIgnoreCase))
        {
            return 96;
        }

        if (string.Equals(type, "Date", StringComparison.OrdinalIgnoreCase) || columnName.Contains("日期") || columnName.Contains("时间"))
        {
            return 168;
        }

        if (string.Equals(type, "Money", StringComparison.OrdinalIgnoreCase) || columnName.Contains("金额") || columnName.Contains("余额"))
        {
            return 110;
        }

        if (columnName.Contains("账号") || columnName.Contains("账户") || columnName.Contains("卡号") || columnName.Contains("身份证") || columnName.Contains("证件"))
        {
            return 150;
        }

        if (columnName.Length <= 2)
        {
            return 88;
        }

        return Math.Clamp((columnName.Length * 16) + 44, 92, 170);
    }

    private static void AddDefaultBankUserColumns(Bank bank)
    {
        AddColumns(bank,
            Column(5, "ID", nameof(BankUser.Id), 48),
            Column(10, "起始日期", nameof(BankUser.StartDate), 170, "Date"),
            Column(20, "终止日期", nameof(BankUser.EndDate), 170, "Date"),
            Column(30, "账号/卡号", nameof(BankUser.AccountNo), 168),
            Column(40, "姓名", nameof(BankUser.AccountName), 100),
            Column(50, "身份证", nameof(BankUser.IdNumber), 140),
            Column(60, "编号", nameof(BankUser.UserCode), 90),
            Column(70, "备注", nameof(BankUser.Remark), 130),
            Column(80, "期初余额", nameof(BankUser.OpeningBalance), 110, "Money"),
            Column(90, "自动计算利息", nameof(BankUser.AutoCalculateInterest), 100, "Boolean"));
    }

    private static void ConfigureFlowGenerationColumns(Bank bank)
    {
        bank.ReferenceColumns.Clear();
        bank.ConstColumns.Clear();
        bank.FlowColumns.Clear();

        if (FlowRuleColumnCatalog.TryGetColumns(bank.Name, FlowRuleColumnKind.Reference, out var referenceColumns))
        {
            AddColumns(bank.ReferenceColumns, CreateFlowRuleColumns(bank.Name, referenceColumns, FlowRuleColumnKind.Reference).ToArray());
        }
        else
        {
            AddDefaultReferenceColumns(bank.ReferenceColumns);
            AddHiddenReferenceColumns(bank.ReferenceColumns);
        }

        if (FlowRuleColumnCatalog.TryGetColumns(bank.Name, FlowRuleColumnKind.Const, out var constColumns))
        {
            AddColumns(bank.ConstColumns, CreateFlowRuleColumns(bank.Name, constColumns, FlowRuleColumnKind.Const).ToArray());
        }
        else
        {
            AddDefaultConstColumns(bank.ConstColumns);
            AddHiddenConstColumns(bank.ConstColumns);
        }

        ConfigureFlowRecordColumns(bank);
    }

    private static IEnumerable<ColumnDefinition> CreateFlowRuleColumns(string bankName, IReadOnlyList<string> columnNames, FlowRuleColumnKind kind)
    {
        var usedFixedFields = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < columnNames.Count; index++)
        {
            var columnName = columnNames[index];
            var (field, type) = ResolveFlowRuleField(columnName, kind);
            if (field is null || ShouldUseExtraFlowRuleField(field, usedFixedFields))
            {
                field = CreateFlowRuleExtraFieldPath(bankName, kind, columnName, index);
                type = "Text";
            }

            yield return Column((index + 1) * 10, columnName, field, GetFlowRuleColumnWidth(columnName, type), type);
        }
    }

    private static bool ShouldUseExtraFlowRuleField(string field, HashSet<string> usedFixedFields)
    {
        if (field == nameof(FlowRuleBase.Id))
        {
            return false;
        }

        return !usedFixedFields.Add(field);
    }

    private static (string? Field, string Type) ResolveFlowRuleField(string columnName, FlowRuleColumnKind kind)
    {
        if (kind == FlowRuleColumnKind.Reference && columnName == "每月出现次数")
        {
            return (nameof(GenerateReferenceRule.PercentMonth), "Text");
        }

        if (kind == FlowRuleColumnKind.Const)
        {
            if (columnName == "固定添加日")
            {
                return (nameof(GenerateConstRule.FixDay), "Text");
            }

            if (columnName == "次数")
            {
                return (nameof(GenerateConstRule.ReCnt), "Text");
            }
        }

        return columnName switch
        {
            "ID" => (nameof(FlowRuleBase.Id), "Text"),
            "选择" => (nameof(FlowRuleBase.IsCheck), "Boolean"),
            "收支属性" => (nameof(FlowRuleBase.IncomeAttribute), "Text"),
            "最小金额" or "最小金颔" or "最小金颜" => (nameof(FlowRuleBase.MinMoney), "Money"),
            "最大金额" or "最大金颜" => (nameof(FlowRuleBase.MaxMoney), "Money"),
            "小数位" => (nameof(FlowRuleBase.FloutLength), "Text"),
            "开始时间" => (nameof(FlowRuleBase.StartDay), "Text"),
            "结束时间" => (nameof(FlowRuleBase.EndDay), "Text"),
            "节假日交易" => (nameof(FlowRuleBase.TradeHoliday), "Boolean"),
            "周六日交易" => (nameof(FlowRuleBase.TradeWeekend), "Boolean"),
            "备注" or "附言" or "留言" or "转账附言" => (nameof(FlowRuleBase.Remark), "Text"),
            "流水号" or "交易流水" or "交易流水号" or "核心流水号" => (nameof(FlowRuleBase.SerialNum), "Text"),
            "资金渠道" or "交易渠道" or "渠道" => (nameof(FlowRuleBase.TradeChannel), "Text"),
            "交易渠道英文" => (nameof(FlowRuleBase.TradeChannelEn), "Text"),
            "交易对方" or "对方户名" or "对方名称" or "对手户名" or "对手方户名" or "对方姓名" or "对方账户名" or "交易对手名称" or "对方账号名称" or "户名" => (nameof(FlowRuleBase.OppositeUsername), "Text"),
            "对方账号" or "对手账号" or "对手方账号" or "交易对手账号" or "对方卡号账号" => (nameof(FlowRuleBase.OppositeAccount), "Text"),
            "对方开户行" or "对方银行" or "对手银行" or "对方行名" or "对方行号" or "对方开户行银联" => (nameof(FlowRuleBase.OppositeBank), "Text"),
            "商家订单号" or "商户单号" or "商户名称" => (nameof(FlowRuleBase.MerchantName), "Text"),
            "支付宝分类" or "交易分类" or "收支其他" => (nameof(FlowRuleBase.IncomeType), "Text"),
            "账号" or "卡号" or "客户账号" or "交易账号" or "账户代码" => (nameof(FlowRuleBase.Account), "Text"),
            "应用号" => (nameof(FlowRuleBase.AppNum), "Text"),
            "序号" => (nameof(FlowRuleBase.SequenceNum), "Text"),
            "币种" or "货币" or "贷币" => (nameof(FlowRuleBase.Currency), "Text"),
            "交易币种" => (nameof(FlowRuleBase.TradeCurrency), "Text"),
            "钞汇" or "交易方式" => (nameof(FlowRuleBase.CashCheck), "Text"),
            "交易代码" or "交易码" => (nameof(FlowRuleBase.TradeCode), "Text"),
            "注释" or "交易说明" or "交易描述" => (nameof(FlowRuleBase.TradeExplain), "Text"),
            "存期" => (nameof(FlowRuleBase.DepositTerm), "Text"),
            "约转期" => (nameof(FlowRuleBase.AgreedTerm), "Text"),
            "通知种类发行代" => (nameof(FlowRuleBase.NoticeType), "Text"),
            "地区号" => (nameof(FlowRuleBase.AreaNum), "Text"),
            "网点号" or "机构号" or "机构码" or "交易机构号" => (nameof(FlowRuleBase.NetNum), "Text"),
            "操作员" or "柜员" or "业务柜员" or "交易柜员" => (nameof(FlowRuleBase.Operator), "Text"),
            "柜员号" or "柜员流水" or "柜员交易号" or "交易柜员号" or "机构柜员流水" => (nameof(FlowRuleBase.OperatorNum), "Text"),
            "界面" => (nameof(FlowRuleBase.InterfacePage), "Text"),
            "交易场所" or "交易地点" or "地点" or "交易网点" or "交易行所" or "商户网点号及名称" => (nameof(FlowRuleBase.TradePlace), "Text"),
            "摘要" or "交易摘要" or "产品摘要" or "银行摘要" or "账单摘要" or "摘要代码" or "记账信息" => (nameof(FlowRuleBase.ProductBrief), "Text"),
            "产品名称" or "交易名称" or "交易种类" or "业务类型" or "交易类型" => (nameof(FlowRuleBase.ProductName), "Text"),
            "业务产品种类" or "产品业务种类" => (nameof(FlowRuleBase.ProductType), "Text"),
            "用途" or "交易用途" => (nameof(FlowRuleBase.Usage), "Text"),
            "分户序号" or "子账户序号" => (nameof(FlowRuleBase.SubAccountNum), "Text"),
            "账户序号" or "账号序号" => (nameof(FlowRuleBase.AccountNum), "Text"),
            "凭证类型" or "凭证种类" => (nameof(FlowRuleBase.VoucherType), "Text"),
            "凭证号" or "凭证号码" or "凭证" or "票据号" or "传票号" or "凭证序号" or "凭证号码业务" or "外部系统流水" => (nameof(FlowRuleBase.VoucherNum), "Text"),
            "日志号" => (nameof(FlowRuleBase.LogNum), "Text"),
            "年份" => (nameof(FlowRuleBase.Year), "Text"),
            "借方发生额" => (nameof(FlowRuleBase.DebitAmount), "Money"),
            "贷方发生额" => (nameof(FlowRuleBase.CreditAmount), "Money"),
            "余额" => (nameof(FlowRuleBase.BalanceAmount), "Money"),
            "回单编号" or "全局路由号" => (nameof(FlowRuleBase.ReceiptNum), "Text"),
            _ => (null, "Text")
        };
    }

    private static string CreateFlowRuleExtraFieldPath(string bankName, FlowRuleColumnKind kind, string columnName, int columnIndex)
    {
        var raw = $"{bankName}|{kind}|{columnIndex}|{columnName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"[RuleField_{Convert.ToHexString(hash)[..12]}]";
    }

    private static int GetFlowRuleColumnWidth(string columnName, string type)
    {
        if (columnName == "ID")
        {
            return 48;
        }

        if (string.Equals(type, "Boolean", StringComparison.OrdinalIgnoreCase))
        {
            return 84;
        }

        if (string.Equals(type, "Money", StringComparison.OrdinalIgnoreCase)
            || columnName.Contains("金额")
            || columnName.Contains("余额")
            || columnName.Contains("金颔")
            || columnName.Contains("金颜"))
        {
            return 100;
        }

        if (columnName.Contains("账号") || columnName.Contains("账户") || columnName.Contains("流水") || columnName.Contains("订单"))
        {
            return 122;
        }

        if (columnName.Length <= 2)
        {
            return 76;
        }

        return Math.Clamp((columnName.Length * 15) + 42, 92, 180);
    }

    private static void AddDefaultReferenceColumns(List<ColumnDefinition> target)
    {
        AddColumns(target,
            Column(5, "ID", nameof(GenerateReferenceRule.Id), 48),
            Column(10, "选择", nameof(GenerateReferenceRule.IsCheck), 58, "Boolean"),
            Column(20, "收支属性", nameof(GenerateReferenceRule.IncomeAttribute), 78),
            Column(30, "最小金额", nameof(GenerateReferenceRule.MinMoney), 100, "Money"),
            Column(40, "最大金额", nameof(GenerateReferenceRule.MaxMoney), 100, "Money"),
            Column(50, "小数位", nameof(GenerateReferenceRule.FloutLength), 100),
            Column(60, "开始时间", nameof(GenerateReferenceRule.StartDay), 100),
            Column(70, "结束时间", nameof(GenerateReferenceRule.EndDay), 100),
            Column(80, "每月出现次数", nameof(GenerateReferenceRule.PercentMonth), 108),
            Column(90, "节假日交易", nameof(GenerateReferenceRule.TradeHoliday), 100, "Boolean"),
            Column(100, "周六日交易", nameof(GenerateReferenceRule.TradeWeekend), 100, "Boolean"),
            Column(110, "备注", nameof(GenerateReferenceRule.Remark), 100),
            Column(120, "流水号", nameof(GenerateReferenceRule.SerialNum), 100),
            Column(130, "资金渠道", nameof(GenerateReferenceRule.TradeChannel), 100),
            Column(140, "交易对方", nameof(GenerateReferenceRule.OppositeUsername), 108),
            Column(150, "商家订单号", nameof(GenerateReferenceRule.MerchantName), 100),
            Column(160, "支付五分类", nameof(GenerateReferenceRule.IncomeType), 100));
    }

    private static void AddDefaultConstColumns(List<ColumnDefinition> target)
    {
        AddColumns(target,
            Column(5, "ID", nameof(GenerateConstRule.Id), 48),
            Column(10, "选择", nameof(GenerateConstRule.IsCheck), 58, "Boolean"),
            Column(20, "收支属性", nameof(GenerateConstRule.IncomeAttribute), 78),
            Column(30, "最小金额", nameof(GenerateConstRule.MinMoney), 100, "Money"),
            Column(40, "最大金额", nameof(GenerateConstRule.MaxMoney), 100, "Money"),
            Column(50, "小数位", nameof(GenerateConstRule.FloutLength), 100),
            Column(60, "开始时间", nameof(GenerateConstRule.StartDay), 100),
            Column(70, "结束时间", nameof(GenerateConstRule.EndDay), 100),
            Column(80, "固定日期", nameof(GenerateConstRule.FixDay), 100),
            Column(90, "重复次数", nameof(GenerateConstRule.ReCnt), 100),
            Column(100, "节假日交易", nameof(GenerateConstRule.TradeHoliday), 100, "Boolean"),
            Column(110, "周六日交易", nameof(GenerateConstRule.TradeWeekend), 100, "Boolean"),
            Column(120, "备注", nameof(GenerateConstRule.Remark), 120),
            Column(130, "流水号", nameof(GenerateConstRule.SerialNum), 100),
            Column(140, "资金渠道", nameof(GenerateConstRule.TradeChannel), 100),
            Column(150, "交易对方", nameof(GenerateConstRule.OppositeUsername), 108),
            Column(160, "商家订单号", nameof(GenerateConstRule.MerchantName), 100),
            Column(170, "支付五分类", nameof(GenerateConstRule.IncomeType), 100));
    }

    private static void AddHiddenReferenceColumns(List<ColumnDefinition> columns)
    {
        AddColumns(columns,
            Column(900, "收入类型", nameof(GenerateReferenceRule.IncomeType), 100, show: false),
            Column(910, "跨行摘要", nameof(GenerateReferenceRule.CrossBankBrief), 100, show: false),
            Column(920, "跨行比例", nameof(GenerateReferenceRule.CrossBankRate), 100, "Money", false),
            Column(930, "跨行最小值", nameof(GenerateReferenceRule.CrossBankMin), 100, "Money", false),
            Column(940, "跨行最大值", nameof(GenerateReferenceRule.CrossBankMax), 100, "Money", false),
            Column(950, "异地摘要", nameof(GenerateReferenceRule.OffSiteBankBrief), 100, show: false),
            Column(960, "异地比例", nameof(GenerateReferenceRule.OffSiteBankRate), 100, "Money", false),
            Column(970, "异地最小值", nameof(GenerateReferenceRule.OffSiteBankMin), 100, "Money", false),
            Column(980, "异地最大值", nameof(GenerateReferenceRule.OffSiteBankMax), 100, "Money", false),
            Column(990, "账户", nameof(GenerateReferenceRule.Account), 100, show: false),
            Column(1000, "产品名称", nameof(GenerateReferenceRule.ProductName), 100, show: false),
            Column(1010, "产品代码", nameof(GenerateReferenceRule.ProductCode), 100, show: false),
            Column(1020, "产品摘要", nameof(GenerateReferenceRule.ProductBrief), 100, show: false),
            Column(1030, "产品类型", nameof(GenerateReferenceRule.ProductType), 100, show: false),
            Column(1040, "操作员", nameof(GenerateReferenceRule.Operator), 100, show: false),
            Column(1050, "操作员编号", nameof(GenerateReferenceRule.OperatorNum), 100, show: false),
            Column(1060, "对方账号", nameof(GenerateReferenceRule.OppositeAccount), 100, show: false),
            Column(1070, "对方银行", nameof(GenerateReferenceRule.OppositeBank), 100, show: false),
            Column(1080, "网点号", nameof(GenerateReferenceRule.BranchNum), 100, show: false),
            Column(1090, "用途", nameof(GenerateReferenceRule.Usage), 100, show: false),
            Column(1100, "应用号", nameof(GenerateReferenceRule.AppNum), 100, show: false),
            Column(1110, "序列号", nameof(GenerateReferenceRule.SequenceNum), 100, show: false),
            Column(1120, "币种", nameof(GenerateReferenceRule.Currency), 100, show: false),
            Column(1130, "钞汇", nameof(GenerateReferenceRule.CashCheck), 100, show: false),
            Column(1140, "交易代码", nameof(GenerateReferenceRule.TradeCode), 100, show: false),
            Column(1150, "交易币种", nameof(GenerateReferenceRule.TradeCurrency), 100, show: false),
            Column(1160, "存期", nameof(GenerateReferenceRule.DepositTerm), 100, show: false),
            Column(1170, "约定期限", nameof(GenerateReferenceRule.AgreedTerm), 100, show: false),
            Column(1180, "通知类型", nameof(GenerateReferenceRule.NoticeType), 100, show: false),
            Column(1190, "地区号", nameof(GenerateReferenceRule.AreaNum), 100, show: false),
            Column(1200, "网点编号", nameof(GenerateReferenceRule.NetNum), 100, show: false),
            Column(1210, "接口页面", nameof(GenerateReferenceRule.InterfacePage), 100, show: false),
            Column(1220, "交易地点", nameof(GenerateReferenceRule.TradePlace), 100, show: false),
            Column(1230, "渠道英文", nameof(GenerateReferenceRule.TradeChannelEn), 100, show: false),
            Column(1240, "交易说明", nameof(GenerateReferenceRule.TradeExplain), 100, show: false),
            Column(1250, "账号编号", nameof(GenerateReferenceRule.AccountNum), 100, show: false),
            Column(1260, "子账号编号", nameof(GenerateReferenceRule.SubAccountNum), 100, show: false),
            Column(1270, "凭证类型", nameof(GenerateReferenceRule.VoucherType), 100, show: false),
            Column(1280, "凭证号", nameof(GenerateReferenceRule.VoucherNum), 100, show: false),
            Column(1290, "日志号", nameof(GenerateReferenceRule.LogNum), 100, show: false),
            Column(1300, "终端号", nameof(GenerateReferenceRule.TerminalNum), 100, show: false),
            Column(1310, "处理状态", nameof(GenerateReferenceRule.HandleStatus), 100, show: false),
            Column(1320, "年份", nameof(GenerateReferenceRule.Year), 100, show: false),
            Column(1330, "贷方金额", nameof(GenerateReferenceRule.CreditAmount), 100, "Money", false),
            Column(1340, "借方金额", nameof(GenerateReferenceRule.DebitAmount), 100, "Money", false),
            Column(1350, "余额金额", nameof(GenerateReferenceRule.BalanceAmount), 100, "Money", false),
            Column(1360, "回单号", nameof(GenerateReferenceRule.ReceiptNum), 100, show: false));
    }

    private static void AddHiddenConstColumns(List<ColumnDefinition> columns)
    {
        AddColumns(columns,
            Column(900, "收入类型", nameof(GenerateConstRule.IncomeType), 100, show: false),
            Column(910, "跨行摘要", nameof(GenerateConstRule.CrossBankBrief), 100, show: false),
            Column(920, "跨行比例", nameof(GenerateConstRule.CrossBankRate), 100, "Money", false),
            Column(930, "跨行最小值", nameof(GenerateConstRule.CrossBankMin), 100, "Money", false),
            Column(940, "跨行最大值", nameof(GenerateConstRule.CrossBankMax), 100, "Money", false),
            Column(950, "异地摘要", nameof(GenerateConstRule.OffSiteBankBrief), 100, show: false),
            Column(960, "异地比例", nameof(GenerateConstRule.OffSiteBankRate), 100, "Money", false),
            Column(970, "异地最小值", nameof(GenerateConstRule.OffSiteBankMin), 100, "Money", false),
            Column(980, "异地最大值", nameof(GenerateConstRule.OffSiteBankMax), 100, "Money", false),
            Column(990, "账户", nameof(GenerateConstRule.Account), 100, show: false),
            Column(1000, "产品名称", nameof(GenerateConstRule.ProductName), 100, show: false),
            Column(1010, "产品代码", nameof(GenerateConstRule.ProductCode), 100, show: false),
            Column(1020, "产品摘要", nameof(GenerateConstRule.ProductBrief), 100, show: false),
            Column(1030, "产品类型", nameof(GenerateConstRule.ProductType), 100, show: false),
            Column(1040, "操作员", nameof(GenerateConstRule.Operator), 100, show: false),
            Column(1050, "操作员编号", nameof(GenerateConstRule.OperatorNum), 100, show: false),
            Column(1060, "对方账号", nameof(GenerateConstRule.OppositeAccount), 100, show: false),
            Column(1070, "对方银行", nameof(GenerateConstRule.OppositeBank), 100, show: false),
            Column(1080, "网点号", nameof(GenerateConstRule.BranchNum), 100, show: false),
            Column(1090, "用途", nameof(GenerateConstRule.Usage), 100, show: false),
            Column(1100, "应用号", nameof(GenerateConstRule.AppNum), 100, show: false),
            Column(1110, "序列号", nameof(GenerateConstRule.SequenceNum), 100, show: false),
            Column(1120, "币种", nameof(GenerateConstRule.Currency), 100, show: false),
            Column(1130, "钞汇", nameof(GenerateConstRule.CashCheck), 100, show: false),
            Column(1140, "交易代码", nameof(GenerateConstRule.TradeCode), 100, show: false),
            Column(1150, "交易币种", nameof(GenerateConstRule.TradeCurrency), 100, show: false),
            Column(1160, "存期", nameof(GenerateConstRule.DepositTerm), 100, show: false),
            Column(1170, "约定期限", nameof(GenerateConstRule.AgreedTerm), 100, show: false),
            Column(1180, "通知类型", nameof(GenerateConstRule.NoticeType), 100, show: false),
            Column(1190, "地区号", nameof(GenerateConstRule.AreaNum), 100, show: false),
            Column(1200, "网点编号", nameof(GenerateConstRule.NetNum), 100, show: false),
            Column(1210, "接口页面", nameof(GenerateConstRule.InterfacePage), 100, show: false),
            Column(1220, "交易地点", nameof(GenerateConstRule.TradePlace), 100, show: false),
            Column(1230, "渠道英文", nameof(GenerateConstRule.TradeChannelEn), 100, show: false),
            Column(1240, "交易说明", nameof(GenerateConstRule.TradeExplain), 100, show: false),
            Column(1250, "账号编号", nameof(GenerateConstRule.AccountNum), 100, show: false),
            Column(1260, "子账号编号", nameof(GenerateConstRule.SubAccountNum), 100, show: false),
            Column(1270, "凭证类型", nameof(GenerateConstRule.VoucherType), 100, show: false),
            Column(1280, "凭证号", nameof(GenerateConstRule.VoucherNum), 100, show: false),
            Column(1290, "日志号", nameof(GenerateConstRule.LogNum), 100, show: false),
            Column(1300, "终端号", nameof(GenerateConstRule.TerminalNum), 100, show: false),
            Column(1310, "处理状态", nameof(GenerateConstRule.HandleStatus), 100, show: false),
            Column(1320, "年份", nameof(GenerateConstRule.Year), 100, show: false),
            Column(1330, "贷方金额", nameof(GenerateConstRule.CreditAmount), 100, "Money", false),
            Column(1340, "借方金额", nameof(GenerateConstRule.DebitAmount), 100, "Money", false),
            Column(1350, "余额金额", nameof(GenerateConstRule.BalanceAmount), 100, "Money", false),
            Column(1360, "回单号", nameof(GenerateConstRule.ReceiptNum), 100, show: false));
    }

    private static void ConfigureFlowRecordColumns(Bank bank)
    {
        if (FlowRecordColumnCatalog.TryGetFlowColumns(bank.Name, out var configuredColumns))
        {
            AddColumns(bank.FlowColumns, CreateFlowRecordColumns(bank.Name, configuredColumns).ToArray());
            return;
        }

        if (bank.Name == "支付宝")
        {
            ConfigureAlipayFlowRecordColumns(bank);
            return;
        }

        ConfigureDefaultBankFlowRecordColumns(bank);
    }

    private static IEnumerable<ColumnDefinition> CreateFlowRecordColumns(string bankName, IReadOnlyList<string> columnNames)
    {
        var usedFixedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(FlowRecord.Index)
        };

        yield return Column(-1, "ID", nameof(FlowRecord.Index), 48);

        for (var index = 0; index < columnNames.Count; index++)
        {
            var columnName = columnNames[index];
            var (field, type) = ExcelColumnFieldResolver.ResolveFlowRecordField(bankName, columnName);
            if (field is null || ShouldUseExtraFlowRecordField(field, usedFixedFields))
            {
                field = CreateFlowRecordExtraFieldPath(bankName, columnName, index);
                type = ExcelColumnFieldResolver.ResolveFlowRecordField(bankName, columnName).Type;
            }

            yield return Column(
                (index + 1) * 10,
                columnName,
                field,
                ExcelColumnFieldResolver.GetFlowRecordColumnWidth(columnName, type),
                type);
        }
    }

    private static bool ShouldUseExtraFlowRecordField(string field, HashSet<string> usedFixedFields)
    {
        return !usedFixedFields.Add(field);
    }

    private static string CreateFlowRecordExtraFieldPath(string bankName, string columnName, int columnIndex)
    {
        var raw = $"{bankName}|FlowRecord|{columnIndex}|{columnName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"[FlowField_{Convert.ToHexString(hash)[..12]}]";
    }

    private static void ConfigureAlipayFlowRecordColumns(Bank bank)
    {
        AddColumns(bank.FlowColumns,
            Column(5, "ID", nameof(FlowRecord.Index), 48),
            Column(10, "记账时间", nameof(FlowRecord.AccountTime), 180, "DateTime"),
            Column(20, "交易金额", nameof(FlowRecord.TradeMoney), 112, "Money"),
            Column(30, "账户余额", nameof(FlowRecord.Balance), 112, "Money"),
            Column(40, "备注", nameof(FlowRecord.Remark), 120),
            Column(50, "流水号", nameof(FlowRecord.SerialNum), 120),
            Column(60, "资金渠道", nameof(FlowRecord.TradeChannel), 120),
            Column(70, "交易对方", nameof(FlowRecord.OppositeUsername), 120),
            Column(80, "商家订单号", nameof(FlowRecord.MerchantName), 120),
            Column(90, "支付宝分类", nameof(FlowRecord.Usage), 120));

        AddHiddenFlowRecordColumns(bank.FlowColumns);
    }

    private static void ConfigureDefaultBankFlowRecordColumns(Bank bank)
    {
        AddColumns(bank.FlowColumns,
            Column(5, "ID", nameof(FlowRecord.Index), 48),
            Column(10, "记账时间", nameof(FlowRecord.AccountTime), 180, "DateTime"),
            Column(20, "交易金额", nameof(FlowRecord.TradeMoney), 112, "Money"),
            Column(30, "账户余额", nameof(FlowRecord.Balance), 112, "Money"),
            Column(40, "日志号", nameof(FlowRecord.LogNum), 112),
            Column(50, "地点", nameof(FlowRecord.AreaNum), 100),
            Column(60, "摘要", nameof(FlowRecord.ProductBrief), 100),
            Column(70, "交易方式", nameof(FlowRecord.CashCheck), 100),
            Column(80, "交易渠道", nameof(FlowRecord.TradeChannel), 110),
            Column(90, "交易说明", nameof(FlowRecord.TradeExplain), 120),
            Column(100, "附言", nameof(FlowRecord.Remark), 160),
            Column(110, "对方账号", nameof(FlowRecord.OppositeAccount), 150),
            Column(120, "户名", nameof(FlowRecord.OppositeUsername), 100),
            Column(130, "对方开户行", nameof(FlowRecord.OppositeBank), 150),
            Column(140, "交易用途", nameof(FlowRecord.Usage), 120),
            Column(150, "流水号", nameof(FlowRecord.SerialNum), 110),
            Column(160, "商家订单号", nameof(FlowRecord.MerchantName), 120),
            Column(170, "币种", nameof(FlowRecord.Currency), 80));

        AddHiddenFlowRecordColumns(bank.FlowColumns);
    }

    private static void AddHiddenFlowRecordColumns(List<ColumnDefinition> columns)
    {
        AddColumns(columns,
            Column(900, "收支属性", nameof(FlowRecord.IncomeAttribute), 90, show: false),
            Column(910, "账户", nameof(FlowRecord.Account), 100, show: false),
            Column(920, "产品名称", nameof(FlowRecord.ProductName), 100, show: false),
            Column(930, "产品代码", nameof(FlowRecord.ProductCode), 100, show: false),
            Column(940, "产品类型", nameof(FlowRecord.ProductType), 100, show: false),
            Column(950, "操作员", nameof(FlowRecord.Operator), 100, show: false),
            Column(960, "操作员编号", nameof(FlowRecord.OperatorNum), 100, show: false),
            Column(970, "网点号", nameof(FlowRecord.BranchNum), 100, show: false),
            Column(980, "应用号", nameof(FlowRecord.AppNum), 100, show: false),
            Column(990, "序列号", nameof(FlowRecord.SequenceNum), 100, show: false),
            Column(1000, "钞汇", nameof(FlowRecord.CashCheck), 100, show: false),
            Column(1010, "交易代码", nameof(FlowRecord.TradeCode), 100, show: false),
            Column(1020, "交易币种", nameof(FlowRecord.TradeCurrency), 100, show: false),
            Column(1030, "存期", nameof(FlowRecord.DepositTerm), 100, show: false),
            Column(1040, "约定期限", nameof(FlowRecord.AgreedTerm), 100, show: false),
            Column(1050, "通知类型", nameof(FlowRecord.NoticeType), 100, show: false),
            Column(1060, "网点编号", nameof(FlowRecord.NetNum), 100, show: false),
            Column(1070, "接口页面", nameof(FlowRecord.InterfacePage), 100, show: false),
            Column(1080, "交易地点", nameof(FlowRecord.TradePlace), 100, show: false),
            Column(1090, "交易渠道英文", nameof(FlowRecord.TradeChannelEn), 120, show: false),
            Column(1100, "账号编号", nameof(FlowRecord.AccountNum), 100, show: false),
            Column(1110, "子账号编号", nameof(FlowRecord.SubAccountNum), 100, show: false),
            Column(1120, "凭证类型", nameof(FlowRecord.VoucherType), 100, show: false),
            Column(1130, "凭证号", nameof(FlowRecord.VoucherNum), 100, show: false),
            Column(1140, "终端号", nameof(FlowRecord.TerminalNum), 100, show: false),
            Column(1150, "处理状态", nameof(FlowRecord.HandleStatus), 100, show: false),
            Column(1160, "年份", nameof(FlowRecord.Year), 80, show: false),
            Column(1170, "借贷类型", nameof(FlowRecord.CreditType), 90, show: false),
            Column(1180, "贷方金额", nameof(FlowRecord.CreditAmount), 100, "Money", false),
            Column(1190, "借方金额", nameof(FlowRecord.DebitAmount), 100, "Money", false),
            Column(1200, "余额金额", nameof(FlowRecord.BalanceAmount), 100, "Money", false),
            Column(1210, "回单号", nameof(FlowRecord.ReceiptNum), 100, show: false),
            Column(1220, "收支标记", nameof(FlowRecord.IncomeFlag), 90, show: false));
    }

    private static void AddColumns(Bank bank, params ColumnDefinition[] columns)
    {
        bank.Columns.AddRange(columns);
    }

    private static void AddColumns(List<ColumnDefinition> target, params ColumnDefinition[] columns)
    {
        target.AddRange(columns);
    }

    private static ColumnDefinition Column(int order, string name, string field, int width, string type = "Text", bool show = true)
    {
        return new ColumnDefinition
        {
            Order = order,
            Name = name,
            Field = field,
            Width = width,
            Type = type,
            Show = show
        };
    }

    private static string CreateMachineDisplayId()
    {
        var raw = $"{Environment.MachineName}|{Environment.UserName}|{Environment.OSVersion.VersionString}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(hash);
        return $"{hex[..8]}-{hex[8..16]}-{hex[16..24]}-{hex[24..32]}";
    }
}
