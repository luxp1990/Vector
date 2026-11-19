using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Vector.Windows;

public partial class PinImageWindow : Window
{
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint MonitorDefaulttonearest = 2;

    private readonly double _minimumAlpha;
    private readonly double _maximumAlpha;
    private readonly double _alphaStep;
    private readonly System.Windows.Point? _physicalPosition;
    private bool _closing;
    private bool _rightHeld;

    public PinImageWindow(
        BitmapSource image,
        double alpha,
        double minimumAlpha,
        double maximumAlpha,
        double alphaStep,
        System.Windows.Point? physicalPosition = null)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(image);

        _physicalPosition = physicalPosition;
        _minimumAlpha = Math.Clamp(minimumAlpha, 0.05, 1);
        _maximumAlpha = Math.Clamp(maximumAlpha, _minimumAlpha, 1);
        _alphaStep = Math.Clamp(alphaStep, 0.01, 0.5);
        Opacity = Math.Clamp(alpha, _minimumAlpha, _maximumAlpha);
        PinnedImage.Source = image;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (PinnedImage.Source is BitmapSource image)
        {
            // 剪贴板贴图（无原位捕获坐标时）：根据目标显示器可用区域自适应缩放超大图片
            if (!_physicalPosition.HasValue)
            {
                (Rect32 workArea, double dpiScale) = GetTargetMonitorWorkArea();
                double workWidthDip = (workArea.Right - workArea.Left) / dpiScale;
                double workHeightDip = (workArea.Bottom - workArea.Top) / dpiScale;
                double maxWidth = Math.Max(160, workWidthDip * 0.85);
                double maxHeight = Math.Max(120, workHeightDip * 0.85);
                if (image.Width > maxWidth || image.Height > maxHeight)
                {
                    double scale = Math.Min(maxWidth / image.Width, maxHeight / image.Height);
                    PinnedImage.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                }
            }
        }

        if (_physicalPosition is System.Windows.Point pt)
        {
            // 原位贴图：
            // 1. 直角边框保证捕获图像的四角像素不被圆角裁剪
            WindowBorder.CornerRadius = new CornerRadius(0);

            int intPtX = (int)Math.Floor(pt.X);
            int intPtY = (int)Math.Floor(pt.Y);
            (double dpiX, double dpiY) = GetMonitorDpiAt(intPtX, intPtY);

            // 2. 将边框设定为精确的 1.0 物理像素（DIP 厚度 = 1.0 / (dpi / 96.0)），
            // 彻底杜绝 125%、150%、250% 等非整数缩放下边框占 1.5px/2.5px 导致的半像素漂移
            double dipBorderX = 1.0 / (dpiX / 96.0);
            double dipBorderY = 1.0 / (dpiY / 96.0);
            WindowBorder.BorderThickness = new Thickness(dipBorderX, dipBorderY, dipBorderX, dipBorderY);

            UpdateLayout();

            if (hwnd != 0)
            {
                int targetX = intPtX - 1;
                int targetY = intPtY - 1;
                SetWindowPos(hwnd, new nint(-1), targetX, targetY, 0, 0, SwpNosize | SwpNoactivate);

                // 3. 用图像实际屏幕物理落点反查并微调，AwayFromZero 舍入消除一切舍入残差
                try
                {
                    System.Windows.Point actualImgScreen = PinnedImage.PointToScreen(new System.Windows.Point(0, 0));
                    int deltaX = (int)Math.Round(pt.X - actualImgScreen.X, MidpointRounding.AwayFromZero);
                    int deltaY = (int)Math.Round(pt.Y - actualImgScreen.Y, MidpointRounding.AwayFromZero);
                    if (deltaX != 0 || deltaY != 0)
                    {
                        targetX += deltaX;
                        targetY += deltaY;
                        SetWindowPos(hwnd, new nint(-1), targetX, targetY, 0, 0, SwpNosize | SwpNoactivate);
                    }
                }
                catch
                {
                }
            }
        }
        else
        {
            UpdateLayout();

            if (hwnd != 0)
            {
                // 剪贴板贴图：居中显示在当前光标所在的显示器工作区
                (Rect32 work, double dpiScale) = GetTargetMonitorWorkArea();
                int windowPhysicalWidth = (int)Math.Round(ActualWidth * dpiScale, MidpointRounding.AwayFromZero);
                int windowPhysicalHeight = (int)Math.Round(ActualHeight * dpiScale, MidpointRounding.AwayFromZero);
                int targetX = work.Left + Math.Max(0, (work.Right - work.Left - windowPhysicalWidth) / 2);
                int targetY = work.Top + Math.Max(0, (work.Bottom - work.Top - windowPhysicalHeight) / 4);
                SetWindowPos(hwnd, new nint(-1), targetX, targetY, 0, 0, SwpNosize | SwpNoactivate);
            }
        }

        Activate();
        Focus();
    }

    private static (Rect32 Work, double DpiScale) GetTargetMonitorWorkArea()
    {
        NativePoint cursor = default;
        _ = GetCursorPos(out cursor);
        nint monitor = MonitorFromPoint(cursor, MonitorDefaulttonearest);
        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
        {
            double scale = 1.0;
            if (GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                scale = dpiX / 96.0;
            return (info.RcWork, scale);
        }

        Rect primWork = SystemParameters.WorkArea;
        (double primDpiX, _) = GetMonitorDpiAt(0, 0);
        double fallbackScale = primDpiX > 0 ? primDpiX / 96.0 : 1.0;
        return (new Rect32
        {
            Left = (int)Math.Round(primWork.Left * fallbackScale, MidpointRounding.AwayFromZero),
            Top = (int)Math.Round(primWork.Top * fallbackScale, MidpointRounding.AwayFromZero),
            Right = (int)Math.Round(primWork.Right * fallbackScale, MidpointRounding.AwayFromZero),
            Bottom = (int)Math.Round(primWork.Bottom * fallbackScale, MidpointRounding.AwayFromZero)
        }, fallbackScale);
    }

    private static (double X, double Y) GetMonitorDpiAt(int x, int y)
    {
        try
        {
            nint monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaulttonearest);
            if (monitor != 0 && GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
                return (dpiX, dpiY);
        }
        catch
        {
        }
        return (96, 96);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            CloseQuick();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 钩子未挂上时走这里。按下只捕获，抬起才关闭，避免窗口在按下过程中消失后抬起落到下层。
        e.Handled = true;
        _rightHeld = true;
        CaptureMouse();
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (_rightHeld)
            RequestClose();
    }

    public void RequestClose() => CloseQuick();

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double direction = e.Delta > 0 ? 1 : -1;
        Opacity = Math.Clamp(
            Opacity + direction * _alphaStep,
            _minimumAlpha,
            _maximumAlpha);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Delete)
        {
            e.Handled = true;
            CloseQuick();
        }
    }

    private void CloseQuick()
    {
        if (_closing)
            return;
        _closing = true;
        _rightHeld = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();

        // 先立刻消失，再异步拆分层窗口，避免透明窗 Close 阻塞输入手感。
        Visibility = Visibility.Hidden;
        PinnedImage.Source = null;
        Dispatcher.BeginInvoke(Close, DispatcherPriority.Background);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect32 RcMonitor;
        public Rect32 RcWork;
        public uint DwFlags;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
