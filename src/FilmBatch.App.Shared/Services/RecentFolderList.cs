namespace FilmBatch.App.Services;

/// <summary>
/// 最近開啟的資料夾清單（「檔案 → 開啟...」子選單）：最新的在最前、以完整路徑去重
/// （Windows 不分大小寫）、最多 <see cref="MaxCount"/> 筆。只操作傳入的清單，不負責儲存。
/// </summary>
public static class RecentFolderList
{
    public const int MaxCount = 10;

    /// <summary>比較資料夾路徑：Windows 檔案系統不分大小寫，其他平台逐字比較。</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>完整路徑並去掉結尾的分隔符號（C:\a\ 與 C:\a 視為同一個資料夾）。</summary>
    public static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>把資料夾移到最前面（已存在時先移除舊位置），超過上限時捨棄最舊的。</summary>
    public static void Add(List<string> list, string folder)
    {
        var path = Normalize(folder);
        list.RemoveAll(p => PathComparer.Equals(SafeNormalize(p), path));
        list.Insert(0, path);
        if (list.Count > MaxCount) list.RemoveRange(MaxCount, list.Count - MaxCount);
    }

    /// <summary>從清單移除資料夾（不論大小寫或結尾分隔符號差異）；有移除時傳回 true。</summary>
    public static bool Remove(List<string> list, string folder)
    {
        var path = Normalize(folder);
        return list.RemoveAll(p => PathComparer.Equals(SafeNormalize(p), path)) > 0;
    }

    /// <summary>
    /// 整理從設定檔讀入的清單：去掉空白與無效路徑、重複項目（保留較前面的）並套用上限。
    /// 清單為空時以舊版設定的 <paramref name="lastFolder"/> 當第一筆（<paramref name="exclude"/> 為不列入的資料夾，例如範例資料夾）。
    /// </summary>
    public static void Sanitize(List<string> list, string? lastFolder, string? exclude = null)
    {
        var excluded = exclude is null ? null : SafeNormalize(exclude);
        var seen = new HashSet<string>(PathComparer);
        var cleaned = new List<string>();
        foreach (var p in list)
        {
            if (string.IsNullOrWhiteSpace(p) || SafeNormalize(p) is not { } path) continue;
            if (excluded is not null && PathComparer.Equals(path, excluded)) continue;
            if (seen.Add(path)) cleaned.Add(path);
        }
        if (cleaned.Count == 0 && !string.IsNullOrWhiteSpace(lastFolder) && SafeNormalize(lastFolder) is { } last
            && (excluded is null || !PathComparer.Equals(last, excluded)))
            cleaned.Add(last);
        if (cleaned.Count > MaxCount) cleaned.RemoveRange(MaxCount, cleaned.Count - MaxCount);
        list.Clear();
        list.AddRange(cleaned);
    }

    private static string? SafeNormalize(string folder)
    {
        try { return Normalize(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
