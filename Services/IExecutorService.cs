using System;
using Vector.Models;

namespace Vector.Services;

public interface IExecutorService
{
    void Execute(ShortcutConfig cfg);
    void SetDiagnosticLogging(bool enabled);

    /// ✅ 1.0.1: 脚本执行失败事件 —— UI 订阅后弹气泡通知
    event Action<string, string, int?>? ExecuteFailed;  // (keyName, reason, exitCode)
}
