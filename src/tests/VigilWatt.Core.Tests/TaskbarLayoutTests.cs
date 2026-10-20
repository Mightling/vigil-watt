using VigilWatt.App.Services;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 任务栏贴片的定位数学（T1–T9）。
/// 2026/10/10 21:34:00 xliu
/// 这块算错的表现是"贴片跑到屏幕外 / 盖住时钟 / 拖动空走一截"， 都是纯几何，必须能在单测里复现。
/// </summary>
public class TaskbarLayoutTests
{
    // 底部任务栏：整条 1920 宽、48 厚；通知区域（托盘）占最右 220
    private static readonly PixelRect BottomTaskbar = new(0, 1032, 1920, 48);
    private static readonly PixelRect BottomTray = new(1700, 1032, 220, 48);

    // T1：自动隐藏收起时任务栏只剩约 2px 厚的一条边 —— 必须判成"不可见"
    [Theory]
    [InlineData(1920, 48, false)]
    [InlineData(1920, 2, true)]
    [InlineData(2, 1080, true)]
    public void IsTaskbarHidden_ByThickness(int width, int height, bool hidden)
        => Assert.Equal(hidden, TaskbarLayout.IsTaskbarHidden(new PixelRect(0, 0, width, height)));

    // T2：横竖判定按"长边"来（宽 ≥ 高即横向）
    [Fact]
    public void IsHorizontal_ByLongerSide()
    {
        Assert.True(TaskbarLayout.IsHorizontal(BottomTaskbar));
        Assert.False(TaskbarLayout.IsHorizontal(new PixelRect(0, 0, 48, 1080)));
    }

    // T3：贴片高度 = 任务栏厚度 - 上下留白，并夹在 [min, max] 内（返回值是物理像素）
    [Fact]
    public void PillHeight_SubtractsMarginAndClamps()
    {
        // 100% 缩放：48 - 6*2 = 36
        Assert.Equal(36, TaskbarLayout.PillHeight(BottomTaskbar, 1.0, 6, 22, 40));
        // 150% 缩放：任务栏 72 物理像素 → 48 DIP → 36 DIP → 54 物理像素
        Assert.Equal(54, TaskbarLayout.PillHeight(new PixelRect(0, 1008, 1920, 72), 1.5, 6, 22, 40));
        // 很薄的任务栏（Win10 40px）：40 - 12 = 28，仍在范围内
        Assert.Equal(28, TaskbarLayout.PillHeight(new PixelRect(0, 1040, 1920, 40), 1.0, 6, 22, 40));
        // 极薄：30 - 12 = 18 → 夹到下限 22
        Assert.Equal(22, TaskbarLayout.PillHeight(new PixelRect(0, 1050, 1920, 30), 1.0, 6, 22, 40));
        // 极厚：120 - 12 = 108 → 夹到上限 40
        Assert.Equal(40, TaskbarLayout.PillHeight(new PixelRect(0, 960, 1920, 120), 1.0, 6, 22, 40));
    }

    // T4：默认位 = 紧贴托盘左边，垂直居中；未拖动时 offset 反推回 0
    [Fact]
    public void Place_DefaultsToLeftOfTray()
    {
        var (x, y) = TaskbarLayout.Place(BottomTaskbar, BottomTray, 200, 36, 0, 6, out int applied);

        Assert.Equal(1700 - 200 - 6, x);          // 1494
        Assert.Equal(1032 + (48 - 36) / 2, y);    // 1038
        Assert.Equal(0, applied);
    }

    // T5：托盘找不到时退回任务栏右缘，不能因此不显示
    [Fact]
    public void Place_FallsBackToTaskbarRightEdge()
    {
        var (x, _) = TaskbarLayout.Place(BottomTaskbar, default, 200, 36, 0, 6, out _);
        Assert.Equal(1920 - 200 - 6, x);
    }

    // T6：向右拖到头会被夹住，并把"实际落点"反推回 offset —— 这样往回拖不会先空走一截
    [Fact]
    public void Place_ClampsAtRightAndBackDerivesOffset()
    {
        var (x, _) = TaskbarLayout.Place(BottomTaskbar, BottomTray, 200, 36, 5000, 6, out int applied);

        Assert.Equal(1920 - 200 - TaskbarLayout.EdgeInsetPx, x);
        Assert.Equal(x - (1700 - 200 - 6), applied);
    }

    // T7：向左拖到头同理
    [Fact]
    public void Place_ClampsAtLeftAndBackDerivesOffset()
    {
        var (x, _) = TaskbarLayout.Place(BottomTaskbar, BottomTray, 200, 36, -5000, 6, out int applied);

        Assert.Equal(TaskbarLayout.EdgeInsetPx, x);
        Assert.Equal(x - (1700 - 200 - 6), applied);
    }

    // T8：竖向（左停靠）任务栏：贴片挂在托盘上边，胶囊比任务栏宽时往桌面一侧伸、不出屏
    [Fact]
    public void Place_VerticalTaskbar_AnchorsAboveTray()
    {
        var taskbar = new PixelRect(0, 0, 48, 1080);
        var tray = new PixelRect(0, 860, 48, 220);

        var (x, y) = TaskbarLayout.Place(taskbar, tray, 200, 36, 0, 6, out _);

        Assert.Equal(0, x);                     // 左停靠 → 从任务栏左缘往右伸，不会跑到屏幕外
        Assert.Equal(860 - 36 - 6, y);
    }

    // T9：竖向（右停靠）任务栏：胶囊往左伸，右缘对齐任务栏右缘
    [Fact]
    public void Place_VerticalRightDockedTaskbar_HangsLeftward()
    {
        var taskbar = new PixelRect(1872, 0, 48, 1080);

        var (x, _) = TaskbarLayout.Place(taskbar, default, 200, 36, 0, 6, out _);

        Assert.Equal(1872 + 48 - 200, x);
    }

    // T13：DPI 往返必须从同一对规范值换算，不能漂（2K 150% ↔ 1080p 100%）
    [Fact]
    public void ScaleOffset_RoundTripPreservesStoredPixels()
    {
        const int stored = -420;
        const uint dpi2k = 144;
        const uint dpi1080 = 96;

        int at1080 = TaskbarLayout.ScaleOffset(stored, dpi2k, dpi1080);
        int back = TaskbarLayout.ScaleOffset(stored, dpi2k, dpi2k);

        Assert.Equal(-280, at1080); // -420 * 96/144
        Assert.Equal(stored, back);
        Assert.Equal(stored, TaskbarLayout.ScaleOffset(at1080, dpi1080, dpi2k));
    }

    // T14：旧配置 Dpi=0 或 DPI 未变 → 原样返回，升级后不跳一下
    [Theory]
    [InlineData(100, 0u, 144u, 100)]
    [InlineData(100, 144u, 144u, 100)]
    [InlineData(100, 144u, 0u, 100)]
    public void ScaleOffset_LegacyOrUnchanged_Passthrough(int offset, uint stored, uint current, int expected)
        => Assert.Equal(expected, TaskbarLayout.ScaleOffset(offset, stored, current));

    // T15：命中落在格内；分隔缝（格与格之间）返回 -1
    [Fact]
    public void HitTest_ChipVersusGap()
    {
        (int X, int Width)[] regions = [(6, 40), (56, 40)];
        Assert.Equal(0, TaskbarLayout.HitTest(regions, 6));
        Assert.Equal(0, TaskbarLayout.HitTest(regions, 45));
        Assert.Equal(-1, TaskbarLayout.HitTest(regions, 50)); // 缝
        Assert.Equal(1, TaskbarLayout.HitTest(regions, 56));
        Assert.Equal(-1, TaskbarLayout.HitTest(regions, 100));
    }

    // T16：底栏提示在格子上方；顶栏在下方；靠边时夹进工作区
    [Fact]
    public void PlaceTooltip_OutsideTaskbarAndClamps()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        var chip = new PixelRect(1494, 1038, 40, 36);

        var (bx, by) = TaskbarLayout.PlaceTooltip(BottomTaskbar, chip, 120, 28, 4, work);
        Assert.Equal(1494 + (40 - 120) / 2, bx);
        Assert.Equal(1038 - 28 - 4, by);

        var topBar = new PixelRect(0, 0, 1920, 48);
        var topChip = new PixelRect(1494, 6, 40, 36);
        var (_, ty) = TaskbarLayout.PlaceTooltip(topBar, topChip, 120, 28, 4, work);
        Assert.Equal(6 + 36 + 4, ty);

        // 格子贴左缘：提示左缘夹到工作区左缘
        var edgeChip = new PixelRect(2, 1038, 40, 36);
        var (ex, _) = TaskbarLayout.PlaceTooltip(BottomTaskbar, edgeChip, 200, 28, 4, work);
        Assert.Equal(0, ex);
    }

    // T17：左停靠提示往右；右停靠往左
    [Fact]
    public void PlaceTooltip_VerticalTaskbar_TowardDesktop()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        var leftBar = new PixelRect(0, 0, 48, 1080);
        var leftChip = new PixelRect(0, 400, 200, 36);
        var (lx, _) = TaskbarLayout.PlaceTooltip(leftBar, leftChip, 120, 28, 4, work);
        Assert.Equal(200 + 4, lx);

        var rightBar = new PixelRect(1872, 0, 48, 1080);
        var rightChip = new PixelRect(1720, 400, 200, 36);
        var (rx, _) = TaskbarLayout.PlaceTooltip(rightBar, rightChip, 120, 28, 4, work);
        Assert.Equal(1720 - 120 - 4, rx);
    }
}

/// <summary>任务栏贴片的两个设置项收敛（T10–T12）：手改坏了的 settings.json 不能把界面带崩。</summary>
public class TaskbarBatteryOptionsTests
{
    // T10：显示内容只认两个合法值，其余（含 null / 手改的乱值）一律按 All
    [Theory]
    [InlineData(null, TaskbarBatteryMode.All)]
    [InlineData("", TaskbarBatteryMode.All)]
    [InlineData("Lowest", TaskbarBatteryMode.Lowest)]
    [InlineData("All", TaskbarBatteryMode.All)]
    [InlineData("lowest", TaskbarBatteryMode.All)]
    [InlineData("乱写的", TaskbarBatteryMode.All)]
    public void ModeNormalize_OnlyAcceptsKnownValues(string? input, string expected)
        => Assert.Equal(expected, TaskbarBatteryMode.Normalize(input));

    // T11：最多台数收敛到 1~5；0（老配置缺字段）按默认 3
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(99, 5)]
    [InlineData(-4, 1)]
    public void MaxDevicesClamp_IntoRange(int input, int expected)
        => Assert.Equal(expected, TaskbarBatteryOptions.Clamp(input));

    // T12：下拉框档位必须与 Clamp 的取值范围一致（防止两处常量漂移）
    [Fact]
    public void Choices_MatchClampRange()
    {
        Assert.Equal(TaskbarBatteryOptions.Max - TaskbarBatteryOptions.Min + 1, TaskbarBatteryOptions.Choices.Count);
        Assert.All(TaskbarBatteryOptions.Choices,
            n => Assert.Equal(n, TaskbarBatteryOptions.Clamp(n)));
    }
}
