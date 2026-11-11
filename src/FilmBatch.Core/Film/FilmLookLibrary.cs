using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace FilmBatch.Core.Film;

/// <summary>一式內附底片：key 存進設定（film.look）；Index 為組內序號 1–9（列優先：濃淡 × 過渡）。</summary>
public sealed record FilmLook(string Key, string Name, string Group, int Index);

/// <summary>啟用的使用者 transform 無法讀取或驗證；呼叫端應回報失敗，不得靜默略過。</summary>
public sealed class ColorTransformAssetException(string key, Exception inner)
    : Exception($"無法載入色彩轉換資產 {key}：{inner.Message}", inner)
{
    public string Key { get; } = key;
}

/// <summary>內附底片清單與 LUT（六個色帶 × 九式，載入時由 <see cref="ToningLooks"/> 烘焙並快取）。</summary>
public static class FilmLookLibrary
{
    public const string DefaultKey = "amber-5";
    public static IReadOnlyList<string> GroupsInOrder { get; } = ["aqua", "amber", "rose", "aqua-amber", "teal-rose", "mono"];

    private static readonly ToningBand[] Bands =
        [ToningBand.Aqua, ToningBand.Amber, ToningBand.Rose, ToningBand.AquaAmber, ToningBand.TealRose, ToningBand.Mono];
    private static readonly string[] GroupNames = ["湖藍", "暖黃", "豆沙紅", "湖藍→暖黃", "青綠→豆沙紅", "黑白"];

    private static readonly ConcurrentDictionary<string, FilmLut> Luts = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<FilmLut, FilmLookAsset> LutAssets = new();
    private static readonly ConditionalWeakTable<ColorTransformAsset, FilmLookAsset> TransformAssets = new();
    private static readonly Dictionary<string, FilmLook> ByKey;
    private static readonly Dictionary<string, (ToningBand Band, int Row, int Column)> Specs;

    public static IReadOnlyList<FilmLook> All { get; }

    /// <summary>舊版 36 卷 RawTherapee 底片 key → 最接近的新式。讀入舊專案與預設時自動換成新 key。</summary>
    public static IReadOnlyDictionary<string, string> LegacyKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["kodak-portra-400"] = "amber-5", ["kodak-portra-160"] = "amber-2", ["kodak-portra-400-vc"] = "amber-6",
        ["kodak-portra-800"] = "amber-8", ["kodak-ektar-100"] = "amber-9", ["kodak-kodachrome-64"] = "amber-3",
        ["kodak-ektachrome-100vs"] = "aqua-5", ["kodak-elite-chrome-200"] = "aqua-4", ["kodak-elite-color-400"] = "amber-4",
        ["fuji-velvia-50"] = "teal-rose-9", ["fuji-provia-100f"] = "aqua-2", ["fuji-astia-100f"] = "amber-1",
        ["fuji-superia-400"] = "aqua-5", ["fuji-pro-400h"] = "aqua-amber-2", ["fuji-pro-160c"] = "aqua-2",
        ["fuji-superia-reala-100"] = "teal-rose-2", ["fuji-superia-xtra-800"] = "teal-rose-5",
        ["agfa-vista-200"] = "amber-7", ["agfa-ultra-100"] = "teal-rose-8", ["lomo-xpro-200"] = "aqua-amber-9",
        ["lomo-redscale-100"] = "amber-8", ["polaroid-690"] = "teal-rose-4", ["polaroid-px70"] = "aqua-amber-4",
        ["polaroid-polachrome"] = "aqua-amber-5", ["kodak-tri-x-400"] = "mono-9", ["kodak-tmax-100"] = "mono-5",
        ["ilford-hp5-400"] = "mono-8", ["ilford-delta-100"] = "mono-4", ["fuji-acros-100"] = "mono-6",
        ["agfa-apx-100"] = "mono-2", ["creative-teal-orange"] = "teal-rose-6", ["creative-bleach-bypass"] = "mono-3",
        ["creative-fall-colors"] = "amber-3", ["creative-soft-warming"] = "amber-1", ["creative-crisp-winter"] = "aqua-3",
        ["creative-moonlight"] = "aqua-7",
    };

    static FilmLookLibrary()
    {
        All = [.. GroupsInOrder.SelectMany((group, g) => Enumerable.Range(1, 9)
            .Select(i => new FilmLook($"{group}-{i}", $"{GroupNames[g]} {i}", group, i)))];
        ByKey = All.ToDictionary(l => l.Key, StringComparer.Ordinal);
        Specs = All.ToDictionary(l => l.Key,
            l => (Bands[GroupsInOrder.ToList().IndexOf(l.Group)], (l.Index - 1) / 3, (l.Index - 1) % 3), StringComparer.Ordinal);
    }

    public static bool TryGet(string key, out FilmLook look) => ByKey.TryGetValue(key, out look!);

    /// <summary>
    /// 切換到此底片時建議的效果比例：黑白與自訂 LUT（使用者匯入的 LUT、CLF）為 100，其餘色帶為 50。
    /// 舊 key 先遷移再判斷。
    /// </summary>
    public static double DefaultStrength(string key)
    {
        if (TryMigrate(key, out var migrated)) key = migrated;
        return TryGet(key, out var look) && look.Group != "mono" ? 50 : 100;
    }

    /// <summary>
    /// 舊 36 卷 key 換成新 key；開發期間曾有、之後移除或改名的組也換成新 key：單色「青綠」（<c>teal-1</c>–<c>teal-9</c>）
    /// 換成同序號的湖藍，「青綠→暖黃」（<c>teal-amber-1</c>–<c>teal-amber-9</c>）換成同序號的青綠→豆沙紅。
    /// 新 key 或未知 key 回傳 false。
    /// </summary>
    public static bool TryMigrate(string key, out string migrated)
    {
        if (key.Length == 6 && key.StartsWith("teal-", StringComparison.Ordinal) && key[5] is >= '1' and <= '9')
        {
            migrated = "aqua-" + key[5];
            return true;
        }
        if (key.Length == 12 && key.StartsWith("teal-amber-", StringComparison.Ordinal) && key[11] is >= '1' and <= '9')
        {
            migrated = "teal-rose-" + key[11];
            return true;
        }
        return LegacyKeys.TryGetValue(key, out migrated!);
    }

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
        if (TryMigrate(key, out var migrated)) key = migrated;
        var builtIn = !Specs.TryGetValue(key, out var spec) ? null
            : Luts.GetOrAdd(key, _ => ToningLooks.Bake(spec.Band, spec.Row, spec.Column));
        return builtIn is null ? null : LutAssets.GetValue(builtIn, static lut => FilmLookAsset.FromLut(lut));
    }
}
