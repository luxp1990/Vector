using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vector.Models;

namespace Vector.Services;

public class ExecutorService : IExecutorService
{
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly ISettingsService _settingsService;
    private readonly IFileTypeDetector _fileTypeDetector;
    private volatile bool _diagnosticLogging;

    /// <summary>✅ 1.0.1: 脚本执行失败事件 —— UI 订阅后弹气泡通知用户</summary>
    public event Action<string, string, int?>? ExecuteFailed;  // (keyName, reason, exitCode)

    public ExecutorService(ISettingsService settingsService, IFileTypeDetector fileTypeDetector)
    {
        _settingsService = settingsService;
        _fileTypeDetector = fileTypeDetector;
    }

    public void SetDiagnosticLogging(bool enabled) => _diagnosticLogging = enabled;

    private void LogDiagnostic(string message)
    {
        if (_diagnosticLogging)
            _ = _settingsService.LogAsync(message);
    }

    public void Execute(ShortcutConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Target))
        {
            LogDiagnostic($"Shortcut execution skipped: key={cfg.Key}; target is empty");
            return;
        }
        LogDiagnostic($"Shortcut execution started: key={cfg.Key}; target={cfg.Target}");
        Task.Run(() => ExecuteCore(cfg));
    }

    private async Task ExecuteCore(ShortcutConfig cfg)
    {
        try
        {
            string actualTarget = cfg.Target;
            string fieldArgs = cfg.Arguments?.Trim() ?? "";
            string fieldWorkDir = cfg.WorkingDirectory?.Trim() ?? "";

            if (actualTarget.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var (t, a, w) = _fileTypeDetector.ResolveShortcutDetails(actualTarget);
                if (!string.IsNullOrWhiteSpace(t))
                    actualTarget = t;
                if (string.IsNullOrEmpty(fieldArgs) && !string.IsNullOrEmpty(a))
                    fieldArgs = a;
                if (string.IsNullOrEmpty(fieldWorkDir) && !string.IsNullOrEmpty(w))
                    fieldWorkDir = w;
            }
            else if (actualTarget.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = _fileTypeDetector.ResolveUrlShortcut(actualTarget);
                if (!string.IsNullOrEmpty(resolved))
                    actualTarget = resolved;
            }

            string fileName;
            string arguments;
            if (cfg.Type == ShortcutTypes.Program)
            {
                (fileName, var parsedArgs) = ParseCommandLine(actualTarget);
                // 独立参数字段优先；否则沿用从 Target 解析出的参数（兼容旧配置）
                arguments = !string.IsNullOrEmpty(fieldArgs) ? fieldArgs : parsedArgs;
            }
            else
            {
                fileName = actualTarget;
                arguments = fieldArgs;
            }

            var targetLower = fileName.ToLowerInvariant();
            bool isScript = (cfg.Type == ShortcutTypes.Program || cfg.Type == ShortcutTypes.File) &&
                            (targetLower.EndsWith(".bat") || targetLower.EndsWith(".cmd") ||
                             targetLower.EndsWith(".ps1") || targetLower.EndsWith(".vbs"));

            // 运行策略（仅非脚本「程序」）
            if (cfg.Type == ShortcutTypes.Program && !isScript
                && TryApplyRunStrategy(RunStrategies.Normalize(cfg.RunStrategy), fileName))
            {
                LogDiagnostic($"Shortcut execution handled by run strategy: key={cfg.Key}; strategy={RunStrategies.Normalize(cfg.RunStrategy)}");
                return;
            }

            var startInfo = new ProcessStartInfo();

            if (isScript)
            {
                string scriptArgs = string.IsNullOrEmpty(arguments) ? "" : " " + arguments;

                if (targetLower.EndsWith(".ps1"))
                {
                    startInfo.FileName = "powershell.exe";
                    startInfo.Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{fileName}\"{scriptArgs}";
                }
                else if (targetLower.EndsWith(".vbs"))
                {
                    startInfo.FileName = "cscript.exe";
                    startInfo.Arguments = $"//nologo \"{fileName}\"{scriptArgs}";
                }
                else
                {
                    startInfo.FileName = "cmd.exe";
                    startInfo.Arguments = $"/c \"{fileName}\"{scriptArgs}";
                }

                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardError = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.UseShellExecute = false;
            }
            else
            {
                startInfo.FileName = fileName;
                startInfo.UseShellExecute = true;
                if (!string.IsNullOrEmpty(arguments))
                    startInfo.Arguments = arguments;
            }

            // 工作目录：显式字段优先，否则程序/文件用所在目录
            if (!string.IsNullOrEmpty(fieldWorkDir) && Directory.Exists(fieldWorkDir))
            {
                startInfo.WorkingDirectory = fieldWorkDir;
            }
            else if ((cfg.Type == ShortcutTypes.Program || cfg.Type == ShortcutTypes.File) && Path.IsPathRooted(fileName))
            {
                string? dir = Path.GetDirectoryName(fileName);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    startInfo.WorkingDirectory = dir;
            }

            if (cfg.Type == ShortcutTypes.Folder && Directory.Exists(fileName))
            {
                startInfo.FileName = "explorer.exe";
                startInfo.Arguments = $"\"{fileName}\"";
                startInfo.UseShellExecute = false;
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                NotifyFailed(cfg, "进程启动失败（返回 null）", null);
                return;
            }
            LogDiagnostic($"Shortcut target started: key={cfg.Key}; processId={process.Id}; file={fileName}");

            if (isScript)
            {
                var stderrTask = process.StandardError.ReadToEndAsync();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                var stderr = await stderrTask;
                var stdout = await stdoutTask;
                var exitCode = process.ExitCode;

                if (exitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
                {
                    var reason = !string.IsNullOrWhiteSpace(stderr)
                        ? $"退出码 {exitCode}，错误输出：{stderr.Trim()}"
                        : $"退出码 {exitCode}（非 0）";
                    NotifyFailed(cfg, reason, exitCode);
                }
                else
                {
                    LogDiagnostic($"Shortcut script completed: key={cfg.Key}; exitCode={exitCode}");
                    if (!string.IsNullOrWhiteSpace(stdout))
                        await _settingsService.LogAsync($"脚本 {cfg.Key} stdout: {stdout.Trim()}");
                }
            }
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync($"执行失败: {cfg.Target} - {ex.Message}");
            NotifyFailed(cfg, ex.Message, null);
        }
    }

    /// <summary>
    /// 按策略处理已存在进程。返回 true 表示已处理完毕（无需再启动）。
    /// New 始终返回 false；Activate/Toggle 找到窗口则处理并返回 true。
    /// </summary>
    private static bool TryApplyRunStrategy(string strategy, string fileName)
    {
        if (strategy == RunStrategies.New) return false;

        var hWnd = FindMainWindowHandle(fileName);
        if (hWnd == IntPtr.Zero) return false;

        if (strategy == RunStrategies.Toggle)
        {
            var fg = GetForegroundWindow();
            if (fg == hWnd && !IsIconic(hWnd))
            {
                ShowWindow(hWnd, SW_MINIMIZE);
            }
            else
            {
                if (IsIconic(hWnd))
                    ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
            }
            return true;
        }

        // Activate（默认）
        if (IsIconic(hWnd))
            ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
        return true;
    }

    private static IntPtr FindMainWindowHandle(string fileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName)) return IntPtr.Zero;
            if (!Path.IsPathRooted(fileName) || !File.Exists(fileName)) return IntPtr.Zero;

            string fullTarget;
            try { fullTarget = Path.GetFullPath(fileName); }
            catch { return IntPtr.Zero; }

            var targetName = Path.GetFileNameWithoutExtension(fullTarget);
            Process[] procs;
            try { procs = Process.GetProcessesByName(targetName); }
            catch { return IntPtr.Zero; }

            foreach (var p in procs)
            {
                try
                {
                    using (p)
                    {
                        string? modPath = null;
                        try { modPath = p.MainModule?.FileName; }
                        catch (Win32Exception) { continue; }
                        catch (InvalidOperationException) { continue; }

                        if (string.IsNullOrEmpty(modPath)) continue;
                        string fullMod;
                        try { fullMod = Path.GetFullPath(modPath); }
                        catch { continue; }

                        if (!string.Equals(fullMod, fullTarget, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var hWnd = p.MainWindowHandle;
                        if (hWnd != IntPtr.Zero)
                            return hWnd;
                    }
                }
                catch { }
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// ✅ 1.0.2+: 用 Windows 原生 CommandLineToArgvW API 解析命令行字符串，
    /// 正确处理带引号路径、转义符、空格分隔等所有 Windows 兼容的写法。
    /// </summary>
    /// <remarks>
    /// ✅ 1.0.5: 修复"含空格但不带引号路径"被错误拆分的问题
    /// 比如 "D:\Program Files\app.exe" 没引号时 CommandLineToArgvW 会拆成
    /// ["D:\Program", "Files\app.exe"]。这里通过智能拼接恢复原始 fileName。
    /// </remarks>
    private static (string FileName, string Arguments) ParseCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return (string.Empty, string.Empty);

        string trimmed = commandLine.Trim();
        IntPtr argv = CommandLineToArgvW(trimmed, out int argc);
        if (argv == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            string[] args = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                args[i] = Marshal.PtrToStringUni(p) ?? string.Empty;
            }

            if (args.Length == 0)
                return (string.Empty, string.Empty);

            // ✅ 1.0.5: 重构 fileName 提取
            // 策略：找到第一个 arg 在原始字符串中的位置，再推断真实的 fileName 边界
            string fileName = ExtractRealFileName(trimmed, args);

            // ✅ 1.0.5: 截取 arguments
            // 用 fileName 在 trimmed 中的实际结束位置向后取，而不是用 args[0].Length
            string arguments = string.Empty;
            if (args.Length > 1)
            {
                int fileNameStart = trimmed.IndexOf(args[0], StringComparison.Ordinal);
                if (fileNameStart < 0)
                {
                    // 找不到 args[0] —— args[0] 可能是从带空格路径中错误拆出来的片段
                    // 这时用 fileName 的实际长度来定位
                    fileNameStart = trimmed.IndexOf(fileName, StringComparison.Ordinal);
                }
                if (fileNameStart >= 0)
                {
                    int startIdx = fileNameStart + fileName.Length;
                    if (startIdx <= trimmed.Length)
                    {
                        arguments = trimmed.Substring(startIdx);
                        // ✅ 1.0.5: 双 trim——剥离可能残留的引号（fileNameStart 命中了带引号版本）和前导空白
                        arguments = arguments.Trim('"').Trim();
                    }
                    else
                    {
                        arguments = string.Join(" ", args.Skip(1));
                    }
                }
                else
                {
                    // fallback：拼接剩余 args
                    arguments = string.Join(" ", args.Skip(1));
                }
            }

            return (fileName, arguments);
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary>
    /// ✅ 1.0.5: 从原始命令行和 args 列表里智能提取真实 fileName
    /// 解决"含空格路径被空格拆错"的问题。
    ///
    /// 策略：
    /// 1) 若 args[0] 在原始字符串中是带引号出现的（"D:\Program Files\app.exe"），
    ///    就去掉两端引号作为 fileName。
    /// 2) 若 args[0] 没带引号（被空格拆开了），则累加 args 直到出现 .exe/.bat 等
    ///    已知可执行文件后缀，或者累加长度超过合理上限。
    /// </summary>
    private static string ExtractRealFileName(string commandLine, string[] args)
    {
        if (args.Length == 0) return string.Empty;

        // 情况 1: args[0] 在 commandLine 中带引号
        string quoted = $"\"{args[0]}\"";
        if (commandLine.Contains(quoted, StringComparison.Ordinal))
        {
            // ✅ 1.0.5: 用累加拼接处理 fileName 含多个空格的情况
            // 比如 "D:\Program Files\Microsoft Office\Office16\WINWORD.EXE" 被拆成
            // ["D:\Program", "Files\Microsoft", "Office\Office16\WINWORD.EXE"]
            // 这里累加直到 commandLine 中能找到连续的 "arg1 arg2 arg3"（带引号）
            string accumulated = args[0];
            for (int i = 1; i < args.Length; i++)
            {
                string candidate = accumulated + " " + args[i];
                string quotedCandidate = $"\"{candidate}\"";
                if (commandLine.Contains(quotedCandidate, StringComparison.Ordinal))
                    accumulated = candidate;
                else
                    break;
            }

            // ✅ 1.0.5: 最后再尝试去掉所有外层引号（避免半边引号问题）
            string finalQuoted = $"\"{accumulated}\"";
            if (commandLine.Contains(finalQuoted, StringComparison.Ordinal))
                return accumulated;  // 返回不带引号的版本

            // 没找到带引号的合并版本，fallback 到 args[0]
            return args[0].Trim('"');
        }

        // 情况 2: args[0] 本身以 ".exe" / ".bat" 等结尾
        // 说明没空格拆分，直接返回 args[0]
        string firstLower = args[0].ToLowerInvariant();
        if (firstLower.EndsWith(".exe") || firstLower.EndsWith(".bat") ||
            firstLower.EndsWith(".cmd") || firstLower.EndsWith(".ps1") ||
            firstLower.EndsWith(".vbs"))
        {
            return args[0];
        }

        // 情况 3: 累加拼接直到找到 .exe 后缀
        string acc = args[0];
        for (int i = 1; i < args.Length; i++)
        {
            acc += " " + args[i];
            string lower = acc.ToLowerInvariant();
            if (lower.EndsWith(".exe") || lower.EndsWith(".bat") ||
                lower.EndsWith(".cmd") || lower.EndsWith(".ps1") ||
                lower.EndsWith(".vbs"))
            {
                return acc;
            }
        }

        // 都没匹配上，按旧逻辑返回 args[0]
        return args[0].Trim('"');
    }

    private void NotifyFailed(ShortcutConfig cfg, string reason, int? exitCode)
    {
        // 不在 ExecutorService 内弹 UI —— 通过事件把责任交给 MainWindow
        ExecuteFailed?.Invoke(cfg.Key, reason, exitCode);

        // 记日志兜底（事件回调里可能没弹 UI）
        _ = _settingsService.LogAsync($"脚本失败 [{cfg.Key}]: {reason}");
    }
}
