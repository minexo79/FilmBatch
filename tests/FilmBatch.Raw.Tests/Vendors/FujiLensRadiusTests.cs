// Fuji 九 knot 表格的半徑讀法迴歸：knot 已是半對角線單位，角落為 1（上游 raf_lens.rs v0.13.0 讀法）。
// 期望以 X-T4 公開表格常數手算（knot 命中／線性內插）與上游已發表的自家 JPEG 量測（自由徑向擬合常數，
// 非本地實拍執行）為準；不斷言係數陣列本身。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Vendors.Fujifilm;

namespace FilmBatch.Raw.Tests.Vendors;

public sealed class FujiLensRadiusTests
{
    private static double WarpSource(double[] k, double r) => r * (k[0] + r * r * (k[1] + r * r * (k[2] + r * r * k[3])));

    private static double VignetteGain(double[] k, double r)
    {
        var g = 1.0;
        var x = r * r;
        var p = x;
        for (var i = 0; i < k.Length; i++, p *= x) g += k[i] * p;
        return g;
    }

    private static (double[] Warp, double[] Gain) Xt4()
    {
        var ops = FujiLens.Corrections("X-T4", FujiLensTests.Xt4Ifd(), FujiLensTests.Xt4Area);
        Assert.Equal(2, ops.Count);
        var warp = Assert.IsType<WarpRectilinear>(ops[0]);
        var vig = Assert.IsType<FixVignetteRadial>(ops[1]);
        Assert.Single(warp.Planes);
        Assert.Equal(new[] { 0.5, 0.5 }, warp.Center);
        Assert.Equal(new[] { 0.5, 0.5 }, vig.Center);
        return (warp.Planes[0], vig.K);
    }

    [Fact]
    public void VignetteGainIsReadAtPlainRadius()
    {
        var (_, k) = Xt4();
        // r = 0.5 正中第二 knot（96.87792969 %）；角落 67.45703125 %，軸上錨定 100 %。
        var atHalf = 100.0 / 96.87792969;
        Assert.InRange(VignetteGain(k, 0.5), atHalf - 0.01, atHalf + 0.01);
        // r = 0.85 在第五、六 knot 間線性內插（舊讀法從 6 % 外側取值，增益差約 0.069）。
        var v = 84.73291016 + (78.43701172 - 84.73291016) * (0.85 - 0.7908450704) / (0.8661971831 - 0.7908450704);
        var atMid = 100.0 / v;
        Assert.InRange(VignetteGain(k, 0.85), atMid - 0.01, atMid + 0.01);
        var atCorner = 100.0 / 67.45703125;
        Assert.InRange(VignetteGain(k, 1.0), atCorner - 0.01, atCorner + 0.01);
        Assert.InRange(VignetteGain(k, 0.0), 1.0 - 1e-12, 1.0 + 1e-12);
    }

    [Fact]
    public void WarpCornerMatchesPlainRadiusReading()
    {
        var (w, _) = Xt4();
        // 桶形畸變：zoom 填框（k0 約 1.03），角落取樣在 raw 內（-6.4 % bracket；舊讀法角落約 0.9668）。
        Assert.InRange(w[0], 1.02, 1.04);
        Assert.InRange(WarpSource(w, 1.0), 0.9641 - 0.002, 0.9641 + 0.002);
    }

    [Fact]
    public void WarpMatchesMeasuredXt4Geometry()
    {
        // 上游已發表的量測（pixls 3913 自家 JPEG 對未校正渲染的自由徑向擬合），非本地實拍執行：
        // source = s·r·(1 + k1·r² + k2·r⁴ + k3·r⁶)，r 為半對角線單位。該擬合不含表格／zoom 模型。
        var (w, _) = Xt4();
        double[] measured = [1.0299688, -0.1024252, 0.0195257, 0.0183398];
        for (var i = 2; i <= 20; i++)
        {
            var r = i / 20.0;
            var r2 = r * r;
            var reference = measured[0] * r * (1.0 + measured[1] * r2 + measured[2] * r2 * r2 + measured[3] * r2 * r2 * r2);
            Assert.InRange(WarpSource(w, r), reference - 0.0012, reference + 0.0012);
        }
    }

    [Fact]
    public void ValidatedModelsCorrectAndOthersStayUncorrected()
    {
        // 合格門檻未變（與上游 VALIDATED 名單逐項一致）：期望寫死輸入字串，不讀產品的 Validated 名單。
        // 已驗證機身產 warp＋vignette（含尾端空白的 Trim；X-Pro3 為另一已驗證機身代表）。
        foreach (var model in new[] { "X-T4", "X-T4 ", "X-Pro3" })
            Assert.Equal(2, FujiLens.Corrections(model, FujiLensTests.Xt4Ifd(), FujiLensTests.Xt4Area).Count);
        // 未驗證／近似名稱（大小寫、GFX 100S 與 GFX100S、空字串）一律不校正。
        foreach (var model in new[] { "", "X-T6", "FinePix S1", "FinePix F550EXR", "X10", "XF1", "XQ1", "GFX 100S", "x-t4" })
            Assert.Empty(FujiLens.Corrections(model, FujiLensTests.Xt4Ifd(), FujiLensTests.Xt4Area));
        Assert.Empty(FujiLens.Corrections("X-T4", FujiLensTests.Xt4Ifd(), new PixelRect(0, 0, 1, 1)));
    }
}
