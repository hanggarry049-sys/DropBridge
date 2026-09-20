using System;
using System.IO;
using System.Text.Json;

namespace DropBridge.Models;

/// <summary>
/// 应用设置，持久化到 %LOCALAPPDATA%\DropBridge\settings.json。
/// 读失败一律退回默认值，不阻断启动。
/// </summary>
public class AppSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropBridge");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>停靠侧：Left / Right</summary>
    public string DockSide { get; set; } = "Left";

    /// <summary>下沉速度（像素/小时）。0 = 永不老化</summary>
    public double SinkSpeedPerHour { get; set; } = 12.0;

    /// <summary>顶部免老化区占画布高度的比例（0 ~ 0.5）</summary>
    public double NoAgeRatio { get; set; } = 1.0 / 3.0;

    /// <summary>侧边区域宽度占屏宽的比例（0.05 ~ 0.40）</summary>
    public double DockWidthRatio { get; set; } = 0.10;

    /// <summary>图标尺寸（像素）：48 小 / 64 中 / 80 大</summary>
    public int IconSize { get; set; } = 64;

    /// <summary>开机自启</summary>
    public bool AutoStart { get; set; }

    /// <summary>暂停下沉：全部图标不再老化（拖进黑洞也不会被吸走）</summary>
    public bool PauseAging { get; set; }

    /// <summary>
    /// 离线时段（关机 / 睡眠 / 进程没在运行）的下沉折算系数：
    /// 0 = 完全不计入（只累计电脑运行时的时长），1 = 照常计入。
    /// 只作用于单次推进中超过一个正常 tick 间隔的那部分时长。
    /// </summary>
    public double OfflineSinkFactor { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            string json = File.ReadAllText(FilePath);
            var s = JsonSerializer.Deserialize<AppSettings>(json);
            if (s != null) { Normalize(s); return s; }
        }
        catch
        {
            // 读失败退回默认
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Normalize(this);
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    /// <summary>把越界值拉回合法区间，避免手工改坏 settings.json 后界面炸掉</summary>
    private static void Normalize(AppSettings s)
    {
        if (s.DockSide != "Right") s.DockSide = "Left";          // 目前只支持左右
        s.SinkSpeedPerHour = Math.Clamp(s.SinkSpeedPerHour, 0, 200);
        s.NoAgeRatio = Math.Clamp(s.NoAgeRatio, 0, 0.5);
        s.DockWidthRatio = Math.Clamp(s.DockWidthRatio, 0.05, 0.40);
        if (s.IconSize != 48 && s.IconSize != 80) s.IconSize = 64;
    }
}
