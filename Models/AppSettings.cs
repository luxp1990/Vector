using System.Collections.ObjectModel;

namespace Vector.Models;

public class AppSettings
{
    public int Version { get; set; } = 4;
    public ObservableCollection<ShortcutConfig> LocalShortcuts { get; set; } = new();
    public string GlobalHotKey { get; set; } = "None";
    public string Theme { get; set; } = "Dark";

    // ✅ 1.0.1: UIPI 缓解 —— 持久化"以管理员身份启动"偏好
    public bool RunAsAdmin { get; set; } = false;

    /// <summary>快捷键功能总开关（与截图功能相互独立；持久化，重启恢复）。</summary>
    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>记录热键注册、WM_HOTKEY 分发和目标启动阶段；默认关闭。</summary>
    public bool HotkeyDiagnosticsEnabled { get; set; } = false;

    /// <summary>开机自启时额外等待秒数（0=不等）；仅带 --from-startup 启动时生效。</summary>
    public int StartupDelaySeconds { get; set; } = 0;
    public PinSettings Pin { get; set; } = new();

    /// <summary>快捷键与其他前台程序和平共处与智能避让设置。</summary>
    public HotkeyCoexistenceSettings Coexistence { get; set; } = new();
}
