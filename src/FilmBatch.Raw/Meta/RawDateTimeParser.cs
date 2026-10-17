// 移植自 LightCraft crates/meta/src/datetime.rs（Exif 拍攝時間解析）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Meta;

/// <summary><c>DateTime::parse_exif / parse_offset / with_subsec</c>：產生契約型別 <see cref="RawDateTime"/>。</summary>
public static class RawDateTimeParser
{
    private static int? Num(string s)
    {
        if (s.Length == 0) return null;
        foreach (char c in s) if (c is not (>= '0' and <= '9')) return null;
        return int.TryParse(s, out int v) ? v : null;
    }
    private static bool Valid(RawDateTime t)
    {
        bool leap = t.Year % 4 == 0 && (t.Year % 100 != 0 || t.Year % 400 == 0);
        int maxDay = t.Month switch { 1 or 3 or 5 or 7 or 8 or 10 or 12 => 31, 4 or 6 or 9 or 11 => 30, 2 when leap => 29, 2 => 28, _ => 0 };
        return t.Day >= 1 && t.Day <= maxDay && t.Hour < 24 && t.Minute < 60 && t.Second < 61 && t.Year > 0 &&
            (t.OffsetMinutes is not { } o || Math.Abs(o) <= 18 * 60);
    }

    /// Exif form `YYYY:MM:DD HH:MM:SS` (also tolerates `-` date separators and a missing time).
    public static RawDateTime? ParseExif(string s)
    {
        s = s.Trim().TrimEnd('\0');
        int space = s.IndexOfAny(' ', 'T');
        string d = space < 0 ? s : s[..space], t = space < 0 ? "" : s[(space + 1)..];
        var dp = d.Split(':', '-');
        if (dp.Length != 3) return null;
        var tp = t.Length == 0 ? [] : t.Split(':');
        int? G(int i) => i < tp.Length ? Num(tp[i].Trim()) : 0;
        int? year = Num(dp[0]), month = Num(dp[1]), day = Num(dp[2]), hour = G(0), minute = G(1), second = G(2);
        if (year is not { } y || month is not { } mo || day is not { } dd || hour is not { } hh || minute is not { } mi || second is not { } se ||
            y > 65535 || mo > 255 || dd > 255 || hh > 255 || mi > 255 || se > 255) return null;
        var result = new RawDateTime(y, mo, dd, hh, mi, se, 0, null);
        return Valid(result) ? result : null;
    }

    /// Exif `OffsetTime*` form `+HH:MM` / `-HH:MM` (or `Z`).
    public static int? ParseOffset(string s)
    {
        s = s.Trim().TrimEnd('\0');
        if (s == "Z") return 0;
        if (s.Length == 0 || s[0] is not ('+' or '-')) return null;
        int sign = s[0] == '+' ? 1 : -1;
        string rest = s[1..];
        int? h, m;
        int colon = rest.IndexOf(':');
        if (colon >= 0) { h = Num(rest[..colon]); m = Num(rest[(colon + 1)..]); }
        else if (rest.Length == 4) { h = Num(rest[..2]); m = Num(rest[2..]); }
        else { h = Num(rest); m = 0; }
        return h is not { } hh || m is not { } mm || hh > 18 || mm > 59 ? null : sign * (hh * 60 + mm);
    }

    /// Set the sub-second part from an Exif `SubSecTime*` string (digits are a decimal fraction).
    public static RawDateTime WithSubsec(RawDateTime t, string s)
    {
        var digits = s.Trim().TakeWhile(c => c is >= '0' and <= '9').Take(3).ToArray();
        if (digits.Length == 0) return t;
        int v = int.Parse(digits) * (int)Math.Pow(10, 3 - digits.Length);
        return t with { Millis = v };
    }
}
