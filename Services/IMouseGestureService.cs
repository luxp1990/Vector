namespace Vector.Services;

public enum MouseGestureEventKind
{
    Begin,
    Move,
    End,
    ReplayRightClick
}

public readonly record struct MouseGestureEvent(
    MouseGestureEventKind Kind,
    int X,
    int Y);

public readonly record struct PinRightClickDecision(bool Swallow, bool RequestClose);

public interface IMouseGestureService
{
    bool Enabled { get; set; }
    double StartDistance { get; set; }

    void Start();
    void Stop();

    /// <summary>
    /// 卸下低级鼠标钩子。读剪贴板前必须先卸下，否则 GetClipboardData 可能卡住全系统鼠标。
    /// 返回 false 时钩子仍在，调用方不得再碰剪贴板。
    /// </summary>
    bool SuspendHook(TimeSpan timeout);

    void ResumeHook();
    bool TryDequeue(out MouseGestureEvent gestureEvent);
    bool TryDequeuePinClose(out nint hwnd);
    void ReplayRightClick();

    /// <summary>
    /// 这些窗口上的右键整段吞掉，并由界面线程关闭窗口，不把按下或抬起交给下层。
    /// </summary>
    void AddClickPassthroughWindow(nint hwnd);

    void RemoveClickPassthroughWindow(nint hwnd);
}
