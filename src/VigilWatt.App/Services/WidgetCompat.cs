namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 窗口外观的 OS 能力判定（纯逻辑，便于单测与集中管理"哪个系统走哪条路"）。
/// 2026/10/08 17:10:27 xliu
/// 具体坑与实修记录见 docs/dev/runtime-pitfalls.md §14 / §17。
/// </summary>
public static class WidgetCompat
{
    /// <summary>Windows 11 首个正式版的 build 号。达到或超过即视为 Win11 及以上。</summary>
    public const int Windows11Build = 22000;

    /// <summary>
    /// 该 build 号是否需要走"透明窗口 + XAML 自绘圆角"的兼容路径。
    /// 只有 Win10 需要；Win11 及以上交给 DWM 原生处理，保持原有观感。
    /// </summary>
    public static bool NeedsTransparentChrome(int build) => build < Windows11Build;

    /// <summary>是否支持 DWM 原生窗口圆角（Win11+）。</summary>
    public static bool SupportsDwmRoundedCorner(int build) => build >= Windows11Build;
}
