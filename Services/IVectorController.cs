using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Vector.Models;

namespace Vector.Services;

public sealed record ShortcutOperationResult(
    bool Success,
    string? ErrorTitle = null,
    string? ErrorMessage = null);

public sealed record UndoOperationResult(
    string Description,
    IReadOnlyList<string> FailedKeys);

public sealed record ImportSettingsResult(
    bool Success,
    bool CancelledOrEmpty = false,
    string? ValidationError = null,
    string? GlobalHotkeyFail = null,
    IReadOnlyList<string>? FailedLocalKeys = null);

public sealed record GlobalHotkeyResult(
    bool Success,
    string? ErrorTitle = null,
    string? ErrorMessage = null,
    bool FocusInput = false);

public interface IVectorController
{
    AppSettings Settings { get; }
    ObservableCollection<ShortcutConfig> Shortcuts { get; }
    bool IsListening { get; }
    bool HotkeysPausedByGlobal { get; }

    event EventHandler? ShortcutsChanged;
    event EventHandler? ListeningChanged;
    event Action<string, string, int?>? ExecuteFailed;

    Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default);
    void ResetToDefaultSettings();
    Task SaveSettingsAsync(CancellationToken ct = default);

    Task<ShortcutOperationResult> AddShortcutAsync(ShortcutConfig cfg, CancellationToken ct = default);
    Task<ShortcutOperationResult> UpdateShortcutAsync(int index, ShortcutConfig cfg, CancellationToken ct = default);
    Task<IReadOnlyList<string>> DeleteShortcutsAsync(IReadOnlyList<ShortcutConfig> items, CancellationToken ct = default);
    Task<IReadOnlyList<string>> DeleteShortcutAtAsync(int index, CancellationToken ct = default);

    Task<IReadOnlyList<string>> SetListeningAsync(bool enabled, CancellationToken ct = default);
    IReadOnlyList<string> SetHotkeysPausedByGlobal(bool paused);
    IReadOnlyList<string> ReregisterShortcutsAfterResume();

    void Execute(ShortcutConfig cfg);
    bool ExecuteByKey(string key);

    Task<UndoOperationResult?> UndoAsync(CancellationToken ct = default);

    Task<ImportSettingsResult> ImportFromFileAsync(string filePath, CancellationToken ct = default);
    Task<ImportSettingsResult> ImportFromJsonAsync(string json, CancellationToken ct = default);
    Task ExportToFileAsync(string filePath, CancellationToken ct = default);
    string ExportToJson();

    Task<GlobalHotkeyResult> SetGlobalHotkeyAsync(string newKey, CancellationToken ct = default);

    string? FindHotkeyConflict(
        string key,
        int? ignoredLocalIndex = null,
        bool ignoreGlobal = false,
        PinSettings? pinSettings = null,
        string? ignoredPinKey = null);

    void EvaluateForegroundPassthrough(string processName, bool isFullscreen);
    void SetDiagnosticLogging(bool enabled);
    void SetCoexistenceEnabled(bool enabled);
    void SetMcpEnabled(bool enabled);
    void SetMcpAllowWrites(bool enabled);
    void SetMcpAllowExecute(bool enabled);
    string RegenerateMcpToken();
    void SetLanguage(string language);
    Task SetStartupDelayAsync(int seconds, CancellationToken ct = default);
}
