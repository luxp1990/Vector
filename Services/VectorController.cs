using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vector.Models;

namespace Vector.Services;

public sealed class VectorController : IVectorController, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IHotkeyService _hotkeyService;
    private readonly IExecutorService _executorService;
    private readonly IUndoService _undoService;
    private readonly IFileTypeDetector _fileTypeDetector;
    private readonly ILanguageService _languageService;

    private AppSettings _settings = new();
    private bool _isListening = true;
    private bool _hotkeysPausedByGlobal;

    public AppSettings Settings => _settings;
    public ObservableCollection<ShortcutConfig> Shortcuts => _settings.LocalShortcuts;
    public bool IsListening => _isListening;
    public bool HotkeysPausedByGlobal => _hotkeysPausedByGlobal;

    public event EventHandler? ShortcutsChanged;
    public event EventHandler? ListeningChanged;
    public event Action<string, string, int?>? ExecuteFailed;

    public VectorController(
        ISettingsService settingsService,
        IHotkeyService hotkeyService,
        IExecutorService executorService,
        IUndoService undoService,
        IFileTypeDetector fileTypeDetector,
        ILanguageService languageService)
    {
        _settingsService = settingsService;
        _hotkeyService = hotkeyService;
        _executorService = executorService;
        _undoService = undoService;
        _fileTypeDetector = fileTypeDetector;
        _languageService = languageService;

        _executorService.ExecuteFailed += OnExecutorExecuteFailed;
    }

    private void OnExecutorExecuteFailed(string keyName, string reason, int? exitCode)
        => ExecuteFailed?.Invoke(keyName, reason, exitCode);

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        _settings = await _settingsService.LoadAsync(ct);
        _hotkeyService.LogInternalHotkeyConflicts(
            _settings.LocalShortcuts,
            _settings.GlobalHotKey,
            _settings.Pin?.SnapshotHotkey,
            _settings.Pin?.ClipboardHotkey,
            _settings.Pin?.ToggleHotkey,
            "startup");

        if (_settings.GlobalHotKey != "None" && !_hotkeyService.RegisterGlobal(_settings.GlobalHotKey))
        {
            _settings.GlobalHotKey = "None";
            await _settingsService.SaveAsync(_settings, ct);
        }

        _settings.Pin ??= new PinSettings();
        _settings.Coexistence ??= new HotkeyCoexistenceSettings();
        _isListening = _settings.HotkeysEnabled;
        _hotkeysPausedByGlobal = false;
        _hotkeyService.SetDiagnosticLogging(_settings.HotkeyDiagnosticsEnabled);
        _executorService.SetDiagnosticLogging(_settings.HotkeyDiagnosticsEnabled);

        IReadOnlyList<string> failedKeys = _isListening
            ? _hotkeyService.RegisterAll(_settings.LocalShortcuts)
            : Array.Empty<string>();

        _undoService.Clear();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        ListeningChanged?.Invoke(this, EventArgs.Empty);
        return failedKeys;
    }

    public void ResetToDefaultSettings()
    {
        _settings = new AppSettings();
        _isListening = _settings.HotkeysEnabled;
        _hotkeysPausedByGlobal = false;
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        ListeningChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task SaveSettingsAsync(CancellationToken ct = default)
        => _settingsService.SaveAsync(_settings, ct);

    public async Task<ShortcutOperationResult> AddShortcutAsync(ShortcutConfig cfg, CancellationToken ct = default)
    {
        var enteringKey = cfg.Key.Trim();
        var rejectReason = _hotkeyService.GetHotkeyRejectReason(enteringKey);
        if (rejectReason != null)
        {
            return new ShortcutOperationResult(
                false,
                _languageService.GetString("Msg_HotkeyUnavailableTitle"),
                rejectReason);
        }

        string? internalConflict = FindHotkeyConflict(enteringKey);
        if (internalConflict != null)
        {
            return new ShortcutOperationResult(
                false,
                _languageService.GetString("Msg_HotkeyConflictTitle"),
                internalConflict);
        }

        if (_settings.LocalShortcuts.Any(s => s.Key == enteringKey))
        {
            return new ShortcutOperationResult(
                false,
                null,
                _languageService.GetString("Msg_HotkeyInUse"));
        }

        var normalized = new ShortcutConfig
        {
            Key = enteringKey,
            Type = cfg.Type,
            Target = cfg.Target,
            Arguments = cfg.Arguments?.Trim() ?? "",
            WorkingDirectory = cfg.WorkingDirectory?.Trim() ?? "",
            RunStrategy = RunStrategies.Normalize(cfg.RunStrategy)
        };

        var preAddSnapshot = _settings.LocalShortcuts.ToList();
        _settings.LocalShortcuts.Add(normalized);
        int newId = _settings.LocalShortcuts.Count;
        if (_isListening && !_hotkeyService.RegisterSingle(normalized, newId))
        {
            _settings.LocalShortcuts.RemoveAt(_settings.LocalShortcuts.Count - 1);
            return new ShortcutOperationResult(
                false,
                _languageService.GetString("Msg_HotkeyRegisterFailedTitle"),
                _languageService.Format(
                    "Msg_SingleHotkeyFailed_Format",
                    normalized.Key,
                    GetHotkeyRegisterFailureDetail(_languageService.GetString("Msg_HotkeyOccupied_Generic"))));
        }

        _undoService.Record(
            preAddSnapshot,
            UndoOperationType.Add,
            _languageService.Format("Undo_Desc_Add", normalized.Key));

        await _settingsService.SaveAsync(_settings, ct);
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        return new ShortcutOperationResult(true);
    }

    public async Task<ShortcutOperationResult> UpdateShortcutAsync(int index, ShortcutConfig cfg, CancellationToken ct = default)
    {
        if (index < 0 || index >= _settings.LocalShortcuts.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var enteringKey = cfg.Key.Trim();
        var isEditingSameKey = _settings.LocalShortcuts[index].Key == enteringKey;
        if (!isEditingSameKey)
        {
            var rejectReason = _hotkeyService.GetHotkeyRejectReason(enteringKey);
            if (rejectReason != null)
            {
                return new ShortcutOperationResult(
                    false,
                    _languageService.GetString("Msg_HotkeyUnavailableTitle"),
                    rejectReason);
            }
        }

        string? internalConflict = FindHotkeyConflict(enteringKey, ignoredLocalIndex: index);
        if (internalConflict != null)
        {
            return new ShortcutOperationResult(
                false,
                _languageService.GetString("Msg_HotkeyConflictTitle"),
                internalConflict);
        }

        for (int i = 0; i < _settings.LocalShortcuts.Count; i++)
        {
            if (i != index && _settings.LocalShortcuts[i].Key == enteringKey)
            {
                return new ShortcutOperationResult(
                    false,
                    null,
                    _languageService.GetString("Msg_HotkeyInUse"));
            }
        }

        var normalized = new ShortcutConfig
        {
            Key = enteringKey,
            Type = cfg.Type,
            Target = cfg.Target,
            Arguments = cfg.Arguments?.Trim() ?? "",
            WorkingDirectory = cfg.WorkingDirectory?.Trim() ?? "",
            RunStrategy = RunStrategies.Normalize(cfg.RunStrategy)
        };

        var oldCfg = _settings.LocalShortcuts[index];
        var preEditSnapshot = _settings.LocalShortcuts.ToList();
        _settings.LocalShortcuts[index] = normalized;

        if (_isListening && !_hotkeyService.RegisterSingle(normalized, index + 1))
        {
            _settings.LocalShortcuts[index] = oldCfg;
            return new ShortcutOperationResult(
                false,
                _languageService.GetString("Msg_HotkeyRegisterFailedTitle"),
                _languageService.Format(
                    "Msg_SingleHotkeyFailed_Format",
                    normalized.Key,
                    GetHotkeyRegisterFailureDetail(_languageService.GetString("Msg_HotkeyOccupied_Generic"))));
        }

        _undoService.Record(
            preEditSnapshot,
            UndoOperationType.Update,
            _languageService.Format("Undo_Desc_Edit", oldCfg.Key));

        await _settingsService.SaveAsync(_settings, ct);
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        return new ShortcutOperationResult(true);
    }

    public async Task<IReadOnlyList<string>> DeleteShortcutsAsync(IReadOnlyList<ShortcutConfig> items, CancellationToken ct = default)
    {
        if (items.Count == 0)
            return Array.Empty<string>();

        var desc = items.Count == 1
            ? _languageService.Format("Undo_Desc_DeleteSingle", items[0].Key)
            : _languageService.Format("Undo_Desc_DeleteMultiple", items.Count);
        _undoService.Record(_settings.LocalShortcuts, UndoOperationType.Delete, desc);

        _hotkeyService.UnregisterAll();
        foreach (var cfg in items)
        {
            _settings.LocalShortcuts.Remove(cfg);
        }

        IReadOnlyList<string> failedKeys = _isListening
            ? _hotkeyService.RegisterAll(_settings.LocalShortcuts)
            : Array.Empty<string>();

        await _settingsService.SaveAsync(_settings, ct);
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        return failedKeys;
    }

    public Task<IReadOnlyList<string>> DeleteShortcutAtAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= _settings.LocalShortcuts.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return DeleteShortcutsAsync(new[] { _settings.LocalShortcuts[index] }, ct);
    }

    public async Task<IReadOnlyList<string>> SetListeningAsync(bool enabled, CancellationToken ct = default)
    {
        _settings.HotkeysEnabled = enabled;
        _hotkeysPausedByGlobal = false;
        var failedKeys = ApplyListeningStateInternal();
        await _settingsService.SaveAsync(_settings, ct);
        return failedKeys;
    }

    public IReadOnlyList<string> SetHotkeysPausedByGlobal(bool paused)
    {
        _hotkeysPausedByGlobal = paused;
        return ApplyListeningStateInternal();
    }

    private IReadOnlyList<string> ApplyListeningStateInternal()
    {
        _isListening = _settings.HotkeysEnabled && !_hotkeysPausedByGlobal;
        IReadOnlyList<string> failedKeys;
        if (_isListening)
        {
            failedKeys = _hotkeyService.RegisterAll(_settings.LocalShortcuts);
        }
        else
        {
            _hotkeyService.UnregisterAll();
            failedKeys = Array.Empty<string>();
        }
        ListeningChanged?.Invoke(this, EventArgs.Empty);
        return failedKeys;
    }

    public IReadOnlyList<string> ReregisterShortcutsAfterResume()
    {
        _hotkeyService.ResetNativeRegistrations();
        _hotkeyService.LogInternalHotkeyConflicts(
            _settings.LocalShortcuts,
            _settings.GlobalHotKey,
            _settings.Pin?.SnapshotHotkey,
            _settings.Pin?.ClipboardHotkey,
            _settings.Pin?.ToggleHotkey,
            "resume");

        if (_settings.GlobalHotKey != "None")
            _hotkeyService.RegisterGlobal(_settings.GlobalHotKey);

        return _isListening
            ? _hotkeyService.RegisterAll(_settings.LocalShortcuts)
            : Array.Empty<string>();
    }

    public void Execute(ShortcutConfig cfg)
    {
        if (_settings.HotkeyDiagnosticsEnabled)
            _ = _settingsService.LogAsync($"Hotkey callback dispatched: key={cfg.Key}; target={cfg.Target}");
        _executorService.Execute(cfg);
    }

    public bool ExecuteByKey(string key)
    {
        var cfg = _settings.LocalShortcuts.FirstOrDefault(
            s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
        if (cfg == null)
            return false;
        Execute(cfg);
        return true;
    }

    public async Task<UndoOperationResult?> UndoAsync(CancellationToken ct = default)
    {
        var entry = _undoService.Undo();
        if (entry == null)
            return null;

        _settings.LocalShortcuts = new ObservableCollection<ShortcutConfig>(entry.Snapshot);
        _hotkeyService.LogInternalHotkeyConflicts(
            _settings.LocalShortcuts,
            _settings.GlobalHotKey,
            _settings.Pin?.SnapshotHotkey,
            _settings.Pin?.ClipboardHotkey,
            _settings.Pin?.ToggleHotkey,
            "undo");

        _hotkeyService.UnregisterAll();
        IReadOnlyList<string> failedKeys = _isListening
            ? _hotkeyService.RegisterAll(_settings.LocalShortcuts)
            : Array.Empty<string>();

        await _settingsService.SaveAsync(_settings, ct);
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        return new UndoOperationResult(entry.Description, failedKeys);
    }

    public async Task<ImportSettingsResult> ImportFromFileAsync(string filePath, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        return await ImportFromJsonAsync(json, ct);
    }

    public async Task<ImportSettingsResult> ImportFromJsonAsync(string json, CancellationToken ct = default)
    {
        var imported = JsonSerializer.Deserialize<AppSettings>(json);
        if (imported == null)
            return new ImportSettingsResult(false, CancelledOrEmpty: true);

        string? validationError = ValidateImportedSettings(imported);
        if (validationError != null)
            return new ImportSettingsResult(false, ValidationError: validationError);

        _hotkeyService.UnregisterAll();
        _hotkeyService.UnregisterPinHotkeys();
        _hotkeyService.UnregisterPinToggleHotkey();

        _settings = imported;
        _settings.LocalShortcuts ??= new ObservableCollection<ShortcutConfig>();
        _settings.Pin ??= new PinSettings();
        _settings.Coexistence ??= new HotkeyCoexistenceSettings();
        _hotkeysPausedByGlobal = false;
        _hotkeyService.SetDiagnosticLogging(_settings.HotkeyDiagnosticsEnabled);
        _executorService.SetDiagnosticLogging(_settings.HotkeyDiagnosticsEnabled);

        string? globalHotkeyFail = null;
        if (_settings.GlobalHotKey != "None" && !string.IsNullOrWhiteSpace(_settings.GlobalHotKey))
        {
            if (!_hotkeyService.RegisterGlobal(_settings.GlobalHotKey))
                globalHotkeyFail = _settings.GlobalHotKey;
        }
        else
        {
            _hotkeyService.RegisterGlobal("None");
            _settings.GlobalHotKey = "None";
        }

        _isListening = _settings.HotkeysEnabled;
        IReadOnlyList<string> failedKeys = _isListening
            ? _hotkeyService.RegisterAll(_settings.LocalShortcuts)
            : Array.Empty<string>();

        await _settingsService.SaveAsync(_settings, ct);
        _undoService.Clear();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        ListeningChanged?.Invoke(this, EventArgs.Empty);

        return new ImportSettingsResult(
            true,
            GlobalHotkeyFail: globalHotkeyFail,
            FailedLocalKeys: failedKeys);
    }

    public async Task ExportToFileAsync(string filePath, CancellationToken ct = default)
    {
        var json = ExportToJson();
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    public string ExportToJson()
        => JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });

    public async Task<GlobalHotkeyResult> SetGlobalHotkeyAsync(string newKey, CancellationToken ct = default)
    {
        string normalized = string.IsNullOrWhiteSpace(newKey) ? "None" : newKey.Trim();
        if (normalized != "None")
        {
            string? rejectReason = _hotkeyService.GetHotkeyRejectReason(normalized);
            string? conflict = FindHotkeyConflict(normalized, ignoreGlobal: true);
            if (rejectReason != null || conflict != null)
            {
                return new GlobalHotkeyResult(
                    false,
                    _languageService.GetString("Msg_HotkeyConflictTitle"),
                    rejectReason ?? conflict!);
            }
        }

        if (_hotkeyService.RegisterGlobal(normalized))
        {
            _settings.GlobalHotKey = normalized;
            await _settingsService.SaveAsync(_settings, ct);
            return new GlobalHotkeyResult(true);
        }

        int errorCode = _hotkeyService.LastRegisterError;
        string message = errorCode switch
        {
            0 => _languageService.GetString("Msg_GlobalRegisterFailed_Generic"),
            1409 => _languageService.GetString("Msg_GlobalRegisterFailed_1409"),
            _ => _languageService.Format("Msg_GlobalRegisterFailed_Code_Format", errorCode)
        };
        return new GlobalHotkeyResult(
            false,
            _languageService.GetString("Common_Error"),
            message,
            FocusInput: true);
    }

    public string? FindHotkeyConflict(
        string key,
        int? ignoredLocalIndex = null,
        bool ignoreGlobal = false,
        PinSettings? pinSettings = null,
        string? ignoredPinKey = null)
    {
        if (!HotkeyService.TryGetHotkeySignature(key, out string signature))
            return _languageService.GetString("Conflict_InvalidFormat");

        for (int i = 0; i < _settings.LocalShortcuts.Count; i++)
        {
            if (i == ignoredLocalIndex)
                continue;
            if (HotkeyService.TryGetHotkeySignature(_settings.LocalShortcuts[i].Key, out string other)
                && other == signature)
                return _languageService.Format("Conflict_LocalUsed_Format", _settings.LocalShortcuts[i].Key);
        }

        if (!ignoreGlobal
            && !string.IsNullOrWhiteSpace(_settings.GlobalHotKey)
            && !string.Equals(_settings.GlobalHotKey, "None", StringComparison.OrdinalIgnoreCase)
            && HotkeyService.TryGetHotkeySignature(_settings.GlobalHotKey, out string global)
            && global == signature)
            return _languageService.GetString("Conflict_GlobalUsed");

        PinSettings pins = pinSettings ?? _settings.Pin;
        if (ignoredPinKey != "snapshot"
            && HotkeyService.TryGetHotkeySignature(pins.SnapshotHotkey, out string snapshot)
            && snapshot == signature)
            return _languageService.GetString("Conflict_PinSnapshotUsed");
        if (ignoredPinKey != "clipboard"
            && HotkeyService.TryGetHotkeySignature(pins.ClipboardHotkey, out string clipboard)
            && clipboard == signature)
            return _languageService.GetString("Conflict_PinClipboardUsed");
        if (ignoredPinKey != "toggle"
            && !string.IsNullOrWhiteSpace(pins.ToggleHotkey)
            && HotkeyService.TryGetHotkeySignature(pins.ToggleHotkey, out string toggle)
            && toggle == signature)
            return _languageService.GetString("Conflict_PinToggleUsed");

        return null;
    }

    public void EvaluateForegroundPassthrough(string processName, bool isFullscreen)
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

    public void SetDiagnosticLogging(bool enabled)
    {
        if (enabled == _settings.HotkeyDiagnosticsEnabled)
            return;

        _settings.HotkeyDiagnosticsEnabled = enabled;
        _hotkeyService.SetDiagnosticLogging(enabled);
        _executorService.SetDiagnosticLogging(enabled);
        _ = _settingsService.LogAsync($"Hotkey diagnostic logging {(enabled ? "enabled" : "disabled")}");
        _ = _settingsService.SaveAsync(_settings);
    }

    public void SetCoexistenceEnabled(bool enabled)
    {
        _settings.Coexistence ??= new HotkeyCoexistenceSettings();
        if (enabled == _settings.Coexistence.Enabled)
            return;

        _settings.Coexistence.Enabled = enabled;
        _ = _settingsService.LogAsync($"Hotkey coexistence {(enabled ? "enabled" : "disabled")}");
        _ = _settingsService.SaveAsync(_settings);
    }

    public void SetMcpEnabled(bool enabled)
    {
        if (enabled == _settings.McpEnabled)
            return;

        _settings.McpEnabled = enabled;
        _ = _settingsService.LogAsync($"MCP server {(enabled ? "enabled" : "disabled")}");
        _ = _settingsService.SaveAsync(_settings);
    }

    public void SetMcpAllowWrites(bool enabled)
    {
        if (enabled == _settings.McpAllowWrites)
            return;

        _settings.McpAllowWrites = enabled;
        _ = _settingsService.LogAsync($"MCP write tools {(enabled ? "enabled" : "disabled")}");
        _ = _settingsService.SaveAsync(_settings);
    }

    public void SetMcpAllowExecute(bool enabled)
    {
        if (enabled == _settings.McpAllowExecute)
            return;

        _settings.McpAllowExecute = enabled;
        _ = _settingsService.LogAsync($"MCP execute tool {(enabled ? "enabled" : "disabled")}");
        _ = _settingsService.SaveAsync(_settings);
    }

    public string RegenerateMcpToken()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .ToLowerInvariant();
        _settings.McpToken = token;
        _ = _settingsService.LogAsync("MCP token regenerated");
        _ = _settingsService.SaveAsync(_settings);
        return token;
    }

    public void SetLanguage(string language)
    {
        if (string.Equals(language, _settings.Language, StringComparison.Ordinal))
            return;

        _settings.Language = language;
        _languageService.Apply(language);
        _ = _settingsService.SaveAsync(_settings);
    }

    public async Task SetStartupDelayAsync(int seconds, CancellationToken ct = default)
    {
        int clamped = Math.Clamp(seconds, 0, 120);
        if (_settings.StartupDelaySeconds == clamped)
            return;

        _settings.StartupDelaySeconds = clamped;
        await _settingsService.SaveAsync(_settings, ct);
    }

    private string? ValidateImportedSettings(AppSettings imported)
    {
        var entries = new List<(string Label, string Key)>();

        if (imported.LocalShortcuts != null)
        {
            foreach (var shortcut in imported.LocalShortcuts)
                entries.Add((_languageService.GetString("Label_LocalShortcut"), shortcut?.Key ?? string.Empty));
        }

        if (!string.IsNullOrWhiteSpace(imported.GlobalHotKey)
            && !string.Equals(imported.GlobalHotKey, "None", StringComparison.OrdinalIgnoreCase))
        {
            entries.Add((_languageService.GetString("Label_GlobalHotkey"), imported.GlobalHotKey));
        }

        PinSettings pin = imported.Pin ?? new PinSettings();
        bool hasSnapshot = !string.IsNullOrWhiteSpace(pin.SnapshotHotkey);
        bool hasClipboard = !string.IsNullOrWhiteSpace(pin.ClipboardHotkey);
        if (hasSnapshot != hasClipboard)
        {
            string missingLabel = hasSnapshot
                ? _languageService.GetString("Label_PinClipboardHotkey")
                : _languageService.GetString("Label_PinSnapshotHotkey");
            return _languageService.Format("Import_PinIncomplete_Format", missingLabel);
        }
        if (hasSnapshot && hasClipboard)
        {
            entries.Add((_languageService.GetString("Label_PinSnapshotHotkey"), pin.SnapshotHotkey!));
            entries.Add((_languageService.GetString("Label_PinClipboardHotkey"), pin.ClipboardHotkey!));
        }
        if (!string.IsNullOrWhiteSpace(pin.ToggleHotkey)
            && !string.Equals(pin.ToggleHotkey.Trim(), "None", StringComparison.OrdinalIgnoreCase))
        {
            entries.Add((_languageService.GetString("Label_PinToggleHotkey"), pin.ToggleHotkey));
        }

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, key) in entries)
        {
            if (!HotkeyService.TryGetHotkeySignature(key, out string signature))
                return _languageService.Format("Import_EntryInvalidFormat_Format", label, key);

            string? rejectReason = _hotkeyService.GetHotkeyRejectReason(key);
            if (rejectReason != null)
                return _languageService.Format("Import_EntryRejected_Format", label, key, rejectReason);

            if (seen.TryGetValue(signature, out string? existing))
                return _languageService.Format("Import_EntryConflict_Format", label, key, existing);

            seen[signature] = _languageService.Format("Import_EntryLabel_Format", label, key);
        }

        return null;
    }

    private string GetHotkeyRegisterFailureDetail(string genericDetail)
    {
        int errorCode = _hotkeyService.LastRegisterError;
        return errorCode switch
        {
            0 => genericDetail,
            1409 => _languageService.GetString("Msg_HotkeyOccupied_1409"),
            _ => _languageService.Format("Msg_HotkeyOccupied_Code_Format", errorCode)
        };
    }

    public void Dispose()
    {
        _executorService.ExecuteFailed -= OnExecutorExecuteFailed;
    }
}
