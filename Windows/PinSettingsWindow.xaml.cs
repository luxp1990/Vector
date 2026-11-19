using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vector.Models;

namespace Vector.Windows;

public partial class PinSettingsWindow : Window
{
    private readonly Action _snapshot;
    private readonly Action _clipboard;

    public PinSettingsWindow(PinSettings settings, Action snapshot, Action clipboard)
    {
        InitializeComponent();
        _snapshot = snapshot;
        _clipboard = clipboard;
        ChkEnabled.IsChecked = settings.Enabled;
        ChkDrawTrace.IsChecked = settings.DrawTrace;
        ChkCopyCaptureToClipboard.IsChecked = settings.CopyCaptureToClipboard;
        TxtSnapshotKey.Text = settings.SnapshotHotkey;
        TxtClipboardKey.Text = settings.ClipboardHotkey;
        SldSensitivity.Value = settings.Sensitive;
        SldAlpha.Value = settings.Alpha;
        ResultSettings = Clone(settings);
    }

    public PinSettings ResultSettings { get; private set; }

    private void Hotkey_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox target)
            return;
        if (e.Key is Key.Escape or Key.Back)
        {
            target.Clear();
            e.Handled = true;
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            LblError.Text = "快捷键至少需要一个修饰键";
            e.Handled = true;
            return;
        }

        string keyName = DisplayKey(key);
        if (string.IsNullOrEmpty(keyName))
        {
            e.Handled = true;
            return;
        }

        var parts = new System.Collections.Generic.List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(keyName);
        target.Text = string.Join("+", parts);
        LblError.Text = "";
        e.Handled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string snapshot = TxtSnapshotKey.Text.Trim();
        string clipboard = TxtClipboardKey.Text.Trim();
        if (string.IsNullOrEmpty(snapshot) || string.IsNullOrEmpty(clipboard))
        {
            LblError.Text = "两个贴图快捷键都不能为空";
            return;
        }
        if (string.Equals(snapshot, clipboard, StringComparison.OrdinalIgnoreCase))
        {
            LblError.Text = "两个贴图快捷键不能相同";
            return;
        }

        ResultSettings.Enabled = ChkEnabled.IsChecked == true;
        ResultSettings.DrawTrace = ChkDrawTrace.IsChecked == true;
        ResultSettings.CopyCaptureToClipboard = ChkCopyCaptureToClipboard.IsChecked == true;
        ResultSettings.SnapshotHotkey = snapshot;
        ResultSettings.ClipboardHotkey = clipboard;
        ResultSettings.Sensitive = (int)Math.Round(SldSensitivity.Value);
        ResultSettings.Alpha = SldAlpha.Value;
        DialogResult = true;
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e) => _snapshot();

    private void Clipboard_Click(object sender, RoutedEventArgs e) => _clipboard();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static PinSettings Clone(PinSettings settings) => new()
    {
        Enabled = settings.Enabled,
        DrawTrace = settings.DrawTrace,
        CopyCaptureToClipboard = settings.CopyCaptureToClipboard,
        GestureColor = settings.GestureColor,
        FailureColor = settings.FailureColor,
        Sensitive = settings.Sensitive,
        StartDistance = settings.StartDistance,
        TraceWidth = settings.TraceWidth,
        SnapshotHotkey = settings.SnapshotHotkey,
        ClipboardHotkey = settings.ClipboardHotkey,
        Alpha = settings.Alpha,
        MinAlpha = settings.MinAlpha,
        MaxAlpha = settings.MaxAlpha,
        AlphaStep = settings.AlphaStep
    };

    private static string DisplayKey(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
            return ((int)(key - Key.D0)).ToString();
        return key switch
        {
            Key.OemPlus => "=",
            Key.OemMinus => "-",
            Key.Oem3 => "`",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemBackslash => "\\",
            _ => key.ToString()
        };
    }
}
