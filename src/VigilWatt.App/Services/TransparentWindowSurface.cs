using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using VigilWatt.Core.Utils;
using WinRT;
using SystemCompositor = Windows.UI.Composition.Compositor;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 清掉 WinUI3 窗口表面的**两层**背景，只留 XAML 画的前景：① 内容岛 Visual 的底色（设透明 SystemBackdrop）、 ② Win32 窗口类背景刷（`COLOR_WINDOW`，浅色主题=纯白）。
/// 2026/10/12 10:44:44 xliu
/// 贴片与小组件共用；通道实测与失败记录见 docs/dev/runtime-pitfalls.md §17。
/// </summary>
internal static class TransparentWindowSurface
{
    /// <summary>系统合成器（内容岛系统背板专用）。有线程亲和性，建一次复用（贴片可反复开关）。</summary>
    private static readonly Lazy<SystemCompositor> Compositor = new(() => new());

    /// <summary>必须静态压住：交给非托管代码的回调被 GC 回收后，窗口再来消息就是访问已释放内存。</summary>
    private static readonly Native.WndProcDele SurfaceProc = SurfaceWndProc;

    /// <summary>旧窗口过程按 HWND 存：贴片与小组件可同时存在，单份字段会让后装的窗口调错前一个的过程。</summary>
    private static readonly ConcurrentDictionary<IntPtr, IntPtr> PrevProcs = new();

    /// <summary>可重复调用。**窗口真正接上内容岛后要再调一次**（构造期那一次不保证生效）。</summary>
    public static void Apply(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero) return;

        // 清残留边框样式（否则非客户区把客户区挤窄，最右一格被裁），再清两层背景
        Native.ApplyBorderlessChrome(hwnd);
        ClearWin32Background(hwnd);
        ClearContentIslandBackground(window);
    }

    /// <summary>内容岛那层：不设透明背板时 Win10 会回退成"纯色主题底"（浅色=纯白），即白框本体。</summary>
    public static void ClearContentIslandBackground(Window window)
    {
        try
        {
            // 画刷必须出自**系统合成器**：系统背板由系统合成器画在内容岛后面，
            // 拿 WinUI 自己的 Window.Compositor 造的画刷类型对不上（编译期 CS0029）
            var brush = Compositor.Value.CreateColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            window.As<ICompositionSupportsSystemBackdrop>().SystemBackdrop = brush;
        }
        catch (Exception ex)
        {
            Log.Warn($"窗口透明背板设置失败：{ex.Message}");
        }
    }

    /// <summary>可重复调用。**按"当前窗口过程是不是已经是自己"判幂等**：重复装会把过程再包一层，消息一进来就无限递归。</summary>
    public static void ClearWin32Background(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var ours = Marshal.GetFunctionPointerForDelegate(SurfaceProc);
        if (Native.GetWindowLongPtr(hwnd, Native.GWLP_WNDPROC) == ours) return;

        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        PrevProcs[hwnd] = Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC, ours);
    }

    /// <summary>接管擦背景与重画：默认处理会用类背景刷（COLOR_WINDOW）把客户区铺满。</summary>
    private static IntPtr SurfaceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_ERASEBKGND) return 1;

        if (msg == Native.WM_PAINT)
        {
            var hdc = Native.BeginPaint(hwnd, out var ps);
            if (hdc != IntPtr.Zero)
            {
                Native.GetClientRect(hwnd, out var rc);
                Native.FillRect(hdc, ref rc, Native.GetStockObject(Native.BLACK_BRUSH));
                Native.EndPaint(hwnd, ref ps);
            }
            return 0;
        }

        if (!PrevProcs.TryGetValue(hwnd, out var prev))
            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);

        // 窗口销毁顺手摘记录，别让表随反复开关窗口增长
        if (msg == Native.WM_NCDESTROY) PrevProcs.TryRemove(hwnd, out _);

        return Native.CallWindowProcW(prev, hwnd, msg, wParam, lParam);
    }
}
