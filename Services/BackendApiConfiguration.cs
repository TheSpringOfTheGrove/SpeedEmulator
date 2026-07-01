using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Services;

public sealed class BackendApiOptions
{
    public const string DefaultBaseAddress = "http://159.75.125.68:8088";

    public string BaseAddress { get; set; } = DefaultBaseAddress;

    public int TimeoutSeconds { get; set; } = 8;
}

public static class BackendApiConfiguration
{
    private const string PrimaryBaseAddressEnvironmentVariable = "SPEEDEMULATOR_API_BASE_URL";
    private const string LegacyBaseAddressEnvironmentVariable = "SPEEDEMULATOR_ADMIN_URL";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static BackendApiOptions Load()
    {
        var options = LoadFromAppSettings() ?? new BackendApiOptions();
        var configuredBaseAddress = ReadConfiguredBaseAddress();
        if (!string.IsNullOrWhiteSpace(configuredBaseAddress))
        {
            options.BaseAddress = configuredBaseAddress.Trim();
        }

        options.BaseAddress = EnsureTrailingSlash(
            string.IsNullOrWhiteSpace(options.BaseAddress)
                ? BackendApiOptions.DefaultBaseAddress
                : options.BaseAddress.Trim());
        options.TimeoutSeconds = Math.Clamp(options.TimeoutSeconds, 3, 60);
        return options;
    }

    private static BackendApiOptions? LoadFromAppSettings()
    {
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Environment.CurrentDirectory, "appsettings.json")
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = File.OpenRead(path);
            var root = JsonSerializer.Deserialize<AppSettingsRoot>(stream, JsonOptions);
            return root?.BackendApi;
        }

        return null;
    }

    private static string? ReadConfiguredBaseAddress()
    {
        var currentName = Environment.GetEnvironmentVariable(PrimaryBaseAddressEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(currentName))
        {
            return currentName;
        }

        return Environment.GetEnvironmentVariable(LegacyBaseAddressEnvironmentVariable);
    }

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    private sealed class AppSettingsRoot
    {
        public BackendApiOptions? BackendApi { get; set; }
    }
}
