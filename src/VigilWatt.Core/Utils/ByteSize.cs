namespace VigilWatt.Core.Utils;

/// <summary>
/// 功 能： 字节数 → 人类可读文本（历史管理页显示库文件大小、删除后释放空间用）。
/// 2026/10/06 16:58:42 xliu
/// 纯函数，可脱离 WinRT 单测。
/// </summary>
public static class ByteSize
{
    private const long KB = 1024;
    private const long MB = KB * 1024;
    private const long GB = MB * 1024;

    /// <summary>负数（不该出现，仅防御）按 0 处理。进位用 1024，不显示小数位以下的无意义精度。</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:0.##} GB",
            >= MB => $"{bytes / (double)MB:0.##} MB",
            >= KB => $"{bytes / (double)KB:0.#} KB",
            _ => $"{bytes} B",
        };
    }
}
