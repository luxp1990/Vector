using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Vector.Services;

public class FileTypeDetector : IFileTypeDetector
{
    private readonly ISettingsService _settingsService;

    public FileTypeDetector(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string Detect(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "程序";
        target = target.Trim();

        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return "网页";

        // ✅ 1.0.5: .lnk 快捷方式 → 解开看真实目标再判断类型
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ResolveShortcut(target);
            if (!string.Equals(resolved, target, StringComparison.OrdinalIgnoreCase))
                return Detect(resolved);
        }

        // ✅ 1.0.5: .url Internet 快捷方式 → 读 URL= 行判断为网页
        if (target.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var url = ResolveUrlShortcut(target);
            if (!string.IsNullOrEmpty(url))
                return "网页";
        }

        try
        {
            if (Directory.Exists(target)) return "文件夹";
            if (File.Exists(target))
            {
                string ext = Path.GetExtension(target).ToLowerInvariant();
                if (ext == ".exe" || ext == ".bat" || ext == ".cmd" ||
                    ext == ".ps1" || ext == ".vbs")
                    return "程序";
                return "文件";
            }
        }
        catch (Exception ex)
        {
            // ✅ 1.0.1: 检测失败时留个 Debug 痕迹，便于排查（可能是权限/路径过长）
            Debug.WriteLine($"[FileTypeDetector] 检测失败 target={target}: {ex.GetType().Name}: {ex.Message}");
        }

        return "程序";
    }

    public string ResolveShortcut(string lnkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                // ✅ 1.0.8: 与其他日志调用保持一致，fire-and-forget
                _ = _settingsService.LogAsync($"[ResolveShortcut] WScript.Shell COM 不可用，返回原始路径");
                return lnkPath;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                string? targetPath = shortcut.TargetPath;
                string? workingDir = shortcut.WorkingDirectory;
                string? arguments = shortcut.Arguments;

                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);

                if (string.IsNullOrWhiteSpace(targetPath))
                    return lnkPath;

                // ✅ 1.0.5: 如果 .lnk 有 Arguments，把它们拼回去（不然像 "D:\app.exe" --foo 这类带参快捷方式丢参）
                // 调用方若需要分字段，请用 ResolveShortcutDetails
                if (!string.IsNullOrWhiteSpace(arguments))
                    return $"{targetPath.Trim('"')} {arguments}".Trim();

                return targetPath!;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            // ✅ 1.0.5: 之前 .Wait() 可能在 UI 线程死锁，现在 fire-and-forget
            _ = _settingsService.LogAsync($"解析快捷方式失败 [{lnkPath}]: {ex.Message}");
            return lnkPath;
        }
    }

    /// <summary>解析 .lnk，分别返回目标 / 参数 / 工作目录（供表单分字段填写）。</summary>
    public (string Target, string Arguments, string WorkingDirectory) ResolveShortcutDetails(string lnkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return (lnkPath, "", "");

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                string targetPath = ((string?)shortcut.TargetPath)?.Trim('"') ?? "";
                string arguments = ((string?)shortcut.Arguments)?.Trim() ?? "";
                string workingDir = ((string?)shortcut.WorkingDirectory)?.Trim() ?? "";
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);

                if (string.IsNullOrWhiteSpace(targetPath))
                    return (lnkPath, "", "");
                return (targetPath, arguments, workingDir);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            _ = _settingsService.LogAsync($"解析快捷方式详情失败 [{lnkPath}]: {ex.Message}");
            return (lnkPath, "", "");
        }
    }

    /// <summary>
    /// ✅ 1.0.5: 解析 .url Internet 快捷方式（INI 格式，从 [InternetShortcut] 段读 URL= 行）
    /// </summary>
    public string ResolveUrlShortcut(string urlPath)
    {
        try
        {
            if (!File.Exists(urlPath)) return string.Empty;

            // .url 是 UTF-16 LE BOM + INI 格式，简单逐行扫描
            foreach (var line in File.ReadAllLines(urlPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring(4).Trim();
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            _ = _settingsService.LogAsync($"解析 .url 失败 [{urlPath}]: {ex.Message}");
            return string.Empty;
        }
    }
}