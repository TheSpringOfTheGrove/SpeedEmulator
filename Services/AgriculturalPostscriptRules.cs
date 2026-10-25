using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

internal static class AgriculturalPostscriptRules
{
    private static readonly HashSet<string> NaSummaries = new(StringComparer.Ordinal)
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

    public static bool RequiresNaPrefix(Bank bank, FlowRecord record)
    {
        if (!IsPersonalAgriculturalBank(bank))
        {
            return false;
        }

        return NaSummaries.Contains(record.ProductBrief?.Trim() ?? string.Empty);
    }

    public static bool RequiresUaPrefix(Bank bank, FlowRecord record)
    {
        if (!IsPersonalAgriculturalBank(bank))
        {
            return false;
        }

        var oppositeAccount = record.OppositeAccount?.Trim() ?? string.Empty;
        var oppositeBank = record.OppositeBank?.Trim() ?? string.Empty;
        return oppositeAccount.Length is > 0 and < 15
            && string.IsNullOrWhiteSpace(oppositeBank);
    }
}
