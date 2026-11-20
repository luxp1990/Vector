
﻿using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Vector.Models;
using Vector.Services;
using Vector.Windows;
using Forms = System.Windows.Forms;
namespace Vector;
public partial class MainWindow : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
    // ✅ 1.0.1: UIPI 提权检测
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern bool IsUserAnAdmin();
    private readonly IHotkeyService _hotkeyService;
    private readonly ISettingsService _settingsService;
    private readonly IExecutorService _executorService;
    private readonly IFileTypeDetector _fileTypeDetector;
    private readonly IThemeService _themeService;
    private readonly IUndoService _undoService;
    private readonly IPinFeatureService _pinFeatureService;
    private AppSettings _settings = new();
    private int _editingIndex = -1;
    private bool _isListening = true;
    private bool _isPinActive = true;
    private bool _hotkeysPausedByGlobal;
    private bool _pinPausedByGlobal;
    private bool _suppressToggleSync = false;      // 程序性同步开关状态时抑制事件
    private bool _reallyExit = false;                   // Bug 11: 退出标志位
    private bool _hasShownMinimizeTip = false;          // Bug N4: 气泡只弹一次
    private bool _trayCheckSuppress = false;            // 托盘勾选项回写时避免递归
    private IntPtr _hwnd;
    private ForegroundProcessMonitor? _foregroundMonitor;
    private Action<ShortcutConfig>? _executeShortcutHandler;  // Bug 4: 保存委托引用
    private Action<PinHotkeyAction>? _pinHotkeyHandler;
    private HwndSource? _hwndSource;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _menuTray;
    private Forms.ToolStripMenuItem? _toggleListenItem;
    private Forms.ToolStripMenuItem? _togglePinItem;        // 托盘"暂停截图"菜单项
    public ObservableCollection<ShortcutConfig> LocalShortcuts { get; private set; } = new();

    /// <summary>
    /// ✅ 1.0.5: 类型排序开关。null = 按原始顺序；true = 按类型排序（程序→文件夹→网页）。
    /// 仅控制 UI 显示顺序，不写盘（重启即回到配置顺序）。
    /// </summary>
    private bool? _sortByType = null;
    private bool _advancedExpanded = false;

    /// <summary>
    /// ✅ 1.0.5: 首次加载配置时的原始顺序备份，用于"再次点击恢复"。
    /// 不持久化（重启后 _sortByType=null 自动重置为原始顺序）。
    /// </summary>
    private List<ShortcutConfig>? _originalOrder;
    private System.Collections.ObjectModel.ObservableCollection<ShortcutConfig>? _originalOrderSourceRef;
    public MainWindow(
        IHotkeyService hotkeyService,
        ISettingsService settingsService,
        IExecutorService executorService,
        IFileTypeDetector fileTypeDetector,
        IThemeService themeService,
        IUndoService undoService,
        IPinFeatureService pinFeatureService)
    {
        _hotkeyService = hotkeyService;
        _settingsService = settingsService;
        _executorService = executorService;
        _fileTypeDetector = fileTypeDetector;
        _themeService = themeService;
        _undoService = undoService;
        _pinFeatureService = pinFeatureService;
        InitializeComponent();
        DataContext = this;
        // 撤销栈状态变化时刷新按钮启用态
        _undoService.StateChanged += (_, _) => UpdateUndoButtonState();
        UpdateUndoButtonState();
        var cm = new System.Windows.Controls.ContextMenu
        {
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
        };
        var miEdit = new System.Windows.Controls.MenuItem { Header = "\u7F16\u8F91" };
        var miDelete = new System.Windows.Controls.MenuItem { Header = "\u5220\u9664" };
        cm.Items.Add(miEdit);
        cm.Items.Add(miDelete);
        LvShortcuts.ContextMenu = cm;
        cm.Opened += (s, e) =>
        {
            if (LvShortcuts.SelectedItems.Count == 0
                && cm.PlacementTarget is System.Windows.Controls.ListViewItem lvi)
                lvi.IsSelected = true;
        };
        miEdit.Click += (s, e) =>
        {
            var item = LvShortcuts.SelectedItem as ShortcutConfig;
            if (item != null) EnterEditMode(item);
        };
        miDelete.Click += (s, e) =>
        {
            if (LvShortcuts.SelectedItems.Count == 0) return;
            var selectedConfigs = LvShortcuts.SelectedItems
                .Cast<ShortcutConfig>()
                .ToList();
            // ✅ Undo: 删除前记录快照（包含选中数与首个目标 key 的描述）
            var desc = selectedConfigs.Count == 1
                ? $"删除 {selectedConfigs[0].Key}"
                : $"删除 {selectedConfigs.Count} 项";
            _undoService.Record(_settings.LocalShortcuts, UndoOperationType.Delete, desc);
            _hotkeyService.UnregisterAll();
            foreach (var cfg in selectedConfigs)
            {
                LocalShortcuts.Remove(cfg);
                _settings.LocalShortcuts.Remove(cfg);
                _originalOrder?.Remove(cfg);
            }
            var failedKeys = _isListening
                ? _hotkeyService.RegisterAll(_settings.LocalShortcuts)
                : new List<string>();
            if (failedKeys.Count > 0)
                System.Windows.MessageBox.Show(
                    $"以下快捷键注册失败（可能被占用）：\n{string.Join("\n", failedKeys)}",
                    "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            _ = _settingsService.SaveAsync(_settings);
            LblCount.Text = $"{_settings.LocalShortcuts.Count} \u9879";
            LblEmptyHint.Visibility = LocalShortcuts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // ✅ Bug N2: 删除后直接退出编辑模式，避免 _editingIndex 指向错误项目
            if (_editingIndex >= 0)
                BtnCancelEdit_Click(null!, null!);
        };
    }
    protected override async void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        _hotkeyService.Initialize(_hwnd);
        _hotkeyService.ToggleListening += OnToggleListening;
        // Bug 4: 用字段保存委托引用，确保能正确取消
        _executeShortcutHandler = cfg =>
        {
            if (_settings.HotkeyDiagnosticsEnabled)
                _ = _settingsService.LogAsync($"Hotkey callback dispatched: key={cfg.Key}; target={cfg.Target}");
            _executorService.Execute(cfg);
        };
        _hotkeyService.ExecuteShortcut += _executeShortcutHandler;
        _pinHotkeyHandler = action => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isPinActive) return;   // 手动关闭或全局暂停时忽略贴图热键
            if (action == PinHotkeyAction.Snapshot)
                _pinFeatureService.StartRegionCapture();
            else
                _pinFeatureService.PinClipboard();
        }));
        _hotkeyService.PinHotkeyTriggered += _pinHotkeyHandler;
        _pinFeatureService.NoticeRequested += OnPinNoticeRequested;
        // ✅ 1.0.1: 订阅脚本执行失败事件，弹气泡通知用户
        _executorService.ExecuteFailed += OnShortcutExecuteFailed;
        try
        {
            _settings = await _settingsService.LoadAsync();
            // ✅ 1.0.8: 先检查注册，再赋值文本框（避免失败后 UI 与实际状态不一致）
            if (_settings.GlobalHotKey != "None" && !_hotkeyService.RegisterGlobal(_settings.GlobalHotKey))
            {
                _settings.GlobalHotKey = "None";
                await _settingsService.SaveAsync(_settings);
            }
            TxtGlobalKey.Text = _settings.GlobalHotKey == "None" ? "" : _settings.GlobalHotKey;
            TxtStartupDelay.Text = Math.Clamp(_settings.StartupDelaySeconds, 0, 120).ToString();
            // 功能开关：快捷键与截图相互独立，按持久化状态恢复
            _settings.Pin ??= new PinSettings();
            _settings.Coexistence ??= new HotkeyCoexistenceSettings();
            _isListening = _settings.HotkeysEnabled;
            _isPinActive = _settings.Pin.Enabled;
            _hotkeysPausedByGlobal = false;
            _pinPausedByGlobal = false;
            _hotkeyService.SetDiagnosticLogging(_settings.HotkeyDiagnosticsEnabled);
            _executorService.SetDiagnosticLogging(_settings.HotkeyDiagnosticsEnabled);
            _suppressToggleSync = true;
            TglHotkeys.IsChecked = _isListening;
            TglPin.IsChecked = _settings.Pin.Enabled;
            ChkHotkeyDiagnostics.IsChecked = _settings.HotkeyDiagnosticsEnabled;
            ChkHotkeyCoexistence.IsChecked = _settings.Coexistence.Enabled;
            _suppressToggleSync = false;
            var failedKeys = _isListening
                ? _hotkeyService.RegisterAll(_settings.LocalShortcuts)
                : new List<string>();
            if (failedKeys.Count > 0)
                System.Windows.MessageBox.Show(
                    $"以下快捷键注册失败（可能被占用）：\n{string.Join("\n", failedKeys)}",
                    "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            // 应用持久化的主题（必须在 RefreshListView 之前）
            _themeService.Apply(_settings.Theme != "Light");
            RefreshListView();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"初始化失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _settings = new AppSettings();
            RefreshListView();
        }
        InitializePinFeatures();
        // 初始化后清空撤销栈：刚加载的状态不应有"撤销"
        _undoService.Clear();
        UpdateUndoButtonState();
        InitTray();
        UpdateStatusUi();
        _foregroundMonitor = new ForegroundProcessMonitor(Dispatcher);
        _foregroundMonitor.ForegroundChanged += OnForegroundProcessChanged;
        _foregroundMonitor.Start();
        // ✅ 休眠/解锁后重注册热键（RegisterHotKey 会话恢复后可能失效）
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // ✅ 1.0.1: 如果当前已是提权进程，托盘提示文案加 "[管理员]" 后缀
        if (IsUserAnAdmin())
        {
            _trayIcon!.Text = $"Vector - {CurrentStatusText()} [管理员]";
        }
    }
    protected override void OnClosed(EventArgs e)
    {
        if (_foregroundMonitor != null)
        {
            _foregroundMonitor.ForegroundChanged -= OnForegroundProcessChanged;
            _foregroundMonitor.Dispose();
            _foregroundMonitor = null;
        }
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _hotkeyService.UnregisterAll();
        _hotkeyService.UnregisterPinHotkeys();
        _pinFeatureService.Shutdown();
        _hwndSource?.RemoveHook(WndProc);
        _hotkeyService.ToggleListening -= OnToggleListening;
        _hotkeyService.ExecuteShortcut -= _executeShortcutHandler;
        _hotkeyService.PinHotkeyTriggered -= _pinHotkeyHandler;
        _pinFeatureService.NoticeRequested -= OnPinNoticeRequested;
        _executorService.ExecuteFailed -= OnShortcutExecuteFailed;  // ✅ 1.0.1: 避免事件持有已关闭窗口
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        base.OnClosed(e);
    }

    private void InitializePinFeatures()
    {
        _settings.Pin ??= new PinSettings();
        try
        {
            _pinFeatureService.Initialize(_settings.Pin);
            // 功能开关解耦：截图功能只受截图开关控制
            _pinFeatureService.SetEnabled(_isPinActive);
            if (_isPinActive
                && !_hotkeyService.RegisterPinHotkeys(
                    _settings.Pin.SnapshotHotkey,
                    _settings.Pin.ClipboardHotkey))
            {
                OnPinNoticeRequested("贴图快捷键注册失败，请在贴图设置中更换组合键");
            }
        }
        catch (Exception ex)
        {
            _ = _settingsService.LogAsync($"初始化贴图工具失败: {ex.Message}");
            OnPinNoticeRequested("贴图工具初始化失败，详情已写入日志");
        }
    }

    private void OnPinNoticeRequested(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsTrayIconValid)
                _trayIcon!.ShowBalloonTip(2500, "Vector · 贴图工具", message, Forms.ToolTipIcon.Info);
            else
                System.Windows.MessageBox.Show(
                    message,
                    "Vector · 贴图工具",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
        }));
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
            Dispatcher.BeginInvoke(new Action(ReregisterHotkeysAfterResume));
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Dispatcher.BeginInvoke(new Action(ReregisterHotkeysAfterResume));
    }
    /// <summary>锁屏解锁 / 休眠恢复后重新注册全局键与本地键。</summary>
    private void ReregisterHotkeysAfterResume()
    {
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        try
        {
            // 关键修复：清除旧的已注册状态缓存，强制向操作系统重新发起 RegisterHotKey 系统调用
            _hotkeyService.ResetNativeRegistrations();

            if (_settings.GlobalHotKey != "None")
                _hotkeyService.RegisterGlobal(_settings.GlobalHotKey);

            if (_isListening)
            {
                var failed = _hotkeyService.RegisterAll(_settings.LocalShortcuts);
                if (failed.Count > 0)
                {
                    System.Windows.MessageBox.Show(
                        $"系统恢复后以下快捷键重新注册失败（可能被占用）：\n{string.Join("\n", failed)}",
                        "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // 功能开关解耦：截图重注册只看截图开关
            if (_isPinActive
                && !_hotkeyService.RegisterPinHotkeys(
                    _settings.Pin.SnapshotHotkey,
                    _settings.Pin.ClipboardHotkey))
                OnPinNoticeRequested("系统恢复后贴图快捷键重新注册失败");

            // 恢复后立即强制刷新并与当前前台进程重新评估避让（即时检测，无需等待300ms轮询）
            _foregroundMonitor?.CheckForeground();
        }
        catch (Exception ex)
        {
            _ = _settingsService.LogAsync($"恢复后重注册热键失败: {ex.Message}");
        }
    }
    private void OnToggleListening()
    {
        // 全局快捷键只暂停/恢复界面手动启用的功能，不修改持久化开关。
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        bool anyActive = (_settings.HotkeysEnabled && !_hotkeysPausedByGlobal)
            || (_settings.Pin.Enabled && !_pinPausedByGlobal);
        _hotkeysPausedByGlobal = anyActive && _settings.HotkeysEnabled;
        _pinPausedByGlobal = anyActive && _settings.Pin.Enabled;
        ApplyEffectiveFeatureStates();
    }

    private void ApplyEffectiveFeatureStates(bool announceFailures = true)
    {
        _isListening = _settings.HotkeysEnabled && !_hotkeysPausedByGlobal;
        _isPinActive = _settings.Pin.Enabled && !_pinPausedByGlobal;
        ApplyHotkeyState();
        ApplyPinState(announceFailures);
    }

    /// <summary>切换快捷键功能后统一应用：注册或注销本地热键并刷新状态。</summary>
    private void ApplyHotkeyState()
    {
        if (_isListening)
        {
            var failedKeys = _hotkeyService.RegisterAll(_settings.LocalShortcuts);
            if (failedKeys.Count > 0)
            {
                System.Windows.MessageBox.Show(
                    $"以下快捷键注册失败（可能被占用）：\n{string.Join("\n", failedKeys)}",
                    "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            _hotkeyService.UnregisterAll();
        }
        UpdateStatusUi();
    }

    /// <summary>切换截图功能后统一应用：注册或注销贴图热键并刷新状态。</summary>
    private void ApplyPinState(bool announceFailures = true)
    {
        if (_isPinActive)
        {
            _pinFeatureService.SetEnabled(true);
            if (!_hotkeyService.RegisterPinHotkeys(
                    _settings.Pin.SnapshotHotkey,
                    _settings.Pin.ClipboardHotkey)
                && announceFailures)
            {
                OnPinNoticeRequested("贴图快捷键注册失败，请在贴图设置中更换组合键");
            }
        }
        else
        {
            _hotkeyService.UnregisterPinHotkeys();
            _pinFeatureService.SetEnabled(false);
        }
        UpdateStatusUi();
    }

    /// <summary>两个功能开关组合出的状态文案。</summary>
    private string CurrentStatusText()
    {
        bool hk = _isListening, pin = _isPinActive;
        bool manualHk = _settings.HotkeysEnabled, manualPin = _settings.Pin.Enabled;
        if (hk && pin) return "监听中";
        if (!hk && !pin)
        {
            if (!manualHk && !manualPin) return "全部功能已手动关闭";
            if (manualHk && manualPin) return "已全部暂停";
            return manualHk ? "快捷键已暂停" : "截图已暂停";
        }
        return hk
            ? (manualPin ? "截图已暂停" : "截图已手动关闭")
            : (manualHk ? "快捷键已暂停" : "快捷键已手动关闭");
    }

    /// <summary>刷新状态行、托盘图标与托盘菜单文本（功能开关的统一出口）。</summary>
    private void UpdateStatusUi()
    {
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        bool hk = _isListening, pin = _isPinActive;
        string text = CurrentStatusText();
        var brush = (hk && pin)
            ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
        LblStatus.Text = text;
        LblStatus.Foreground = brush;
        StatusDot.Fill = brush;
        if (IsTrayIconValid)
        {
            _trayIcon!.Text = $"Vector - {text}";
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = CreateAppIcon(paused: !hk && !pin);
            oldIcon?.Dispose();
        }
        if (_toggleListenItem != null)
        {
            bool anyManuallyEnabled = _settings.HotkeysEnabled || _settings.Pin.Enabled;
            _toggleListenItem.Enabled = anyManuallyEnabled;
            _toggleListenItem.Text = (hk || pin)
                ? "暂停当前启用功能"
                : anyManuallyEnabled ? "恢复当前启用功能" : "所有功能已手动关闭";
        }
        if (_togglePinItem != null)
            _togglePinItem.Text = _settings.Pin.Enabled ? "手动关闭截图" : "手动启用截图";
    }

    private void TglHotkeys_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync) return;
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        bool enable = TglHotkeys.IsChecked == true;
        if (enable == _settings.HotkeysEnabled) return;
        _settings.HotkeysEnabled = enable;
        _hotkeysPausedByGlobal = false;
        _ = _settingsService.SaveAsync(_settings);
        ApplyEffectiveFeatureStates();
    }

    private void TglPin_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync) return;
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        bool enable = sender == _togglePinItem
            ? !_settings.Pin.Enabled          // 托盘菜单点击：翻转当前状态
            : TglPin.IsChecked == true;       // 主界面开关点击
        _suppressToggleSync = true;
        TglPin.IsChecked = enable;
        _suppressToggleSync = false;
        if (enable == _settings.Pin.Enabled) return;
        _settings.Pin.Enabled = enable;
        _pinPausedByGlobal = false;
        _ = _settingsService.SaveAsync(_settings);
        ApplyEffectiveFeatureStates();
    }

    private void ChkHotkeyDiagnostics_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync || Dispatcher == null || Dispatcher.HasShutdownStarted)
            return;
        bool enabled = ChkHotkeyDiagnostics.IsChecked == true;
        if (enabled == _settings.HotkeyDiagnosticsEnabled)
            return;

        _settings.HotkeyDiagnosticsEnabled = enabled;
        _hotkeyService.SetDiagnosticLogging(enabled);
        _executorService.SetDiagnosticLogging(enabled);
        _ = _settingsService.LogAsync($"Hotkey diagnostic logging {(enabled ? "enabled" : "disabled")}");
        _ = _settingsService.SaveAsync(_settings);
    }

    private void ChkHotkeyCoexistence_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync || Dispatcher == null || Dispatcher.HasShutdownStarted)
            return;
        bool enabled = ChkHotkeyCoexistence.IsChecked == true;
        _settings.Coexistence ??= new HotkeyCoexistenceSettings();
        if (enabled == _settings.Coexistence.Enabled)
            return;

        _settings.Coexistence.Enabled = enabled;
        _ = _settingsService.LogAsync($"Hotkey coexistence {(enabled ? "enabled" : "disabled")}");
        _ = _settingsService.SaveAsync(_settings);
        _foregroundMonitor?.TriggerCurrent();
    }

    private void OnForegroundProcessChanged(string processName, bool isFullscreen)
    {
        if (_settings?.Coexistence == null)
            return;

        var activeChords = new List<string>();
        if (_settings.LocalShortcuts != null)
        {
            foreach (var sc in _settings.LocalShortcuts)
            {
                if (!string.IsNullOrWhiteSpace(sc?.Key))
                    activeChords.Add(sc.Key);
            }
        }
        if (_settings.Pin != null)
        {
            if (!string.IsNullOrWhiteSpace(_settings.Pin.SnapshotHotkey))
                activeChords.Add(_settings.Pin.SnapshotHotkey);
            if (!string.IsNullOrWhiteSpace(_settings.Pin.ClipboardHotkey))
                activeChords.Add(_settings.Pin.ClipboardHotkey);
        }

        var (passthrough, reason) = HotkeyPassthroughPolicy.Evaluate(
            processName,
            isFullscreen,
            _settings.Coexistence,
            activeChords,
            _settings.GlobalHotKey);

        if (_settings.HotkeyDiagnosticsEnabled)
        {
            if (!string.IsNullOrEmpty(reason))
                _ = _settingsService.LogAsync($"[Coexistence] {reason} (active foreground: '{processName}')");
            else
                _ = _settingsService.LogAsync($"[Foreground] Active: '{processName}' (isFullscreen={isFullscreen})");
        }

        _hotkeyService.SetPassthroughSignatures(passthrough);
    }

    private string? FindHotkeyConflict(
        string key,
        int? ignoredLocalIndex = null,
        bool ignoreGlobal = false,
        PinSettings? pinSettings = null,
        string? ignoredPinKey = null)
    {
        if (!HotkeyService.TryGetHotkeySignature(key, out string signature))
            return "快捷键格式无效";

        for (int i = 0; i < _settings.LocalShortcuts.Count; i++)
        {
            if (i == ignoredLocalIndex)
                continue;
            if (HotkeyService.TryGetHotkeySignature(_settings.LocalShortcuts[i].Key, out string other)
                && other == signature)
                return $"该组合键已被本地快捷键「{_settings.LocalShortcuts[i].Key}」使用";
        }

        if (!ignoreGlobal
            && !string.IsNullOrWhiteSpace(_settings.GlobalHotKey)
            && !string.Equals(_settings.GlobalHotKey, "None", StringComparison.OrdinalIgnoreCase)
            && HotkeyService.TryGetHotkeySignature(_settings.GlobalHotKey, out string global)
            && global == signature)
            return "该组合键已被全局开关热键使用";

        PinSettings pins = pinSettings ?? _settings.Pin;
        if (ignoredPinKey != "snapshot"
            && HotkeyService.TryGetHotkeySignature(pins.SnapshotHotkey, out string snapshot)
            && snapshot == signature)
            return "该组合键已被贴图框选热键使用";
        if (ignoredPinKey != "clipboard"
            && HotkeyService.TryGetHotkeySignature(pins.ClipboardHotkey, out string clipboard)
            && clipboard == signature)
            return "该组合键已被剪贴板贴图热键使用";

        return null;
    }

    private string? ValidateImportedSettings(AppSettings imported)
    {
        var entries = new List<(string Label, string Key)>();

        if (imported.LocalShortcuts != null)
        {
            foreach (var shortcut in imported.LocalShortcuts)
                entries.Add(("本地快捷键", shortcut?.Key ?? string.Empty));
        }

        if (!string.IsNullOrWhiteSpace(imported.GlobalHotKey)
            && !string.Equals(imported.GlobalHotKey, "None", StringComparison.OrdinalIgnoreCase))
        {
            entries.Add(("全局开关热键", imported.GlobalHotKey));
        }

        PinSettings pin = imported.Pin ?? new PinSettings();
        bool hasSnapshot = !string.IsNullOrWhiteSpace(pin.SnapshotHotkey);
        bool hasClipboard = !string.IsNullOrWhiteSpace(pin.ClipboardHotkey);
        if (hasSnapshot != hasClipboard)
        {
            string missingLabel = hasSnapshot ? "剪贴板贴图热键" : "贴图框选热键";
            return $"贴图热键配置不完整：{missingLabel}为空";
        }
        if (hasSnapshot && hasClipboard)
        {
            entries.Add(("贴图框选热键", pin.SnapshotHotkey!));
            entries.Add(("剪贴板贴图热键", pin.ClipboardHotkey!));
        }

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, key) in entries)
        {
            if (!HotkeyService.TryGetHotkeySignature(key, out string signature))
                return $"{label}「{key}」无效：快捷键格式无效";

            string? rejectReason = _hotkeyService.GetHotkeyRejectReason(key);
            if (rejectReason != null)
                return $"{label}「{key}」无效：{rejectReason}";

            if (seen.TryGetValue(signature, out string? existing))
                return $"{label}「{key}」与{existing}冲突（组合键重复）";

            seen[signature] = $"{label}「{key}」";
        }

        return null;
    }
    private void InitTray()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateAppIcon(),
            Visible = true,
            Text = "Vector - \u76D1\u542C\u4E2D",
        };
        _menuTray = new Forms.ContextMenuStrip();
        var menu = _menuTray;
        menu.Items.Add("\u663E\u793A", null, (s, ev) => { Show(); WindowState = WindowState.Normal; Activate(); });
        menu.Items.Add("框选贴图", null, (s, ev) =>
            Dispatcher.BeginInvoke(new Action(_pinFeatureService.StartRegionCapture)));
        menu.Items.Add("剪贴板贴图", null, (s, ev) =>
            Dispatcher.BeginInvoke(new Action(_pinFeatureService.PinClipboard)));
        menu.Items.Add("贴图设置", null, (s, ev) =>
            Dispatcher.BeginInvoke(new Action(OpenPinSettings)));
        menu.Items.Add(new Forms.ToolStripSeparator());

        // 暂停/恢复快捷键菜单项
        _toggleListenItem = new Forms.ToolStripMenuItem(
            (_isListening || _isPinActive) ? "暂停当前启用功能" : "恢复当前启用功能");
        _toggleListenItem.Click += (s, ev) => OnToggleListening();
        menu.Items.Add(_toggleListenItem);

        // 暂停/恢复截图菜单项（与主界面截图开关同步）
        _togglePinItem = new Forms.ToolStripMenuItem(
            _settings.Pin.Enabled ? "手动关闭截图" : "手动启用截图");
        _togglePinItem.Click += (s, ev) =>
            Dispatcher.BeginInvoke(new Action(() => TglPin_Changed(_togglePinItem!, new RoutedEventArgs())));
        menu.Items.Add(_togglePinItem);

        var reregisterItem = new Forms.ToolStripMenuItem("立即重新注册热键");
        reregisterItem.Click += (s, ev) => ReregisterHotkeysAfterResume();
        menu.Items.Add(reregisterItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var themeItem = new Forms.ToolStripMenuItem("\u5207\u6362\u6DF1/\u6D45\u8272\u6A21\u5F0F");
        themeItem.Click += async (s, ev) =>
        {
            _themeService.Toggle();
            _settings.Theme = _themeService.IsDark ? "Dark" : "Light";
            await _settingsService.SaveAsync(_settings);
        };
        menu.Items.Add(themeItem);
        // ✅ 开机自启
        var autoStartItem = new Forms.ToolStripMenuItem("开机启动")
        {
            Checked = AutoStartService.IsEnabled(),
            CheckOnClick = true
        };
        autoStartItem.CheckedChanged += (s, ev) =>
        {
            if (_trayCheckSuppress) return;
            if (!AutoStartService.SetEnabled(autoStartItem.Checked))
            {
                _trayCheckSuppress = true;
                autoStartItem.Checked = AutoStartService.IsEnabled();
                _trayCheckSuppress = false;
                System.Windows.MessageBox.Show("无法修改开机启动项（可能权限不足）。", "开机启动",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        menu.Items.Add(autoStartItem);

        // ✅ 便携换机：配置写在程序目录
        var portableItem = new Forms.ToolStripMenuItem("配置保存在程序目录（便携换机）")
        {
            Checked = AppPaths.IsPortableMode(),
            CheckOnClick = true
        };
        portableItem.CheckedChanged += async (s, ev) =>
        {
            if (_trayCheckSuppress) return;
            // 先落盘现行目录，再拷到目标，避免启用便携后重启丢最近改动
            await _settingsService.SaveAsync(_settings);
            var msg = SettingsService.PreparePortableModeSwitch(portableItem.Checked);
            var actual = AppPaths.IsPortableMode();
            if (portableItem.Checked != actual)
            {
                _trayCheckSuppress = true;
                portableItem.Checked = actual;
                _trayCheckSuppress = false;
            }
            System.Windows.MessageBox.Show(msg, "便携配置", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        menu.Items.Add(portableItem);

        var openCfgItem = new Forms.ToolStripMenuItem("打开配置文件夹");
        openCfgItem.Click += (s, ev) =>
        {
            try
            {
                var dir = _settingsService.DataFolder;
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"无法打开：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        menu.Items.Add(openCfgItem);

        // ✅ 1.0.1: UIPI 缓解入口 — 托盘菜单提供"以管理员模式启动"开关
        var adminItem = new Forms.ToolStripMenuItem("\u4EE5\u7BA1\u7406\u5458\u6A21\u5F0F\u542F\u52A8")
        {
            Checked = _settings.RunAsAdmin,
            CheckOnClick = true
        };
        adminItem.CheckedChanged += async (s, ev) =>
        {
            _settings.RunAsAdmin = adminItem.Checked;
            await _settingsService.SaveAsync(_settings);
            if (adminItem.Checked && !IsUserAnAdmin())
            {
                var result = System.Windows.MessageBox.Show(
                    "需要以管理员身份重启 Vector 才能让全局热键穿透到高权限程序（如全屏游戏）。\n\n是否立即重启？",
                    "UIPI 提权", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    RestartAsAdmin();
                }
            }
        };
        menu.Items.Add(adminItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("\u9000\u51FA", null, (s, ev) =>
        {
            _reallyExit = true;   // Bug 11: 标记真正退出
            _trayIcon!.Visible = false;
            System.Windows.Application.Current.Shutdown();
        });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (s, ev) => { Show(); WindowState = WindowState.Normal; Activate(); };
    }
    // ✅ 1.0.1: 以管理员身份重启 Vector
    public void RestartAsAdmin()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                System.Windows.MessageBox.Show("无法获取程序路径，请手动重启。", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = exePath,
                Verb = "runas",  // 触发 UAC 弹窗
                WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? ""
            };
            // ✅ 修复: UseShellExecute=true 时不能用 ArgumentList，必须用 Arguments 字符串
            psi.Arguments = "--elevated";

            // ✅ 1.0.5: ShellExecute=true + runas 时 Process.Start 可能返回 null
            // （用户取消 UAC / 系统繁忙）。失败时不退出当前进程，避免软件"凭空消失"。
            var newProc = System.Diagnostics.Process.Start(psi);
            if (newProc == null)
            {
                System.Windows.MessageBox.Show(
                    "启动提权进程失败（可能被系统拒绝或用户取消 UAC）。当前进程继续运行，请稍后重试。",
                    "提权失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 关闭当前进程（普通权限），让提权进程接管
            _reallyExit = true;
            if (_trayIcon != null) _trayIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户取消了 UAC 弹窗
            System.Windows.MessageBox.Show("已取消管理员重启。当前进程仍以普通权限运行，全局热键可能无法在管理员程序中触发。",
                "已取消", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"重启失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private System.Drawing.Icon CreateAppIcon(bool paused = false)
    {
        // 优先从嵌入资源加载（紫色 = 监听中，红色 = 已暂停）
        try
        {
            var resName = paused ? "Resources/app_paused.ico" : "Resources/app.ico";
            var stream = System.Windows.Application.GetResourceStream(
                new Uri($"pack://application:,,,/{resName}"))?.Stream;
            if (stream != null)
                return new System.Drawing.Icon(stream);
        }
        catch { }

        // 回退：GDI+ 绘制三角形
        const int sz = 64;
        using var bmp = new System.Drawing.Bitmap(sz, sz);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.Clear(System.Drawing.Color.Transparent);
            int pad = 8;
            int left = pad, top = pad, right = sz - pad, midX = sz / 2, bottom = sz - pad;
            var pts = new[]
            {
                new System.Drawing.Point(left, top),
                new System.Drawing.Point(right, top),
                new System.Drawing.Point(midX, bottom),
            };
            var colorTop = paused
                ? System.Drawing.Color.FromArgb(239, 68, 68)
                : System.Drawing.Color.FromArgb(99, 102, 241);
            var colorBot = paused
                ? System.Drawing.Color.FromArgb(248, 113, 113)
                : System.Drawing.Color.FromArgb(129, 140, 248);
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, sz, sz), colorTop, colorBot, 45f);
            g.FillPolygon(brush, pts);
        }
        // Bug 10: 克隆后释放原始句柄，防止 GDI 泄漏
        IntPtr hicon = bmp.GetHicon();
        var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hicon).Clone();
        DestroyIcon(hicon);
        return icon;
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312)
        {
            _hotkeyService.ProcessMessage(wParam);
            handled = true;
        }
        return IntPtr.Zero;
    }
    private bool IsTrayIconValid => _trayIcon != null;
    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            // 与关闭进托盘一致：气泡只提示一次
            if (IsTrayIconValid && !_hasShownMinimizeTip)
            {
                _trayIcon!.ShowBalloonTip(1000, "Vector", "\u5DF2\u6700\u5C0F\u5316\u5230\u6258\u76D8", Forms.ToolTipIcon.Info);
                _hasShownMinimizeTip = true;
            }
        }
    }
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Bug 11: _reallyExit=true 时才真正退出，否则最小化到托盘
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            // ✅ Bug N4: 气泡只弹第一次，之后不再骚扰
            if (IsTrayIconValid && !_hasShownMinimizeTip)
            {
                _trayIcon!.ShowBalloonTip(1000, "Vector", "\u5DF2\u6700\u5C0F\u5316\u5230\u6258\u76D8", Forms.ToolTipIcon.Info);
                _hasShownMinimizeTip = true;
            }
        }
    }
    private async void BtnSetGlobal_Click(object sender, RoutedEventArgs e)
    {
        string oldKey = _settings.GlobalHotKey;   // ✅ 1.0.8: 保存旧热键，失败时恢复
        // 留空 = 关闭全局开关热键
        string newKey = string.IsNullOrWhiteSpace(TxtGlobalKey.Text)
            ? "None"
            : TxtGlobalKey.Text.Trim();
        if (newKey != "None")
        {
            string? rejectReason = _hotkeyService.GetHotkeyRejectReason(newKey);
            string? conflict = FindHotkeyConflict(newKey, ignoreGlobal: true);
            if (rejectReason != null || conflict != null)
            {
                System.Windows.MessageBox.Show(rejectReason ?? conflict!, "快捷键冲突",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtGlobalKey.Text = oldKey == "None" ? "" : oldKey;
                return;
            }
        }
        if (_hotkeyService.RegisterGlobal(newKey))
        {
            _settings.GlobalHotKey = newKey;
            await _settingsService.SaveAsync(_settings);
            TxtGlobalKey.Text = newKey == "None" ? "" : newKey;
            System.Windows.MessageBox.Show(
                newKey == "None" ? "已关闭全局开关热键" : "全局热键已保存",
                "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show("热键注册失败，可能被其他程序占用。\n请尝试其他组合。",
                "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtGlobalKey.Text = oldKey == "None" ? "" : oldKey;
            TxtGlobalKey.Focus();
        }
    }

    private void BtnPinSettings_Click(object sender, RoutedEventArgs e) => OpenPinSettings();

    private async void OpenPinSettings()
    {
        var dialog = new PinSettingsWindow(
            _settings.Pin,
            _pinFeatureService.StartRegionCapture,
            _pinFeatureService.PinClipboard)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        PinSettings oldSettings = _settings.Pin;
        PinSettings newSettings = dialog.ResultSettings;
        string? snapshotReject = _hotkeyService.GetHotkeyRejectReason(newSettings.SnapshotHotkey);
        string? clipboardReject = _hotkeyService.GetHotkeyRejectReason(newSettings.ClipboardHotkey);
        string? snapshotConflict = FindHotkeyConflict(
            newSettings.SnapshotHotkey,
            pinSettings: newSettings,
            ignoredPinKey: "snapshot");
        string? clipboardConflict = FindHotkeyConflict(
            newSettings.ClipboardHotkey,
            pinSettings: newSettings,
            ignoredPinKey: "clipboard");
        string? configurationConflict = snapshotReject ?? clipboardReject ?? snapshotConflict ?? clipboardConflict;
        if (configurationConflict != null)
        {
            System.Windows.MessageBox.Show(configurationConflict, "快捷键冲突",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool manualPinStateChanged = newSettings.Enabled != oldSettings.Enabled;
        bool newPinWillBeActive = newSettings.Enabled
            && (manualPinStateChanged || !_pinPausedByGlobal);
        // 功能开关解耦：贴图热键注册只看截图开关，不再受快捷键监听状态影响
        if (newPinWillBeActive)
        {
            if (!_hotkeyService.RegisterPinHotkeys(
                newSettings.SnapshotHotkey,
                newSettings.ClipboardHotkey))
            {
                System.Windows.MessageBox.Show(
                    "贴图快捷键注册失败，可能与 Vector 快捷键或其他程序冲突。旧快捷键仍保持可用。",
                    "快捷键冲突",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }
        else
            _hotkeyService.UnregisterPinHotkeys();

        _settings.Pin = newSettings;
        if (manualPinStateChanged)
            _pinPausedByGlobal = false;
        _isPinActive = _settings.Pin.Enabled && !_pinPausedByGlobal;
        _pinFeatureService.UpdateSettings(newSettings);
        _pinFeatureService.SetEnabled(_isPinActive);
        _suppressToggleSync = true;
        TglPin.IsChecked = newSettings.Enabled;
        _suppressToggleSync = false;
        UpdateStatusUi();
        await _settingsService.SaveAsync(_settings);
        OnPinNoticeRequested("贴图设置已保存并立即生效");
    }

    private async void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        // Bug 15: 删除无用的占位符判断
        if (string.IsNullOrWhiteSpace(TxtLocalKey.Text)) return;
        if (string.IsNullOrWhiteSpace(TxtTarget.Text)) return;
        // ✅ 1.0.2: 提交前清理目标路径（脏数据防御的最后一道闸）
        TxtTarget.Text = CleanTarget(TxtTarget.Text);
        if (string.IsNullOrWhiteSpace(TxtTarget.Text)) return;
        TxtWorkingDir.Text = CleanTarget(TxtWorkingDir.Text);
        // ✅ 快捷键黑名单检查（编辑模式如果未改键则跳过）
        var enteringKey = TxtLocalKey.Text.Trim();
        var isEditingSameKey = _editingIndex >= 0
            && _settings.LocalShortcuts[_editingIndex].Key == enteringKey;
        if (!isEditingSameKey)
        {
            var rejectReason = _hotkeyService.GetHotkeyRejectReason(enteringKey);
            if (rejectReason != null)
            {
                System.Windows.MessageBox.Show(rejectReason, "快捷键不可用",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        string? internalConflict = FindHotkeyConflict(
            enteringKey,
            ignoredLocalIndex: _editingIndex >= 0 ? _editingIndex : null);
        if (internalConflict != null)
        {
            System.Windows.MessageBox.Show(internalConflict, "快捷键冲突",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var cfg = new ShortcutConfig
        {
            Key = enteringKey,
            Type = (CboType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "\u7A0B\u5E8F",
            Target = TxtTarget.Text,
            Arguments = TxtArguments.Text?.Trim() ?? "",
            WorkingDirectory = TxtWorkingDir.Text?.Trim() ?? "",
            RunStrategy = GetSelectedRunStrategy()
        };
        if (_editingIndex >= 0)
        {
            // 编辑模式：检查与其他项重复
            for (int i = 0; i < _settings.LocalShortcuts.Count; i++)
            {
                // ✅ 1.0.8: 用 enteringKey (trimmed) 检查，避免粘贴带空格文本时漏检
                if (i != _editingIndex && _settings.LocalShortcuts[i].Key == enteringKey)
                {
                    System.Windows.MessageBox.Show("\u8BE5\u5FEB\u6377\u952E\u5DF2\u88AB\u5176\u4ED6\u9879\u4F7F\u7528\uFF01");
                    return;
                }
            }
            // 取旧值（用于失败回滚 + Undo 描述）
            var oldCfg = _settings.LocalShortcuts[_editingIndex];
            // ✅ Undo: 编辑前记录快照（此时列表仍是旧状态）
            _undoService.Record(_settings.LocalShortcuts,
                UndoOperationType.Update, $"编辑 {oldCfg.Key}");
            _settings.LocalShortcuts[_editingIndex] = cfg;
            if (_originalOrder != null)
            {
                int origIdx = _originalOrder.IndexOf(oldCfg);
                if (origIdx >= 0)
                    _originalOrder[origIdx] = cfg;
                else
                    _originalOrder = null;
            }
            if (_isListening && !_hotkeyService.RegisterSingle(cfg, _editingIndex + 1))
            {
                // RegisterSingle 会先注册新快捷键，再撤销旧映射；失败时旧快捷键仍保持可用。
                _settings.LocalShortcuts[_editingIndex] = oldCfg;
                if (_originalOrder != null)
                {
                    int origIdx = _originalOrder.IndexOf(cfg);
                    if (origIdx >= 0)
                        _originalOrder[origIdx] = oldCfg;
                    else
                        _originalOrder = null;
                }
                System.Windows.MessageBox.Show($"\u5FEB\u6377\u952E {cfg.Key} \u5DF2\u88AB\u7CFB\u7EDF\u6216\u5176\u4ED6\u7A0B\u5E8F\u5360\u7528\uFF0C\u8BF7\u66F4\u6362\uFF01", "\u6CE8\u518C\u5931\u8D25",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // ✅ 1.0.8: 删除死代码 —— cfg 是新实例，IndexOf 用引用比较永远返回 -1
            // UI 同步由末尾的 RefreshListView() 统一处理
            _editingIndex = -1;
            BtnAdd.Content = "+ \u6DFB\u52A0";
            BtnCancelEdit.Visibility = Visibility.Collapsed;
        }
        else
        {
            // ✅ 1.0.8: 用 enteringKey (trimmed) 检查，避免粘贴带空格文本时漏检
            if (_settings.LocalShortcuts.Any(s => s.Key == enteringKey))
            {
                System.Windows.MessageBox.Show("\u8BE5\u5FEB\u6377\u952E\u5DF2\u88AB\u5176\u4ED6\u9879\u4F7F\u7528\uFF01");
                return;
            }
// ✅ Undo: 添加前记录快照（此时列表还未包含新项）
            _undoService.Record(_settings.LocalShortcuts,
                UndoOperationType.Add, $"添加 {cfg.Key}");
            _settings.LocalShortcuts.Add(cfg);
            _originalOrder?.Add(cfg);
            // ✅ 修复：不再手动同步 LocalShortcuts，由末尾 RefreshListView() 统一刷新
            int newId = _settings.LocalShortcuts.Count;
            if (_isListening && !_hotkeyService.RegisterSingle(cfg, newId))
            {
                _settings.LocalShortcuts.RemoveAt(_settings.LocalShortcuts.Count - 1);
                _originalOrder?.Remove(cfg);
                System.Windows.MessageBox.Show($"\u5FEB\u6377\u952E {cfg.Key} \u5DF2\u88AB\u7CFB\u7EDF\u6216\u5176\u4ED6\u7A0B\u5E8F\u5360\u7528\uFF0C\u8BF7\u66F4\u6362\uFF01", "\u6CE8\u518C\u5931\u8D25",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        // Bug 6: 删除冗余的 RegisterAll，RegisterSingle 已经完成了注册
        await _settingsService.SaveAsync(_settings);
        RefreshListView();
        ClearAddForm();
        LblEditTip.Visibility = Visibility.Collapsed;
    }
    private void BtnCancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _editingIndex = -1;
        BtnAdd.Content = "+ \u6DFB\u52A0";
        BtnCancelEdit.Visibility = Visibility.Collapsed;
        ClearAddForm();
        LblEditTip.Visibility = Visibility.Collapsed;
        LblCount.Text = $"{_settings.LocalShortcuts.Count} 项";
    }

    private void ClearAddForm()
    {
        TxtLocalKey.Text = "";
        TxtTarget.Text = "";
        TxtArguments.Text = "";
        TxtWorkingDir.Text = "";
        CboType.SelectedIndex = 0;
        SetRunStrategySelection(RunStrategies.Activate);
        UpdateExtraFieldsEnabled();
        SetAdvancedExpanded(false);
    }

    private void BtnToggleAdvanced_Click(object sender, RoutedEventArgs e)
    {
        SetAdvancedExpanded(!_advancedExpanded);
    }

    private void SetAdvancedExpanded(bool expanded)
    {
        _advancedExpanded = expanded;
        if (ExtraFieldsGrid != null)
            ExtraFieldsGrid.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (LblAdvancedArrow != null)
            LblAdvancedArrow.Text = expanded ? "▾" : "▸";
        if (LblAdvancedTitle != null)
            LblAdvancedTitle.Text = expanded ? "高级选项（参数 / 工作目录 / 运行策略）" : "高级选项";
    }

    private string GetSelectedRunStrategy()
    {
        if (CboRunStrategy.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return RunStrategies.Normalize(tag);
        return RunStrategies.Activate;
    }

    private void SetRunStrategySelection(string strategy)
    {
        var want = RunStrategies.Normalize(strategy);
        for (int i = 0; i < CboRunStrategy.Items.Count; i++)
        {
            if (CboRunStrategy.Items[i] is ComboBoxItem item && item.Tag as string == want)
            {
                CboRunStrategy.SelectedIndex = i;
                return;
            }
        }
        CboRunStrategy.SelectedIndex = 1; // Activate
    }

    private void CboType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateExtraFieldsEnabled();
    }

    private void UpdateExtraFieldsEnabled()
    {
        if (CboRunStrategy == null || TxtArguments == null) return;
        var isProg = (CboType.SelectedItem as ComboBoxItem)?.Content?.ToString() == "程序";
        CboRunStrategy.IsEnabled = isProg;
        // 参数/工作目录对程序最有用，文件/脚本也可填参数
        TxtArguments.IsEnabled = true;
        TxtWorkingDir.IsEnabled = true;
    }

    private async void TxtStartupDelay_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtStartupDelay.Text.Trim(), out int sec))
            sec = 0;
        sec = Math.Clamp(sec, 0, 120);
        TxtStartupDelay.Text = sec.ToString();
        if (_settings.StartupDelaySeconds == sec) return;
        _settings.StartupDelaySeconds = sec;
        await _settingsService.SaveAsync(_settings);
    }


    /// <summary>
    /// ✅ 优化方案：点击空白区域时自动退出编辑模式，恢复"添加"按钮状态
    /// </summary>
    private void RootGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 如果当前不处于编辑模式，不处理
        if (_editingIndex < 0) return;

        var source = e.OriginalSource as System.Windows.DependencyObject;

        // 1. 如果点击发生在"添加快捷键"卡片内部（包含输入框、按钮、下拉框等所有子元素），不取消
        if (IsDescendantOf(source, AddCardBorder)) return;

        // 2. 如果点击发生在 ComboBox 的下拉项（位于独立的 Popup 可视树中），不取消
        if (source is System.Windows.Controls.ComboBoxItem) return;

        // 3. 如果点击发生在右键菜单相关元素上，不取消
        if (source is System.Windows.Controls.ContextMenu ||
            source is System.Windows.Controls.MenuItem) return;

        // 其余情况（点击列表空白、卡片背景、侧边栏等）触发取消编辑逻辑
        BtnCancelEdit_Click(sender, e);
    }

    /// <summary>
    /// ✅ 辅助方法：向上遍历可视树，判断元素是否是某个父级的后代
    /// </summary>
    private static bool IsDescendantOf(System.Windows.DependencyObject? element, System.Windows.DependencyObject? parent)
    {
        if (element == null || parent == null) return false;
        while (element != null)
        {
            if (element == parent) return true;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }
    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var typeItem = CboType.SelectedItem as ComboBoxItem;
        var type = typeItem?.Content?.ToString();
        if (type == "\u6587\u4EF6\u5939")
        {
            var dialog = new Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
                TxtTarget.Text = dialog.SelectedPath;
        }
        else if (type == "\u7F51\u9875")  // Bug 9: 网页类型不弹文件选择器
        {
            TxtTarget.Focus();
            TxtTarget.SelectAll();
            System.Windows.MessageBox.Show("\u8BF7\u76F4\u63A5\u8F93\u5165\u7F51\u5740\uFF0C\u4F8B\u5982 https://www.google.com",
                "\u63D0\u793A", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (type == "\u6587\u4EF6")  // 文件类型：选择文档
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "\u5E38\u7528\u6587\u6863 (*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt)|*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt|\u6240\u6709\u6587\u4EF6 (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
                TxtTarget.Text = dialog.FileName;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "\u7A0B\u5E8F (*.exe)|*.exe|\u6240\u6709\u6587\u4EF6 (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
                TxtTarget.Text = dialog.FileName;
        }
    }
    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog() == true)
        {
            var count = _settings.LocalShortcuts?.Count ?? 0;
            var confirm = System.Windows.MessageBox.Show(
                $"导入将替换当前全部配置（现有 {count} 项快捷键）。\n是否继续？",
                "确认导入", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                var json = await System.IO.File.ReadAllTextAsync(dialog.FileName);
                var imported = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                if (imported != null)
                {
                    string? validationError = ValidateImportedSettings(imported);
                    if (validationError != null)
                    {
                        System.Windows.MessageBox.Show(
                            $"配置导入已拒绝：{validationError}",
                            "导入失败",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    _hotkeyService.UnregisterAll();
                    _hotkeyService.UnregisterPinHotkeys();
                    // Bug 18: 先清除选中状态，避免引用断裂
                    LvShortcuts.SelectedIndex = -1;
                    _settings = imported;
                    _settings.LocalShortcuts ??= new System.Collections.ObjectModel.ObservableCollection<ShortcutConfig>();
                    _settings.Pin ??= new PinSettings();
                    _hotkeysPausedByGlobal = false;
                    _pinPausedByGlobal = false;
                    _hotkeyService.SetDiagnosticLogging(_settings.HotkeyDiagnosticsEnabled);
                    _executorService.SetDiagnosticLogging(_settings.HotkeyDiagnosticsEnabled);
                    // ✅ 1.0.7 修复: 检查全局热键注册结果。失败时**保留**配置中的 GlobalHotKey 字符串
                    // （用户的偏好），但 TxtGlobalKey 显示会变成空（运行时未生效）。
                    string? globalHotkeyFail = null;
                    // 无论新配置有无全局键，都先走 RegisterGlobal（空/None 会卸载旧键）
                    if (_settings.GlobalHotKey != "None"
                        && !string.IsNullOrWhiteSpace(_settings.GlobalHotKey))
                    {
                        if (!_hotkeyService.RegisterGlobal(_settings.GlobalHotKey))
                            globalHotkeyFail = _settings.GlobalHotKey;
                    }
                    else
                    {
                        _hotkeyService.RegisterGlobal("None");
                        _settings.GlobalHotKey = "None";
                    }
                    // 功能开关解耦：导入后按各自开关恢复
                    _isListening = _settings.HotkeysEnabled;
                    _isPinActive = _settings.Pin.Enabled;
                    var failedKeys = _isListening
                        ? _hotkeyService.RegisterAll(_settings.LocalShortcuts)
                        : new List<string>();
                    bool pinHotkeyFail = _isPinActive
                        && !_hotkeyService.RegisterPinHotkeys(
                            _settings.Pin.SnapshotHotkey,
                            _settings.Pin.ClipboardHotkey);
                    _pinFeatureService.UpdateSettings(_settings.Pin);
                    _pinFeatureService.SetEnabled(_isPinActive);
                    _settings.Coexistence ??= new HotkeyCoexistenceSettings();
                    _suppressToggleSync = true;
                    TglHotkeys.IsChecked = _isListening;
                    TglPin.IsChecked = _settings.Pin.Enabled;
                    ChkHotkeyDiagnostics.IsChecked = _settings.HotkeyDiagnosticsEnabled;
                    ChkHotkeyCoexistence.IsChecked = _settings.Coexistence.Enabled;
                    _suppressToggleSync = false;
                    UpdateStatusUi();
                    _foregroundMonitor?.TriggerCurrent();
                    // Bug 18: 更新全局热键输入框（None → 空，勿假显示默认键）
                    TxtGlobalKey.Text = _settings.GlobalHotKey == "None" ? "" : _settings.GlobalHotKey;
                    TxtStartupDelay.Text = Math.Clamp(_settings.StartupDelaySeconds, 0, 120).ToString();
                    // ✅ 1.0.8: 合并为单一消息框，避免双重弹窗
                    if (failedKeys.Count > 0 || globalHotkeyFail != null || pinHotkeyFail)
                    {
                        var msg = "配置导入成功";
                        if (failedKeys.Count > 0)
                            msg += $"，但以下快捷键注册失败（可能被占用）：\n{string.Join("\n", failedKeys)}";
                        if (globalHotkeyFail != null)
                            msg += $"\n全局热键 {globalHotkeyFail} 注册失败，可稍后在'全局开关'中重新设置";
                        if (pinHotkeyFail)
                            msg += "\n贴图快捷键注册失败，可稍后在“贴图工具”中重新设置";
                        System.Windows.MessageBox.Show(msg, "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        System.Windows.MessageBox.Show("\u914D\u7F6E\u5BFC\u5165\u6210\u529F\uFF01", "\u63D0\u793A",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    await _settingsService.SaveAsync(_settings);
                    RefreshListView();
                    // ✅ Undo: 导入整批配置不属于"单步操作"，清空撤销栈
                    _undoService.Clear();
                    UpdateUndoButtonState();
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"\u5BFC\u5165\u5931\u8D25: {ex.Message}", "\u9519\u8BEF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = "Vector\u914D\u7F6E.json"
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_settings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(dialog.FileName, json);
            }
            catch (Exception ex)
            {
                await _settingsService.LogAsync($"\u5BFC\u51FA\u5931\u8D25: {ex.Message}");
            }
        }
    }
    private void LvShortcuts_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LvShortcuts.SelectedItem is ShortcutConfig cfg)
            EnterEditMode(cfg);
    }
    private void EnterEditMode(ShortcutConfig cfg)
    {
        _editingIndex = _settings.LocalShortcuts.IndexOf(cfg);
        TxtLocalKey.Text = cfg.Key;
        CboType.SelectedIndex = cfg.Type switch
        {
            "\u6587\u4EF6\u5939" => 1,
            "\u7F51\u9875" => 2,
            "\u6587\u4EF6" => 3,
            _ => 0
        };
        TxtTarget.Text = cfg.Target;
        TxtArguments.Text = cfg.Arguments ?? "";
        TxtWorkingDir.Text = cfg.WorkingDirectory ?? "";
        SetRunStrategySelection(cfg.RunStrategy);
        UpdateExtraFieldsEnabled();
        // 编辑时若已有高级字段，自动展开，避免用户看不到已有配置
        bool hasAdvanced =
            !string.IsNullOrWhiteSpace(cfg.Arguments)
            || !string.IsNullOrWhiteSpace(cfg.WorkingDirectory)
            || (!string.IsNullOrWhiteSpace(cfg.RunStrategy)
                && !string.Equals(RunStrategies.Normalize(cfg.RunStrategy), RunStrategies.Activate, StringComparison.OrdinalIgnoreCase));
        SetAdvancedExpanded(hasAdvanced);
        BtnAdd.Content = "\u66F4\u65B0";
        BtnCancelEdit.Visibility = Visibility.Visible;
        LblEditTip.Visibility = Visibility.Visible;
        TxtLocalKey.Focus();
    }
    private void TxtTarget_PreviewDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
            if (TxtTarget.TryFindResource("SurfaceHoverBrush") is System.Windows.Media.Brush b)
                TxtTarget.Background = b;
        }
    }
    private void TxtTarget_PreviewDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (TxtTarget.TryFindResource("SurfaceBrush") is System.Windows.Media.Brush b)
            TxtTarget.Background = b;
    }
    private void TxtTarget_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
        }
    }
    private void TxtTarget_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
        if (files?.Length > 0)
        {
            string path = files[0];

            // ✅ 1.0.5: 处理 Windows 快捷方式
            // .lnk → 解析 TargetPath
            // .url → 解析 URL= 行（Internet 快捷方式）
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var (target, args, workDir) = _fileTypeDetector.ResolveShortcutDetails(path);
                if (!string.IsNullOrWhiteSpace(target)
                    && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                {
                    path = target;
                    if (!string.IsNullOrEmpty(args))
                        TxtArguments.Text = args;
                    if (!string.IsNullOrEmpty(workDir))
                        TxtWorkingDir.Text = workDir;
                }
            }
            else if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                var url = _fileTypeDetector.ResolveUrlShortcut(path);
                if (!string.IsNullOrEmpty(url))
                    path = url;
            }

            TxtTarget.Text = CleanTarget(path);
            AutoDetectType(TxtTarget.Text);
            UpdateExtraFieldsEnabled();
            if (!string.IsNullOrWhiteSpace(TxtArguments.Text)
                || !string.IsNullOrWhiteSpace(TxtWorkingDir.Text))
                SetAdvancedExpanded(true);
        }
        if (TxtTarget.TryFindResource("SurfaceBrush") is System.Windows.Media.Brush b)
            TxtTarget.Background = b;
    }
    private void TxtWorkingDir_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        var files = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
        if (files?.Length > 0)
        {
            var path = files[0];
            if (System.IO.File.Exists(path))
                path = System.IO.Path.GetDirectoryName(path) ?? path;
            TxtWorkingDir.Text = CleanTarget(path);
        }
    }
    private void TxtTarget_LostFocus(object sender, RoutedEventArgs e)
    {
        // ✅ 1.0.2: 失焦时强制清理一次（应对粘贴来的脏数据）
        TxtTarget.Text = CleanTarget(TxtTarget.Text);
        if (!string.IsNullOrWhiteSpace(TxtTarget.Text))
            AutoDetectType(TxtTarget.Text);
    }
    /// <summary>
    /// ✅ 1.0.2: 清理目标路径 —— 剪贴板/拖拽/粘贴可能带换行、引号、空格、控制字符，
    /// 这些脏数据如果进 File.Exists 会抛 ArgumentException，且会污染 JSON 配置。
    /// </summary>
    private static string CleanTarget(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        // 去除常见污染物：首尾空白 + 首尾引号 + 控制字符（\r \n \t）
        var trimmed = raw.Trim().Trim(' ', '"', '\'', '\r', '\n', '\t');
        // 进一步去除中间残留的控制字符（保留 ASCII 可见字符 + Unicode 路径字符）
        var sb = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t' || ch == '\0')
                continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }
    private void AutoDetectType(string target)
    {
        var type = _fileTypeDetector.Detect(target);
        CboType.SelectedIndex = type switch
        {
            "\u6587\u4EF6\u5939" => 1,
            "\u7F51\u9875" => 2,
            "\u6587\u4EF6" => 3,
            _ => 0
        };
    }
    private void TxtLocalKey_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        CaptureModifierKey(e, TxtLocalKey);
    }
    private void TxtGlobalKey_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        CaptureModifierKey(e, TxtGlobalKey);
    }
    private void CaptureModifierKey(System.Windows.Input.KeyEventArgs e, System.Windows.Controls.TextBox target)
    {
        if (e.Key == Key.Escape)
        {
            if (_editingIndex >= 0) BtnCancelEdit_Click(null!, null!);
            return;
        }
        // Bug 8: 过滤纯修饰键，防止生成 "Ctrl+LeftCtrl" 之类的无效热键
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LWin || e.Key == Key.RWin)
            return;
        // 必须至少有一个修饰键
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
            return;
        // Bug 8: 修复数字键显示（D1→1, NumPad1→1）
        string keyStr = e.Key switch
        {
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
            Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            Key.NumPad0 => "0", Key.NumPad1 => "1", Key.NumPad2 => "2", Key.NumPad3 => "3",
            Key.NumPad4 => "4", Key.NumPad5 => "5", Key.NumPad6 => "6", Key.NumPad7 => "7",
            Key.NumPad8 => "8", Key.NumPad9 => "9",
            Key.OemTilde => "`", Key.OemMinus => "-", Key.OemPlus => "=",
            Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
            Key.OemBackslash => "\\", Key.OemPipe => "|",
            _ => e.Key.ToString()
        };
        string hotkey = "";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) hotkey += "Ctrl+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) hotkey += "Alt+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) hotkey += "Shift+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) hotkey += "Win+";
        hotkey += keyStr;
        target.Text = hotkey;
        e.Handled = true;
    }
    private void RefreshListView()
    {
        // ✅ 1.0.5: 检测是否需要重新备份原始顺序
        // 触发条件：首次 / ObservableCollection 实例被替换（导入/撤销）/ 数量不一致（增删）
        bool needRebackup = _originalOrder == null
            || _originalOrderSourceRef != _settings.LocalShortcuts
            || _originalOrder!.Count != _settings.LocalShortcuts.Count;

        if (needRebackup)
        {
            _originalOrder = new List<ShortcutConfig>(_settings.LocalShortcuts);
            _originalOrderSourceRef = _settings.LocalShortcuts;
        }

        LocalShortcuts.Clear();

        // ✅ 1.0.5: 按排序状态决定填充顺序
        IEnumerable<ShortcutConfig> source = _settings.LocalShortcuts;
        if (_sortByType == true)
        {
            // 优先级：程序 → 文件夹 → 网页；同类型内保持原始顺序
            source = _settings.LocalShortcuts
                .OrderBy(s => GetTypePriority(s.Type))
                .ThenBy(s => SafeIndexOf(_originalOrder!, s));
        }
        else
        {
            // 恢复原始顺序（按 _originalOrder 排序）
            source = _settings.LocalShortcuts
                .OrderBy(s => SafeIndexOf(_originalOrder!, s));
        }

        foreach (var s in source)
            LocalShortcuts.Add(s);

        LblCount.Text = $"{_settings.LocalShortcuts.Count} \u9879";
        LblEmptyHint.Visibility = LocalShortcuts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _foregroundMonitor?.TriggerCurrent();
    }

    /// <summary>
    /// ✅ 1.0.5: 安全 IndexOf —— 如果 s 不在 originalOrder 里（极端情况下的竞态），
    /// 返回大数把它排到末尾，而不是抛 ArgumentException
    /// </summary>
    private static int SafeIndexOf(List<ShortcutConfig> list, ShortcutConfig item)
    {
        int idx = list.IndexOf(item);
        return idx < 0 ? int.MaxValue : idx;
    }

    /// <summary>
    /// ✅ 1.0.5: 类型排序优先级
    /// </summary>
    private static int GetTypePriority(string type)
    {
        return type switch
        {
            "\u7A0B\u5E8F" => 0,  // 程序
            "\u6587\u4EF6\u5939" => 1,  // 文件夹
            "\u7F51\u9875" => 2,  // 网页
            "\u6587\u4EF6" => 3,  // 文件
            _ => 4,  // 未知类型
        };
    }

    /// <summary>
    /// ✅ 1.0.5: 点击"类型"列头 → 切换排序状态
    /// </summary>
    private void HeaderType_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 三态切换：null → true → null（首次点击排序，再次点击恢复）
        if (_sortByType == null)
            _sortByType = true;
        else
            _sortByType = null;

        // ✅ 因为 HeaderType 在 ListView 的 ControlTemplate 内部，x:Name 不会作为字段生成
        // 用 Tag 标识的 TextBlock 找回来，更新箭头
        var header = sender as System.Windows.Controls.TextBlock;
        if (header != null)
            header.Text = _sortByType == true ? "类型 \u25B2" : "类型";

        RefreshListView();
    }
    // ==================== Ctrl+Z 撤销 ====================
    /// <summary>CommandBinding CanExecute：控制 Ctrl+Z 和按钮的启用状态</summary>
    private void OnUndoCanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
    {
        // ✅ TextBox 焦点劫持防御：让 TextBox 自己处理撤销（撤销输入字符）
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
        {
            e.CanExecute = false;
            return;
        }
        e.CanExecute = _undoService.CanUndo;
    }
    /// <summary>CommandBinding Executed：Ctrl+Z 触发</summary>
    private void OnUndoExecuted(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        DoUndo();
    }
    /// <summary>撤销按钮点击</summary>
    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        DoUndo();
    }
    /// <summary>刷新按钮 Tooltip 和启用态</summary>
    private void UpdateUndoButtonState()
    {
        if (BtnUndo == null) return; // InitializeComponent 之前
        var desc = _undoService.PeekDescription();
        BtnUndo.IsEnabled = _undoService.CanUndo;
        BtnUndo.ToolTip = desc != null ? $"撤销：{desc}" : "撤销（无可撤销操作）";
        BtnUndo.Opacity = _undoService.CanUndo ? 1.0 : 0.5;
    }
    /// <summary>执行撤销：恢复 LocalShortcuts + 重注册热键 + 持久化</summary>
    private async void DoUndo()
    {
        var entry = _undoService.Undo();
        if (entry == null) return;
        // ✅ 1. 写回 _settings.LocalShortcuts（直接换 ObservableCollection 实例，
        //    只触发一次 PropertyChanged + 一次 CollectionChanged，避免 Clear+Add×N 闪烁）
        _settings.LocalShortcuts = new ObservableCollection<ShortcutConfig>(entry.Snapshot);
        // ✅ 修复：不再让 LocalShortcuts 与 _settings.LocalShortcuts 指向同一实例
        //    由 RefreshListView() 统一重建 UI 集合，彻底避免双写 / 引用合并问题
        // ✅ 3. 重注册所有热键（ID 重新映射为 1..N）+ 收集失败项 toast
        _hotkeyService.UnregisterAll();
        var failedKeys = _isListening
            ? _hotkeyService.RegisterAll(_settings.LocalShortcuts)
            : new List<string>();
        // ✅ 4. 持久化
        await _settingsService.SaveAsync(_settings);
        // ✅ 5. 退出编辑模式（撤销后 _editingIndex 指向的项可能已不存在或已变化）
        if (_editingIndex >= 0)
            BtnCancelEdit_Click(null!, null!);
        // ✅ 6. 1.0.7 修复: 统一调用 RefreshListView，让 _sortByType 排序状态正确应用
        //    （之前手动 LblCount/LblEmptyHint 更新会绕过排序，撤销后排序丢失）
        RefreshListView();
        // ✅ 7. 失败反馈 + 撤销提示
        if (failedKeys.Count > 0)
            System.Windows.MessageBox.Show(
                $"已撤销：{entry.Description}\n但以下快捷键注册失败：\n{string.Join("\n", failedKeys)}",
                "撤销完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            System.Windows.MessageBox.Show($"已撤销：{entry.Description}", "撤销",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }
    /// <summary>触发属性变更通知（替换 ObservableCollection 实例时用）</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    // ✅ 1.0.1: 脚本执行失败回调 —— 弹气泡 + 写日志
    private void OnShortcutExecuteFailed(string keyName, string reason, int? exitCode)
    {
        // 窗口可能已关闭或正在关闭，跳过 UI 更新
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (IsTrayIconValid)
            {
                var title = $"快捷键 {keyName} 执行失败";
                // 截断过长的错误信息（气泡提示最多 256 字符）
                var tip = reason.Length > 200 ? reason[..200] + "..." : reason;
                _trayIcon!.ShowBalloonTip(3000, title, tip, Forms.ToolTipIcon.Warning);
            }
        });
    }
}
