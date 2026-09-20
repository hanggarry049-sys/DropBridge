using System;
using System.Windows;
using System.Windows.Controls;
using DropBridge.Models;
using DropBridge.Services;

namespace DropBridge;

/// <summary>
/// 设置窗口。刻意做成最朴素的形式：控件在 C# 里逐个读写，
/// 不引入 MVVM / 数据绑定 / 样式，减少出错面。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _s = settings;
        FillFrom(settings);
    }

    private void FillFrom(AppSettings s)
    {
        SelectByTag(DockBox, s.DockSide);
        SelectByTag(SizeBox, s.IconSize.ToString());
        SinkBox.Text = s.SinkSpeedPerHour.ToString("0.#");
        SelectNearestFactor(OfflineBox, s.OfflineSinkFactor);
        NoAgeBox.Text = (s.NoAgeRatio * 100).ToString("0.#");
        WidthBox.Text = (s.DockWidthRatio * 100).ToString("0.#");
        AutoStartCheck.IsChecked = s.AutoStart;
        PauseCheck.IsChecked = s.PauseAging;
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (object o in box.Items)
        {
            if (o is ComboBoxItem ci && Equals(ci.Tag, tag))
            {
                box.SelectedItem = ci;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    /// <summary>
    /// 按数值就近选中。用「最近一档」而不是精确匹配，是为了兜住手工把
    /// settings.json 改成 0.35 这类中间值的情况——否则会静默跳到第一项，
    /// 看起来像设置被改掉了。
    /// </summary>
    private static void SelectNearestFactor(ComboBox box, double value)
    {
        ComboBoxItem best = null;
        double bestDiff = double.MaxValue;

        foreach (object o in box.Items)
        {
            if (o is not ComboBoxItem ci) continue;
            if (!double.TryParse(ci.Tag?.ToString(), out double tag)) continue;

            double diff = Math.Abs(tag - value);
            if (diff < bestDiff) { bestDiff = diff; best = ci; }
        }

        if (best != null) box.SelectedItem = best;
        else if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _s.DockSide = SelectedTag(DockBox) ?? "Left";

        // 数值项解析失败就保持原值，别让一次误输入把速度写成 0 或 NaN
        if (double.TryParse(SinkBox.Text, out double sp))
            _s.SinkSpeedPerHour = Math.Clamp(sp, 0, 200);

        if (double.TryParse(SelectedTag(OfflineBox), out double off))
            _s.OfflineSinkFactor = Math.Clamp(off, 0, 1);

        if (double.TryParse(NoAgeBox.Text, out double pct))
            _s.NoAgeRatio = Math.Clamp(pct / 100.0, 0, 0.5);

        if (double.TryParse(WidthBox.Text, out double wpct))
            _s.DockWidthRatio = Math.Clamp(wpct / 100.0, 0.05, 0.40);

        if (int.TryParse(SelectedTag(SizeBox), out int sz))
            _s.IconSize = sz;

        _s.AutoStart = AutoStartCheck.IsChecked == true;
        _s.PauseAging = PauseCheck.IsChecked == true;

        _s.Save();
        AutoStart.Apply(_s.AutoStart);

        DialogResult = true;   // 会自动关闭窗口
    }
}
