using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Vector.Services;

/// <summary>开机自启：HKCU\Software\Microsoft\Windows\CurrentVersion\Run</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Vector";
    public const string FromStartupFlag = "--from-startup";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStart] 读取失败: {ex.Message}");
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return false;
                // 带标记，便于启动时识别并应用 StartupDelaySeconds
                key.SetValue(ValueName, $"\"{exe}\" {FromStartupFlag}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStart] 写入失败: {ex.Message}");
            return false;
        }
    }

    public static bool IsFromStartup(string[] args)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, FromStartupFlag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
