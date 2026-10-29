// 移植自 LightCraft crates/meta/src/exif.rs（RAW 用 TIFF→Metadata 擷取；XMP/IPTC/GPS/容器不屬本切片）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Meta;

/// <summary>LightCraft <c>meta::from_tiff</c>：把已解析的 TIFF（IFD0＋Exif，DNG 標籤後備）填入 <see cref="Metadata"/>。</summary>
public static class MetadataReader
{
    private static string? Text(Ifd? ifd, ushort tag)
    {
        var s = ifd?.String(tag)?.Trim();
        return s is { Length: > 0 } && s.Any(c => c is not (' ' or '\0')) ? s : null;
    }
    private static double? Pos(double? v) => v is { } x && double.IsFinite(x) && x > 0 ? x : null;

    /// <summary>填寫 RAW 解碼用到的契約欄位；寬高為「記錄值」（各廠商切片之後再以裁切覆寫）。</summary>
    public static Metadata FromTiff(TiffFile tiff)
    {
        var ifd0 = tiff.Ifds.Count > 0 ? tiff.Ifds[0] : null;
        var exif = tiff.Exif();
        TiffValue? Either(ushort tag) => exif?.Value(tag) ?? ifd0?.Value(tag);
        var m = new Metadata
        {
            Make = Text(ifd0, Tags.Make),
            Model = Text(ifd0, Tags.Model) ?? Text(ifd0, Tags.UniqueCameraModel),
            UniqueCameraModel = Text(ifd0, Tags.UniqueCameraModel),
            Software = Text(ifd0, Tags.Software),
            SerialNumber = Text(exif, Tags.BodySerialNumber) ?? Text(ifd0, Tags.CameraSerialNumber),
            LensMake = Text(exif, Tags.LensMake),
            LensModel = Text(exif, Tags.LensModel),
        };
        // Exif requires LensSpecification/LensInfo as 4 rationals.
        var spec = exif?.F64s(Tags.LensSpecification) ?? ifd0?.F64s(Tags.LensInfo);
        if (spec is { Length: 4 } s && s[0] > 0) m.LensSpec = [s[0], s[1], s[2], s[3]];
        // Nikon DSLRs write neither LensModel nor LensSpecification, only the same four values in their maker note.
        m.LensSpec ??= NikonLensSpec(exif, m.Make, tiff.Order);
        m.Orientation = ifd0?.U16(Tags.Orientation) is { } o && o is >= 1 and <= 8 ? OrientationExtensions.FromExif(o) : null;

        m.ExposureTime = Pos(Either(Tags.ExposureTime)?.GetF64(0))
            ?? (exif?.F64(Tags.ShutterSpeedValue) is { } tv && Math.Abs(tv) < 64 ? Math.Pow(2, -tv) : null);
        m.FNumber = Pos(Either(Tags.FNumber)?.GetF64(0))
            ?? (exif?.F64(Tags.ApertureValue) is { } av && Math.Abs(av) < 64 ? Math.Pow(2, av / 2) : null);
        ulong? iso = Either(Tags.IsoSpeed)?.GetU64(0) is { } i && i > 0 ? i : null;
        ulong? isoExt = exif?.U64(Tags.RecommendedExposureIndex) ?? exif?.U64(Tags.IsoSpeedRatingsExt);
        if (isoExt is 0) isoExt = null;
        m.Iso = (iso == 65535 || iso is null) && isoExt is { } extended ? (uint)Math.Min(extended, uint.MaxValue) :
            iso is { } v ? (uint)Math.Min(v, uint.MaxValue) : null;
        m.FocalLength = Pos(Either(Tags.FocalLength)?.GetF64(0));
        m.FocalLength35mm = Pos(exif?.F64(Tags.FocalLength35mm));
        m.ExposureBias = exif?.F64(Tags.ExposureBias) is { } b && double.IsFinite(b) && Math.Abs(b) < 100 ? b : null;

        // capture time: DateTimeOriginal, then DateTimeDigitized, then IFD0 DateTime
        (Ifd? Ifd, ushort Dt, ushort Off, ushort Sub)[] candidates =
            [(exif, Tags.DateTimeOriginal, Tags.OffsetTimeOriginal, Tags.SubsecTimeOriginal),
             (exif, Tags.DateTimeDigitized, Tags.OffsetTimeDigitized, Tags.SubsecTimeDigitized),
             (ifd0, Tags.DateTime, Tags.OffsetTime, Tags.SubsecTime)];
        foreach (var (ifd, dtTag, offTag, subTag) in candidates)
            if (Text(ifd, dtTag) is { } text && RawDateTimeParser.ParseExif(text) is { } dt)
            {
                if (Text(exif, subTag) is { } sub) dt = RawDateTimeParser.WithSubsec(dt, sub);
                if (Text(exif, offTag) is { } off && RawDateTimeParser.ParseOffset(off) is { } om) dt = dt with { OffsetMinutes = om };
                m.CaptureTime = dt;
                break;
            }
        m.Width = exif?.U32(Tags.PixelXDimension) is > 0 ? exif!.U32(Tags.PixelXDimension) : ifd0?.U32(Tags.ImageWidth);
        m.Height = exif?.U32(Tags.PixelYDimension) is > 0 ? exif!.U32(Tags.PixelYDimension) : ifd0?.U32(Tags.ImageLength);
        return m;
    }

    /// Nikon maker note tag `0x0084`: shortest and longest focal length, then the widest aperture at each, as rationals.
    private const ushort NikonLens = 0x0084;

    /// The lens a Nikon camera records in its maker note, when it's plausible.
    private static double[]? NikonLensSpec(Ifd? exif, string? make, ByteOrder order)
    {
        if (make is null || !make.Trim().ToUpperInvariant().StartsWith("NIKON", StringComparison.Ordinal)) return null;
        var note = exif?.Bytes(Tags.MakerNote);
        if (note is null) return null;
        var mn = MakerNote.ParseMakerNote(note, 0, note.Length, order, make);
        if (mn?.Ifd.F64s(NikonLens) is not { Length: 4 } s) return null;
        static bool Focal(double f) => double.IsFinite(f) && f > 0 && f < 10_000;
        static bool Aperture(double a) => double.IsFinite(a) && a > 0 && a < 100;
        return Focal(s[0]) && Focal(s[1]) && s[1] >= s[0] && Aperture(s[2]) && Aperture(s[3]) ? [s[0], s[1], s[2], s[3]] : null;
    }
}
