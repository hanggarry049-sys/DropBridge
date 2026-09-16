using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DropBridge.Models;

namespace DropBridge.Services;

/// <summary>
/// 条目的持久化与校验。数据落在 %LOCALAPPDATA%\DropBridge\。
/// </summary>
public class ItemStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropBridge");

    /// <summary>数据目录（%LOCALAPPDATA%\DropBridge），托盘「打开数据目录」用</summary>
    public static string DataDir => Dir;

    private static readonly string FilePath = Path.Combine(Dir, "items.json");

    public List<BridgeItem> Items { get; } = new();

    public static ItemStore Load()
    {
        var store = new ItemStore();
        try
        {
            if (!File.Exists(FilePath)) return store;
            string json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<List<BridgeItem>>(json);
            if (loaded != null)
            {
                // 迁移：旧版本条目没有 SinkAt（或为 default），按"从现在开始下沉"补齐，避免瞬间被吸入
                var now = DateTime.Now;
                foreach (var it in loaded)
                    if (it.SinkAt == default) it.SinkAt = now;
                store.Items.AddRange(loaded);
            }
        }
        catch
        {
            // 读失败就当空库，不阻断启动
        }
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var opt = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, opt));
        }
        catch
        {
            // 写失败不阻断运行
        }
    }

    /// <summary>已存在同路径则返回 false（比较时忽略大小写与结尾分隔符）</summary>
    public bool TryAdd(string path, out BridgeItem item) => TryAdd(path, -1, -1, out item);

    /// <summary>带初始位置的添加：x/y 传 -1 表示不预设、交给 EnsurePosition 找空位。</summary>
    public bool TryAdd(string path, double x, double y, out BridgeItem item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Directory.Exists(path)) return false;

        string norm = Normalize(path);
        if (Items.Any(i => Normalize(i.Path) == norm)) return false;

        item = new BridgeItem
        {
            Path = path,
            DisplayName = System.IO.Path.GetFileName(path.TrimEnd('\\')),
            AddedAt = DateTime.Now,
            SinkAt = DateTime.Now,
            X = x,
            Y = y
        };
        Items.Add(item);
        return true;
    }

    public void Remove(BridgeItem item)
    {
        Items.Remove(item);
    }

    /// <summary>刷新每个条目的存在性标记</summary>
    public void RefreshExistence()
    {
        foreach (var it in Items)
            it.Missing = !Directory.Exists(it.Path);
    }

    public static string Normalize(string p)
    {
        if (string.IsNullOrEmpty(p)) return string.Empty;
        return p.TrimEnd('\\', '/').ToLowerInvariant();
    }
}
