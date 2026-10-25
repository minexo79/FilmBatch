// 移植自 LightCraft crates/raw/src/demosaic/bilinear.rs（任意 CFA 雙線性插值）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Raster;

namespace FilmBatch.Raw.Develop;

public static partial class Demosaic
{
    private static (int Dx, int Dy)[][][] NeighbourTable(Mosaic m)
    {
        var cfa = m.Cfa; var table = new (int, int)[cfa.Width * cfa.Height][][];
        for (var py = 0; py < cfa.Height; py++) for (var px = 0; px < cfa.Width; px++)
        {
            var per = new (int, int)[3][];
            for (var c = 0; c < 3; c++)
            {
                var list = new List<(int, int)>();
                for (var r = 1; r <= 2; r++)
                {
                    for (var dy = -r; dy <= r; dy++) for (var dx = -r; dx <= r; dx++)
                        if (cfa.ColorAt(px + dx + 6 * cfa.Width, py + dy + 6 * cfa.Height) == c) list.Add((dx, dy));
                    if (list.Count != 0) break;
                }
                per[c] = list.ToArray();
            }
            table[py * cfa.Width + px] = per;
        }
        return table;
    }

    private static RgbImage Bilinear(Mosaic m)
    {
        var (w, h) = (m.W, m.H); var table = NeighbourTable(m); var cfa = m.Cfa; var outp = new RgbImage(w, h);
        Parallel.For(0, h, RawProcessingScope.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var own = cfa.ColorAt(x, y); var interior = x >= 2 && y >= 2 && x + 2 < w && y + 2 < h;
                var entry = table[y % cfa.Height * cfa.Width + x % cfa.Width];
                for (var c = 0; c < 3; c++)
                {
                    if (c == own) { outp.Data[(y * w + x) * 3 + c] = m.Data[y * w + x]; continue; }
                    var sum = 0f; var n = 0;
                    if (interior)
                    {
                        foreach (var (dx, dy) in entry[c]) { sum += m.Data[(y + dy) * w + x + dx]; n++; }
                    }
                    else
                    {
                        for (var r = 1; r <= 2; r++)
                        {
                            for (var dy = -r; dy <= r; dy++) for (var dx = -r; dx <= r; dx++)
                                if (m.Color(x + dx, y + dy) == c) { sum += m.Get(x + dx, y + dy); n++; }
                            if (n > 0) break;
                        }
                    }
                    outp.Data[(y * w + x) * 3 + c] = n > 0 ? sum / n : m.Data[y * w + x];
                }
            }
        });
        return outp;
    }
}
