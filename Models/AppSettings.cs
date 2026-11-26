using System.Collections.ObjectModel;

namespace Vector.Models;

public class AppSettings
{
    public int Version { get; set; } = 4;
    public ObservableCollection<ShortcutConfig> LocalShortcuts { get; set; } = new();
    public string GlobalHotKey { get; set; } = "None";
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "zh-CN";

    // ✅ 1.0.1: UIPI 缓解 —— 持久化"以管理员身份启动"偏好
    public bool RunAsAdmin { get; set; } = false;

    /// <summary>快捷键功能总开关（与截图功能相互独立；持久化，重启恢复）。</summary>
    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>记录热键注册、WM_HOTKEY 分发和目标启动阶段；默认关闭。</summary>
    public bool HotkeyDiagnosticsEnabled { get; set; } = false;

    /// <summary>是否启用托盘进程内只读 MCP 命名管道服务端（Vector.Mcp）；默认关闭。</summary>
    public bool McpEnabled { get; set; } = false;

    /// <summary>是否允许 MCP 写操作工具（add/update/delete/set_listening/import_config）；默认关闭。</summary>
    public bool McpAllowWrites { get; set; } = false;

    /// <summary>是否允许 MCP 高危执行工具（execute_shortcut）；默认关闭。</summary>
    public bool McpAllowExecute { get; set; } = false;

    /// <summary>MCP 写/执行工具调用校验令牌；默认为空（空时拒绝一切写/执行调用）。</summary>
    public string McpToken { get; set; } = string.Empty;

    /// <summary>开机自启时额外等待秒数（0=不等）；仅带 --from-startup 启动时生效。</summary>
    public int StartupDelaySeconds { get; set; } = 0;
    public PinSettings Pin { get; set; } = new();

    /// <summary>快捷键与其他前台程序和平共处与智能避让设置。</summary>
    public HotkeyCoexistenceSettings Coexistence { get; set; } = new();
}
