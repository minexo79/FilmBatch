using System.Globalization;

namespace FilmBatch.Core.Film;

/// <summary>讀取 Resolve／Adobe 風格的 .cube 3D LUT。節點沿用檔案數值，色彩空間由呼叫端指定（預設 sRGB）。</summary>
public static class CubeLut
{
    public static FilmLut Parse(string text, FilmLutColorSpace colorSpace = FilmLutColorSpace.Srgb)
    {
        int? size = null;
        var domainMin = new[] { 0d, 0d, 0d };
        var domainMax = new[] { 1d, 1d, 1d };
        var samples = new List<float>(4096);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("TITLE", StringComparison.Ordinal)) continue;
            if (line.StartsWith("LUT_1D", StringComparison.Ordinal))
                throw new InvalidDataException("含 1D shaper 的 .cube 不能直接當成底片 LUT。");
            if (line.StartsWith("LUT_3D_SIZE", StringComparison.Ordinal))
            {
                size = int.Parse(LastToken(line), CultureInfo.InvariantCulture);
                continue;
            }
            if (line.StartsWith("DOMAIN_MIN", StringComparison.Ordinal))
            {
                Read3(line, domainMin);
                continue;
            }
            if (line.StartsWith("DOMAIN_MAX", StringComparison.Ordinal))
            {
                Read3(line, domainMax);
                continue;
            }
            if (line.StartsWith("LUT_3D_INPUT_RANGE", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3
                    || !Near01(double.Parse(parts[1], CultureInfo.InvariantCulture), 0)
                    || !Near01(double.Parse(parts[2], CultureInfo.InvariantCulture), 1))
                    throw new InvalidDataException("LUT_3D_INPUT_RANGE 不是 0–1。");
                continue;
            }
            var nums = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length != 3) throw new InvalidDataException($"無法解析 .cube 列：{line}");
            samples.Add(float.Parse(nums[0], CultureInfo.InvariantCulture));
            samples.Add(float.Parse(nums[1], CultureInfo.InvariantCulture));
            samples.Add(float.Parse(nums[2], CultureInfo.InvariantCulture));
        }
        if (size is not >= 2) throw new InvalidDataException(".cube 缺少 LUT_3D_SIZE。");
        if (samples.Count != size * size * size * 3)
            throw new InvalidDataException($".cube 樣本數 {samples.Count / 3} 與 {size}³ 不符。");
        for (var i = 0; i < 3; i++)
            if (!Near01(domainMin[i], 0) || !Near01(domainMax[i], 1))
                throw new InvalidDataException("DOMAIN 不是 0–1，此檔不是 sRGB 全範圍 LUT。");
        var lut = new FilmLut(size.Value, samples.ToArray(), colorSpace);
        return size.Value > FilmLutFile.MaxSize ? lut.Resample(FilmLutFile.MaxSize) : lut;
    }

    private static bool Near01(double value, double target) => Math.Abs(value - target) <= 1e-4;

    private static string LastToken(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];

    private static void Read3(string line, double[] into)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) throw new InvalidDataException($"無法解析 {parts[0]}。");
        for (var i = 0; i < 3; i++) into[i] = double.Parse(parts[i + 1], CultureInfo.InvariantCulture);
    }
}
