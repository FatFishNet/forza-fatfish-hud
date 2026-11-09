using System.IO;
using System.Windows;
using ForzaOverlay.App.Services;

namespace ForzaOverlay.App.Ui;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        VersionText.Text = $"当前版本 {UpdateService.CurrentVersionText} → 新版本 {info.Version}";
        NotesBox.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "（该版本没有更新说明）"
            : info.ReleaseNotes;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateApplier.CanWriteAppDirectory())
        {
            MessageBox.Show(
                this,
                "程序所在目录不可写，无法自动更新。\n请把程序放到可写目录，或到 GitHub Releases 手动下载。",
                "无法更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        InstallButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        StatusText.Text = "正在下载…";

        try
        {
            Progress<double> progress = new(value => ProgressBar.Value = value * 100);
            string zipPath = await UpdateService.DownloadAsync(_info, progress);
            StatusText.Text = "正在准备更新…";
            string extractDirectory = Path.Combine(UpdateService.GetTempDirectory(), "update-" + _info.Version);
            string scriptPath = UpdateApplier.PrepareApplyScript(zipPath, extractDirectory);
            StatusText.Text = "即将重启并完成更新…";
            await Task.Delay(600);
            UpdateApplier.LaunchAndExit(scriptPath);
        }
        catch (Exception ex)
        {
            ForzaOverlay.Core.Logging.Diagnostics.Write("更新失败: " + ex);
            StatusText.Text = "更新失败：" + ex.Message;
            InstallButton.IsEnabled = true;
        }
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
