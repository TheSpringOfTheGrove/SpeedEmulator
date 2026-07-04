using System.IO;
using System.Threading;
using System.Windows;
using SpeedEmulator.Views;
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

        UpdateProgressWindow? progressWindow = null;
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

            progressWindow = await ShowProgressWindowAsync(
                manager.CurrentVersion.ToString(),
                updateInfo.TargetFullRelease.Version.ToString()).ConfigureAwait(false);

            ReportDownloadProgress(progressWindow, 0);
            await manager.DownloadUpdatesAsync(
                updateInfo,
                progress => ReportDownloadProgress(progressWindow, progress),
                CancellationToken.None).ConfigureAwait(false);
            await ShowInstallingAsync(progressWindow).ConfigureAwait(false);
            manager.ApplyUpdatesAndRestart(updateInfo.TargetFullRelease);
        }
        catch (Exception exception)
        {
            await CloseProgressWindowAsync(progressWindow).ConfigureAwait(false);
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

    private static async Task<UpdateProgressWindow?> ShowProgressWindowAsync(string currentVersion, string targetVersion)
    {
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return await application.Dispatcher.InvokeAsync(() =>
        {
            var window = new UpdateProgressWindow(currentVersion, targetVersion);
            var owner = application.MainWindow;
            if (owner is { IsVisible: true })
            {
                window.Owner = owner;
            }

            window.Show();
            return window;
        });
    }

    private static void ReportDownloadProgress(UpdateProgressWindow? window, int progress)
    {
        var application = Application.Current;
        if (application is null || window is null)
        {
            return;
        }

        var normalizedProgress = Math.Clamp(progress, 0, 100);
        _ = application.Dispatcher.BeginInvoke(() =>
        {
            if (window.IsVisible)
            {
                window.SetProgress(normalizedProgress, $"正在下载更新... {normalizedProgress}%");
            }
        });
    }

    private static async Task ShowInstallingAsync(UpdateProgressWindow? window)
    {
        var application = Application.Current;
        if (application is null || window is null)
        {
            return;
        }

        await application.Dispatcher.InvokeAsync(() =>
        {
            if (window.IsVisible)
            {
                window.SetInstalling();
            }
        });
    }

    private static async Task CloseProgressWindowAsync(UpdateProgressWindow? window)
    {
        var application = Application.Current;
        if (application is null || window is null)
        {
            return;
        }

        await application.Dispatcher.InvokeAsync(() =>
        {
            if (window.IsVisible)
            {
                window.Close();
            }
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
