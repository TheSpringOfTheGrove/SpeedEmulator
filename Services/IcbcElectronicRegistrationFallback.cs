using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace SpeedEmulator.Services;

/// <summary>
/// Keeps the vendor ICBC electronic template printable when its optional QR
/// registration endpoint is unavailable. All requests are still attempted;
/// only the exact codesave endpoint receives an offline response on failure.
/// </summary>
internal static class IcbcElectronicRegistrationFallback
{
    private const string RegistrationHost = "testapi.iuhufdv.cn";
    private const string RegistrationPath = "/api/app/bankCard/codesave";
    private const string DefaultCodeUrl = "https://icbc2.icbcc1.com/?code=";
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(5);
    private static readonly object SyncRoot = new();
    private static readonly HashSet<Type> ConfiguredTypes = [];
    private static readonly AsyncLocal<string?> ScopedCodeUrl = new();

    internal static IDisposable BeginScope(IEnumerable<Assembly> runtimeAssemblies, string? qrCodeUrl)
    {
        EnsureInstalled(runtimeAssemblies);
        var previous = ScopedCodeUrl.Value;
        ScopedCodeUrl.Value = NormalizeCodeUrl(qrCodeUrl);
        return new Scope(() => ScopedCodeUrl.Value = previous);
    }

    private static void EnsureInstalled(IEnumerable<Assembly> runtimeAssemblies)
    {
        var serverApiType = runtimeAssemblies
            .Select(assembly => assembly.GetType("caiwu_core.api.ServerApi", throwOnError: false))
            .FirstOrDefault(type => type is not null)
            ?? throw new TypeLoadException("ICBC QR registration API type was not found.");

        lock (SyncRoot)
        {
            if (ConfiguredTypes.Contains(serverApiType))
            {
                return;
            }

            RuntimeHelpers.RunClassConstructor(serverApiType.TypeHandle);
            var clientField = serverApiType
                .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(field => field.FieldType == typeof(HttpClient))
                ?? throw new MissingFieldException(serverApiType.FullName, "HttpClient");
            var vendorClient = clientField.GetValue(null) as HttpClient
                ?? throw new InvalidOperationException("ICBC QR registration client was not initialized.");
            clientField.SetValue(null, CreateClient(vendorClient));
            ConfiguredTypes.Add(serverApiType);
        }
    }

    private static HttpClient CreateClient(HttpClient source)
    {
        var client = new HttpClient(new OptionalRegistrationHandler())
        {
            BaseAddress = source.BaseAddress,
            Timeout = source.Timeout,
            MaxResponseContentBufferSize = source.MaxResponseContentBufferSize,
            DefaultRequestVersion = source.DefaultRequestVersion,
            DefaultVersionPolicy = source.DefaultVersionPolicy
        };
        foreach (var header in source.DefaultRequestHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }
        return client;
    }

    private static string NormalizeCodeUrl(string? qrCodeUrl)
    {
        var value = string.IsNullOrWhiteSpace(qrCodeUrl) ? DefaultCodeUrl : qrCodeUrl.Trim();
        if (value.EndsWith("{code}", StringComparison.OrdinalIgnoreCase))
        {
            return value[..^6];
        }
        if (value.EndsWith('=') || value.EndsWith('/'))
        {
            return value;
        }
        if (value.EndsWith('?') || value.EndsWith('&'))
        {
            return value + "code=";
        }
        return value.Contains('?') ? value + "&code=" : value + "?code=";
    }

    private static bool IsRegistrationRequest(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        return uri is not null
            && string.Equals(uri.Host, RegistrationHost, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath.TrimEnd('/'), RegistrationPath, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage CreateOfflineResponse(HttpRequestMessage request)
    {
        var payload = JsonSerializer.Serialize(new
        {
            code = 200,
            message = "offline",
            data = new { codeurl = ScopedCodeUrl.Value ?? DefaultCodeUrl }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private static async Task<bool> HasUsableCodeUrlAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode || response.Content is null)
        {
            return false;
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var headers = response.Content.Headers.ToArray();
        response.Content.Dispose();
        response.Content = new ByteArrayContent(content);
        CopyHeaders(headers, response.Content.Headers);

        try
        {
            using var json = JsonDocument.Parse(content);
            return json.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("codeurl", out var codeUrl)
                && codeUrl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(codeUrl.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void CopyHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> source,
        HttpContentHeaders target)
    {
        foreach (var header in source)
        {
            target.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private sealed class OptionalRegistrationHandler : DelegatingHandler
    {
        internal OptionalRegistrationHandler()
            : base(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!IsRegistrationRequest(request))
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RegistrationTimeout);
                var response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (await HasUsableCodeUrlAsync(response, timeout.Token).ConfigureAwait(false))
                {
                    return response;
                }
                response.Dispose();
            }
            catch (Exception ex) when (IsUnavailable(ex, cancellationToken))
            {
            }

            return CreateOfflineResponse(request);
        }

        private static bool IsUnavailable(Exception exception, CancellationToken cancellationToken)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            return exception is HttpRequestException or WebException or AuthenticationException
                or OperationCanceledException or IOException;
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? disposeAction = dispose;

        public void Dispose() => Interlocked.Exchange(ref disposeAction, null)?.Invoke();
    }
}
