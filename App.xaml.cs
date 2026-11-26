using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vector.Models;
using Vector.Services;

namespace Vector;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Mutex? _singleInstanceMutex;   // 提升为字段，防止 GC 回收
    private EventWaitHandle? _wakeupEvent;  // ✅ 1.0.2: 跨进程唤醒信号（旧实例监听，新实例触发）

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    private const int SW_RESTORE = 9;

    // ✅ 1.0.2: 用 EventWaitHandle 替代跨进程 Win32 直接操作 —— 旧实例监听信号，
    // 新实例只 Set 一下，由旧实例自己通过 Dispatcher 走 WPF 生命周期。
    // 命名：Local\ 前缀让所有会话可见（跨用户登录也 OK）。
    private const string WAKEUP_EVENT_NAME = "Local\\Vector_WakeupEvent";
    private const string MUTEX_NAME = "Local\\Vector_SingleInstance";

    // ✅ 1.0.1: UIPI 检测与自举
    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    private static extern bool IsUserAnAdmin();

    private const string ELEVATION_FLAG = "--elevated";  // 提权启动标记，防止无限循环

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                var dataDir = AppPaths.ResolveDataDirectory();

                services.AddSingleton<ISettingsService>(_ => new SettingsService(dataDir));
                services.AddSingleton<IHotkeyService, HotkeyService>();
                services.AddSingleton<IThemeService, ThemeService>();
                services.AddSingleton<ILanguageService, LanguageService>();
                services.AddSingleton<IUndoService, UndoService>();
                services.AddSingleton<IMouseGestureService, MouseGestureService>();
                services.AddSingleton<ScreenCaptureService>();
                services.AddSingleton<IPinFeatureService, PinFeatureService>();
                services.AddSingleton<IFileTypeDetector>(sp =>
                    new FileTypeDetector(sp.GetRequiredService<ISettingsService>()));
                services.AddSingleton<IExecutorService>(sp =>
                    new ExecutorService(
                        sp.GetRequiredService<ISettingsService>(),
                        sp.GetRequiredService<IFileTypeDetector>(),
                        sp.GetRequiredService<ILanguageService>()));
                services.AddSingleton<IVectorController, VectorController>();
                services.AddSingleton<IMcpPipeServerService, McpPipeServerService>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        DispatcherUnhandledException += (s, ex) =>
        {
            LogException($"[UI CRASH] {ex.Exception}");
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            LogException($"[DOMAIN CRASH] {ex.ExceptionObject}");
        };
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        // ✅ 1.0.1: UIPI 自举 —— 如果配置要求以管理员启动且当前进程未提权，则触发 UAC 重启
        if (ShouldRequestElevation(e.Args))
        {
            if (TryRestartElevated(e.Args))
            {
                // 提权进程已启动，当前进程退出
                Shutdown();
                return;
            }
            // 提权失败（用户取消 UAC），继续以普通权限运行
            Debug.WriteLine("[Vector] UAC 提权失败或被用户取消，继续以普通权限运行");
        }

        // 单例锁
        _singleInstanceMutex = new Mutex(true, MUTEX_NAME, out bool createdNew);
        if (!createdNew)
        {
            // ✅ 1.0.2: 旧实例已存在 —— 通过命名 EventWaitHandle 通知它，
            // 由旧实例自己通过 Dispatcher 走 WPF 生命周期（Show + Normal + Activate）。
            // 不再直接调 Win32 ShowWindow（那会让旧实例 WPF 逻辑树 Visibility 残留为 Hidden，
            // 出现"幽灵窗口"或内容不渲染）。
            try
            {
                using var wakeup = EventWaitHandle.OpenExisting(WAKEUP_EVENT_NAME);
                wakeup.Set();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Vector 单例] 唤醒信号失败: {ex.GetType().Name}: {ex.Message}");
                // 兜底：仍尝试走旧路径，确保用户至少能感知到已有实例
                var currentProc = System.Diagnostics.Process.GetCurrentProcess();
                var existingProcs = System.Diagnostics.Process.GetProcessesByName(currentProc.ProcessName);
                foreach (var p in existingProcs)
                {
                    if (p.Id != currentProc.Id && p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(p.MainWindowHandle);
                        break;
                    }
                }
            }
            Shutdown();
            return;
        }

        // ✅ 1.0.2: 我是旧实例 —— 创建 EventWaitHandle 并起后台线程监听唤醒信号
        try
        {
            _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WAKEUP_EVENT_NAME);
            var thread = new Thread(WakeupListenerLoop)
            {
                IsBackground = true,
                Name = "Vector.WakeupListener"
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Vector 单例] 创建唤醒事件失败: {ex.GetType().Name}: {ex.Message}");
        }

        // 开机自启延迟：仅 --from-startup 时等待（手动打开不拖慢）
        if (AutoStartService.IsFromStartup(e.Args))
        {
            int delaySec = ReadStartupDelaySeconds();
            if (delaySec > 0)
            {
                Debug.WriteLine($"[Vector] 自启延迟 {delaySec}s …");
                await Task.Delay(TimeSpan.FromSeconds(delaySec));
            }
        }

        base.OnStartup(e);
        await _host!.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static int ReadStartupDelaySeconds()
    {
        try
        {
            var configPath = AppPaths.ConfigPathIn(AppPaths.ResolveDataDirectory());
            if (!File.Exists(configPath)) return 0;
            var json = File.ReadAllText(configPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return Math.Clamp(settings?.StartupDelaySeconds ?? 0, 0, 120);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>✅ 1.0.2: 后台线程监听 EventWaitHandle，收到信号后通过 Dispatcher 唤起主窗口</summary>
    private void WakeupListenerLoop()
    {
        while (_wakeupEvent != null)
        {
            try
            {
                _wakeupEvent.WaitOne();  // 阻塞等待 Set
            }
            catch
            {
                return;  // 句柄被释放 → 退出监听
            }

            // 通过 Dispatcher 让 WPF 自己处理窗口生命周期
            try
            {
                Dispatcher.Invoke(() =>
                {
                    var win = MainWindow;
                    if (win == null) return;
                    if (!win.IsVisible) win.Show();
                    if (win.WindowState == System.Windows.WindowState.Minimized)
                        win.WindowState = System.Windows.WindowState.Normal;
                    win.Activate();
                    win.Topmost = true;  // 临时置顶抢焦点
                    win.Topmost = false;
                    win.Focus();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Vector 单例] Dispatcher 唤醒失败: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ✅ 1.0.1: 是否需要请求提权
    private bool ShouldRequestElevation(string[] args)
    {
        // 已经标记为提权进程（被 UAC 重新拉起的），不要再请求
        foreach (var a in args) if (a == ELEVATION_FLAG) return false;

        // 读取持久化偏好（与运行时同一套便携/AppData 解析）
        var configPath = AppPaths.ConfigPathIn(AppPaths.ResolveDataDirectory());
        if (!File.Exists(configPath)) return false;

        try
        {
            var json = File.ReadAllText(configPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings?.RunAsAdmin == true && !IsUserAnAdmin();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Vector 启动] 读取 RunAsAdmin 偏好失败: {ex.Message}");
            return false;
        }
    }

    // ✅ 1.0.1: 以管理员身份重启
    private bool TryRestartElevated(string[] args)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return false;

            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = exePath,
                Verb = "runas",  // 触发 UAC
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
            };

            // ✅ 修复: UseShellExecute=true 时不能用 ArgumentList（.NET 会抛 InvalidOperationException）
            // 改用 Arguments 字符串拼接，简单加双引号包住含空格的参数
            var sb = new System.Text.StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.Contains(' ') || a.Contains('\t')) sb.Append('"').Append(a).Append('"');
                else sb.Append(a);
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(ELEVATION_FLAG);
            psi.Arguments = sb.ToString();

            var newProc = Process.Start(psi);
            return newProc != null;
        }
        catch (Exception ex)
        {
            // 常见异常：用户取消 UAC (Win32Exception) / 操作被取消
            Debug.WriteLine($"[Vector 启动] 提权重启失败: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        // ✅ 1.0.2: 释放 EventWaitHandle，否则下次启动 OpenExisting 会抛 AbandonedMutexException
        // 也可能挂住 WaitOne 线程。Set 一下让监听线程自然退出，再 Dispose。
        try
        {
            _wakeupEvent?.Set();
            _wakeupEvent?.Dispose();
            _wakeupEvent = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Vector 单例] 释放唤醒事件失败: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch { }

        if (_host != null)
        {
            try
            {
                var mcpServer = _host.Services.GetService<IMcpPipeServerService>();
                if (mcpServer != null)
                    await mcpServer.StopAsync();
            }
            catch { }

            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        base.OnExit(e);
    }

    private static void LogException(string msg)
    {
        try
        {
            var logPath = AppPaths.LogPathIn(AppPaths.ResolveDataDirectory());
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch (Exception ex)
        {
            // ✅ 1.0.1: 全局异常处理器自己崩了也要留个痕迹
            Debug.WriteLine($"[Vector LogException 失败] {ex.GetType().Name}: {ex.Message} | 原消息: {msg}");
        }
    }
}
