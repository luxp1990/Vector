using System;
using System.Collections.Generic;
using Vector.Models;

namespace Vector.Services;

public interface IUndoService
{
    /// <summary>当前是否可以撤销（栈非空）</summary>
    bool CanUndo { get; }

    /// <summary>栈顶快照描述，无则返回 null（用于按钮 Tooltip 显示）</summary>
    string? PeekDescription();

    /// <summary>记录一次写操作前的快照（在写操作发生前调用）</summary>
    /// <param name="currentList">写操作前的当前 LocalShortcuts（会被 JSON 深拷贝）</param>
    /// <param name="operation">操作类型</param>
    /// <param name="description">用户可读描述</param>
    void Record(IEnumerable<ShortcutConfig> currentList, UndoOperationType operation, string description);

    /// <summary>弹出栈顶快照；空栈返回 null</summary>
    UndoEntry? Undo();

    /// <summary>清空撤销栈（导入配置/重置等场景使用）</summary>
    void Clear();

    /// <summary>栈变化时触发（用于刷新按钮启用状态）</summary>
    event EventHandler? StateChanged;
}
