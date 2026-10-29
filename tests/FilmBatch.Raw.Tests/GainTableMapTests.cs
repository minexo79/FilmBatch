// DNG 增益表映射行為測試（對應 lightcraft gaintable.rs 單元測試；期望為手算／上游字面量）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Text.Json;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Tests.Support;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests;

public sealed class GainTableMapTests
{
    private static readonly float[] Green = [0f, 1f, 0f, 0f, 0f];
    private static readonly float[] Luma = [1f / 3f, 1f / 3f, 1f / 3f, 0f, 0f];

    private static byte[] Tag(ByteOrder order, uint[] dims, double[] spacing, double[] origin, float[] weights, (uint DataType, float Gamma, float Min, float Max)? v2, byte[] data)
    {
        var b = new List<byte>();
        void U32(uint v)
        {
            if (order == ByteOrder.Little) for (var i = 0; i < 4; i++) b.Add((byte)(v >> (8 * i)));
            else for (var i = 3; i >= 0; i--) b.Add((byte)(v >> (8 * i)));
        }
        void F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));
        void F64(double v)
        {
            var bits = BitConverter.DoubleToUInt64Bits(v);
            if (order == ByteOrder.Little) for (var i = 0; i < 8; i++) b.Add((byte)(bits >> (8 * i)));
            else for (var i = 7; i >= 0; i--) b.Add((byte)(bits >> (8 * i)));
        }
        U32(dims[0]); U32(dims[1]);
        F64(spacing[0]); F64(spacing[1]); F64(origin[0]); F64(origin[1]);
        U32(dims[2]);
        foreach (var w in weights) F32(w);
        if (v2 is { } v) { U32(v.DataType); F32(v.Gamma); F32(v.Min); F32(v.Max); }
        b.AddRange(data);
        return [.. b];
    }

    private static byte[] Floats(ByteOrder order, float[] gains)
    {
        var b = new List<byte>(4 * gains.Length);
        foreach (var g in gains)
        {
            var v = BitConverter.SingleToUInt32Bits(g);
            if (order == ByteOrder.Little) for (var i = 0; i < 4; i++) b.Add((byte)(v >> (8 * i)));
            else for (var i = 3; i >= 0; i--) b.Add((byte)(v >> (8 * i)));
        }
        return [.. b];
    }

    /// <summary>2×2 表格、每表 4 點：(r, c) 表為 base·[1, 2, 3, 4]，base 依序 1..4（上游 grid）。</summary>
    private static float[] Grid()
    {
        var g = new List<float>(16);
        for (var t = 0; t < 4; t++) for (var i = 1; i <= 4; i++) g.Add((t + 1) * i);
        return [.. g];
    }

    private static GainTableMap MakeMap(int v, int h, int n, double sv, double sh, double ov, double oh, float[] weights, float[] gains, float gamma = 1f) =>
        new()
        {
            PointsV = v, PointsH = h, PointsN = n,
            SpacingV = sv, SpacingH = sh, OriginV = ov, OriginH = oh,
            Weights = (float[])weights.Clone(), Gamma = gamma, Gains = (float[])gains.Clone(),
        };

    [Fact]
    public void Version2_StorageTypesAndGamma()
    {
        var order = ByteOrder.Little;
        GainTableMap? V2(uint dt, float gamma, byte[] data) =>
            GainTableMap.Parse(Tag(order, [1, 1, 4], [1.0, 1.0], [0.0, 0.0], Green, (dt, gamma, 0.5f, 2.5f), data), order, true);
        // u8：0 → GainMin，255 → GainMax
        var m = V2(0, 1f, [0, 51, 255, 255]);
        Assert.NotNull(m);
        var want = new[] { 0.5f, 0.9f, 2.5f, 2.5f };
        for (var i = 0; i < 4; i++) Assert.True(MathF.Abs(m!.Gains[i] - want[i]) < 1e-6f, $"gains[{i}] = {m!.Gains[i]}");
        // u16
        m = V2(1, 1f, [0, 0, 0xff, 0xff, 0, 0, 0xff, 0xff]);
        Assert.NotNull(m);
        Assert.Equal([0.5f, 2.5f, 0.5f, 2.5f], m!.Gains);
        // f16 忽略 GainMin/GainMax：1.0、2.0、0.5、0
        m = V2(2, 1f, [0x00, 0x3c, 0x00, 0x40, 0x00, 0x38, 0, 0]);
        Assert.NotNull(m);
        Assert.Equal([1f, 2f, 0.5f, 0f], m!.Gains);
        // f32 帶 gamma 2
        m = V2(3, 2f, Floats(order, [1f, 2f, 3f, 4f]));
        Assert.NotNull(m);
        Assert.Equal([1f, 2f, 3f, 4f], m!.Gains);
        Assert.Equal(2f, m!.Gamma);
        // 未知資料型別、gamma 超界、長度不符：忽略
        Assert.Null(V2(4, 1f, [0, 0, 0, 0]));
        Assert.Null(V2(0, 0.1f, [0, 0, 0, 0]));
        Assert.Null(V2(0, 5f, [0, 0, 0, 0]));
        Assert.Null(V2(1, 1f, [0, 0, 0, 0]));
        // 整數負值域
        var neg = Tag(order, [1, 1, 4], [1.0, 1.0], [0.0, 0.0], Green, (0, 1f, -1f, 1f), [0, 0, 0, 0]);
        Assert.Null(GainTableMap.Parse(neg, order, true));
    }

    [Fact]
    public void MalformedTagsAreRejected()
    {
        var order = ByteOrder.Big;
        var good = Tag(order, [2, 2, 4], [0.5, 0.5], [0.25, 0.25], Green, null, Floats(order, Grid()));
        Assert.NotNull(GainTableMap.Parse(good, order, false));
        for (var n = 0; n < good.Length; n++)
            Assert.Null(GainTableMap.Parse(good.AsSpan(0, n), order, false));
        var longer = good.Concat([(byte)0]).ToArray();
        Assert.Null(GainTableMap.Parse(longer, order, false));
        // 以 v2 讀 v1 位元組：檔頭不足以容納資料
        Assert.Null(GainTableMap.Parse(good, order, true));
        GainTableMap? Bad(uint[] dims, double[] spacing, double[] origin, float[] gains) =>
            GainTableMap.Parse(Tag(order, dims, spacing, origin, Green, null, Floats(order, gains)), order, false);
        var g = Grid(); g[5] = float.NaN;
        Assert.Null(Bad([2, 2, 4], [0.5, 0.5], [0.0, 0.0], g));
        g[5] = -1f;
        Assert.Null(Bad([2, 2, 4], [0.5, 0.5], [0.0, 0.0], g));
        g[5] = float.PositiveInfinity;
        Assert.Null(Bad([2, 2, 4], [0.5, 0.5], [0.0, 0.0], g));
        Assert.Null(Bad([2, 2, 4], [0.0, 0.5], [0.0, 0.0], Grid()));
        Assert.Null(Bad([2, 2, 4], [0.5, double.NaN], [0.0, 0.0], Grid()));
        Assert.Null(Bad([2, 2, 4], [0.5, 0.5], [double.PositiveInfinity, 0.0], Grid()));
        Assert.Null(Bad([0, 2, 4], [0.5, 0.5], [0.0, 0.0], []));
        Assert.Null(Bad([1, 1, 0], [0.5, 0.5], [0.0, 0.0], []));
        // 巨大維度在配置前拒收
        Assert.Null(Bad([uint.MaxValue, uint.MaxValue, uint.MaxValue], [0.5, 0.5], [0.0, 0.0], Grid()));
        Assert.Null(Bad([4096, 4096, 1], [0.5, 0.5], [0.0, 0.0], Grid()));
        Assert.Null(Bad([1, 1, uint.MaxValue], [0.5, 0.5], [0.0, 0.0], Grid()));
        // 單表不需要間距
        Assert.NotNull(Bad([1, 1, 2], [0.0, 0.0], [0.0, 0.0], [1f, 2f]));
        var w = (float[])Green.Clone(); w[3] = float.NaN;
        Assert.Null(GainTableMap.Parse(Tag(order, [1, 1, 1], [1.0, 1.0], [0.0, 0.0], w, null, Floats(order, [1f])), order, false));
    }

    [Fact]
    public void ByteOrderIsTheFilesWithBigEndianFallback()
    {
        byte[] V1(ByteOrder o) => Tag(o, [2, 2, 4], [0.5, 0.5], [0.25, 0.25], Green, null, Floats(o, Grid()));
        var little = V1(ByteOrder.Little);
        var big = V1(ByteOrder.Big);
        // 依檔案位元組序讀取
        var parsedLittle = GainTableMap.Parse(little, ByteOrder.Little, false);
        Assert.NotNull(parsedLittle);
        Assert.Equal(Grid(), parsedLittle!.Gains);
        Assert.Null(GainTableMap.Parse(little, ByteOrder.Big, false));
        // 小端檔中的大端負載：fallback
        Assert.Equal(parsedLittle!.Gains, GainTableMap.Parse(big, ByteOrder.Little, false)!.Gains);
        Assert.Equal(parsedLittle!.Gains, GainTableMap.Parse(big, ByteOrder.Big, false)!.Gains);
    }

    [Fact]
    public void WritesWhatItReads()
    {
        foreach (var order in new[] { ByteOrder.Big, ByteOrder.Little })
        {
            var m = GainTableMap.Parse(Tag(order, [2, 2, 4], [0.5, 0.5], [0.25, 0.25], Green, null, Floats(order, Grid())), order, false)!;
            var (v2, b) = m.ToBytes(order);
            Assert.False(v2);
            Assert.Equal(m.Gains, GainTableMap.Parse(b, order, false)!.Gains);
            var g = MakeMap(m.PointsV, m.PointsH, m.PointsN, m.SpacingV, m.SpacingH, m.OriginV, m.OriginH, m.Weights, m.Gains, 2f);
            var (v2b, bb) = g.ToBytes(order);
            Assert.True(v2b);
            var back = GainTableMap.Parse(bb, order, true)!;
            Assert.Equal(2f, back.Gamma);
            Assert.Equal(g.Gains, back.Gains);
        }
    }

    [Fact]
    public void GainFollowsTableInputAtInputTimesN()
    {
        // 單表 3 點：index = input × 3（DNG 1.7.1），夾到最後一點
        var m = MakeMap(1, 1, 3, 1.0, 1.0, 0.0, 0.0, Luma, [1f, 2f, 3f]);
        Assert.Equal(1f, m.Gain(0f, 0f, 0f, 0.5, 0.5));
        Assert.True(MathF.Abs(m.Gain(1f / 3f, 1f / 3f, 1f / 3f, 0.5, 0.5) - 2f) < 1e-5f);
        Assert.True(MathF.Abs(m.Gain(1f / 6f, 1f / 6f, 1f / 6f, 0.5, 0.5) - 1.5f) < 1e-5f);
        Assert.True(MathF.Abs(m.Gain(0.9f, 0.9f, 0.9f, 0.5, 0.5) - 3f) < 1e-5f);
        // min/max 權重與 gamma：input = max(R, G, B)²，兩點
        m = MakeMap(1, 1, 2, 1.0, 1.0, 0.0, 0.0, [0f, 0f, 0f, 0f, 1f], [1f, 3f], 2f);
        // max 0.5 → 0.25 → index 0.5 → ×2
        Assert.True(MathF.Abs(m.Gain(0.1f, 0.5f, 0.2f, 0.0, 0.0) - 2f) < 1e-5f);
    }

    [Fact]
    public void TablesInterpolateAcrossGridAndReplicateAtEdges()
    {
        // 1×2 常數表：x = 0.25（×1）與 x = 0.75（×3）
        var m = MakeMap(1, 2, 1, 1.0, 0.5, 0.0, 0.25, Luma, [1f, 3f]);
        float G(double x) => m.Gain(0.2f, 0.2f, 0.2f, x, 0.5);
        Assert.True(MathF.Abs(G(0.25) - 1f) < 1e-5f && MathF.Abs(G(0.5) - 2f) < 1e-5f && MathF.Abs(G(0.75) - 3f) < 1e-5f);
        Assert.True(MathF.Abs(G(0.0) - 1f) < 1e-5f && MathF.Abs(G(1.0) - 3f) < 1e-5f);
        Assert.True(MathF.Abs(G(-10.0) - 1f) < 1e-5f && MathF.Abs(G(10.0) - 3f) < 1e-5f);
        // 垂直亦然：2×1，y = 0（×1）與 y = 1（×2）
        m = MakeMap(2, 1, 1, 1.0, 1.0, 0.0, 0.0, Luma, [1f, 2f]);
        Assert.True(MathF.Abs(m.Gain(0.2f, 0.2f, 0.2f, 0.5, 0.25) - 1.25f) < 1e-5f);
    }

    [Fact]
    public void HandBuiltMapsNeverPanic()
    {
        var m = MakeMap(int.MaxValue, 1, 1, 1.0, 1.0, 0.0, 0.0, Luma, [2f]);
        Assert.Equal(1f, m.Gain(0.5f, 0.5f, 0.5f, 0.5, double.PositiveInfinity));
        foreach (var (v, h, n, len) in new[] { (int.MaxValue, 1, 1, 1), (1, int.MaxValue, int.MaxValue, 3), (0, 0, 0, 0), (2, 2, 0, 0), (1, 1, 2, 1) })
        {
            m = MakeMap(v, h, n, 1.0, 1.0, 0.0, 0.0, Luma, Enumerable.Repeat(2f, len).ToArray());
            foreach (var spacing in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, 1e-300 })
            {
                m.SpacingV = spacing; m.SpacingH = spacing;
                foreach (var p in new[] { new[] { 0.5f, 0.5f, 0.5f }, new[] { float.NaN, float.NaN, float.NaN }, new[] { float.PositiveInfinity, 0f, -1f }, new[] { 1e30f, 1e30f, 1e30f } })
                    foreach (var (x, y) in new[] { (0.5, 0.5), (double.PositiveInfinity, double.NegativeInfinity), (double.NaN, double.NaN) })
                        Assert.True(float.IsFinite(m.Gain(p[0], p[1], p[2], x, y)));
            }
        }
        m = MakeMap(1, 1, 2, 1e-300, 1e-300, 0.0, 0.0, [float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue], [1f, 4f]);
        Assert.True(float.IsFinite(m.Gain(1e30f, 1e30f, 1e30f, double.PositiveInfinity, double.NegativeInfinity)));
    }

    [Fact]
    public void PlacementUndoesCropAndOrientation()
    {
        // 來源為有效區右半，順時針轉 90°：定向後左上來自裁切左下
        var p = new SourcePlacement(0.5, 0.0, 0.5, 1.0, Orientation.Rotate90);
        var (x, y) = p.Active(0.0, 0.0);
        Assert.True(Math.Abs(x - 0.5) < 1e-12 && Math.Abs(y - 1.0) < 1e-12, $"{x} {y}");
        (x, y) = p.Active(1.0, 0.0);
        Assert.True(Math.Abs(x - 0.5) < 1e-12 && Math.Abs(y) < 1e-12, $"{x} {y}");
        var plain = new SourcePlacement(0.0, 0.0, 1.0, 1.0, Orientation.Normal);
        Assert.Equal((0.25, 0.75), plain.Active(0.25, 0.75));
    }

    [Fact]
    public void DngDecode_RetainsGainMapWithV2Precedence()
    {
        // raw IFD 帶 v1，IFD0 帶損毀的 v2：v2 損毀應落到 v1
        var v1 = Tag(ByteOrder.Little, [1, 1, 2], [1.0, 1.0], [0.0, 0.0], [1f, 0f, 0f, 0f, 0f], null, Floats(ByteOrder.Little, [1f, 2f]));
        var raw = DngFile(ifd0Extra: [(Tags.ProfileGainTableMap2, new byte[] { 1, 2, 3 })], rawExtra: [(Tags.ProfileGainTableMap, v1)]);
        var decoded = RawDecoder.Decode(raw);
        Assert.NotNull(decoded.Color.Profile.GainTableMap);
        Assert.Equal([1f, 2f], decoded.Color.Profile.GainTableMap!.Gains);
        // IFD0 的有效 v2 優先於 raw IFD 的 v1
        var v2 = Tag(ByteOrder.Little, [1, 1, 2], [1.0, 1.0], [0.0, 0.0], [1f, 0f, 0f, 0f, 0f], (3, 2f, 0f, 1f), Floats(ByteOrder.Little, [3f, 4f]));
        raw = DngFile(ifd0Extra: [(Tags.ProfileGainTableMap2, v2)], rawExtra: [(Tags.ProfileGainTableMap, v1)]);
        decoded = RawDecoder.Decode(raw);
        Assert.Equal(2f, decoded.Color.Profile.GainTableMap!.Gamma);
        Assert.Equal([3f, 4f], decoded.Color.Profile.GainTableMap!.Gains);
        // 兩者皆損毀：忽略
        raw = DngFile(ifd0Extra: null, rawExtra: [(Tags.ProfileGainTableMap, new byte[] { 1, 2, 3 })]);
        decoded = RawDecoder.Decode(raw);
        Assert.Null(decoded.Color.Profile.GainTableMap);
    }

    [Fact]
    public void DngDecode_GainMapSelectsEachPriorityBeforeFallingBack()
    {
        static byte[] Map(float value, bool v2) =>
            Tag(ByteOrder.Little, [1, 1, 2], [1.0, 1.0], [0.0, 0.0], [1f, 0f, 0f, 0f, 0f],
                v2 ? (3, 2f, 0f, 1f) : null, Floats(ByteOrder.Little, [value, value]));

        var rootV2 = Map(2f, true);
        var rawV2 = Map(3f, true);
        var rawV1 = Map(4f, false);
        var rootV1 = Map(5f, false);
        ( (ushort Tag, byte[] Value)[] root, (ushort Tag, byte[] Value)[] raw, float expected)[] cases =
        [
            ([(Tags.ProfileGainTableMap2, rootV2), (Tags.ProfileGainTableMap, rootV1)],
             [(Tags.ProfileGainTableMap2, rawV2), (Tags.ProfileGainTableMap, rawV1)], 2f),
            ([(Tags.ProfileGainTableMap, rootV1)],
             [(Tags.ProfileGainTableMap2, rawV2), (Tags.ProfileGainTableMap, rawV1)], 3f),
            ([(Tags.ProfileGainTableMap, rootV1)], [(Tags.ProfileGainTableMap, rawV1)], 4f),
            ([(Tags.ProfileGainTableMap, rootV1)], [], 5f),
        ];
        foreach (var (root, raw, expected) in cases)
        {
            var map = RawDecoder.Decode(DngFile(root, raw)).Color.Profile.GainTableMap;
            Assert.NotNull(map);
            Assert.Equal(expected, map.Gains[0]);
        }
        Assert.Null(RawDecoder.Decode(DngFile(null, null)).Color.Profile.GainTableMap);
    }

    [Fact]
    public void ProRawGainMap_ParsesOracleDimensions()
    {
        var oracle = Environment.GetEnvironmentVariable("FILMBATCH_RAW_ORACLE") is { Length: > 0 } dir
            ? Path.Combine(dir, "stages", "dng-apple-iphone12pro-proraw", "info.json") : null;
        Assert.SkipUnless(oracle is not null && File.Exists(oracle), "FILMBATCH_RAW_ORACLE 缺少 ProRAW 參攷。");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(oracle!));
        var g = doc.RootElement.GetProperty("info").GetProperty("color").GetProperty("profile").GetProperty("gain_table_map");
        var map = new GainTableMap
        {
            PointsV = g.GetProperty("points_v").GetInt32(),
            PointsH = g.GetProperty("points_h").GetInt32(),
            PointsN = g.GetProperty("points_n").GetInt32(),
            SpacingV = g.GetProperty("spacing_v").GetDouble(),
            SpacingH = g.GetProperty("spacing_h").GetDouble(),
            OriginV = g.GetProperty("origin_v").GetDouble(),
            OriginH = g.GetProperty("origin_h").GetDouble(),
            Weights = g.GetProperty("weights").EnumerateArray().Select(v => v.GetSingle()).ToArray(),
            Gamma = g.GetProperty("gamma").GetSingle(),
            Gains = g.GetProperty("gains").EnumerateArray().Select(v => v.GetSingle()).ToArray(),
        };
        Assert.Equal((6, 8, 257), (map.PointsV, map.PointsH, map.PointsN));
        Assert.Equal(6 * 8 * 257, map.Gains.Length);
        Assert.All(map.Gains, x => Assert.True(float.IsFinite(x) && x >= 0f));
        var eval = map.Prepare();
        Assert.True(eval.HasValue);
        // 上游 oracle 首增益（線性查表起點附近的表格值）
        Assert.True(MathF.Abs(map.Gains[0] - 2.283073663711548f) < 1e-5f, $"gains[0] = {map.Gains[0]}");
        var gain = map.Gain(0.2f, 0.2f, 0.2f, 0.5, 0.5);
        Assert.True(float.IsFinite(gain) && gain >= 0f);
        // 序列化往返保持位元組（gamma = 1 → v1）
        var (version2, bytes) = map.ToBytes(ByteOrder.Little);
        Assert.False(version2);
        Assert.Equal(map.Gains, GainTableMap.Parse(bytes, ByteOrder.Little, false)!.Gains);
    }

    private static byte[] DngFile((ushort Tag, byte[] Value)[]? ifd0Extra, (ushort Tag, byte[] Value)[]? rawExtra)
    {
        const int W = 8, H = 8;
        var raw = new IfdBuilder();
        raw.Set(Tags.NewSubfileType, new TiffValue.Long([0]));
        raw.Set(Tags.ImageWidth, new TiffValue.Long([W]));
        raw.Set(Tags.ImageLength, new TiffValue.Long([H]));
        raw.Set(Tags.BitsPerSample, new TiffValue.Short([16]));
        raw.Set(Tags.Compression, new TiffValue.Short([1]));
        raw.Set(Tags.Photometric, new TiffValue.Short([TiffPhotometric.Cfa]));
        raw.Set(Tags.SamplesPerPixel, new TiffValue.Short([1]));
        raw.Set(Tags.CfaRepeatPatternDim, new TiffValue.Short([2, 2]));
        raw.Set(Tags.CfaPatternEp, new TiffValue.Byte([0, 1, 1, 2]));
        raw.SetImage(new ImageData.Strips((uint)H, [new byte[W * H * 2]]));
        if (rawExtra is not null) foreach (var (tag, value) in rawExtra) raw.Set(tag, new TiffValue.Undefined(value));
        var ifd0 = new IfdBuilder();
        ifd0.Set(Tags.ImageWidth, new TiffValue.Long([W]));
        ifd0.Set(Tags.ImageLength, new TiffValue.Long([H]));
        ifd0.Set(Tags.DngVersion, new TiffValue.Byte([1, 7, 0, 0]));
        if (ifd0Extra is not null) foreach (var (tag, value) in ifd0Extra) ifd0.Set(tag, new TiffValue.Undefined(value));
        ifd0.AddSubIfd(raw);
        return new TiffWriter().Write([ifd0]);
    }
}
