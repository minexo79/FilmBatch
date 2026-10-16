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
            // JPEG 的 EXIF profile 展開成 exif:*；TIFF 的 EXIF 子目錄同樣是 exif:*，
            // 但 IFD0 的 Make／Model／DateTime 由 TIFF 解碼器寫成 tiff:make／tiff:model／tiff:timestamp
            string? Attr(params string[] keys) => keys
                .Select(k => image.GetAttribute(k)?.Trim().TrimEnd('\0').Trim())
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));

            var taken = ParseDate(Attr("exif:DateTimeOriginal", "exif:DateTimeDigitized", "exif:DateTime", "tiff:timestamp"));
            return new PhotoExif(
                name,
                taken ?? FileTime(path),
                CameraName(Attr("exif:Make", "tiff:make"), Attr("exif:Model", "tiff:model")),
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
