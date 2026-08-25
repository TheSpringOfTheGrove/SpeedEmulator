using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Services;

public sealed class AppUpdateOptions
{
    public const string DefaultFeedUrl = "http://159.75.125.68/speedemulator/updates/";

    public bool Enabled { get; set; } = true;

    public string FeedUrl { get; set; } = DefaultFeedUrl;

    public int TimeoutSeconds { get; set; } = 30;
}

public static class AppUpdateConfiguration
{
    private const string FeedUrlEnvironmentVariable = "SPEEDEMULATOR_UPDATE_FEED_URL";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static AppUpdateOptions Load()
    {
        var options = LoadFromAppSettings() ?? new AppUpdateOptions();
        var configuredFeedUrl = Environment.GetEnvironmentVariable(FeedUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredFeedUrl))
        {
            options.FeedUrl = configuredFeedUrl.Trim();
        }

        options.FeedUrl = EnsureTrailingSlash(
            string.IsNullOrWhiteSpace(options.FeedUrl)
                ? AppUpdateOptions.DefaultFeedUrl
                : options.FeedUrl.Trim());
        options.TimeoutSeconds = Math.Clamp(options.TimeoutSeconds, 5, 180);
        return options;
    }

    private static AppUpdateOptions? LoadFromAppSettings()
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
            return root?.Update;
        }

        return null;
    }

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    private sealed class AppSettingsRoot
    {
        public AppUpdateOptions? Update { get; set; }
    }
}
