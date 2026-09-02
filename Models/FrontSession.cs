namespace SpeedEmulator.Models;

public sealed class FrontSession
{
    private readonly List<AuthorizedBankInfo> authorizedBanks = [];

    public string Token { get; private set; } = string.Empty;

    public string TokenType { get; private set; } = "Bearer";

    public DateTime ExpiresAt { get; private set; }

    public long AccountId { get; private set; }

    public string Account { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string MachineCode { get; private set; } = string.Empty;

    public string PermissionPlan { get; private set; } = PermissionPlans.Basic;

    public string PermissionName { get; private set; } = "基础版本";

    public bool CanUsePersonalBank { get; private set; } = true;

    public bool CanUseCorporateBank { get; private set; }

    public bool CanUploadPdf { get; private set; }

    public IReadOnlyList<AuthorizedBankInfo> AuthorizedBanks => authorizedBanks;

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    public string AuthorizationHeader => $"{TokenType} {Token}".Trim();

    public void Clear()
    {
        Token = string.Empty;
        TokenType = "Bearer";
        ExpiresAt = default;
        AccountId = 0;
        Account = string.Empty;
        DisplayName = string.Empty;
        MachineCode = string.Empty;
        PermissionPlan = PermissionPlans.Basic;
        PermissionName = "基础版本";
        CanUsePersonalBank = true;
        CanUseCorporateBank = false;
        CanUploadPdf = false;
        authorizedBanks.Clear();
    }

    public void Apply(FrontLoginData data, bool preserveExistingToken = false)
    {
        if (!preserveExistingToken || !string.IsNullOrWhiteSpace(data.Token))
        {
            Token = data.Token?.Trim() ?? string.Empty;
        }

        if (!preserveExistingToken || !string.IsNullOrWhiteSpace(data.TokenType))
        {
            TokenType = string.IsNullOrWhiteSpace(data.TokenType) ? "Bearer" : data.TokenType.Trim();
        }

        ExpiresAt = data.ExpiresAt;
        AccountId = data.AccountId;
        Account = data.Account ?? string.Empty;
        DisplayName = data.DisplayName ?? string.Empty;
        MachineCode = data.MachineCode ?? string.Empty;
        PermissionPlan = PermissionPlans.Normalize(data.PermissionPlan);
        PermissionName = string.IsNullOrWhiteSpace(data.PermissionName)
            ? PermissionPlans.GetDisplayName(PermissionPlan)
            : data.PermissionName.Trim();
        CanUsePersonalBank = data.CanUsePersonalBank;
        CanUseCorporateBank = data.CanUseCorporateBank;
        CanUploadPdf = data.CanUploadPdf;

        authorizedBanks.Clear();
        foreach (var bank in data.AuthorizedBanks ?? [])
        {
            if (!string.IsNullOrWhiteSpace(bank.Name))
            {
                authorizedBanks.Add(bank);
            }
        }
    }
}

public sealed class FrontLoginData
{
    public string? Token { get; set; }

    public string? TokenType { get; set; }

    public DateTime ExpiresAt { get; set; }

    public long AccountId { get; set; }

    public string? Account { get; set; }

    public string? DisplayName { get; set; }

    public string? MachineCode { get; set; }

    public string? PermissionPlan { get; set; }

    public string? PermissionName { get; set; }

    public bool CanUsePersonalBank { get; set; } = true;

    public bool CanUseCorporateBank { get; set; }

    public bool CanUploadPdf { get; set; }

    public List<AuthorizedBankInfo>? AuthorizedBanks { get; set; }
}

public static class PermissionPlans
{
    public const string Basic = "BASIC";
    public const string Enhanced = "ENHANCED";
    public const string Universal = "UNIVERSAL";

    public static string Normalize(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            Enhanced => Enhanced,
            Universal => Universal,
            _ => Basic
        };
    }

    public static string GetDisplayName(string permissionPlan)
    {
        return permissionPlan switch
        {
            Enhanced => "增强版",
            Universal => "通用版",
            _ => "基础版本"
        };
    }
}

public sealed class AuthorizedBankInfo
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Category { get; set; }
}
