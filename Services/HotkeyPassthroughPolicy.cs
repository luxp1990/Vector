using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vector.Models;

namespace Vector.Services;

/// <summary>
/// 纯函数决策引擎：根据当前前台进程、全屏状态与配置，
/// 计算当前应当避让（暂时注销以交还给前台软件）的快捷键签名集合。
/// </summary>
public static class HotkeyPassthroughPolicy
{
    private static readonly Dictionary<string, HashSet<string>> KnownAppConflicts = new(StringComparer.OrdinalIgnoreCase);

    static HotkeyPassthroughPolicy()
    {
        // 1. 即时通讯软件 (IM)：微信（含微信4.x Weixin.exe、小程序宿主 WeChatAppEx.exe）、QQ、钉钉、飞书等
        // 关键冲突：Alt+S(发消息)、Alt+A/Ctrl+Alt+A(截屏)、Ctrl+Alt+S(贴图/录屏)
        RegisterAppConflict(
            new[] { "wechat.exe", "weixin.exe", "wechatappex.exe", "qq.exe", "wxdump.exe", "dingtalk.exe", "feishu.exe", "lark.exe", "tim.exe" },
            new[] { "Alt+S", "Alt+A", "Ctrl+Alt+A", "Ctrl+Alt+S", "Ctrl+Alt+W" });

        // 2. 现代网页浏览器：Chrome、Edge、Firefox、Brave 等
        // 关键冲突：Ctrl+1..9(切换标签页)、Ctrl+T(新标签)、Ctrl+W(关标签)、Ctrl+Tab
        RegisterAppConflict(
            new[] { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "360chrome.exe", "sogouexplorer.exe", "qqbrowser.exe" },
            new[]
            {
                "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7", "Ctrl+8", "Ctrl+9",
                "Ctrl+T", "Ctrl+W", "Ctrl+Tab", "Ctrl+Shift+Tab", "Alt+Left", "Alt+Right"
            });

        // 3. 办公软件：Excel
        // 关键冲突：Ctrl+1(设置单元格格式)、Ctrl+Shift+1
        RegisterAppConflict(
            new[] { "excel.exe" },
            new[] { "Ctrl+1", "Ctrl+Shift+1" });

        // 4. 开发工具与 IDE：VS Code、Visual Studio、JetBrains 等
        // 关键冲突：Ctrl+Shift+E(资源管理器)、Ctrl+1..9(工具窗口)、Ctrl+Alt+L(代码格式化)
        RegisterAppConflict(
            new[] { "code.exe", "devenv.exe", "idea64.exe", "pycharm64.exe", "clion64.exe", "rider64.exe", "webstorm64.exe" },
            new[] { "Ctrl+Shift+E", "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+Alt+L", "Ctrl+Alt+S" });

        // 5. 工程设计与 CAD/三维软件：Siemens NX (ugraf.exe)、SolidWorks、AutoCAD、Catia、Creo 等
        // 关键冲突：Ctrl+1..9(视图/图层/快捷命令)、Ctrl+Shift+1..9
        RegisterAppConflict(
            new[] { "ugraf.exe", "sldworks.exe", "acad.exe", "cnext.exe", "inventor.exe", "xtop.exe", "creo.exe" },
            new[] { "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+Shift+1", "Ctrl+Shift+2", "Ctrl+Alt+T" });
    }

    private static void RegisterAppConflict(IEnumerable<string> processes, IEnumerable<string> chords)
    {
        var sigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chord in chords)
        {
            if (HotkeyService.TryGetHotkeySignature(chord, out string sig))
                sigs.Add(sig);
        }

        foreach (var proc in processes)
        {
            if (!KnownAppConflicts.TryGetValue(proc, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                KnownAppConflicts[proc] = set;
            }
            foreach (var s in sigs)
                set.Add(s);
        }
    }

    /// <summary>
    /// 标准化进程名称（仅保留小写文件名，如 "c:\path\WeChat.exe" -> "wechat.exe"）
    /// </summary>
    public static string NormalizeProcessName(string? rawPathOrName)
    {
        if (string.IsNullOrWhiteSpace(rawPathOrName))
            return string.Empty;

        string name = Path.GetFileName(rawPathOrName.Trim());
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";
        return name.ToLowerInvariant();
    }

    public static bool TryNormalizeSignature(string chord, out string signature)
    {
        return HotkeyService.TryGetHotkeySignature(chord, out signature);
    }

    /// <summary>
    /// 评估当前前台状态下需要让路（暂时注销）的快捷键签名集合。
    /// </summary>
    /// <param name="foregroundProcess">当前前台进程文件名，如 "wechat.exe"</param>
    /// <param name="isFullscreenOrBusy">当前系统是否处于全屏/忙碌态（QUNS_BUSY）</param>
    /// <param name="settings">和平共处与避让设置</param>
    /// <param name="activeChords">Vector 当前已配置的全部快捷键（包含本地与贴图快捷键）</param>
    /// <param name="globalHotkeySignature">Vector 全局开关热键签名或按键组合（绝对不参与避让）</param>
    /// <returns>应当暂时注销的快捷键签名集合，以及当前避让命中的摘要原因</returns>
    public static (HashSet<string> PassthroughSignatures, string Reason) Evaluate(
        string foregroundProcess,
        bool isFullscreenOrBusy,
        HotkeyCoexistenceSettings? settings,
        IEnumerable<string> activeChords,
        string? globalHotkeySignature)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string reason = string.Empty;

        if (settings == null || !settings.Enabled)
            return (result, reason);

        string proc = NormalizeProcessName(foregroundProcess);

        string? globalSig = null;
        if (!string.IsNullOrWhiteSpace(globalHotkeySignature))
        {
            if (HotkeyService.TryGetHotkeySignature(globalHotkeySignature, out string resolved))
                globalSig = resolved;
            else
                globalSig = globalHotkeySignature;
        }

        // 1. 全屏/演示/全屏游戏检测（QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE）
        // 关键防护：即使前台进程名由于安全/反作弊权限受限解析为空，只要处在全屏态就必须避让普通键！
        if (isFullscreenOrBusy && settings.SuppressInFullscreen && proc != "vector.exe")
        {
            foreach (var chord in activeChords)
            {
                if (HotkeyService.TryGetHotkeySignature(chord, out string sig)
                    && !string.Equals(sig, globalSig, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(sig);
                }
            }
            reason = string.IsNullOrEmpty(proc) ? "全屏/游戏状态避让" : $"全屏状态避让 ({proc})";
            return (result, reason);
        }

        if (string.IsNullOrEmpty(proc) || proc == "vector.exe")
            return (result, reason);

        // 2. 自定义完全排除名单检测
        bool isCustomExcluded = settings.CustomExclusionProcesses != null
            && settings.CustomExclusionProcesses.Any(p => string.Equals(NormalizeProcessName(p), proc, StringComparison.OrdinalIgnoreCase));

        if (isCustomExcluded)
        {
            foreach (var chord in activeChords)
            {
                if (HotkeyService.TryGetHotkeySignature(chord, out string sig)
                    && !string.Equals(sig, globalSig, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(sig);
                }
            }
            reason = $"自定义排除程序 ({proc})";
            return (result, reason);
        }

        // 3. 预置应用智能冲突避让（仅避让与该应用冲突的特定组合键）
        if (KnownAppConflicts.TryGetValue(proc, out var conflictSigs))
        {
            var hitChords = new List<string>();
            foreach (var chord in activeChords)
            {
                if (HotkeyService.TryGetHotkeySignature(chord, out string sig)
                    && conflictSigs.Contains(sig)
                    && !string.Equals(sig, globalSig, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(sig);
                    hitChords.Add(chord);
                }
            }
            if (result.Count > 0)
                reason = $"{proc} 冲突避让: {string.Join(", ", hitChords)}";
        }

        return (result, reason);
    }
}
