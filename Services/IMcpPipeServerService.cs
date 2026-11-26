using System;
using System.Threading.Tasks;

namespace Vector.Services;

/// <summary>
/// 托盘进程内只读 MCP 命名管道服务端（Vector.Mcp）。
/// </summary>
public interface IMcpPipeServerService : IDisposable
{
    bool IsRunning { get; }
    void Start();
    Task StopAsync();
    void SetEnabled(bool enabled);
}
