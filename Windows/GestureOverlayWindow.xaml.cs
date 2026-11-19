using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vector.Services;

namespace Vector.Windows;

/// <summary>
/// 鼠标手势轨迹遮罩窗。生产流程每次手势新建、结束后销毁，
/// 避免复用分层窗时 DWM 重显旧轨迹；独立契约测试仍验证空闲停车行为。
/// </summary>
public partial class GestureOverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolwindow = 0x00000080;
    private const long WsExNoactivate = 0x08000000;
    private const int WmGetminmaxinfo = 0x0024;
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;
    private const uint SwpNoactivate = 0x0010;

    /// <summary>空闲停放位：远在虚拟桌面之外，任何显示器都覆盖不到。</summary>
    private const int ParkX = -32000;
    private const int ParkY = -32000;

    private Polyline? _segment;
    private System.Windows.Point? _lastPoint;
    private System.Windows.Media.Brush _traceBrush = System.Windows.Media.Brushes.MediumSlateBlue;
    private double _traceWidth = 3;
    private nint _hwnd;

    public GestureOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void SetTraceStyle(string color, double width)
    {
        try
        {
            _traceBrush = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(color)!;
            if (_traceBrush.CanFreeze)
                _traceBrush.Freeze();
        }
        catch
        {
            _traceBrush = System.Windows.Media.Brushes.MediumSlateBlue;
        }

        _traceWidth = Math.Max(1, width);
    }

    public void ShowForGesture()
    {
        Clear();
        if (!IsVisible)
            Show();
        // SM_*VIRTUALSCREEN 每轮重取，以适配运行时的显示器配置变化。
        CoverVirtualScreen();
    }

    public void HideOverlay()
    {
        // 常驻空透明窗：只清内容，不 Hide。连续手势时不会触发 DWM 重显旧帧。
        Clear();
        UpdateLayout();
        // 但必须挪出屏幕：铺满显示器的常驻顶层透明窗会被当成"全屏程序"，
        // 把系统置成 QUNS_BUSY，害得别的软件的"全屏时禁用快捷键"逻辑误判。
        ParkOffscreen();
    }

    public void AddPhysicalPoint(int x, int y)
    {
        System.Windows.Point local = PointFromScreen(new System.Windows.Point(x, y));
        if (_lastPoint == local)
            return;

        if (_segment is null || _segment.Points.Count >= 24)
        {
            _segment = new Polyline
            {
                Stroke = _traceBrush,
                StrokeThickness = _traceWidth,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            if (_lastPoint is System.Windows.Point previous)
                _segment.Points.Add(previous);
            TraceCanvas.Children.Add(_segment);
        }

        _segment.Points.Add(local);
        _lastPoint = local;
    }

    public void ShowResult(string text, string color)
    {
        if (_lastPoint is not System.Windows.Point position)
            return;

        System.Windows.Media.Brush brush;
        try
        {
            brush = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(color)!;
        }
        catch
        {
            brush = System.Windows.Media.Brushes.SlateGray;
        }

        var label = new System.Windows.Controls.TextBlock
        {
            Text = text,
            Foreground = brush,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            IsHitTestVisible = false
        };
        TraceCanvas.Children.Add(label);
        System.Windows.Controls.Canvas.SetLeft(label, position.X);
        System.Windows.Controls.Canvas.SetTop(label, position.Y - 42);
    }

    public void Clear()
    {
        TraceCanvas.Children.Clear();
        _segment = null;
        _lastPoint = null;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        nint style = GetWindowLongPtr(_hwnd, GwlExstyle);
        SetWindowLongPtr(
            _hwnd,
            GwlExstyle,
            new nint(style.ToInt64() | WsExTransparent | WsExToolwindow | WsExNoactivate));

        HwndSource? source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        // 初始化时暂时停到屏幕外；ShowForGesture 随后才铺到虚拟桌面。
        ParkOffscreen();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmGetminmaxinfo && lParam != 0)
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            int vcx = Math.Max(1, GetSystemMetrics(SmCxvirtualscreen));
            int vcy = Math.Max(1, GetSystemMetrics(SmCyvirtualscreen));
            mmi.PtMaxSize.X = Math.Max(mmi.PtMaxSize.X, vcx);
            mmi.PtMaxSize.Y = Math.Max(mmi.PtMaxSize.Y, vcy);
            mmi.PtMaxTrackSize.X = Math.Max(mmi.PtMaxTrackSize.X, vcx);
            mmi.PtMaxTrackSize.Y = Math.Max(mmi.PtMaxTrackSize.Y, vcy);
            Marshal.StructureToPtr(mmi, lParam, true);
        }
        return IntPtr.Zero;
    }

    /// <summary>空闲态：把遮罩窗移到屏幕外，既不 Hide 也不覆盖任何显示器。</summary>
    private void ParkOffscreen()
    {
        if (_hwnd == 0)
            return;

        _ = SetWindowPos(
            _hwnd,
            new nint(-1),
            ParkX,
            ParkY,
            Math.Max(1, GetSystemMetrics(SmCxvirtualscreen)),
            Math.Max(1, GetSystemMetrics(SmCyvirtualscreen)),
            SwpNoactivate);
    }

    /// <summary>手势态：铺满整个虚拟桌面，保证任意显示器上都能画出轨迹。</summary>
    private void CoverVirtualScreen()
    {
        if (_hwnd == 0)
            return;

        int vx = GetSystemMetrics(SmXvirtualscreen);
        int vy = GetSystemMetrics(SmYvirtualscreen);
        int vcx = Math.Max(1, GetSystemMetrics(SmCxvirtualscreen));
        int vcy = Math.Max(1, GetSystemMetrics(SmCyvirtualscreen));

        _ = SetWindowPos(
            _hwnd,
            new nint(-1),
            vx,
            vy,
            vcx,
            vcy,
            SwpNoactivate);

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = vx / dpiX;
        Top = vy / dpiY;
        Width = vcx / dpiX;
        Height = vcy / dpiY;
        TraceCanvas.Width = Width;
        TraceCanvas.Height = Height;
        UpdateLayout();
    }

    private static nint GetWindowLongPtr(nint window, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new nint(GetWindowLong32(window, index));

    private static nint SetWindowLongPtr(nint window, int index, nint value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new nint(SetWindowLong32(window, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint PtReserved;
        public NativePoint PtMaxSize;
        public NativePoint PtMaxPosition;
        public NativePoint PtMinTrackSize;
        public NativePoint PtMaxTrackSize;
    }
}
