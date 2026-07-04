using System.Windows;

namespace SpeedEmulator.Views;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow(string currentVersion, string targetVersion)
    {
        InitializeComponent();
        VersionText.Text = $"当前版本 {currentVersion}  ->  新版本 {targetVersion}";
    }

    public void SetProgress(int progress, string status)
    {
        var normalizedProgress = Math.Clamp(progress, 0, 100);
        DownloadProgressBar.Value = normalizedProgress;
        PercentText.Text = $"{normalizedProgress}%";
        StatusText.Text = status;
    }

    public void SetInstalling()
    {
        TitleText.Text = "正在安装更新";
        SetProgress(100, "下载完成，正在安装并重启...");
    }
}
