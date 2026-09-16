using System;
using Microsoft.Win32;

namespace DropBridge.Services;

/// <summary>
/// 开机自启：写 HKCU 的 Run 键。
/// 走 HKCU 而不是 HKLM —— 不需要管理员权限，也不会影响机器上其他账号。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DropBridge";

    /// <summary>按给定开关写入或删除自启项。失败静默（权限/策略限制时不该拖垮主流程）。</summary>
    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;

            if (enable)
            {
                // Environment.ProcessPath 拿的是当前 exe 真实路径，
                // 单文件发布后也正确，不需要额外处理
                string exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);   // false = 不存在时也不抛
            }
        }
        catch
        {
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            string v = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(v);
        }
        catch
        {
            return false;
        }
    }
}
