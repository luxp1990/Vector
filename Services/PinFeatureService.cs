using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vector.Models;
using Vector.Windows;

namespace Vector.Services;

public sealed class PinFeatureService : IPinFeatureService
{
    private const int MaxGesturePoints = 8192;
    private const int MaxPendingGestureCaptures = 2;
    private readonly IMouseGestureService _mouse;
    private readonly ScreenCaptureService _capture;
    private readonly ISettingsService? _settingsService;
    private readonly ILanguageService? _languageService;
    private readonly GestureMatcher _matcher = new();
    private readonly List<System.Windows.Point> _points = new();
    private readonly List<PinImageWindow> _pinWindows = new();

    private Dispatcher? _dispatcher;
    private DispatcherTimer? _pollTimer;
    private GestureOverlayWindow? _overlay;
    private RegionSelectorWindow? _selector;
    private PinSettings _settings = new();
    private DateTime? _clearOverlayAt;
    private System.Windows.Point? _pendingTracePoint;
    private bool _gesturePointLimitReached;
    private int _pendingGestureCaptures;
    private readonly SemaphoreSlim _clipboardGate = new(1, 1);
    private bool _initialized;
    private bool _runtimeEnabled;

    public PinFeatureService()
        : this(new MouseGestureService(), new ScreenCaptureService())
    {
    }

    public PinFeatureService(
        IMouseGestureService mouseGestureService,
        ScreenCaptureService screenCaptureService)
    {
        _mouse = mouseGestureService;
        _capture = screenCaptureService;
    }

    public PinFeatureService(
        IMouseGestureService mouseGestureService,
        ScreenCaptureService screenCaptureService,
        ISettingsService settingsService,
        ILanguageService? languageService = null)
        : this(mouseGestureService, screenCaptureService)
    {
        _settingsService = settingsService;
        _languageService = languageService;
    }

    private string L(string key, string fallback) => _languageService?.GetString(key) ?? fallback;

    public event Action<string>? NoticeRequested;

    public void Initialize(PinSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_initialized)
        {
            UpdateSettings(settings);
            return;
        }

        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _dispatcher.VerifyAccess();
        _settings = settings;
        _matcher.Sensitivity = settings.Sensitive;
        _mouse.StartDistance = settings.StartDistance;
        _runtimeEnabled = settings.Enabled;
        _mouse.Enabled = _runtimeEnabled;

        _pollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Input,
            OnPoll,
            _dispatcher);
        _pollTimer.Start();
        _mouse.Start();
        _initialized = true;
    }

    public void UpdateSettings(PinSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RunOnUi(() =>
        {
            _settings = settings;
            _matcher.Sensitivity = settings.Sensitive;
            _mouse.StartDistance = settings.StartDistance;
            if (settings.DrawTrace)
                _overlay?.SetTraceStyle(settings.GestureColor, settings.TraceWidth);
            else
                CloseGestureOverlay();
            if (!settings.Enabled)
                SetEnabled(false);
        });
    }

    public void SetEnabled(bool enabled)
    {
        RunOnUi(() =>
        {
            _runtimeEnabled = enabled;
            _mouse.Enabled = enabled;
            if (!enabled)
                ResetGesture();
        });
    }

    public void StartRegionCapture()
    {
        RunOnUi(() =>
        {
            if (!_initialized || !_runtimeEnabled)
                return;
            if (_selector is { IsVisible: true })
            {
                _selector.Activate();
                return;
            }

            _selector = new RegionSelectorWindow(
                _capture,
                (image, pt) => SpawnCapturedPin(image, pt),
                reason =>
                {
                    _ = _settingsService?.LogAsync($"区域截图失败: {reason}");
                    NoticeRequested?.Invoke(L("Pin_Notice_RegionCaptureFailed", "区域截图失败"));
                });
            _selector.Closed += (_, _) => _selector = null;
            _selector.Show();
        });
    }

    public void PinClipboard()
    {
        RunOnUi(() =>
        {
            if (!_initialized || !_runtimeEnabled || _dispatcher is null)
                return;
            if (!_clipboardGate.Wait(0))
            {
                NoticeRequested?.Invoke(L("Pin_Notice_ClipboardBusy", "剪贴板仍在处理中，请稍后重试"));
                return;
            }
            _ = PinClipboardAsync();
        });
    }

    private async Task PinClipboardAsync()
    {
        bool releaseGate = true;
        bool hookOff = false;
        try
        {
            // 低级鼠标钩子还挂着时，Win32 剪贴板的同步渲染会把整个桌面的鼠标输入卡住。
            hookOff = await Task.Run(() => _mouse.SuspendHook(TimeSpan.FromMilliseconds(500)));
            if (!hookOff)
            {
                _ = _settingsService?.LogAsync("剪贴板贴图已取消：鼠标钩子未能及时卸下");
                NoticeRequested?.Invoke(L("Pin_Notice_ClipboardUnsafe", "暂时无法安全读取剪贴板，请稍后重试"));
                return;
            }

            Task<ClipboardPayload> readTask = Task.Run(ClipboardImageIo.TryRead);
            Task finished = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromMilliseconds(2500)));
            if (finished != readTask)
            {
                releaseGate = false;
                bool resume = hookOff;
                hookOff = false;
                _ = _settingsService?.LogAsync("读取剪贴板图片超时；鼠标钩子保持卸下，直到这次读取返回");
                NoticeRequested?.Invoke(L("Pin_Notice_ClipboardTimeout", "剪贴板没有及时返回，本次贴图已取消。现在可以继续点击其他窗口"));
                _ = readTask.ContinueWith(
                    task =>
                    {
                        if (task.IsFaulted)
                            _ = task.Exception;
                        if (resume)
                            _mouse.ResumeHook();
                        _clipboardGate.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                return;
            }

            ClipboardPayload payload = await readTask;
            _mouse.ResumeHook();
            hookOff = false;
            if (!_initialized || !_runtimeEnabled)
                return;

            switch (payload.Status)
            {
                case ClipboardReadStatus.Empty:
                    NoticeRequested?.Invoke(L("Pin_Notice_ClipboardEmpty", "剪贴板里没有图片"));
                    return;
                case ClipboardReadStatus.Busy:
                    NoticeRequested?.Invoke(L("Pin_Notice_ClipboardOccupied", "剪贴板正被其他程序占用，请稍后重试"));
                    return;
                case ClipboardReadStatus.Failed:
                    _ = _settingsService?.LogAsync($"读取剪贴板图片失败: {payload.Detail}");
                    NoticeRequested?.Invoke(L("Pin_Notice_ClipboardReadFailed", "读取剪贴板图片失败"));
                    return;
            }

            BitmapSource? image = CreatePinImage(payload);
            if (image is null)
            {
                _ = _settingsService?.LogAsync("剪贴板图片无法显示");
                NoticeRequested?.Invoke(L("Pin_Notice_ClipboardInvalidImage", "剪贴板里的图片无法显示"));
                return;
            }

            SpawnPin(image);
        }
        catch (Exception ex)
        {
            _ = _settingsService?.LogAsync($"读取剪贴板图片失败: {ex}");
            NoticeRequested?.Invoke(L("Pin_Notice_ClipboardReadFailed", "读取剪贴板图片失败"));
        }
        finally
        {
            if (hookOff)
                _mouse.ResumeHook();
            if (releaseGate)
                _clipboardGate.Release();
        }
    }

    public void Shutdown()
    {
        RunOnUi(() =>
        {
            if (!_initialized)
                return;

            _pollTimer?.Stop();
            _pollTimer = null;
            _mouse.Stop();

            _selector?.Close();
            _selector = null;
            foreach (PinImageWindow window in _pinWindows.ToArray())
                window.Close();
            _pinWindows.Clear();

            _overlay?.Close();
            _overlay = null;
            _points.Clear();
            _pendingTracePoint = null;
            _gesturePointLimitReached = false;
            _clearOverlayAt = null;
            _initialized = false;
        });
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (_clearOverlayAt is DateTime deadline && DateTime.UtcNow >= deadline)
        {
            CloseGestureOverlay();
            _clearOverlayAt = null;
        }

        int count = 0;
        while (count++ < 32 && _mouse.TryDequeuePinClose(out nint pinHwnd))
            ClosePinByHwnd(pinHwnd);

        count = 0;
        while (count++ < 512 && _mouse.TryDequeue(out MouseGestureEvent gestureEvent))
        {
            switch (gestureEvent.Kind)
            {
                case MouseGestureEventKind.Begin:
                    BeginGesture(gestureEvent.X, gestureEvent.Y);
                    break;
                case MouseGestureEventKind.Move:
                    MoveGesture(gestureEvent.X, gestureEvent.Y);
                    break;
                case MouseGestureEventKind.End:
                    EndGesture(gestureEvent.X, gestureEvent.Y);
                    break;
                case MouseGestureEventKind.ReplayRightClick:
                    _mouse.ReplayRightClick();
                    break;
            }
        }

        // 队列内可能有大量 2px 间隔的 Move；保留全部匹配点，但每帧只更新一次 WPF 折线。
        if (_pendingTracePoint is System.Windows.Point tracePoint)
        {
            _pendingTracePoint = null;
            if (_runtimeEnabled && _settings.DrawTrace && _points.Count > 0)
                _overlay?.AddPhysicalPoint((int)tracePoint.X, (int)tracePoint.Y);
        }
    }

    private void ClosePinByHwnd(nint hwnd)
    {
        if (hwnd == 0)
            return;
        foreach (PinImageWindow window in _pinWindows.ToArray())
        {
            if (new WindowInteropHelper(window).Handle == hwnd)
            {
                window.RequestClose();
                return;
            }
        }
    }

    private void BeginGesture(int x, int y)
    {
        if (!_runtimeEnabled)
            return;

        _clearOverlayAt = null;
        _pendingTracePoint = null;
        // 每轮创建新的分层窗口，避免 DWM 重显上一轮的缓存帧；结束即销毁窗口。
        CloseGestureOverlay();
        if (_settings.DrawTrace)
        {
            _overlay = new GestureOverlayWindow();
            _overlay.SetTraceStyle(_settings.GestureColor, _settings.TraceWidth);
            _overlay.ShowForGesture();
        }
        _points.Clear();
        _points.Add(new System.Windows.Point(x, y));
        _gesturePointLimitReached = false;
        if (_settings.DrawTrace)
            _overlay?.AddPhysicalPoint(x, y);
    }

    private void MoveGesture(int x, int y)
    {
        if (!_runtimeEnabled || _points.Count == 0 || _gesturePointLimitReached)
            return;

        if (_points.Count >= MaxGesturePoints)
        {
            // 极长或异常轨迹不允许无限增长并在松手时排序/匹配数十万点。
            _points.Clear();
            _pendingTracePoint = null;
            _gesturePointLimitReached = true;
            CloseGestureOverlay();
            _ = _settingsService?.LogAsync($"手势轨迹超过 {MaxGesturePoints} 个采样点，已取消本次框选");
            return;
        }

        _points.Add(new System.Windows.Point(x, y));
        if (_settings.DrawTrace)
            _pendingTracePoint = new System.Windows.Point(x, y);
    }

    private void EndGesture(int x, int y)
    {
        if (!_runtimeEnabled || _points.Count == 0)
        {
            ResetGesture();
            return;
        }

        _points.Add(new System.Windows.Point(x, y));
        if (_settings.DrawTrace)
            _overlay?.AddPhysicalPoint(x, y);

        System.Windows.Point[] capturedPoints = _points.ToArray();
        _points.Clear();
        _pendingTracePoint = null;
        if (!_matcher.TryMatchRectangle(capturedPoints, out Rect bounds, out _))
        {
            _overlay?.ShowResult(L("Pin_Overlay_Unmatched", "未匹配"), _settings.FailureColor);
            _clearOverlayAt = DateTime.UtcNow.AddMilliseconds(450);
            return;
        }

        // 销毁本轮轨迹窗后让桌面合成器异步提交一帧，再截屏；不阻塞 UI 等待 DWM。
        CloseGestureOverlay();
        if (_pendingGestureCaptures >= MaxPendingGestureCaptures)
        {
            NoticeRequested?.Invoke(L("Pin_Notice_CaptureBusy", "截图处理中，请稍后再试"));
            return;
        }
        _pendingGestureCaptures++;
        _ = CaptureGestureRegionAsync(bounds);
    }

    private void SpawnPin(BitmapSource image, System.Windows.Point? physicalPosition = null)
    {
        var window = new PinImageWindow(
            image,
            _settings.Alpha,
            _settings.MinAlpha,
            _settings.MaxAlpha,
            _settings.AlphaStep,
            physicalPosition);
        nint windowHwnd = 0;
        window.SourceInitialized += (_, _) =>
        {
            windowHwnd = new WindowInteropHelper(window).Handle;
            if (windowHwnd != 0)
                _mouse.AddClickPassthroughWindow(windowHwnd);
        };
        window.Closed += (_, _) =>
        {
            if (windowHwnd != 0)
                _mouse.RemoveClickPassthroughWindow(windowHwnd);
            _pinWindows.Remove(window);
        };
        _pinWindows.Add(window);
        window.Show();
    }

    private void SpawnCapturedPin(BitmapSource image, System.Windows.Point? physicalPosition = null)
    {
        if (_settings.CopyCaptureToClipboard && _dispatcher is not null)
            _ = CopyCapturedImageToClipboardAsync(image);
        SpawnPin(image, physicalPosition);
    }

    private async Task CopyCapturedImageToClipboardAsync(BitmapSource image)
    {
        if (!await _clipboardGate.WaitAsync(0))
        {
            NoticeRequested?.Invoke(L("Pin_Notice_PinShownClipboardBusy", "贴图已显示，但剪贴板仍在处理中"));
            return;
        }

        bool hookOff = false;
        try
        {
            byte[] dib = ClipboardImageIo.EncodeFromSource(image);
            hookOff = await Task.Run(() => _mouse.SuspendHook(TimeSpan.FromMilliseconds(500)));
            if (!hookOff)
            {
                NoticeRequested?.Invoke(L("Pin_Notice_PinShownClipboardUnsafe", "贴图已显示，但暂时无法安全写入剪贴板"));
                return;
            }

            bool wrote = await Task.Run(() => ClipboardImageIo.TryWriteDib(dib));
            if (!wrote)
            {
                _ = _settingsService?.LogAsync("截图后写入剪贴板失败");
                NoticeRequested?.Invoke(L("Pin_Notice_PinShownClipboardCopyFailed", "贴图已显示，但复制到剪贴板失败"));
            }
        }
        catch (Exception ex)
        {
            _ = _settingsService?.LogAsync($"截图后复制到剪贴板失败: {ex}");
            NoticeRequested?.Invoke(L("Pin_Notice_PinShownClipboardCopyFailed", "贴图已显示，但复制到剪贴板失败"));
        }
        finally
        {
            if (hookOff)
                _mouse.ResumeHook();
            _clipboardGate.Release();
        }
    }

    private static BitmapSource? CreatePinImage(ClipboardPayload payload)
    {
        (double dpiX, double dpiY) = GetCurrentCursorMonitorDpi();
        if (payload.Kind == ClipboardPayloadKind.Png)
        {
            if (payload.Data is null || payload.Data.Length == 0)
                return null;
            using var stream = new MemoryStream(payload.Data, writable: false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;
            BitmapSource frame = decoder.Frames[0];
            if (frame.PixelWidth is < 2 or > ClipboardImageIo.MaxDimension
                || frame.PixelHeight is < 2 or > ClipboardImageIo.MaxDimension)
                return null;
            int width = frame.PixelWidth;
            int height = frame.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            BitmapSource converted = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(pixels, stride, 0);

            BitmapSource pngImage = BitmapSource.Create(
                width,
                height,
                dpiX,
                dpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            pngImage.Freeze();
            return pngImage;
        }

        if (payload.Data is null
            || payload.Width is < 2 or > ClipboardImageIo.MaxDimension
            || payload.Height is < 2 or > ClipboardImageIo.MaxDimension
            || payload.Data.Length < payload.Width * payload.Height * 4)
            return null;

        BitmapSource image = BitmapSource.Create(
            payload.Width,
            payload.Height,
            dpiX,
            dpiY,
            PixelFormats.Bgra32,
            null,
            payload.Data,
            payload.Width * 4);
        image.Freeze();
        return image;
    }

    private void ResetGesture()
    {
        _points.Clear();
        _gesturePointLimitReached = false;
        _clearOverlayAt = null;
        CloseGestureOverlay();
    }

    private void CloseGestureOverlay()
    {
        GestureOverlayWindow? overlay = _overlay;
        _overlay = null;
        if (overlay is null)
            return;

        overlay.Dispatcher.VerifyAccess();
        overlay.Close();
    }

    private async Task CaptureGestureRegionAsync(Rect bounds)
    {
        // 让 DWM 至少有一帧时间移除已销毁的轨迹层。Task.Delay 不占用 UI 线程，
        // 避免 DwmFlush/同步 Render 在图形驱动或桌面合成器繁忙时长时间卡住输入。
        try
        {
            await Task.Delay(34);
            if (!_initialized)
                return;

            int left = (int)Math.Floor(bounds.Left);
            int top = (int)Math.Floor(bounds.Top);
            int right = (int)Math.Ceiling(bounds.Right);
            int bottom = (int)Math.Ceiling(bounds.Bottom);
            BitmapSource image = await Task.Run(() =>
                _capture.CaptureRegion(left, top, right, bottom));
            if (!_initialized)
                return;
            SpawnCapturedPin(image, new System.Windows.Point(left, top));
        }
        catch (Exception ex)
        {
            _ = _settingsService?.LogAsync($"手势截图失败: {ex}");
            NoticeRequested?.Invoke(L("Pin_Notice_GestureCaptureFailed", "手势区域截图失败"));
        }
        finally
        {
            _pendingGestureCaptures--;
        }
    }

    private void RunOnUi(Action action)
    {
        Dispatcher? dispatcher = _dispatcher ?? System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    private static (double X, double Y) GetCurrentCursorMonitorDpi()
    {
        try
        {
            if (GetCursorPos(out NativePoint pt))
            {
                nint monitor = MonitorFromPoint(pt, 2); // MONITOR_DEFAULTTONEAREST
                if (monitor != 0 && GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
                    return (dpiX, dpiY);
            }
        }
        catch
        {
        }
        return (96, 96);
    }
}
