using System.Net.Http;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public interface IFrontApiClient
{
    Task<FrontSession> LoginAsync(
        string account,
        string password,
        string machineCode,
        string networkIp,
        string loginRegion,
        CancellationToken cancellationToken = default);

    Task<FrontSession> ValidateSessionAsync(CancellationToken cancellationToken = default);

    Task<FrontAnnouncement> GetAnnouncementAsync(CancellationToken cancellationToken = default);

    Task<BankUser> SaveBankUserAsync(Bank bank, BankUser user, CancellationToken cancellationToken = default);

    HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string requestUri);
}

public sealed class FrontApiClient : IFrontApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] ContactFieldNames =
    [
        "contactConfig",
        "contact",
        "contactText",
        "serviceContact",
        "customerService"
    ];

    private static readonly string[] AnnouncementFieldNames =
    [
        "announcementContent",
        "announcement",
        "announcementText",
        "content",
        "notice"
    ];

    private readonly HttpClient httpClient;
    private readonly FrontSession session;
    private readonly BackendApiOptions apiOptions;

    public FrontApiClient(FrontSession session, BackendApiOptions? apiOptions = null)
    {
        this.session = session;
        this.apiOptions = apiOptions ?? BackendApiConfiguration.Load();
        httpClient = new HttpClient
        {
            BaseAddress = new Uri(this.apiOptions.BaseAddress),
            Timeout = TimeSpan.FromSeconds(this.apiOptions.TimeoutSeconds)
        };
    }

    public async Task<FrontSession> LoginAsync(
        string account,
        string password,
        string machineCode,
        string networkIp,
        string loginRegion,
        CancellationToken cancellationToken = default)
    {
        var request = new FrontLoginRequestDto(account, password, machineCode, networkIp, loginRegion);
        using var response = await SendAsync(
            () => httpClient.PostAsJsonAsync("api/front/login", request, JsonOptions, cancellationToken),
            cancellationToken);

        var payload = await ReadPayloadAsync<FrontLoginData>(response, "登录失败", cancellationToken);
        session.Apply(payload);
        return session;
    }

    public async Task<FrontSession> ValidateSessionAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "api/front/session");
        using var response = await SendAsync(() => httpClient.SendAsync(request, cancellationToken), cancellationToken);
        var payload = await ReadPayloadAsync<FrontLoginData>(response, "会话验证失败", cancellationToken);
        session.Apply(payload);
        return session;
    }

    public async Task<FrontAnnouncement> GetAnnouncementAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "api/front/announcement");
        using var response = await SendAsync(() => httpClient.SendAsync(request, cancellationToken), cancellationToken);
        return await ReadAnnouncementAsync(response, cancellationToken);
    }

    public async Task<BankUser> SaveBankUserAsync(Bank bank, BankUser user, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bank.Code))
        {
            throw new FrontApiException("当前银行缺少后台授权 Code，请重新登录后再保存。");
        }

        var isUpdate = user.BackendId > 0;
        var requestUri = isUpdate ? $"api/front/bank-users/{user.BackendId}" : "api/front/bank-users";
        var method = isUpdate ? HttpMethod.Put : HttpMethod.Post;
        using var request = CreateAuthorizedRequest(method, requestUri);
        request.Content = JsonContent.Create(CreateBankUserRequest(bank, user), options: JsonOptions);

        using var response = await SendAsync(() => httpClient.SendAsync(request, cancellationToken), cancellationToken);
        var payload = await ReadPayloadAsync<FrontBankUserResponseDto>(
            response,
            isUpdate ? "修改用户信息失败" : "保存用户信息失败",
            cancellationToken);

        return ApplyBankUserResponse(bank, user, payload);
    }

    public HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        if (session.HasToken)
        {
            request.Headers.TryAddWithoutValidation("Authorization", session.AuthorizationHeader);
        }

        return request;
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        try
        {
            return await send();
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FrontApiException($"连接后台服务超时，请确认 {apiOptions.BaseAddress} 已启动。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new FrontApiException($"无法连接后台服务，请确认 {apiOptions.BaseAddress} 已启动。", ex);
        }
    }

    private static async Task<T> ReadPayloadAsync<T>(HttpResponseMessage response, string fallbackMessage, CancellationToken cancellationToken)
    {
        ApiResponseDto<T>? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ApiResponseDto<T>>(JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new FrontApiException($"{fallbackMessage}：后台返回格式无法解析。", ex);
        }

        if (body is null)
        {
            throw new FrontApiException($"{fallbackMessage}：后台没有返回数据。");
        }

        if (!response.IsSuccessStatusCode || !body.Success || body.Data is null)
        {
            var message = string.IsNullOrWhiteSpace(body.Message) ? fallbackMessage : body.Message;
            throw new FrontApiException(message);
        }

        return body.Data;
    }

    private static async Task<FrontAnnouncement> ReadAnnouncementAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new FrontApiException("读取公告配置失败：后台返回格式无法解析。", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var payload = root;
            string? responseMessage = null;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(root, "message", out var messageElement))
                {
                    responseMessage = ReadStringValue(messageElement);
                }

                if (TryGetPropertyIgnoreCase(root, "success", out var successElement)
                    && ReadBooleanValue(successElement) == false)
                {
                    throw new FrontApiException(string.IsNullOrWhiteSpace(responseMessage)
                        ? "读取公告配置失败。"
                        : responseMessage);
                }

                if (TryGetPropertyIgnoreCase(root, "data", out var dataElement))
                {
                    if (dataElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    {
                        throw new FrontApiException("读取公告配置失败：后台没有返回数据。");
                    }

                    payload = dataElement;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new FrontApiException(string.IsNullOrWhiteSpace(responseMessage)
                    ? "读取公告配置失败。"
                    : responseMessage);
            }

            var contactText = FindStringProperty(payload, ContactFieldNames) ?? string.Empty;
            var announcementText = FindStringProperty(payload, AnnouncementFieldNames) ?? string.Empty;
            return new FrontAnnouncement(contactText, announcementText);
        }
    }

    private static string? FindStringProperty(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                var text = ReadStringValue(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => null
        };
    }

    private static bool? ReadBooleanValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var value) => value,
            _ => null
        };
    }

    private sealed record FrontLoginRequestDto(
        string Account,
        string Password,
        string MachineCode,
        string NetworkIp,
        string LoginRegion);

    private static FrontBankUserRequestDto CreateBankUserRequest(Bank bank, BankUser user)
    {
        return new FrontBankUserRequestDto
        {
            BankCode = bank.Code.Trim(),
            UserCode = user.UserCode,
            AccountName = user.AccountName,
            AccountNo = user.AccountNo,
            IdNumber = user.IdNumber,
            PhoneNumber = user.PhoneNumber,
            OpenBranch = user.OpenBranch,
            Balance = user.Balance,
            LoginPassword = user.LoginPassword,
            PaymentPassword = user.PaymentPassword,
            UShieldNo = user.UShieldNo,
            Remark = user.Remark,
            StartDate = FormatLocalDateTime(user.StartDate),
            EndDate = FormatLocalDateTime(user.EndDate),
            TransactionType = user.TransactionType,
            Currency = user.Currency,
            ChapterCode = user.ChapterCode,
            ChapterBranch = user.ChapterBranch,
            ShouldPrintSeal = user.ShouldPrintSeal,
            OpeningBalance = user.OpeningBalance,
            AutoCalculateInterest = user.AutoCalculateInterest,
            SealImagePath = null,
            Enabled = true
        };
    }

    private static string FormatLocalDateTime(DateTime value)
    {
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static BankUser ApplyBankUserResponse(Bank bank, BankUser source, FrontBankUserResponseDto response)
    {
        var user = source.Clone();
        user.BackendId = response.Id ?? user.BackendId;
        user.BankId = bank.Id;
        user.BankName = string.IsNullOrWhiteSpace(response.BankName) ? bank.Name : response.BankName;
        user.UserCode = response.UserCode ?? user.UserCode;
        user.AccountName = response.AccountName ?? user.AccountName;
        user.AccountNo = response.AccountNo ?? user.AccountNo;
        user.IdNumber = response.IdNumber ?? user.IdNumber;
        user.PhoneNumber = response.PhoneNumber ?? user.PhoneNumber;
        user.OpenBranch = response.OpenBranch ?? user.OpenBranch;
        user.Balance = response.Balance ?? user.Balance;
        user.LoginPassword = response.LoginPassword ?? user.LoginPassword;
        user.PaymentPassword = response.PaymentPassword ?? user.PaymentPassword;
        user.UShieldNo = response.UShieldNo ?? user.UShieldNo;
        user.Remark = response.Remark ?? user.Remark;
        user.StartDate = response.StartDate ?? user.StartDate;
        user.EndDate = response.EndDate ?? user.EndDate;
        user.TransactionType = response.TransactionType ?? user.TransactionType;
        user.Currency = response.Currency ?? user.Currency;
        user.ChapterCode = response.ChapterCode ?? user.ChapterCode;
        user.ChapterBranch = response.ChapterBranch ?? user.ChapterBranch;
        user.ShouldPrintSeal = response.ShouldPrintSeal ?? user.ShouldPrintSeal;
        user.OpeningBalance = response.OpeningBalance ?? user.OpeningBalance;
        user.AutoCalculateInterest = response.AutoCalculateInterest ?? user.AutoCalculateInterest;
        user.SealImagePath = source.SealImagePath;
        user.CreatedAt = response.CreatedAt ?? user.CreatedAt;
        user.UpdatedAt = response.UpdatedAt ?? user.UpdatedAt;
        return user;
    }

    private sealed class FrontBankUserRequestDto
    {
        public string BankCode { get; set; } = string.Empty;

        public string UserCode { get; set; } = string.Empty;

        public string AccountName { get; set; } = string.Empty;

        public string AccountNo { get; set; } = string.Empty;

        public string IdNumber { get; set; } = string.Empty;

        public string PhoneNumber { get; set; } = string.Empty;

        public string OpenBranch { get; set; } = string.Empty;

        public decimal Balance { get; set; }

        public string LoginPassword { get; set; } = string.Empty;

        public string PaymentPassword { get; set; } = string.Empty;

        public string UShieldNo { get; set; } = string.Empty;

        public string Remark { get; set; } = string.Empty;

        public string StartDate { get; set; } = string.Empty;

        public string EndDate { get; set; } = string.Empty;

        public string TransactionType { get; set; } = string.Empty;

        public string Currency { get; set; } = string.Empty;

        public string ChapterCode { get; set; } = string.Empty;

        public string ChapterBranch { get; set; } = string.Empty;

        public bool ShouldPrintSeal { get; set; }

        public decimal OpeningBalance { get; set; }

        public bool AutoCalculateInterest { get; set; }

        public string? SealImagePath { get; set; }

        public bool Enabled { get; set; }
    }

    private sealed class FrontBankUserResponseDto
    {
        public long? Id { get; set; }

        public long? BankCatalogId { get; set; }

        public string? BankCode { get; set; }

        public string? BankName { get; set; }

        public string? BankCategory { get; set; }

        public string? UserCode { get; set; }

        public string? AccountName { get; set; }

        public string? AccountNo { get; set; }

        public string? IdNumber { get; set; }

        public string? PhoneNumber { get; set; }

        public string? OpenBranch { get; set; }

        public decimal? Balance { get; set; }

        public string? LoginPassword { get; set; }

        public string? PaymentPassword { get; set; }

        public string? UShieldNo { get; set; }

        public string? Remark { get; set; }

        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        public string? TransactionType { get; set; }

        public string? Currency { get; set; }

        public string? ChapterCode { get; set; }

        public string? ChapterBranch { get; set; }

        public bool? ShouldPrintSeal { get; set; }

        public decimal? OpeningBalance { get; set; }

        public bool? AutoCalculateInterest { get; set; }

        public string? SealImagePath { get; set; }

        public bool? Enabled { get; set; }

        public DateTime? CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }
    }

    private sealed class ApiResponseDto<T>
    {
        public bool Success { get; set; }

        public string? Message { get; set; }

        public T? Data { get; set; }
    }
}

public sealed class FrontApiException : Exception
{
    public FrontApiException(string message)
        : base(message)
    {
    }

    public FrontApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
