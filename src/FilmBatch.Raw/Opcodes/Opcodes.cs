// 移植自 LightCraft crates/raw/src/opcodes.rs（Opcode 列舉體；解析與套用見 OpcodeOps）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Opcodes;

/// <summary>DNG 運算碼各區域共用的矩形、平面選擇與間距（lightcraft <c>Area</c>）。</summary>
public readonly record struct Area(uint Top, uint Left, uint Bottom, uint Right, uint Plane, uint Planes, uint RowPitch, uint ColPitch);

/// <summary>DNG WarpRectilinear 運算碼（id 1）：每平面 kr0..kr3、kt0、kt1 與相對光學中心。</summary>
public sealed record WarpRectilinear(double[][] Planes, double[] Center) : Opcode
{
    public bool Equals(WarpRectilinear? other) =>
        other is not null && Planes.Length == other.Planes.Length
        && Planes.Zip(other.Planes, (a, b) => a.SequenceEqual(b)).All(e => e)
        && Center.SequenceEqual(other.Center);
    public override int GetHashCode() => HashCode.Combine(Planes.Length, Center.Length);
}

/// <summary>DNG WarpFisheye 運算碼（id 2）：已記錄但不套用。</summary>
public sealed record WarpFisheye(double[][] Planes, double[] Center) : Opcode
{
    public bool Equals(WarpFisheye? other) =>
        other is not null && Planes.Length == other.Planes.Length
        && Planes.Zip(other.Planes, (a, b) => a.SequenceEqual(b)).All(e => e)
        && Center.SequenceEqual(other.Center);
    public override int GetHashCode() => HashCode.Combine(Planes.Length, Center.Length);
}

/// <summary>DNG 1.6 WarpRectilinear2 運算碼（id 14）：每平面 19 個值（kr0..kr14、kt0、kt1、min/max 有效半徑）。</summary>
public sealed record WarpRectilinear2(double[][] Planes, double[] Center, bool Reciprocal, bool Optional) : Opcode
{
    public bool Equals(WarpRectilinear2? other) =>
        other is not null && Reciprocal == other.Reciprocal && Optional == other.Optional
        && Planes.Length == other.Planes.Length
        && Planes.Zip(other.Planes, (a, b) => a.SequenceEqual(b)).All(e => e)
        && Center.SequenceEqual(other.Center);
    public override int GetHashCode() => HashCode.Combine(Planes.Length, Reciprocal, Optional);
}

/// <summary>DNG FixVignetteRadial 運算碼（id 3）。</summary>
public sealed record FixVignetteRadial(double[] K, double[] Center) : Opcode;

/// <summary>DNG FixBadPixelsConstant 運算碼（id 4）。</summary>
public sealed record FixBadPixelsConstant(uint Constant, uint BayerPhase) : Opcode;

/// <summary>DNG FixBadPixelsList 運算碼（id 5）：Points 為 (Row, Col)，Rects 為 [top, left, bottom, right]。</summary>
public sealed record FixBadPixelsList(uint BayerPhase, (uint Row, uint Col)[] Points, uint[][] Rects) : Opcode
{
    public bool Equals(FixBadPixelsList? other) =>
        other is not null && BayerPhase == other.BayerPhase && Points.SequenceEqual(other.Points)
        && Rects.Length == other.Rects.Length
        && Rects.Zip(other.Rects, (a, b) => a.SequenceEqual(b)).All(e => e);
    public override int GetHashCode() => HashCode.Combine(BayerPhase, Points.Length, Rects.Length);
}

/// <summary>DNG TrimBounds 運算碼（id 6）：已記錄但不套用。</summary>
public sealed record TrimBounds(uint Top, uint Left, uint Bottom, uint Right) : Opcode;

/// <summary>DNG MapTable 運算碼（id 7）。</summary>
public sealed record MapTable(Area Area, ushort[] Table) : Opcode;

/// <summary>DNG MapPolynomial 運算碼（id 8）。</summary>
public sealed record MapPolynomial(Area Area, double[] Coefficients) : Opcode;

/// <summary>DNG GainMap 運算碼（id 9）。</summary>
public sealed record GainMap(Area Area, uint PointsV, uint PointsH, double[] Spacing, double[] Origin, uint MapPlanes, float[] Gains) : Opcode;

/// <summary>DNG DeltaPerRow 運算碼（id 10）。</summary>
public sealed record DeltaPerRow(Area Area, float[] Deltas) : Opcode;

/// <summary>DNG DeltaPerColumn 運算碼（id 11）。</summary>
public sealed record DeltaPerColumn(Area Area, float[] Deltas) : Opcode;

/// <summary>DNG ScalePerRow 運算碼（id 12）。</summary>
public sealed record ScalePerRow(Area Area, float[] Scales) : Opcode;

/// <summary>DNG ScalePerColumn 運算碼（id 13）。</summary>
public sealed record ScalePerColumn(Area Area, float[] Scales) : Opcode;

/// <summary>未知或無法解析的運算碼（照原樣保留，不套用）。</summary>
public sealed record Unknown(uint Id, uint Flags, byte[] Params) : Opcode;

public abstract partial record Opcode
{
    /// <summary><c>ApplyList</c>／<c>ApplyList3</c> 是否實作此運算碼。</summary>
    public bool IsApplied() => this is not Unknown and not TrimBounds { } and not WarpFisheye { };

    /// <summary>是否為鏡頭校正（WarpRectilinear、WarpRectilinear2、FixVignetteRadial）。</summary>
    public bool IsLensCorrection() => this is WarpRectilinear or WarpRectilinear2 or FixVignetteRadial;

    /// <summary>
    /// 把鏡頭校正改寫給寬高 width × height 影像中 crop 區域：對裁切後影像套用，與先校正再裁切相同。
    /// 其他運算碼、過小影像或超出範圍的裁切回傳 null。
    /// </summary>
    public Opcode? Cropped(int width, int height, PixelRect crop)
    {
        static bool Fits(int x, int len, int limit) => x >= 0 && len >= 0 && (long)x + len <= limit;
        if (width < 2 || height < 2 || crop.Width < 2 || crop.Height < 2
            || !Fits(crop.X, crop.Width, width) || !Fits(crop.Y, crop.Height, height))
            return null;
        var (aw, ah) = (width - 1d, height - 1d);
        var (cw, ch) = (crop.Width - 1d, crop.Height - 1d);
        static (double[] Center, double S)? Reframe(double[] c, double aw, double ah, double cw, double ch, PixelRect crop)
        {
            var (px, py) = (c[0] * aw, c[1] * ah);
            var (qx, qy) = (px - crop.X, py - crop.Y);
            var s = OpcodeOps.MaxRadius(qx, qy, cw, ch) / OpcodeOps.MaxRadius(px, py, aw, ah);
            return double.IsFinite(s) && s > 0d && double.IsFinite(qx) && double.IsFinite(qy)
                ? (new[] { qx / cw, qy / ch }, s) : null;
        }
        switch (this)
        {
            case WarpRectilinear wr:
            {
                if (Reframe(wr.Center, aw, ah, cw, ch, crop) is not (var center, var s)) return null;
                var planes = wr.Planes.Select(k =>
                {
                    var n = (double[])k.Clone();
                    for (var i = 0; i < Math.Min(4, n.Length); i++) n[i] *= Math.Pow(s, 2 * i);
                    if (n.Length > 4) n[4] *= s;
                    if (n.Length > 5) n[5] *= s;
                    return n;
                }).ToArray();
                return new WarpRectilinear(planes, center);
            }
            case WarpRectilinear2 wr2:
            {
                if (Reframe(wr2.Center, aw, ah, cw, ch, crop) is not (var center, var s)) return null;
                var planes = wr2.Planes.Select(k =>
                {
                    var n = (double[])k.Clone();
                    for (var i = 0; i < Math.Min(15, n.Length); i++) n[i] *= Math.Pow(s, i);
                    if (n.Length > 15) n[15] *= s;
                    if (n.Length > 16) n[16] *= s;
                    if (n.Length > 17) n[17] /= s;
                    if (n.Length > 18) n[18] /= s;
                    return n;
                }).ToArray();
                return new WarpRectilinear2(planes, center, wr2.Reciprocal, wr2.Optional);
            }
            case FixVignetteRadial fv:
            {
                if (Reframe(fv.Center, aw, ah, cw, ch, crop) is not (var center, var s)) return null;
                var k = (double[])fv.K.Clone();
                for (var i = 0; i < k.Length; i++) k[i] *= Math.Pow(s, 2 * i + 2);
                return new FixVignetteRadial(k, center);
            }
            default:
                return null;
        }
    }
}
