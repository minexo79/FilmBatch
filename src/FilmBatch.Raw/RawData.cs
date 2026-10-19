namespace FilmBatch.Raw;

/// <summary>
/// 黑階模型（DNG <c>BlackLevelRepeatDim</c>、<c>BlackLevel</c>、<c>BlackLevelDeltaH/V</c>）。
/// 重複排列與 delta 都錨定在有效區域的左上角。對應 lightcraft-raw 的 <c>BlackLevel</c>。
/// </summary>
public sealed class BlackLevel
{
    public int RepeatRows { get; init; } = 1;

    public int RepeatCols { get; init; } = 1;

    /// <summary><c>RepeatRows × RepeatCols × cpp</c> 個值。</summary>
    public float[] Values { get; init; } = [0f];

    /// <summary>每個有效區域欄的增量。</summary>
    public float[] DeltaH { get; init; } = [];

    /// <summary>每個有效區域列的增量。</summary>
    public float[] DeltaV { get; init; } = [];

    public static BlackLevel Uniform(float v) => new() { Values = [v] };

    /// <summary>有效區域座標 (x, y)、<paramref name="cpp"/> 個樣本中第 <paramref name="s"/> 個的黑階。</summary>
    public float At(int x, int y, int s, int cpp)
    {
        int r = Math.Max(RepeatRows, 1), c = Math.Max(RepeatCols, 1);
        var idx = ((y % r) * c + (x % c)) * cpp + s;
        var b = idx < Values.Length ? Values[idx] : Values.Length > 0 ? Values[0] : 0f;
        var dh = x < DeltaH.Length ? DeltaH[x] : 0f;
        var dv = y < DeltaV.Length ? DeltaV[y] : 0f;
        return b + dh + dv;
    }

    /// <summary>重複排列值的平均（依序以 float 累加，與 lightcraft 相同）。</summary>
    public float Mean()
    {
        if (Values.Length == 0) return 0f;
        var sum = -0f;
        foreach (var v in Values) sum += v;
        return sum / Values.Length;
    }
}

/// <summary>感光元件樣本：一般為 16-bit 整數；浮點 DNG（已線性化、白階通常為 1.0）為 float。</summary>
public abstract class RawData
{
    private RawData()
    {
    }

    public abstract int Length { get; }

    /// <summary>第 <paramref name="i"/> 個樣本的值。</summary>
    public abstract float Get(int i);

    public sealed class U16(ushort[] samples) : RawData
    {
        public ushort[] Samples { get; } = samples;

        public override int Length => Samples.Length;

        public override float Get(int i) => Samples[i];
    }

    public sealed class F32(float[] samples) : RawData
    {
        public float[] Samples { get; } = samples;

        public override int Length => Samples.Length;

        public override float Get(int i) => Samples[i];
    }
}

/// <summary><c>RawDecoder</c> 辨識出的 RAW 容器格式（lightcraft-raw 的 <c>RawFormat</c>）。</summary>
public enum RawFormat
{
    Dng,
    Cr2,
    Cr3,
    Nef,
    Nrw,
    Arw,
    Raf,
    Orf,
    Rw2,
    Pef,
    Srw,
    /// <summary>Canon CRW（CIFF heap），CR2 之前的機身。</summary>
    Crw,
    /// <summary>Minolta MRW（<c>\0MRM</c> 區塊容器）。</summary>
    Mrw,
    /// <summary>Sigma／Foveon X3F（<c>FOVb</c>）。</summary>
    X3f,
    /// <summary>沒有 DNG 版本標籤、但以 DNG 方式描述自己的 TIFF RAW（Hasselblad 3FR、FFF）；由 DNG 讀取器處理。</summary>
    CfaTiff,
    /// <summary>Kodak DCS520C TIFF。</summary>
    KodakDcs,
    /// <summary>Epson ERF。</summary>
    Erf,
    /// <summary>Mamiya MEF。</summary>
    Mef,
    /// <summary>其他 TIFF 型 RAW（3FR、IIQ、KDC、DCR、MOS…）。</summary>
    OtherTiff,
}

/// <summary>解碼範圍：完整（檔頭＋樣本），或只讀檔頭（樣本為空，由樣本量測的黑白階只是佔位值）。</summary>
internal enum DecodeMode
{
    Full,
    Header,
}
