namespace Vector.Models;

public sealed class PinSettings
{
    public bool Enabled { get; set; } = true;
    public bool DrawTrace { get; set; } = true;
    public string GestureColor { get; set; } = "#6366F1";
    public string FailureColor { get; set; } = "#94A3B8";
    public int Sensitive { get; set; } = 50;
    public double StartDistance { get; set; } = 8;
    public double TraceWidth { get; set; } = 3;
    public string SnapshotHotkey { get; set; } = "Ctrl+Alt+A";
    public string ClipboardHotkey { get; set; } = "Ctrl+Alt+S";
    /// <summary>手势框图和屏幕框选成功后，是否复制截图到系统剪贴板。</summary>
    public bool CopyCaptureToClipboard { get; set; } = false;
    public double Alpha { get; set; } = 0.88;
    public double MinAlpha { get; set; } = 0.25;
    public double MaxAlpha { get; set; } = 1;
    public double AlphaStep { get; set; } = 0.05;
}
