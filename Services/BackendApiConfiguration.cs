using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Services;

public sealed class BackendApiOptions
{
    public const string DefaultBaseAddress = "http://159.75.125.68";

    public string BaseAddress { get; set; } = DefaultBaseAddress;

    public int TimeoutSeconds { get; set; } = 8;
}

public static class BackendApiConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static BackendApiOptions Load()
    {
        var options = LoadFromAppSettings() ?? new BackendApiOptions();
        options.BaseAddress = EnsureTrailingSlash(BackendApiOptions.DefaultBaseAddress);
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

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    private sealed class AppSettingsRoot
    {
        public BackendApiOptions? BackendApi { get; set; }
    }
}
