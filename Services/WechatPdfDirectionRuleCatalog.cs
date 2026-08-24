using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Services;

internal static class WechatPdfDirectionRuleCatalog
{
    public const string FileName = "wechat-pdf-other-direction-rules.json";
    public const string UnresolvedDirectionField = "__WechatPdfUnresolvedDirection";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static WechatPdfDirectionRuleSet Load()
    {
        var path = EnumerateCandidatePaths().FirstOrDefault(File.Exists);
        if (path is null)
        {
            return new WechatPdfDirectionRuleSet(
                new Dictionary<string, string>(StringComparer.Ordinal),
                $"缺少微信 PDF 交易方向配置文件 Data\\{FileName}。");
        }

        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<WechatPdfDirectionRuleDocument>(stream, JsonOptions);
            if (document?.Rules.Count is not > 0)
            {
                return new WechatPdfDirectionRuleSet(
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    $"微信 PDF 交易方向配置文件 Data\\{FileName} 没有有效规则。");
            }

            var rules = new Dictionary<string, string>(StringComparer.Ordinal);
            var errors = new List<string>();
            foreach (var rule in document.Rules)
            {
                var tradeType = NormalizeTradeType(rule.TradeType);
                var direction = rule.Direction?.Trim() ?? string.Empty;
                if (tradeType.Length == 0 || direction is not ("收入" or "支出"))
                {
                    errors.Add($"交易类型“{rule.TradeType}”的方向必须是“收入”或“支出”");
                    continue;
                }

                if (rules.TryGetValue(tradeType, out var existingDirection)
                    && !string.Equals(existingDirection, direction, StringComparison.Ordinal))
                {
                    errors.Add($"交易类型“{rule.TradeType}”同时配置了“{existingDirection}”和“{direction}”");
                    continue;
                }

                rules[tradeType] = direction;
            }

            var errorMessage = errors.Count == 0
                ? null
                : $"微信 PDF 交易方向配置无效：{string.Join("；", errors)}。";
            return new WechatPdfDirectionRuleSet(rules, errorMessage);
        }
        catch (Exception ex)
        {
            return new WechatPdfDirectionRuleSet(
                new Dictionary<string, string>(StringComparer.Ordinal),
                $"读取微信 PDF 交易方向配置 Data\\{FileName} 失败：{ex.Message}");
        }
    }

    public static string NormalizeTradeType(string? value)
    {
        return string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character))).Trim();
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Data", FileName);
        yield return Path.Combine(AppContext.BaseDirectory, FileName);
    }

    private sealed class WechatPdfDirectionRuleDocument
    {
        public int Version { get; init; }

        public List<WechatPdfDirectionRule> Rules { get; init; } = [];
    }

    private sealed class WechatPdfDirectionRule
    {
        public string TradeType { get; init; } = string.Empty;

        public string Direction { get; init; } = string.Empty;
    }
}

internal sealed class WechatPdfDirectionRuleSet(
    IReadOnlyDictionary<string, string> rules,
    string? errorMessage)
{
    public string? ErrorMessage { get; } = errorMessage;

    public bool TryResolve(string tradeType, out string direction)
    {
        return rules.TryGetValue(WechatPdfDirectionRuleCatalog.NormalizeTradeType(tradeType), out direction!);
    }
}
