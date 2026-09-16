using System;
using System.Text.Json.Serialization;

namespace DropBridge.Models;

/// <summary>
/// 一个悬浮桥接条目（指向磁盘上的某个文件夹）。
/// </summary>
public class BridgeItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>目标文件夹的完整路径</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>显示名（文件夹名，非完整路径）</summary>
    public string DisplayName { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 下沉基准时刻：图标当前位置（X/Y）所对应的时间点。
    /// 此后图标按恒定速度（SinkSpeed）向底部"黑洞"（删除区）匀速下沉；
    /// 位置即老化程度，拖动改变位置即改变寿命，无需任何重置判定。
    /// </summary>
    public DateTime SinkAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 在左侧画布中的自由坐标（相对画布左上角，像素）。
    /// -1 表示尚未摆放，由界面自动找个空位。
    /// </summary>
    public double X { get; set; } = -1;

    public double Y { get; set; } = -1;

    // ---- 以下为运行时状态，不持久化 ----

    /// <summary>目标路径已不存在（被移动或删除且未追踪到新位置）</summary>
    [JsonIgnore]
    public bool Missing { get; set; }
}
