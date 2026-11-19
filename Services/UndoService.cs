using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Vector.Models;

namespace Vector.Services;

public class UndoService : IUndoService
{
    private const int MaxDepth = 50;

    private readonly Stack<UndoEntry> _stack = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public bool CanUndo => _stack.Count > 0;

    public event EventHandler? StateChanged;

    public string? PeekDescription() => _stack.Count > 0 ? _stack.Peek().Description : null;

    public void Record(IEnumerable<ShortcutConfig> currentList, UndoOperationType operation, string description)
    {
        // JSON 序列化做深拷贝：ShortcutConfig 是纯 POCO（3 个 string 字段），
        // 序列化结果 100% 纯净，无引用泄漏/事件丢失/JsonIgnore 副作用。
        var snapshot = currentList
            .Select(c => JsonSerializer.Deserialize<ShortcutConfig>(
                JsonSerializer.Serialize(c, JsonOpts), JsonOpts)!)
            .ToList();

        _stack.Push(new UndoEntry
        {
            Operation = operation,
            Description = description,
            Snapshot = snapshot,
        });

        // 超出深度丢最早的
        while (_stack.Count > MaxDepth)
        {
            // Stack<T> 不支持随机删除，转 list 重建
            var temp = _stack.ToList();
            temp.RemoveAt(temp.Count - 1);
            _stack.Clear();
            foreach (var item in temp.AsEnumerable().Reverse()) _stack.Push(item);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public UndoEntry? Undo()
    {
        if (_stack.Count == 0) return null;
        var entry = _stack.Pop();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public void Clear()
    {
        if (_stack.Count == 0) return;
        _stack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
