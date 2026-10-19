// 移植自 LightCraft crates/raw/src/ljpeg.rs（解碼側的 4:2:0/4:2:2 子取樣變體：Sony M/S ARW、Canon sRAW/mRAW）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Codecs;

internal static partial class Ljpeg
{
    /// <summary>子取樣框的首欄預測與遞迴預測器的選擇方式（Rust <c>ljpeg::Prediction</c>）。</summary>
    public enum Prediction
    {
        /// <summary>Sony：平面內的左鄰；首欄取上方樣本，但 MCU 列的頂端 luma 列讀上一個 MCU 列的頂端 luma 樣本。</summary>
        Geometric,
        /// <summary>Canon：每個元件各自為一條依解碼序的一維鏈；MCU 列首樣本接續上一個 MCU 列的首樣本。</summary>
        Sequential,
    }

    /// <summary>子取樣框：Y 全解析度；Cb/Cr 寬半、垂直 1× 或 2× 子取樣（Rust <c>ljpeg::FrameSubsampled</c>）。</summary>
    public sealed class FrameSubsampled
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int VerticalSubsampling { get; init; }

        /// <summary>[0] = Y 全解析度；[1]/[2] = Cb/Cr（寬半）。</summary>
        public ushort[][] Planes { get; init; } = [];
    }

    private static int CheckSubsampled(Header h)
    {
        var vertical = h.Sampling switch
        {
            [0x22, 0x11, 0x11] => 2,
            [0x21, 0x11, 0x11] => 1,
            _ => throw RawException.Unsupported("lossless JPEG subsampling layout"),
        };
        if (h.Predictor != 1 || h.Restart != 0)
            throw RawException.Unsupported("lossless JPEG subsampled predictor / restarts");
        if (h.Width == 0 || h.Height == 0 || h.Width % 2 != 0 || h.Height % vertical != 0)
            throw Err("subsampled frame dimensions do not match MCU size");
        return vertical;
    }

    /// <summary>Sony M/S tile 與 Canon sRAW/mRAW：(width, height, 色度平面的垂直子取樣)。子取樣平面保持分離。</summary>
    internal static (int Width, int Height, int Vertical) FrameInfoSubsampled(ReadOnlySpan<byte> d)
    {
        var h = ParseHeader(d);
        var vertical = CheckSubsampled(h);
        return (h.Width, h.Height, vertical);
    }

    /// <summary>
    /// Sony（M/S ARW）與 Canon（sRAW/mRAW）的 4:2:0/4:2:2 LJ92 變體：T.81 MCU 順序與差值，mod 2^16。
    /// <paramref name="prediction"/> 選擇各家使用的預測器。
    /// Sony：首欄時頂端 luma 列由上一個 MCU 列的頂端樣本預測（兩條影像列之上），底端列由當前列頂端樣本預測。
    /// </summary>
    internal static FrameSubsampled DecodeSubsampled(ReadOnlySpan<byte> d, int maxSamples, Prediction prediction)
    {
        var h = ParseHeader(d);
        var vertical = CheckSubsampled(h);
        var pixels = (long)h.Width * h.Height;
        var chroma = pixels / (2 * vertical);
        var total = pixels + chroma * 2;
        if (total > maxSamples)
            throw RawException.Limit("lossless JPEG frame larger than expected");
        var entropy = d[h.ScanStart..];
        if (((long)entropy.Length + 64) * 8 < total)
            throw Err("entropy data too short for frame");
        var tables = new Huffman[h.TableFor.Count];
        for (var i = 0; i < tables.Length; i++)
            tables[i] = h.TableFor[i] < 4 && h.Tables[h.TableFor[i]] is { } t ? t : throw Err("missing Huffman table");
        var planes = new[] { new ushort[pixels], new ushort[chroma], new ushort[chroma] };
        var br = new BitReader(entropy);
        var init = 1 << (h.Precision - h.Pt - 1);
        // Sequential 預測：各元件的最後一個樣本與其上一個 MCU 列的第一個樣本
        var last = new[] { init, init, init };
        var rowFirst = new[] { init, init, init };
        for (var my = 0; my < h.Height / vertical; my++)
        {
            for (var mx = 0; mx < h.Width / 2; mx++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var plane = planes[c];
                    var horizontalFactor = c == 0 ? 2 : 1;
                    var verticalFactor = c == 0 ? vertical : 1;
                    var width = h.Width * horizontalFactor / 2;
                    for (var dy = 0; dy < verticalFactor; dy++)
                    {
                        for (var dx = 0; dx < horizontalFactor; dx++)
                        {
                            var x = mx * horizontalFactor + dx;
                            var y = my * verticalFactor + dy;
                            var i = y * width + x;
                            int pred;
                            if (prediction == Prediction.Geometric)
                            {
                                if (x > 0)
                                    pred = plane[i - 1];
                                else if (c == 0 && dy == 0 && y >= vertical)
                                    pred = plane[i - vertical * width];
                                else if (y > 0)
                                    pred = plane[i - width];
                                else
                                    pred = init;
                            }
                            else if (mx == 0 && dx == 0 && dy == 0)
                            {
                                pred = rowFirst[c];
                            }
                            else
                            {
                                pred = last[c];
                            }
                            var ssss = tables[c].Decode(ref br);
                            if (ssss > 16)
                                throw Err("invalid difference category");
                            var value = (ushort)((pred + DiffValue(ref br, ssss)) & 0xffff);
                            plane[i] = value;
                            last[c] = value;
                            if (mx == 0 && dx == 0 && dy == 0)
                                rowFirst[c] = value;
                        }
                    }
                }
            }
            if (br.Overrun > br.BitsLeft)
                throw Err("entropy data exhausted");
        }
        if (h.Pt > 0)
        {
            foreach (var plane in planes)
                for (var i = 0; i < plane.Length; i++)
                    plane[i] <<= h.Pt;
        }
        return new FrameSubsampled { Width = h.Width, Height = h.Height, VerticalSubsampling = vertical, Planes = planes };
    }
}
