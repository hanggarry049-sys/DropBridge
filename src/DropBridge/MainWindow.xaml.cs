using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DropBridge.Models;
using DropBridge.Native;
using DropBridge.Services;

namespace DropBridge;

public partial class MainWindow : Window
{
    // ---------- 尺寸 ----------
    // 图标尺寸来自设置（48 / 64 / 80），单元尺寸随之推导。
    // 原来写死的 76 / 92 / 82 正好是 IconSize=64 时的 +12 / +28 / +18，沿用同一套关系。
    private int IconSize => _settings.IconSize;
    private double ItemW => IconSize + 12;
    private double ItemH => IconSize + 28;
    private double ItemVisualBottom => IconSize + 18;
    private const double CanvasPad = 12;
    private const double TrashReserve = 100;     // 底部为删除区预留的高度
    // 隐藏期下沉落盘的节流间隔：每 30 秒最多写一次 items.json
    private static readonly TimeSpan AgingSaveInterval = TimeSpan.FromSeconds(30);

    private const string ItemIdFormat = "DropBridge/ItemId";

    // 黑洞下沉速度（像素/小时），由设置决定；0 = 永不老化。
    // 1080p 屏从免老化线到黑洞约 44 小时。
    private double SinkSpeed => _settings.SinkSpeedPerHour / 3600.0;   // 像素/秒

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_DISPLAYCHANGE = 0x007E;   // 分辨率/显示器数量变化
    private const int WM_SETTINGCHANGE = 0x001A;   // 任务栏/工作区变化（SPI_SETWORKAREA）
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    private readonly ItemStore _store;
    private readonly AppSettings _settings;
    private HwndSource _hwndSource;
    private DispatcherTimer _agingTimer;
    private DispatcherTimer _armTimer;
    private ShellNotifyWatcher _shellNotifyWatcher;

    private bool _explorerForeground;
    private bool _selfDragging;      // 自身正在发起 OLE 拖拽（拖出到资源管理器）
    private bool _armed;             // 检测到外部拖拽 → 整条激活为放置区
    private bool _wasLeftDown;
    private bool _downInExplorer;
    private DateTime _lastAgingSave = DateTime.Now;   // 上次隐藏期下沉落盘时刻

    // 三个区域的底色：平时完全透明，只在拖入文件夹时亮起（说明文字 + 底色 + 分隔线一起出现）。
    private static readonly Brush NoAgeHighlight  = Rgb(0x42, 0x3A, 0x6F, 0xFF);   // 常驻区高亮
    private static readonly Brush CenterHighlight = Rgb(0x16, 0xFF, 0xFF, 0xFF);   // 自由摆放区高亮（淡白）
    private static readonly Brush TrashHighlight  = Rgb(0x42, 0xFF, 0x5A, 0x5A);   // 移除区高亮

    // 图标底板：半透明磨砂面，图标 + 名称共用一块。
    //
    // 注意「真毛玻璃」在架构上做不到——WPF 的 BlurEffect 只能模糊窗口自身画的内容，
    // 模糊不了窗口背后的桌面 / 资源管理器；能模糊背后的只有 DWM 的窗口级 Acrylic，
    // 那会把整条 192px 都变成磨砂条并破坏现在的空白区鼠标穿透。
    // 所以这里用半透明白色块在观感上模拟磨砂玻璃。
    //
    // 不加描边：纯色块，底片边界不显得生硬。
    // 不透明度是 0x99（60%）而不是更淡：名称是纯黑，底板必须够亮才压得住。
    // 32% 白在纯黑背景上合成出来只有 #525252，黑字对比度只有 2.8:1（读不清）；
    // 60% 白合成 #999999，对比度 7.4:1，纯黑背景到纯白背景都能读。
    private static readonly Brush GlassFill       = Rgb(0x99, 0xFF, 0xFF, 0xFF);   // 磨砂面

    private static Brush Rgb(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    // 最近一次布局算出的画布尺寸（DIP）。
    // 不用 ItemsCanvas.ActualWidth/Height：那要等一次 layout pass 才更新，
    // 分辨率/任务栏变化后刚改完 Window.Width/Height 就去读会拿到旧值，
    // 导致分区高度、拖出判定、clamp 全按过期尺寸算。
    private double _layoutW;
    private double _layoutH;
    private System.Windows.Rect _lastWorkArea;

    // 图标拖动状态：画布内自绘摆放 + 拖出窗口才走 OLE
    private BridgeItem _dragItem;
    private FrameworkElement _dragView;
    private Point _pressCanvas;                    // 按下瞬间光标在画布内的 DIP 坐标
    private double _dragStartX, _dragStartY;       // 按下时图标的视觉位置（即当前位置）
    private bool _dragging;                        // 左键按下、正在跟踪拖动
    private bool _placing;                         // 已越过拖动阈值

    public MainWindow(ItemStore store, AppSettings settings)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;

        LayoutWindow();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { LayoutWindow(); RenderItems(); };

        // 从资源管理器拖入
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        _agingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _agingTimer.Tick += (_, _) => UpdateAging();

        // 20ms 轮询：自己拖图标（TrackSelfDrag）或外部拖拽检测（PollDragArming）
        _armTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _armTimer.Tick += (_, _) => PollTick();

        // 强制创建 hwnd：窗口启动后是隐藏的，不主动 EnsureHandle 的话
        // SourceInitialized 不会触发，热键注册与 WS_EX_TOOLWINDOW 就全漏了
        _ = new WindowInteropHelper(this).EnsureHandle();
    }

    // ---------- 窗口样式与布局 ----------

    private void LayoutWindow()
    {
        var wa = SystemParameters.WorkArea;

        // 宽度来自设置（占屏宽比例），上下限再兜一层：
        // 最小 120 保证放得下一个图标单元，最大不超过半个屏宽，别把桌面挡掉
        Width = Math.Clamp(wa.Width * _settings.DockWidthRatio,
                           Math.Min(120, wa.Width),
                           wa.Width * 0.5);
        Height = wa.Height;
        Left = _settings.DockSide == "Right" ? wa.Right - Width : wa.Left;
        Top = wa.Top;

        // 窗口无边框（WindowStyle=None），客户区就等于 Width×Height，
        // Canvas 撑满后尺寸一致，可直接当作画布尺寸用
        _layoutW = Width;
        _layoutH = Height;
        _lastWorkArea = wa;

        LayoutZones();
    }

    /// <summary>画布宽度（DIP）：优先用布局时算好的值，未布局时退回窗口宽度</summary>
    private double CanvasW() => _layoutW > 0 ? _layoutW : Width;

    /// <summary>画布高度（DIP）：优先用布局时算好的值，未布局时退回窗口高度</summary>
    private double CanvasH() => _layoutH > 0 ? _layoutH : Height;

    /// <summary>免老化区边界的 Y 坐标：落在此线上方的条目不老化</summary>
    private double NoAgeLine()
    {
        return CanvasH() * _settings.NoAgeRatio;
    }

    private void LayoutZones()
    {
        double h = CanvasH();

        // 上方免老化区：淡蓝铺底 + 上沿边界线
        double line = h * _settings.NoAgeRatio;
        NoAgeZone.Height = line;
        NoAgeDivider.Margin = new Thickness(0, line - 1, 0, 0);

        // 底部移除区（黑洞）：淡红铺底 + 上沿边界线，与上方免老化区同一套视觉语言
        TrashArea.Height = TrashReserve;
        TrashDivider.Margin = new Thickness(0, 0, 0, TrashReserve - 1);

        // 拖拽说明文字定位到各自区域中心（两行 18px 文字约 50px 高，偏移 25 使其居中）
        double midY = line + (h - line - TrashReserve) / 2;
        NoAgeLabel.Margin = new Thickness(0, Math.Max(8, line / 2 - 25), 0, 0);
        CenterLabel.Margin = new Thickness(0, Math.Max(line + 8, midY - 25), 0, 0);
        TrashLabel.Margin = new Thickness(0, Math.Max(midY + 25, h - TrashReserve / 2 - 25), 0, 0);
    }

    /// <summary>
    /// 工作区尺寸可能变了（改分辨率 / 插拔显示器 / 任务栏高度变化）。
    /// 只有真的变了才重布局，并把所有条目 clamp 回新画布范围内——
    /// 否则屏高变小后原本 Y=800 的图标会跑到画布外，甚至直接压在黑洞里被吞掉。
    /// </summary>
    private void OnWorkAreaMaybeChanged()
    {
        var wa = SystemParameters.WorkArea;
        if (Math.Abs(wa.Width - _lastWorkArea.Width) < 1 &&
            Math.Abs(wa.Height - _lastWorkArea.Height) < 1 &&
            Math.Abs(wa.Left - _lastWorkArea.Left) < 1 &&
            Math.Abs(wa.Top - _lastWorkArea.Top) < 1)
            return;

        LayoutWindow();   // 内部会刷新 _layoutW / _layoutH / _lastWorkArea
        ClampItemsIntoCanvas();
        RenderItems();
    }

    /// <summary>
    /// 把所有已摆放的条目 clamp 回当前画布范围内。
    /// 画布尺寸变了就得调：分辨率/任务栏变化，或用户改了侧边宽度 / 图标尺寸。
    /// </summary>
    private void ClampItemsIntoCanvas()
    {
        double w = CanvasW();
        // 下边界按「黑洞上沿」算而不是画布底部：尺寸变小是环境变化，
        // 不该让用户的图标因此被瞬间吸入
        double maxY = Math.Max(4, CanvasH() - TrashReserve - ItemVisualBottom);
        double maxX = Math.Max(4, w - ItemW - 4);

        bool dirty = false;
        foreach (var item in _store.Items)
        {
            if (item.X < 0 || item.Y < 0) continue;   // 未摆放的交给 EnsurePosition

            double nx = Math.Clamp(item.X, 4, maxX);
            double ny = Math.Clamp(item.Y, 4, maxY);
            if (Math.Abs(nx - item.X) > 0.5 || Math.Abs(ny - item.Y) > 0.5)
            {
                item.X = nx;
                item.Y = ny;
                dirty = true;
            }
        }

        if (dirty) _store.Save();
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource.AddHook(WndProc);

        // 路径自动跟随：监听 Shell 文件系统变更，文件夹被移动/重命名/删除时同步条目
        _shellNotifyWatcher = new ShellNotifyWatcher(hwnd, _store);
        _shellNotifyWatcher.ItemsChanged += OnShellItemsChanged;
        _shellNotifyWatcher.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        if (_shellNotifyWatcher != null)
        {
            _shellNotifyWatcher.ItemsChanged -= OnShellItemsChanged;
            _shellNotifyWatcher.Dispose();
            _shellNotifyWatcher = null;
        }
        base.OnClosed(e);
    }

    private void OnShellItemsChanged()
    {
        // 路径改写或被标记失效 → 重渲染让 BuildItemView 用新 Path 重新取图标
        Dispatcher.BeginInvoke(RenderItems);
    }

    /// <summary>
    /// 空白区返回 HTTRANSPARENT，让鼠标穿透到下面的资源管理器（否则左侧 10% 会挡死导航窗格）。
    /// 命中到具体元素时交回 WPF 默认处理。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_APP_SHELLNOTIFY)
        {
            _shellNotifyWatcher?.HandleNotify(wParam, lParam);
            handled = true;
            return IntPtr.Zero;
        }

        // 分辨率 / 显示器数量 / 任务栏工作区变化 → 重新布局。
        // 这两条消息很频繁（改主题、换壁纸也会发 WM_SETTINGCHANGE），
        // 所以只排队，真正比对尺寸放到 OnWorkAreaMaybeChanged 里做
        if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE)
        {
            Dispatcher.BeginInvoke(new Action(OnWorkAreaMaybeChanged),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        if (msg == WM_NCHITTEST)
        {
            if (_selfDragging)
            {
                // 拖拽期整窗可命中，让 OLE 把画布当作 drop target。
                // 否则松在画布空白区会穿透到下面的资源管理器，摆不进去。
                handled = true;
                return new IntPtr(HTCLIENT);
            }

            int sx = (short)(lParam.ToInt32() & 0xFFFF);
            int sy = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

            var hit = VisualTreeHelper.HitTest(this, PointFromScreen(new System.Windows.Point(sx, sy)));
            if (hit == null)
            {
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }
        }

        return IntPtr.Zero;
    }

    // ---------- 渲染 ----------

    public void RenderItems()
    {
        _store.RefreshExistence();
        LayoutZones();

        // 渲染不调 AdvanceAll——避免拖入/热键/右键等触发「显示期间」下沉。
        // 隐藏期下沉由 _agingTimer 触发 UpdateAging 负责；
        // 隐藏→显示的瞬间由 SetExplorerForeground(true) 显式补算。

        ItemsCanvas.Children.Clear();
        foreach (var item in _store.Items)
        {
            EnsurePosition(item);
            var view = BuildItemView(item);
            Canvas.SetLeft(view, item.X);
            Canvas.SetTop(view, item.Y);
            ItemsCanvas.Children.Add(view);
        }

        EmptyHint.Visibility = _store.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MaybeHide();
    }

    /// <summary>没摆过位置的条目自动找个空位</summary>
    private void EnsurePosition(BridgeItem item)
    {
        if (item.X >= 0 && item.Y >= 0) return;

        var slot = FindFreeSlot();
        item.X = slot.X;
        item.Y = slot.Y;
    }

    private System.Windows.Point FindFreeSlot()
    {
        double w = CanvasW();
        double h = CanvasH();

        int cols = Math.Max(1, (int)((w - CanvasPad * 2 + 8) / (ItemW + 8)));
        // 默认落在老化区（免老化线以下），上方 1/3 留给用户主动拖进去的条目
        double startY = NoAgeLine() + CanvasPad;
        double maxY = h - TrashReserve - ItemH;

        for (int i = 0; i < 400; i++)
        {
            double x = CanvasPad + (i % cols) * (ItemW + 8);
            double y = startY + (i / cols) * (ItemH + 6);
            if (y > maxY) break;
            if (!SlotOccupied(x, y)) return new System.Windows.Point(x, y);
        }

        return new System.Windows.Point(CanvasPad, startY);
    }

    private bool SlotOccupied(double x, double y)
    {
        foreach (var it in _store.Items)
        {
            if (it.X < 0 || it.Y < 0) continue;
            if (Math.Abs(it.X - x) < ItemW * 0.6 && Math.Abs(it.Y - y) < ItemH * 0.6) return true;
        }
        return false;
    }

    private FrameworkElement BuildItemView(BridgeItem item)
    {
        var root = new StackPanel
        {
            Width = ItemW,
            Cursor = Cursors.Hand,
            Tag = item
        };
        root.ToolTip = item.Path;

        // 磨砂底板：图标 + 名称共用一块卡片，无描边。
        // 名称放进底板内（而不是挂在下面），文字才能用纯黑——否则浅色背景上白字看不清。
        // 用上面 frozen 的静态 Brush 而不是每次 new——渲染在拖拽 / 下沉期间会被高频触发。
        var tile = new Border
        {
            Width = ItemW,
            CornerRadius = new CornerRadius(10),
            Background = GlassFill,
            Opacity = item.Missing ? 0.35 : 1.0,
            Padding = new Thickness(4, 6, 4, 5)
        };

        var stack = new StackPanel();

        // 先用系统通用文件夹图标占位（同步且 <1ms），高质量图标后台提取完再替换。
        // 不能在这里同步调 IShellItemImageFactory：首次提取每个要几十毫秒，
        // 启动加载 N 个条目 / 批量拖入时会把 UI 线程整个堵住。
        var img = new Image
        {
            Width = IconSize - 14,
            Height = IconSize - 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Source = IconProvider.GetGenericFolderIcon(IconSize)
        };

        var label = new TextBlock
        {
            Text = item.DisplayName,
            Foreground = Brushes.Black,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        };

        stack.Children.Add(img);
        stack.Children.Add(label);
        tile.Child = stack;

        IconProvider.GetFolderIconAsync(item.Path, IconSize, bmp =>
        {
            // 不要用 img.IsLoaded 当「视图还在」的判据！
            // 缓存命中（内存或磁盘）时这个回调是**同步**触发的，此刻刚 new 出来的 Image
            // 还没进可视化树，IsLoaded 必然是 false（实测确认），于是真图标被静默丢弃，
            // 只剩通用文件夹占位图——磁盘缓存等于白做，带自定义图标/缩略图的文件夹永远显示不出来。
            // 写到已被丢弃的元素上没有任何副作用（最多白赋值一次），所以直接写。
            if (bmp != null) img.Source = bmp;
        });

        root.Children.Add(tile);

        root.MouseLeftButtonDown += OnItemMouseDown;

        return root;
    }

    /// <summary>
    /// 整理排列：把所有图标重置为「未摆放」，下次渲染按默认槽位整齐重排。
    /// 只从托盘菜单调用——2026-09-11 起图标右键菜单已整体移除（打开/固定/移除
    /// 分别由双击、拖到常驻区、拖到移除区替代），这里是它唯一的入口。
    /// </summary>
    public void TidyAll()
    {
        foreach (var it in _store.Items) { it.X = -1; it.Y = -1; }
        RenderItems();
        _store.Save();
    }

    // ---------- 老化：黑洞模型（位置即老化程度） ----------
    //
    // 图标以恒定速度向底部"黑洞"（删除区）匀速下沉；触到黑洞上沿即被吸入移除。
    // 位置就是状态：拖近黑洞 = 更快被吸，拖高 = 远离黑洞续命，无需任何重置判定。

    /// <summary>推进所有图标下沉到 now。</summary>
    /// <param name="now">当前时刻</param>
    /// <param name="moved">是否有条目的 Y 真的变过（用于决定要不要落盘）</param>
    /// <returns>是否有条目被黑洞吸入</returns>
    private bool AdvanceAll(DateTime now, out bool moved)
    {
        moved = false;

        // 暂停下沉：位置不再变化，也就不会被黑洞吸走
        if (_settings.PauseAging) return false;

        double noAgeLine = NoAgeLine();
        double canvasH = CanvasH();
        double trashTop = canvasH - TrashReserve;
        bool removed = false;

        foreach (var item in _store.Items.ToList())
        {
            // 常驻区（免老化区）：不下沉，但基准时刻必须推进到 now。
            // 否则会累积「下沉欠账」——在常驻区放两天再拖下来的瞬间，
            // 下一次会按累计的两天一次性下沉 576px，直接被黑洞吞掉。
            if (item.Y < noAgeLine)
            {
                item.SinkAt = now;
                continue;
            }

            double elapsed = (now - item.SinkAt).TotalSeconds;
            // SinkSpeed > 0 才置 moved：速度为 0（永不老化）时 Y 根本没变，
            // 置了 moved 会让隐藏期每隔 30 秒白写一次 items.json，纯浪费。
            if (elapsed > 0 && SinkSpeed > 0)
            {
                item.Y += SinkSpeed * elapsed;   // 位置即老化：直接改 Y
                moved = true;
            }

            // 不论是否真的移动过，基准都要推进：
            // 一是避免上面的欠账，二是时钟回拨时（elapsed &lt; 0）不会在将来一次性补偿
            item.SinkAt = now;

            if (item.Y + ItemVisualBottom / 2 >= trashTop)   // 图标视觉中心触到黑洞上沿 → 吸入
            {
                _store.Remove(item);
                removed = true;
            }
        }
        return removed;
    }

    private void UpdateAging()
    {
        if (_store.Items.Count == 0) return;

        // 显示期间不沉：仅把 Canvas 位置同步到 item.Y（拖动摆放松手后用得上），
        // 任何下沉由隐藏期 _agingTimer 触发时统一处理
        if (_explorerForeground)
        {
            ApplyPositions();
            return;
        }

        if (AdvanceAll(DateTime.Now, out bool moved))
        {
            _store.Save();
            _lastAgingSave = DateTime.Now;
            RenderItems();
            return;
        }

        // 沉了但还没被吸入时也要落盘，否则重启后所有图标弹回上次保存的位置、
        // 隐藏期积累的老化进度全丢（每天都重启的话黑洞机制基本不生效）。
        // 5 秒一次写文件太勤，节流到 30 秒。
        if (moved && (DateTime.Now - _lastAgingSave) >= AgingSaveInterval)
        {
            _store.Save();
            _lastAgingSave = DateTime.Now;
        }

        ApplyPositions();
    }

    /// <summary>把数据里的最新位置同步到 Canvas 上的图标（下沉动画）</summary>
    private void ApplyPositions()
    {
        foreach (UIElement el in ItemsCanvas.Children)
        {
            if (el is not FrameworkElement fe) continue;
            if (fe.Tag is not BridgeItem item) continue;
            Canvas.SetLeft(fe, item.X);
            Canvas.SetTop(fe, item.Y);
            fe.Opacity = item.Missing ? 0.4 : 1.0;
        }
    }

    // ---------- 图标拖动：画布内自绘摆放 + 拖出窗口才 OLE ----------
    //
    // 为什么不用 OLE 做画布内摆放：OLE 的 drop 目标判定依赖元素命中测试，而画布空白区
    // （Background=null）不参与命中测试，松手只能落在图标自身那 76×92 的范围内——鼠标一
    // 离开图标就「接不住」了。
    // 所以画布内摆放改成纯自绘：20ms 轮询读光标位置，直接改图标的 Canvas 坐标，不依赖任何
    // 命中测试。只有鼠标拖出窗口边界，才发起 OLE DoDragDrop 交给资源管理器复制/移动。

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        var el = (FrameworkElement)sender;
        var item = (BridgeItem)el.Tag;

        if (e.ClickCount == 2)
        {
            ShellWatcher.OpenInExplorer(item.Path);
            ResetDragState();
            e.Handled = true;
            return;
        }

        _dragItem = item;
        _dragView = el;
        _dragging = true;
        _placing = false;

        // 用画布内 DIP 坐标，避免 GetCursorPos 的物理像素与 Canvas 的 DIP 混用导致高 DPI 下拖不动/漂移
        _pressCanvas = CursorInCanvas();

        // 记录按下时图标的当前位置（黑洞模型下位置即状态，无锚点/下沉分离）
        _dragStartX = Canvas.GetLeft(el);
        _dragStartY = Canvas.GetTop(el);

        e.Handled = true;
    }

    /// <summary>20ms 轮询：拖动期间由本方法驱动图标移动，不依赖 WPF 鼠标捕获。</summary>
    private void TrackSelfDrag()
    {
        if (_selfDragging) return;   // OLE 拖放中（阻塞，理论上不会 tick，防御）

        if (!Win32.IsLeftButtonDown())
        {
            FinishPlacing(CursorInCanvas());
            return;
        }

        var cur = CursorInCanvas();   // 画布 DIP 坐标

        if (!_placing)
        {
            var delta = cur - _pressCanvas;
            if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;   // 尚未越过拖动阈值

            // 越过阈值：进入摆放，图标从当前位置开始跟手（黑洞模型下位置即状态）
            _placing = true;
            Canvas.SetLeft(_dragView, _dragStartX);
            Canvas.SetTop(_dragView, _dragStartY);

            // 自拖（拖区域里已有的图标）也要亮起三个区域。
            // 这条路径不走 OLE，不会触发 DragEnter/DragOver，所以得在这里手动点亮；
            // 否则只有从资源管理器拖文件夹进来才看得到区域说明和高亮。
            SetZoneLabels(true);
        }

        // 拖出画布边界 → 交给资源管理器（OLE 复制/移动）
        if (IsOutsideCanvas(cur))
        {
            StartOleDrag();
            return;
        }

        // 摆放预览：保持按下时鼠标相对图标的偏移
        Canvas.SetLeft(_dragView, _dragStartX + (cur.X - _pressCanvas.X));
        Canvas.SetTop(_dragView, _dragStartY + (cur.Y - _pressCanvas.Y));
    }

    private void FinishPlacing(Point canvasPt)
    {
        var item = _dragItem;
        var view = _dragView;
        bool wasPlacing = _placing;

        _dragging = false;
        _placing = false;

        if (item == null)
        {
            ResetDragState();
            return;
        }

        if (wasPlacing)
        {
            if (TrashRect().Contains(canvasPt))
            {
                _store.Remove(item);
                _store.Save();
                RenderItems();
                MaybeHide();
            }
            else
            {
                double w = CanvasW();
                double h = CanvasH();

                item.X = Math.Clamp(Canvas.GetLeft(view), 4, Math.Max(4, w - ItemW - 4));
                // 位置即状态：松手处就是新位置，同时把下沉基准时刻重置到 now，
                // 从此位置继续向黑洞匀速下沉（拖高续命、拖低加速吸入，无需其他判定）
                item.Y = Math.Clamp(Canvas.GetTop(view), 4, Math.Max(4, h - ItemH - 4));
                item.SinkAt = DateTime.Now;

                _store.Save();
                UpdateAging();
            }
        }
        // else：未越过阈值（一次普通点击，非双击），无操作

        ResetDragState();
    }

    private void StartOleDrag()
    {
        var item = _dragItem;
        if (item == null) return;

        // 源文件夹已不存在（被删或被移走）：拖出去资源管理器也不会收，
        // 直接拒绝并给提示，避免用户遇到"拖了但完全没反应"
        if (item.Missing)
        {
            ResetDragState();
            ShowToast("源文件夹已不存在");
            return;
        }

        _placing = false;

        // 图标复位到按下前的位置，拖出去复制不改动摆放位置
        Canvas.SetLeft(_dragView, _dragStartX);
        Canvas.SetTop(_dragView, _dragStartY);

        // 同时提供 FileDrop（给资源管理器）与自定义 ItemId（给自己识别）
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { item.Path });
        data.SetData(ItemIdFormat, item.Id);

        _selfDragging = true;

        DragDropEffects effect;
        try
        {
            effect = DragDrop.DoDragDrop(_dragView, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _selfDragging = false;
        }

        // Move：同盘移动成功 → 资源管理器已接手，桥接条目作废
        // Copy：跨盘复制 → 源还在原位置，桥接继续生效
        // None ：拖出后没人接收 → 保持原位
        if (effect == DragDropEffects.Move)
        {
            _store.Remove(item);
            _store.Save();
            RenderItems();
        }

        _dragging = false;
        ResetDragState();
    }

    private bool IsOutsideCanvas(Point canvasPt)
    {
        const double m = 12;
        double w = CanvasW();
        double h = CanvasH();
        return canvasPt.X < -m || canvasPt.Y < -m || canvasPt.X > w + m || canvasPt.Y > h + m;
    }

    private void ResetDragState()
    {
        _dragItem = null;
        _dragView = null;
        _dragging = false;
        _placing = false;
        _pressCanvas = default;
        _dragStartX = 0;
        _dragStartY = 0;

        // 一次性兜掉所有拖动结束路径（松手摆放 / 吸入黑洞 / 拖出窗口后的 OLE 收尾 /
        // 源已失效)的区域高亮。DragLeave 在 OLE 被 Esc 取消等情况下不一定到得了。
        SetZoneLabels(false);
    }

    private void HandleInternalDrop(BridgeItem item)
    {
        var p = CursorInCanvas();

        if (TrashRect().Contains(p))
        {
            _store.Remove(item);
            _store.Save();
            RenderItems();
            MaybeHide();
            return;
        }

        double w = CanvasW();
        double h = CanvasH();

        if (p.X < 0 || p.Y < 0 || p.X > w || p.Y > h) return;   // 落在画布外，保持原位

        item.X = Math.Clamp(p.X - ItemW / 2, 4, Math.Max(4, w - ItemW - 4));
        item.Y = Math.Clamp(p.Y - ItemH / 2, 4, Math.Max(4, h - ItemH - 4));
        item.SinkAt = DateTime.Now;   // 位置即状态：落点即新位置，下沉基准重置到 now

        _store.Save();
        RenderItems();
    }

    private System.Windows.Point CursorInCanvas()
    {
        Win32.GetCursorPos(out var sp);
        return ItemsCanvas.PointFromScreen(new System.Windows.Point(sp.X, sp.Y));
    }

    private Rect TrashRect()
    {
        double w = CanvasW();
        double h = CanvasH();
        return new Rect(0, h - TrashReserve, w, TrashReserve);
    }

    // ---------- 外部拖拽检测：决定整条何时可接住 drop ----------

    /// <summary>20ms 统一入口：自己拖图标走 TrackSelfDrag，否则做外部拖拽检测。</summary>
    private void PollTick()
    {
        if (_dragging)
        {
            TrackSelfDrag();
            return;
        }
        PollDragArming();
    }

    private void PollDragArming()
    {
        if (_selfDragging) return;

        bool down = Win32.IsLeftButtonDown();

        if (down && !_wasLeftDown)
        {
            Win32.GetCursorPos(out var pt);
            IntPtr hit = Win32.WindowFromPoint(pt);
            IntPtr root = Win32.GetAncestor(hit, Win32.GA_ROOT);
            _downInExplorer = ShellWatcher.IsExplorerWindow(root);
        }
        else if (!down && _wasLeftDown)
        {
            _downInExplorer = false;
            Disarm();
        }

        _wasLeftDown = down;

        if (down && _downInExplorer) Arm();
    }

    private void Arm()
    {
        if (_armed) return;
        _armed = true;

        // 铺一层 alpha=1 的画刷：肉眼几乎不可见，但让整条参与命中测试，
        // 这样 OLE 拖放才能把这里识别为放置目标
        DropSurface.Background = new SolidColorBrush(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF));
    }

    private void Disarm()
    {
        if (!_armed) return;
        _armed = false;

        DropSurface.Background = null;
        SetZoneLabels(false);
    }

    /// <summary>拖入文件夹时三个区域一起显现：说明文字 + 底色 + 分隔线。</summary>
    private void SetZoneLabels(bool show)
    {
        var v = show ? Visibility.Visible : Visibility.Collapsed;
        NoAgeLabel.Visibility = v;
        CenterLabel.Visibility = v;
        TrashLabel.Visibility = v;

        // 常驻区 / 移除区的底色和分隔线只在拖动期存在。平时整条完全透明：
        // 既不挡资源管理器导航窗格，也没有常驻的色带和横线。
        NoAgeZone.Background    = show ? NoAgeHighlight  : null;
        NoAgeDivider.Visibility = v;
        ItemsCanvas.Background  = show ? CenterHighlight : null;
        TrashArea.Background    = show ? TrashHighlight  : null;
        TrashDivider.Visibility = v;
    }

    // ---------- 从资源管理器拖入 ----------

    private void OnDragEnter(object sender, DragEventArgs e) => EvaluateDrag(e);

    private void OnDragOver(object sender, DragEventArgs e) => EvaluateDrag(e);

    private void EvaluateDrag(DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetZoneLabels(ok);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        SetZoneLabels(false);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetZoneLabels(false);
        Disarm();

        // 自拖放：自己拖出的图标松在画布内 → 摆放 / 删除
        if (e.Data.GetDataPresent(ItemIdFormat))
        {
            var item = _store.Items.FirstOrDefault(i => i.Id == (string)e.Data.GetData(ItemIdFormat));
            if (item != null) HandleInternalDrop(item);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths == null) return;

        // 拿到松手点在画布 DIP 坐标系下的位置；第一个条目中心对齐光标，
        // 多个条目同时拖入时阶梯偏移避免叠在一起
        var dropPt = e.GetPosition(ItemsCanvas);
        int added = 0;
        int dup = 0;       // 已经在列表里
        int invalid = 0;   // 拖进来的不是文件夹

        for (int i = 0; i < paths.Length; i++)
        {
            string p = paths[i];
            if (!Directory.Exists(p)) { invalid++; continue; }
            double x = dropPt.X - ItemW / 2 + i * 8;
            double y = dropPt.Y - ItemH / 2 + i * 16;
            if (_store.TryAdd(p, x, y, out _)) added++;
            else dup++;
        }

        if (added > 0)
        {
            _store.Save();
            RenderItems();
        }

        // 一个都没加进去时给个说法，否则用户只会以为拖入没生效
        if (added == 0 && dup > 0) ShowToast("已在悬浮列表中");
        else if (added == 0 && invalid > 0) ShowToast("只能拖入文件夹");
    }

    // ---------- 轻量操作反馈 ----------

    private DispatcherTimer _toastTimer;

    /// <summary>短暂显示一条提示（拖入重复项、热键无选中项、源已失效等），1.8 秒后自动消失。</summary>
    private void ShowToast(string message)
    {
        if (_toastTimer == null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            _toastTimer.Tick += (_, _) =>
            {
                Toast.Visibility = Visibility.Collapsed;
                _toastTimer.Stop();
            };
        }

        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();     // 连续触发时重置计时，不提前消失
        _toastTimer.Start();
    }

    // ---------- 外部控制 ----------

    /// <summary>设置变更后重新应用：重算布局（停靠侧 / 分区比例）并重建图标（尺寸）</summary>
    public void ApplySettings()
    {
        LayoutWindow();
        ClampItemsIntoCanvas();   // 宽度/图标尺寸变了，已摆放的图标要收回可视范围
        RenderItems();
    }

    public void SetExplorerForeground(bool foreground)
    {
        _explorerForeground = foreground;

        // 哪怕一个条目都没有也要显示：否则空库状态下根本接不住从资源管理器拖来的东西
        if (foreground)
        {
            // 隐藏→显示的瞬间：先补算隐藏期间的下沉（黑洞吸入 / Y 位移），
            // 再渲染。否则会"切回来一看全堆在底部"
            if (_store.Items.Count > 0)
            {
                if (AdvanceAll(DateTime.Now, out _)) _store.Save();
                ApplyPositions();
            }
            RenderItems();
            Show();
            _agingTimer.Stop();   // 显示期间不下沉——避免 _agingTimer 在拖动过程中覆盖 TrackSelfDrag 写的 Canvas 坐标（"跳一下"的根因）
            _armTimer.Start();
        }
        else
        {
            Disarm();
            Hide();
            _agingTimer.Start();   // 隐藏期间按真实时间下沉老化
            _armTimer.Stop();
        }
    }

    private void MaybeHide()
    {
        // 资源管理器仍在前台时不隐藏，否则马上又想拖新条目就接不住了
        if (_store.Items.Count == 0 && !_explorerForeground) Hide();
    }
}
