using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using Vector.Services;

namespace Vector.Windows;

public partial class RegionSelectorWindow : Window
{
    private const int WmGetminmaxinfo = 0x0024;
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;
    private const uint SwpNoactivate = 0x0010;

    private readonly ScreenCaptureService _captureService;
    private readonly Action<BitmapSource, System.Windows.Point> _completedWithPos;
    private readonly Action<string>? _failed;
    private System.Windows.Point? _start;
    private NativePoint? _startPhysical;

    public RegionSelectorWindow(
        ScreenCaptureService captureService,
        Action<BitmapSource, System.Windows.Point> completed,
        Action<string>? failed = null)
    {
        InitializeComponent();
        _captureService = captureService;
        _completedWithPos = completed;
        _failed = failed;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) =>
        {
            Activate();
            Focus();
        };
    }

    public RegionSelectorWindow(
        ScreenCaptureService captureService,
        Action<BitmapSource> completed,
        Action<string>? failed = null)
        : this(captureService, (img, _) => completed(img), failed)
    {
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        HwndSource? source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        int vx = GetSystemMetrics(SmXvirtualscreen);
        int vy = GetSystemMetrics(SmYvirtualscreen);
        int vcx = Math.Max(1, GetSystemMetrics(SmCxvirtualscreen));
        int vcy = Math.Max(1, GetSystemMetrics(SmCyvirtualscreen));

        _ = SetWindowPos(
            hwnd,
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
        SelectionCanvas.Width = Width;
        SelectionCanvas.Height = Height;
        UpdateLayout();
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

    /// <summary>
    /// 用按下/抬起时的物理光标坐标定截图矩形。小于 2 像素视为取消，不进入截图。
    /// </summary>
    internal static bool TryNormalizeCaptureRect(
        int x1,
        int y1,
        int x2,
        int y2,
        out int left,
        out int top,
        out int right,
        out int bottom)
    {
        left = Math.Min(x1, x2);
        top = Math.Min(y1, y2);
        right = Math.Max(x1, x2);
        bottom = Math.Max(y1, y2);
        return right - left >= 2 && bottom - top >= 2;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out NativePoint cursor))
        {
            Close();
            return;
        }

        _startPhysical = cursor;
        _start = PhysicalToCanvas(cursor);
        SelectionRectangle.Visibility = Visibility.Visible;
        UpdateRectangle(_start.Value, _start.Value);
        SelectionCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_start is System.Windows.Point start
            && e.LeftButton == MouseButtonState.Pressed
            && GetCursorPos(out NativePoint cursor))
            UpdateRectangle(start, PhysicalToCanvas(cursor));
    }

    private async void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is not System.Windows.Point || _startPhysical is not NativePoint startPhysical)
        {
            Close();
            return;
        }

        SelectionCanvas.ReleaseMouseCapture();
        _start = null;
        _startPhysical = null;

        if (!GetCursorPos(out NativePoint endPhysical)
            || !TryNormalizeCaptureRect(
                startPhysical.X,
                startPhysical.Y,
                endPhysical.X,
                endPhysical.Y,
                out int left,
                out int top,
                out int right,
                out int bottom))
        {
            Close();
            return;
        }

        Hide();
        // 隐藏全屏选择层后异步让桌面合成一帧，避免同步等待 WPF/DWM 阻塞输入线程。
        await Task.Delay(34);
        if (!IsLoaded)
            return;

        try
        {
            BitmapSource image = await Task.Run(() =>
                _captureService.CaptureRegion(left, top, right, bottom));
            if (!IsLoaded)
                return;
            Close();
            _completedWithPos(image, new System.Windows.Point(left, top));
        }
        catch (Exception ex)
        {
            Close();
            _failed?.Invoke($"{ex.Message} rect=({left},{top})-({right},{bottom})");
        }
    }

    private System.Windows.Point PhysicalToCanvas(NativePoint cursor)
    {
        try
        {
            return PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
        }
        catch (InvalidOperationException)
        {
            return new System.Windows.Point(cursor.X, cursor.Y);
        }
    }

    private void UpdateRectangle(System.Windows.Point first, System.Windows.Point second)
    {
        double left = Math.Min(first.X, second.X);
        double top = Math.Min(first.Y, second.Y);
        System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, left);
        System.Windows.Controls.Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = Math.Abs(second.X - first.X);
        SelectionRectangle.Height = Math.Abs(second.Y - first.Y);
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

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
