using System.Collections.Generic;

namespace Vector.Models;

public enum UndoOperationType
{
    Add,
    Update,
    Delete,
}

public class UndoEntry
{
    /// <summary>操作类型（用于显示/过滤，未来可扩展）</summary>
    public UndoOperationType Operation { get; init; }

    /// <summary>用户可读描述，例如 "添加 Ctrl+Alt+S" 或 "删除 3 项"</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>操作前的 LocalShortcuts 深拷贝（用 JSON 序列化做的纯净深拷贝）</summary>
    public List<ShortcutConfig> Snapshot { get; init; } = new();
}
