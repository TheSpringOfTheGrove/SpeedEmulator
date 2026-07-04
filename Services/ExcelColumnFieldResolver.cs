using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public static class ExcelColumnFieldResolver
{
    public static (string? Field, string Type) ResolveBankUserField(string columnName)
    {
        return columnName switch
        {
            "ID" => (nameof(BankUser.Id), "Text"),
            "起始日期" or "开始日期" or "开始对账日期" or "查询起日" or "起息日期" => (nameof(BankUser.StartDate), "Date"),
            "终止日期" or "结束日期" or "截止日期" or "结束对账日期" or "查询止日" => (nameof(BankUser.EndDate), "Date"),
            "支付宝账户" or "微信号" or "账号" or "帐号" or "卡号" or "账号卡号" or "客户账号" or "户口号" or "账户账号" or "账户号" or "借记卡号" or "客户账口" or "客户户口" => (nameof(BankUser.AccountNo), "Text"),
            "姓名" or "户名" or "客户姓名" or "账户名称" or "户口名称" or "客户名称" or "公司名称" or "单位名称" or "账户名" or "账户名" or "客户户名" or "存款人名称" => (nameof(BankUser.AccountName), "Text"),
            "身份证" or "身份证号" or "证件号" or "证件号码" or "证件编号" => (nameof(BankUser.IdNumber), "Text"),
            "编号" or "序号" or "客户号" => (nameof(BankUser.UserCode), "Text"),
            "开户行" or "开户机构" or "开户网点" or "支行名称" => (nameof(BankUser.OpenBranch), "Text"),
            "余额" => (nameof(BankUser.Balance), "Money"),
            "交易类型" => (nameof(BankUser.TransactionType), "Text"),
            "币种" or "货币" or "币别" => (nameof(BankUser.Currency), "Text"),
            "章内编码" or "章内编码2" => (nameof(BankUser.ChapterCode), "Text"),
            "章内支行" => (nameof(BankUser.ChapterBranch), "Text"),
            "是否打印章" => (nameof(BankUser.ShouldPrintSeal), "Boolean"),
            "备注" => (nameof(BankUser.Remark), "Text"),
            "期初余额" => (nameof(BankUser.OpeningBalance), "Money"),
            "自动计算利息" => (nameof(BankUser.AutoCalculateInterest), "Boolean"),
            _ => (null, "Text")
        };
    }

    public static (string? Field, string Type) ResolveFlowRecordField(string columnName)
    {
        return columnName switch
        {
            "ID" => (nameof(FlowRecord.Index), "Text"),
            "序号" => (nameof(FlowRecord.SequenceNum), "Text"),
            "日期" or "时间" or "交易日期" or "交易时间" or "记账时间" or "记帐日期" or "记账日期" or "工作日期" or "记账日" => (nameof(FlowRecord.AccountTime), "DateTime"),
            "交易金额" or "金额" or "发生额" or "借贷方发生额" => (nameof(FlowRecord.TradeMoney), "Money"),
            "收入" or "存入" or "存入金额" or "贷方" or "贷方发生额" or "贷方交易金额" => (nameof(FlowRecord.CreditAmount), "Money"),
            "支出" or "支取金额" or "借方" or "借方发生额" or "借方交易金额" or "支出金额" => (nameof(FlowRecord.DebitAmount), "Money"),
            "账户余额" or "帐户余额" or "余额" or "联机余额" or "交易后余额" => (nameof(FlowRecord.Balance), "Money"),
            "摘要" or "交易摘要" or "摘要信息" or "银行摘要" or "摘要代码" or "商品说明" => (nameof(FlowRecord.ProductBrief), "Text"),
            "备注" or "附言" or "留言" or "转账附言" or "APP备注" or "回单个性信息" => (nameof(FlowRecord.Remark), "Text"),
            "流水号" or "交易流水号" or "交易流水" or "核心流水号" or "柜员流水" or "柜员流水号" or "交易单号" or "交易报文号" or "机构柜员流水" => (nameof(FlowRecord.SerialNum), "Text"),
            "资金渠道" or "交易渠道" or "渠道" => (nameof(FlowRecord.TradeChannel), "Text"),
            "交易渠道英文" => (nameof(FlowRecord.TradeChannelEn), "Text"),
            "交易说明" or "注释" or "交易描述" => (nameof(FlowRecord.TradeExplain), "Text"),
            "交易方式" or "现转标志" or "钞汇" or "冲补帐" => (nameof(FlowRecord.CashCheck), "Text"),
            "交易代码" or "交易码" => (nameof(FlowRecord.TradeCode), "Text"),
            "交易对方" or "对方户名" or "对方名称" or "对手户名" or "对手方户名" or "对方账户名" or "交易对手名称" or "对方姓名" or "对方名称" or "对方户名" => (nameof(FlowRecord.OppositeUsername), "Text"),
            "对方账号" or "对方账户" or "对手账号" or "交易对手账号" or "对方卡号账号" or "对手方账户" or "对方帐号" => (nameof(FlowRecord.OppositeAccount), "Text"),
            "对方开户行" or "对方银行" or "对手银行" or "对方行名" or "对方开户行联行号" or "对手银行" or "对方开户行" => (nameof(FlowRecord.OppositeBank), "Text"),
            "商家订单号" or "商户单号" or "商户名称" => (nameof(FlowRecord.MerchantName), "Text"),
            "支付宝分类" or "交易分类" or "收支其他" or "APP交易分类" => (nameof(FlowRecord.Usage), "Text"),
            "收入支出" or "收支" or "收支属性" => (nameof(FlowRecord.IncomeAttribute), "Text"),
            "账号" or "帐号" or "客户账号" or "交易账户" or "账户代码" or "卡号" => (nameof(FlowRecord.Account), "Text"),
            "应用号" => (nameof(FlowRecord.AppNum), "Text"),
            "币种" or "货币" or "币别" => (nameof(FlowRecord.Currency), "Text"),
            "交易币种" => (nameof(FlowRecord.TradeCurrency), "Text"),
            "存期" => (nameof(FlowRecord.DepositTerm), "Text"),
            "约转期" => (nameof(FlowRecord.AgreedTerm), "Text"),
            "通知种类发行代码" => (nameof(FlowRecord.NoticeType), "Text"),
            "地区" or "地区号" => (nameof(FlowRecord.AreaNum), "Text"),
            "网点号" or "机构号" or "机构码" or "交易机构号" or "机构柜员流水" or "交易机构" => (nameof(FlowRecord.NetNum), "Text"),
            "操作员" or "柜员" or "业务柜员" or "交易柜员" or "操作柜员" => (nameof(FlowRecord.Operator), "Text"),
            "柜员号" or "柜员交易号" or "交易柜员号" or "操作员编号" => (nameof(FlowRecord.OperatorNum), "Text"),
            "界面" or "接口页面" => (nameof(FlowRecord.InterfacePage), "Text"),
            "交易场所" or "交易地点" or "地点" or "交易网点" or "交易行所" or "网点名称" or "商户网点号及名称" => (nameof(FlowRecord.TradePlace), "Text"),
            "产品名称" or "交易名称" or "交易种类" or "业务类型" or "交易类型" or "业务产品种类" or "产品业务种类" => (nameof(FlowRecord.ProductName), "Text"),
            "产品代码" => (nameof(FlowRecord.ProductCode), "Text"),
            "产品类型" => (nameof(FlowRecord.ProductType), "Text"),
            "用途" or "交易用途" => (nameof(FlowRecord.Usage), "Text"),
            "分户序号" or "子账户序号" => (nameof(FlowRecord.SubAccountNum), "Text"),
            "账户序号" or "账号序号" => (nameof(FlowRecord.AccountNum), "Text"),
            "凭证类型" or "凭证种类" => (nameof(FlowRecord.VoucherType), "Text"),
            "凭证号" or "凭证号码" or "凭证" or "票据号" or "传票号" or "凭证序号" or "外部系统流水" => (nameof(FlowRecord.VoucherNum), "Text"),
            "日志号" => (nameof(FlowRecord.LogNum), "Text"),
            "年份" => (nameof(FlowRecord.Year), "Text"),
            "回单编号" or "全局路由号" => (nameof(FlowRecord.ReceiptNum), "Text"),
            _ => (null, InferType(columnName))
        };
    }

    public static int GetBankUserColumnWidth(string columnName, string type)
    {
        if (columnName == "ID")
        {
            return 48;
        }

        if (string.Equals(type, "Boolean", StringComparison.OrdinalIgnoreCase))
        {
            return 96;
        }

        if (string.Equals(type, "Date", StringComparison.OrdinalIgnoreCase)
            || columnName.Contains("日期", StringComparison.Ordinal)
            || columnName.Contains("时间", StringComparison.Ordinal))
        {
            return 168;
        }

        if (IsMoneyLike(columnName) || string.Equals(type, "Money", StringComparison.OrdinalIgnoreCase))
        {
            return 110;
        }

        if (columnName.Contains("账号", StringComparison.Ordinal)
            || columnName.Contains("账户", StringComparison.Ordinal)
            || columnName.Contains("卡号", StringComparison.Ordinal)
            || columnName.Contains("身份证", StringComparison.Ordinal)
            || columnName.Contains("证件", StringComparison.Ordinal))
        {
            return 150;
        }

        return Math.Clamp((columnName.Length * 16) + 44, 88, 170);
    }

    public static int GetFlowRecordColumnWidth(string columnName, string type)
    {
        if (columnName == "ID")
        {
            return 48;
        }

        if (string.Equals(type, "Date", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "DateTime", StringComparison.OrdinalIgnoreCase)
            || columnName.Contains("日期", StringComparison.Ordinal)
            || columnName.Contains("时间", StringComparison.Ordinal))
        {
            return 170;
        }

        if (IsMoneyLike(columnName) || string.Equals(type, "Money", StringComparison.OrdinalIgnoreCase))
        {
            return 112;
        }

        if (columnName.Contains("账号", StringComparison.Ordinal)
            || columnName.Contains("账户", StringComparison.Ordinal)
            || columnName.Contains("流水", StringComparison.Ordinal)
            || columnName.Contains("订单", StringComparison.Ordinal))
        {
            return 132;
        }

        if (columnName.Length <= 2)
        {
            return 78;
        }

        return Math.Clamp((columnName.Length * 15) + 42, 92, 180);
    }

    private static string InferType(string columnName)
    {
        if (columnName.Contains("日期", StringComparison.Ordinal)
            || columnName.Contains("时间", StringComparison.Ordinal))
        {
            return "DateTime";
        }

        return IsMoneyLike(columnName) ? "Money" : "Text";
    }

    private static bool IsMoneyLike(string columnName)
    {
        return columnName.Contains("金额", StringComparison.Ordinal)
            || columnName.Contains("余额", StringComparison.Ordinal)
            || columnName.Contains("借方", StringComparison.Ordinal)
            || columnName.Contains("贷方", StringComparison.Ordinal)
            || columnName.Contains("收入", StringComparison.Ordinal)
            || columnName.Contains("支出", StringComparison.Ordinal)
            || columnName.Contains("存入", StringComparison.Ordinal)
            || columnName.Contains("支取", StringComparison.Ordinal)
            || columnName is "借方" or "贷方" or "金额";
    }
}
