using VigilWatt.Core.Utils;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： ByteSize —— 历史管理页显示库文件大小 / 释放空间用的格式化（1024 进位，最多两位小数）。
/// 2026/10/06 16:58:42 xliu
/// </summary>
public class ByteSizeTests
{
    [Fact]
    public void B1_不足1KB按字节显示()
    {
        Assert.Equal("0 B", ByteSize.Format(0));
        Assert.Equal("512 B", ByteSize.Format(512));
        Assert.Equal("1023 B", ByteSize.Format(1023));
    }

    [Fact]
    public void B2_进位到KB()
    {
        Assert.Equal("1 KB", ByteSize.Format(1024));
        Assert.Equal("1.5 KB", ByteSize.Format(1536));
    }

    [Fact]
    public void B3_进位到MB()
    {
        Assert.Equal("1 MB", ByteSize.Format(1024 * 1024));
        Assert.Equal("1.24 MB", ByteSize.Format(1300234));
    }

    [Fact]
    public void B4_进位到GB()
    {
        Assert.Equal("1 GB", ByteSize.Format(1024L * 1024 * 1024));
        Assert.Equal("2.5 GB", ByteSize.Format((long)(2.5 * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void B5_负数按零处理()
    {
        Assert.Equal("0 B", ByteSize.Format(-5));
    }
}
