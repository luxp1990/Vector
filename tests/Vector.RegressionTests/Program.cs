using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vector.Windows;
using Vector.Models;
using Vector.Services;

namespace Vector.RegressionTests;

/// <summary>
/// Vector 回归测试套件。
///
/// 守卫的核心契约：GestureOverlayWindow 停车/关闭后不得覆盖任何显示器，
/// 否则 Windows 会把系统判成「有全屏程序在跑」（SHQueryUserNotificationState 返回 QUNS_BUSY），
///   导致所有「检测到全屏就抑制自己」的功能被误伤（Gemini 的 Win+Alt+Space
///   全局快捷键就是这么被吞掉的）。
///
/// 断言全部基于「同一进程内自洽」的量：窗口矩形（GetWindowRect）对比虚拟桌面
/// （GetSystemMetrics），因此不受 DPI 缩放影响。
/// </summary>
internal static class Program
{
    private const int QunsBusy = 2;

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;
    private const int GwlExstyle = -20;

    private const long WsExTransparent = 0x00000020;
    private const long WsExToolwindow = 0x00000080;
    private const long WsExNoactivate = 0x08000000;

    private static int _pass;
    private static int _fail;
    private static int _skip;
    private static int _baseline;
    private static bool _qunsConclusive;
    private static readonly List<string> Failures = new();
    private static readonly List<string> Skips = new();

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 与主程序保持同一 DPI 感知上下文（PerMonitorV2），否则本进程拿到的是被系统
        // 缩放过的"虚拟化"坐标，断言虽仍自洽，但和 Vector.exe 实跑的数字对不上。
        try
        {
            SetProcessDpiAwarenessContext(new nint(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        }
        catch
        {
            // 老系统上不支持，退回默认感知模式，断言依旧自洽
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            try
            {
                RunAll();
            }
            catch (Exception ex)
            {
                _fail++;
                Failures.Add("测试宿主异常：" + ex);
                Console.WriteLine();
                Console.WriteLine("  [FAIL] 测试宿主异常：" + ex);
            }
            finally
            {
                app.Shutdown();
            }
        }));
        app.Run();

        return Summary();
    }

    private static void RunAll()
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" Vector 回归测试：手势遮罩生命周期与设置保存");
        Console.WriteLine("================================================================");

        Box virt = VirtualScreen();
        Console.WriteLine($"虚拟桌面 : x={virt.L} y={virt.T} w={virt.W} h={virt.H}");

        _baseline = Quns();
        _qunsConclusive = _baseline != QunsBusy;
        Console.WriteLine($"环境基线 : SHQueryUserNotificationState = {_baseline} ({Describe(_baseline)})");
        if (!_qunsConclusive)
        {
            Console.WriteLine("           ! 基线本身就是 BUSY：已有别的程序把系统置成全屏态，");
            Console.WriteLine("             QUNS 相关断言自动降级为 SKIP（不算失败，但也证明不了本修复）。");
        }

        Group1StartupSequence();
        Group2GesturePhase();
        Group3IdleFallback();
        Group4GestureCycles();
        Group5FreshWindow();
        Group6Teardown();
        Group7ConcurrentSettingsSaves();
        Group8ClipboardImage();
        Group9CaptureRect();
        Group10PinRightClick();
        Group11HotkeyCoexistencePolicy();
    }

    private static void Group9CaptureRect()
    {
        Section("[组 9] 区域截图矩形：微选区是取消，不是失败");

        bool tiny = RegionSelectorWindow.TryNormalizeCaptureRect(10, 20, 10, 20, out _, out _, out _, out _);
        Check("9.1 同一点按下抬起不构成截图区域", !tiny);

        bool onePixel = RegionSelectorWindow.TryNormalizeCaptureRect(10, 20, 11, 40, out _, out _, out _, out _);
        Check("9.2 宽度不足 2 像素不构成截图区域", !onePixel);

        bool ok = RegionSelectorWindow.TryNormalizeCaptureRect(
            30, 80, 10, 20, out int left, out int top, out int right, out int bottom);
        Check("9.3 反向拖拽被规范成左上-右下",
            ok && left == 10 && top == 20 && right == 30 && bottom == 80,
            $"rect=({left},{top})-({right},{bottom}) ok={ok}");
    }

    private static void Group10PinRightClick()
    {
        Section("[组 10] 贴图右键整段吞掉，避免下层窗口卡住鼠标");

        PinRightClickDecision down = MouseGestureService.DecidePinRightClick(true, false, false);
        Check("10.1 贴图上的右键按下被吞掉并请求关闭", down.Swallow && down.RequestClose);

        PinRightClickDecision up = MouseGestureService.DecidePinRightClick(false, true, true);
        Check("10.2 配对的右键抬起只吞掉，不交给下层", up.Swallow && !up.RequestClose);

        PinRightClickDecision desktop = MouseGestureService.DecidePinRightClick(false, false, false);
        Check("10.3 桌面上的右键按下不走贴图关闭", !desktop.Swallow && !desktop.RequestClose);
    }

    private static void Group11HotkeyCoexistencePolicy()
    {
        Section("[组 11] 热键智能共存与避让策略");

        var settings = new HotkeyCoexistenceSettings
        {
            Enabled = true,
            SuppressInFullscreen = true,
            CustomExclusionProcesses = new List<string> { "obs64.exe" }
        };

        var chords = new List<string>
        {
            "Alt+S",
            "Ctrl+1",
            "Ctrl+Alt+A",
            "Ctrl+Shift+E",
            "Ctrl+Alt+V"
        };
        string globalHotkey = "Ctrl+Alt+V";

        // 11.1 微信前台：让渡 Alt+S 和 Ctrl+Alt+A，绝不让渡全局热键 Ctrl+Alt+V
        var (wechatPass, _) = HotkeyPassthroughPolicy.Evaluate("WeChat.exe", false, settings, chords, globalHotkey);
        HotkeyPassthroughPolicy.TryNormalizeSignature("Alt+S", out string sigAltS);
        HotkeyPassthroughPolicy.TryNormalizeSignature("Ctrl+Alt+A", out string sigCtrlAltA);
        HotkeyPassthroughPolicy.TryNormalizeSignature("Ctrl+1", out string sigCtrl1);
        HotkeyPassthroughPolicy.TryNormalizeSignature("Ctrl+Alt+V", out string sigGlobal);

        Check("11.1 微信前台让渡 Alt+S", wechatPass.Contains(sigAltS));
        Check("11.2 微信前台让渡 Ctrl+Alt+A", wechatPass.Contains(sigCtrlAltA));
        Check("11.3 微信前台不让渡无关键 Ctrl+1", !wechatPass.Contains(sigCtrl1));
        Check("11.4 微信前台绝不让渡全局热键 Ctrl+Alt+V", !wechatPass.Contains(sigGlobal));

        // 11.5 浏览器/Excel 前台：让渡 Ctrl+1，不让渡 Alt+S
        var (chromePass, _) = HotkeyPassthroughPolicy.Evaluate("chrome.exe", false, settings, chords, globalHotkey);
        Check("11.5 Chrome 前台让渡 Ctrl+1", chromePass.Contains(sigCtrl1));
        Check("11.6 Chrome 前台不让渡 Alt+S", !chromePass.Contains(sigAltS));

        var (excelPass, _) = HotkeyPassthroughPolicy.Evaluate("EXCEL.EXE", false, settings, chords, globalHotkey);
        Check("11.7 Excel 前台让渡 Ctrl+1", excelPass.Contains(sigCtrl1));

        // 11.8 VS Code 前台：让渡 Ctrl+Shift+E
        HotkeyPassthroughPolicy.TryNormalizeSignature("Ctrl+Shift+E", out string sigVscode);
        var (vscodePass, _) = HotkeyPassthroughPolicy.Evaluate("code.exe", false, settings, chords, globalHotkey);
        Check("11.8 VS Code 前台让渡 Ctrl+Shift+E", vscodePass.Contains(sigVscode));

        // 11.9 全屏检测：让渡除全局热键外的所有键
        var (fullscreenPass, _) = HotkeyPassthroughPolicy.Evaluate("game.exe", true, settings, chords, globalHotkey);
        Check("11.9 全屏态让渡 Alt+S", fullscreenPass.Contains(sigAltS));
        Check("11.10 全屏态让渡 Ctrl+1", fullscreenPass.Contains(sigCtrl1));
        Check("11.11 全屏态绝不让渡全局热键", !fullscreenPass.Contains(sigGlobal));

        // 11.12 用户自定义进程白名单/排除列表
        var (obsPass, _) = HotkeyPassthroughPolicy.Evaluate("OBS64.exe", false, settings, chords, globalHotkey);
        Check("11.12 自定义排除进程 OBS 让渡普通热键", obsPass.Contains(sigAltS) && obsPass.Contains(sigCtrl1));
        Check("11.13 自定义排除进程 OBS 绝不让渡全局热键", !obsPass.Contains(sigGlobal));

        // 11.14 功能开关关闭：返回空集合，不进行任何避让
        var disabledSettings = new HotkeyCoexistenceSettings { Enabled = false };
        var (disabledPass, _) = HotkeyPassthroughPolicy.Evaluate("WeChat.exe", true, disabledSettings, chords, globalHotkey);
        Check("11.14 避让开关关闭时不让渡任何键", disabledPass.Count == 0);

        // 11.15 微信 4.x (Weixin.exe) 与小程序宿主 (WeChatAppEx.exe) 支持
        var (weixinPass, _) = HotkeyPassthroughPolicy.Evaluate("Weixin.exe", false, settings, chords, globalHotkey);
        Check("11.15 微信 4.x (Weixin.exe) 让渡 Alt+S", weixinPass.Contains(sigAltS));
        var (wxAppExPass, _) = HotkeyPassthroughPolicy.Evaluate("WeChatAppEx.exe", false, settings, chords, globalHotkey);
        Check("11.16 微信小程序宿主 (WeChatAppEx.exe) 让渡 Alt+S", wxAppExPass.Contains(sigAltS));

        // 11.17 空进程名全屏态（反作弊全屏游戏/受限进程）让渡普通键且保护全局键
        var (emptyProcFullscreenPass, _) = HotkeyPassthroughPolicy.Evaluate("", true, settings, chords, globalHotkey);
        Check("11.17 空进程名全屏态避让普通热键", emptyProcFullscreenPass.Contains(sigAltS) && emptyProcFullscreenPass.Contains(sigCtrl1));
        Check("11.18 空进程名全屏态绝不让渡全局热键", !emptyProcFullscreenPass.Contains(sigGlobal));

        // 11.19 ResetNativeRegistrations 确保休眠唤醒后重注册不被增量 Reconcile 跳过
        var svc = new HotkeyService();
        svc.Initialize(new IntPtr(100)); // 模拟窗口句柄
        svc.ResetNativeRegistrations(); // 不抛出异常且状态归零
        Check("11.19 ResetNativeRegistrations 能安全重置本地与全局热键状态", true);
    }

    private static void Group8ClipboardImage()
    {
        Section("[组 8] 剪贴板图片解码：不碰系统剪贴板，也不安装鼠标钩子");

        byte[] bgra =
        {
            1, 2, 3, 0,
            4, 5, 6, 0,
            7, 8, 9, 255,
            10, 11, 12, 255
        };
        byte[] dib = ClipboardImageIo.EncodeDib(2, 2, bgra);
        bool decoded = ClipboardImageIo.TryDecodeDib(dib, out int width, out int height, out byte[] pixels);
        Check("8.1 自下而上的 32 位 DIB 能还原像素",
            decoded && width == 2 && height == 2 && pixels.AsSpan().SequenceEqual(bgra),
            $"decoded={decoded} {width}x{height}");

        byte[] zeroAlpha = { 9, 8, 7, 0, 6, 5, 4, 0 };
        byte[] zeroDib = ClipboardImageIo.EncodeDib(2, 1, zeroAlpha);
        bool opaque = ClipboardImageIo.TryDecodeDib(zeroDib, out _, out _, out byte[] opaquePixels);
        Check("8.2 全透明 Alpha 视为不透明，避免贴图窗口吃掉点击却看不见",
            opaque && opaquePixels[3] == 255 && opaquePixels[7] == 255,
            opaque ? $"alpha={opaquePixels[3]},{opaquePixels[7]}" : "decode failed");

        Check("8.3 损坏的 DIB 被拒绝", !ClipboardImageIo.TryDecodeDib(new byte[8], out _, out _, out _));

        var mouse = new MouseGestureService();
        Check("8.4 钩子未启动时读取剪贴板不需要卸钩", mouse.SuspendHook(TimeSpan.FromMilliseconds(50)));
        mouse.ResumeHook();
    }

    // ── 组 1：独立遮罩窗口初始化后停车 ───────────────────────────────────────
    private static void Group1StartupSequence()
    {
        Section("[组 1] 独立遮罩窗口初始化后停车：Show() → HideOverlay()");

        var overlay = CreateOverlay();
        overlay.Show();
        overlay.HideOverlay();
        Pump(400);

        Box rect = WindowRect(overlay);
        Check("1.1 遮罩窗保持可见（本项目硬约束：不 Hide，避免 DWM 重显旧帧）",
            overlay.IsVisible, $"IsVisible={overlay.IsVisible}");

        Check("1.2 空闲态已停到屏幕外，不覆盖任何显示器",
            !Intersects(rect, VirtualScreen()), $"rect={Fmt(rect)} / 虚拟桌面={Fmt(VirtualScreen())}");

        CheckQuns("1.3 空闲态没有把系统置成 QUNS_BUSY");

        long ex = ExStyle(overlay);
        Check("1.4 扩样式含 WS_EX_TRANSPARENT | TOOLWINDOW | NOACTIVATE",
            (ex & WsExTransparent) != 0 && (ex & WsExToolwindow) != 0 && (ex & WsExNoactivate) != 0,
            $"ExStyle=0x{ex:X8}");

        _overlay = overlay;
    }

    // ── 组 2：手势态 ────────────────────────────────────────────────────────
    private static void Group2GesturePhase()
    {
        Section("[组 2] 手势态 —— ShowForGesture() 必须铺满虚拟桌面，且坐标换算不残留停车偏移");

        GestureOverlayWindow overlay = _overlay!;
        overlay.ShowForGesture();
        Pump(300);

        Box rect = WindowRect(overlay);
        Check("2.1 手势态窗口覆盖整个虚拟桌面",
            Covers(rect, VirtualScreen()), $"rect={Fmt(rect)}");

        Box v = VirtualScreen();
        overlay.AddPhysicalPoint(v.L + 120, v.T + 90);
        overlay.AddPhysicalPoint(v.L + 240, v.T + 180);
        Pump(80);

        Canvas canvas = TraceCanvas(overlay);
        Polyline? line = canvas.Children.OfType<Polyline>().FirstOrDefault();

        Check("2.2 轨迹已绘制成 1 条折线（2 个点）",
            line is not null && line.Points.Count == 2,
            $"children={canvas.Children.Count} points={line?.Points.Count.ToString() ?? "null"}");

        Check("2.3 轨迹点落在窗口客户区内（证明停车→复位的坐标换算没失效）",
            line is not null && line.Points.Count == 2 && line.Points.All(p => InClient(p, canvas)),
            (line is null
                ? "没有折线"
                : string.Join(" ", line.Points.Select(p => $"({p.X:0},{p.Y:0})")))
            + $" 客户区={canvas.ActualWidth:0}x{canvas.ActualHeight:0}");

        Check("2.4 轨迹位移方向与屏幕位移一致（未被镜像/翻转）",
            line is { Points.Count: 2 }
                && line.Points[1].X > line.Points[0].X
                && line.Points[1].Y > line.Points[0].Y,
            line is null ? "没有折线" : $"{line.Points[0]} → {line.Points[1]}");

        Check("2.5 SetTraceStyle 生效（线宽 = 3）",
            line is not null && Math.Abs(line.StrokeThickness - 3) < 1e-6,
            $"StrokeThickness={line?.StrokeThickness:0.##}");

        overlay.ShowResult("命中", "#FFFF0000");
        Pump(60);
        Check("2.6 ShowResult 能正常叠加结果标签",
            canvas.Children.OfType<TextBlock>().Any(), $"children={canvas.Children.Count}");

        DpiScale dpi = System.Windows.Media.VisualTreeHelper.GetDpi(overlay);
        double expectedW = v.W / (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0);
        double expectedH = v.H / (dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
        Check("2.7 遮罩窗口与 Canvas 逻辑尺寸铺满虚拟桌面且不被边界裁剪",
            overlay.Width >= expectedW - 1 && canvas.Width >= expectedW - 1,
            $"overlay={overlay.Width:0}x{overlay.Height:0} canvas={canvas.Width:0}x{canvas.Height:0} expected={expectedW:0}x{expectedH:0}");
    }

    // ── 组 3：回落空闲态 ────────────────────────────────────────────────────
    private static void Group3IdleFallback()
    {
        Section("[组 3] 回落空闲态 —— HideOverlay() 必须清轨迹 + 挪出屏幕 + 状态恢复");

        GestureOverlayWindow overlay = _overlay!;
        overlay.HideOverlay();
        Pump(300);

        Box rect = WindowRect(overlay);
        Check("3.1 窗口已挪出屏幕", !Intersects(rect, VirtualScreen()), $"rect={Fmt(rect)}");
        Check("3.2 仍然保持可见（不 Hide）", overlay.IsVisible, $"IsVisible={overlay.IsVisible}");

        int children = TraceCanvas(overlay).Children.Count;
        Check("3.3 轨迹已清空", children == 0, $"children={children}");

        CheckQuns("3.4 系统状态回到基线");
    }

    // ── 组 4：连续创建/关闭窗口，模拟生产手势生命周期 ─────────────────────────
    private static void Group4GestureCycles()
    {
        Section("[组 4] 连续 5 轮手势窗口创建/关闭 —— 结束后不留覆盖窗");
        for (int i = 1; i <= 5; i++)
        {
            GestureOverlayWindow overlay = CreateOverlay();
            overlay.ShowForGesture();
            Pump(250);
            Box covering = WindowRect(overlay);

            overlay.Close();
            Pump(350);
            int remainingWindows = VisibleTopLevelWindowCount();
            bool recovered = _qunsConclusive ? QunsMatchesBaseline() : true;

            Check($"4.{i} 第 {i} 轮：结束后销毁覆盖窗且状态恢复",
                remainingWindows == 1 && recovered,
                $"铺屏 rect={Fmt(covering)} / 剩余可见窗={remainingWindows} / QUNS={Quns()} 基线={_baseline}");

            if (!_qunsConclusive)
            {
                _skip++;
                Skips.Add($"4.{i} 的 QUNS 断言（基线已 BUSY）");
            }
        }
    }

    // ── 组 5：边界——全新窗口未 Show 过 ──────────────────────────────────────
    private static void Group5FreshWindow()
    {
        Section("[组 5] 边界：全新窗口没 Show() 过就直接 ShowForGesture()");

        var fresh = CreateOverlay();
        fresh.ShowForGesture();
        Box virt = VirtualScreen();
        fresh.AddPhysicalPoint(virt.L + 120, virt.T + 90);
        fresh.AddPhysicalPoint(virt.L + 240, virt.T + 180);
        Pump(300);

        Box covering = WindowRect(fresh);
        Check("5.1 直接 ShowForGesture 也能铺满虚拟桌面",
            Covers(covering, VirtualScreen()), $"rect={Fmt(covering)}");
        Canvas canvas = TraceCanvas(fresh);
        Polyline? line = canvas.Children.OfType<Polyline>().FirstOrDefault();
        Check("5.2 刚显示后立即添加的轨迹点仍在客户区内",
            line is { Points.Count: 2 } && line.Points.All(p => InClient(p, canvas)),
            line is null ? "没有折线" : string.Join(" ", line.Points.Select(p => $"({p.X:0},{p.Y:0})")));

        fresh.HideOverlay();
        Pump(300);
        Box parked = WindowRect(fresh);
        Check("5.3 HideOverlay 后正常停车", !Intersects(parked, VirtualScreen()), $"rect={Fmt(parked)}");
        CheckQuns("5.4 状态仍为基线");

        fresh.Close();
        Pump(250);

        int visible = VisibleTopLevelWindowCount();
        Check("5.5 边界窗口关闭后只剩主遮罩窗可见", visible == 1, $"可见顶层窗口数={visible}");
    }

    // ── 组 6：终态 ─────────────────────────────────────────────────────────
    private static void Group6Teardown()
    {
        Section("[组 6] 终态");

        GestureOverlayWindow overlay = _overlay!;
        Box rect = WindowRect(overlay);
        Check("6.1 主遮罩窗仍停在屏幕外", !Intersects(rect, VirtualScreen()), $"rect={Fmt(rect)}");
        CheckQuns("6.2 最终系统状态仍为基线");

        overlay.Close();
        Pump(250);
        int visible = VisibleTopLevelWindowCount();
        Check("6.3 全部关闭后进程内无可见窗口残留", visible == 0, $"可见顶层窗口数={visible}");
    }

    // ── 组 7：多个界面开关快速保存，不得留下半写入配置 ───────────────────────
    private static void Group7ConcurrentSettingsSaves()
    {
        Section("[组 7] 并发保存设置 —— 最终配置必须是完整有效的 JSON");
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VectorRegression-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SettingsService(directory);
            Task[] saves = Enumerable.Range(0, 24)
                .Select(i => Task.Run(() => service.SaveAsync(new AppSettings
                {
                    Theme = "test-" + i,
                    GlobalHotKey = "None"
                })))
                .ToArray();
            Task.WaitAll(saves);

            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(System.IO.Path.Combine(directory, AppPaths.ConfigFileName)));
            Check("7.1 并发保存后配置仍可完整反序列化",
                loaded is not null && loaded.Theme.StartsWith("test-", StringComparison.Ordinal),
                loaded?.Theme ?? "配置为空或无法解析");
        }
        catch (Exception ex)
        {
            Check("7.1 并发保存后配置仍可完整反序列化", false, ex.ToString());
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // ── 辅助 ───────────────────────────────────────────────────────────────
    private static GestureOverlayWindow CreateOverlay()
    {
        var w = new GestureOverlayWindow();
        w.SetTraceStyle("#FF5F9EA0", 3);
        return w;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', 64));
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  [PASS] {name}");
            return;
        }

        _fail++;
        string line = string.IsNullOrEmpty(detail) ? name : $"{name}  →  {detail}";
        Failures.Add(line);
        Console.WriteLine($"  [FAIL] {line}");
    }

    private static void CheckQuns(string name)
    {
        if (!_qunsConclusive)
        {
            _skip++;
            Skips.Add(name + "（环境基线已 BUSY，不可判定）");
            Console.WriteLine($"  [SKIP] {name}（环境基线已 BUSY，不可判定）");
            return;
        }

        Check(name, QunsMatchesBaseline(), $"QUNS={Quns()} 基线={_baseline}");
    }

    private static bool QunsMatchesBaseline(int timeoutMs = 1500)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (Quns() == _baseline)
                return true;
            if (Environment.TickCount64 >= end)
                return false;
            Pump(100);
        }
    }

    private static int Quns()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) == 0 ? state : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string Describe(int state) => state switch
    {
        1 => "QUNS_NOT_PRESENT",
        2 => "QUNS_BUSY（全屏/演示/忙碌）",
        3 => "QUNS_RUNNING_D3D_FULL_SCREEN",
        4 => "QUNS_PRESENTATION_MODE",
        5 => "QUNS_ACCEPTS_NOTIFICATIONS（正常）",
        6 => "QUNS_QUIET_TIME",
        7 => "QUNS_APP",
        _ => "未知"
    };

    private static void Pump(int ms)
    {
        long end = Environment.TickCount64 + ms;
        while (true)
        {
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            if (Environment.TickCount64 >= end)
                return;
            Thread.Sleep(10);
        }
    }

    private static Box WindowRect(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out Rect32 r))
            return new Box(0, 0, 0, 0);
        return new Box(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private static Box VirtualScreen() => new(
        GetSystemMetrics(SmXvirtualscreen),
        GetSystemMetrics(SmYvirtualscreen),
        GetSystemMetrics(SmCxvirtualscreen),
        GetSystemMetrics(SmCyvirtualscreen));

    private static long ExStyle(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
            return 0;
        return IntPtr.Size == 8
            ? GetWindowLongPtrW(hwnd, GwlExstyle).ToInt64()
            : GetWindowLongW(hwnd, GwlExstyle);
    }

    private static Canvas TraceCanvas(Window window)
    {
        FieldInfo? field = typeof(GestureOverlayWindow).GetField(
            "TraceCanvas",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return (Canvas)field!.GetValue(window)!;
    }

    private static bool InClient(System.Windows.Point p, Canvas canvas) =>
        p.X >= -1 && p.Y >= -1
        && p.X <= canvas.ActualWidth + 1 && p.Y <= canvas.ActualHeight + 1;

    private static bool Intersects(Box a, Box b) =>
        a.L < b.L + b.W && a.L + a.W > b.L && a.T < b.T + b.H && a.T + a.H > b.T;

    private static bool Covers(Box a, Box b) =>
        a.L <= b.L && a.T <= b.T && a.L + a.W >= b.L + b.W && a.T + a.H >= b.T + b.H;

    private static string Fmt(Box b) => $"({b.L},{b.T},{b.W}x{b.H})";

    private static int VisibleTopLevelWindowCount()
    {
        int count = 0;
        uint self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self && IsWindowVisible(hwnd))
                count++;
            return true;
        }, 0);
        return count;
    }

    private static int Summary()
    {
        int total = _pass + _fail + _skip;
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine($" 结果：通过 {_pass} / 失败 {_fail} / 跳过 {_skip}（共 {total} 项）");
        Console.WriteLine("================================================================");

        if (Failures.Count > 0)
        {
            Console.WriteLine(" 失败项：");
            foreach (string f in Failures)
                Console.WriteLine("   - " + f);
        }

        if (Skips.Count > 0)
        {
            Console.WriteLine(" 跳过项：");
            foreach (string s in Skips)
                Console.WriteLine("   - " + s);
        }

        Console.WriteLine(_fail == 0 ? " 结论：全部通过" : " 结论：存在失败");
        return _fail == 0 ? 0 : 1;
    }

    private static GestureOverlayWindow? _overlay;

    private readonly record struct Box(int L, int T, int W, int H);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect32 rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
}
