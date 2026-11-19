using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Vector.Services;

public sealed class ScreenCaptureService
{
    private const uint SrccopyCaptureblt = 0x40CC0020;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private static int _dwmFlushInFlight;

    public BitmapSource CaptureRegion(int left, int top, int right, int bottom)
    {
        (left, right) = (Math.Min(left, right), Math.Max(left, right));
        (top, bottom) = (Math.Min(top, bottom), Math.Max(top, bottom));
        int width = right - left;
        int height = bottom - top;
        if (width < 2 || height < 2)
            throw new ArgumentOutOfRangeException(nameof(right), "Capture region is too small.");

        // 在执行 GDI 抓屏前刷新 DWM 合成队列，确保刚被隐藏/销毁的蒙层或轨迹窗口已被完全从屏幕表面刷除
        FlushDwm(250);

        nint screenDc = 0;
        nint memoryDc = 0;
        nint bitmap = 0;
        nint previous = 0;
        bool selected = false;

        try
        {
            screenDc = GetDC(0);
            if (screenDc == 0)
                throw LastError("GetDC");

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
                throw LastError("CreateCompatibleDC");

            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == 0)
                throw LastError("CreateCompatibleBitmap");

            previous = SelectObject(memoryDc, bitmap);
            if (previous == 0 || previous == new nint(-1))
                throw LastError("SelectObject");
            selected = true;

            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, SrccopyCaptureblt))
                throw LastError("BitBlt");

            SelectObject(memoryDc, previous);
            selected = false;

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb
                }
            };
            int stride = checked(width * 4);
            byte[] pixels = new byte[checked(stride * height)];
            int copied = GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors);
            if (copied != height)
                throw LastError("GetDIBits");

            (double dpiX, double dpiY) = GetMonitorDpi(left, top);
            BitmapSource source = BitmapSource.Create(
                width,
                height,
                dpiX,
                dpiY,
                System.Windows.Media.PixelFormats.Bgr32,
                null,
                pixels,
                stride);
            source.Freeze();
            return source;
        }
        finally
        {
            if (selected && memoryDc != 0 && previous != 0)
                SelectObject(memoryDc, previous);
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (memoryDc != 0)
                DeleteDC(memoryDc);
            if (screenDc != 0)
                ReleaseDC(0, screenDc);
        }
    }

    /// <summary>
    /// 在后台截屏线程调用 DwmFlush，等待 DWM 合成器呈现已隐藏/关闭的窗口表面，消除截图残影。
    /// 包含单飞标记（Single-Flight Gate）与 250ms 超时保护：前序调用若因锁屏/DWM假死未返回，不重复开新线程，绝不泄露线程池。
    /// </summary>
    public static void FlushDwm(int timeoutMs = 250)
    {
        try
        {
            if (DwmIsCompositionEnabled(out bool enabled) == 0 && enabled)
            {
                if (Interlocked.CompareExchange(ref _dwmFlushInFlight, 1, 0) != 0)
                    return;

                var flushTask = Task.Run(static () =>
                {
                    try
                    {
                        _ = DwmFlush();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _dwmFlushInFlight, 0);
                    }
                });
                _ = flushTask.Wait(timeoutMs);
            }
        }
        catch
        {
        }
    }

    private static Win32Exception LastError(string operation) =>
        new(Marshal.GetLastWin32Error(), $"{operation} failed.");

    private static (double X, double Y) GetMonitorDpi(int x, int y)
    {
        try
        {
            nint monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, 2);
            if (monitor != 0
                && GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) == 0
                && dpiX > 0
                && dpiY > 0)
                return (dpiX, dpiY);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        return (96, 96);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint value);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint destination,
        int xDestination,
        int yDestination,
        int width,
        int height,
        nint source,
        int xSource,
        int ySource,
        uint rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        nint dc,
        nint bitmap,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfo info,
        uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool pfEnabled);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
