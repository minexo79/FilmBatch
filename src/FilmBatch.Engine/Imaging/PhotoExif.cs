using System.Globalization;
using ImageMagick;

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
    /// <summary>只讀檔頭（Ping，不解碼像素）。EXIF 讀不到時仍回傳檔名，日期改用檔案修改時間。</summary>
    public static PhotoExif Read(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            // 以屬性讀取：JPEG 由 EXIF profile 展開、TIFF 由解碼器直接寫入，兩者都是 exif:* 名稱
            string? Attr(params string[] keys) => keys
                .Select(k => image.GetAttribute(k)?.Trim().TrimEnd('\0').Trim())
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));

            var taken = ParseDate(Attr("exif:DateTimeOriginal", "exif:DateTimeDigitized", "exif:DateTime"));
            return new PhotoExif(
                name,
                taken ?? FileTime(path),
                CameraName(Attr("exif:Make"), Attr("exif:Model")),
                Attr("exif:LensModel", "exif:Lens"),
                ParseInt(Attr("exif:PhotographicSensitivity", "exif:ISOSpeedRatings")),
                ParseRational(Attr("exif:ExposureTime")),
                ParseRational(Attr("exif:FNumber")));
        }
        catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException)
        {
            return new PhotoExif(name, FileTime(path), null, null, null, null, null);
        }
    }

    /// <summary>「1/250 s」或「2 s」。</summary>
    public string? ShutterText => ExposureSeconds is not { } t || t <= 0 ? null
        : t < 1 ? $"1/{Math.Round(1 / t):0} s"
        : $"{t.ToString("0.#", CultureInfo.InvariantCulture)} s";

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
    private static string? CameraName(string? make, string? model)
    {
        if (string.IsNullOrEmpty(model)) return make;
        if (string.IsNullOrEmpty(make)) return model;
        var brand = make.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return model.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    private static DateTime? ParseDate(string? value) =>
        value is not null && DateTime.TryParseExact(value, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt) ? dt : null;

    /// <summary>EXIF 有理數字串：「1/250」「28/10」或小數。</summary>
    private static double? ParseRational(string? value)
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
    private static int? ParseInt(string? value)
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
