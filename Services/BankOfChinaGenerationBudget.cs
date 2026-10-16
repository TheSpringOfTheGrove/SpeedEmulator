using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

/// <summary>Personal BOC generation only; final-balance tolerance remains a separate contract.</summary>
public static class BankOfChinaGenerationBudget
{
    public const double RelativeTolerance = 0.10d;

    public static bool AppliesTo(Bank bank)
    {
        var name = bank.Name.Trim();
        return bank.Type == BankTypes.Personal
            && (bank.Id == 18 || name is "中行" or "中国银行");
    }

    public static double ExpenseTarget(FlowGenerationConfig config) =>
        Math.Round(Math.Max(0, config.AllInMoney - config.LastMoney), 2, MidpointRounding.AwayFromZero);

    public static (double Min, double Max) Bounds(double target) =>
        (Math.Round(target * (1 - RelativeTolerance), 2, MidpointRounding.AwayFromZero),
         Math.Round(target * (1 + RelativeTolerance), 2, MidpointRounding.AwayFromZero));

    public static bool TryValidate(
        Bank bank, FlowGenerationConfig config, FlowAutoGenerationResult result, out string reason)
    {
        reason = string.Empty;
        if (!AppliesTo(bank)) return true;
        return ValidateAmount("收入", config.AllInMoney, result.IncomeTotal, out reason)
            && ValidateAmount("支出", ExpenseTarget(config), result.ExpenseTotal, out reason);
    }

    private static bool ValidateAmount(string direction, double target, double actual, out string reason)
    {
        reason = string.Empty;
        if (!double.IsFinite(target) || target < 0 || !double.IsFinite(actual) || actual < 0)
        {
            reason = $"中行{direction}配置或生成金额无效";
            return false;
        }
        var bounds = Bounds(target);
        if (actual >= bounds.Min - 0.009d && actual <= bounds.Max + 0.009d) return true;
        reason = $"中行{direction}合计偏离配置超过10%：目标 {target:N2}，实际 {actual:N2}，" +
                 $"允许范围 {bounds.Min:N2}～{bounds.Max:N2}。请检查勾选规则的金额容量、次数和可交易日期";
        return false;
    }
}
