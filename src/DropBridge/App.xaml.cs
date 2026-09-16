using System;
using System.Diagnostics;
using System.Threading;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using DropBridge.Models;
using DropBridge.Services;

namespace DropBridge;

// 显式写全，避免与 System.Windows.Forms.Application 歧义
public partial class App : System.Windows.Application
{
    private const string MutexName = "DropBridge_SingleInstance_9F3A";

    private Mutex _mutex;
    private NotifyIcon _tray;
    private MainWindow _main;
    private ShellWatcher _watcher;
    private AppSettings _settings;
    private ItemStore _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 托盘常驻：主窗口始终是隐藏的，关闭它不该结束进程
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 单实例
        _mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        // 清理长期未命中的图标缓存，避免 icons/ 无限膨胀。
        // 丢线程池，别把启动路径堵在文件 IO 上。
        ThreadPool.QueueUserWorkItem(_ => IconProvider.PruneIconCache());

        _settings = AppSettings.Load();
        _store = ItemStore.Load();
        _main = new MainWindow(_store, _settings);

        _watcher = new ShellWatcher();
        _watcher.ForegroundChanged += fg => _main.SetExplorerForeground(fg);
        _watcher.Start();

        BuildTray();
    }

    /// <summary>
    /// 托盘图标：从嵌入的 app.ico 读，而不是 SystemIcons.Application 那个系统默认图标。
    /// 走 pack:// 资源流而不是 Icon.ExtractAssociatedIcon(ProcessPath)——后者在单文件发布时
    /// 拿不到真实的 exe 路径，会失败。
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            // 必须小写 resources/：MSBuild 会把嵌入资源名规范成全小写（g.resources 里的
            // 实际条目是 "resources/app.ico"），写成 Resources/ 可能匹配不上。
            var uri = new Uri("pack://application:,,,/resources/app.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri).Stream;
            return new Icon(stream);
        }
        catch
        {
            // 资源缺失或损坏时退回系统图标：托盘图标不对不该让整个应用起不来
            return SystemIcons.Application;
        }
    }

    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "DropBridge",
            Visible = true
        };

        var menu = new ContextMenuStrip();

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => OpenSettings();

        var openDir = new ToolStripMenuItem("打开数据目录");
        openDir.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", ItemStore.DataDir)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        };

        var pause = new ToolStripMenuItem("暂停下沉") { CheckOnClick = true };
        pause.Checked = _settings.PauseAging;
        pause.CheckedChanged += (_, _) =>
        {
            _settings.PauseAging = pause.Checked;
            _settings.Save();
        };

        var tidy = new ToolStripMenuItem("整理排列");
        tidy.Click += (_, _) => _main.TidyAll();

        var clear = new ToolStripMenuItem("清空全部图标");
        clear.Click += (_, _) => ClearAllItems();

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => Shutdown();

        menu.Items.Add(settingsItem);
        menu.Items.Add(openDir);
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(tidy);
        menu.Items.Add(clear);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _tray.ContextMenuStrip = menu;

        // 双击托盘图标直接开设置，省得每次右键
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings);
        // 故意不设 Owner：主窗口是隐藏的 topmost 浮层，挂上去会让设置窗口行为怪异
        if (win.ShowDialog() == true) _main.ApplySettings();
    }

    private void ClearAllItems()
    {
        if (_store.Items.Count == 0) return;

        var r = System.Windows.MessageBox.Show(
            $"确定清空全部 {_store.Items.Count} 个悬浮图标？\n（只移除桥接，不动磁盘上的文件夹）",
            "DropBridge",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (r != MessageBoxResult.Yes) return;

        _store.Items.Clear();
        _store.Save();
        _main.RenderItems();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
