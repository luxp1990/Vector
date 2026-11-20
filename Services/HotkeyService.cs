using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Vector.Models;

namespace Vector.Services;

public class HotkeyService : IHotkeyService
{
    private const int MaxHotkeyId = 0xBFFF;

    /// <summary>
    /// ✅ 1.0.2: 符号键反向映射 —— UI 把 OemPlus 显示成 "="，但 System.Windows.Forms.Keys 枚举叫 "Oemplus"，
    /// Enum.TryParse("=", ...) 会静默失败，导致热键注册返回 false 但系统并没占用（无法排查）。
    /// 这里覆盖 13 个常见符号，确保 "Ctrl+=" 等用户友好写法能正确解析为底层 vk。
    /// </summary>
    private static readonly Dictionary<string, Keys> SymbolKeyMap = new(StringComparer.Ordinal)
    {
        ["="] = Keys.Oemplus,
        ["-"] = Keys.OemMinus,
        ["`"] = Keys.Oemtilde,
        ["["] = Keys.OemOpenBrackets,
        ["]"] = Keys.OemCloseBrackets,
        [";"] = Keys.OemSemicolon,
        ["'"] = Keys.OemQuotes,
        [","] = Keys.Oemcomma,
        ["."] = Keys.OemPeriod,
        ["/"] = Keys.OemQuestion,
        ["\\"] = Keys.OemBackslash,
        ["|"] = Keys.OemPipe,
    };

    private IntPtr _hwnd;
    private readonly Dictionary<int, ShortcutConfig> _hotkeyMap = new();
    private readonly HashSet<int> _registeredIds = new();
    private readonly Dictionary<int, int> _logicalToActualId = new();
    private readonly ISettingsService? _settingsService;
    private int _nextDynamicId = 10000;
    private volatile bool _diagnosticLogging;
    private int _globalHotkeyId;
    private string _globalHotkeySignature = string.Empty;
    private int _pinSnapshotId;
    private int _pinClipboardId;
    private string _pinSnapshotSignature = string.Empty;
    private string _pinClipboardSignature = string.Empty;
    private readonly HashSet<string> _passthroughSignatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ShortcutConfig> _desiredShortcuts = new();
    private string? _desiredSnapshotKey;
    private string? _desiredClipboardKey;
    private bool _pinDesired;

    public HotkeyService()
    {
    }

    public HotkeyService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void SetDiagnosticLogging(bool enabled) => _diagnosticLogging = enabled;

    public static bool TryGetHotkeySignature(string keyStr, out string signature)
    {
        signature = string.Empty;
        if (!ParseModifierKey(keyStr, out uint modifiers, out Keys key))
            return false;
        signature = $"{modifiers:X2}:{(uint)key:X2}";
        return true;
    }

    private void Log(string message, bool always = false)
    {
        if (!always && !_diagnosticLogging)
            return;
        if (_settingsService is not null)
            _ = _settingsService.LogAsync(message);
        else
            System.Diagnostics.Debug.WriteLine(message);
    }

    private bool RegisterNative(int id, uint modifiers, Keys key, string label)
    {
        if (RegisterHotKey(_hwnd, id, modifiers, (uint)key))
        {
            Log($"Hotkey registered: {label} id={id} chord={modifiers:X2}:{(uint)key:X2}");
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        Log($"Hotkey registration failed: {label} id={id} chord={modifiers:X2}:{(uint)key:X2} win32={error}", always: true);
        return false;
    }

    private int AllocateDynamicId()
    {
        for (int attempts = 0; attempts < MaxHotkeyId - 9999; attempts++)
        {
            if (_nextDynamicId < 10000 || _nextDynamicId > MaxHotkeyId)
                _nextDynamicId = 10000;
            int candidate = _nextDynamicId++;
            if (!_registeredIds.Contains(candidate)
                && candidate != _globalHotkeyId
                && candidate != _pinSnapshotId
                && candidate != _pinClipboardId)
                return candidate;
        }
        throw new InvalidOperationException("No free hotkey IDs remain.");
    }

    /// <summary>系统保留快捷键黑名单（大小写不敏感）</summary>
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // 编辑类（几乎所有 App 都用）
        "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y",
        "Ctrl+A", "Ctrl+S", "Ctrl+F", "Ctrl+H",
        // 窗口/标签类
        "Ctrl+W", "Ctrl+T", "Ctrl+N", "Ctrl+O", "Ctrl+P",
        "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+Shift+T",
        // 浏览器/IDE 常用
        "Ctrl+Shift+Delete", "Ctrl+Shift+Del", "Ctrl+L",
        "Ctrl+Shift+N", "Ctrl+Shift+W",
        "Ctrl+G",  // 转到/搜索行
        // F1-F12（Help/调试/多媒体）
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Ctrl+F1", "Ctrl+F2", "Ctrl+F3", "Ctrl+F4", "Ctrl+F5",
        "Ctrl+F6", "Ctrl+F7", "Ctrl+F8", "Ctrl+F9", "Ctrl+F10",
        "Ctrl+F11", "Ctrl+F12",
        "Shift+F1", "Shift+F2", "Shift+F3", "Shift+F4", "Shift+F5",
        "Shift+F6", "Shift+F7", "Shift+F8", "Shift+F9", "Shift+F10",
        "Shift+F11", "Shift+F12",
        "Alt+F1", "Alt+F2", "Alt+F3", "Alt+F4", "Alt+F5",
        "Alt+F6", "Alt+F7", "Alt+F8", "Alt+F9", "Alt+F10",
        "Alt+F11", "Alt+F12",
        // 导航/编辑键
        "Escape", "Tab", "Shift+Tab",
        "Ctrl+Escape", "Alt+Escape",
        "Ctrl+Tab", "Alt+Tab", "Alt+Shift+Tab",
        "Ctrl+Shift+Escape",  // 任务管理器
        "PrintScreen", "Ctrl+PrintScreen", "Alt+PrintScreen",
        "Insert", "Ctrl+Insert", "Alt+Insert",
        "Delete", "Ctrl+Delete", "Alt+Delete",
    };

    public event Action? ToggleListening;
    public event Action<ShortcutConfig>? ExecuteShortcut;
    public event Action<PinHotkeyAction>? PinHotkeyTriggered;

    public void Initialize(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    public void ProcessMessage(IntPtr wParam)
    {
        int id = wParam.ToInt32();
        Log($"WM_HOTKEY received: id={id}");
        if (_globalHotkeyId != 0 && id == _globalHotkeyId)
        {
            Log("WM_HOTKEY dispatched to global toggle");
            ToggleListening?.Invoke();
        }
        else if (id == _pinSnapshotId && id != 0)
        {
            Log("WM_HOTKEY dispatched to pin region capture");
            PinHotkeyTriggered?.Invoke(PinHotkeyAction.Snapshot);
        }
        else if (id == _pinClipboardId && id != 0)
        {
            Log("WM_HOTKEY dispatched to clipboard pin");
            PinHotkeyTriggered?.Invoke(PinHotkeyAction.Clipboard);
        }
        else if (_hotkeyMap.TryGetValue(id, out var cfg))
        {
            Log($"WM_HOTKEY dispatched: id={id} key={cfg.Key} target={cfg.Target}");
            ExecuteShortcut?.Invoke(cfg);
        }
        else
            Log($"WM_HOTKEY has no registered mapping: id={id}", always: true);
    }

    public bool RegisterGlobal(string keyStr)
    {
        // 空 / None = 关闭全局开关热键（仍算成功）
        if (string.IsNullOrWhiteSpace(keyStr)
            || string.Equals(keyStr.Trim(), "None", StringComparison.OrdinalIgnoreCase))
        {
            if (_globalHotkeyId != 0)
                Unregister(_globalHotkeyId);
            _globalHotkeyId = 0;
            _globalHotkeySignature = string.Empty;
            return true;
        }
        if (!ParseModifierKey(keyStr, out uint mods, out Keys key))
        {
            Log($"Global hotkey parse failed: key={keyStr}", always: true);
            return false;
        }
        if (!TryGetHotkeySignature(keyStr, out string nextSignature)) return false;
        if (_globalHotkeyId != 0
            && nextSignature == _globalHotkeySignature)
            return true;

        int replacementId = AllocateDynamicId();
        if (!RegisterNative(replacementId, mods, key, $"global:{keyStr}"))
            return false;

        int oldId = _globalHotkeyId;
        _globalHotkeyId = replacementId;
        _globalHotkeySignature = nextSignature;
        if (oldId != 0)
            Unregister(oldId);
        return true;
    }

    public void SetPassthroughSignatures(IEnumerable<string> signatures)
    {
        _passthroughSignatures.Clear();
        if (signatures != null)
        {
            foreach (var s in signatures)
            {
                if (!string.IsNullOrWhiteSpace(s))
                    _passthroughSignatures.Add(s);
            }
        }

        ReconcileLocals();
        ReconcilePin();
    }

    public void ResetNativeRegistrations()
    {
        if (_globalHotkeyId != 0)
        {
            Unregister(_globalHotkeyId);
            _globalHotkeyId = 0;
            _globalHotkeySignature = string.Empty;
        }

        if (_pinSnapshotId != 0)
        {
            Unregister(_pinSnapshotId);
            _pinSnapshotId = 0;
            _pinSnapshotSignature = string.Empty;
        }

        if (_pinClipboardId != 0)
        {
            Unregister(_pinClipboardId);
            _pinClipboardId = 0;
            _pinClipboardSignature = string.Empty;
        }

        foreach (var actId in _registeredIds.ToList())
        {
            Unregister(actId);
        }
        _registeredIds.Clear();
        _logicalToActualId.Clear();
        _hotkeyMap.Clear();

        Log("Native hotkey registrations reset for resume/reconnect", always: true);
    }

    public List<string> RegisterAll(IEnumerable<ShortcutConfig> shortcuts)
    {
        _desiredShortcuts.Clear();
        if (shortcuts != null)
            _desiredShortcuts.AddRange(shortcuts);

        return ReconcileLocals();
    }

    public void UnregisterAll()
    {
        _desiredShortcuts.Clear();
        ReconcileLocals();
    }

    public bool RegisterSingle(ShortcutConfig cfg, int id)
    {
        if (!ParseModifierKey(cfg.Key, out uint mods, out Keys key))
        {
            Log($"Hotkey parse failed: key={cfg.Key}", always: true);
            return false;
        }

        string newSig = $"{mods:X2}:{(uint)key:X2}";

        int index = id - 1;
        while (_desiredShortcuts.Count <= index)
            _desiredShortcuts.Add(cfg);
        _desiredShortcuts[index] = cfg;

        if (_passthroughSignatures.Contains(newSig))
        {
            if (_logicalToActualId.TryGetValue(id, out int oldActId))
            {
                Unregister(oldActId);
                _registeredIds.Remove(oldActId);
                _hotkeyMap.Remove(oldActId);
                _logicalToActualId.Remove(id);
            }
            Log($"Hotkey stored in desired list but surrendered for coexistence: logicalId={id} key={cfg.Key}");
            return true;
        }

        int? oldActualId = _logicalToActualId.TryGetValue(id, out int mappedId)
            ? mappedId
            : null;
        if (oldActualId is int currentId
            && _hotkeyMap.TryGetValue(currentId, out ShortcutConfig? currentCfg)
            && TryGetHotkeySignature(currentCfg.Key, out string oldSignature)
            && oldSignature == newSig)
        {
            _hotkeyMap[currentId] = cfg;
            Log($"Hotkey mapping updated without re-registering: logicalId={id} key={cfg.Key}");
            return true;
        }

        int actualId = AllocateDynamicId();
        if (!RegisterNative(actualId, mods, key, $"local:{cfg.Key}"))
            return false;

        _hotkeyMap[actualId] = cfg;
        _registeredIds.Add(actualId);
        if (oldActualId is int previousId)
        {
            Unregister(previousId);
            _registeredIds.Remove(previousId);
            _hotkeyMap.Remove(previousId);
        }
        _logicalToActualId[id] = actualId;
        return true;
    }

    private List<string> ReconcileLocals()
    {
        var failedKeys = new List<string>();
        var desiredLogicalIds = new HashSet<int>();

        for (int i = 0; i < _desiredShortcuts.Count; i++)
        {
            int logicalId = i + 1;
            desiredLogicalIds.Add(logicalId);
            ShortcutConfig cfg = _desiredShortcuts[i];

            if (!ParseModifierKey(cfg.Key, out uint mods, out Keys key))
            {
                Log($"Hotkey parse failed: key={cfg.Key}", always: true);
                failedKeys.Add(cfg.Key);
                continue;
            }

            string sig = $"{mods:X2}:{(uint)key:X2}";
            bool isPassthrough = _passthroughSignatures.Contains(sig);

            int? currentActualId = _logicalToActualId.TryGetValue(logicalId, out int mappedId)
                ? mappedId
                : null;

            if (isPassthrough)
            {
                if (currentActualId is int actId && _registeredIds.Contains(actId))
                {
                    Unregister(actId);
                    _registeredIds.Remove(actId);
                    _hotkeyMap.Remove(actId);
                    _logicalToActualId.Remove(logicalId);
                    Log($"Hotkey surrendered for coexistence: logicalId={logicalId} key={cfg.Key}");
                }
            }
            else
            {
                if (currentActualId is int actId
                    && _hotkeyMap.TryGetValue(actId, out ShortcutConfig? currentCfg)
                    && TryGetHotkeySignature(currentCfg.Key, out string oldSignature)
                    && oldSignature == sig)
                {
                    _hotkeyMap[actId] = cfg;
                }
                else
                {
                    int newActualId = AllocateDynamicId();
                    if (RegisterNative(newActualId, mods, key, $"local:{cfg.Key}"))
                    {
                        _hotkeyMap[newActualId] = cfg;
                        _registeredIds.Add(newActualId);
                        if (currentActualId is int prevId)
                        {
                            Unregister(prevId);
                            _registeredIds.Remove(prevId);
                            _hotkeyMap.Remove(prevId);
                        }
                        else
                        {
                            Log($"Hotkey restored from coexistence: logicalId={logicalId} key={cfg.Key}");
                        }
                        _logicalToActualId[logicalId] = newActualId;
                    }
                    else
                    {
                        failedKeys.Add(cfg.Key);
                    }
                }
            }
        }

        var removedLogicalIds = _logicalToActualId.Keys.Where(k => !desiredLogicalIds.Contains(k)).ToList();
        foreach (var remId in removedLogicalIds)
        {
            if (_logicalToActualId.TryGetValue(remId, out int actId))
            {
                Unregister(actId);
                _registeredIds.Remove(actId);
                _hotkeyMap.Remove(actId);
                _logicalToActualId.Remove(remId);
            }
        }

        return failedKeys;
    }

    public bool RegisterPinHotkeys(string snapshotKey, string clipboardKey)
    {
        _desiredSnapshotKey = snapshotKey;
        _desiredClipboardKey = clipboardKey;
        _pinDesired = true;
        return ReconcilePin();
    }

    public void UnregisterPinHotkeys()
    {
        _desiredSnapshotKey = null;
        _desiredClipboardKey = null;
        _pinDesired = false;
        ReconcilePin();
    }

    private bool ReconcilePin()
    {
        if (!_pinDesired || string.IsNullOrWhiteSpace(_desiredSnapshotKey) || string.IsNullOrWhiteSpace(_desiredClipboardKey))
        {
            if (_pinSnapshotId != 0)
            {
                Unregister(_pinSnapshotId);
                _registeredIds.Remove(_pinSnapshotId);
                _pinSnapshotId = 0;
                _pinSnapshotSignature = string.Empty;
            }
            if (_pinClipboardId != 0)
            {
                Unregister(_pinClipboardId);
                _registeredIds.Remove(_pinClipboardId);
                _pinClipboardId = 0;
                _pinClipboardSignature = string.Empty;
            }
            return true;
        }

        string snapshotText = _desiredSnapshotKey.Trim();
        string clipboardText = _desiredClipboardKey.Trim();
        if (string.Equals(snapshotText, clipboardText, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!ParseModifierKey(snapshotText, out uint snapshotMods, out Keys snapshot)
            || !ParseModifierKey(clipboardText, out uint clipboardMods, out Keys clipboard))
        {
            Log($"Pin hotkey parse failed: snapshot={snapshotText}; clipboard={clipboardText}", always: true);
            return false;
        }

        string snapshotSignature = $"{snapshotMods:X2}:{(uint)snapshot:X2}";
        string clipboardSignature = $"{clipboardMods:X2}:{(uint)clipboard:X2}";

        bool snapshotPassthrough = _passthroughSignatures.Contains(snapshotSignature);
        bool clipboardPassthrough = _passthroughSignatures.Contains(clipboardSignature);

        if (snapshotPassthrough)
        {
            if (_pinSnapshotId != 0)
            {
                Unregister(_pinSnapshotId);
                _registeredIds.Remove(_pinSnapshotId);
                _pinSnapshotId = 0;
                _pinSnapshotSignature = string.Empty;
                Log($"Pin snapshot hotkey surrendered: key={snapshotText}");
            }
        }
        else
        {
            if (_pinSnapshotId == 0 || _pinSnapshotSignature != snapshotSignature)
            {
                int newId = AllocateDynamicId();
                if (RegisterNative(newId, snapshotMods, snapshot, $"pin-region:{snapshotText}"))
                {
                    if (_pinSnapshotId != 0) { Unregister(_pinSnapshotId); _registeredIds.Remove(_pinSnapshotId); }
                    _pinSnapshotId = newId;
                    _registeredIds.Add(newId);
                    _pinSnapshotSignature = snapshotSignature;
                }
            }
        }

        if (clipboardPassthrough)
        {
            if (_pinClipboardId != 0)
            {
                Unregister(_pinClipboardId);
                _registeredIds.Remove(_pinClipboardId);
                _pinClipboardId = 0;
                _pinClipboardSignature = string.Empty;
                Log($"Pin clipboard hotkey surrendered: key={clipboardText}");
            }
        }
        else
        {
            if (_pinClipboardId == 0 || _pinClipboardSignature != clipboardSignature)
            {
                int newId = AllocateDynamicId();
                if (RegisterNative(newId, clipboardMods, clipboard, $"pin-clipboard:{clipboardText}"))
                {
                    if (_pinClipboardId != 0) { Unregister(_pinClipboardId); _registeredIds.Remove(_pinClipboardId); }
                    _pinClipboardId = newId;
                    _registeredIds.Add(newId);
                    _pinClipboardSignature = clipboardSignature;
                }
            }
        }

        return true;
    }

    // 注: Win 键组合在 Windows 上大部分被系统保留（如 Win+L 锁屏、Win+E 资源管理器），
    // RegisterHotKey 注册 Win+其他键 通常会失败（返回 false），但我们仍把解析逻辑补全，
    // 失败时上层根据 RegisterGlobal/RegisterSingle 返回 false 给用户友好提示。
    private static bool ParseModifierKey(string s, out uint mods, out System.Windows.Forms.Keys key)
    {
        mods = 0;
        key = System.Windows.Forms.Keys.None;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            var part = p.Trim();
            if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= 0x0002;       // MOD_CONTROL
            else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase)) mods |= 0x0001;  // MOD_ALT
            else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase)) mods |= 0x0004; // MOD_SHIFT
            else if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase)) mods |= 0x0008;    // MOD_WIN
            else if (MapStringToKey(part, out var k)) key = k;
            else return false;
        }
        return key != System.Windows.Forms.Keys.None;
    }

    /// <summary>✅ 1.0.2: 把 UI 显示字符串（如 "="）映射到 System.Windows.Forms.Keys 枚举值</summary>
    private static bool MapStringToKey(string str, out Keys key)
    {
        // 1. 先查符号映射（UI 友好字符 → 底层 vk）
        if (SymbolKeyMap.TryGetValue(str, out key)) return true;

        // 2. ✅ 1.0.3: 数字字符 "0"-"9" → Keys.D0-D9
        // （不然 Enum.TryParse("1") 会解析为 Keys.LButton 鼠标左键，导致 Ctrl+Shift+1 注册成 Ctrl+Shift+LButton，按不出）
        if (str.Length == 1 && str[0] >= '0' && str[0] <= '9')
        {
            key = (Keys)(Keys.D0 + (str[0] - '0'));
            return true;
        }

        // ✅ 1.0.5: 拒绝多字符纯数字字符串（"10"、"100" 之类）
        // Enum.TryParse 会把它们解析为对应数值的 Keys 值（如 "100" → NumPad4），
        // 但这不是用户想要的合法热键标识。
        bool allDigits = str.Length > 1;
        foreach (var c in str)
        {
            if (c < '0' || c > '9') { allDigits = false; break; }
        }
        if (allDigits)
        {
            key = Keys.None;
            return false;
        }

        // 3. 再走枚举解析（覆盖 F1-F12 / 字母 / D0-D9 / NumPad0-9 等）
        if (Enum.TryParse(str, true, out Keys k))
        {
            key = k;
            return true;
        }

        key = Keys.None;
        return false;
    }

    private void Unregister(int id)
    {
        try
        {
            if (!UnregisterHotKey(_hwnd, id))
            {
                int error = Marshal.GetLastWin32Error();
                Log($"Hotkey unregistration failed: id={id} win32={error}", always: true);
            }
        }
        catch { }
    }

    public string? GetHotkeyRejectReason(string keyStr)
    {
        if (string.IsNullOrWhiteSpace(keyStr)) return "快捷键不能为空";

        // 先精确匹配黑名单
        if (ReservedKeys.Contains(keyStr))
            return $"「{keyStr}」是系统保留快捷键，请选择其他组合";

        // 再做语义兼容：去掉两端的空格再匹配
        var trimmed = keyStr.Trim();
        if (ReservedKeys.Contains(trimmed))
            return $"「{trimmed}」是系统保留快捷键，请选择其他组合";

        if (!TryGetHotkeySignature(trimmed, out _))
            return "快捷键格式无效，请重新录入组合键";

        return null; // 可用
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
