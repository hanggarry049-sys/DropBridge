using System;
using System.IO;
using System.Runtime.InteropServices;
using DropBridge.Models;
using DropBridge.Native;

namespace DropBridge.Services;

/// <summary>
/// 监听 Shell 文件系统变更（移动 / 重命名 / 删除），自动同步条目路径。
///
/// 原理：同盘移动在 Shell 层面是一次 rename，会广播 SHCNE_RENAMEFOLDER 并带上新旧 PIDL。
/// 我们无需猜测或搜索，比对旧路径命中则改写为新路径；删除事件命中则标记失效。
/// 跨盘复制产生的是 SHCNE_CREATE，源不变，无需处理。
///
/// 关键约束：消息里的 PIDL 仅在消息处理期间有效，绝不能缓存，绝不能手动释放。
/// </summary>
public sealed class ShellNotifyWatcher : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly ItemStore _store;
    private uint _regId;

    /// <summary>条目发生变更（路径改写或被标记失效）时触发，订阅方应重渲染。</summary>
    public event Action ItemsChanged;

    public ShellNotifyWatcher(IntPtr hwnd, ItemStore store)
    {
        _hwnd = hwnd;
        _store = store;
    }

    public bool Start()
    {
        if (_regId != 0) return true;

        // entry: pidl=NULL + fRecursive=TRUE → 全盘订阅
        var entry = new Win32.SHChangeNotifyEntry { pidl = IntPtr.Zero, fRecursive = 1 };

        _regId = Win32.SHChangeNotifyRegister(
            _hwnd,
            // RecursiveInterrupt 必须配 fRecursive=TRUE，否则深层目录变更收不到
            Win32.SHCNRF_InterruptLevel | Win32.SHCNRF_ShellLevel | Win32.SHCNRF_RecursiveInterrupt,
            Win32.SHCNE_RENAMEFOLDER | Win32.SHCNE_RENAMEITEM
                | Win32.SHCNE_DELETE | Win32.SHCNE_RMDIR,
            Win32.WM_APP_SHELLNOTIFY,
            1, ref entry);

        if (_regId == 0)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"SHChangeNotifyRegister failed: err={err}");
            return false;
        }
        return true;
    }

    public void Stop()
    {
        if (_regId == 0) return;
        Win32.SHChangeNotifyDeregister(_regId);
        _regId = 0;
    }

    /// <summary>
    /// 由 MainWindow.WndProc 在收到 WM_APP_SHELLNOTIFY 时调用。
    ///
    /// ⚠️ 参数语义（MSDN SHChangeNotifyRegister 备注）：
    /// 未指定 SHCNRF_NewDelivery 时，**wParam 是指向「两个 PIDLIST_ABSOLUTE 指针」的数组，
    /// lParam 才是事件 ID**。
    /// 之前这里写反了（拿 wParam 当事件 ID、把 lParam 拆成高低 32 位当两个 PIDL），
    /// 后果：事件 ID 读到的是堆指针（随机值），PIDL 读到的是事件 ID 的高低半段——
    /// 拿 0x20000 这种无效指针去调 SHGetPathFromIDList，跟随永远不可能生效，还有崩溃风险。
    /// 这是「path auto-follow 没实现成功」的真正根因（比事件位测试那次更深一层）。
    /// </summary>
    public void HandleNotify(IntPtr wParam, IntPtr lParam)
    {
        uint eventId = unchecked((uint)lParam.ToInt64());
        IntPtr ppidl = wParam;   // PIDLIST_ABSOLUTE[2]

        IntPtr pidl1 = IntPtr.Zero, pidl2 = IntPtr.Zero;
        if (ppidl != IntPtr.Zero)
        {
            pidl1 = Marshal.ReadIntPtr(ppidl);                 // [0] 旧 PIDL
            pidl2 = Marshal.ReadIntPtr(ppidl, IntPtr.Size);    // [1] 新 PIDL（仅 RENAME* 有意义）
        }

        bool changed = false;

        // 必须用位测试而不是等值比较：
        // Shell 可能把多个 SHCNE_ 事件合并成一条通知发过来（lParam 事件 ID 是多个事件位的组合）。
        // 尤其 SHCNE_RENAMEFOLDER = 0x00020000 高 16 位非零，一旦事件 ID 里混了其它位，
        // 等值比较就永远匹配不上——重命名/移动的跟随会静默失效。
        if ((eventId & Win32.SHCNE_RENAMEFOLDER) != 0 ||
            (eventId & Win32.SHCNE_RENAMEITEM) != 0)
        {
            if (pidl1 == IntPtr.Zero || pidl2 == IntPtr.Zero) return;
            string oldPath = PidlToPath(pidl1);
            string newPath = PidlToPath(pidl2);
            if (!string.IsNullOrEmpty(oldPath) && !string.IsNullOrEmpty(newPath))
                changed = RenameInStore(oldPath, newPath);
        }
        else if ((eventId & Win32.SHCNE_DELETE) != 0 ||
                 (eventId & Win32.SHCNE_RMDIR) != 0)
        {
            if (pidl1 == IntPtr.Zero) return;
            string oldPath = PidlToPath(pidl1);
            if (!string.IsNullOrEmpty(oldPath))
                changed = MarkMissingInStore(oldPath);
        }

        if (changed)
        {
            _store.Save();
            ItemsChanged?.Invoke();
        }
    }

    private static string PidlToPath(IntPtr pidl)
    {
        // SHGetPathFromIDList 对虚拟文件夹（库、This PC）会失败 → 直接忽略
        var sb = new System.Text.StringBuilder(Win32.MAX_PATH);
        return Win32.SHGetPathFromIDList(pidl, sb) ? sb.ToString() : null;
    }

    private bool RenameInStore(string oldPath, string newPath)
    {
        string oldNorm = ItemStore.Normalize(oldPath);
        bool any = false;
        foreach (var item in _store.Items)
        {
            if (ItemStore.Normalize(item.Path) != oldNorm) continue;
            item.Path = newPath;
            item.DisplayName = Path.GetFileName(newPath.TrimEnd('\\', '/'));
            any = true;
        }
        return any;
    }

    private bool MarkMissingInStore(string oldPath)
    {
        string oldNorm = ItemStore.Normalize(oldPath);
        bool any = false;
        foreach (var item in _store.Items)
        {
            if (ItemStore.Normalize(item.Path) != oldNorm) continue;
            item.Missing = true;
            any = true;
        }
        return any;
    }

    public void Dispose() => Stop();
}
