using System;
using Vector.Models;

namespace Vector.Services;

public interface IPinFeatureService
{
    event Action<string>? NoticeRequested;

    void Initialize(PinSettings settings);
    void UpdateSettings(PinSettings settings);
    void SetEnabled(bool enabled);
    void StartRegionCapture();
    void PinClipboard();
    void Shutdown();
}
