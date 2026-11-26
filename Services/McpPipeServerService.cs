using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "Vector",
                Version = version
            },
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>
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
            }
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
}
