namespace FilmBatch.Core.Film;

/// <summary>
/// 同一個 <c>film.look</c> 選擇的資產容器（CT-2 §3.1）：legacy <see cref="FilmLut"/>
/// 或 <see cref="ColorTransformAsset"/> 二者擇一，不可同時存在；也不允許兩者皆空。
/// renderer 依 <see cref="ColorTransformAsset.Options"/> 的 stage 決定 transform 在哪個固定 hook 執行；
/// legacy 路徑的 LUT 語義與 key 行為不變。
/// </summary>
public sealed class FilmLookAsset
{
    private FilmLookAsset(FilmLut? lut, ColorTransformAsset? transform)
    {
        Lut = lut;
        Transform = transform;
    }

    /// <summary>legacy 3D LUT 資產；transform 選擇時為 null。</summary>
    public FilmLut? Lut { get; }

    /// <summary>新 transform 資產（含 stage／input／output 宣告）；legacy 選擇時為 null。</summary>
    public ColorTransformAsset? Transform { get; }

    /// <summary>包一個已驗證的 legacy LUT。</summary>
    public static FilmLookAsset FromLut(FilmLut lut)
    {
        ArgumentNullException.ThrowIfNull(lut);
        return new FilmLookAsset(lut, null);
    }

    /// <summary>包一個已驗證的 transform 資產。</summary>
    public static FilmLookAsset FromTransform(ColorTransformAsset transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return new FilmLookAsset(null, transform);
    }
}
