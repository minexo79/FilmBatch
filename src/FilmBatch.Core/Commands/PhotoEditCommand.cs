using FilmBatch.Core.Projects;

namespace FilmBatch.Core.Commands;

/// <summary>
/// 修改一或多張照片設定的命令：保存每張照片修改前後的 <see cref="PhotoEntry"/>（不可變），
/// 因此所有照片操作都能以同一種命令復原。
/// </summary>
public sealed class PhotoEditCommand : IMergeableCommand
{
    private readonly Project _project;
    private readonly Dictionary<string, PhotoEntry> _before;
    private Dictionary<string, PhotoEntry> _after;

    public string Description { get; }
    public string? MergeKey { get; }
    public IReadOnlyCollection<string> FileNames => _after.Keys;

    public PhotoEditCommand(Project project, string description, IReadOnlyDictionary<string, PhotoEntry> after, string? mergeKey = null)
    {
        _project = project;
        Description = description;
        MergeKey = mergeKey;
        _after = new(after, StringComparer.Ordinal);
        _before = after.Keys.ToDictionary(k => k, k => project[k], StringComparer.Ordinal);
    }

    /// <summary>沒有任何照片實際改變。</summary>
    public bool IsNoOp => _after.All(kv => _before[kv.Key].Equals(kv.Value));

    public void Execute() => _project.Replace(_after);

    public void Undo() => _project.Replace(_before);

    public bool TryMerge(IUndoableCommand next)
    {
        if (next is not PhotoEditCommand n || n.MergeKey != MergeKey || !n._after.Keys.ToHashSet().SetEquals(_after.Keys))
            return false;
        _after = n._after;
        return true;
    }
}
