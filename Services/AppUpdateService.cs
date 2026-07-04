using System.IO;
using System.Threading;
using System.Windows;
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
            if (updateInfo?.TargetFullRelease is null)
            {
                return;
            }

            var confirmed = await ConfirmUpdateAsync(
                manager.CurrentVersion.ToString(),
                updateInfo.TargetFullRelease.Version.ToString()).ConfigureAwait(false);
            if (!confirmed)
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

    private static async Task<bool> ConfirmUpdateAsync(string currentVersion, string targetVersion)
    {
        var application = Application.Current;
        if (application is null)
        {
            return false;
        }

        return await application.Dispatcher.InvokeAsync(() =>
        {
            var owner = application.MainWindow;
            var message = $"检测到新版本 {targetVersion}，当前版本 {currentVersion}。\n\n是否立即更新？\n更新完成后程序会自动重启。";

            return owner is null
                ? MessageBox.Show(message, "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes
                : MessageBox.Show(owner, message, "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
        });
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
