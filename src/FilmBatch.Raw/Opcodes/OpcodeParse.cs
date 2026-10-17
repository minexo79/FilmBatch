// 移植自 LightCraft crates/raw/src/opcodes.rs（運算碼清單解析與序列化）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Buffers.Binary;

namespace FilmBatch.Raw.Opcodes;

/// <summary>DNG 運算碼清單的解析、序列化與套用（lightcraft <c>opcodes</c> 模組）。清單一律大端序。</summary>
public static partial class OpcodeOps
{
    /// <summary><c>WarpRectilinear2</c> 每組係數的值個數：15 個徑向、2 個切向、min／max 有效半徑。</summary>
    public const int Wr2Len = 19;
    /// <summary><c>WarpRectilinear2</c> 一份清單最多的平面數。</summary>
    private const int Wr2MaxPlanes = 4;
    /// <summary><c>WarpRectilinear2</c> 係數的最大絕對值（真實鏡頭擬合遠小於此）。</summary>
    private const double Wr2MaxCoef = 1e9;
    /// <summary>一個壞點運算碼最多修復的像素數。</summary>
    private const int MaxBadPixels = 1 << 22;

    private ref struct Reader
    {
        public ReadOnlySpan<byte> Data;
        public int Pos;
        public Reader(ReadOnlySpan<byte> data) { Data = data; Pos = 0; }
        public uint? U32()
        {
            if (Pos < 0 || Pos + 4 > Data.Length) return null;
            var v = BinaryPrimitives.ReadUInt32BigEndian(Data.Slice(Pos, 4)); Pos += 4; return v;
        }
        public ushort? U16()
        {
            if (Pos < 0 || Pos + 2 > Data.Length) return null;
            var v = BinaryPrimitives.ReadUInt16BigEndian(Data.Slice(Pos, 2)); Pos += 2; return v;
        }
        public double? F64()
        {
            if (Pos < 0 || Pos + 8 > Data.Length) return null;
            var v = BinaryPrimitives.ReadDoubleBigEndian(Data.Slice(Pos, 8)); Pos += 8; return v;
        }
        public float? F32()
        {
            if (Pos < 0 || Pos + 4 > Data.Length) return null;
            var v = BinaryPrimitives.ReadSingleBigEndian(Data.Slice(Pos, 4)); Pos += 4; return v;
        }
        public Area? GetArea()
        {
            var t = U32(); var l = U32(); var b = U32(); var r = U32();
            var p = U32(); var ps = U32(); var rp = U32(); var cp = U32();
            if (t is null || l is null || b is null || r is null || p is null || ps is null || rp is null || cp is null) return null;
            return new Area(t.Value, l.Value, b.Value, r.Value, p.Value, ps.Value, Math.Max(rp.Value, 1), Math.Max(cp.Value, 1));
        }
        public int Remaining => Math.Max(Data.Length - Pos, 0);
    }

    private static Opcode? ParseOne(uint id, uint flags, ReadOnlySpan<byte> paramSpan)
    {
        var r = new Reader(paramSpan);
        static double? Fin(ref Reader r) { var v = r.F64(); return v is { } x && double.IsFinite(x) ? x : null; }
        switch (id)
        {
            case 1:
            {
                if (r.U32() is not { } n || n == 0 || n > 4) return null;
                var planes = new double[n][];
                for (var i = 0; i < planes.Length; i++)
                {
                    var k = new double[6];
                    for (var j = 0; j < 6; j++) { if (Fin(ref r) is not { } v) return null; k[j] = v; }
                    planes[i] = k;
                }
                if (Fin(ref r) is not { } cx || Fin(ref r) is not { } cy) return null;
                return new WarpRectilinear(planes, [cx, cy]);
            }
            case 2:
            {
                if (r.U32() is not { } n || n == 0 || n > 4) return null;
                var planes = new double[n][];
                for (var i = 0; i < planes.Length; i++)
                {
                    var k = new double[4];
                    for (var j = 0; j < 4; j++) { if (Fin(ref r) is not { } v) return null; k[j] = v; }
                    planes[i] = k;
                }
                if (Fin(ref r) is not { } cx || Fin(ref r) is not { } cy) return null;
                return new WarpFisheye(planes, [cx, cy]);
            }
            case 14:
            {
                if (r.U32() is not { } n || n == 0 || n > Wr2MaxPlanes) return null;
                // N 組 19 個 double，再加中心（2 個 double）與 reciprocalRadial（LONG）；不可多不可少
                if (paramSpan.Length != 4 + n * Wr2Len * 8 + 16 + 4) return null;
                var planes = new double[n][];
                for (var i = 0; i < planes.Length; i++)
                {
                    var k = new double[Wr2Len];
                    for (var j = 0; j < Wr2Len; j++)
                    {
                        if (Fin(ref r) is not { } v || Math.Abs(v) > Wr2MaxCoef) return null;
                        k[j] = v;
                    }
                    // 規範要求 0 <= min < max <= 1
                    if (!(k[17] >= 0d && k[17] < k[18] && k[18] <= 1d)) return null;
                    planes[i] = k;
                }
                if (Fin(ref r) is not { } cx || Math.Abs(cx) > 4d || Fin(ref r) is not { } cy || Math.Abs(cy) > 4d) return null;
                if (r.U32() is not { } rec) return null;
                return new WarpRectilinear2(planes, [cx, cy], rec != 0, (flags & 1) != 0);
            }
            case 3:
            {
                var k = new double[5];
                for (var j = 0; j < 5; j++) { if (Fin(ref r) is not { } v) return null; k[j] = v; }
                if (Fin(ref r) is not { } cx || Fin(ref r) is not { } cy) return null;
                return new FixVignetteRadial(k, [cx, cy]);
            }
            case 4:
            {
                if (r.U32() is not { } c || r.U32() is not { } b) return null;
                return new FixBadPixelsConstant(c, b);
            }
            case 5:
            {
                if (r.U32() is not { } phase || r.U32() is not { } np || r.U32() is not { } nr) return null;
                if ((ulong)np * 8 + (ulong)nr * 16 > (ulong)r.Remaining) return null;
                var points = new (uint, uint)[np];
                for (var i = 0; i < points.Length; i++)
                {
                    if (r.U32() is not { } row || r.U32() is not { } col) return null;
                    points[i] = (row, col);
                }
                var rects = new uint[nr][];
                for (var i = 0; i < rects.Length; i++)
                {
                    var rect = new uint[4];
                    for (var j = 0; j < 4; j++) { if (r.U32() is not { } v) return null; rect[j] = v; }
                    rects[i] = rect;
                }
                return new FixBadPixelsList(phase, points, rects);
            }
            case 6:
            {
                if (r.U32() is not { } t || r.U32() is not { } l || r.U32() is not { } b || r.U32() is not { } rr) return null;
                return new TrimBounds(t, l, b, rr);
            }
            case 7:
            {
                if (r.GetArea() is not { } area || r.U32() is not { } n) return null;
                if (n == 0 || n > 65536 || (ulong)n * 2 > (ulong)r.Remaining) return null;
                var table = new ushort[n];
                for (var i = 0; i < table.Length; i++) { if (r.U16() is not { } v) return null; table[i] = v; }
                return new MapTable(area, table);
            }
            case 8:
            {
                if (r.GetArea() is not { } area || r.U32() is not { } deg || deg > 8) return null;
                var coef = new double[deg + 1];
                for (var i = 0; i < coef.Length; i++) { if (Fin(ref r) is not { } v) return null; coef[i] = v; }
                return new MapPolynomial(area, coef);
            }
            case 9:
            {
                if (r.GetArea() is not { } area || r.U32() is not { } pv || r.U32() is not { } ph) return null;
                if (Fin(ref r) is not { } sv || Fin(ref r) is not { } sh || Fin(ref r) is not { } ov || Fin(ref r) is not { } oh) return null;
                if (r.U32() is not { } mp) return null;
                ulong count = checked((ulong)pv * ph * mp);
                if (count == 0 || count * 4 > (ulong)r.Remaining) return null;
                var gains = new float[count];
                for (ulong i = 0; i < count; i++) { if (r.F32() is not { } v) return null; gains[i] = v; }
                return new GainMap(area, pv, ph, [sv, sh], [ov, oh], mp, gains);
            }
            case 10:
            case 11:
            case 12:
            case 13:
            {
                if (r.GetArea() is not { } area || r.U32() is not { } n) return null;
                if ((ulong)n * 4 > (ulong)r.Remaining) return null;
                var v = new float[n];
                for (var i = 0; i < v.Length; i++) { if (r.F32() is not { } f) return null; v[i] = f; }
                return id switch
                {
                    10 => new DeltaPerRow(area, v),
                    11 => new DeltaPerColumn(area, v),
                    12 => new ScalePerRow(area, v),
                    _ => new ScalePerColumn(area, v),
                };
            }
            default:
                return new Unknown(id, flags, paramSpan.ToArray());
        }
    }

    private static void PutArea(List<byte> o, Area a)
    {
        foreach (var v in new[] { a.Top, a.Left, a.Bottom, a.Right, a.Plane, a.Planes, a.RowPitch, a.ColPitch })
        {
            var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); o.AddRange(b);
        }
    }

    private static (uint Id, uint Flags, byte[] Params) OpcodeParams(Opcode op)
    {
        var o = new List<byte>();
        static void F64s(List<byte> o, double[] v) { foreach (var x in v) { var b = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, x); o.AddRange(b); } }
        static void U32s(List<byte> o, uint[] v) { foreach (var x in v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, x); o.AddRange(b); } }
        static void F32s(List<byte> o, float[] v) { foreach (var x in v) { var b = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, x); o.AddRange(b); } }
        var id = op switch
        {
            WarpRectilinear wr => (Id: 1u, Act: (Action)(() => { U32s(o, [(uint)wr.Planes.Length]); foreach (var p in wr.Planes) F64s(o, p); F64s(o, wr.Center); })),
            WarpFisheye wf => (Id: 2u, Act: (Action)(() => { U32s(o, [(uint)wf.Planes.Length]); foreach (var p in wf.Planes) F64s(o, p); F64s(o, wf.Center); })),
            WarpRectilinear2 w2 => (Id: 14u, Act: (Action)(() => { U32s(o, [(uint)w2.Planes.Length]); foreach (var p in w2.Planes) F64s(o, p); F64s(o, w2.Center); U32s(o, [w2.Reciprocal ? 1u : 0u]); })),
            FixVignetteRadial fv => (Id: 3u, Act: (Action)(() => { F64s(o, fv.K); F64s(o, fv.Center); })),
            FixBadPixelsConstant fb => (Id: 4u, Act: (Action)(() => U32s(o, [fb.Constant, fb.BayerPhase]))),
            FixBadPixelsList fl => (Id: 5u, Act: (Action)(() =>
            {
                U32s(o, [fl.BayerPhase, (uint)fl.Points.Length, (uint)fl.Rects.Length]);
                foreach (var (row, col) in fl.Points) U32s(o, [row, col]);
                foreach (var rect in fl.Rects) U32s(o, rect);
            })),
            TrimBounds tb => (Id: 6u, Act: (Action)(() => U32s(o, [tb.Top, tb.Left, tb.Bottom, tb.Right]))),
            MapTable mt => (Id: 7u, Act: (Action)(() =>
            {
                PutArea(o, mt.Area); U32s(o, [(uint)mt.Table.Length]);
                foreach (var v in mt.Table) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); o.AddRange(b); }
            })),
            MapPolynomial mp => (Id: 8u, Act: (Action)(() => { PutArea(o, mp.Area); U32s(o, [(uint)Math.Max(mp.Coefficients.Length - 1, 0)]); F64s(o, mp.Coefficients); })),
            GainMap gm => (Id: 9u, Act: (Action)(() =>
            {
                PutArea(o, gm.Area); U32s(o, [gm.PointsV, gm.PointsH]);
                F64s(o, gm.Spacing); F64s(o, gm.Origin); U32s(o, [gm.MapPlanes]); F32s(o, gm.Gains);
            })),
            DeltaPerRow dr => (Id: 10u, Act: (Action)(() => { PutArea(o, dr.Area); U32s(o, [(uint)dr.Deltas.Length]); F32s(o, dr.Deltas); })),
            DeltaPerColumn dc => (Id: 11u, Act: (Action)(() => { PutArea(o, dc.Area); U32s(o, [(uint)dc.Deltas.Length]); F32s(o, dc.Deltas); })),
            ScalePerRow sr => (Id: 12u, Act: (Action)(() => { PutArea(o, sr.Area); U32s(o, [(uint)sr.Scales.Length]); F32s(o, sr.Scales); })),
            ScalePerColumn sc => (Id: 13u, Act: (Action)(() => { PutArea(o, sc.Area); U32s(o, [(uint)sc.Scales.Length]); F32s(o, sc.Scales); })),
            Unknown u => (Id: u.Id, Act: (Action)(() => o.AddRange(u.Params))),
            _ => throw RawException.Corrupt("unknown opcode"),
        };
        id.Act();
        var flags = op is WarpRectilinear2 wr2 ? (wr2.Optional ? 1u : 0u) : op is Unknown u2 ? u2.Flags : 0u;
        return (id.Id, flags, o.ToArray());
    }

    /// <summary>序列化運算碼清單（<c>ParseList</c> 的逆運算）。</summary>
    public static byte[] WriteList(IReadOnlyList<Opcode> list)
    {
        var outp = new List<byte>();
        var count = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(count, (uint)list.Count); outp.AddRange(count);
        foreach (var op in list)
        {
            var (id, flags, p) = OpcodeParams(op);
            foreach (var v in new[] { id, id == 14 ? 0x01060000u : 0x01030000u, flags, (uint)p.Length })
            {
                var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); outp.AddRange(b);
            }
            outp.AddRange(p);
        }
        return outp.ToArray();
    }

    /// <summary>解析運算碼清單位元組。損毀的運算碼變成 <c>Unknown</c>；截斷的清單提早結束。</summary>
    public static List<Opcode> ParseList(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        if (r.U32() is not { } count) return [];
        var outp = new List<Opcode>();
        for (ulong i = 0; i < Math.Min(count, 4096); i++)
        {
            if (r.U32() is not { } id || r.U32() is not { } || r.U32() is not { } flags || r.U32() is not { } size) break;
            // 以 long／ulong 檢查截斷：位元組長度來自檔案，不可溢位回到緩衝區內
            if ((ulong)r.Pos + size > (ulong)data.Length) break;
            var paramSpan = data.Slice(r.Pos, (int)size);
            r.Pos += (int)size;
            var parsed = ParseOne(id, flags, paramSpan);
            outp.Add(parsed ?? new Unknown(id, flags, paramSpan.ToArray()));
        }
        return outp;
    }
}
