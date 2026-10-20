using VigilWatt.Core.Plugins;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： PluginUtils.StableHash + DeviceClaimRegistry —— 都是零依赖纯逻辑，之前没有覆盖（全检报告行动项 5）。
/// 2026/10/10 17:44:33 xliu
/// StableHash 的稳定性是设备 Id 的前提：重启/重扫后同一条路径必须映射到同一个 Id， 否则历史记录会认不出设备（每条记录都变成新设备）。
/// </summary>
public class PluginUtilsTests
{
    [Fact]
    public void P1_同一输入永远得到同一结果()
    {
        const string path = @"\\?\hid#vid_24ae&pid_1412#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal(PluginUtils.StableHash(path), PluginUtils.StableHash(path));
    }

    [Fact]
    public void P2_不同输入得到不同结果()
    {
        Assert.NotEqual(PluginUtils.StableHash("hid#a"), PluginUtils.StableHash("hid#b"));
    }

    [Fact]
    public void P3_结果固定为16位十六进制_不受平台默认格式影响()
    {
        // 用 "X16" 而不是 "x" 或依赖文化：Id 会进 history.db，格式漂移等于历史对不上
        var hash = PluginUtils.StableHash("x");
        Assert.Equal(16, hash.Length);
        Assert.Matches("^[0-9A-F]{16}$", hash);
    }

    [Fact]
    public void P4_空串也有确定结果_不抛异常()
    {
        var hash = PluginUtils.StableHash("");
        Assert.Equal(16, hash.Length);
        Assert.Equal(hash, PluginUtils.StableHash(""));
    }

    [Fact]
    public void P5_大小写敏感_路径大小写不同视为不同设备()
    {
        // 设备路径由系统给出，大小写固定；这里只锁"实现不会自作主张归一化"
        Assert.NotEqual(PluginUtils.StableHash("HID#A"), PluginUtils.StableHash("hid#a"));
    }
}

/// <summary>接口认领注册表：同一接口同一时刻只能归一个插件（抢句柄/抢会话的防线）。</summary>
public class DeviceClaimRegistryTests
{
    private const string PathA = @"\\?\hid#a";
    private const string PathB = @"\\?\hid#b";

    [Fact]
    public void R1_先到先得_后到的插件被拒()
    {
        var reg = new DeviceClaimRegistry();
        Assert.True(reg.TryClaim(PathA, "Rapoo"));
        Assert.False(reg.TryClaim(PathA, "Mchose"));
        Assert.True(reg.IsClaimedBy(PathA, "Rapoo"));
        Assert.False(reg.IsClaimedBy(PathA, "Mchose"));
    }

    [Fact]
    public void R2_同一插件重复认领是幂等的()
    {
        var reg = new DeviceClaimRegistry();
        Assert.True(reg.TryClaim(PathA, "Rapoo"));
        Assert.True(reg.TryClaim(PathA, "Rapoo"));
    }

    [Fact]
    public void R3_不同接口互不影响()
    {
        var reg = new DeviceClaimRegistry();
        Assert.True(reg.TryClaim(PathA, "Rapoo"));
        Assert.True(reg.TryClaim(PathB, "Mchose"));
    }

    [Fact]
    public void R4_非持有者释放无效_防止误放别人的接口()
    {
        var reg = new DeviceClaimRegistry();
        reg.TryClaim(PathA, "Rapoo");

        reg.Release(PathA, "Mchose");   // 不是持有者，应被忽略
        Assert.True(reg.IsClaimedBy(PathA, "Rapoo"));
    }

    [Fact]
    public void R5_持有者释放后可被他人认领()
    {
        var reg = new DeviceClaimRegistry();
        reg.TryClaim(PathA, "Rapoo");
        reg.Release(PathA, "Rapoo");

        Assert.False(reg.IsClaimedBy(PathA, "Rapoo"));
        Assert.True(reg.TryClaim(PathA, "Mchose"));
    }

    [Fact]
    public void R6_ReleaseAll只放自己的()
    {
        var reg = new DeviceClaimRegistry();
        reg.TryClaim(PathA, "Rapoo");
        reg.TryClaim(PathB, "Mchose");

        reg.ReleaseAll("Rapoo");
        Assert.False(reg.IsClaimedBy(PathA, "Rapoo"));
        Assert.True(reg.IsClaimedBy(PathB, "Mchose"));
    }
}
