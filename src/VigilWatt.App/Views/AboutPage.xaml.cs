using System.Diagnostics;
using VigilWatt.App.Services;
using VigilWatt.Core.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace VigilWatt.App.Views;

/// <summary>
/// 功 能： 关于页：介绍、检查更新与项目联系。
/// 2026/10/16 15:37:49 xliu
/// </summary>
public sealed partial class AboutPage : Page
{
    private const string RepoUrl = "https://gitee.com/YGWG-LX/vigil-watt";
    private const string ReleasesUrl = "https://gitee.com/YGWG-LX/vigil-watt/releases";

    public AboutPage()
    {
        InitializeComponent();
        App.UpdateStatusChanged += status => DispatcherQueue.TryEnqueue(() =>
            UpdateStatusText.Text = status);
        UpdateDownloadUi.ProgressChanged += OnUpdateProgressChanged;
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = GetType().Assembly.GetName().Version?.ToString(3) ?? "—";
        UpdateStatusText.Text = string.IsNullOrEmpty(App.UpdateStatusMessage)
            ? "检查更新"
            : App.UpdateStatusMessage;
    }

    private void OnUpdateProgressChanged(bool active, int percent, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!active)
            {
                UpdateProgressBar.Visibility = Visibility.Collapsed;
                UpdateProgressBar.IsIndeterminate = false;
                return;
            }

            UpdateProgressBar.Visibility = Visibility.Visible;
            if (percent <= 0)
            {
                UpdateProgressBar.IsIndeterminate = true;
            }
            else
            {
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = Math.Clamp(percent, 0, 100);
            }
        });
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        App.UpdateStatusMessage = "正在检查 Gitee 更新…";
        UpdateStatusText.Text = App.UpdateStatusMessage;
        try
        {
            var svc = new UpdateService();
            if (!svc.IsInstalled)
            {
                App.UpdateStatusMessage = "当前是未安装的运行副本（bin/publish），无法自动更新";
                UpdateStatusText.Text = App.UpdateStatusMessage;
                return;
            }

            var info = await svc.CheckAsync();
            if (info == null)
            {
                App.UpdateStatusMessage = $"已是最新版本（{svc.CurrentVersion}）";
                UpdateStatusText.Text = App.UpdateStatusMessage;
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "发现新版本 " + info.TargetFullRelease.Version,
                Content = $"当前 {svc.CurrentVersion} → 最新 {info.TargetFullRelease.Version}，下载并安装？\n安装完成后应用会自动重启。",
                PrimaryButtonText = "下载并安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                App.UpdateStatusMessage = "已取消更新";
                UpdateStatusText.Text = App.UpdateStatusMessage;
                return;
            }

            await UpdateDownloadUi.DownloadAndApplyAsync(svc, info, XamlRoot);
        }
        catch (Exception ex)
        {
            Log.Warn("检查更新失败: " + ex.Message);
            if (!App.UpdateStatusMessage.StartsWith("更新安装失败", StringComparison.Ordinal))
                App.UpdateStatusMessage = "检查更新失败：" + ex.Message;
            UpdateStatusText.Text = App.UpdateStatusMessage;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenUrl(ReleasesUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开链接失败: {ex.Message}");
        }
    }
}
