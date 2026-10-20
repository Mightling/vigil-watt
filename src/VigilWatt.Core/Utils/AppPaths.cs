namespace VigilWatt.Core.Utils;

/// <summary>
/// 功 能： 统一管理应用数据根目录（%LOCALAPPDATA%\VigilWatt）。
/// 2026/10/06 15:53:36 xliu
/// </summary>
public static class AppPaths
{
    public const string AppName = "VigilWatt";

    public static readonly string RootDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
}
