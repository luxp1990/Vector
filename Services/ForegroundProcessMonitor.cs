using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Vector.Services;

public sealed class ForegroundProcessMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int QunsRunningD3dFullScreen = 3;
    private const int QunsPresentationMode = 4;
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const uint MonitorDefaultToNearest = 2;

    private readonly Dispatcher _dispatcher;
    private WinEventDelegate? _winEventDelegate;
    private IntPtr _hook;
    private DispatcherTimer? _pollTimer;
    private string _currentProcess = string.Empty;
    private bool _currentIsFullscreen;
    private bool _disposed;

    public event Action<string, bool>? ForegroundChanged;

    public string CurrentProcess => _currentProcess;
    public bool CurrentIsFullscreen => _currentIsFullscreen;

    public ForegroundProcessMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
            return;

        _winEventDelegate = OnWinEvent;
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            WineventOutofcontext);

        _pollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, OnPoll, _dispatcher);
        _pollTimer.Start();

        CheckForeground();
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _pollTimer?.Stop();
        _pollTimer = null;
        _winEventDelegate = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType == EventSystemForeground)
        {
            CheckForeground(hwnd);
        }
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        CheckForeground();
    }

    public void CheckForeground(IntPtr targetHwnd = default)
    {
        if (_disposed)
            return;

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => CheckForeground(targetHwnd)));
            return;
        }

        IntPtr hwnd = (targetHwnd != IntPtr.Zero && IsWindow(targetHwnd))
            ? targetHwnd
            : GetForegroundWindow();

        string processName = string.Empty;

        if (hwnd != IntPtr.Zero && IsWindow(hwnd))
        {
            uint pid = ResolveRealProcessId(hwnd);
            if (pid != 0)
                processName = GetProcessName(pid);
        }

        bool isFullscreen = false;
        if (SHQueryUserNotificationState(out int quns) == 0)
        {
            // 关键修复：绝不把 QUNS_BUSY (2) 当成全屏！
            // QUNS_BUSY 在 Windows 开启专注助手、双屏某窗口最大化时常驻为 2，会造成系统常态被永久误判为全屏。
            // 仅对明确的 3D 全屏独占 (3)、PPT 演示模式 (4) 或真实的无边框满屏几何窗口判定为全屏！
            isFullscreen = (quns == QunsRunningD3dFullScreen || quns == QunsPresentationMode || IsWindowBorderlessFullscreen(hwnd));
        }
        else
        {
            isFullscreen = IsWindowBorderlessFullscreen(hwnd);
        }

        if (!string.Equals(_currentProcess, processName, StringComparison.OrdinalIgnoreCase)
            || _currentIsFullscreen != isFullscreen)
        {
            _currentProcess = processName;
            _currentIsFullscreen = isFullscreen;
            ForegroundChanged?.Invoke(_currentProcess, _currentIsFullscreen);
        }
    }

    public void TriggerCurrent()
    {
        if (_disposed)
            return;

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(TriggerCurrent));
            return;
        }

        ForegroundChanged?.Invoke(_currentProcess, _currentIsFullscreen);
    }

    private static bool IsWindowBorderlessFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;

        int style = GetWindowLongW(hwnd, GwlStyle);
        // 如果有 WS_CAPTION 标题栏，属于普通窗口或常规最大化窗口，绝非全屏应用/游戏
        if ((style & WsCaption) == WsCaption)
            return false;

        IntPtr hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero)
            return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref mi))
            return false;

        if (!GetWindowRect(hwnd, out RECT rc))
            return false;

        // 无边框且完全覆盖所在显示器
        return rc.Left <= mi.rcMonitor.Left
            && rc.Top <= mi.rcMonitor.Top
            && rc.Right >= mi.rcMonitor.Right
            && rc.Bottom >= mi.rcMonitor.Bottom;
    }

    private uint ResolveRealProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return 0;

        string name = GetProcessName(pid);
        if (string.Equals(name, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint childPid = 0;
            EnumChildWindows(
                hwnd,
                (child, _) =>
                {
                    GetWindowThreadProcessId(child, out uint cPid);
                    if (cPid != 0 && cPid != pid)
                    {
                        childPid = cPid;
                        return false;
                    }
                    return true;
                },
                IntPtr.Zero);

            if (childPid != 0)
                return childPid;
        }

        return pid;
    }

    private string GetProcessName(uint pid)
    {
        if (pid == (uint)Environment.ProcessId)
            return "vector.exe";

        string name = string.Empty;
        IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (hProc != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(hProc, 0, sb, ref size))
                {
                    name = Path.GetFileName(sb.ToString()).ToLowerInvariant();
                }
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                name = (proc.ProcessName + ".exe").ToLowerInvariant();
            }
            catch
            {
                name = string.Empty;
            }
        }

        return name;
    }

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
