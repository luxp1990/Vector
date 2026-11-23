using System;
using System.Collections.Generic;
using Vector.Models;

namespace Vector.Services;

public enum PinHotkeyAction
{
    Snapshot,
    Clipboard
}

public interface IHotkeyService
{
    event Action? ToggleListening;
    event Action<ShortcutConfig>? ExecuteShortcut;
    event Action<PinHotkeyAction>? PinHotkeyTriggered;

    void Initialize(IntPtr hwnd);
    void ProcessMessage(IntPtr wParam);
    bool RegisterGlobal(string keyStr);
    System.Collections.Generic.List<string> RegisterAll(System.Collections.Generic.IEnumerable<ShortcutConfig> shortcuts);
    void UnregisterAll();
    bool RegisterSingle(ShortcutConfig cfg, int id);
    bool RegisterPinHotkeys(string snapshotKey, string clipboardKey);
    void UnregisterPinHotkeys();
    int LastRegisterError { get; }
    void SetDiagnosticLogging(bool enabled);

    /// <summary>
    /// 设置当前避让放行（不持有）的快捷键签名集合，并执行差值重对齐（Reconcile）：
    /// 仅注销需要避让的项，仅重新注册恢复生效的项，未发生变化的项保持原样无闪烁。
    /// </summary>
    void SetPassthroughSignatures(IEnumerable<string> signatures);

    /// <summary>
    /// 清除所有 Win32 本地和全局键的已注册状态缓存，以便在锁屏解锁/系统休眠唤醒后
    /// 强制向操作系统重新发起 RegisterHotKey 系统调用，防止增量对齐误判为已注册而跳过。
    /// </summary>
    void ResetNativeRegistrations();


    /// <summary>
    /// 检查快捷键是否被系统保留，返回 null 表示可用，
    /// 返回具体原因字符串表示被禁止（如 "系统保留快捷键"）。
    /// </summary>
    string? GetHotkeyRejectReason(string keyStr);
}
