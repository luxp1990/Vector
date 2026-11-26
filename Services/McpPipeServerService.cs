using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vector.Models;

namespace Vector.Services;

/// <summary>
/// 托盘进程内只读 MCP 命名管道服务端。
/// 管道名固定为 Vector.Mcp，仅允许当前用户 SID 访问（管理员模式下显式授权当前用户 SID）。
/// </summary>
public sealed class McpPipeServerService : IMcpPipeServerService
{
    public const string PipeName = "Vector.Mcp";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IVectorController _controller;
    private readonly ISettingsService _settingsService;
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private bool _disposed;

    public McpPipeServerService(
        IVectorController controller,
        ISettingsService settingsService)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _cts != null && !_cts.IsCancellationRequested;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
            Start();
        else
            _ = StopAsync();
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            if (_cts != null && !_cts.IsCancellationRequested)
                return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(ct), CancellationToken.None);
        }

        _ = _settingsService.LogAsync($"[MCP] Named pipe server started (pipe={PipeName})");
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loopTask;

        lock (_lifecycleLock)
        {
            cts = _cts;
            loopTask = _acceptLoopTask;
            _cts = null;
            _acceptLoopTask = null;
        }

        if (cts == null)
            return;

        try
        {
            cts.Cancel();
            if (loopTask != null)
            {
                try
                {
                    await loopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    await _settingsService.LogAsync($"[MCP] Stop accept loop error: {ex.GetType().Name}: {ex.Message}")
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            cts.Dispose();
        }

        await _settingsService.LogAsync("[MCP] Named pipe server stopped").ConfigureAwait(false);
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            cts = _cts;
            _cts = null;
            _acceptLoopTask = null;
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    private static PipeSecurity CreateCurrentUserPipeSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User
            ?? throw new InvalidOperationException("Unable to resolve current Windows user SID.");

        var security = new PipeSecurity();
        // 仅允许当前用户 SID；在管理员模式下显式授权当前用户 SID，确保同用户的非提权 MCP 客户端也可连接
        security.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        PipeSecurity pipeSecurity;
        try
        {
            pipeSecurity = CreateCurrentUserPipeSecurity();
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync($"[MCP] Failed to create PipeSecurity: {ex.GetType().Name}: {ex.Message}")
                .ConfigureAwait(false);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var clientPipe = pipe;
                pipe = null; // 所有权移交给 HandleClientAsync
                _ = Task.Run(() => HandleClientAsync(clientPipe, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                await _settingsService.LogAsync($"[MCP] Pipe accept error: {ex.GetType().Name}: {ex.Message}")
                    .ConfigureAwait(false);
                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        await _settingsService.LogAsync($"[MCP:{sessionId}] Client connected").ConfigureAwait(false);

        try
        {
            await using var transport = new StreamServerTransport(pipe, pipe, $"Vector-{sessionId}");
            var options = CreateServerOptions();
            await using var server = McpServer.Create(transport, options);
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常关闭
        }
        catch (IOException ex)
        {
            await _settingsService.LogAsync($"[MCP:{sessionId}] Pipe disconnected: {ex.Message}")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync($"[MCP:{sessionId}] Session error: {ex.GetType().Name}: {ex.Message}")
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            await _settingsService.LogAsync($"[MCP:{sessionId}] Session closed").ConfigureAwait(false);
        }
    }

    private McpServerOptions CreateServerOptions()
    {
        var version = typeof(McpPipeServerService).Assembly.GetName().Version?.ToString(3) ?? "2.0.3";
        var settings = _controller.Settings;

        var tools = new McpServerPrimitiveCollection<McpServerTool>
        {
            McpServerTool.Create(
                (CancellationToken ct) => ListShortcutsAsync(ct),
                new McpServerToolCreateOptions
                {
                    Name = "list_shortcuts",
                    Description = "List all configured local shortcuts in Vector (read-only).",
                    ReadOnly = true,
                    Destructive = false,
                    Idempotent = true,
                    OpenWorld = false
                }),
            McpServerTool.Create(
                (CancellationToken ct) => GetStatusAsync(ct),
                new McpServerToolCreateOptions
                {
                    Name = "get_status",
                    Description = "Get Vector runtime status and settings summary (read-only).",
                    ReadOnly = true,
                    Destructive = false,
                    Idempotent = true,
                    OpenWorld = false
                }),
            McpServerTool.Create(
                (CancellationToken ct) => ExportConfigAsync(ct),
                new McpServerToolCreateOptions
                {
                    Name = "export_config",
                    Description = "Export current Vector configuration as JSON (read-only).",
                    ReadOnly = true,
                    Destructive = false,
                    Idempotent = true,
                    OpenWorld = false
                })
        };

        if (settings.McpAllowWrites)
        {
            tools.Add(McpServerTool.Create(
                (
                    [Description("MCP access token configured in Vector settings.")] string token,
                    [Description("Hotkey combination, e.g. 'Ctrl+Alt+N'.")] string key,
                    [Description("Target program path, folder path, URL, or file path.")] string target,
                    [Description("Shortcut type: Program, Folder, Web, or File (default: Program).")] string? type,
                    [Description("Optional command-line arguments.")] string? arguments,
                    [Description("Optional working directory.")] string? workingDirectory,
                    [Description("Run strategy: Activate (default), New, or Toggle.")] string? runStrategy,
                    CancellationToken ct) =>
                    AddShortcutAsync(token, key, target, type, arguments, workingDirectory, runStrategy, ct),
                new McpServerToolCreateOptions
                {
                    Name = "add_shortcut",
                    Description = "Add a new local shortcut to Vector (requires McpAllowWrites and valid McpToken).",
                    ReadOnly = false,
                    Destructive = false,
                    Idempotent = false,
                    OpenWorld = false
                }));

            tools.Add(McpServerTool.Create(
                (
                    [Description("MCP access token configured in Vector settings.")] string token,
                    [Description("0-based shortcut index from list_shortcuts.")] int index,
                    [Description("Hotkey combination, e.g. 'Ctrl+Alt+N'.")] string key,
                    [Description("Target program path, folder path, URL, or file path.")] string target,
                    [Description("Shortcut type: Program, Folder, Web, or File (default: Program).")] string? type,
                    [Description("Optional command-line arguments.")] string? arguments,
                    [Description("Optional working directory.")] string? workingDirectory,
                    [Description("Run strategy: Activate (default), New, or Toggle.")] string? runStrategy,
                    CancellationToken ct) =>
                    UpdateShortcutAsync(token, index, key, target, type, arguments, workingDirectory, runStrategy, ct),
                new McpServerToolCreateOptions
                {
                    Name = "update_shortcut",
                    Description = "Update an existing local shortcut by index (requires McpAllowWrites and valid McpToken).",
                    ReadOnly = false,
                    Destructive = true,
                    Idempotent = true,
                    OpenWorld = false
                }));

            tools.Add(McpServerTool.Create(
                (
                    [Description("MCP access token configured in Vector settings.")] string token,
                    [Description("0-based shortcut index from list_shortcuts.")] int index,
                    CancellationToken ct) =>
                    DeleteShortcutAsync(token, index, ct),
                new McpServerToolCreateOptions
                {
                    Name = "delete_shortcut",
                    Description = "Delete a local shortcut by index (requires McpAllowWrites and valid McpToken).",
                    ReadOnly = false,
                    Destructive = true,
                    Idempotent = false,
                    OpenWorld = false
                }));

            tools.Add(McpServerTool.Create(
                (
                    [Description("MCP access token configured in Vector settings.")] string token,
                    [Description("True to enable hotkey listening, false to pause.")] bool enabled,
                    CancellationToken ct) =>
                    SetListeningAsync(token, enabled, ct),
                new McpServerToolCreateOptions
                {
                    Name = "set_listening",
                    Description = "Enable or pause global hotkey listening (requires McpAllowWrites and valid McpToken).",
                    ReadOnly = false,
                    Destructive = false,
                    Idempotent = true,
                    OpenWorld = false
                }));

            tools.Add(McpServerTool.Create(
                (
                    [Description("MCP access token configured in Vector settings.")] string token,
                    [Description("Vector configuration JSON string (same schema as export_config).")] string json,
                    CancellationToken ct) =>
                    ImportConfigAsync(token, json, ct),
                new McpServerToolCreateOptions
                {
                    Name = "import_config",
                    Description = "Import and apply Vector configuration JSON (requires McpAllowWrites and valid McpToken).",
                    ReadOnly = false,
                    Destructive = true,
                    Idempotent = true,
                    OpenWorld = false
                }));
        }

        if (settings.McpAllowExecute)
        {
            tools.Add(McpServerTool.Create(
                (
                    [Description("MCP access token configured in Vector settings.")] string token,
                    [Description("0-based shortcut index from list_shortcuts (optional if key is provided).")] int? index,
                    [Description("Hotkey string, e.g. 'Ctrl+Alt+N' (optional if index is provided).")] string? key,
                    CancellationToken ct) =>
                    ExecuteShortcutAsync(token, index, key, ct),
                new McpServerToolCreateOptions
                {
                    Name = "execute_shortcut",
                    Description = "Execute a configured shortcut target by index or key (high risk; requires McpAllowExecute and valid McpToken).",
                    ReadOnly = false,
                    Destructive = true,
                    Idempotent = false,
                    OpenWorld = true
                }));
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "Vector",
                Version = version
            },
            ToolCollection = tools
        };
    }

    private static async Task<T> InvokeOnUiThreadAsync<T>(Func<T> func, CancellationToken ct)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is not { } dispatcher || dispatcher.HasShutdownStarted)
            throw new InvalidOperationException("WPF Application Dispatcher is unavailable.");

        return await dispatcher.InvokeAsync(func, DispatcherPriority.Normal, ct)
            .Task
            .ConfigureAwait(false);
    }

    private static async Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> func, CancellationToken ct)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is not { } dispatcher || dispatcher.HasShutdownStarted)
            throw new InvalidOperationException("WPF Application Dispatcher is unavailable.");

        return await dispatcher.InvokeAsync(func, DispatcherPriority.Normal, ct)
            .Task
            .Unwrap()
            .ConfigureAwait(false);
    }

    private async Task<string> ListShortcutsAsync(CancellationToken ct)
    {
        await _settingsService.LogAsync("[MCP] Tool call: list_shortcuts", ct).ConfigureAwait(false);
        try
        {
            var payload = await InvokeOnUiThreadAsync(() =>
            {
                var items = _controller.Shortcuts
                    .Select((s, index) => new
                    {
                        index,
                        key = s.Key,
                        type = s.Type,
                        target = s.Target,
                        arguments = s.Arguments,
                        workingDirectory = s.WorkingDirectory,
                        runStrategy = s.RunStrategy
                    })
                    .ToList();

                return new
                {
                    count = items.Count,
                    shortcuts = items
                };
            }, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(payload, s_jsonOptions);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync(
                $"[MCP] Tool error (list_shortcuts): {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> GetStatusAsync(CancellationToken ct)
    {
        await _settingsService.LogAsync("[MCP] Tool call: get_status", ct).ConfigureAwait(false);
        try
        {
            var payload = await InvokeOnUiThreadAsync(() =>
            {
                var settings = _controller.Settings;
                return new
                {
                    isListening = _controller.IsListening,
                    hotkeysEnabled = settings.HotkeysEnabled,
                    hotkeysPausedByGlobal = _controller.HotkeysPausedByGlobal,
                    globalHotKey = settings.GlobalHotKey,
                    shortcutCount = _controller.Shortcuts.Count,
                    pinEnabled = settings.Pin?.Enabled ?? false,
                    hotkeyDiagnosticsEnabled = settings.HotkeyDiagnosticsEnabled,
                    coexistenceEnabled = settings.Coexistence?.Enabled ?? false,
                    mcpEnabled = settings.McpEnabled,
                    language = settings.Language,
                    theme = settings.Theme,
                    runAsAdmin = settings.RunAsAdmin,
                    startupDelaySeconds = settings.StartupDelaySeconds
                };
            }, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(payload, s_jsonOptions);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync(
                $"[MCP] Tool error (get_status): {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> ExportConfigAsync(CancellationToken ct)
    {
        await _settingsService.LogAsync("[MCP] Tool call: export_config", ct).ConfigureAwait(false);
        try
        {
            return await InvokeOnUiThreadAsync(() => _controller.ExportToJson(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync(
                $"[MCP] Tool error (export_config): {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private void ValidateMcpAccess(string? providedToken, bool requireExecute = false)
    {
        var settings = _controller.Settings;
        if (requireExecute)
        {
            if (!settings.McpAllowExecute)
                throw new UnauthorizedAccessException("MCP shortcut execution is disabled in Vector settings.");
        }
        else
        {
            if (!settings.McpAllowWrites)
                throw new UnauthorizedAccessException("MCP write operations are disabled in Vector settings.");
        }

        var expectedToken = settings.McpToken ?? string.Empty;
        if (string.IsNullOrEmpty(expectedToken))
            throw new UnauthorizedAccessException("MCP token is not configured. Generate a token in Vector settings before calling write or execute tools.");

        var providedBytes = Encoding.UTF8.GetBytes(providedToken ?? string.Empty);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            throw new UnauthorizedAccessException("Invalid MCP token.");
    }

    private static string NormalizeShortcutType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return ShortcutTypes.Program;

        return type.Trim().ToLowerInvariant() switch
        {
            "program" or "程序" => ShortcutTypes.Program,
            "folder" or "文件夹" => ShortcutTypes.Folder,
            "web" or "网页" => ShortcutTypes.Web,
            "file" or "文件" => ShortcutTypes.File,
            _ => throw new ArgumentException($"Unsupported shortcut type '{type}'. Expected Program, Folder, Web, or File.")
        };
    }

    private async Task<string> AddShortcutAsync(
        string token,
        string key,
        string target,
        string? type,
        string? arguments,
        string? workingDirectory,
        string? runStrategy,
        CancellationToken ct)
    {
        await _settingsService.LogAsync("[MCP] Tool call: add_shortcut", ct).ConfigureAwait(false);
        try
        {
            var payload = await InvokeOnUiThreadAsync(async () =>
            {
                ValidateMcpAccess(token);

                if (string.IsNullOrWhiteSpace(key))
                    throw new ArgumentException("Shortcut key cannot be empty.", nameof(key));
                if (string.IsNullOrWhiteSpace(target))
                    throw new ArgumentException("Shortcut target cannot be empty.", nameof(target));

                var cfg = new ShortcutConfig
                {
                    Key = key.Trim(),
                    Type = NormalizeShortcutType(type),
                    Target = target.Trim(),
                    Arguments = arguments?.Trim() ?? string.Empty,
                    WorkingDirectory = workingDirectory?.Trim() ?? string.Empty,
                    RunStrategy = RunStrategies.Normalize(runStrategy)
                };

                var result = await _controller.AddShortcutAsync(cfg, ct);
                if (!result.Success)
                    throw new InvalidOperationException($"{result.ErrorTitle ?? "Add shortcut failed"}: {result.ErrorMessage}");

                return new
                {
                    success = true,
                    index = _controller.Shortcuts.Count - 1,
                    shortcut = new
                    {
                        key = cfg.Key,
                        type = cfg.Type,
                        target = cfg.Target,
                        arguments = cfg.Arguments,
                        workingDirectory = cfg.WorkingDirectory,
                        runStrategy = cfg.RunStrategy
                    }
                };
            }, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(payload, s_jsonOptions);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync(
                $"[MCP] Tool error (add_shortcut): {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> UpdateShortcutAsync(
        string token,
        int index,
        string key,
        string target,
        string? type,
        string? arguments,
        string? workingDirectory,
        string? runStrategy,
        CancellationToken ct)
    {
        await _settingsService.LogAsync($"[MCP] Tool call: update_shortcut (index={index})", ct).ConfigureAwait(false);
        try
        {
            var payload = await InvokeOnUiThreadAsync(async () =>
            {
                ValidateMcpAccess(token);

                if (index < 0 || index >= _controller.Shortcuts.Count)
                    throw new ArgumentOutOfRangeException(nameof(index), $"Shortcut index {index} is out of range (0..{_controller.Shortcuts.Count - 1}).");
                if (string.IsNullOrWhiteSpace(key))
                    throw new ArgumentException("Shortcut key cannot be empty.", nameof(key));
                if (string.IsNullOrWhiteSpace(target))
                    throw new ArgumentException("Shortcut target cannot be empty.", nameof(target));

                var cfg = new ShortcutConfig
                {
                    Key = key.Trim(),
                    Type = NormalizeShortcutType(type),
                    Target = target.Trim(),
                    Arguments = arguments?.Trim() ?? string.Empty,
                    WorkingDirectory = workingDirectory?.Trim() ?? string.Empty,
                    RunStrategy = RunStrategies.Normalize(runStrategy)
                };

                var result = await _controller.UpdateShortcutAsync(index, cfg, ct);
                if (!result.Success)
                    throw new InvalidOperationException($"{result.ErrorTitle ?? "Update shortcut failed"}: {result.ErrorMessage}");

                return new
                {
                    success = true,
                    index,
                    shortcut = new
                    {
                        key = cfg.Key,
                        type = cfg.Type,
                        target = cfg.Target,
                        arguments = cfg.Arguments,
                        workingDirectory = cfg.WorkingDirectory,
                        runStrategy = cfg.RunStrategy
                    }
                };
            }, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(payload, s_jsonOptions);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync(
                $"[MCP] Tool error (update_shortcut): {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> DeleteShortcutAsync(string token, int index, CancellationToken ct)
    {
        await _settingsService.LogAsync($"[MCP] Tool call: delete_shortcut (index={index})", ct).ConfigureAwait(false);
        try
        {
            var payload = await InvokeOnUiThreadAsync(async () =>
            {
                ValidateMcpAccess(token);

                if (index < 0 || index >= _controller.Shortcuts.Count)
                    throw new ArgumentOutOfRangeException(nameof(index), $"Shortcut index {index} is out of range (0..{_controller.Shortcuts.Count - 1}).");

                var failedKeys = await _controller.DeleteShortcutAtAsync(index, ct);
                return new
                {
                    success = true,
                    deletedIndex = index,
                    shortcutCount = _controller.Shortcuts.Count,
                    failedKeys
                };
            }, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(payload, s_jsonOptions);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync(
                $"[MCP] Tool error (delete_shortcut): {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> SetListeningAsync(string token, bool enabled, CancellationToken ct)
    {
        await _settingsService.LogAsync($"[MCP] Tool call: set_listening (enabled={enabled})", ct).ConfigureAwait(false);
        try
        {
            var payload = await InvokeOnUiThreadAsync(async () =>
            {
                ValidateMcpAccess(token);

                var failedKeys = await _controller.SetListeningAsync(enabled, ct);
                return new
                {
                    success = true,
                    isListening = _controller.IsListening,
                    failedKeys
                };
            }, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(payload, s_jsonOptions);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync(
                $"[MCP] Tool error (set_listening): {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> ImportConfigAsync(string token, string json, CancellationToken ct)
    {
        await _settingsService.LogAsync("[MCP] Tool call: import_config", ct).ConfigureAwait(false);
        try
        {
            var payload = await InvokeOnUiThreadAsync(async () =>
            {
                ValidateMcpAccess(token);

                if (string.IsNullOrWhiteSpace(json))
                    throw new ArgumentException("Configuration JSON cannot be empty.", nameof(json));

                var result = await _controller.ImportFromJsonAsync(json, ct);
                if (!result.Success)
                {
                    if (!string.IsNullOrWhiteSpace(result.ValidationError))
                        throw new ArgumentException(result.ValidationError);
                    if (result.CancelledOrEmpty)
                        throw new ArgumentException("Configuration JSON is empty or invalid.");
                    throw new InvalidOperationException("Failed to import configuration.");
                }

                return new
                {
                    success = true,
                    shortcutCount = _controller.Shortcuts.Count,
                    globalHotkeyFail = result.GlobalHotkeyFail,
                    failedLocalKeys = result.FailedLocalKeys
                };
            }, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(payload, s_jsonOptions);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync(
                $"[MCP] Tool error (import_config): {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> ExecuteShortcutAsync(string token, int? index, string? key, CancellationToken ct)
    {
        await _settingsService.LogAsync($"[MCP] Tool call: execute_shortcut (index={index}, key={key})", ct).ConfigureAwait(false);
        try
        {
            var payload = await InvokeOnUiThreadAsync(() =>
            {
                ValidateMcpAccess(token, requireExecute: true);

                ShortcutConfig? targetShortcut = null;
                if (index.HasValue)
                {
                    if (index.Value < 0 || index.Value >= _controller.Shortcuts.Count)
                        throw new ArgumentOutOfRangeException(nameof(index), $"Shortcut index {index.Value} is out of range (0..{_controller.Shortcuts.Count - 1}).");

                    targetShortcut = _controller.Shortcuts[index.Value];
                    _controller.Execute(targetShortcut);
                }
                else if (!string.IsNullOrWhiteSpace(key))
                {
                    var normalizedKey = key.Trim();
                    targetShortcut = _controller.Shortcuts.FirstOrDefault(
                        s => string.Equals(s.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
                    if (targetShortcut == null || !_controller.ExecuteByKey(normalizedKey))
                        throw new ArgumentException($"No shortcut found with key '{normalizedKey}'.");
                }
                else
                {
                    throw new ArgumentException("Either 'index' or 'key' must be provided.");
                }

                return new
                {
                    success = true,
                    key = targetShortcut.Key,
                    type = targetShortcut.Type,
                    target = targetShortcut.Target
                };
            }, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(payload, s_jsonOptions);
        }
        catch (Exception ex)
        {
            await _settingsService.LogAsync(
                $"[MCP] Tool error (execute_shortcut): {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
