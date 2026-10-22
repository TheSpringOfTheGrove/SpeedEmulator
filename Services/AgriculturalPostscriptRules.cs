using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

internal static class AgriculturalPostscriptRules
{
    private static readonly HashSet<string> NaConsumerSummaries = new(StringComparer.Ordinal)
    {
        "微信支付",
        "抖音支付",
        "支付宝",
        "美团支付",
        "多多支付",
        "银联支付"
    };

    public static bool IsPersonalAgriculturalBank(Bank bank)
    {
        return string.Equals(bank.Name, "农行", StringComparison.Ordinal)
            && string.Equals(bank.Type, BankTypes.Personal, StringComparison.Ordinal);
    }

    public static bool RequiresNaConsumerPrefix(Bank bank, FlowRecord record)
    {
        if (!IsPersonalAgriculturalBank(bank)
            || !string.Equals(record.CashCheck?.Trim(), "消费", StringComparison.Ordinal))
        {
            return false;
        }

        return NaConsumerSummaries.Contains(record.ProductBrief?.Trim() ?? string.Empty);
    }
}
