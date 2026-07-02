using System.IO;
using System.Threading;
using Velopack;
using Velopack.Sources;

namespace SpeedEmulator.Services;

public static class AppUpdateService
{
    private static int hasStarted;

    public static void StartBackgroundCheck()
    {
        if (Interlocked.Exchange(ref hasStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(CheckAndApplyAsync);
    }

    private static async Task CheckAndApplyAsync()
    {
        var options = AppUpdateConfiguration.Load();
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.FeedUrl))
        {
            return;
        }

        try
        {
            var timeoutMinutes = Math.Max(1.0, options.TimeoutSeconds / 60.0);
            var source = new SimpleWebSource(options.FeedUrl, null, timeoutMinutes);
            var manager = new UpdateManager(source);

            if (manager.CurrentVersion is null)
            {
                return;
            }

            var updateInfo = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo is null)
            {
                return;
            }

            await manager.DownloadUpdatesAsync(updateInfo, null, CancellationToken.None).ConfigureAwait(false);
            manager.ApplyUpdatesAndRestart(updateInfo.TargetFullRelease);
        }
        catch (Exception exception)
        {
            WriteLog(exception);
        }
    }

    private static void WriteLog(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpeedEmulator",
                "logs");
            Directory.CreateDirectory(logDirectory);

            var logPath = Path.Combine(logDirectory, "update.log");
            var message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}";
            File.AppendAllText(logPath, message);
        }
        catch
        {
            // Update failures must never block normal login.
        }
    }
}
