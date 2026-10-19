// 移植自 LightCraft crates/raw/src/opcodes.rs（tests：vignette 與 warp 幾何）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Tests.Opcodes;

public sealed class WarpTests
{
    private static RgbImage FromFn(int w, int h, Func<int, int, float[]> f)
    {
        var data = new float[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                Array.Copy(f(x, y), 0, data, (y * w + x) * 3, 3);
        return new RgbImage(w, h, data);
    }

    private static float[] Get(RgbImage img, int x, int y)
    {
        var o = (y * img.Width + x) * 3;
        return [img.Data[o], img.Data[o + 1], img.Data[o + 2]];
    }

    private static byte[] Be(params double[] vals)
    {
        var b = new byte[vals.Length * 8];
        for (var i = 0; i < vals.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(b.AsSpan(i * 8, 8), vals[i]);
        return b;
    }

    private static byte[] BeU32(params uint[] vals)
    {
        var b = new byte[vals.Length * 4];
        for (var i = 0; i < vals.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4, 4), vals[i]);
        return b;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] List(params (uint Id, byte[] Params)[] ops)
    {
        var body = new List<byte>();
        foreach (var (id, p) in ops)
        {
            var h = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0, 4), id);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4, 4), 0x01030000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8, 4), 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12, 4), (uint)p.Length);
            body.AddRange(h);
            body.AddRange(p);
        }
        var head = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, (uint)ops.Length);
        return [.. head, .. body];
    }

    [Fact]
    public void VignetteAndWarpIdentity()
    {
        var img = FromFn(9, 7, (x, y) => [x / 9.0f, y / 7.0f, 0.5f]);
        var orig = (float[])img.Data.Clone();
        var p = Concat(Be(0.0, 0.0, 0.0, 0.0, 0.0), Be(0.5, 0.5));
        var warp = Concat(BeU32(1), Be(1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.5, 0.5));
        var list = OpcodeOps.ParseList(List((3, p), (1, warp)));
        Assert.Equal(2, list.Count);
        var outImg = OpcodeOps.ApplyList3(list, img);
        for (var i = 0; i < orig.Length; i++)
            Assert.True(MathF.Abs(outImg.Data[i] - orig[i]) < 1e-5f, $"{i}: {outImg.Data[i]} vs {orig[i]}");

        // k0 為正：角落比中央亮更多
        var p2 = Concat(Be(1.0, 0.0, 0.0, 0.0, 0.0), Be(0.5, 0.5));
        var list2 = OpcodeOps.ParseList(List((3, p2)));
        var flat = FromFn(9, 9, (_, _) => [0.5f, 0.5f, 0.5f]);
        var out2 = OpcodeOps.ApplyList3(list2, flat);
        Assert.True(Get(out2, 0, 0)[0] > 0.85f, $"{Get(out2, 0, 0)[0]}");
        Assert.True(MathF.Abs(Get(out2, 4, 4)[0] - 0.5f) < 0.01f);
    }

    [Fact]
    public void WarpBarrelMovesPixelsOutward()
    {
        var img = FromFn(31, 31, (x, y) => x == 24 && y == 15 ? [1.0f, 1.0f, 1.0f] : [0.0f, 0.0f, 0.0f]);
        var plane = new double[][] { [1.0, -0.6, 0.0, 0.0, 0.0, 0.0] };
        var outImg = OpcodeOps.WarpRectilinear(img, plane, [0.5, 0.5]);
        var bx = 0;
        var best = 0.0f;
        for (var x = 0; x < 31; x++)
        {
            var v = Get(outImg, x, 15)[0];
            if (v > best) { best = v; bx = x; }
        }
        Assert.True(bx >= 25, $"{bx}");
    }

    private static double[] Wr2Set(params (int I, double V)[] kvs)
    {
        var k = new double[OpcodeOps.Wr2Len];
        foreach (var (i, v) in kvs) k[i] = v;
        k[18] = 1.0;
        return k;
    }

    private static byte[] Wr2Params(double[][] sets, double[] center, uint reciprocal)
    {
        var b = new List<byte>(BeU32((uint)sets.Length));
        foreach (var s in sets) b.AddRange(Be(s));
        b.AddRange(Be(center));
        b.AddRange(BeU32(reciprocal));
        return b.ToArray();
    }

    [Fact]
    public void WarpRectilinear2IdentityLeavesTheImage()
    {
        var img = FromFn(21, 15, (x, y) => [x / 21.0f, y / 15.0f, 0.25f]);
        foreach (var reciprocal in new[] { false, true })
        {
            var p = Wr2Params([Wr2Set((0, 1.0))], [0.5, 0.5], reciprocal ? 1u : 0u);
            var data = (float[])img.Data.Clone();
            var probe = new RgbImage(img.Width, img.Height, data);
            var outImg = OpcodeOps.ApplyList3(OpcodeOps.ParseList(List((14, p))), probe);
            for (var i = 0; i < data.Length; i++)
                Assert.True(MathF.Abs(outImg.Data[i] - img.Data[i]) < 1e-5f, $"{i}: {outImg.Data[i]} vs {img.Data[i]}");
        }
    }

    [Fact]
    public void WarpRectilinear2MatchesWarpRectilinearOnEvenTerms()
    {
        var img = FromFn(31, 31, (x, y) => [((x * 7) % 13) / 13.0f, ((y * 5) % 11) / 11.0f, ((x + y) % 4) / 4.0f]);
        var a = OpcodeOps.WarpRectilinear(img, [[1.0, -0.3, 0.1, 0.0, 0.01, -0.02]], [0.5, 0.5]);
        var k = Wr2Set((0, 1.0), (2, -0.3), (4, 0.1));
        k[15] = 0.01;
        k[16] = -0.02;
        var b = OpcodeOps.WarpRectilinear2(img, [k], [0.5, 0.5], false);
        for (var i = 0; i < a.Data.Length; i++)
            Assert.True(MathF.Abs(a.Data[i] - b.Data[i]) < 1e-5f, $"{i}: {a.Data[i]} vs {b.Data[i]}");

        // 奇次項、倒數與有效半徑箝制
        var k2 = Wr2Set((0, 1.0), (1, 0.5));
        Assert.Equal(1.25, OpcodeOps.Wr2Factor(k2, false, 0.5));
        Assert.Equal(0.8, OpcodeOps.Wr2Factor(k2, true, 0.5));
        k2[18] = 0.5;
        Assert.Equal(1.25, OpcodeOps.Wr2Factor(k2, false, 0.9));
        k2[17] = 0.25;
        Assert.Equal(1.125, OpcodeOps.Wr2Factor(k2, false, 0.0));
        Assert.Null(OpcodeOps.Wr2Factor(Wr2Set(), true, 0.3));
    }
    [Fact]
    public void MalformedWarpRectilinear2IsUnknownNotAThrow()
    {
        var good = Wr2Params([Wr2Set((0, 1.0))], [0.5, 0.5], 0);
        bool IsWr2(byte[] p) => OpcodeOps.ParseList(List((14, p))) is [WarpRectilinear2];
        Assert.True(IsWr2(good));
        for (var n = 0; n < good.Length; n++) Assert.False(IsWr2(good[..n]), $"truncation {n}");
        Assert.False(IsWr2([.. good, 0]));
        foreach (var n in new uint[] { 0, 2, 5, uint.MaxValue })
        {
            var p = (byte[])good.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0, 4), n);
            Assert.False(IsWr2(p), $"N = {n}");
        }
        void Patch(int at, double v)
        {
            var p = (byte[])good.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(p.AsSpan(at, 8), v);
            Assert.False(IsWr2(p), $"{at} {v}");
        }
        Patch(4, double.NaN); Patch(12, double.PositiveInfinity); Patch(4 + 8 * 3, 1e300);
        Patch(4 + 8 * 17, -0.5); Patch(4 + 8 * 18, 1.5); Patch(4 + 8 * 17, 1.0);
        Patch(4 + 19 * 8, double.NaN); Patch(4 + 19 * 8 + 8, 1e30);
        var refused = OpcodeOps.ParseList(List((14, good[..10])));
        Assert.IsType<Unknown>(Assert.Single(refused));
        var img = FromFn(5, 5, (_, _) => [0.5f, 0.5f, 0.5f]);
        var after = OpcodeOps.ApplyList3(refused, img);
        Assert.Equal([0.5f, 0.5f, 0.5f], Get(after, 2, 2));
        // 敵意但可接受的集合（巨大的徑向項）仍給出有限影像
        var hostile = Wr2Set((0, 1.0), (14, 1e9), (13, -1e9));
        var ramp = FromFn(9, 9, (x, y) => [x / 9f, y / 9f, 0.5f]);
        foreach (var reciprocal in new[] { false, true })
        {
            var warped = OpcodeOps.WarpRectilinear2(ramp, [hostile], [0.5, 0.5], reciprocal);
            Assert.All(warped.Data, v => Assert.True(float.IsFinite(v)));
        }
    }

    [Fact]
    public void WarpRectilinear2ParsesTheSpecLayout()
    {
        var k = Wr2Set((0, 1.0), (1, 0.25), (14, -2.0));
        k[15] = 0.001; k[16] = -0.002; k[17] = 0.1; k[18] = 0.9;
        var blob = List((14, Wr2Params([k, Wr2Set((0, 1.0))], [0.5, 0.25], 1)));
        var list = OpcodeOps.ParseList(blob);
        var wr2 = Assert.IsType<WarpRectilinear2>(Assert.Single(list));
        Assert.Equal(k, wr2.Planes[0]); Assert.Equal([0.5, 0.25], wr2.Center);
        Assert.True(wr2.Reciprocal); Assert.False(wr2.Optional);
        Assert.True(wr2.IsApplied() && wr2.IsLensCorrection());
        var again = new List<Opcode> { new WarpRectilinear2([k], [0.5, 0.5], false, true) };
        var bytes = OpcodeOps.WriteList(again);
        Assert.Equal(14u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)));
        Assert.Equal(0x01060000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)));
        var back = OpcodeOps.ParseList(bytes);
        Assert.Equal([0.5, 0.5], ((WarpRectilinear2)back[0]).Center);
        Assert.True(((WarpRectilinear2)back[0]).Optional);
    }

    [Fact]
    public void CroppedLensCorrectionsCorrectTheCropLikeTheWhole()
    {
        const int w = 90, h = 64;
        var crop = new PixelRect(7, 5, 70, 50);
        var ramp = FromFn(w, h, (x, y) => [0.002f * x + 0.001f * y + 0.1f, 0.3f + 0.0015f * y, 0.2f]);
        var wr2 = Wr2Set((0, 1.0), (1, 0.01), (2, -0.03), (4, 0.01));
        wr2[15] = 0.002; wr2[16] = -0.001; wr2[17] = 0.05; wr2[18] = 0.9;
        Opcode[] warps =
        [
            new WarpRectilinear([[0.97, 0.04, -0.01, 0.002, 0.003, -0.002]], [0.47, 0.53]),
            new WarpRectilinear2([wr2], [0.52, 0.46], false, true),
            new WarpRectilinear2([wr2], [0.52, 0.46], true, false),
        ];
        foreach (var op in warps)
        {
            var whole = OpcodeOps.ApplyList3([op], FromFn(w, h, (x, y) => [ramp.Data[(y * w + x) * 3], ramp.Data[(y * w + x) * 3 + 1], ramp.Data[(y * w + x) * 3 + 2]]));
            Assert.NotNull(op.Cropped(w, h, crop));
            Assert.Equal(op.GetType(), op.Cropped(w, h, crop)!.GetType());
            var re = op.Cropped(w, h, crop)!;

            var partSrc = FromFn(w, h, (x, y) => [ramp.Data[(y * w + x) * 3], ramp.Data[(y * w + x) * 3 + 1], ramp.Data[(y * w + x) * 3 + 2]]).Crop(crop.X, crop.Y, crop.Width, crop.Height);
            var part = OpcodeOps.ApplyList3([re], partSrc);
            var worst = 0f;
            for (var y = 6; y < crop.Height - 6; y++)
                for (var x = 6; x < crop.Width - 6; x++)
                    for (var c = 0; c < 3; c++)
                        worst = MathF.Max(worst, MathF.Abs(whole.Data[((y + crop.Y) * w + x + crop.X) * 3 + c] - part.Data[(y * crop.Width + x) * 3 + c]));
            Assert.True(worst < 1e-4f, $"{op.GetType().Name}: {worst}");
        }
        Assert.Equal(warps[0], warps[0].Cropped(w, h, new PixelRect(0, 0, w, h)));
        Assert.Null(new TrimBounds(0, 0, 2, 2).Cropped(w, h, crop));
        Assert.Null(warps[0].Cropped(1, h, new PixelRect(0, 0, 1, h)));
        Assert.Null(warps[0].Cropped(w, h, new PixelRect(30, 0, 70, 50)));
        Assert.Null(warps[0].Cropped(w, h, new PixelRect(int.MaxValue, 0, 70, 50)));
    }
}
