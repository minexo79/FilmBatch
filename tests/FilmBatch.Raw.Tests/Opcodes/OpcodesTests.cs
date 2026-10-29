// 移植自 LightCraft crates/raw/src/opcodes.rs（tests：區域運算碼解析與套用）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Buffers.Binary;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Tests.Opcodes;

public sealed class OpcodesTests
{
    private sealed class ListWriter
    {
        private readonly List<byte> _body = [];
        private uint _count;

        public ListWriter Op(uint id, byte[] ps)
        {
            AppendU32(id);
            AppendU32(0x01030000);
            AppendU32(0);
            AppendU32((uint)ps.Length);
            _body.AddRange(ps);
            _count++;
            return this;
        }

        public byte[] Finish()
        {
            var head = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(head, _count);
            return [.. head, .. _body];
        }

        private void AppendU32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            foreach (var x in b.ToArray()) _body.Add(x);
        }
    }

    private static byte[] Be(params double[] vals)
    {
        var b = new byte[vals.Length * 8];
        for (var i = 0; i < vals.Length; i++) BinaryPrimitives.WriteDoubleBigEndian(b.AsSpan(i * 8, 8), vals[i]);
        return b;
    }

    private static byte[] BeF32(params float[] vals)
    {
        var b = new byte[vals.Length * 4];
        for (var i = 0; i < vals.Length; i++) BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(i * 4, 4), vals[i]);
        return b;
    }

    private static byte[] BeU32(params uint[] vals)
    {
        var b = new byte[vals.Length * 4];
        for (var i = 0; i < vals.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4, 4), vals[i]);
        return b;
    }

    private static byte[] BeU16(params ushort[] vals)
    {
        var b = new byte[vals.Length * 2];
        for (var i = 0; i < vals.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(i * 2, 2), vals[i]);
        return b;
    }

    private static byte[] AreaBytes(params uint[] a)
    {
        Assert.Equal(8, a.Length);
        return BeU32(a);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static void AssertOpcodeEqual(Opcode expected, Opcode actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        switch (expected, actual)
        {
            case (WarpRectilinear e, WarpRectilinear a):
                Assert.Equal(e.Planes.Length, a.Planes.Length);
                for (var i = 0; i < e.Planes.Length; i++) Assert.Equal(e.Planes[i], a.Planes[i]);
                Assert.Equal(e.Center, a.Center);
                break;
            case (WarpFisheye e, WarpFisheye a):
                Assert.Equal(e.Planes.Length, a.Planes.Length);
                for (var i = 0; i < e.Planes.Length; i++) Assert.Equal(e.Planes[i], a.Planes[i]);
                Assert.Equal(e.Center, a.Center);
                break;
            case (WarpRectilinear2 e, WarpRectilinear2 a):
                Assert.Equal(e.Planes.Length, a.Planes.Length);
                for (var i = 0; i < e.Planes.Length; i++) Assert.Equal(e.Planes[i], a.Planes[i]);
                Assert.Equal(e.Center, a.Center);
                Assert.Equal(e.Reciprocal, a.Reciprocal);
                Assert.Equal(e.Optional, a.Optional);
                break;
            case (FixVignetteRadial e, FixVignetteRadial a):
                Assert.Equal(e.K, a.K);
                Assert.Equal(e.Center, a.Center);
                break;
            case (FixBadPixelsConstant e, FixBadPixelsConstant a):
                Assert.Equal(e.Constant, a.Constant);
                Assert.Equal(e.BayerPhase, a.BayerPhase);
                break;
            case (FixBadPixelsList e, FixBadPixelsList a):
                Assert.Equal(e.BayerPhase, a.BayerPhase);
                Assert.Equal(e.Points, a.Points);
                Assert.Equal(e.Rects.Length, a.Rects.Length);
                for (var i = 0; i < e.Rects.Length; i++) Assert.Equal(e.Rects[i], a.Rects[i]);
                break;
            case (TrimBounds e, TrimBounds a):
                Assert.Equal(e, a);
                break;
            case (MapTable e, MapTable a):
                Assert.Equal(e.Area, a.Area);
                Assert.Equal(e.Table, a.Table);
                break;
            case (MapPolynomial e, MapPolynomial a):
                Assert.Equal(e.Area, a.Area);
                Assert.Equal(e.Coefficients, a.Coefficients);
                break;
            case (GainMap e, GainMap a):
                Assert.Equal(e.Area, a.Area);
                Assert.Equal(e.PointsV, a.PointsV);
                Assert.Equal(e.PointsH, a.PointsH);
                Assert.Equal(e.Spacing, a.Spacing);
                Assert.Equal(e.Origin, a.Origin);
                Assert.Equal(e.MapPlanes, a.MapPlanes);
                Assert.Equal(e.Gains, a.Gains);
                break;
            case (DeltaPerRow e, DeltaPerRow a):
                Assert.Equal(e.Area, a.Area);
                Assert.Equal(e.Deltas, a.Deltas);
                break;
            case (DeltaPerColumn e, DeltaPerColumn a):
                Assert.Equal(e.Area, a.Area);
                Assert.Equal(e.Deltas, a.Deltas);
                break;
            case (ScalePerRow e, ScalePerRow a):
                Assert.Equal(e.Area, a.Area);
                Assert.Equal(e.Scales, a.Scales);
                break;
            case (ScalePerColumn e, ScalePerColumn a):
                Assert.Equal(e.Area, a.Area);
                Assert.Equal(e.Scales, a.Scales);
                break;
            case (Unknown e, Unknown a):
                Assert.Equal(e.Id, a.Id);
                Assert.Equal(e.Flags, a.Flags);
                Assert.Equal(e.Params, a.Params);
                break;
            default:
                Assert.Fail($"未處理的運算碼：{expected.GetType()}");
                break;
        }
    }

    [Fact]
    public void ParseAndApplyAreaOps()
    {
        const int w = 4, h = 3;
        var gm = Concat(
            AreaBytes(0, 0, 3, 4, 0, 1, 1, 1),
            BeU32(2, 2),
            Be(1.0, 1.0, 0.0, 0.0),
            BeU32(1),
            BeF32(1.0f, 2.0f, 1.0f, 2.0f));
        var dpr = Concat(
            AreaBytes(0, 0, 3, 4, 0, 1, 1, 1),
            BeU32(3),
            BeF32(0.0f, 0.1f, 0.2f));
        var poly = Concat(
            AreaBytes(0, 0, 3, 4, 0, 1, 1, 2),
            BeU32(1),
            Be(0.0, 2.0));
        var blob = new ListWriter().Op(9, gm).Op(10, dpr).Op(8, poly).Op(99, [1, 2, 3]).Finish();
        var list = OpcodeOps.ParseList(blob);
        Assert.Equal(4, list.Count);
        Assert.IsType<Unknown>(list[3]);
        Assert.Equal(99u, ((Unknown)list[3]).Id);
        Assert.All(list.Take(3), o => Assert.True(o.IsApplied()));

        var buf = Enumerable.Repeat(0.25f, w * h).ToArray();
        OpcodeOps.ApplyList(list, buf, w, h, 1, null, 1.0f);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var g = 1.0f + x / (float)w;
                var v = 0.25f * g + new[] { 0.0f, 0.1f, 0.2f }[y];
                if (x % 2 == 0) v *= 2.0f;
                Assert.True(MathF.Abs(buf[y * w + x] - v) < 1e-6f, $"{x},{y}: {buf[y * w + x]} vs {v}");
            }
        }
    }

    [Theory]
    [InlineData(1073741824u, 1073741824u, 4u)]
    [InlineData(uint.MaxValue, uint.MaxValue, uint.MaxValue)]
    public void OversizedGainMapCountsArePreservedAsUnknownWithoutAllocation(uint rows, uint columns, uint planes)
    {
        var payload = Concat(
            AreaBytes(0, 0, 1, 1, 0, 1, 1, 1),
            BeU32(rows, columns),
            Be(1, 1, 0, 0),
            BeU32(planes));
        var parsed = OpcodeOps.ParseList(new ListWriter().Op(9, payload).Finish());
        Assert.IsType<Unknown>(Assert.Single(parsed));
    }

    [Fact]
    public void GainMapPreservesPitchedAreaPlaneOffsetsAndClampedInterpolation()
    {
        const int w = 8, h = 8, cpp = 4;
        var pixels = new float[w * h * cpp];
        for (var i = 0; i < pixels.Length; i += cpp)
        {
            pixels[i] = 2; pixels[i + 1] = 3; pixels[i + 2] = 5; pixels[i + 3] = 7;
        }
        var map = new GainMap(new Area(1, 1, 12, 12, 1, 3, 2, 2),
            2, 2, [.5, .5], [.125, .125], 2, [1, 2, 3, 6, 5, 10, 7, 14]);
        OpcodeOps.ApplyList([map], pixels, w, h, cpp, null, 1f);
        // Coordinates 1,3,5,7 sample 0,1/2,1,1 on each grid axis.
        float[,] gains = { { 1, 2, 3, 3 }, { 3, 4, 5, 5 }, { 5, 6, 7, 7 }, { 5, 6, 7, 7 } };
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var offset = (y * w + x) * cpp;
                var selected = x % 2 == 1 && y % 2 == 1;
                var gain = selected ? gains[y / 2, x / 2] : 1f;
                Assert.Equal(2f, pixels[offset]);
                Assert.Equal(3f * gain, pixels[offset + 1]);
                Assert.Equal(5f * gain * (selected ? 2f : 1f), pixels[offset + 2]);
                Assert.Equal(7f * gain * (selected ? 2f : 1f), pixels[offset + 3]);
            }
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void GainMapNonpositiveSpacingUsesFirstGridCoordinate(double spacing)
    {
        float[] pixels = [1, 2, 3, 4];
        var map = new GainMap(new Area(0, 0, 2, 2, 0, 0, 0, 0),
            2, 2, [spacing, spacing], [.25, .25], 1, [2, 7, 11, 13]);
        OpcodeOps.ApplyList([map], pixels, 2, 2, 1, null, 1f);
        Assert.Equal(new float[] { 2, 4, 6, 8 }, pixels);
    }

    [Theory]
    [InlineData(4u, 0u, 0u)]
    [InlineData(0u, 4u, 0u)]
    [InlineData(0u, 0u, 1u)]
    [InlineData(uint.MaxValue, 0u, 0u)]
    public void GainMapOutsideImageOrPlanesLeavesSamplesUntouched(uint top, uint left, uint plane)
    {
        float[] pixels = [1, 2, 3, 4];
        var map = new GainMap(new Area(top, left, 9, 9, plane, 1, 1, 1),
            1, 1, [1, 1], [0, 0], 1, [9]);
        OpcodeOps.ApplyList([map], pixels, 2, 2, 1, null, 1f);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, pixels);
    }

    [Fact]
    public void MapTableRawScaleAndBadPixels()
    {
        var t = Concat(AreaBytes(0, 0, 2, 2, 0, 1, 1, 1), BeU32(3), BeU16(0, 30000, 65535));
        var list = OpcodeOps.ParseList(new ListWriter().Op(7, t).Finish());
        var buf = new float[] { 0.0f, 1.0f / 65535.0f, 2.0f / 65535.0f, 0.5f };
        OpcodeOps.ApplyList(list, buf, 2, 2, 1, null, 1.0f);
        Assert.Equal(0.0f, buf[0]);
        Assert.True(MathF.Abs(buf[1] - 30000.0f / 65535.0f) < 1e-7f);
        Assert.Equal(1.0f, buf[3]);

        var p = BeU32(7, 0);
        var list2 = OpcodeOps.ParseList(new ListWriter().Op(4, p).Finish());
        var cfa = Cfa.Bayer("RGGB");
        Assert.NotNull(cfa);
        var img = Enumerable.Range(0, 36).Select(i => 100.0f + (i % 6)).ToArray();
        img[2 * 6 + 2] = 7.0f;
        OpcodeOps.ApplyList(list2, img, 6, 6, 1, cfa, 65535.0f);
        Assert.Equal(102.0f, img[2 * 6 + 2]);
    }

    private static void AssertBadPixelsCapped(Opcode op)
    {
        const int w = 2048, h = 2054;
        var buf = new float[w * h];
        Array.Fill(buf, 0.0f, 0, w);
        for (var i = 0; i < w; i++) buf[i] = 500.0f;
        for (var i = 0; i < w; i++) buf[(h - 1) * w + i] = 900.0f;
        OpcodeOps.ApplyList([op], buf, w, h, 1, null, 65535.0f);
        for (var x = 0; x < w; x++) Assert.Equal(500.0f, buf[w + x]);
        for (var x = 0; x < w; x++) Assert.Equal(0.0f, buf[(h - 2) * w + x]);
    }

    [Fact]
    public void BadPixelsConstantIsCapped()
    {
        AssertBadPixelsCapped(new FixBadPixelsConstant(0, 0));
    }

    [Fact]
    public void BadPixelsListIsCappedInsideARectangle()
    {
        AssertBadPixelsCapped(new FixBadPixelsList(0, [], [[1, 0, 2053, 2048]]));
    }

    [Fact]
    public void WriteParseRoundtrip()
    {
        var a = new Area(1, 2, 30, 40, 0, 3, 1, 2);
        static double[] Wr2(params (int I, double V)[] kvs)
        {
            var k = new double[OpcodeOps.Wr2Len];
            foreach (var (i, v) in kvs) k[i] = v;
            k[18] = 1.0;
            return k;
        }
        List<Opcode> list =
        [
            new WarpRectilinear([ [1.0, 0.01, -0.002, 0.0, 1e-4, -2e-4], [1.0, 0.01, -0.002, 0.0, 1e-4, -2e-4], [1.0, 0.01, -0.002, 0.0, 1e-4, -2e-4] ], [0.5, 0.49]),
            new WarpFisheye([ [1.0, 0.1, 0.0, 0.0] ], [0.5, 0.5]),
            new FixVignetteRadial([0.1, 0.2, 0.0, 0.0, 0.0], [0.5, 0.5]),
            new FixBadPixelsConstant(0, 1),
            new FixBadPixelsList(0, [(3, 4)], [[1, 1, 2, 2]]),
            new TrimBounds(1, 1, 9, 9),
            new MapTable(a, [0, 100, 65535]),
            new MapPolynomial(a, [0.0, 1.0, -0.1]),
            new GainMap(a, 2, 1, [1.0, 1.0], [0.0, 0.0], 1, [1.0f, 1.5f]),
            new DeltaPerRow(a, [0.1f, 0.2f]),
            new DeltaPerColumn(a, [0.3f]),
            new ScalePerRow(a, [1.1f]),
            new ScalePerColumn(a, [0.9f, 1.0f]),
            new WarpRectilinear2([Wr2([(0, 0.5), (3, 0.1)]), Wr2([(0, 0.25)])], [0.5, 0.49], true, true),
            new Unknown(77, 1, [1, 2, 3]),
        ];
        var back = OpcodeOps.ParseList(OpcodeOps.WriteList(list));
        Assert.Equal(list.Count, back.Count);
        for (var i = 0; i < list.Count; i++) AssertOpcodeEqual(list[i], back[i]);
    }

    [Fact]
    public void TruncatedListsDoNotThrow()
    {
        var gm = Concat(AreaBytes(0, 0, 3, 4, 0, 1, 1, 1), new byte[64]);
        for (var i = 0; i < 64; i++) gm[32 + i] = 0xff;
        var blob = new ListWriter().Op(9, gm).Op(5, new byte[12]).Op(1, new byte[] { 0, 0, 0, 9 }).Finish();
        for (var n = 0; n < blob.Length; n++)
        {
            var l = OpcodeOps.ParseList(blob.AsSpan(0, n));
            var buf = Enumerable.Repeat(0.5f, 12).ToArray();
            OpcodeOps.ApplyList(l, buf, 4, 3, 1, null, 1.0f);
        }
    }

    [Fact]
    public void IsAppliedAndIsLensCorrection()
    {
        Assert.True(new WarpRectilinear([[1, 0, 0, 0, 0, 0]], [0.5, 0.5]).IsApplied());
        Assert.True(new WarpRectilinear2([new double[OpcodeOps.Wr2Len]], [0.5, 0.5], false, false).IsApplied());
        Assert.True(new FixVignetteRadial(new double[5], [0.5, 0.5]).IsApplied());
        Assert.True(new MapTable(new Area(0, 0, 1, 1, 0, 1, 1, 1), [0]).IsApplied());
        Assert.False(new WarpFisheye([[1, 0, 0, 0]], [0.5, 0.5]).IsApplied());
        Assert.False(new TrimBounds(0, 0, 1, 1).IsApplied());
        Assert.False(new Unknown(99, 0, [1]).IsApplied());

        Assert.True(new WarpRectilinear([[1, 0, 0, 0, 0, 0]], [0.5, 0.5]).IsLensCorrection());
        Assert.True(new WarpRectilinear2([new double[OpcodeOps.Wr2Len]], [0.5, 0.5], false, false).IsLensCorrection());
        Assert.True(new FixVignetteRadial(new double[5], [0.5, 0.5]).IsLensCorrection());
        Assert.False(new WarpFisheye([[1, 0, 0, 0]], [0.5, 0.5]).IsLensCorrection());
        Assert.False(new MapTable(new Area(0, 0, 1, 1, 0, 1, 1, 1), [0]).IsLensCorrection());
    }

    [Fact]
    public void TrimBoundsIsRecordedNotApplied()
    {
        var buf = new float[] { 1.0f, 2.0f, 3.0f, 4.0f };
        var before = (float[])buf.Clone();
        OpcodeOps.ApplyList([new TrimBounds(0, 0, 1, 1)], buf, 2, 2, 1, null, 1.0f);
        Assert.Equal(before, buf);
        var img = new RgbImage(2, 1, [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f]);
        var snap = (float[])img.Data.Clone();
        var outImg = OpcodeOps.ApplyList3([new TrimBounds(0, 0, 1, 1)], img);
        Assert.Equal(snap, outImg.Data);
    }

    [Fact]
    public void ExcessiveList3PassesFailWithoutReturningPartiallyCorrectedPixels()
    {
        var image = new RgbImage(1, 1, [1f / 512, 1f / 512, 1f / 512]);
        var scale = new MapPolynomial(new Area(0, 0, 1, 1, 0, 3, 1, 1), [0, 2]);
        var allowed = Enumerable.Repeat<Opcode>(scale, 8).Append(new TrimBounds(0, 0, 1, 1)).ToArray();
        Assert.Equal(new float[] { .5f, .5f, .5f }, OpcodeOps.ApplyList3(allowed, image).Data);
        var error = Assert.Throws<RawException>(() => OpcodeOps.ApplyList3([.. allowed, scale], image));
        Assert.Equal(RawErrorKind.Limit, error.Kind);
        Assert.Equal(new float[] { 1f / 512, 1f / 512, 1f / 512 }, image.Data);
    }

    [Fact]
    public void List3LimitCountsAppliedOperationsAfterOptionalSkip()
    {
        var image = new RgbImage(2, 2, Enumerable.Repeat(.25f, 12).ToArray());
        var vignette = new FixVignetteRadial([0, 0, 0, 0, 0], [.5, .5]);
        var coefficients = new double[OpcodeOps.Wr2Len];
        coefficients[0] = 1; coefficients[18] = 1;
        var wr = new WarpRectilinear([[1, 0, 0, 0, 0, 0]], [.5, .5]);
        var prefix = Enumerable.Repeat<Opcode>(vignette, 7).ToArray();
        var optional = new WarpRectilinear2([coefficients], [.5, .5], false, true);
        var required = new WarpRectilinear2([coefficients], [.5, .5], false, false);
        Assert.Equal(image.Data, OpcodeOps.ApplyList3([.. prefix, optional, wr, new Unknown(99, 0, [])], image).Data);
        Assert.Equal(RawErrorKind.Limit, Assert.Throws<RawException>(() =>
            OpcodeOps.ApplyList3([.. prefix, required, wr], image)).Kind);
    }

    [Fact]
    public void WarpListOrderAndTheOptionalSkipRule()
    {
        static double[] IdWr2()
        {
            var k = new double[OpcodeOps.Wr2Len];
            k[0] = 1.0;
            k[18] = 1.0;
            return k;
        }
        WarpRectilinear2 Wr2(bool optional) => new([IdWr2()], [0.5, 0.5], false, optional);
        var wr = new WarpRectilinear([[1.0, 0.0, 0.0, 0.0, 0.0, 0.0]], [0.5, 0.5]);
        var fish = new WarpFisheye([[1.0, 0.0, 0.0, 0.0]], [0.5, 0.5]);
        var vig = new FixVignetteRadial([0, 0, 0, 0, 0], [0.5, 0.5]);
        static string Id(Opcode o) => o.GetType().Name;
        static List<string> Ids(IEnumerable<Opcode> l) => OpcodeOps.AppliedOps(l).Select(Id).ToList();

        Assert.Equal([nameof(WarpRectilinear2)], Ids([Wr2(true), wr]));
        Assert.Equal([nameof(WarpRectilinear2)], Ids([Wr2(true), fish]));
        Assert.Equal([nameof(WarpRectilinear2), nameof(WarpRectilinear)], Ids([Wr2(false), wr]));
        Assert.Equal([nameof(WarpRectilinear2), nameof(FixVignetteRadial), nameof(WarpRectilinear)], Ids([Wr2(true), vig, wr]));
        Assert.Equal([nameof(WarpRectilinear2), nameof(WarpRectilinear2)], Ids([Wr2(true), Wr2(true), wr]));
        Assert.Equal([nameof(WarpRectilinear), nameof(WarpRectilinear2)], Ids([wr, Wr2(true)]));

        var w2params = Wr2Params([IdWr2()], [0.5, 0.5], 0);
        var one = Concat(BeU32(1), Be(1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.5, 0.5));
        var blob = new List<byte>();
        void RawOp(uint id, uint flags, byte[] p)
        {
            var tmp = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(tmp.AsSpan(0, 4), id);
            BinaryPrimitives.WriteUInt32BigEndian(tmp.AsSpan(4, 4), 0x01060000);
            BinaryPrimitives.WriteUInt32BigEndian(tmp.AsSpan(8, 4), flags);
            BinaryPrimitives.WriteUInt32BigEndian(tmp.AsSpan(12, 4), (uint)p.Length);
            blob.AddRange(tmp);
            blob.AddRange(p);
        }
        var head = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(head, 2);
        var full = Concat(head, blob.ToArray());
        // 手工組出 [optional opcode14, opcode1] 再解析，驗證旗標帶來的 skip
        var manual = new List<byte>(head);
        var t1 = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(t1.AsSpan(0, 4), 14);
        BinaryPrimitives.WriteUInt32BigEndian(t1.AsSpan(4, 4), 0x01060000);
        BinaryPrimitives.WriteUInt32BigEndian(t1.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(t1.AsSpan(12, 4), (uint)w2params.Length);
        manual.AddRange(t1);
        manual.AddRange(w2params);
        var t2 = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(t2.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(t2.AsSpan(4, 4), 0x01060000);
        BinaryPrimitives.WriteUInt32BigEndian(t2.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(t2.AsSpan(12, 4), (uint)one.Length);
        manual.AddRange(t2);
        manual.AddRange(one);
        var list = OpcodeOps.ParseList(manual.ToArray());
        Assert.Equal(2, list.Count);
        Assert.Single(OpcodeOps.AppliedOps(list));

        var bad = new Unknown(14, 1, []);
        Assert.Contains(OpcodeOps.AppliedOps([bad, wr]), o => o is WarpRectilinear);
        _ = full;
        RawOp(0, 0, []);
    }

    private static byte[] Wr2Params(double[][] sets, double[] center, uint reciprocal)
    {
        var outB = new List<byte>(BeU32((uint)sets.Length));
        foreach (var s in sets) outB.AddRange(Be(s));
        outB.AddRange(Be(center));
        outB.AddRange(BeU32(reciprocal));
        return outB.ToArray();
    }
}
