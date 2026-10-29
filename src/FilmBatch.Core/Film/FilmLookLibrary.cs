using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FilmBatch.Core.Film;

/// <summary>一卷內附底片：key 存進設定（film.look），Source 是 HaldCLUT.zip 內的來源路徑（工具與文件用）。</summary>
public sealed record FilmLook(string Key, string Name, string Group, string Source);

/// <summary>啟用的使用者 transform 無法讀取或驗證；呼叫端應回報失敗，不得靜默略過。</summary>
public sealed class ColorTransformAssetException(string key, Exception inner)
    : Exception($"無法載入色彩轉換資產 {key}：{inner.Message}", inner)
{
    public string Key { get; } = key;
}

/// <summary>內附底片清單與 LUT（嵌入資源 FilmBatch.Looks/*；LUT 第一次使用時載入並快取）。</summary>
public static class FilmLookLibrary
{
    public const string DefaultKey = "kodak-portra-400";
    public static IReadOnlyList<string> GroupsInOrder { get; } = ["kodak", "fuji", "agfa-lomo", "polaroid", "bw", "creative"];

    private static readonly ConcurrentDictionary<string, FilmLut?> Luts = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<FilmLut, FilmLookAsset> LutAssets = new();
    private static readonly ConditionalWeakTable<ColorTransformAsset, FilmLookAsset> TransformAssets = new();
    private static readonly Dictionary<string, FilmLook> ByKey;

    public static IReadOnlyList<FilmLook> All { get; }

    static FilmLookLibrary()
    {
        using var stream = Open("looks.json") ?? throw new InvalidOperationException("缺少內嵌的 looks.json。");
        // 用 JsonDocument 手動讀（Core 為 IsTrimmable，反射式 Deserialize 會觸發 IL2026）。
        using var doc = JsonDocument.Parse(stream);
        All = doc.RootElement.GetProperty("looks").EnumerateArray()
            .Select(l => new FilmLook(Str(l, "key"), Str(l, "name"), Str(l, "group"), Str(l, "source")))
            .ToArray();
        ByKey = All.ToDictionary(l => l.Key, StringComparer.Ordinal);
    }

    public static bool TryGet(string key, out FilmLook look) => ByKey.TryGetValue(key, out look!);

    /// <summary>舊／未知 key 不存在時回傳 null；新 transform 失敗時保留原因並明確回報。</summary>
    public static FilmLookAsset? Resolve(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (UserTransformLibrary.IsLookKey(key))
        {
            try
            {
                var asset = UserTransformLibrary.Resolve(AppPaths.UserLuts, key, cancellationToken);
                return TransformAssets.GetValue(asset, static a => FilmLookAsset.FromTransform(a));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new ColorTransformAssetException(key, ex);
            }
        }
        if (UserLutLibrary.TryGet(key, out var imported))
            return LutAssets.GetValue(imported!, static lut => FilmLookAsset.FromLut(lut));
        var builtIn = !ByKey.ContainsKey(key) ? null : Luts.GetOrAdd(key, static k =>
        {
            using var stream = Open(k + ".fblut");
            return stream is null ? null : FilmLutFile.Read(stream);
        });
        return builtIn is null ? null : LutAssets.GetValue(builtIn, static lut => FilmLookAsset.FromLut(lut));
    }

    private static Stream? Open(string name) =>
        typeof(FilmLookLibrary).Assembly.GetManifestResourceStream("FilmBatch.Looks/" + name);

    private static string Str(JsonElement e, string name) =>
        e.GetProperty(name).GetString() ?? throw new InvalidDataException($"looks.json 的 {name} 不可為 null。");
}
