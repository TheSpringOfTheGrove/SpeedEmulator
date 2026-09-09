using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public static class BankInterestPolicy
{
    public static bool SupportsAutomaticInterest(Bank bank)
    {
        ArgumentNullException.ThrowIfNull(bank);
        var bankName = bank.Name?.Trim() ?? string.Empty;
        return !string.Equals(bankName, "微信", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bankName, "支付宝", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldCalculate(Bank bank, BankUser bankUser)
    {
        ArgumentNullException.ThrowIfNull(bankUser);
        return bankUser.AutoCalculateInterest && SupportsAutomaticInterest(bank);
    }
}
