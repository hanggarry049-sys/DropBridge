using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using DropBridge.Native;

namespace DropBridge.Services;

/// <summary>
/// 监视前台窗口，判断浮层（悬浮条）此刻该显示还是隐藏。
///
/// 判定不是简单的「前台窗口是不是资源管理器」，而是三态：
///   1. 文件资源管理器窗口（CabinetWClass / ExploreWClass，explorer.exe）→ 按最小化/最大化/可见性显示或隐藏；
///   2. 桌面窗口（Progman / WorkerW，explorer.exe）→ 还要再分辨一次（见下）；
///   3. 其它应用（Chrome / VS Code / …）→ 立即隐藏。
///
/// 为什么桌面这一支最麻烦：同一个信号（前台=桌面）对应两种**完全相反**的场景——
///   a) 双击悬浮图标打开文件夹、或新资源管理器窗口创建时，前台会短暂落到桌面（几百毫秒），
///      这是过渡态，此时**不能隐藏**，否则图标会闪一下；
///   b) 用户关掉/最小化了最后一个资源管理器窗口，此时**必须隐藏**，否则图标一直浮在桌面上。
/// 从 API 看两者一模一样，没有别的信号能区分，所以只能去查「系统里还有没有开着的
/// 资源管理器窗口」：有 → 按 (a) 保持原状；没有 → 按 (b) 隐藏。
/// 加延迟防抖是行不通的——切走必须零延迟，否则浮层会肉眼可见地"黏"在原地一小会儿。
/// </summary>
public class ShellWatcher : IDisposable
{
    private Win32.WinEventDelegate _hookProc;   // 必须持有引用，否则被 GC 后回调崩溃
    private IntPtr _hook = IntPtr.Zero;
    private bool _lastState;

    // 轮询兜底：WinEvent 并不是所有场景都发得出来。最典型的就是 OLE 拖放期间——
    // 从资源管理器把文件拖到别的应用，前台窗口确实切走了，但系统不发
    // EVENT_SYSTEM_FOREGROUND（DoDragDrop 进了模态循环），光靠事件就会一直卡在旧状态，
    // 表现就是「拖文件时浮层不隐藏」。120ms 轮询补上这个缺口，开销很轻。
    // 有了它，过渡态也不再需要单独的延迟重查定时器。
    private DispatcherTimer _pollTimer;

    public bool IsExplorerForeground { get; private set; }

    public event Action<bool> ForegroundChanged;

    public void Start()
    {
        _hookProc = OnWinEvent;
        // 订阅整个 EVENT_SYSTEM_* 范围：前台切换、最小化/还原、最大化/缩放结束都会触发，
        // 否则点击任务栏标签最小化/恢复时（常不触发 FOREGROUND）悬浮条会跟不上
        _hook = Win32.SetWinEventHook(
            Win32.EVENT_MIN,
            Win32.EVENT_MAX,
            IntPtr.Zero,
            _hookProc,
            0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

        // 事件负责「即时」，轮询负责「不漏」。两者都走同一个 Refresh()，判定规则只有一份。
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();

        Refresh();
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType != Win32.EVENT_SYSTEM_FOREGROUND &&
            eventType != Win32.EVENT_SYSTEM_MINIMIZESTART &&
            eventType != Win32.EVENT_SYSTEM_MINIMIZEEND &&
            eventType != Win32.EVENT_SYSTEM_MOVESIZEEND)
            return;

        Refresh();
    }

    public void Refresh()
    {
        IntPtr fg = Win32.GetForegroundWindow();

        if (IsExplorerWindow(fg))
        {
            // 文件资源管理器窗口在前台：非最小化、非最大化才显示。
            // 最大化同样隐藏——需要悬浮条的是多窗口并排搬运的场景，顶格单窗口用不上。
            bool iconic = Win32.IsIconic(fg);
            bool zoomed = Win32.IsZoomed(fg);
            bool visible = Win32.IsWindowVisible(fg);

            // 新窗口创建/激活过渡态：前台已是 CabinetWClass 但 WS_VISIBLE 还没置位。
            // 保持原状即可，120ms 后的轮询会重新判一次，窗口可见后自然显示。
            if (!visible && !iconic && !zoomed)
                return;

            SetState(!iconic && !zoomed && visible);
        }
        else if (fg == IntPtr.Zero || IsShellDesktopWindow(fg))
        {
            // 前台是桌面（或无前台窗口）——这里有两种截然不同的情形，必须分开处理：
            //
            //   1) 过渡态：双击打开文件夹、新资源管理器窗口创建时，前台会短暂落到这里。
            //      此时系统里仍有别的资源管理器窗口 → 保持原状，避免「双击打开后图标闪没」。
            //
            //   2) 真的没有了：用户关掉了最后一个资源管理器窗口（或全部最小化）。
            //      这时必须隐藏，否则图标会一直浮在桌面上——之前一律「保持原状」，
            //      表现就是关掉资源管理器后图标不消失，要等到切到别的应用才隐掉。
            //
            // 判据：系统里还有没有「可见且未最小化」的资源管理器窗口。
            if (!AnyOpenExplorerWindow())
                SetState(false);
        }
        else
        {
            // 前台是其它应用 → 立即隐藏，不延迟
            SetState(false);
        }
    }

    private void SetState(bool show)
    {
        if (show == _lastState) return;
        _lastState = show;
        IsExplorerForeground = show;
        ForegroundChanged?.Invoke(show);
    }

    /// <summary>文件资源管理器窗口：CabinetWClass / ExploreWClass 且进程是 explorer.exe。</summary>
    public static bool IsExplorerWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        string cls = Win32.GetClassNameOf(hwnd);
        if (cls != "CabinetWClass" && cls != "ExploreWClass") return false;

        string proc = Win32.GetProcessNameOf(hwnd);
        return proc.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>桌面窗口（Progman / WorkerW）：explorer.exe 的一部分，但不是文件资源管理器窗口。</summary>
    public static bool IsShellDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        string cls = Win32.GetClassNameOf(hwnd);
        if (cls != "Progman" && cls != "WorkerW") return false;

        string proc = Win32.GetProcessNameOf(hwnd);
        return proc.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 系统里是否还存在「可见且未最小化」的资源管理器窗口。
    /// 只服务于一个目的：当前台是桌面时，区分「新窗口创建中的过渡态」
    /// 和「用户把资源管理器关完了 / 全最小化了」这两种情形。
    /// </summary>
    private static bool AnyOpenExplorerWindow()
    {
        bool found = false;

        Win32.EnumWindows((hwnd, _) =>
        {
            if (IsExplorerWindow(hwnd) &&
                Win32.IsWindowVisible(hwnd) &&
                !Win32.IsIconic(hwnd))
            {
                found = true;
                return false;   // 已经找到了，停止枚举
            }
            return true;        // 继续找
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// 打开文件夹。优先复用已经打开着该目录的资源管理器窗口——
    /// 冷启动一个 explorer.exe 要几百毫秒到一秒多，双击图标时会明显发涩，
    /// 而已存在的窗口只是切一下前台，是瞬时的。
    /// </summary>
    public static void OpenInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        try
        {
            Type t = Type.GetTypeFromProgID("Shell.Application");
            if (t != null)
            {
                dynamic shell = Activator.CreateInstance(t);

                foreach (dynamic win in shell.Windows())
                {
                    try
                    {
                        string p = (string)win.Document.Folder.Self.Path;
                        if (!string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) continue;

                        IntPtr hwnd = new IntPtr((long)win.HWND);
                        if (Win32.IsIconic(hwnd)) Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);
                        Win32.SetForegroundWindow(hwnd);
                        return;
                    }
                    catch
                    {
                        // 部分窗口没有 Document（如浏览器），跳过
                    }
                }

                shell.Open(path);   // 没有现成窗口才新建
                return;
            }
        }
        catch
        {
            // 走兜底
        }

        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        _pollTimer?.Stop();
        _pollTimer = null;
    }
}
