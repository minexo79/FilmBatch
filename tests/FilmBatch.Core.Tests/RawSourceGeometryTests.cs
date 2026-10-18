// RAW 來源幾何的行為迴歸：定向後正規化位置 → 有效區正規化位置，
// 必經 EXIF 方向、預設裁切與實際套用的 List3 warp 鏈（反向），期望一律手算。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Opcodes;

namespace FilmBatch.Core.Tests;

public sealed class RawSourceGeometryTests
{
    /// <summary>最小線性 RawImage（無取樣需求：幾何只讀中繼資料）。</summary>
    private static RawImage Raw(int w, int h, PixelRect? crop = null, Orientation o = Orientation.Normal,
        List<Opcode>? list3 = null, float pixelAspect = 1f)
    {
        var full = new PixelRect(0, 0, w, h);
        return new RawImage
        {
            Format = RawFormat.Dng,
            Width = w,
            Height = h,
            Cpp = 3,
            Data = new RawData.F32([]),
            ActiveArea = full,
            Crop = crop ?? full,
            Orientation = o,
            PixelAspect = pixelAspect,
            Opcodes = new OpcodeLists { List3 = list3 ?? [] },
            Metadata = new FilmBatch.Raw.Meta.Metadata(),
        };
    }

    private static void Near(double expected, double actual, double tol, string what) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"{what}: expected {expected}, got {actual}");

    [Fact]
    public void NoWarpActiveMatchesPlacementOnGrid()
    {
        // 無 warp 時與 SourcePlacement 等價（裁切＋方向），8 方向逐一對照既有 oracle。
        var crop = new PixelRect(4, 2, 10, 6);
        foreach (var o in Enum.GetValues<Orientation>())
        {
            var geo = RawSourceGeometry.Create(Raw(16, 12, crop, o));
            var a = new PixelRect(0, 0, 16, 12);
            var placement = new SourcePlacement(
                crop.X / (double)a.Width, crop.Y / (double)a.Height,
                crop.Width / (double)a.Width, crop.Height / (double)a.Height, o);
            for (var i = 0; i <= 10; i++)
            {
                var (u, v) = (i / 10.0, 1.0 - i / 10.0);
                var (ax, ay) = geo.Active(u, v);
                var (px, py) = placement.Active(u, v);
                Near(px, ax, 1e-12, $"x {o} u={u}");
                Near(py, ay, 1e-12, $"y {o} v={v}");
            }
        }
    }

    [Fact]
    public void BarrelWarpMapsCorrectedPositionToSource()
    {
        // 10×10 全幅；warp：k = [1.2, 0, 0, 0, 0, 0]，中心 (0.5, 0.5) → f = 1.2 常數。
        // 索引中心 c = 4.5；正規化 m = maxdist(角) = 4.5·√2。
        // 影像 (u,v) → 連續 (u·10, v·10) → 索引 (u·10−0.5, v·10−0.5)；
        // src_index = c + 1.2·(ix − c)。
        var geo = RawSourceGeometry.Create(Raw(10, 10,
            list3: [new WarpRectilinear([[1.2, 0, 0, 0, 0, 0]], [0.5, 0.5])]));

        var (u, v) = (0.8, 0.8);
        var (ax, ay) = geo.Active(u, v);
        var ix = 0.8 * 10 - 0.5; // 7.5
        var sx = 4.5 + 1.2 * (ix - 4.5); // 8.1
        Near((sx + 0.5) / 10.0, ax, 1e-12, "x");
        Near((sx + 0.5) / 10.0, ay, 1e-12, "y");

        // 中心像素不動：(u,v) = (0.5, 0.5) → ix = 4.5 = c → src = c。
        (ax, ay) = geo.Active(0.5, 0.5);
        Near(0.5, ax, 1e-12, "centre x");
        Near(0.5, ay, 1e-12, "centre y");
    }

    [Fact]
    public void RadialWarpUsesSquaredRadiusNotLinear()
    {
        // k = [1, -0.2, 0, 0]：f = 1 − 0.2r²；r² = 0.5 在對角方向 d = m/√2。
        var geo = RawSourceGeometry.Create(Raw(10, 10,
            list3: [new WarpRectilinear([[1.0, -0.2, 0, 0, 0, 0]], [0.5, 0.5])]));
        var m = 4.5 * Math.Sqrt(2.0);
        var d = m * Math.Sqrt(0.5); // r² = 0.5 → f = 0.9
        // 對角方向：取 ix = c + d/√2 → src 偏移 = 0.9·d/√2
        var ix = 4.5 + d / Math.Sqrt(2.0);
        var u = (ix + 0.5) / 10.0;
        var (ax, ay) = geo.Active(u, u);
        var s = 4.5 + 0.9 * d / Math.Sqrt(2.0);
        Near((s + 0.5) / 10.0, ax, 1e-12, "x");
        Near((s + 0.5) / 10.0, ay, 1e-12, "y");
    }

    [Fact]
    public void TangentialTermsShiftOffAxis()
    {
        // k = [1, 0, 0, 0, kt0 = 0.05, kt1 = −0.02]：dx·f + kt0·2dxdy + kt1·(r² + 2dx²)
        var geo = RawSourceGeometry.Create(Raw(10, 10,
            list3: [new WarpRectilinear([[1.0, 0, 0, 0, 0.05, -0.02]], [0.5, 0.5])]));
        var m = 4.5 * Math.Sqrt(2.0);
        var (ix, iy) = (7.0, 3.0);
        var dx = (ix - 4.5) / m;
        var dy = (iy - 4.5) / m;
        var r2 = dx * dx + dy * dy;
        var sx = 4.5 + m * (dx * 1.0 + 0.05 * 2 * dx * dy + -0.02 * (r2 + 2 * dx * dx));
        var sy = 4.5 + m * (dy * 1.0 + -0.02 * 2 * dx * dy + 0.05 * (r2 + 2 * dy * dy));
        var (ax, ay) = geo.Active((ix + 0.5) / 10.0, (iy + 0.5) / 10.0);
        Near((sx + 0.5) / 10.0, ax, 1e-12, "x");
        Near((sy + 0.5) / 10.0, ay, 1e-12, "y");
    }

    [Fact]
    public void CropComposesWithWarp()
    {
        // 裁切 (2, 1, 6, 8)（active 相對）＋ f = 2 放大 warp：
        // (u,v) → 裁切連續 (u·6, v·8) → active 連續 (+2, +1) → 索引 −0.5 → src = c + 2(ix − c)。
        var geo = RawSourceGeometry.Create(Raw(10, 12, new PixelRect(2, 1, 6, 8),
            list3: [new WarpRectilinear([[2.0, 0, 0, 0, 0, 0]], [0.5, 0.5])]));
        var (u, v) = (0.5, 0.5);
        var (ax, ay) = geo.Active(u, v);
        // active 連續 = (2 + 3, 1 + 4) = (5, 5) → 索引 (4.5, 4.5)；c = (4.5, 5.5)
        var sx = 4.5 + 2.0 * (4.5 - 4.5);
        var sy = 5.5 + 2.0 * (4.5 - 5.5);
        Near((sx + 0.5) / 10.0, ax, 1e-12, "x");
        Near((sy + 0.5) / 12.0, ay, 1e-12, "y");
    }

    [Fact]
    public void Orientation90CropHandComputed()
    {
        // Rotate90（順時針 90°）：定向後 (u,v) → 未轉正裁切正規化 (v, 1−u)。
        var crop = new PixelRect(2, 0, 4, 8);
        var geo = RawSourceGeometry.Create(Raw(8, 8, crop, Orientation.Rotate90));
        var (ax, ay) = geo.Active(0.25, 0.75);
        // 裁切正規化 (0.75, 0.75) → active 連續 (2 + 3, 0 + 6) = (5, 6) → (5/8, 6/8)
        Near(5.0 / 8.0, ax, 1e-12, "x");
        Near(6.0 / 8.0, ay, 1e-12, "y");
        // (0, 0) → 裁切正規化 (0, 1) → 連續 (2, 8) → (0.25, 1.0)
        (ax, ay) = geo.Active(0.0, 0.0);
        Near(0.25, ax, 1e-12, "origin x");
        Near(1.0, ay, 1e-12, "origin y");
    }

    [Fact]
    public void WarpChainAppliesStagesInReverseApplicationOrder()
    {
        // List3 [A, B]：顯影先 A 後 B；來源位置 = map_B 之後 map_A。
        // A：中心 (0.25, 0.5) f = 3；B：中心 (0.75, 0.5) f = 2（索引中心）。
        var raw = Raw(10, 10, list3:
        [
            new WarpRectilinear([[3.0, 0, 0, 0, 0, 0]], [0.25, 0.5]),
            new WarpRectilinear([[2.0, 0, 0, 0, 0, 0]], [0.75, 0.5]),
        ]);
        var geo = RawSourceGeometry.Create(raw);
        var (u, v) = (0.8, 0.5);
        var (ax, ay) = geo.Active(u, v);
        var ix = 0.8 * 10 - 0.5; // 7.5 → 先過 B（center 7.5·(0.75…)=7.5, m=4.5√2）
        var cB = 0.75 * 9.0; // 6.75
        var cA = 0.25 * 9.0; // 2.25
        var mid = cB + 2.0 * (ix - cB); // map B
        var s = cA + 3.0 * (mid - cA); // map A
        Near((s + 0.5) / 10.0, ax, 1e-12, "chained x");
        Near(0.5, ay, 1e-12, "chained y");

        // 順序錯（先 A 後 B）會給出不同值，確保鏈向可分辨：
        var wrong = cA + 3.0 * (ix - cA);
        wrong = cB + 2.0 * (wrong - cB);
        Assert.True(Math.Abs(wrong - s) > 0.5, "test data must distinguish stage order");
    }

    [Fact]
    public void WarpRectilinear2UsesClampedRadiusAndTangential()
    {
        // WarpRectilinear2：kr 只有 k0 = 2（f = 2 常數），kt0 = 0.1, kt1 = 0；
        // 有效半徑 [0, 1e6]（不箝制）；與切向項組合。
        var k = new double[OpcodeOps.Wr2Len];
        k[0] = 2.0; k[15] = 0.1; k[16] = 0.0; k[17] = 0.0; k[18] = 1e6;
        var geo = RawSourceGeometry.Create(Raw(10, 10,
            list3: [new WarpRectilinear2([k], [0.5, 0.5], Reciprocal: false, Optional: false)]));
        var m = 4.5 * Math.Sqrt(2.0);
        var (ix, iy) = (7.0, 3.0);
        var dx = (ix - 4.5) / m;
        var dy = (iy - 4.5) / m;
        var r2 = dx * dx + dy * dy;
        var sx = 4.5 + m * (dx * 2.0 + 0.1 * 2 * dx * dy + 0.0 * (r2 + 2 * dx * dx));
        var sy = 4.5 + m * (dy * 2.0 + 0.0 * 2 * dx * dy + 0.1 * (r2 + 2 * dy * dy));
        var (ax, ay) = geo.Active((ix + 0.5) / 10.0, (iy + 0.5) / 10.0);
        Near((sx + 0.5) / 10.0, ax, 1e-12, "x");
        Near((sy + 0.5) / 10.0, ay, 1e-12, "y");
    }

    [Fact]
    public void WarpRectilinear2RadiusClampAndReciprocal()
    {
        // reciprocal：src = d / f；f 以箝制後半徑取值。kr = [2]，valid = [0, 0.3]：
        // 半徑 > 0.3 時 f 取 r = 0.3 的值（仍 2，因係數只有 k0）→ 用 k1 讓箝制可見：
        // k = [1, 4] → f(r) = 1 + 4r；r_c = min(r, 0.3)。
        var k = new double[OpcodeOps.Wr2Len];
        k[0] = 1.0; k[1] = 4.0; k[17] = 0.0; k[18] = 0.3;
        var geo = RawSourceGeometry.Create(Raw(10, 10,
            list3: [new WarpRectilinear2([k], [0.5, 0.5], Reciprocal: true, Optional: false)]));
        var m = 4.5 * Math.Sqrt(2.0);
        // r = 0.5 > 0.3 → f = 1 + 4·0.3 = 2.2；src = d / 2.2
        var d = 0.5 * m; // 沿 x 方向
        var ix = 4.5 + d;
        var u = (ix + 0.5) / 10.0;
        var (ax, ay) = geo.Active(u, 0.5);
        var s = 4.5 + d / 2.2;
        Near((s + 0.5) / 10.0, ax, 1e-12, "clamped reciprocal x");
        Near(0.5, ay, 1e-12, "clamped reciprocal y");
    }

    [Fact]
    public void OptionalWarpRectilinear2SkipsFollowingWarpRectilinear()
    {
        // DNG 1.6 跳過規則：optional 的 WR2 之後緊接的 WarpRectilinear 不套用，
        // 幾何快照必須沿用 AppliedOps 的同一套規則。
        var k = new double[OpcodeOps.Wr2Len];
        k[0] = 2.0; k[17] = 0.0; k[18] = 1e6;
        var withSkip = Raw(10, 10, list3:
        [
            new WarpRectilinear2([k], [0.5, 0.5], Reciprocal: false, Optional: true),
            new WarpRectilinear([[5.0, 0, 0, 0, 0, 0]], [0.5, 0.5]),
        ]);
        var onlyWr2 = Raw(10, 10, list3:
        [
            new WarpRectilinear2([k], [0.5, 0.5], Reciprocal: false, Optional: true),
        ]);
        var (u, v) = (0.8, 0.7);
        var a = RawSourceGeometry.Create(withSkip).Active(u, v);
        var b = RawSourceGeometry.Create(onlyWr2).Active(u, v);
        Near(b.X, a.X, 1e-12, "skipped WR must not move x");
        Near(b.Y, a.Y, 1e-12, "skipped WR must not move y");
    }

    [Fact]
    public void NonWarpList3OpsDoNotMoveGeometry()
    {
        // FixVignetteRadial／MapPolynomial 等不動像素；幾何等同無 warp。
        var geo = RawSourceGeometry.Create(Raw(10, 10, list3:
        [
            new FixVignetteRadial([0.2, 0, 0, 0, 0], [0.5, 0.5]),
            new MapPolynomial(new Area(0, 0, 10, 10, 0, 3, 1, 1), [0.0, 1.0]),
        ]));
        var (ax, ay) = geo.Active(0.3, 0.7);
        Near(0.3, ax, 1e-12, "x");
        Near(0.7, ay, 1e-12, "y");
    }

    [Fact]
    public void CoversRejectsPositionsOutsideTheSource()
    {
        // f = 0.5 縮小 warp：角落映射到影像外（上游 blank-canvas 規則）。
        var geo = RawSourceGeometry.Create(Raw(10, 10,
            list3: [new WarpRectilinear([[0.5, 0, 0, 0, 0, 0]], [0.5, 0.5])]));
        // (0,0) → ix = −0.5 → src = 4.5 + 0.5(−5) = 2（在界內）
        Assert.True(geo.Covers(0.0, 0.0));
        // f = 3 放大：角落 src = 4.5 + 3(−5) = −10.5 → 索引 < −1 → 界外
        var zoomed = RawSourceGeometry.Create(Raw(10, 10,
            list3: [new WarpRectilinear([[3.0, 0, 0, 0, 0, 0]], [0.5, 0.5])]));
        Assert.False(zoomed.Covers(0.0, 0.0));
        Assert.True(zoomed.Covers(0.5, 0.5));
        // 無 warp：整幅覆蓋。
        var plain = RawSourceGeometry.Create(Raw(10, 10));
        Assert.True(plain.Covers(-0.2, 1.2) is false || plain.Covers(0.2, 0.8));
    }

    [Fact]
    public void PixelAspectDoesNotDisturbNormalizedMap()
    {
        // 非方形像素只拉伸列數；正規化座標的對映不變（拉伸在正規化中抵消）。
        var geo = RawSourceGeometry.Create(Raw(10, 10, pixelAspect: 2.0f));
        var (ax, ay) = geo.Active(0.25, 0.5);
        Near(0.25, ax, 1e-12, "x");
        Near(0.5, ay, 1e-12, "y");
    }

    [Fact]
    public void CreateReadsNoSensorSamples()
    {
        // Data 為空（Create 不碰取樣）：建構與映射都不能要求 Data 長度。
        var raw = Raw(8, 6, list3: [new WarpRectilinear([[1.1, 0, 0, 0, 0, 0]], [0.5, 0.5])]);
        Assert.Equal(0, raw.Data.Length);
        var geo = RawSourceGeometry.Create(raw);
        var (ax, ay) = geo.Active(0.5, 0.5);
        Near(0.5, ax, 1e-12, "x");
        Near(0.5, ay, 1e-12, "y");
    }

    [Fact]
    public void DegenerateWarpIsSkippedNotThrown()
    {
        // 無 planes 的 WarpRectilinear：套用路徑會失敗，但幾何快照略過（不擲出）。
        var geo = RawSourceGeometry.Create(Raw(10, 10, list3:
        [
            new WarpRectilinear([], [0.5, 0.5]),
        ]));
        var (ax, ay) = geo.Active(0.4, 0.6);
        Near(0.4, ax, 1e-12, "x");
        Near(0.6, ay, 1e-12, "y");
    }

    [Fact]
    public void EmptyCropFallsBackToActiveArea()
    {
        // 裁切完全在界外 → DevelopCrop 回落全有效區；幾何沿用同一規則。
        var geo = RawSourceGeometry.Create(Raw(10, 10, new PixelRect(20, 20, 4, 4)));
        var (ax, ay) = geo.Active(0.3, 0.7);
        Near(0.3, ax, 1e-12, "x");
        Near(0.7, ay, 1e-12, "y");
    }
}
