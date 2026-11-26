
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
    private readonly IVectorController _controller;
    private readonly IFileTypeDetector _fileTypeDetector;
    private readonly IThemeService _themeService;
    private readonly ILanguageService _languageService;
    private readonly IUndoService _undoService;
    private readonly IPinFeatureService _pinFeatureService;
    private readonly IMcpPipeServerService _mcpPipeServer;
    private int _editingIndex = -1;
    private bool _isPinActive = true;
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
    private System.Windows.Controls.MenuItem? _miEdit;
    private System.Windows.Controls.MenuItem? _miDelete;
    private System.Windows.Controls.TextBlock? _headerTypeTextBlock;
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
        IVectorController controller,
        IFileTypeDetector fileTypeDetector,
        IThemeService themeService,
        ILanguageService languageService,
        IUndoService undoService,
        IPinFeatureService pinFeatureService,
        IMcpPipeServerService mcpPipeServer)
    {
        _hotkeyService = hotkeyService;
        _settingsService = settingsService;
        _controller = controller;
        _fileTypeDetector = fileTypeDetector;
        _themeService = themeService;
        _languageService = languageService;
        _undoService = undoService;
        _pinFeatureService = pinFeatureService;
        _mcpPipeServer = mcpPipeServer;
        InitializeComponent();
        DataContext = this;
        // 撤销栈状态变化时刷新按钮启用态
        _undoService.StateChanged += (_, _) => UpdateUndoButtonState();
        _languageService.LanguageChanged += OnLanguageChanged;
        _controller.ShortcutsChanged += OnControllerShortcutsChanged;
        _controller.ListeningChanged += OnControllerListeningChanged;
        UpdateUndoButtonState();
        var cm = new System.Windows.Controls.ContextMenu
        {
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
        };
        _miEdit = new System.Windows.Controls.MenuItem { Header = _languageService.GetString("Common_Edit") };
        _miDelete = new System.Windows.Controls.MenuItem { Header = _languageService.GetString("Common_Delete") };
        cm.Items.Add(_miEdit);
        cm.Items.Add(_miDelete);
        LvShortcuts.ContextMenu = cm;
        cm.Opened += (s, e) =>
        {
            if (LvShortcuts.SelectedItems.Count == 0
                && cm.PlacementTarget is System.Windows.Controls.ListViewItem lvi)
                lvi.IsSelected = true;
        };
        _miEdit.Click += (s, e) =>
        {
            var item = LvShortcuts.SelectedItem as ShortcutConfig;
            if (item != null) EnterEditMode(item);
        };
        _miDelete.Click += async (s, e) =>
        {
            if (LvShortcuts.SelectedItems.Count == 0) return;
            var selectedConfigs = LvShortcuts.SelectedItems
                .Cast<ShortcutConfig>()
                .ToList();
            foreach (var cfg in selectedConfigs)
                _originalOrder?.Remove(cfg);
            var failedKeys = await _controller.DeleteShortcutsAsync(selectedConfigs);
            if (failedKeys.Count > 0)
                System.Windows.MessageBox.Show(
                    _languageService.Format("Msg_HotkeysRegisterFailed_Format", string.Join("\n", failedKeys)),
                    _languageService.GetString("Common_Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
            // ✅ Bug N2: 删除后直接退出编辑模式，避免 _editingIndex 指向错误项目
            if (_editingIndex >= 0)
                BtnCancelEdit_Click(null!, null!);
        };
    }

    private void OnControllerShortcutsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess())
            RefreshListView();
        else
            Dispatcher.BeginInvoke(new Action(RefreshListView));
    }

    private void OnControllerListeningChanged(object? sender, EventArgs e)
    {
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        void Sync()
        {
            _suppressToggleSync = true;
            if (TglHotkeys != null)
                TglHotkeys.IsChecked = _controller.Settings.HotkeysEnabled;
            _suppressToggleSync = false;
            UpdateStatusUi();
        }
        if (Dispatcher.CheckAccess())
            Sync();
        else
            Dispatcher.BeginInvoke(new Action(Sync));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        RefreshListView();
        if (_miEdit != null) _miEdit.Header = _languageService.GetString("Common_Edit");
        if (_miDelete != null) _miDelete.Header = _languageService.GetString("Common_Delete");
        if (_headerTypeTextBlock != null)
        {
            var typeHeader = _languageService.GetString("List_Header_Type");
            _headerTypeTextBlock.Text = _sortByType == true ? $"{typeHeader} \u25B2" : typeHeader;
        }
        if (BtnAdd != null)
            BtnAdd.Content = _editingIndex >= 0
                ? _languageService.GetString("Common_Update")
                : _languageService.GetString("AddCard_Add_Btn");
        if (LblAdvancedTitle != null)
            LblAdvancedTitle.Text = _advancedExpanded
                ? _languageService.GetString("AddCard_Advanced_Expanded")
                : _languageService.GetString("AddCard_Advanced_Title");
        UpdateUndoButtonState();
        RebuildTrayMenu();
        UpdateStatusUi();
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
        _executeShortcutHandler = cfg => _controller.Execute(cfg);
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
        _hotkeyService.PinToggleHotkeyTriggered += OnPinToggleHotkeyTriggered;
        _pinFeatureService.NoticeRequested += OnPinNoticeRequested;
        // ✅ 1.0.1: 订阅脚本执行失败事件，弹气泡通知用户
        _controller.ExecuteFailed += OnShortcutExecuteFailed;
        try
        {
            var failedKeys = await _controller.LoadAsync();
            var settings = _controller.Settings;
            // ✅ 1.0.8: 先检查注册，再赋值文本框（避免失败后 UI 与实际状态不一致）
            TxtGlobalKey.Text = settings.GlobalHotKey == "None" ? "" : settings.GlobalHotKey;
            TxtStartupDelay.Text = Math.Clamp(settings.StartupDelaySeconds, 0, 120).ToString();
            // 功能开关：快捷键与截图相互独立，按持久化状态恢复
            _isPinActive = settings.Pin.Enabled;
            _pinPausedByGlobal = false;
            _suppressToggleSync = true;
            TglHotkeys.IsChecked = _controller.IsListening;
            TglPin.IsChecked = settings.Pin.Enabled;
            ChkHotkeyDiagnostics.IsChecked = settings.HotkeyDiagnosticsEnabled;
            ChkHotkeyCoexistence.IsChecked = settings.Coexistence.Enabled;
            ChkMcpEnabled.IsChecked = settings.McpEnabled;
            CboLanguage.SelectedIndex = string.Equals(settings.Language, "en-US", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _suppressToggleSync = false;
            _mcpPipeServer.SetEnabled(settings.McpEnabled);
            if (failedKeys.Count > 0)
                System.Windows.MessageBox.Show(
                    _languageService.Format("Msg_HotkeysRegisterFailed_Format", string.Join("\n", failedKeys)),
                    _languageService.GetString("Common_Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
            // 应用持久化的主题与语言（必须在 RefreshListView 之前）
            _themeService.Apply(settings.Theme != "Light");
            _languageService.Apply(settings.Language);
            RefreshListView();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                _languageService.Format("Msg_InitFailed_Format", ex.Message),
                _languageService.GetString("Common_Error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            _controller.ResetToDefaultSettings();
            _languageService.Apply(_controller.Settings.Language);
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
        _languageService.LanguageChanged -= OnLanguageChanged;
        _controller.ShortcutsChanged -= OnControllerShortcutsChanged;
        _controller.ListeningChanged -= OnControllerListeningChanged;
        _hotkeyService.UnregisterAll();
        _hotkeyService.UnregisterPinHotkeys();
        _hotkeyService.UnregisterPinToggleHotkey();
        _pinFeatureService.Shutdown();
        _hwndSource?.RemoveHook(WndProc);
        _hotkeyService.ToggleListening -= OnToggleListening;
        _hotkeyService.ExecuteShortcut -= _executeShortcutHandler;
        _hotkeyService.PinHotkeyTriggered -= _pinHotkeyHandler;
        _hotkeyService.PinToggleHotkeyTriggered -= OnPinToggleHotkeyTriggered;
        _pinFeatureService.NoticeRequested -= OnPinNoticeRequested;
        _controller.ExecuteFailed -= OnShortcutExecuteFailed;  // ✅ 1.0.1: 避免事件持有已关闭窗口
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
        var pin = _controller.Settings.Pin ??= new PinSettings();
        try
        {
            _pinFeatureService.Initialize(pin);
            // 功能开关解耦：截图功能只受截图开关控制
            _pinFeatureService.SetEnabled(_isPinActive);
            if (_isPinActive
                && !_hotkeyService.RegisterPinHotkeys(
                    pin.SnapshotHotkey,
                    pin.ClipboardHotkey))
            {
                OnPinNoticeRequested(_languageService.GetString("Msg_PinHotkeyRegisterFailed"));
            }
            if (!string.IsNullOrWhiteSpace(pin.ToggleHotkey)
                && !_hotkeyService.RegisterPinToggleHotkey(pin.ToggleHotkey))
            {
                OnPinNoticeRequested(_languageService.GetString("Msg_PinToggleHotkeyRegisterFailed"));
            }
        }
        catch (Exception ex)
        {
            _ = _settingsService.LogAsync($"初始化贴图工具失败: {ex.Message}");
            OnPinNoticeRequested(_languageService.GetString("Msg_PinInitFailed"));
        }
    }

    private void OnPinNoticeRequested(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var title = _languageService.GetString("Pin_Window_Title");
            if (IsTrayIconValid)
                _trayIcon!.ShowBalloonTip(2500, title, message, Forms.ToolTipIcon.Info);
            else
                System.Windows.MessageBox.Show(
                    message,
                    title,
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
            var failed = _controller.ReregisterShortcutsAfterResume();
            if (failed.Count > 0)
            {
                System.Windows.MessageBox.Show(
                    _languageService.Format("Msg_ResumeHotkeysRegisterFailed_Format", string.Join("\n", failed)),
                    _languageService.GetString("Common_Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var pin = _controller.Settings.Pin;
            // 功能开关解耦：截图重注册只看截图开关
            if (_isPinActive
                && !_hotkeyService.RegisterPinHotkeys(
                    pin.SnapshotHotkey,
                    pin.ClipboardHotkey))
                OnPinNoticeRequested(_languageService.GetString("Msg_ResumePinHotkeyFailed"));

            if (!string.IsNullOrWhiteSpace(pin?.ToggleHotkey)
                && !_hotkeyService.RegisterPinToggleHotkey(pin.ToggleHotkey))
                OnPinNoticeRequested(_languageService.GetString("Msg_ResumePinToggleFailed"));

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
        var settings = _controller.Settings;
        bool anyActive = (settings.HotkeysEnabled && !_controller.HotkeysPausedByGlobal)
            || (settings.Pin.Enabled && !_pinPausedByGlobal);
        _pinPausedByGlobal = anyActive && settings.Pin.Enabled;
        var failedKeys = _controller.SetHotkeysPausedByGlobal(anyActive && settings.HotkeysEnabled);
        if (failedKeys.Count > 0)
        {
            System.Windows.MessageBox.Show(
                _languageService.Format("Msg_HotkeysRegisterFailed_Format", string.Join("\n", failedKeys)),
                _languageService.GetString("Common_Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _isPinActive = settings.Pin.Enabled && !_pinPausedByGlobal;
        ApplyPinState();
    }

    /// <summary>切换截图功能后统一应用：注册或注销贴图热键并刷新状态。</summary>
    private void ApplyPinState(bool announceFailures = true)
    {
        var pin = _controller.Settings.Pin;
        if (_isPinActive)
        {
            _pinFeatureService.SetEnabled(true);
            if (!_hotkeyService.RegisterPinHotkeys(
                    pin.SnapshotHotkey,
                    pin.ClipboardHotkey)
                && announceFailures)
            {
                OnPinNoticeRequested(_languageService.GetString("Msg_PinHotkeyRegisterFailed"));
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
        var settings = _controller.Settings;
        bool hk = _controller.IsListening, pin = _isPinActive;
        bool manualHk = settings.HotkeysEnabled, manualPin = settings.Pin.Enabled;
        if (hk && pin) return _languageService.GetString("Status_Listening");
        if (!hk && !pin)
        {
            if (!manualHk && !manualPin) return _languageService.GetString("Status_AllDisabled");
            if (manualHk && manualPin) return _languageService.GetString("Status_AllPaused");
            return manualHk
                ? _languageService.GetString("Status_HotkeysPaused")
                : _languageService.GetString("Status_PinPaused");
        }
        return hk
            ? (manualPin ? _languageService.GetString("Status_PinPaused") : _languageService.GetString("Status_PinDisabled"))
            : (manualHk ? _languageService.GetString("Status_HotkeysPaused") : _languageService.GetString("Status_HotkeysDisabled"));
    }

    private string FormatTrayText(string statusText)
    {
        var text = $"Vector - {statusText}";
        if (IsUserAnAdmin())
            text += _languageService.GetString("Tray_AdminSuffix");
        return text;
    }

    /// <summary>刷新状态行、托盘图标与托盘菜单文本（功能开关的统一出口）。</summary>
    private void UpdateStatusUi()
    {
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        var settings = _controller.Settings;
        bool hk = _controller.IsListening, pin = _isPinActive;
        string text = CurrentStatusText();
        var brush = (hk && pin)
            ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
            : (System.Windows.Media.Brush)FindResource("DangerBrush");
        LblStatus.Text = text;
        LblStatus.Foreground = brush;
        StatusDot.Fill = brush;
        if (IsTrayIconValid)
        {
            _trayIcon!.Text = FormatTrayText(text);
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = CreateAppIcon(paused: !hk && !pin);
            oldIcon?.Dispose();
        }
        if (_toggleListenItem != null)
        {
            bool anyManuallyEnabled = settings.HotkeysEnabled || settings.Pin.Enabled;
            _toggleListenItem.Enabled = anyManuallyEnabled;
            _toggleListenItem.Text = (hk || pin)
                ? _languageService.GetString("Tray_Menu_PauseActive")
                : anyManuallyEnabled
                    ? _languageService.GetString("Tray_Menu_ResumeActive")
                    : _languageService.GetString("Tray_Menu_AllDisabled");
        }
        if (_togglePinItem != null)
            _togglePinItem.Text = settings.Pin.Enabled
                ? _languageService.GetString("Tray_Menu_DisablePin")
                : _languageService.GetString("Tray_Menu_EnablePin");
    }

    private async void TglHotkeys_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync) return;
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        bool enable = TglHotkeys.IsChecked == true;
        if (enable == _controller.Settings.HotkeysEnabled) return;
        var failedKeys = await _controller.SetListeningAsync(enable);
        if (failedKeys.Count > 0)
        {
            System.Windows.MessageBox.Show(
                _languageService.Format("Msg_HotkeysRegisterFailed_Format", string.Join("\n", failedKeys)),
                _languageService.GetString("Common_Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TglPin_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync) return;
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        bool enable = sender == _togglePinItem
            ? !_controller.Settings.Pin.Enabled          // 托盘菜单点击：翻转当前状态
            : TglPin.IsChecked == true;       // 主界面开关点击
        SetPinEnabled(enable);
    }

    private void OnPinToggleHotkeyTriggered()
    {
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
            SetPinEnabled(!_controller.Settings.Pin.Enabled);
        }));
    }

    private void SetPinEnabled(bool enable)
    {
        if (Dispatcher == null || Dispatcher.HasShutdownStarted) return;
        _suppressToggleSync = true;
        TglPin.IsChecked = enable;
        _suppressToggleSync = false;
        var pin = _controller.Settings.Pin;
        if (enable == pin.Enabled) return;
        pin.Enabled = enable;
        _pinPausedByGlobal = false;
        _ = _controller.SaveSettingsAsync();
        _isPinActive = pin.Enabled && !_pinPausedByGlobal;
        ApplyPinState();
    }

    private void ChkHotkeyDiagnostics_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync || Dispatcher == null || Dispatcher.HasShutdownStarted)
            return;
        _controller.SetDiagnosticLogging(ChkHotkeyDiagnostics.IsChecked == true);
    }

    private void ChkHotkeyCoexistence_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync || Dispatcher == null || Dispatcher.HasShutdownStarted)
            return;
        _controller.SetCoexistenceEnabled(ChkHotkeyCoexistence.IsChecked == true);
        _foregroundMonitor?.TriggerCurrent();
    }

    private void ChkMcpEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleSync || Dispatcher == null || Dispatcher.HasShutdownStarted)
            return;
        bool enabled = ChkMcpEnabled.IsChecked == true;
        _controller.SetMcpEnabled(enabled);
        _mcpPipeServer.SetEnabled(enabled);
    }

    private void CboLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggleSync || Dispatcher == null || Dispatcher.HasShutdownStarted)
            return;
        if (CboLanguage.SelectedItem is not ComboBoxItem item || item.Tag is not string lang)
            return;
        _controller.SetLanguage(lang);
    }

    private void OnForegroundProcessChanged(string processName, bool isFullscreen)
        => _controller.EvaluateForegroundPassthrough(processName, isFullscreen);
    private void InitTray()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateAppIcon(),
            Visible = true,
            Text = FormatTrayText(CurrentStatusText()),
        };
        _menuTray = new Forms.ContextMenuStrip();
        RebuildTrayMenu();
        _trayIcon.ContextMenuStrip = _menuTray;
        _trayIcon.DoubleClick += (s, ev) => { Show(); WindowState = WindowState.Normal; Activate(); };
    }

    private void RebuildTrayMenu()
    {
        if (_menuTray == null) return;
        _menuTray.Items.Clear();
        var menu = _menuTray;
        menu.Items.Add(_languageService.GetString("Tray_Menu_Show"), null, (s, ev) => { Show(); WindowState = WindowState.Normal; Activate(); });
        menu.Items.Add(_languageService.GetString("Tray_Menu_RegionCapture"), null, (s, ev) =>
            Dispatcher.BeginInvoke(new Action(_pinFeatureService.StartRegionCapture)));
        menu.Items.Add(_languageService.GetString("Tray_Menu_ClipboardPin"), null, (s, ev) =>
            Dispatcher.BeginInvoke(new Action(_pinFeatureService.PinClipboard)));
        menu.Items.Add(_languageService.GetString("Tray_Menu_PinSettings"), null, (s, ev) =>
            Dispatcher.BeginInvoke(new Action(OpenPinSettings)));
        menu.Items.Add(new Forms.ToolStripSeparator());

        // 暂停/恢复快捷键菜单项
        _toggleListenItem = new Forms.ToolStripMenuItem(
            (_controller.IsListening || _isPinActive)
                ? _languageService.GetString("Tray_Menu_PauseActive")
                : _languageService.GetString("Tray_Menu_ResumeActive"));
        _toggleListenItem.Click += (s, ev) => OnToggleListening();
        menu.Items.Add(_toggleListenItem);

        // 暂停/恢复截图菜单项（与主界面截图开关同步）
        _togglePinItem = new Forms.ToolStripMenuItem(
            _controller.Settings.Pin.Enabled
                ? _languageService.GetString("Tray_Menu_DisablePin")
                : _languageService.GetString("Tray_Menu_EnablePin"));
        _togglePinItem.Click += (s, ev) =>
            Dispatcher.BeginInvoke(new Action(() => TglPin_Changed(_togglePinItem!, new RoutedEventArgs())));
        menu.Items.Add(_togglePinItem);

        var reregisterItem = new Forms.ToolStripMenuItem(_languageService.GetString("Tray_Menu_ReregisterHotkeys"));
        reregisterItem.Click += (s, ev) => ReregisterHotkeysAfterResume();
        menu.Items.Add(reregisterItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var themeItem = new Forms.ToolStripMenuItem(_languageService.GetString("Tray_Menu_ToggleTheme"));
        themeItem.Click += async (s, ev) =>
        {
            _themeService.Toggle();
            _controller.Settings.Theme = _themeService.IsDark ? "Dark" : "Light";
            await _controller.SaveSettingsAsync();
        };
        menu.Items.Add(themeItem);
        // ✅ 开机自启
        var autoStartItem = new Forms.ToolStripMenuItem(_languageService.GetString("Tray_Menu_AutoStart"))
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
                System.Windows.MessageBox.Show(
                    _languageService.GetString("Msg_AutoStartFailed"),
                    _languageService.GetString("Tray_Menu_AutoStart"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        menu.Items.Add(autoStartItem);

        // ✅ 便携换机：配置写在程序目录
        var portableItem = new Forms.ToolStripMenuItem(_languageService.GetString("Tray_Menu_Portable"))
        {
            Checked = AppPaths.IsPortableMode(),
            CheckOnClick = true
        };
        portableItem.CheckedChanged += async (s, ev) =>
        {
            if (_trayCheckSuppress) return;
            // 先落盘现行目录，再拷到目标，避免启用便携后重启丢最近改动
            await _controller.SaveSettingsAsync();
            var msg = SettingsService.PreparePortableModeSwitch(portableItem.Checked, _languageService);
            var actual = AppPaths.IsPortableMode();
            if (portableItem.Checked != actual)
            {
                _trayCheckSuppress = true;
                portableItem.Checked = actual;
                _trayCheckSuppress = false;
            }
            System.Windows.MessageBox.Show(msg, _languageService.GetString("Msg_PortableTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        };
        menu.Items.Add(portableItem);

        var openCfgItem = new Forms.ToolStripMenuItem(_languageService.GetString("Tray_Menu_OpenConfigFolder"));
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
                System.Windows.MessageBox.Show(
                    _languageService.Format("Msg_OpenFolderFailed_Format", ex.Message),
                    _languageService.GetString("Common_Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        menu.Items.Add(openCfgItem);

        // ✅ 1.0.1: UIPI 缓解入口 — 托盘菜单提供"以管理员模式启动"开关
        var adminItem = new Forms.ToolStripMenuItem(_languageService.GetString("Tray_Menu_RunAsAdmin"))
        {
            Checked = _controller.Settings.RunAsAdmin,
            CheckOnClick = true
        };
        adminItem.CheckedChanged += async (s, ev) =>
        {
            _controller.Settings.RunAsAdmin = adminItem.Checked;
            await _controller.SaveSettingsAsync();
            if (adminItem.Checked && !IsUserAnAdmin())
            {
                var result = System.Windows.MessageBox.Show(
                    _languageService.GetString("Msg_AdminRestartPrompt"),
                    _languageService.GetString("Msg_AdminRestartTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    RestartAsAdmin();
                }
            }
        };
        menu.Items.Add(adminItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_languageService.GetString("Tray_Menu_Exit"), null, (s, ev) =>
        {
            _reallyExit = true;   // Bug 11: 标记真正退出
            _trayIcon!.Visible = false;
            System.Windows.Application.Current.Shutdown();
        });
    }
    // ✅ 1.0.1: 以管理员身份重启 Vector
    public void RestartAsAdmin()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                System.Windows.MessageBox.Show(
                    _languageService.GetString("Msg_ExePathNotFound"),
                    _languageService.GetString("Common_Error"),
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
                    _languageService.GetString("Msg_ElevationFailed"),
                    _languageService.GetString("Msg_ElevationFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            System.Windows.MessageBox.Show(
                _languageService.GetString("Msg_ElevationCancelled"),
                _languageService.GetString("Common_Cancelled"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                _languageService.Format("Msg_RestartFailed_Format", ex.Message),
                _languageService.GetString("Common_Error"),
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
                _trayIcon!.ShowBalloonTip(1000, "Vector", _languageService.GetString("Tray_Minimized_Tip"), Forms.ToolTipIcon.Info);
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
                _trayIcon!.ShowBalloonTip(1000, "Vector", _languageService.GetString("Tray_Minimized_Tip"), Forms.ToolTipIcon.Info);
                _hasShownMinimizeTip = true;
            }
        }
    }
    private async void BtnSetGlobal_Click(object sender, RoutedEventArgs e)
    {
        string oldKey = _controller.Settings.GlobalHotKey;   // ✅ 1.0.8: 保存旧热键，失败时恢复
        string newKey = string.IsNullOrWhiteSpace(TxtGlobalKey.Text)
            ? "None"
            : TxtGlobalKey.Text.Trim();

        var result = await _controller.SetGlobalHotkeyAsync(newKey);
        if (result.Success)
        {
            TxtGlobalKey.Text = newKey == "None" ? "" : newKey;
            System.Windows.MessageBox.Show(
                newKey == "None"
                    ? _languageService.GetString("Msg_GlobalHotkeyDisabled")
                    : _languageService.GetString("Msg_GlobalHotkeySaved"),
                _languageService.GetString("Common_Success"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show(result.ErrorMessage!, result.ErrorTitle!,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtGlobalKey.Text = oldKey == "None" ? "" : oldKey;
            if (result.FocusInput)
                TxtGlobalKey.Focus();
        }
    }

    private void BtnPinSettings_Click(object sender, RoutedEventArgs e) => OpenPinSettings();

    private async void OpenPinSettings()
    {
        var dialog = new PinSettingsWindow(
            _controller.Settings.Pin,
            _pinFeatureService.StartRegionCapture,
            _pinFeatureService.PinClipboard,
            _languageService)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        PinSettings oldSettings = _controller.Settings.Pin;
        PinSettings newSettings = dialog.ResultSettings;
        string? snapshotReject = _hotkeyService.GetHotkeyRejectReason(newSettings.SnapshotHotkey);
        string? clipboardReject = _hotkeyService.GetHotkeyRejectReason(newSettings.ClipboardHotkey);
        string? toggleReject = string.IsNullOrWhiteSpace(newSettings.ToggleHotkey)
            ? null
            : _hotkeyService.GetHotkeyRejectReason(newSettings.ToggleHotkey);
        string? snapshotConflict = _controller.FindHotkeyConflict(
            newSettings.SnapshotHotkey,
            pinSettings: newSettings,
            ignoredPinKey: "snapshot");
        string? clipboardConflict = _controller.FindHotkeyConflict(
            newSettings.ClipboardHotkey,
            pinSettings: newSettings,
            ignoredPinKey: "clipboard");
        string? toggleConflict = string.IsNullOrWhiteSpace(newSettings.ToggleHotkey)
            ? null
            : _controller.FindHotkeyConflict(
                newSettings.ToggleHotkey,
                pinSettings: newSettings,
                ignoredPinKey: "toggle");
        string? configurationConflict = snapshotReject ?? clipboardReject ?? toggleReject
            ?? snapshotConflict ?? clipboardConflict ?? toggleConflict;
        if (configurationConflict != null)
        {
            System.Windows.MessageBox.Show(configurationConflict, _languageService.GetString("Msg_HotkeyConflictTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool toggleHotkeyChanged = !string.Equals(
            oldSettings.ToggleHotkey?.Trim() ?? "",
            newSettings.ToggleHotkey?.Trim() ?? "",
            StringComparison.OrdinalIgnoreCase);
        if (toggleHotkeyChanged && !_hotkeyService.RegisterPinToggleHotkey(newSettings.ToggleHotkey))
        {
            int errorCode = _hotkeyService.LastRegisterError;
            string message = errorCode switch
            {
                0 => _languageService.GetString("Msg_PinToggleRegisterFailed_Generic"),
                1409 => _languageService.GetString("Msg_PinToggleRegisterFailed_1409"),
                _ => _languageService.Format("Msg_PinToggleRegisterFailed_Code_Format", errorCode)
            };
            System.Windows.MessageBox.Show(
                message,
                _languageService.GetString("Msg_HotkeyConflictTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
                if (toggleHotkeyChanged)
                    _hotkeyService.RegisterPinToggleHotkey(oldSettings.ToggleHotkey ?? "");
                int errorCode = _hotkeyService.LastRegisterError;
                string message = errorCode switch
                {
                    0 => _languageService.GetString("Msg_PinHotkeysRegisterFailed_Generic"),
                    1409 => _languageService.GetString("Msg_PinHotkeysRegisterFailed_1409"),
                    _ => _languageService.Format("Msg_PinHotkeysRegisterFailed_Code_Format", errorCode)
                };
                System.Windows.MessageBox.Show(
                    message,
                    _languageService.GetString("Msg_HotkeyConflictTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }
        else
            _hotkeyService.UnregisterPinHotkeys();

        _controller.Settings.Pin = newSettings;
        if (manualPinStateChanged)
            _pinPausedByGlobal = false;
        _isPinActive = _controller.Settings.Pin.Enabled && !_pinPausedByGlobal;
        _pinFeatureService.UpdateSettings(newSettings);
        _pinFeatureService.SetEnabled(_isPinActive);
        _suppressToggleSync = true;
        TglPin.IsChecked = newSettings.Enabled;
        _suppressToggleSync = false;
        UpdateStatusUi();
        await _controller.SaveSettingsAsync();
        OnPinNoticeRequested(_languageService.GetString("Msg_PinSettingsSaved"));
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

        var cfg = new ShortcutConfig
        {
            Key = TxtLocalKey.Text.Trim(),
            Type = GetSelectedType(),
            Target = TxtTarget.Text,
            Arguments = TxtArguments.Text?.Trim() ?? "",
            WorkingDirectory = TxtWorkingDir.Text?.Trim() ?? "",
            RunStrategy = GetSelectedRunStrategy()
        };

        ShortcutOperationResult result;
        if (_editingIndex >= 0)
        {
            var oldCfg = _controller.Settings.LocalShortcuts[_editingIndex];
            result = await _controller.UpdateShortcutAsync(_editingIndex, cfg);
            if (!result.Success)
            {
                if (result.ErrorTitle != null)
                    System.Windows.MessageBox.Show(result.ErrorMessage!, result.ErrorTitle,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    System.Windows.MessageBox.Show(result.ErrorMessage!);
                return;
            }
            if (_originalOrder != null)
            {
                int origIdx = _originalOrder.IndexOf(oldCfg);
                if (origIdx >= 0)
                    _originalOrder[origIdx] = _controller.Settings.LocalShortcuts[_editingIndex];
                else
                    _originalOrder = null;
            }
            _editingIndex = -1;
            BtnAdd.Content = _languageService.GetString("AddCard_Add_Btn");
            BtnCancelEdit.Visibility = Visibility.Collapsed;
        }
        else
        {
            result = await _controller.AddShortcutAsync(cfg);
            if (!result.Success)
            {
                if (result.ErrorTitle != null)
                    System.Windows.MessageBox.Show(result.ErrorMessage!, result.ErrorTitle,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    System.Windows.MessageBox.Show(result.ErrorMessage!);
                return;
            }
            _originalOrder?.Add(_controller.Settings.LocalShortcuts[^1]);
        }
        RefreshListView();
        ClearAddForm();
        LblEditTip.Visibility = Visibility.Collapsed;
    }
    private void BtnCancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _editingIndex = -1;
        BtnAdd.Content = _languageService.GetString("AddCard_Add_Btn");
        BtnCancelEdit.Visibility = Visibility.Collapsed;
        ClearAddForm();
        LblEditTip.Visibility = Visibility.Collapsed;
        LblCount.Text = _languageService.Format("Main_Count_Format", _controller.Settings.LocalShortcuts.Count);
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
            LblAdvancedTitle.Text = expanded
                ? _languageService.GetString("AddCard_Advanced_Expanded")
                : _languageService.GetString("AddCard_Advanced_Title");
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

    private string GetSelectedType()
        => (CboType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ShortcutTypes.Program;

    private void UpdateExtraFieldsEnabled()
    {
        if (CboRunStrategy == null || TxtArguments == null) return;
        var isProg = GetSelectedType() == ShortcutTypes.Program;
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
        await _controller.SetStartupDelayAsync(sec);
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
        var type = GetSelectedType();
        if (type == ShortcutTypes.Folder)
        {
            var dialog = new Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
                TxtTarget.Text = dialog.SelectedPath;
        }
        else if (type == ShortcutTypes.Web)  // Bug 9: 网页类型不弹文件选择器
        {
            TxtTarget.Focus();
            TxtTarget.SelectAll();
            System.Windows.MessageBox.Show(
                _languageService.GetString("Msg_BrowseWebHint"),
                _languageService.GetString("Common_Info"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (type == ShortcutTypes.File)  // 文件类型：选择文档
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = _languageService.GetString("FileFilter_Documents")
            };
            if (dialog.ShowDialog() == true)
                TxtTarget.Text = dialog.FileName;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = _languageService.GetString("FileFilter_Programs")
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
            var count = _controller.Settings.LocalShortcuts?.Count ?? 0;
            var confirm = System.Windows.MessageBox.Show(
                _languageService.Format("Msg_ImportConfirm_Format", count),
                _languageService.GetString("Msg_ImportConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                var result = await _controller.ImportFromFileAsync(dialog.FileName);
                if (result.CancelledOrEmpty)
                    return;

                if (result.ValidationError != null)
                {
                    System.Windows.MessageBox.Show(
                        _languageService.Format("Msg_ImportRejected_Format", result.ValidationError),
                        _languageService.GetString("Msg_ImportFailedTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Bug 18: 先清除选中状态，避免引用断裂
                LvShortcuts.SelectedIndex = -1;
                // 导入会整体替换列表：残留的编辑索引会指向新列表的错误项（甚至越界），先退出编辑模式
                if (_editingIndex >= 0) BtnCancelEdit_Click(null!, null!);

                var settings = _controller.Settings;
                _pinPausedByGlobal = false;
                _isPinActive = settings.Pin.Enabled;
                bool pinHotkeyFail = _isPinActive
                    && !_hotkeyService.RegisterPinHotkeys(
                        settings.Pin.SnapshotHotkey,
                        settings.Pin.ClipboardHotkey);
                bool pinToggleFail = !_hotkeyService.RegisterPinToggleHotkey(settings.Pin.ToggleHotkey);
                _pinFeatureService.UpdateSettings(settings.Pin);
                _pinFeatureService.SetEnabled(_isPinActive);

                _suppressToggleSync = true;
                TglHotkeys.IsChecked = _controller.IsListening;
                TglPin.IsChecked = settings.Pin.Enabled;
                ChkHotkeyDiagnostics.IsChecked = settings.HotkeyDiagnosticsEnabled;
                ChkHotkeyCoexistence.IsChecked = settings.Coexistence.Enabled;
                ChkMcpEnabled.IsChecked = settings.McpEnabled;
                _suppressToggleSync = false;
                _mcpPipeServer.SetEnabled(settings.McpEnabled);
                UpdateStatusUi();
                _foregroundMonitor?.TriggerCurrent();

                // Bug 18: 更新全局热键输入框（None → 空，勿假显示默认键）
                TxtGlobalKey.Text = result.GlobalHotkeyFail != null || settings.GlobalHotKey == "None" ? "" : settings.GlobalHotKey;
                TxtStartupDelay.Text = Math.Clamp(settings.StartupDelaySeconds, 0, 120).ToString();

                var failedKeys = result.FailedLocalKeys ?? Array.Empty<string>();
                // ✅ 1.0.8: 合并为单一消息框，避免双重弹窗
                if (failedKeys.Count > 0 || result.GlobalHotkeyFail != null || pinHotkeyFail || pinToggleFail)
                {
                    var msg = _languageService.GetString("Msg_ImportPartialSuccess_Base");
                    if (failedKeys.Count > 0)
                        msg += _languageService.Format("Msg_ImportPartialSuccess_LocalFail_Format", string.Join("\n", failedKeys));
                    if (result.GlobalHotkeyFail != null)
                        msg += _languageService.Format("Msg_ImportPartialSuccess_GlobalFail_Format", result.GlobalHotkeyFail);
                    if (pinHotkeyFail)
                        msg += _languageService.GetString("Msg_ImportPartialSuccess_PinFail");
                    if (pinToggleFail)
                        msg += _languageService.GetString("Msg_ImportPartialSuccess_PinToggleFail");
                    System.Windows.MessageBox.Show(msg, _languageService.GetString("Msg_ImportPartialTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        _languageService.GetString("Msg_ImportSuccess"),
                        _languageService.GetString("Common_Info"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                RefreshListView();
                UpdateUndoButtonState();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    _languageService.Format("Msg_ImportError_Format", ex.Message),
                    _languageService.GetString("Common_Error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = _languageService.GetString("Export_DefaultFileName")
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                await _controller.ExportToFileAsync(dialog.FileName);
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
        _editingIndex = _controller.Settings.LocalShortcuts.IndexOf(cfg);
        TxtLocalKey.Text = cfg.Key;
        CboType.SelectedIndex = cfg.Type switch
        {
            ShortcutTypes.Folder => 1,
            ShortcutTypes.Web => 2,
            ShortcutTypes.File => 3,
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
        BtnAdd.Content = _languageService.GetString("Common_Update");
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
            ShortcutTypes.Folder => 1,
            ShortcutTypes.Web => 2,
            ShortcutTypes.File => 3,
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
        var shortcuts = _controller.Settings.LocalShortcuts;
        // ✅ 1.0.5: 检测是否需要重新备份原始顺序
        // 触发条件：首次 / ObservableCollection 实例被替换（导入/撤销）/ 数量不一致（增删）
        bool needRebackup = _originalOrder == null
            || _originalOrderSourceRef != shortcuts
            || _originalOrder!.Count != shortcuts.Count;

        if (needRebackup)
        {
            _originalOrder = new List<ShortcutConfig>(shortcuts);
            _originalOrderSourceRef = shortcuts;
        }

        LocalShortcuts.Clear();

        // ✅ 1.0.5: 按排序状态决定填充顺序
        IEnumerable<ShortcutConfig> source = shortcuts;
        if (_sortByType == true)
        {
            // 优先级：程序 → 文件夹 → 网页；同类型内保持原始顺序
            source = shortcuts
                .OrderBy(s => GetTypePriority(s.Type))
                .ThenBy(s => SafeIndexOf(_originalOrder!, s));
        }
        else
        {
            // 恢复原始顺序（按 _originalOrder 排序）
            source = shortcuts
                .OrderBy(s => SafeIndexOf(_originalOrder!, s));
        }

        foreach (var s in source)
            LocalShortcuts.Add(s);

        LblCount.Text = _languageService.Format("Main_Count_Format", shortcuts.Count);
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
            ShortcutTypes.Program => 0,
            ShortcutTypes.Folder => 1,
            ShortcutTypes.Web => 2,
            ShortcutTypes.File => 3,
            _ => 4,
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
        {
            _headerTypeTextBlock = header;
            var typeHeader = _languageService.GetString("List_Header_Type");
            header.Text = _sortByType == true ? $"{typeHeader} \u25B2" : typeHeader;
        }

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
        BtnUndo.ToolTip = desc != null
            ? _languageService.Format("Undo_Tooltip_WithDesc", desc)
            : _languageService.GetString("Undo_Tooltip_Empty");
        BtnUndo.Opacity = _undoService.CanUndo ? 1.0 : 0.5;
    }
    /// <summary>执行撤销：恢复 LocalShortcuts + 重注册热键 + 持久化</summary>
    private async void DoUndo()
    {
        var result = await _controller.UndoAsync();
        if (result == null) return;
        // ✅ 5. 退出编辑模式（撤销后 _editingIndex 指向的项可能已不存在或已变化）
        if (_editingIndex >= 0)
            BtnCancelEdit_Click(null!, null!);
        // ✅ 6. 1.0.7 修复: 统一调用 RefreshListView，让 _sortByType 排序状态正确应用
        //    （之前手动 LblCount/LblEmptyHint 更新会绕过排序，撤销后排序丢失）
        RefreshListView();
        // ✅ 7. 失败反馈 + 撤销提示
        if (result.FailedKeys.Count > 0)
            System.Windows.MessageBox.Show(
                _languageService.Format("Undo_PartialFail_Format", result.Description, string.Join("\n", result.FailedKeys)),
                _languageService.GetString("Undo_Complete_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            System.Windows.MessageBox.Show(
                _languageService.Format("Undo_Success_Format", result.Description),
                _languageService.GetString("Undo_Title"),
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
                var title = _languageService.Format("Msg_ExecuteFailedTitle_Format", keyName);
                // 截断过长的错误信息（气泡提示最多 256 字符）
                var tip = reason.Length > 200 ? reason[..200] + "..." : reason;
                _trayIcon!.ShowBalloonTip(3000, title, tip, Forms.ToolTipIcon.Warning);
            }
        });
    }
}

public sealed class ShortcutTypeToDisplayConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string type || string.IsNullOrEmpty(type))
            return string.Empty;

        var key = ShortcutTypes.ToResourceKey(type);
        if (key != null && System.Windows.Application.Current?.TryFindResource(key) is string localized)
            return localized;

        return type;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
