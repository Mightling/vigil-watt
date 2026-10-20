namespace VigilWatt.App.Services;

/// <summary>屏幕坐标下的像素矩形（左上角 + 宽高）。任务栏定位全程物理像素，包一个类型免得四处传四个 int。</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>
/// 功 能： 任务栏贴片的定位数学：零 WinRT、零 P/Invoke 的纯函数，直接链进测试项目（R8）。
/// 2026/10/10 21:34:00 xliu
/// 窗口类只负责"把值喂进来、把结果写进 AppWindow"——算错位置这种事必须能在单测里复现。
/// </summary>
public static class TaskbarLayout
{
    /// <summary>贴片与任务栏两端的最小间距（物理像素）。</summary>
    public const int EdgeInsetPx = 4;

    /// <summary>任务栏收起后的判定厚度：自动隐藏后只剩约 2px 的一条边，贴片必须跟着一起藏。</summary>
    public const int HiddenThicknessPx = 4;

    /// <summary>任务栏当前是否不可见（自动隐藏已收起）。</summary>
    public static bool IsTaskbarHidden(PixelRect taskbar) =>
        Math.Min(taskbar.Width, taskbar.Height) <= HiddenThicknessPx;

    /// <summary>横向任务栏（贴片左右排）还是竖向（上下排）。</summary>
    public static bool IsHorizontal(PixelRect taskbar) => taskbar.Width >= taskbar.Height;

    /// <summary>贴片高度（物理像素）：按任务栏厚度减上下留白，再夹在 [minDip, maxDip] 内。</summary>
    public static int PillHeight(PixelRect taskbar, double scale, double marginDip, double minDip, double maxDip)
    {
        double thicknessDip = (IsHorizontal(taskbar) ? taskbar.Height : taskbar.Width) / scale;
        return (int)Math.Ceiling(Math.Clamp(thicknessDip - marginDip * 2, minDip, maxDip) * scale);
    }

    /// <summary>
    /// 贴片左上角（物理像素）。默认位 = 紧贴托盘左边（竖向任务栏则是托盘上边），再叠加用户拖动出来的
    /// <paramref name="offset"/>；结果夹在任务栏内，并把"实际落点"反推回 <paramref name="appliedOffset"/>——
    /// 这样拖到头再往回拖不会有一段"空走"。
    /// </summary>
    public static (int X, int Y) Place(
        PixelRect taskbar, PixelRect tray, int pillW, int pillH, int offset, int gapPx, out int appliedOffset)
    {
        if (IsHorizontal(taskbar))
        {
            int anchor = (tray.Width > 0 ? tray.Left : taskbar.Right) - pillW - gapPx;
            int min = taskbar.Left + EdgeInsetPx;
            int max = Math.Max(min, taskbar.Right - pillW - EdgeInsetPx);
            int x = Math.Clamp(anchor + offset, min, max);
            appliedOffset = x - anchor;
            return (x, taskbar.Top + (taskbar.Height - pillH) / 2);
        }

        int anchorY = (tray.Height > 0 ? tray.Top : taskbar.Bottom) - pillH - gapPx;
        int minY = taskbar.Top + EdgeInsetPx;
        int maxY = Math.Max(minY, taskbar.Bottom - pillH - EdgeInsetPx);
        int y = Math.Clamp(anchorY + offset, minY, maxY);
        appliedOffset = y - anchorY;

        int centeredX = taskbar.Left + (taskbar.Width - pillW) / 2;
        if (pillW > taskbar.Width)
        {
            // 竖向任务栏只有 40~50px 宽、装不下横向胶囊：居中会让它一半跑到屏幕外，
            // 改挂在"桌面那一侧"——左停靠往右伸、右停靠往左伸
            centeredX = taskbar.Left <= 0 ? taskbar.Left : taskbar.Right - pillW;
        }
        return (centeredX, y);
    }

    /// <summary>把记下偏移时的物理像素换算到当前 DPI；Dpi=0 或未变则原样返回（勿逐帧累乘，见 §16.5）。</summary>
    public static int ScaleOffset(int offsetPx, uint storedDpi, uint currentDpi)
    {
        if (storedDpi == 0 || currentDpi == 0 || storedDpi == currentDpi) return offsetPx;
        return (int)Math.Round(offsetPx * (double)currentDpi / storedDpi);
    }

    /// <summary>客户区 X 落在哪一格；分隔缝返回 -1。</summary>
    public static int HitTest(IReadOnlyList<(int X, int Width)> regions, int clientX)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            if (clientX >= r.X && clientX < r.X + r.Width) return i;
        }
        return -1;
    }

    /// <summary>
    /// 提示框左上角（屏幕坐标）：摆在任务栏朝桌面一侧，水平/竖直对准格子中心，再夹进工作区。
    /// </summary>
    public static (int X, int Y) PlaceTooltip(
        PixelRect taskbar, PixelRect chipScreen, int tipW, int tipH, int gapPx, PixelRect workArea)
    {
        int x, y;
        if (IsHorizontal(taskbar))
        {
            x = chipScreen.Left + (chipScreen.Width - tipW) / 2;
            // Top==0 视为顶栏，否则底栏（主屏底栏 Top 很大；副屏同理）
            y = taskbar.Top <= 0
                ? chipScreen.Bottom + gapPx
                : chipScreen.Top - tipH - gapPx;
        }
        else
        {
            y = chipScreen.Top + (chipScreen.Height - tipH) / 2;
            // Left==0 左停靠 → 提示往右伸到桌面；否则右停靠往左
            x = taskbar.Left <= 0
                ? chipScreen.Right + gapPx
                : chipScreen.Left - tipW - gapPx;
        }

        if (workArea.Width > 0 && workArea.Height > 0)
        {
            x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - tipW));
            y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - tipH));
        }
        return (x, y);
    }
}
