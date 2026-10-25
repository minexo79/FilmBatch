// 移植自 LightCraft crates/raw/src/lib.rs（normalized、develop、finish_demosaiced）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw;

public sealed partial class RawImage
{
    /// <summary>
    /// 扣黑階、以白階正規化並裁到有效區域：先套用 OpcodeList1（對原始值），再正規化，最後 OpcodeList2
    /// （對正規化值）。1.0 = 白階；NaN／無限的樣本當作黑（0.0），讓去馬賽克與之後各階段只見到數字。
    /// </summary>
    public Normalized Normalized()
    {
        Validate();
        var (w, cpp) = (Width, Cpp);
        var a = ActiveArea;
        // 階段 1（完整影像）運算碼只在存在時才需要可變的 float 複本
        float[]? stage1 = null;
        if (Opcodes.List1.Count != 0)
        {
            stage1 = new float[Data.Length];
            for (var i = 0; i < stage1.Length; i++) stage1[i] = Data.Get(i);
            OpcodeOps.ApplyList(Opcodes.List1, stage1, Width, Height, cpp, Cfa, 65535f);
        }
        var scale = new float[cpp];
        for (var s = 0; s < cpp; s++)
        {
            var range = WhiteAt(s) - Black.Mean();
            scale[s] = range > 0f ? 1f / range : 1f;
        }
        var outp = new float[checked((long)a.Width * a.Height * cpp)];
        var rowLen = a.Width * cpp;
        Parallel.For(0, a.Height, RawParallel.Options, y =>
        {
            var rowBase = y * rowLen;
            var baseIdx = ((long)(a.Y + y) * w + a.X) * cpp;
            for (var x = 0; x < a.Width; x++)
            {
                for (var s = 0; s < cpp; s++)
                {
                    var i = (int)(baseIdx + x * cpp + s);
                    var v = stage1 is { } v1 ? v1[i] : Data.Get(i);
                    var b = Black.At(x, y, s, cpp);
                    var n = (v - b) * scale[s];
                    outp[rowBase + x * cpp + s] = float.IsFinite(n) ? n : 0f;
                }
            }
        });
        var cfa = Cfa?.Shifted(a.X, a.Y);
        OpcodeOps.ApplyList(Opcodes.List2, outp, a.Width, a.Height, cpp, cfa, 1f);
        return new Normalized { Width = a.Width, Height = a.Height, Cpp = cpp, Data = outp, Cfa = cfa };
    }

    /// <summary>完整的「RAW → 相機 RGB」：正規化、以 <paramref name="method"/> 去馬賽克、套用 OpcodeList3、預設裁切。</summary>
    public RgbImage Develop(DemosaicMethod method) => FinishDemosaiced(Demosaic.Run(Normalized(), method));

    /// <summary>
    /// <see cref="Develop"/> 的最後幾步：對用其他方式從 <see cref="Normalized"/> 去馬賽克得到的相機 RGB
    /// 套用 OpcodeList3，再做預設裁切（與像素高寬比的列數拉伸）。
    /// </summary>
    public RgbImage FinishDemosaiced(RgbImage rgb)
    {
        rgb = OpcodeOps.ApplyList3(Opcodes.List3, rgb);
        var c = DevelopCrop(rgb.Width, rgb.Height);
        if (c.X != 0 || c.Y != 0 || c.Width != rgb.Width || c.Height != rgb.Height)
            rgb = rgb.Crop(c.X, c.Y, c.Width, c.Height);
        var h = StretchedHeight(rgb.Height, PixelAspect);
        return h == rgb.Height ? rgb : Resample.Resize(rgb, rgb.Width, h, ResampleFilter.Mitchell);
    }
}
