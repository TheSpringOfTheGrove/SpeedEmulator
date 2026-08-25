using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace SpeedEmulator.Services;

internal static partial class FrontApiDiagnosticLogger
{
    private const int MaxResponseBodyLength = 32 * 1024;
    private static readonly object SyncRoot = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpeedEmulator",
        "logs",
        "front-api.log");

    public static string WriteFailure(
        HttpMethod method,
        Uri requestUri,
        Exception exception,
        HttpStatusCode? statusCode = null,
        string? responseBody = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();
            builder.AppendLine(new string('=', 80));
            builder.AppendLine($"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            builder.AppendLine($"Request: {method.Method} {requestUri}");
            builder.AppendLine(statusCode.HasValue
                ? $"HTTP Status: {(int)statusCode.Value} {statusCode.Value}"
                : "HTTP Status: <not available>");
            builder.AppendLine("Response Body:");
            builder.AppendLine(SanitizeResponseBody(responseBody));
            builder.AppendLine("Exception.ToString():");
            builder.AppendLine(exception.ToString());
            builder.AppendLine("InnerException:");
            builder.AppendLine(exception.InnerException?.ToString() ?? "<none>");

            lock (SyncRoot)
            {
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostic logging must never replace the original API failure.
        }

        return LogPath;
    }

    private static string SanitizeResponseBody(string? responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
        {
            return "<not available>";
        }

        var body = responseBody.Length > MaxResponseBodyLength
            ? responseBody[..MaxResponseBodyLength] + "\n<truncated>"
            : responseBody;
        return SensitiveJsonValueRegex().Replace(body, "$1<redacted>$2");
    }

    [GeneratedRegex(
        "(?i)(\\\"(?:password|token|accessToken|refreshToken|authorization)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveJsonValueRegex();
}
