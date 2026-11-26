using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vector.Services;

/// <summary>
/// 纯字节转发的 MCP stdio ↔ 命名管道（Vector.Mcp）桥接器。
/// 不解析 JSON、不引用 ModelContextProtocol 程序集、不初始化 WPF 或托盘。
/// </summary>
public static class McpStdioBridge
{
    public const string BridgeArg = "--mcp-bridge";
    private const string PipeName = "Vector.Mcp";
    private const int ConnectTimeoutMs = 1000;

    private const string PipeUnavailableJsonRpcError =
        "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32000,\"message\":\"Vector MCP pipe 'Vector.Mcp' is unavailable. Ensure Vector is running and MCP is enabled.\"}}\n";

    public static bool HasBridgeArg(string[]? args)
    {
        if (args == null || args.Length == 0)
            return false;

        foreach (var arg in args)
        {
            if (string.Equals(arg, BridgeArg, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static int Run()
    {
        return RunAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync()
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await WriteJsonRpcErrorToStdoutAsync().ConfigureAwait(false);
            return 1;
        }

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var cts = new CancellationTokenSource();

        var stdinToPipe = PumpAsync(stdin, pipe, cts.Token);
        var pipeToStdout = PumpAsync(pipe, stdout, cts.Token);

        await Task.WhenAny(stdinToPipe, pipeToStdout).ConfigureAwait(false);
        cts.Cancel();

        try
        {
            pipe.Dispose();
        }
        catch
        {
        }

        try
        {
            await Task.WhenAll(stdinToPipe, pipeToStdout).ConfigureAwait(false);
        }
        catch
        {
        }

        return 0;
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (bytesRead <= 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task WriteJsonRpcErrorToStdoutAsync()
    {
        try
        {
            using var stdout = Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(PipeUnavailableJsonRpcError);
            await stdout.WriteAsync(bytes.AsMemory(0, bytes.Length)).ConfigureAwait(false);
            await stdout.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
