// 移植自 LightCraft crates/meta/src/xmp.rs 的 write_xmp（FilmBatch Metadata 子集的交換欄位）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Meta;

namespace FilmBatch.Raw;

/// <summary>由 <see cref="Metadata"/> 產生 XMP 包（lightcraft <c>write_xmp(meta, None)</c> 的子集）。</summary>
internal static class DngWriterXmp
{
    private static string Escape(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Frac(double v)
    {
        if (v > 0.0 && v < 1.0)
        {
            double inv = 1.0 / v;
            if (Math.Abs(inv - Math.Round(inv)) < 1e-6) return $"1/{(ulong)Math.Round(inv)}";
        }
        var (n, d) = DngWriterTiff.Rational(v);
        return $"{n}/{d}";
    }

    private static string? Iso(RawDateTime d)
    {
        var s = $"{d.Year:0000}-{d.Month:00}-{d.Day:00}T{d.Hour:00}:{d.Minute:00}:{d.Second:00}";
        if (d.Millis != 0) s += $".{d.Millis:000}";
        if (d.OffsetMinutes is { } o) s += $"{(o < 0 ? '-' : '+')}{Math.Abs(o) / 60:00}:{Math.Abs(o) % 60:00}";
        return s;
    }

    /// <summary>產生完整 XMP 包（含 xpacket 首尾）。</summary>
    internal static string Build(Metadata m)
    {
        var simple = new List<(string Key, string Value)>();
        void Push(string k, string? v) { if (v is { Length: > 0 }) simple.Add((k, v)); }
        Push("tiff:Make", m.Make);
        Push("tiff:Model", m.Model);
        Push("tiff:Orientation", m.Orientation?.ToExif().ToString());
        Push("tiff:ImageWidth", m.Width?.ToString());
        Push("tiff:ImageLength", m.Height?.ToString());
        Push("xmp:CreatorTool", m.Software);
        Push("exif:DateTimeOriginal", m.CaptureTime is { } dt ? Iso(dt) : null);
        Push("photoshop:DateCreated", m.CaptureTime is { } dt2 ? Iso(dt2) : null);
        Push("exif:ExposureTime", m.ExposureTime is { } e && e > 0 ? Frac(e) : null);
        Push("exif:FNumber", m.FNumber is { } f && f > 0 ? Frac(f) : null);
        Push("exif:FocalLength", m.FocalLength is { } fl && fl > 0 ? Frac(fl) : null);
        Push("exif:FocalLengthIn35mmFilm", m.FocalLength35mm is { } f35 && f35 > 0 ? $"{Math.Round(f35)}" : null);
        Push("exif:ExposureBiasValue", m.ExposureBias is { } eb && Math.Abs(eb) < 100 ? $"{DngWriterTiff.SRational(eb).Num}/{DngWriterTiff.SRational(eb).Den}" : null);
        Push("exifEX:LensMake", m.LensMake);
        Push("exifEX:LensModel", m.LensModel);

        var x = new System.Text.StringBuilder();
        x.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        x.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"FilmBatch\">\n");
        x.Append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        x.Append("  <rdf:Description rdf:about=\"\"");
        x.Append("\n    xmlns:dc=\"http://purl.org/dc/elements/1.1/\"");
        x.Append("\n    xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"");
        x.Append("\n    xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\"");
        x.Append("\n    xmlns:exif=\"http://ns.adobe.com/exif/1.0/\"");
        x.Append("\n    xmlns:exifEX=\"http://cipa.jp/exif/1.0/\"");
        x.Append("\n    xmlns:tiff=\"http://ns.adobe.com/tiff/1.0/\">\n");
        foreach (var (k, v) in simple) x.Append($"   <{k}>{Escape(v)}</{k}>\n");
        if (m.Iso is { } iso) x.Append($"   <exif:ISOSpeedRatings>\n    <rdf:Seq>\n     <rdf:li>{iso}</rdf:li>\n    </rdf:Seq>\n   </exif:ISOSpeedRatings>\n");
        if (m.LensSpec is { Length: 4 } spec)
        {
            x.Append("   <exifEX:LensSpecification>\n    <rdf:Seq>\n");
            foreach (var v in spec) x.Append($"     <rdf:li>{Frac(v)}</rdf:li>\n");
            x.Append("    </rdf:Seq>\n   </exifEX:LensSpecification>\n");
        }
        x.Append("  </rdf:Description>\n </rdf:RDF>\n</x:xmpmeta>\n<?xpacket end=\"w\"?>");
        return x.ToString();
    }
}
