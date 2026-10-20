using VigilWatt.Core.Ble;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： ClassicDeviceName —— 经典蓝牙显示名的"占位名"识别。
/// 2026/10/06 15:53:36 xliu
/// 用例取自真机：EDIFIER W820NB 的 AEP 名字实际是设备 ID，列表里就显示成一长串 ID，这正是要挡掉的。
/// </summary>
public class ClassicDeviceNameTests
{
    /// <summary>真机实测的 AEP 设备 ID（WinRT 在名字没解析出来时就返回它当名字）。</summary>
    private const string RealAepId = "Bluetooth#Bluetooth1c:ce:51:23:28:7b-0c:ae:bd:c6:cd:a7";

    [Fact]
    public void N1_AEP设备ID算占位名()
    {
        Assert.True(ClassicDeviceName.IsPlaceholder(RealAepId));
        Assert.True(ClassicDeviceName.IsPlaceholder("btc:" + RealAepId));
    }

    [Fact]
    public void N2_空值算占位名()
    {
        Assert.True(ClassicDeviceName.IsPlaceholder(null));
        Assert.True(ClassicDeviceName.IsPlaceholder(""));
        Assert.True(ClassicDeviceName.IsPlaceholder("   "));
    }

    [Fact]
    public void N3_设备接口路径算占位名()
    {
        Assert.True(ClassicDeviceName.IsPlaceholder(
            @"\\?\BTHENUM#{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&005d#7&1d3d8b&0&0caebdc6cda7_C00000000"));
        Assert.True(ClassicDeviceName.IsPlaceholder("BTHENUM#7&1d3d8b&0&0caebdc6cda7"));
    }

    [Fact]
    public void N4_裸MAC算占位名()
    {
        Assert.True(ClassicDeviceName.IsPlaceholder("0c:ae:bd:c6:cd:a7"));
        Assert.True(ClassicDeviceName.IsPlaceholder("0C-AE-BD-C6-CD-A7"));
    }

    [Fact]
    public void N5_真实设备名不算占位名()
    {
        Assert.False(ClassicDeviceName.IsPlaceholder("EDIFIER W820NB 双金标版"));
        Assert.False(ClassicDeviceName.IsPlaceholder("WH-1000XM5"));
        Assert.False(ClassicDeviceName.IsPlaceholder("Beats Studio Pro"));
    }

    [Fact]
    public void N6_MAC文本统一成大写冒号()
    {
        Assert.Equal("0C:AE:BD:C6:CD:A7", ClassicDeviceName.MacText("0c:ae:bd:c6:cd:a7"));
        Assert.Equal("0C:AE:BD:C6:CD:A7", ClassicDeviceName.MacText("0C-AE-BD-C6-CD-A7"));
    }

    [Fact]
    public void N7_兜底显示名()
    {
        Assert.Equal("经典蓝牙设备 0C:AE:BD:C6:CD:A7", ClassicDeviceName.FallbackName("0c:ae:bd:c6:cd:a7"));
        Assert.Equal("经典蓝牙设备", ClassicDeviceName.FallbackName(null));
        Assert.Equal("经典蓝牙设备", ClassicDeviceName.FallbackName(""));
    }
}
