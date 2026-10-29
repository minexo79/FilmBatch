// Standalone DCP（相機描述檔）嚴格 parser 的行為測試（D1／AC-DCP-01）。
// 合成 fixture 全部自創矩陣、表格與機型；期望數值為手算字面量，不由受測程式推出。
// 涵蓋兩種 endian、完整受限 profile、截斷／重複／重疊／未知 tag 與型別、
// 矩陣與光源配對、HSV 與 tone 驗證、enum／HDR 值與資源上限。
using System.Text;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Tests.Color;

public sealed class DcpProfileTests
{
    // ---------------------------------------------------------------------
    // 獨立、有界的合成 DCP/TIFF 寫入器（不經產品 parser；手工、直接佈局）。
    // ---------------------------------------------------------------------
    private sealed class DcpBuilder
    {
        private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[]? Data, uint? Field, string? Shared);
        private readonly ByteOrder _order;
        private readonly List<Entry> _entries = [];
        private readonly Dictionary<string, uint> _sharedOffsets = new();

        public DcpBuilder(ByteOrder order) => _order = order;
        public ushort Magic = 0x4352;
        public uint IfdOffset = 8;
        public uint NextIfd;
        public ushort? DeclaredCount;
        public int TailPad;
        /// <summary>一般項目；payload 為原始值位元組（≤4 會 inline）。count 可與 payload 不一致以製作畸形檔。</summary>
        public DcpBuilder Add(ushort tag, FieldType type, uint count, byte[] payload)
        {
            _entries.Add(new Entry(tag, (ushort)type, count, payload, null, null));
            return this;
        }
        /// <summary>手動指定 4 位元組 value/offset 欄位（製作越界、重疊或未知型別）。</summary>
        public DcpBuilder AddRaw(ushort tag, ushort type, uint count, uint field)
        {
            _entries.Add(new Entry(tag, type, count, null, field, null));
            return this;
        }
        /// <summary>兩個項目共享同一 offset 區域（重疊 offset 測試）。</summary>
        public DcpBuilder AddShared(ushort tag, FieldType type, uint count, byte[] payload, string key)
        {
            _entries.Add(new Entry(tag, (ushort)type, count, payload, null, key));
            return this;
        }

        public byte[] U16s(params ushort[] v)
        {
            var b = new byte[v.Length * 2];
            for (var i = 0; i < v.Length; i++)
                if (_order == ByteOrder.Little) { b[i * 2] = (byte)v[i]; b[i * 2 + 1] = (byte)(v[i] >> 8); }
                else { b[i * 2] = (byte)(v[i] >> 8); b[i * 2 + 1] = (byte)v[i]; }
            return b;
        }
        public byte[] U32s(params uint[] v)
        {
            var b = new byte[v.Length * 4];
            for (var i = 0; i < v.Length; i++) Put32(b, i * 4, v[i]);
            return b;
        }
        public byte[] SRationals(params (int Num, int Den)[] v)
        {
            var b = new byte[v.Length * 8];
            for (var i = 0; i < v.Length; i++) { Put32(b, i * 8, unchecked((uint)v[i].Num)); Put32(b, i * 8 + 4, unchecked((uint)v[i].Den)); }
            return b;
        }
        public byte[] Floats(params float[] v)
        {
            var b = new byte[v.Length * 4];
            for (var i = 0; i < v.Length; i++) Put32(b, i * 4, BitConverter.SingleToUInt32Bits(v[i]));
            return b;
        }
        public byte[] Text(string s) => Encoding.UTF8.GetBytes(s + "\0");
        public byte[] DynamicRange(ushort version, ushort range, float hint)
        {
            var b = new byte[8];
            var u = U16s(version, range);
            Array.Copy(u, b, 4);
            Put32(b, 4, BitConverter.SingleToUInt32Bits(hint));
            return b;
        }
        private void Put32(byte[] b, int at, uint v)
        {
            if (_order == ByteOrder.Little) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
            else { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
        }

        public byte[] Build()
        {
            var output = new List<byte>();
            void W16(ushort v) { output.AddRange(U16s(v)); }
            void W32(uint v)
            {
                var t = new byte[4];
                Put32(t, 0, v);
                output.AddRange(t);
            }
            output.Add((byte)(_order == ByteOrder.Little ? 'I' : 'M'));
            output.Add((byte)(_order == ByteOrder.Little ? 'I' : 'M'));
            W16(Magic);
            W32(IfdOffset);
            while (output.Count < IfdOffset) output.Add(0);
            W16(DeclaredCount ?? (ushort)_entries.Count);
            // out-of-line value offset 由 IFD 結尾往後排。
            var fields = new uint[_entries.Count];
            var payloads = new byte[_entries.Count][];
            long valueBase = IfdOffset + 2 + _entries.Count * 12 + 4;
            long pos = valueBase;
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                payloads[i] = e.Data ?? [];
                if (e.Field is { } f) { fields[i] = f; continue; }
                if (payloads[i].Length <= 4) { fields[i] = uint.MaxValue; continue; } // inline 標記
                if (e.Shared is { } key)
                {
                    if (!_sharedOffsets.TryGetValue(key, out var off)) { off = (uint)pos; _sharedOffsets[key] = off; pos += payloads[i].Length; }
                    fields[i] = off;
                }
                else { fields[i] = (uint)pos; pos += payloads[i].Length; }
            }
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                W16(e.Tag);
                W16(e.Type);
                W32(e.Count);
                if (e.Field is { } f) W32(f);
                else if (fields[i] == uint.MaxValue)
                {
                    var inline = new byte[4];
                    Array.Copy(payloads[i], inline, Math.Min(4, payloads[i].Length));
                    output.AddRange(inline);
                }
                else W32(fields[i]);
            }
            W32(NextIfd);
            // 依 entries 順序寫出 out-of-line payloads（shared 只寫一次）。
            var written = new HashSet<string>();
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Field is not null || payloads[i].Length <= 4) continue;
                if (e.Shared is { } key && !written.Add(key)) continue;
                output.AddRange(payloads[i]);
            }
            for (var i = 0; i < TailPad; i++) output.Add(0);
            return output.ToArray();
        }
    }

    private static DcpBuilder B(ByteOrder o = ByteOrder.Little) => new(o);

    /// <summary>有取消語意的讀取；所有非取消專項測試都帶入 <see cref="TestContext"/> 的 token。</summary>
    private static DcpProfile Read(ReadOnlyMemory<byte> source) =>
        DcpProfile.Read(source, TestContext.Current.CancellationToken);

    // 非 identity 且兩組不同的 3×3 SRATIONAL 矩陣（手算字面量）。
    private static readonly (int Num, int Den)[] Cm1 =
        [(1, 2), (1, 4), (-1, 8), (1, 10), (1, 1), (1, 20), (1, 50), (-1, 10), (3, 4)];
    private static readonly (int Num, int Den)[] Cm2 =
        [(3, 5), (1, 5), (-1, 10), (3, 20), (9, 10), (2, 25), (3, 100), (-2, 25), (4, 5)];
    private static readonly (int Num, int Den)[] Fm1 =
        [(1, 1), (0, 1), (0, 1), (0, 1), (1, 1), (0, 1), (1, 4), (1, 4), (1, 2)];
    private static readonly (int Num, int Den)[] Fm2 =
        [(4, 5), (1, 10), (1, 10), (0, 1), (7, 10), (1, 5), (1, 5), (1, 4), (11, 20)];

    private static double[] Of((int Num, int Den)[] v) => v.Select(x => (double)x.Num / x.Den).ToArray();

    /// <summary>H×S×V 的非對稱表：s＝0 的 value scale 必為 1，其餘每格值皆不同（順序敏感）。</summary>
    private static float[] Hsv(int h, int s, int v, float scale)
    {
        var data = new float[h * s * v * 3];
        var cell = 0;
        for (var vi = 0; vi < v; vi++)
            for (var hi = 0; hi < h; hi++)
                for (var si = 0; si < s; si++, cell++)
                {
                    data[cell * 3] = si == 0 ? 1.5f : -4f + cell * scale;
                    data[cell * 3 + 1] = 0.8f + cell * 0.01f * scale;
                    data[cell * 3 + 2] = si == 0 ? 1f : 0.9f + cell * 0.005f * scale;
                }
        return data;
    }

    /// <summary>最小合法 profile：UniqueCameraModel＋CalibrationIlluminant1＋ColorMatrix1。</summary>
    private static DcpBuilder Minimal(ByteOrder o) => B(o)
        .Add(Tags.UniqueCameraModel, FieldType.Ascii, (uint)"Acme Model A".Length + 1, B(o).Text("Acme Model A"))
        .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(o).SRationals(Cm1))
        .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(o).U16s(21));

    /// <summary>受限集合全開的 profile：雙光源、雙 HSV、Look、tone、metadata 與 SDR dynamic range。</summary>
    private static DcpBuilder Full(ByteOrder o)
    {
        var b = Minimal(o);
        b.Add(Tags.CalibrationIlluminant2, FieldType.Short, 1, b.U16s(17))
            .Add(Tags.ColorMatrix2, FieldType.SRational, 9, b.SRationals(Cm2))
            .Add(Tags.ForwardMatrix1, FieldType.SRational, 9, b.SRationals(Fm1))
            .Add(Tags.ForwardMatrix2, FieldType.SRational, 9, b.SRationals(Fm2))
            .Add(Tags.ProfileName, FieldType.Ascii, (uint)"Unit Test Profile".Length + 1, b.Text("Unit Test Profile"))
            .Add(Tags.ProfileCopyright, FieldType.Ascii, (uint)"Copyright Acme 2026".Length + 1, b.Text("Copyright Acme 2026"))
            .Add(Tags.ProfileCalibrationSignature, FieldType.Ascii, 6, b.Text("Sig-A"))
            .Add(ProfileGroupNameTag, FieldType.Ascii, 8, b.Text("Group-9"))
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, b.U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, b.Floats(Hsv1))
            .Add(Tags.ProfileHueSatMapData2, FieldType.Float, 72, b.Floats(Hsv2))
            .Add(Tags.ProfileHueSatMapEncoding, FieldType.Long, 1, b.U32s(1))
            .Add(Tags.ProfileLookTableDims, FieldType.Long, 3, b.U32s(3, 2, 2))
            .Add(Tags.ProfileLookTableData, FieldType.Float, 36, b.Floats(Look))
            .Add(Tags.ProfileLookTableEncoding, FieldType.Long, 1, b.U32s(1))
            .Add(Tags.ProfileToneCurve, FieldType.Float, 8, b.Floats(Tone))
            .Add(Tags.BaselineExposureOffset, FieldType.SRational, 1, b.SRationals((-1, 2)))
            .Add(Tags.ProfileEmbedPolicy, FieldType.Long, 1, b.U32s(2))
            .Add(Tags.DefaultBlackRender, FieldType.Long, 1, b.U32s(1))
            .Add(ProfileDynamicRangeTag, FieldType.Undefined, 8, b.DynamicRange(1, 0, 0.8f));
        return b;
    }

    private const ushort ProfileDynamicRangeTag = 52551;
    private const ushort ProfileGroupNameTag = 52552;
    private const ushort RgbTablesTag = 52543;
    private const ushort ProfileHueSatMapData3Tag = 52537;
    private const ushort ExtraCameraProfilesTag = 50933;

    private static readonly float[] Hsv1 = Hsv(4, 3, 2, 0.25f);
    private static readonly float[] Hsv2 = Hsv(4, 3, 2, 0.11f);
    private static readonly float[] Look = Hsv(3, 2, 2, 0.4f);
    private static readonly float[] Tone = [0f, 0f, 0.3f, 0.12f, 0.7f, 0.85f, 1f, 1f];

    private static void AssertMatrix(DcpMatrix m, (int Num, int Den)[] expected)
    {
        Assert.Equal(expected.Length, m.Rationals.Length);
        var rats = m.Rationals;
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Num, rats[i].Num);
            Assert.Equal(expected[i].Den, rats[i].Den);
        }
        var d = Of(expected);
        for (var i = 0; i < 9; i++)
            Assert.Equal(d[i], m.Matrix[i / 3, i % 3], 15);
    }

    [Theory]
    [InlineData(ByteOrder.Little)]
    [InlineData(ByteOrder.Big)]
    public void Byte_profile_name_and_unsigned_rational_offset_are_preserved(ByteOrder order)
    {
        var b = Minimal(order);
        var text = Encoding.UTF8.GetBytes("自創 profile\0");
        b.Add(Tags.ProfileName, FieldType.Byte, (uint)text.Length, text)
            .Add(Tags.BaselineExposureOffset, FieldType.Rational, 1, b.U32s(uint.MaxValue, 4000000000u));
        var p = Read(b.Build());
        Assert.Equal("自創 profile", p.ProfileName);
        Assert.Equal(new DcpRational(4294967295L, 4000000000L), p.BaselineExposureOffsetRational);
    }

    [Theory]
    [InlineData(Tags.ProfileHueSatMapDims)]
    [InlineData(Tags.ProfileLookTableDims)]
    public void Short_table_dimensions_are_rejected(ushort tag)
    {
        var b = Minimal(ByteOrder.Little);
        b.Add(tag, FieldType.Short, 3, b.U16s(4, 3, 2));
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    [Theory]
    [InlineData(Tags.ProfileHueSatMapEncoding)]
    [InlineData(Tags.ProfileLookTableEncoding)]
    [InlineData(Tags.ProfileEmbedPolicy)]
    [InlineData(Tags.DefaultBlackRender)]
    public void Short_enum_values_are_rejected(ushort tag)
    {
        var b = Minimal(ByteOrder.Little);
        b.Add(tag, FieldType.Short, 1, b.U16s(0));
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    [Theory]
    [InlineData(FieldType.Ascii)]
    [InlineData(FieldType.Byte)]
    public void Profile_text_rejects_invalid_utf8_and_missing_terminator(FieldType type)
    {
        var invalid = Minimal(ByteOrder.Little).Add(Tags.ProfileName, type, 3, [0xff, 0xfe, 0]);
        Assert.Throws<RawException>(() => Read(invalid.Build()));
        var missing = Minimal(ByteOrder.Little).Add(Tags.ProfileName, type, 4, [65, 66, 67, 68]);
        Assert.Throws<RawException>(() => Read(missing.Build()));
    }

    [Fact]
    public void Unsigned_exposure_offset_zero_denominator_is_rejected()
    {
        var b = Minimal(ByteOrder.Little);
        b.Add(Tags.BaselineExposureOffset, FieldType.Rational, 1, b.U32s(1, 0));
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    [Fact]
    public void Tone_pair_limit_is_checked_with_complete_payload()
    {
        var b = Minimal(ByteOrder.Little);
        var values = new float[131074];
        b.Add(Tags.ProfileToneCurve, FieldType.Float, (uint)values.Length, b.Floats(values));
        var error = Assert.Throws<RawException>(() => Read(b.Build()));
        Assert.Equal(RawErrorKind.Limit, error.Kind);
    }

    [Fact]
    public void Look_table_rejects_nonunit_zero_saturation_value_scale()
    {
        var b = Minimal(ByteOrder.Little);
        var bad = (float[])Look.Clone();
        bad[2] = 0.5f;
        b.Add(Tags.ProfileLookTableDims, FieldType.Long, 3, b.U32s(3, 2, 2))
            .Add(Tags.ProfileLookTableData, FieldType.Float, 36, b.Floats(bad));
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    // ---------------------------------------------------------------------
    // 兩種 endian 的完整解析
    // ---------------------------------------------------------------------
    [Theory]
    [InlineData(ByteOrder.Little)]
    [InlineData(ByteOrder.Big)]
    public void Full_profile_parses_in_both_byte_orders(ByteOrder order)
    {
        var p = Read(Full(order).Build());
        Assert.Equal("Acme Model A", p.UniqueCameraModel);
        Assert.Equal("Unit Test Profile", p.ProfileName);
        Assert.Equal("Copyright Acme 2026", p.ProfileCopyright);
        Assert.Equal("Sig-A", p.ProfileCalibrationSignature);
        Assert.Equal("Group-9", p.ProfileGroupName);
        Assert.Equal(2, p.ProfileEmbedPolicy);
        Assert.Equal(1, p.DefaultBlackRender);
        Assert.Equal((ushort)21, p.CalibrationIlluminant1);
        Assert.Equal((ushort)17, p.CalibrationIlluminant2);
        AssertMatrix(p.ColorMatrix1, Cm1);
        var cm2 = p.ColorMatrix2;
        Assert.NotNull(cm2);
        AssertMatrix(cm2!, Cm2);
        var fm1 = p.ForwardMatrix1;
        Assert.NotNull(fm1);
        AssertMatrix(fm1!, Fm1);
        var fm2 = p.ForwardMatrix2;
        Assert.NotNull(fm2);
        AssertMatrix(fm2!, Fm2);
        var h1 = p.HueSatMap1;
        Assert.NotNull(h1);
        Assert.Equal(4, h1!.HueDivisions);
        Assert.Equal(3, h1.SatDivisions);
        Assert.Equal(2, h1.ValDivisions);
        Assert.True(h1.SrgbValue);
        Assert.Equal(Hsv1, h1.Data.ToArray());
        var h2 = p.HueSatMap2;
        Assert.NotNull(h2);
        Assert.Equal(Hsv2, h2!.Data.ToArray());
        var look = p.LookTable;
        Assert.NotNull(look);
        Assert.Equal((3, 2, 2), (look!.HueDivisions, look.SatDivisions, look.ValDivisions));
        Assert.Equal(Look, look.Data.ToArray());
        var tone = p.ToneCurve;
        Assert.NotNull(tone);
        Assert.Equal(Tone, tone!.Points.ToArray());
        Assert.Equal(-0.5, p.BaselineExposureOffset);
        Assert.Equal(new DcpRational(-1, 2), p.BaselineExposureOffsetRational);
        var dr = p.DynamicRange;
        Assert.NotNull(dr);
        Assert.Equal(1, dr!.Version);
        Assert.Equal(0, dr.Range);
        Assert.Equal(0.8f, dr.HintMaxOutputValue);
    }

    [Fact]
    public void Table_order_is_preserved_exactly()
    {
        // 兩個格位資料相同但次序不同的表；次序變更會改查表結果，必須逐位元保留。
        var a = Hsv(4, 3, 2, 0.25f);
        var swapped = (float[])a.Clone();
        (swapped[12], swapped[21]) = (swapped[21], swapped[12]); // 兩個非零飽和位置互換
        var p1 = Read(Full(ByteOrder.Little).Build());
        Assert.Equal(a, p1.HueSatMap1!.Data.ToArray());
        var b2 = Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, B(ByteOrder.Little).Floats(swapped));
        var p2 = Read(b2.Build());
        Assert.Equal(swapped, p2.HueSatMap1!.Data.ToArray());
        Assert.NotEqual(a, p2.HueSatMap1!.Data.ToArray());
    }

    [Fact]
    public void Minimal_profile_uses_documented_defaults()
    {
        var p = Read(Minimal(ByteOrder.Little).Build());
        Assert.Equal("Acme Model A", p.UniqueCameraModel);
        Assert.Equal(0, p.ProfileEmbedPolicy);
        Assert.Null(p.DefaultBlackRender);
        Assert.Null(p.ColorMatrix2);
        Assert.Null(p.CalibrationIlluminant2);
        Assert.Null(p.ForwardMatrix1);
        Assert.Null(p.HueSatMap1);
        Assert.Null(p.HueSatMap2);
        Assert.Null(p.LookTable);
        Assert.Null(p.ToneCurve);
        Assert.Equal(0.0, p.BaselineExposureOffset);
        Assert.Null(p.BaselineExposureOffsetRational);
        Assert.Null(p.DynamicRange);
        Assert.Equal((ushort)21, p.CalibrationIlluminant1);
    }

    [Fact]
    public void Single_unknown_illuminant_code_is_allowed()
    {
        var only = B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm1))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(0));
        var p = Read(only.Build());
        Assert.Equal((ushort)0, p.CalibrationIlluminant1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Hue_sat_encoding_0_and_1_are_accepted(ushort encoding)
    {
        var b = Minimal(ByteOrder.Big)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Big).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, B(ByteOrder.Big).Floats(Hsv1));
        if (encoding != 0)
            b.Add(Tags.ProfileHueSatMapEncoding, FieldType.Long, 1, B(ByteOrder.Big).U32s(encoding));
        var p = Read(b.Build());
        Assert.Equal(encoding == 1, p.HueSatMap1!.SrgbValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Embed_policy_0_to_3_is_accepted(uint policy)
    {
        var p = Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileEmbedPolicy, FieldType.Long, 1, B(ByteOrder.Little).U32s(policy)).Build());
        Assert.Equal((int)policy, p.ProfileEmbedPolicy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Default_black_render_0_and_1_are_accepted(uint value)
    {
        var p = Read(Minimal(ByteOrder.Little)
            .Add(Tags.DefaultBlackRender, FieldType.Long, 1, B(ByteOrder.Little).U32s(value)).Build());
        Assert.Equal((int)value, p.DefaultBlackRender);
    }

    [Fact]
    public void Sdr_dynamic_range_hint_zero_means_no_hint()
    {
        var p = Read(Minimal(ByteOrder.Little)
            .Add(ProfileDynamicRangeTag, FieldType.Undefined, 8, B(ByteOrder.Little).DynamicRange(1, 0, 0f)).Build());
        Assert.NotNull(p.DynamicRange);
        Assert.Equal(0f, p.DynamicRange!.HintMaxOutputValue);
    }

    [Fact]
    public void Negative_signed_exposure_offset_is_preserved()
    {
        var p = Read(Minimal(ByteOrder.Little)
            .Add(Tags.BaselineExposureOffset, FieldType.SRational, 1, B(ByteOrder.Little).SRationals((-3, 4))).Build());
        Assert.Equal(-0.75, p.BaselineExposureOffset);
        Assert.Equal(new DcpRational(-3, 4), p.BaselineExposureOffsetRational);
    }

    [Fact]
    public void Parsed_values_are_independent_of_source_buffer_mutation()
    {
        var bytes = Full(ByteOrder.Little).Build();
        var p = Read(bytes);
        var before = p.HueSatMap1!.Data.ToArray();
        for (var i = 0; i < bytes.Length; i++) bytes[i] = 0xFF;
        Assert.Equal(before, p.HueSatMap1.Data.ToArray());
        Assert.Equal("Acme Model A", p.UniqueCameraModel);
    }

    [Fact]
    public void Materialized_copies_cannot_mutate_the_snapshot()
    {
        var p = Read(Full(ByteOrder.Little).Build());
        var snapshot = p.HueSatMap1!.Data.ToArray();
        var tone = p.ToneCurve!.Points.ToArray();
        // 由外部複製的陣列被修改，不影響 snapshot。
        var copy = p.HueSatMap1.Data.ToArray();
        copy[12] = 999f;
        var look = p.ToProfileLook();
        look.HueSatMap[0]!.Data[12] = -777f;
        look.ToneCurve!.Points[0] = 9f;
        Assert.Equal(snapshot, p.HueSatMap1.Data.ToArray());
        Assert.Equal(tone, p.ToneCurve.Points.ToArray());
    }

    // ---------------------------------------------------------------------
    // 標頭與 IFD 結構
    // ---------------------------------------------------------------------
    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { (byte)'I' })]
    [InlineData(new byte[] { (byte)'I', (byte)'I' })]
    [InlineData(new byte[] { (byte)'Z', (byte)'Z', 42, 0 })]
    public void Truncated_or_garbage_header_is_rejected(byte[] bytes) =>
        Assert.Throws<RawException>(() => Read(bytes));

    [Fact]
    public void Classic_tiff_magic_is_rejected()
    {
        var b = Minimal(ByteOrder.Little);
        b.Magic = 42;
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    [Fact]
    public void Big_tiff_magic_is_rejected()
    {
        var b = Minimal(ByteOrder.Little);
        b.Magic = 43;
        var error = Assert.Throws<RawException>(() => Read(b.Build()));
        Assert.Contains("BigTIFF", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_tiff_reader_still_rejects_dcp_magic()
    {
        var bytes = Minimal(ByteOrder.Little).Build();
        Assert.Throws<RawException>(() => TiffFile.Parse(bytes));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(7u)]
    public void Ifd_offset_inside_header_is_rejected(uint offset)
    {
        var b = Minimal(ByteOrder.Little);
        b.IfdOffset = offset;
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    [Fact]
    public void Ifd_offset_past_end_is_rejected()
    {
        var bytes = Minimal(ByteOrder.Little).Build();
        B(ByteOrder.Little).U32s((uint)bytes.Length + 1).CopyTo(bytes, 4);
        Assert.Throws<RawException>(() => Read(bytes));
    }

    [Fact]
    public void Every_truncation_prefix_is_rejected()
    {
        var bytes = Minimal(ByteOrder.Little).Build();
        for (var n = 0; n < bytes.Length; n++)
            Assert.Throws<RawException>(() => Read(bytes[..n]));
    }

    [Fact]
    public void Nonzero_next_ifd_offset_is_rejected()
    {
        var b = Minimal(ByteOrder.Little);
        b.NextIfd = 64;
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    [Fact]
    public void Truncated_entry_table_is_rejected()
    {
        var b = Minimal(ByteOrder.Little);
        b.DeclaredCount = 5; // 宣稱 5 項但檔案只有 3 個 entry
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    [Fact]
    public void Missing_next_ifd_field_is_rejected()
    {
        var bytes = Minimal(ByteOrder.Little).Build();
        // 截掉 4 位元組 next-offset：IFD 結尾前無法放 value offset，只有捨棄該欄位本身。
        var ifdEnd = 8 + 2 + 3 * 12;
        Assert.Throws<RawException>(() => Read(bytes[..ifdEnd]));
    }

    // ---------------------------------------------------------------------
    // 項目層級：未知 tag／型別／重複／越界／重疊
    // ---------------------------------------------------------------------
    [Fact]
    public void Unknown_tag_is_rejected()
    {
        var error = Assert.Throws<RawException>(() => Read(
            Minimal(ByteOrder.Little).Add(65000, FieldType.Short, 1, B(ByteOrder.Little).U16s(1)).Build()));
        Assert.Contains("65000", error.Message);
    }

    [Fact]
    public void Unknown_field_type_is_rejected()
    {
        var error = Assert.Throws<RawException>(() => Read(
            Minimal(ByteOrder.Little).AddRaw(Tags.ProfileName, 99, 1, 0).Build()));
        Assert.Contains("99", error.Message);
    }

    [Fact]
    public void Duplicate_tag_is_rejected()
    {
        var b = Minimal(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 4, B(ByteOrder.Little).Text("Dup"));
        var error = Assert.Throws<RawException>(() => Read(b.Build()));
        Assert.Contains("50708", error.Message);
    }

    [Fact]
    public void Value_offset_past_end_is_rejected() =>
        Assert.Throws<RawException>(() => Read(
            Minimal(ByteOrder.Little).AddRaw(Tags.ProfileName, (ushort)FieldType.Ascii, 8, 0xFFF0).Build()));

    [Fact]
    public void Overlapping_value_offsets_are_rejected()
    {
        var b = Minimal(ByteOrder.Little)
            .AddShared(Tags.ProfileName, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Shared\0"), "k")
            .AddShared(Tags.ProfileCopyright, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Shared\0"), "k");
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    [Fact]
    public void Value_offset_inside_ifd_is_rejected()
    {
        var b = Minimal(ByteOrder.Little)
            .AddRaw(Tags.ProfileName, (ushort)FieldType.Ascii, 8, 10);
        Assert.Throws<RawException>(() => Read(b.Build()));
    }

    [Theory]
    [InlineData((ushort)52529)] // CalibrationIlluminant3
    [InlineData((ushort)52530)] // CameraCalibration3
    [InlineData((ushort)52531)] // ColorMatrix3
    [InlineData((ushort)52532)] // ForwardMatrix3
    [InlineData(ProfileHueSatMapData3Tag)]
    [InlineData((ushort)52538)] // ReductionMatrix3
    public void Third_illuminant_tags_are_rejected(ushort tag) =>
        Assert.Throws<RawException>(() => Read(
            Minimal(ByteOrder.Little).Add(tag, FieldType.Short, 1, B(ByteOrder.Little).U16s(21)).Build()));

    [Theory]
    [InlineData((ushort)52533)] // IlluminantData1
    [InlineData((ushort)52534)] // IlluminantData2
    [InlineData((ushort)52535)] // IlluminantData3
    public void Custom_illuminant_tags_are_rejected(ushort tag) =>
        Assert.Throws<RawException>(() => Read(
            Minimal(ByteOrder.Little).Add(tag, FieldType.Undefined, 8, new byte[8]).Build()));

    [Theory]
    [InlineData((ushort)52525)] // ProfileGainTableMap
    [InlineData((ushort)52544)] // ProfileGainTableMap2
    public void Profile_gain_table_maps_are_rejected(ushort tag) =>
        Assert.Throws<RawException>(() => Read(
            Minimal(ByteOrder.Little).Add(tag, FieldType.Undefined, 16, new byte[16]).Build()));

    [Fact]
    public void Rgb_tables_tag_is_rejected()
    {
        var error = Assert.Throws<RawException>(() => Read(
            Minimal(ByteOrder.Little).Add(RgbTablesTag, FieldType.Undefined, 16, new byte[16]).Build()));
        Assert.Contains("52543", error.Message);
    }

    [Fact]
    public void Extra_camera_profiles_tag_is_rejected() =>
        Assert.Throws<RawException>(() => Read(
            Minimal(ByteOrder.Little).Add(ExtraCameraProfilesTag, FieldType.Long, 1, B(ByteOrder.Little).U32s(64)).Build()));

    // ---------------------------------------------------------------------
    // 型別與數量
    // ---------------------------------------------------------------------
    [Fact]
    public void Matrix_with_wrong_field_type_is_rejected()
    {
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix1, FieldType.Float, 9, B(ByteOrder.Little).Floats(new float[9]))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(21)).Build()));
    }

    [Theory]
    [InlineData(8u)]
    [InlineData(12u)]
    public void Matrix_with_wrong_count_is_rejected(uint count)
    {
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix1, FieldType.SRational, count, B(ByteOrder.Little).SRationals(Cm1))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(21)).Build()));
    }

    [Fact]
    public void Zero_denominator_in_matrix_is_rejected()
    {
        var bad = ((int Num, int Den)[])Cm1.Clone();
        bad[3] = (5, 0);
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(bad))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(21)).Build()));
    }

    [Fact]
    public void Unique_camera_model_of_wrong_type_is_rejected() =>
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Long, 1, B(ByteOrder.Little).U32s(7))
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm1))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(21)).Build()));

    [Fact]
    public void Baseline_exposure_offset_of_wrong_type_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.BaselineExposureOffset, FieldType.Long, 1, B(ByteOrder.Little).U32s(1)).Build()));

    [Fact]
    public void Baseline_exposure_offset_zero_denominator_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.BaselineExposureOffset, FieldType.SRational, 1, B(ByteOrder.Little).SRationals((1, 0))).Build()));

    [Fact]
    public void Missing_unique_camera_model_is_rejected() =>
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm1))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(21)).Build()));

    [Fact]
    public void Missing_color_matrix1_is_rejected() =>
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix2, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm2))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(21))
            .Add(Tags.CalibrationIlluminant2, FieldType.Short, 1, B(ByteOrder.Little).U16s(17)).Build()));

    [Fact]
    public void Missing_calibration_illuminant1_is_rejected() =>
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm1)).Build()));

    // ---------------------------------------------------------------------
    // 光源與矩陣配對
    // ---------------------------------------------------------------------
    [Fact]
    public void Color_matrix2_without_illuminant2_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ColorMatrix2, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm2)).Build()));

    [Fact]
    public void Illuminant2_without_color_matrix2_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.CalibrationIlluminant2, FieldType.Short, 1, B(ByteOrder.Little).U16s(17)).Build()));

    [Fact]
    public void Dual_illuminants_must_differ() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.CalibrationIlluminant2, FieldType.Short, 1, B(ByteOrder.Little).U16s(21))
            .Add(Tags.ColorMatrix2, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm2)).Build()));

    [Fact]
    public void Dual_illuminants_must_both_be_standard() =>
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm1))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(0))
            .Add(Tags.ColorMatrix2, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm2))
            .Add(Tags.CalibrationIlluminant2, FieldType.Short, 1, B(ByteOrder.Little).U16s(17)).Build()));

    [Theory]
    [InlineData((ushort)255)] // Other／自訂
    [InlineData((ushort)100)] // 未定義碼
    public void Unrecognised_illuminant_codes_are_rejected(ushort code) =>
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm1))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(code)).Build()));

    [Fact]
    public void Forward_matrix2_without_forward_matrix1_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.CalibrationIlluminant2, FieldType.Short, 1, B(ByteOrder.Little).U16s(17))
            .Add(Tags.ColorMatrix2, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm2))
            .Add(Tags.ForwardMatrix2, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Fm2)).Build()));

    [Fact]
    public void Forward_matrix2_without_color_matrix2_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ForwardMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Fm1))
            .Add(Tags.ForwardMatrix2, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Fm2)).Build()));

    [Fact]
    public void Singular_matrix_is_rejected()
    {
        var singular = new (int Num, int Den)[9];
        for (var i = 0; i < 9; i++) singular[i] = (1, 1); // 全 1：行列式 0
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(singular))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(21)).Build()));
    }

    [Fact]
    public void Ill_conditioned_matrix_is_rejected()
    {
        // 第三列為前兩列和加 1e-9：行列式 ~1e-9，低於病態門檻。
        var ill = new (int Num, int Den)[]
        {
            (1, 1), (0, 1), (0, 1), (0, 1), (1, 1), (0, 1),
            (1, 1), (1, 1), (1, 1_000_000_000),
        };
        Assert.Throws<RawException>(() => Read(B(ByteOrder.Little)
            .Add(Tags.UniqueCameraModel, FieldType.Ascii, 8, B(ByteOrder.Little).Text("Acme M0"))
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(ill))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, B(ByteOrder.Little).U16s(21)).Build()));
    }

    // ---------------------------------------------------------------------
    // HSV／Look 表
    // ---------------------------------------------------------------------
    [Fact]
    public void Hue_sat_data_without_dims_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, B(ByteOrder.Little).Floats(Hsv1)).Build()));

    [Fact]
    public void Hue_sat_dims_without_data_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2)).Build()));

    [Fact]
    public void Hue_sat_data2_without_data1_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.CalibrationIlluminant2, FieldType.Short, 1, B(ByteOrder.Little).U16s(17))
            .Add(Tags.ColorMatrix2, FieldType.SRational, 9, B(ByteOrder.Little).SRationals(Cm2))
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData2, FieldType.Float, 72, B(ByteOrder.Little).Floats(Hsv2)).Build()));

    [Fact]
    public void Hue_sat_map2_requires_second_illuminant() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, B(ByteOrder.Little).Floats(Hsv1))
            .Add(Tags.ProfileHueSatMapData2, FieldType.Float, 72, B(ByteOrder.Little).Floats(Hsv2)).Build()));

    [Fact]
    public void Hue_sat_data_count_must_match_dims() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 71, B(ByteOrder.Little).Floats(Hsv1[..71])).Build()));

    [Fact]
    public void Hue_sat_nonzero_hue_at_zero_saturation_is_accepted()
    {
        // hue 在零飽和列未定義但合法（Hsv fixture 在 s=0 放 1.5）；不應被多餘約束拒絕。
        var p = Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, B(ByteOrder.Little).Floats(Hsv1)).Build());
        Assert.Equal(Hsv1, p.HueSatMap1!.Data.ToArray());
    }

    [Fact]
    public void Hue_sat_non_unit_value_scale_at_zero_saturation_is_rejected()
    {
        var bad = (float[])Hsv1.Clone();
        bad[2] = 1.5f; // 第一格（v=0,h=0,s=0）的 value scale：DNG 要求恰為 1
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, B(ByteOrder.Little).Floats(bad)).Build()));
    }

    [Theory]
    [InlineData(0, 3, 2)]
    [InlineData(4, 1, 2)]
    [InlineData(4, 3, 0)]
    public void Illegal_hue_sat_dims_are_rejected(int h, int s, int v)
    {
        var cells = Math.Max(0, h) * Math.Max(0, s) * Math.Max(0, v);
        var data = new float[Math.Max(3, cells * 3)];
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s((ushort)h, (ushort)s, (ushort)v))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, (uint)data.Length, B(ByteOrder.Little).Floats(data)).Build()));
    }

    [Fact]
    public void Unknown_hue_sat_encoding_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, B(ByteOrder.Little).Floats(Hsv1))
            .Add(Tags.ProfileHueSatMapEncoding, FieldType.Long, 1, B(ByteOrder.Little).U32s(2)).Build()));

    [Fact]
    public void Non_finite_hue_sat_values_are_rejected()
    {
        var bad = (float[])Hsv1.Clone();
        bad[10] = float.NaN;
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, B(ByteOrder.Little).Floats(bad)).Build()));
        bad[10] = float.PositiveInfinity;
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(4, 3, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 72, B(ByteOrder.Little).Floats(bad)).Build()));
    }

    [Fact]
    public void Look_table_dims_without_data_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileLookTableDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(3, 2, 2)).Build()));

    [Fact]
    public void Look_table_unknown_encoding_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileLookTableDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(3, 2, 2))
            .Add(Tags.ProfileLookTableData, FieldType.Float, 36, B(ByteOrder.Little).Floats(Look))
            .Add(Tags.ProfileLookTableEncoding, FieldType.Long, 1, B(ByteOrder.Little).U32s(7)).Build()));

    // ---------------------------------------------------------------------
    // Tone curve
    // ---------------------------------------------------------------------
    [Fact]
    public void Tone_curve_missing_endpoints_is_rejected()
    {
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileToneCurve, FieldType.Float, 6, B(ByteOrder.Little).Floats(0.1f, 0.1f, 0.5f, 0.5f, 1f, 1f)).Build()));
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileToneCurve, FieldType.Float, 6, B(ByteOrder.Little).Floats(0f, 0f, 0.5f, 0.5f, 0.9f, 0.9f)).Build()));
    }

    [Fact]
    public void Tone_curve_non_increasing_x_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileToneCurve, FieldType.Float, 6, B(ByteOrder.Little).Floats(0f, 0f, 0.5f, 0.5f, 0.5f, 1f)).Build()));

    [Fact]
    public void Tone_curve_odd_or_short_count_is_rejected()
    {
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileToneCurve, FieldType.Float, 5, B(ByteOrder.Little).Floats(0f, 0f, 0.5f, 0.5f, 1f)).Build()));
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileToneCurve, FieldType.Float, 2, B(ByteOrder.Little).Floats(0f, 1f)).Build()));
    }

    [Fact]
    public void Tone_curve_out_of_range_or_non_finite_is_rejected()
    {
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileToneCurve, FieldType.Float, 6, B(ByteOrder.Little).Floats(0f, 0f, 0.5f, 1.5f, 1f, 1f)).Build()));
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileToneCurve, FieldType.Float, 6, B(ByteOrder.Little).Floats(0f, 0f, 0.5f, float.NaN, 1f, 1f)).Build()));
    }

    // ---------------------------------------------------------------------
    // enum／HDR
    // ---------------------------------------------------------------------
    [Fact]
    public void Embed_policy_4_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileEmbedPolicy, FieldType.Long, 1, B(ByteOrder.Little).U32s(4)).Build()));

    [Fact]
    public void Default_black_render_2_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.DefaultBlackRender, FieldType.Long, 1, B(ByteOrder.Little).U32s(2)).Build()));

    [Fact]
    public void Hdr_dynamic_range_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(ProfileDynamicRangeTag, FieldType.Undefined, 8, B(ByteOrder.Little).DynamicRange(1, 1, 0.5f)).Build()));

    [Fact]
    public void Dynamic_range_version_2_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(ProfileDynamicRangeTag, FieldType.Undefined, 8, B(ByteOrder.Little).DynamicRange(2, 0, 0.5f)).Build()));

    [Fact]
    public void Dynamic_range_hint_above_one_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(ProfileDynamicRangeTag, FieldType.Undefined, 8, B(ByteOrder.Little).DynamicRange(1, 0, 2f)).Build()));

    [Fact]
    public void Dynamic_range_non_finite_hint_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(ProfileDynamicRangeTag, FieldType.Undefined, 8, B(ByteOrder.Little).DynamicRange(1, 0, float.NaN)).Build()));

    [Fact]
    public void Dynamic_range_wrong_size_or_type_is_rejected()
    {
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(ProfileDynamicRangeTag, FieldType.Undefined, 7, new byte[7]).Build()));
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(ProfileDynamicRangeTag, FieldType.Long, 2, B(ByteOrder.Little).U32s(1, 0)).Build()));
    }

    // ---------------------------------------------------------------------
    // 資源上限
    // ---------------------------------------------------------------------
    [Fact]
    public void Source_over_64_mib_is_rejected()
    {
        var b = Minimal(ByteOrder.Little);
        var probe = b.Build();
        b.TailPad = (64 << 20) + 1 - probe.Length;
        var bytes = b.Build();
        Assert.True(bytes.Length > 64 << 20);
        var error = Assert.Throws<RawException>(() => Read(bytes));
        Assert.Equal(RawErrorKind.Limit, error.Kind);
    }

    [Fact]
    public void Source_exactly_64_mib_is_accepted()
    {
        var b = Minimal(ByteOrder.Little);
        var probe = b.Build();
        b.TailPad = (64 << 20) - probe.Length;
        var bytes = b.Build();
        Assert.Equal(64 << 20, bytes.Length);
        var p = Read(bytes);
        Assert.Equal("Acme Model A", p.UniqueCameraModel);
    }

    [Fact]
    public void Ifd_over_256_entries_is_rejected()
    {
        var b = Minimal(ByteOrder.Little);
        for (var i = 0; i < 260; i++)
            b.Add((ushort)(60000 + i), FieldType.Short, 1, B(ByteOrder.Little).U16s(1));
        var error = Assert.Throws<RawException>(() => Read(b.Build()));
        Assert.Equal(RawErrorKind.Limit, error.Kind);
    }

    [Fact]
    public void Hue_sat_cells_over_limit_are_rejected_before_allocation()
    {
        var error = Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(1024, 1024, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, 0, []).Build()));
        Assert.Equal(RawErrorKind.Limit, error.Kind);
    }

    [Fact]
    public void Hue_sat_cells_at_limit_are_accepted()
    {
        // 1,048,576 格剛好在限內；資料量 12 MiB 在檔案 64 MiB 內。
        var cells = 1 << 20;
        var data = new float[cells * 3];
        for (var c = 0; c < cells; c += 512) data[c * 3 + 2] = 1f; // 每列 s=0 的 value scale 恰為 1
        var b = Minimal(ByteOrder.Little)
            .Add(Tags.ProfileHueSatMapDims, FieldType.Long, 3, B(ByteOrder.Little).U32s(1024, 512, 2))
            .Add(Tags.ProfileHueSatMapData1, FieldType.Float, (uint)data.Length, B(ByteOrder.Little).Floats(data));
        var p = Read(b.Build());
        Assert.Equal(cells * 3, p.HueSatMap1!.Data.Length);
    }

    [Fact]
    public void Tone_curve_over_65536_pairs_is_rejected() =>
        Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileToneCurve, FieldType.Float, 131074u, B(ByteOrder.Little).Floats(new float[4])).Build()));

    [Fact]
    public void Parsed_data_over_40_mib_is_rejected()
    {
        // 41 MiB 的 ASCII copyright：檔案仍在 64 MiB 內，但總解析資料超限。
        var big = new byte[(41 << 20) + 1];
        Array.Fill(big, (byte)'A');
        var error = Assert.Throws<RawException>(() => Read(Minimal(ByteOrder.Little)
            .Add(Tags.ProfileCopyright, FieldType.Ascii, (uint)big.Length, big).Build()));
        Assert.Equal(RawErrorKind.Limit, error.Kind);
    }

    [Fact]
    public void Cancelled_token_stops_read()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => DcpProfile.Read(Minimal(ByteOrder.Little).Build(), cts.Token));
    }

    // ---------------------------------------------------------------------
    // 來源資料分離：DNG baseline／signature／UniqueCameraModel
    // ---------------------------------------------------------------------
    private static byte[] DngLikeTiff(ByteOrder order)
    {
        var b = new DcpBuilder(order) { Magic = 42 };
        b.Add(Tags.UniqueCameraModel, FieldType.Ascii, 12, b.Text("Acme Unique"))
            .Add(Tags.Model, FieldType.Ascii, 9, b.Text("Acme Cam"))
            .Add(Tags.BaselineExposure, FieldType.SRational, 1, b.SRationals((1, 2)))
            .Add(Tags.BaselineExposureOffset, FieldType.SRational, 1, b.SRationals((-1, 4)))
            .Add(Tags.CameraCalibrationSignature, FieldType.Ascii, 7, b.Text("CamSig"))
            .Add(Tags.ProfileCalibrationSignature, FieldType.Ascii, 8, b.Text("ProfSig"))
            .Add(Tags.CalibrationIlluminant1, FieldType.Short, 1, b.U16s(21))
            .Add(Tags.ColorMatrix1, FieldType.SRational, 9, b.SRationals(Cm1));
        return b.Build();
    }

    [Theory]
    [InlineData(ByteOrder.Little)]
    [InlineData(ByteOrder.Big)]
    public void Dng_source_components_are_kept_separate(ByteOrder order)
    {
        var tiff = TiffFile.Parse(DngLikeTiff(order));
        var ifd = tiff.Ifds[0];
        var color = Dng.ColorData(ifd, ifd, order);
        Assert.Equal(0.25, color.BaselineExposure, 12); // 既有消費者看到的總和不變
        Assert.Equal(0.5, color.ImageBaselineExposure, 12);
        Assert.Equal(-0.25, color.ProfileBaselineExposureOffset, 12);
        Assert.Equal("CamSig", color.CameraCalibrationSignature);
        Assert.Equal("ProfSig", color.ProfileCalibrationSignature);
        var meta = MetadataReader.FromTiff(tiff);
        Assert.Equal("Acme Unique", meta.UniqueCameraModel);
        Assert.Equal("Acme Cam", meta.Model); // Model 優先，UniqueCameraModel 獨立保存
    }

    [Fact]
    public void Dng_without_offset_keeps_baseline_semantics()
    {
        var b = new DcpBuilder(ByteOrder.Little) { Magic = 42 };
        b.Add(Tags.UniqueCameraModel, FieldType.Ascii, 12, b.Text("Acme Unique"))
            .Add(Tags.BaselineExposure, FieldType.SRational, 1, b.SRationals((3, 4)));
        var tiff = TiffFile.Parse(b.Build());
        var color = Dng.ColorData(tiff.Ifds[0], tiff.Ifds[0], tiff.Order);
        Assert.Equal(0.75, color.BaselineExposure, 12);
        Assert.Equal(0.75, color.ImageBaselineExposure, 12);
        Assert.Equal(0.0, color.ProfileBaselineExposureOffset);
        Assert.Null(color.CameraCalibrationSignature);
        Assert.Null(color.ProfileCalibrationSignature);
        var meta = MetadataReader.FromTiff(tiff);
        Assert.Equal("Acme Unique", meta.UniqueCameraModel);
        Assert.Equal("Acme Unique", meta.Model); // 無 Model 時的既有 fallback 不變
    }

    [Fact]
    public void Color_data_clone_copies_source_metadata()
    {
        var tiff = TiffFile.Parse(DngLikeTiff(ByteOrder.Little));
        var color = Dng.ColorData(tiff.Ifds[0], tiff.Ifds[0], tiff.Order);
        var clone = color.Clone();
        Assert.Equal(color.ImageBaselineExposure, clone.ImageBaselineExposure);
        Assert.Equal(color.ProfileBaselineExposureOffset, clone.ProfileBaselineExposureOffset);
        Assert.Equal(color.CameraCalibrationSignature, clone.CameraCalibrationSignature);
        Assert.Equal(color.ProfileCalibrationSignature, clone.ProfileCalibrationSignature);
        Assert.Equal(color.BaselineExposure, clone.BaselineExposure);
    }
}
