namespace FilmBatch.Core.Commands;

public interface IUndoableCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}

/// <summary>可與後續同類命令合併（例如連續的滑桿拖曳合併成一筆復原紀錄）。</summary>
public interface IMergeableCommand : IUndoableCommand
{
    string? MergeKey { get; }
    /// <summary>把 <paramref name="next"/> 併入本命令；回傳 false 表示無法合併。</summary>
    bool TryMerge(IUndoableCommand next);
}

/// <summary>專案層級的復原堆疊。</summary>
public sealed class CommandStack(int limit = 200)
{
    private readonly LinkedList<IUndoableCommand> _undo = new();
    private readonly Stack<IUndoableCommand> _redo = new();
    private string? _openMergeKey;

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Last?.Value.Description;
    public string? RedoDescription => _redo.TryPeek(out var c) ? c.Description : null;

    public void Execute(IUndoableCommand command)
    {
        command.Execute();
        _redo.Clear();

        if (command is IMergeableCommand { MergeKey: { } key }
            && key == _openMergeKey
            && _undo.Last?.Value is IMergeableCommand top
            && top.MergeKey == key
            && top.TryMerge(command))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _openMergeKey = (command as IMergeableCommand)?.MergeKey;
        _undo.AddLast(command);
        if (_undo.Count > limit) _undo.RemoveFirst();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>結束合併（例如放開滑桿）：之後的命令不再併入目前這筆。</summary>
    public void CloseMerge() => _openMergeKey = null;

    public void Undo()
    {
        if (_undo.Last is not { } node) return;
        node.Value.Undo();
        _undo.RemoveLast();
        _redo.Push(node.Value);
        _openMergeKey = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!_redo.TryPeek(out var cmd)) return;
        cmd.Execute();
        _redo.Pop();
        _undo.AddLast(cmd);
        _openMergeKey = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _openMergeKey = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>以委派實作的一般命令（預設庫操作等）。</summary>
public sealed class DelegateCommand(string description, Action execute, Action undo) : IUndoableCommand
{
    public string Description => description;
    public void Execute() => execute();
    public void Undo() => undo();
}

/// <summary>多個命令組成一筆復原紀錄。</summary>
public sealed class CompositeCommand(string description, IReadOnlyList<IUndoableCommand> commands) : IUndoableCommand
{
    public string Description => description;
    public void Execute()
    {
        foreach (var c in commands) c.Execute();
    }
    public void Undo()
    {
        for (var i = commands.Count - 1; i >= 0; i--) commands[i].Undo();
    }
}
