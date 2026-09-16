using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DropBridge.Native;

/// <summary>
/// Win32 / Shell P/Invoke 封装。只放实际用得到的，避免无谓膨胀。
/// </summary>
public static class Win32
{
    // ---------- 窗口样式 ----------
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;

    // ---------- WinEvent ----------
    public const uint EVENT_MIN = 0x0001;
    public const uint EVENT_MAX = 0x0017;                 // 只订阅 EVENT_SYSTEM_* 范围，避免海量 EVENT_OBJECT_*
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;  // 移动/缩放/最大化/还原结束
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ---------- 窗口 / 进程信息 ----------
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ---------- 鼠标按键状态（用于判定是否正在拖拽） ----------
    public const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public static bool IsLeftButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    // ---------- 窗口激活（复用已有资源管理器窗口时用） ----------
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    // ---------- 顶层窗口枚举 ----------
    // 用于判断「系统里还有没有开着的资源管理器窗口」——区分「前台短暂落到桌面的过渡态」
    // 和「用户真的把资源管理器关完了」这两种情形。
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;

    public const uint GA_ROOT = 2;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ---------- 扩展样式读写（x64 必须走 SetWindowLongPtr） ----------
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : GetWindowLong32(hWnd, nIndex);

    // ---------- Shell 通知（SHChangeNotifyRegister）----------
    // WM_USER = 0x0400，WM_APP_SHELLNOTIFY = WM_USER + 0x17 = 0x0417
    public const int WM_APP_SHELLNOTIFY = 0x0417;

    public const uint SHCNRF_InterruptLevel = 0x0001;
    public const uint SHCNRF_ShellLevel = 0x0002;
    // 订阅整棵子树：注册时 fRecursive=TRUE，按 MSDN 必须同时带上这个标志，
    // 否则深层目录（D:\a\b\c）的变更收不到，只有桌面直属层才有通知
    public const uint SHCNRF_RecursiveInterrupt = 0x1000;

    public const uint SHCNE_RENAMEITEM = 0x00000001;
    public const uint SHCNE_DELETE = 0x00000004;
    public const uint SHCNE_RMDIR = 0x00000010;
    public const uint SHCNE_RENAMEFOLDER = 0x00020000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SHChangeNotifyEntry
    {
        public IntPtr pidl;
        public int fRecursive;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern uint SHChangeNotifyRegister(
        IntPtr hwnd,
        uint fSources,
        uint fEvents,
        uint wMsg,
        int cEntries,
        ref SHChangeNotifyEntry ppshEntry);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHChangeNotifyDeregister(uint ulID);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    public const int MAX_PATH = 260;

    // ---------- 图标：IShellItemImageFactory ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItemImageFactory ppv);

    public static readonly Guid IID_IShellItemImageFactory =
        new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    // ---------- 通用图标（SHGetFileInfo）----------
    // 只查系统图标缓存，不走缩略图生成，调用开销 <1ms。
    // 用作 IShellItemImageFactory 异步提取高质量图标之前的即时占位，
    // 避免启动 / 批量拖入时图标位置一片空白。
    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- 结构 ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    // ---------- 辅助 ----------
    public static string GetClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetProcessNameOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return string.Empty;

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return string.Empty;

        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return string.Empty;
            string full = sb.ToString();
            return System.IO.Path.GetFileName(full);
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
