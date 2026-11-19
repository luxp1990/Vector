using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Vector.Services;

public sealed class MouseGestureService : IMouseGestureService
{
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const uint WmQuit = 0x0012;
    private const uint WmMouseMove = 0x0200;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint LlmhfInjected = 0x00000001;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint WmSuspendHook = 0x8001;
    private const uint WmResumeHook = 0x8002;
    private const uint GaRoot = 2;
    private const double MoveStep = 2;
    private const int MaxQueuedEvents = 1024;

    private readonly ConcurrentQueue<MouseGestureEvent> _events = new();
    private readonly ConcurrentQueue<nint> _pinCloseRequests = new();
    private readonly ConcurrentDictionary<nint, byte> _passthroughWindows = new();
    private readonly object _lifecycleLock = new();
    private readonly HookProc _callback;
    private Thread? _thread;
    private nint _hook;
    private int _hookInstalled;
    private uint _threadId;
    private ManualResetEventSlim? _suspendWait;
    private readonly object _suspendLock = new();
    private int _running;
    private int _enabled = 1;
    private int _queuedCount;
    private long _startDistanceBits = BitConverter.DoubleToInt64Bits(8);
    private bool _armed;
    private bool _gesturing;
    private bool _suppressPinRightUp;
    private NativePoint _origin;
    private NativePoint _lastEmitted;

    public MouseGestureService()
    {
        _callback = HookCallback;
    }

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    public double StartDistance
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _startDistanceBits));
        set => Interlocked.Exchange(
            ref _startDistanceBits,
            BitConverter.DoubleToInt64Bits(Math.Max(1, value)));
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_thread is not null)
                return;

            Volatile.Write(ref _running, 1);
            _thread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "Vector.MouseGestureHook"
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lifecycleLock)
        {
            thread = _thread;
            if (thread is null)
                return;
            Volatile.Write(ref _running, 0);
            uint threadId = Volatile.Read(ref _threadId);
            if (threadId != 0)
                _ = PostThreadMessage(threadId, WmQuit, 0, 0);
        }

        if (thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(2));

        lock (_lifecycleLock)
        {
            if (_thread == thread)
                _thread = null;
        }
    }

    public bool TryDequeue(out MouseGestureEvent gestureEvent)
    {
        if (!_events.TryDequeue(out gestureEvent))
            return false;
        Interlocked.Decrement(ref _queuedCount);
        return true;
    }

    public bool TryDequeuePinClose(out nint hwnd) => _pinCloseRequests.TryDequeue(out hwnd);

    /// <summary>
    /// 贴图上的右键必须按下和抬起一起吞掉。只吞抬起时，按下会在窗口销毁时落到下层，
    /// 下层一直等不到抬起，桌面图标就会点不了。
    /// </summary>
    internal static PinRightClickDecision DecidePinRightClick(bool overPin, bool buttonUp, bool suppressUp)
    {
        if (!buttonUp && overPin)
            return new PinRightClickDecision(true, true);
        if (buttonUp && suppressUp)
            return new PinRightClickDecision(true, false);
        return new PinRightClickDecision(false, false);
    }

    public void ReplayRightClick()
    {
        mouse_event(MouseeventfRightdown | MouseeventfRightup, 0, 0, 0, 0);
    }

    public bool SuspendHook(TimeSpan timeout)
    {
        uint threadId = Volatile.Read(ref _threadId);
        if (threadId == 0)
            return true;

        var wait = new ManualResetEventSlim(false);
        lock (_suspendLock)
            _suspendWait = wait;

        if (!PostThreadMessage(threadId, WmSuspendHook, 0, 0))
            return false;
        if (!wait.Wait(timeout) || Volatile.Read(ref _hookInstalled) != 0)
        {
            // 卸下请求可能还在队列里。补一条恢复，避免钩子一直消失，或在钩子还挂着时去读剪贴板。
            ResumeHook();
            return false;
        }

        return true;
    }

    public void ResumeHook()
    {
        uint threadId = Volatile.Read(ref _threadId);
        if (threadId != 0)
            _ = PostThreadMessage(threadId, WmResumeHook, 0, 0);
    }

    public void AddClickPassthroughWindow(nint hwnd)
    {
        if (hwnd != 0)
            _passthroughWindows[hwnd] = 0;
    }

    public void RemoveClickPassthroughWindow(nint hwnd)
    {
        if (hwnd != 0)
            _passthroughWindows.TryRemove(hwnd, out _);
    }

    private void RunMessageLoop()
    {
        try
        {
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            _ = PeekMessage(out _, 0, 0, 0, 0);
            EnsureHook();
            if (Volatile.Read(ref _hookInstalled) == 0)
                return;

            while (Volatile.Read(ref _running) != 0)
            {
                int result = GetMessage(out NativeMessage message, 0, 0, 0);
                if (result <= 0)
                    break;
                if (message.Message == WmSuspendHook)
                {
                    DetachHook();
                    _armed = false;
                    _gesturing = false;
                    _suppressPinRightUp = false;
                    lock (_suspendLock)
                        _suspendWait?.Set();
                    continue;
                }
                if (message.Message == WmResumeHook)
                {
                    EnsureHook();
                    continue;
                }
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }
        }
        finally
        {
            DetachHook();
            Volatile.Write(ref _threadId, 0);
            Volatile.Write(ref _running, 0);
        }
    }

    private void EnsureHook()
    {
        if (_hook != 0 || Volatile.Read(ref _running) == 0)
            return;
        _hook = SetWindowsHookEx(WhMouseLl, _callback, 0, 0);
        Volatile.Write(ref _hookInstalled, _hook != 0 ? 1 : 0);
    }

    private void DetachHook()
    {
        nint hook = _hook;
        _hook = 0;
        Volatile.Write(ref _hookInstalled, 0);
        if (hook != 0)
            _ = UnhookWindowsHookEx(hook);
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code != HcAction)
                return CallNextHookEx(_hook, code, wParam, lParam);

            MouseHookData data = Marshal.PtrToStructure<MouseHookData>(lParam);
            if ((data.Flags & LlmhfInjected) != 0)
                return CallNextHookEx(_hook, code, wParam, lParam);

            uint message = unchecked((uint)wParam.ToInt64());
            if (message == WmRButtonDown)
            {
                bool overPin = TryGetPassthrough(data.Point, out nint pin);
                PinRightClickDecision decision = DecidePinRightClick(overPin, false, _suppressPinRightUp);
                if (decision.Swallow)
                {
                    // 按下和抬起都留在钩子里。关闭放到界面线程，避免在按下消息里拆掉窗口。
                    _suppressPinRightUp = true;
                    _armed = false;
                    _gesturing = false;
                    if (decision.RequestClose)
                        _pinCloseRequests.Enqueue(pin);
                    return 1;
                }

                if (Enabled)
                {
                    _armed = true;
                    _gesturing = false;
                    _origin = data.Point;
                    _lastEmitted = data.Point;
                    return 1;
                }
            }

            if (message == WmRButtonUp)
            {
                PinRightClickDecision decision = DecidePinRightClick(false, true, _suppressPinRightUp);
                if (decision.Swallow)
                {
                    _suppressPinRightUp = false;
                    _armed = false;
                    _gesturing = false;
                    return 1;
                }
            }

            if (message == WmMouseMove && _armed)
            {
                if (!_gesturing)
                {
                    long dx = data.Point.X - _origin.X;
                    long dy = data.Point.Y - _origin.Y;
                    double threshold = StartDistance;
                    if (dx * dx + dy * dy >= threshold * threshold)
                    {
                        _gesturing = true;
                        Enqueue(new MouseGestureEvent(
                            MouseGestureEventKind.Begin,
                            _origin.X,
                            _origin.Y));
                        Enqueue(new MouseGestureEvent(
                            MouseGestureEventKind.Move,
                            data.Point.X,
                            data.Point.Y));
                        _lastEmitted = data.Point;
                    }
                }
                else
                {
                    long dx = data.Point.X - _lastEmitted.X;
                    long dy = data.Point.Y - _lastEmitted.Y;
                    if (dx * dx + dy * dy >= MoveStep * MoveStep)
                    {
                        Enqueue(new MouseGestureEvent(
                            MouseGestureEventKind.Move,
                            data.Point.X,
                            data.Point.Y));
                        _lastEmitted = data.Point;
                    }
                }

                return CallNextHookEx(_hook, code, wParam, lParam);
            }

            if (message == WmRButtonUp && _armed)
            {
                bool wasGesturing = _gesturing;
                _armed = false;
                _gesturing = false;
                if (wasGesturing)
                {
                    Enqueue(new MouseGestureEvent(
                        MouseGestureEventKind.End,
                        data.Point.X,
                        data.Point.Y));
                }
                else
                {
                    ReplayRightClick();
                }
                return 1;
            }

            return CallNextHookEx(_hook, code, wParam, lParam);
        }
        catch
        {
            try
            {
                return CallNextHookEx(_hook, code, wParam, lParam);
            }
            catch
            {
                return 0;
            }
        }
    }

    private void Enqueue(MouseGestureEvent gestureEvent)
    {
        if (gestureEvent.Kind == MouseGestureEventKind.Move
            && Volatile.Read(ref _queuedCount) >= MaxQueuedEvents)
            return;
        Interlocked.Increment(ref _queuedCount);
        _events.Enqueue(gestureEvent);
    }

    private bool TryGetPassthrough(NativePoint point, out nint hwnd)
    {
        hwnd = 0;
        if (_passthroughWindows.IsEmpty)
            return false;

        nint hit = WindowFromPoint(point);
        if (hit == 0)
            return false;

        nint root = GetAncestor(hit, GaRoot);
        if (root == 0)
            root = hit;

        if (_passthroughWindows.ContainsKey(root))
        {
            hwnd = root;
            return true;
        }

        if (_passthroughWindows.ContainsKey(hit))
        {
            hwnd = hit;
            return true;
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        HookProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out NativeMessage message,
        nint window,
        uint minimum,
        uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        nint window,
        uint minimum,
        uint maximum,
        uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        nuint extraInfo);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
