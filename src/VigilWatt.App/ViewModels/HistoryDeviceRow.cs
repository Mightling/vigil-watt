using VigilWatt.Core.Storage;

namespace VigilWatt.App.ViewModels;

/// <summary>
/// 功 能： 历史管理页的设备行（一次性绑定，每次刷新重建）。
/// 2026/10/06 16:58:42 xliu
/// 勾选状态由 CheckBox 的 Checked/Unchecked 事件写回，**不做双向绑定**——事件回调与绑定更新的先后顺序没有保证， 读 CheckBox 自身的 IsChecked 才确定。
/// </summary>
public sealed class HistoryDeviceRow
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required int Count { get; init; }
    public required DateTimeOffset First { get; init; }
    public required DateTimeOffset Last { get; init; }

    /// <summary>勾选状态：仅由页面的事件处理器写入。</summary>
    public bool IsSelected { get; set; }

    public string CountText => $"{Count:N0} 条";

    public string RangeText => $"{First.LocalDateTime:MM-dd HH:mm} ~ {Last.LocalDateTime:MM-dd HH:mm}";

    public static HistoryDeviceRow From(DeviceHistoryStat stat) => new()
    {
        DeviceId = stat.DeviceId,
        Name = stat.Name,
        Count = stat.Count,
        First = stat.First,
        Last = stat.Last,
    };
}
