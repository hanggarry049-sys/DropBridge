using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using DropBridge.Native;

namespace DropBridge.Services;

/// <summary>
/// 文件夹图标提取：优先 IShellItemImageFactory（可拿到带内容的缩略图），
/// 失败回退到系统通用文件夹图标。结果做内存 + 磁盘两级缓存。
///
/// 关键性能设计：IShellItemImageFactory 的 COM 调用要几十毫秒（首次还要生成缩略图），
/// 全部堆在 UI 线程上会让「启动加载 N 个条目」和「批量拖入」明显发涩。
/// 所以新增路径一律走异步接口，UI 先用极快的通用图标占位，真图标后台提取完再填回。
/// </summary>
public static class IconProvider
{
    private static readonly Dictionary<string, BitmapSource> Memory = new();

    // 异步提取会在线程池线程写 Memory，同步读在 UI 线程，必须加锁
    private static readonly object Sync = new();

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DropBridge", "icons");

    /// <summary>磁盘缓存保留天数：超过且期间没被命中过就清掉，避免 icons/ 无限膨胀</summary>
    private const int CacheRetentionDays = 30;

    private static BitmapSource _genericFolder;

    /// <summary>
    /// 系统通用文件夹图标。走 SHGetFileInfo，只查系统图标缓存、不生成缩略图，
    /// 开销 &lt;1ms，可放心在 UI 线程同步调用。用作高质量图标到达前的占位。
    /// </summary>
    public static BitmapSource GetGenericFolderIcon(int size = 64)
    {
        if (_genericFolder != null) return _genericFolder;

        try
        {
            var sfi = new Win32.SHFILEINFO();
            // pszPath 必须传非空字符串：实测传 null 时 SHGetFileInfo 直接返回 0 失败。
            // 配 SHGFI_USEFILEATTRIBUTES 后它只看 dwFileAttributes，
            // 所以这里传任意占位名即可拿到「通用文件夹」图标。
            IntPtr r = Win32.SHGetFileInfo(
                "folder", Win32.FILE_ATTRIBUTE_DIRECTORY, ref sfi,
                (uint)Marshal.SizeOf(typeof(Win32.SHFILEINFO)),
                Win32.SHGFI_ICON | Win32.SHGFI_USEFILEATTRIBUTES | Win32.SHGFI_LARGEICON);

            if (r != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    _genericFolder = bs;
                    return bs;
                }
                finally
                {
                    Win32.DestroyIcon(sfi.hIcon);
                }
            }
        }
        catch
        {
            // 拿不到就返回 null，调用方显示空底板，不影响功能
        }
        return null;
    }

    /// <summary>
    /// 异步取文件夹图标。缓存命中时同步回调（零延迟）；
    /// 否则在线程池做 COM 提取，完成后把回调投递回调用方线程（UI 线程）。
    /// </summary>
    public static void GetFolderIconAsync(string path, int size, Action<BitmapSource> onReady)
    {
        if (string.IsNullOrEmpty(path)) { onReady(null); return; }

        string key = CacheKey(path, size);

        lock (Sync)
            if (Memory.TryGetValue(key, out var hit)) { onReady(hit); return; }

        string cacheFile = CacheFileFor(key);
        if (File.Exists(cacheFile))
        {
            var fromDisk = LoadPng(cacheFile);
            if (fromDisk != null)
            {
                fromDisk.Freeze();
                lock (Sync) Memory[key] = fromDisk;
                Touch(cacheFile);
                onReady(fromDisk);
                return;
            }
        }

        // 慢路径：几十毫秒的 COM 提取丢到线程池，别堵 UI 线程
        // WPF UI 线程上 Current 是 DispatcherSynchronizationContext，Post 即投递回 dispatcher
        var uiContext = SynchronizationContext.Current;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var bmp = Extract(path, size);
            if (bmp != null)
            {
                bmp.Freeze();
                lock (Sync) Memory[key] = bmp;
                SavePng(bmp, cacheFile);
            }

            // BitmapSource 已 Freeze 可跨线程读，但给 Image.Source 赋值必须回 UI 线程
            if (uiContext != null) uiContext.Post(_ => onReady(bmp), null);
            else onReady(bmp);   // 无 UI 上下文（如单元测试）时直接调，由调用方保证线程安全
        });
    }

    /// <summary>清理长期未命中的图标缓存文件。启动时跑一次即可，成本可忽略。</summary>
    public static void PruneIconCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            var cutoff = DateTime.Now.AddDays(-CacheRetentionDays);
            foreach (string f in Directory.EnumerateFiles(CacheDir, "*.png"))
            {
                try
                {
                    // 用写时间判断：命中缓存时会 Touch 刷新它，
                    // 所以「最近还在用」的条目不会被误删
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                }
                catch
                {
                    // 单个文件删不掉（被占用等）不影响其它
                }
            }
        }
        catch
        {
        }
    }

    private static string CacheKey(string path, int size)
        => path.ToLowerInvariant() + "|" + size;

    private static string CacheFileFor(string key)
        => Path.Combine(CacheDir, Sha1Hex(key) + ".png");

    private static void Touch(string file)
    {
        try { File.SetLastWriteTime(file, DateTime.Now); } catch { }
    }

    private static BitmapSource Extract(string path, int size)
    {
        try
        {
            Win32.SHCreateItemFromParsingName(
                path, IntPtr.Zero, Win32.IID_IShellItemImageFactory,
                out Win32.IShellItemImageFactory factory);

            var sz = new Win32.SIZE { cx = size, cy = size };
            // SIIGBF_BIGGERSIZEOK = 1：允许返回更大的图再缩放，画质更好
            factory.GetImage(sz, 1, out IntPtr hbmp);
            if (hbmp == IntPtr.Zero) return null;

            try
            {
                var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            finally
            {
                Win32.DeleteObject(hbmp);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void SavePng(BitmapSource src, string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = new FileStream(file, FileMode.Create, FileAccess.Write);
            enc.Save(fs);
        }
        catch
        {
            // 缓存写失败无所谓
        }
    }

    private static BitmapSource LoadPng(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
            var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return dec.Frames.Count > 0 ? dec.Frames[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Sha1Hex(string s)
    {
        var bytes = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
