using System.Collections.Generic;

namespace Vector.Models;

public sealed class HotkeyCoexistenceSettings
{
    /// <summary>是否开启前台程序智能冲突避让（如微信中放行 Alt+S，浏览器中放行 Ctrl+1..9）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>全屏或演示（QUNS_BUSY）时自动避让本地与截图热键，保障全屏游戏/汇报不被干扰。</summary>
    public bool SuppressInFullscreen { get; set; } = true;

    /// <summary>完全避让（全部本地热键让路）的自定义进程名列表（不带路径，大小写不敏感，如 "game.exe"）。</summary>
    public List<string> CustomExclusionProcesses { get; set; } = new();
}
