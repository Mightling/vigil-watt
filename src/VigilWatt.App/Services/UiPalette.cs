using Microsoft.UI.Xaml.Media;
using VigilWatt.Core.Models;
using Windows.UI;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 全局统一状态色板：设备列表、历史图表、桌面小组件、托盘图标共用同一组状态色值。
/// 2026/10/06 15:53:36 xliu
/// 状态色为明暗主题通用的固定值；中性色（文字/卡片底/分隔线）一律用 ThemeResource，不在这里定义。
/// </summary>
public static class UiPalette
{
    public static readonly Color Charging = FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Low = FromRgb(0xE2, 0x44, 0x5C);
    public static readonly Color Mid = FromRgb(0xFA, 0xAA, 0x17);
    public static readonly Color Good = FromRgb(0x34, 0xC7, 0x59);
    public static readonly Color Offline = FromRgb(0x8E, 0x8E, 0x93);

    // ---- 来源徽标色（设备列表 + 桌面小组件共用）----
    // 三种来源用三个互不相近的色相，一眼区分：蓝牙=蓝（蓝牙的通用色）/ 2.4G=紫 / 本机=青。
    // 与电量状态色刻意错开（状态色是红/琥珀/绿/蓝/灰，这里避开红绿琥珀，免得"是来源还是电量"分不清）。
    public static readonly Color SourceBluetooth = FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Source24G = FromRgb(0xAF, 0x52, 0xDE);
    public static readonly Color SourceSystem = FromRgb(0x30, 0xB0, 0xC7);

    /// <summary>来源 → 徽标主色（文字色；胶囊底色用 <see cref="Tint"/> 压淡）。</summary>
    public static Color SourceTag(DeviceSource source) => source switch
    {
        DeviceSource.Ble or DeviceSource.BluetoothClassic => SourceBluetooth,
        DeviceSource.System => SourceSystem,
        _ => Source24G,
    };

    /// <summary>同色半透明底（徽标胶囊背景）。默认约 16% 不透明度——明暗主题下都够淡，不抢电量条。</summary>
    public static Color Tint(Color color, byte alpha = 0x28) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>多系列图表循环取色（首位为主系列色）。</summary>
    public static readonly Color[] SeriesColors =
    [
        Charging,
        Good,
        Mid,
        FromRgb(0xAF, 0x52, 0xDE),
        FromRgb(0x5E, 0xCE, 0xF1),
        FromRgb(0xFF, 0x9F, 0x0A),
    ];

    /// <summary>电量状态 → 状态色。离线（IsOnline=false）由调用方直接用 Offline。</summary>
    public static Color BatteryLevel(int? percent, bool charging)
    {
        if (charging) return Charging;
        return percent switch
        {
            null => Offline,
            <= 20 => Low,
            <= 50 => Mid,
            _ => Good,
        };
    }

    public static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>托盘图标走 GDI+，需要 System.Drawing 的颜色类型。</summary>
    public static System.Drawing.Color ToDrawing(Color color) =>
        System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

    private static Color FromRgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
