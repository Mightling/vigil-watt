using Velopack;
using Velopack.Sources;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 自动更新：包一层 Velopack 的 UpdateManager。
/// 2026/10/14 22:17:14 xliu
/// 更新源固定为公开 Gitee 仓库的最新 Release。
/// </summary>
public sealed class UpdateService
{
    // 更新源单一来源：scripts/publish-gitee.ps1 发布的必须是同一个仓库
    private const string RepoUrl = "https://gitee.com/YGWG-LX/vigil-watt";

    private readonly UpdateManager _mgr;

    public UpdateService()
    {
        // 用内置 GiteaSource：Gitee 的 /api/v1 与 /api/v5 返回一致，无需自写适配器。见 docs/dev/build.md §8
        // 匿名访问即可（公开仓库，限流 60 次/小时/IP）；accessToken / downloader 传 null 用默认实现
        _mgr = new UpdateManager(new GiteaSource(RepoUrl, null, false, null));
    }

    public bool IsInstalled => _mgr.IsInstalled;

    public string? CurrentVersion => _mgr.CurrentVersion?.ToString();

    /// <summary>检查更新。null = 已是最新；检查源不可达等异常向上抛，由调用方呈现。</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        return await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
    }

    public Task DownloadAsync(UpdateInfo info, Action<int>? progress = null, CancellationToken cancel = default)
    {
        return _mgr.DownloadUpdatesAsync(info, progress, cancel);
    }

    /// <summary>应用更新并重启。Velopack 的流程是“Update.exe 等本进程退出 → 应用更新 → 重启”。</summary>
    public void ApplyAndRestart(UpdateInfo info)
    {
        _mgr.ApplyUpdatesAndRestart(info);
    }
}
