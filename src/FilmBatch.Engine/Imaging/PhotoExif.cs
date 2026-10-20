using System.Globalization;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Meta;

namespace FilmBatch.Engine.Imaging;

/// <summary>預覽區顯示用的拍攝資訊（EXIF）。任一欄位缺少時為 null。</summary>
public sealed record PhotoExif(
    string FileName,
    DateTime? Taken,
    string? Camera,
    string? Lens,
    int? Iso,
    double? ExposureSeconds,
    double? FNumber)
{
    /// <summary>只讀檔頭（不解碼像素）。EXIF 讀不到時仍回傳檔名，日期改用檔案修改時間。</summary>
    public static PhotoExif Read(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            if (RawSourceLoader.TryReadInfo(path) is { } raw)
            {
                var meta = raw.Metadata;
                return new PhotoExif(name, CaptureTime(meta.CaptureTime) ?? FileTime(path),
                    CameraName(meta.Make, meta.Model), meta.LensModel,
                    meta.Iso is <= int.MaxValue ? (int?)meta.Iso : null, meta.ExposureTime, meta.FNumber);
            }
            var (taken, camera, lens, iso, exposure, fnumber) = ImageBackend.ReadExif(path);
            return new PhotoExif(name, taken ?? FileTime(path), camera, lens, iso, exposure, fnumber);
        }
        catch (Exception e) when (e is InvalidDataException or RawException or IOException or UnauthorizedAccessException
            || e.GetType().Name == "MagickException")
        {
            return new PhotoExif(name, FileTime(path), null, null, null, null, null);
        }
    }

    /// <summary>
    /// 1/4 秒以下寫成「1/250 s」（分母四捨五入）；更慢的快門只有倒數為整數時（1/2、1/3）才寫成分數，
    /// 其餘照實顯示秒數（0.6 s、0.8 s、2 s），不把 0.6 秒四捨五入成 1/2 秒。
    /// </summary>
    public string? ShutterText
    {
        get
        {
            if (ExposureSeconds is not { } t || t <= 0) return null;
            var reciprocal = 1 / t;
            var denominator = Math.Round(reciprocal);
            return denominator >= 2 && (t <= 0.25 || Math.Abs(reciprocal - denominator) < 0.01 * denominator)
                ? $"1/{denominator:0} s"
                : $"{t.ToString("0.#", CultureInfo.InvariantCulture)} s";
        }
    }

    public string? ApertureText => FNumber is { } f && f > 0 ? $"f/{f.ToString("0.#", CultureInfo.InvariantCulture)}" : null;

    public string? IsoText => Iso is { } iso && iso > 0 ? $"ISO {iso}" : null;

    /// <summary>ISO、快門、光圈合成一行，以「·」分隔；全部缺少時為 null。</summary>
    public string? ExposureText
    {
        get
        {
            var parts = new[] { IsoText, ShutterText, ApertureText }.Where(p => p is not null).ToArray();
            return parts.Length == 0 ? null : string.Join("  ·  ", parts);
        }
    }

    public string? TakenText => Taken?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>型號已含廠牌時不重複（例如 Canon／Canon EOS R5、NIKON CORPORATION／NIKON Z 6）。</summary>
    internal static string? CameraName(string? make, string? model)
    {
        if (string.IsNullOrEmpty(model)) return make;
        if (string.IsNullOrEmpty(make)) return model;
        var brand = make.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return model.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    internal static DateTime? ParseDate(string? value) =>
        value is not null && DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt) ? dt : null;

    private static DateTime? CaptureTime(RawDateTime? value)
    {
        if (value is not { } t) return null;
        try { return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Millis); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>EXIF 有理數字串：「1/250」「28/10」或小數。</summary>
    internal static double? ParseRational(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var slash = value.IndexOf('/');
        if (slash < 0)
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return double.TryParse(value[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
               && double.TryParse(value[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
               && den != 0 ? n / den : null;
    }

    /// <summary>ISO 可能是多值（「100, 0」），取第一個。</summary>
    internal static int? ParseInt(string? value)
    {
        var first = value?.Split(',', ' ')[0];
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private static DateTime? FileTime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
