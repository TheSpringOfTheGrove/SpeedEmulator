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

    public IReadOnlyList<AuthorizedBankInfo> AuthorizedBanks => authorizedBanks;

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    public string AuthorizationHeader => $"{TokenType} {Token}".Trim();

    public void Apply(FrontLoginData data)
    {
        Token = data.Token ?? string.Empty;
        TokenType = string.IsNullOrWhiteSpace(data.TokenType) ? "Bearer" : data.TokenType;
        ExpiresAt = data.ExpiresAt;
        AccountId = data.AccountId;
        Account = data.Account ?? string.Empty;
        DisplayName = data.DisplayName ?? string.Empty;
        MachineCode = data.MachineCode ?? string.Empty;

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

    public List<AuthorizedBankInfo>? AuthorizedBanks { get; set; }
}

public sealed class AuthorizedBankInfo
{
    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Category { get; set; }
}
