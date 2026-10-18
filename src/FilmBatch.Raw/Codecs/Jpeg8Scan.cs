// 移植自 zune-jpeg 0.5.15（MIT OR Apache-2.0 OR Zlib；Copyright (c) 2023 zune-jpeg contributors）
// 的掃描迴圈與後處理（src/mcu.rs、mcu_prog.rs、worker.rs、upsampler.rs 選擇邏輯）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Codecs;

/// <summary>MCU 掃描與後處理（zune decode_mcu_ycbcr_baseline／decode_mcu_ycbcr_progressive＋post_process）。</summary>
internal static class JpegScan
{
    private enum Cont
    {
        Ok,
        AnotherSos,
        InterScan,
        Terminate,
    }

    public static void DecodeBaseline(ref Jpeg8.Decoder d, byte[] px)
    {
        SetupParams(ref d);
        d.CheckTables();
        int mcuW, mcuH;
        if (d.IsInterleaved)
        {
            d.SetUpsampling();
            mcuW = d.McuX;
            mcuH = d.McuY;
        }
        else
        {
            mcuW = DivCeil(d.Width, 8);
            mcuH = DivCeil(d.Height, 8);
        }
        if (d.IsInterleaved && NumIn(ref d) > 1 && d.OutCh == 1
            && (RatioOf(ref d) is Jpeg8.SampleRatio.V or Jpeg8.SampleRatio.Hv))
        {
            mcuH = mcuH * d.VMax / d.HMax;
            d.Coeff = 2;
        }
        if (d.Input == Jpeg8.InputCs.Luma && d.IsInterleaved)
        {
            d.ResetParams();
            mcuW = DivCeil(d.Width, 8);
            mcuH = DivCeil(d.Height, 8);
        }
        bool allFirst = d.NumScans == d.Comps.Count;
        var prog = new short[4][] { [], [], [], [] };
        if (!allFirst)
        {
            // zune decode_mcu_ycbcr_baseline：非交錯（各 SOS 只含部分元件）時先設 Needed、
            // 配整圖像素域暫存（mcuW×Hs×Vs×mcuH×64），不配列緩衝；解完所有掃描再 copy-only 收尾。
            MarkNeeded(ref d);
            for (int i = 0; i < d.Comps.Count; i++)
            {
                var c = d.Comps[i];
                long len = (long)mcuW * c.Hs * c.Vs * mcuH * 64;
                if (len > RawLimits.MaxSamples)
                    throw RawException.Limit("image too large");
                prog[i] = new short[(int)len];
            }
        }
        else
        {
            AllocateRows(ref d);
        }
        int padded = Jpeg8.Decoder.PaddedWidth(d.Width, RatioOf(ref d), GenericH(ref d));
        var scratch = new short[d.Comps.Count > 0 && d.IsInterleaved ? MaxStride(ref d) * 8 : 0];
        int written = 0;
        while (true)
        {
            for (int i = 0; i < mcuH; i++)
            {
                if (d.OverreadBy > 0)
                {
                    px.AsSpan(written).Fill(128);
                    break;
                }
                var t = DecodeMcuWidth(ref d, mcuW, i, allFirst, prog, d.NumScans);
                if (allFirst)
                    PostProcess(ref d, px, i, mcuH, padded, ref written, scratch);
                switch (t)
                {
                    case Cont.Ok:
                        break;
                    case Cont.AnotherSos:
                        goto NextSos;
                    case Cont.InterScan:
                        if (AdvanceToSos(ref d))
                            goto NextSos;
                        return;
                    case Cont.Terminate:
                        px.AsSpan(written).Fill(128);
                        return;
                }
            }
            break;
        NextSos:;
        }
        if (!allFirst)
            FinishBaseline(ref d, prog, px);
    }

    private static int NumIn(ref Jpeg8.Decoder d) => d.Input switch
    {
        Jpeg8.InputCs.Luma => 1,
        Jpeg8.InputCs.Cmyk or Jpeg8.InputCs.Ycck => 4,
        _ => 3,
    };

    private static void AllocateRows(ref Jpeg8.Decoder d)
    {
        MarkNeeded(ref d);
        for (int p = 0; p < d.Comps.Count; p++)
        {
            var c = d.Comps[p];
            if (!c.Needed)
                continue;
            long len = (long)c.WidthStride * c.Vs * 8;
            if (len > RawLimits.MaxSamples)
                throw RawException.Limit("image too large");
            c.RawCoeff = new short[(int)len];
        }
    }

    /// <summary>只標 Needed（zune mcu.rs 147-167 與 finish_baseline_decoding 341-355 的條件）。</summary>
    private static void MarkNeeded(ref Jpeg8.Decoder d)
    {
        for (int p = 0; p < d.Comps.Count; p++)
        {
            var c = d.Comps[p];
            c.Needed = Math.Min(d.OutCh - 1, p) == p
                || d.Input == Jpeg8.InputCs.Ycck || d.Input == Jpeg8.InputCs.Cmyk;
        }
    }

    private static Jpeg8.SampleRatio RatioOf(ref Jpeg8.Decoder d) =>
        d.Comps.Count == 0 ? Jpeg8.SampleRatio.None : HVOf(ref d);

    private static Jpeg8.SampleRatio HVOf(ref Jpeg8.Decoder d)
    {
        int hm = 1, vm = 1;
        foreach (var c in d.Comps)
        {
            hm = Math.Max(hm, c.Hs);
            vm = Math.Max(vm, c.Vs);
        }
        return (hm, vm) switch
        {
            (1, 1) => Jpeg8.SampleRatio.None,
            (2, 1) => Jpeg8.SampleRatio.H,
            (1, 2) => Jpeg8.SampleRatio.V,
            (2, 2) => Jpeg8.SampleRatio.Hv,
            _ => Jpeg8.SampleRatio.Generic,
        };
    }

    private static int GenericH(ref Jpeg8.Decoder d)
    {
        int hm = 1;
        foreach (var c in d.Comps)
            hm = Math.Max(hm, c.Hs);
        return hm;
    }

    private static int MaxStride(ref Jpeg8.Decoder d)
    {
        int m = 0;
        foreach (var c in d.Comps)
            m = Math.Max(m, c.WidthStride);
        return m;
    }

    private static void SetupParams(ref Jpeg8.Decoder d) => d.SetupComponentParams();

    private static Cont DecodeMcuWidth(ref Jpeg8.Decoder d, int mcuW, int row, bool interleavedNotProg, short[][] prog, int numScans)
    {
        int duW = mcuW;
        if (!interleavedNotProg)
        {
            int k0 = d.ZOrder[0];
            var c0 = d.Comps[k0];
            duW = DivCeil(d.Width * c0.Hs, d.HMax * 8);
            if (numScans > 1)
            {
                int min = int.MaxValue;
                for (int i = 0; i < numScans; i++)
                    min = Math.Min(min, d.Comps[d.ZOrder[i]].WidthStride / 8);
                duW = Math.Min(duW, min);
            }
        }
        bool oneByOne = !d.ScanSubsampled;
        for (int j = 0; j < duW; j++)
        {
            for (int s = 0; s < numScans; s++)
            {
                int k = d.ZOrder[s];
                var comp = d.Comps[k];
                var dc = ((uint)comp.DcTable < 4 ? d.DcHuff[comp.DcTable] : null) ?? throw RawException.Corrupt("lossy JPEG tile: missing DC Huffman table");
                var ac = ((uint)comp.AcTable < 4 ? d.AcHuff[comp.AcTable] : null) ?? throw RawException.Corrupt("lossy JPEG tile: missing AC Huffman table");
                // 上游 SAMPLED specialization：交錯 baseline 且掃描有子採樣時才走 Hs×Vs 內圈；
                // 非交錯（PROGRESSIVE=true）只走一次。
                bool sampled = !oneByOne && interleavedNotProg;
                int vN = sampled ? comp.Vs : 1;
                for (int v = 0; v < vN; v++)
                {
                    int hN = sampled ? comp.Hs : 1;
                    for (int h = 0; h < hN; h++)
                    {
                        int len;
                        if (comp.Needed)
                        {
                            Array.Clear(d.Tmp, 0, 64);
                            int pred = comp.DcPred;
                            try
                            {
                                len = d.DecodeMcuBlock(dc, ac, comp.Qt, d.Tmp, ref pred);
                            }
                            catch (RawException)
                            {
                                return Cont.Terminate;
                            }
                            comp.DcPred = pred;
                            int idctPos = interleavedNotProg
                                ? comp.WidthStride * (v * 8) + (j * comp.Hs + h) * 8
                                : j * 8;
                            Span<short> dst;
                            if (interleavedNotProg)
                            {
                                if ((uint)(idctPos + 7 * comp.WidthStride + 8) > (uint)comp.RawCoeff.Length)
                                    throw RawException.Corrupt("lossy JPEG tile: MCU past row buffer");
                                dst = comp.RawCoeff.AsSpan(idctPos);
                            }
                            else
                            {
                                int off = row * comp.WidthStride * 8 * comp.Vs;
                                if ((uint)(off + idctPos + 7 * comp.WidthStride + 8) > (uint)prog[k].Length)
                                    throw RawException.Corrupt("lossy JPEG tile: MCU past scan buffer");
                                dst = prog[k].AsSpan(off + idctPos);
                            }
                            if (len <= 1)
                                JpegDsp.Idct1x1(d.Tmp, dst, comp.WidthStride);
                            else if (len <= 10)
                                JpegDsp.Idct4x4(d.Tmp, dst, comp.WidthStride);
                            else
                                JpegDsp.Idct(d.Tmp, dst, comp.WidthStride);
                        }
                        else
                        {
                            try
                            {
                                d.DiscardMcuBlock(dc, ac);
                            }
                            catch (RawException)
                            {
                                return Cont.Terminate;
                            }
                        }
                    }
                }
            }
            d.Todo = unchecked(d.Todo - 1);
            if (d.Todo == 0)
            {
                HandleRstMain(ref d);
                continue;
            }
            if (d.Marker != -1 && d.BitsLeft == 0)
                break;
        }
        return AfterMcuWidth(ref d);
    }

    private static Cont AfterMcuWidth(ref Jpeg8.Decoder d)
    {
        if (d.Marker == -1)
            return Cont.Ok;
        int m = d.Marker;
        if (m == 0xD9)
        {
            d.Marker = -1;
            d.SeenEoi = true;
            return Cont.Ok;
        }
        if (m is >= 0xD0 and <= 0xD7)
        {
            if (d.Todo == 0)
                HandleRst(ref d);
            return Cont.Ok;
        }
        if (m == 0xDA)
        {
            d.Marker = -1;
            d.BitsReset();
            d.ParseMarker(0xDA);
            d.BitsReset();
            return Cont.AnotherSos;
        }
        if (m == 0xC4 || m == 0xDB || m == 0xDD || m == 0xFE || (m >= 0xE0 && m <= 0xEF))
        {
            d.PendingInterScan = m;
            d.Marker = -1;
            return Cont.InterScan;
        }
        d.Marker = -1;
        d.BitsReset();
        return Cont.Terminate;
    }

    /// <summary>解析 inter-scan marker 並找到下一個 SOS（zune advance_to_next_sos）。回傳 false 表 EOI。</summary>
    private static bool AdvanceToSos(ref Jpeg8.Decoder d)
    {
        d.ParseMarker(PendingMarker(ref d));
        d.BitsReset();
        for (int i = 0; i < 64; i++)
        {
            int m = GetMarker(ref d);
            if (m == 0xDA)
            {
                d.ParseMarker(0xDA);
                d.BitsReset();
                return true;
            }
            if (m == 0xD9)
            {
                d.SeenEoi = true;
                return false;
            }
            if (m == 0xC4 || m == 0xDB || m == 0xDD || m == 0xFE || (m >= 0xE0 && m <= 0xEF))
                d.ParseMarker(m);
        }
        throw RawException.Corrupt("lossy JPEG tile: too many markers between scans");
    }

    private static int PendingMarker(ref Jpeg8.Decoder d) => d.PendingInterScan;

    private static int GetMarker(ref Jpeg8.Decoder d)
    {
        if (d.Marker != -1)
        {
            int m = d.Marker;
            d.Marker = -1;
            return m;
        }
        while (!d.AtEnd)
        {
            int b = (int)d.ReadRaw();
            if (b == 0xFF)
            {
                int r = (int)d.ReadRaw();
                while (r == 0xFF)
                    r = (int)d.ReadRaw();
                if (r != 0)
                    return r;
            }
        }
        throw RawException.Corrupt("lossy JPEG tile: truncated stream");
    }

    /// <summary>重啟邊界處理（zune mcu.rs handle_rst）：RST 清碼流並歸零 DC 預測；
    /// EOI 保持 latch（SeenEoi 不清、Marker 不動），其後 Refill 全補零。</summary>
    private static void HandleRst(ref Jpeg8.Decoder d)
    {
        d.Todo = d.RestartInterval;
        if (d.Marker is >= 0xD0 and <= 0xD7)
        {
            d.Marker = -1;
            d.BitsReset();
            foreach (var c in d.Comps)
                c.DcPred = 0;
        }
        else if (d.Marker == 0xD9)
        {
            d.SeenEoi = true;
        }
    }

    private static void HandleRstMain(ref Jpeg8.Decoder d)
    {
        if (d.Todo == 0)
            d.Refill();
        if (d.Todo == 0 && d.RestartInterval != 0 && d.Marker == -1 && !d.SeenEoi)
        {
            try
            {
                int m = GetMarker(ref d);
                d.Marker = m;
            }
            catch (RawException)
            {
            }
        }
        if (d.Todo == 0)
            HandleRst(ref d);
    }

    /// <summary>非交錯 baseline 收尾（zune finish_baseline_decoding）：prog[] 已是像素域，
    /// 只配 Needed 列緩衝並 copy-only 搬運，再走 post-process；不可再解量化／IDCT。</summary>
    private static void FinishBaseline(ref Jpeg8.Decoder d, short[][] prog, byte[] px)
    {
        int mcuH = d.McuY;
        int padded = Jpeg8.Decoder.PaddedWidth(d.Width, RatioOf(ref d), GenericH(ref d));
        var scratch = new short[d.IsInterleaved ? MaxStride(ref d) : 0];
        // Needed 在 DecodeBaseline 已標；此處配列緩衝（zune 在 finish 內配，此處等價）。
        for (int p = 0; p < d.Comps.Count; p++)
        {
            var c = d.Comps[p];
            if (!c.Needed)
                continue;
            long len = (long)c.WidthStride * c.Vs * 8;
            if (len > RawLimits.MaxSamples)
                throw RawException.Limit("image too large");
            c.RawCoeff = new short[(int)len];
            c.IdctPos = 0;
        }
        int written = 0;
        for (int i = 0; i < mcuH; i++)
        {
            for (int p = 0; p < d.Comps.Count; p++)
            {
                var c = d.Comps[p];
                if (!c.Needed)
                    continue;
                int step = prog[p].Length / mcuH;
                var slice = prog[p].AsSpan(i * step, step);
                if ((uint)step > (uint)c.RawCoeff.Length)
                    throw RawException.Corrupt("lossy JPEG tile: bad scan buffer");
                slice.CopyTo(c.RawCoeff.AsSpan(0, step));
            }
            PostProcess(ref d, px, i, mcuH, padded, ref written, scratch);
        }
    }

    // ---- 後處理（zune post_process＋worker::color_convert） ----

    private static void PostProcess(ref Jpeg8.Decoder d, byte[] px, int row, int mcuH, int padded, ref int written, short[] scratch)
    {
        int outCh = d.OutCh;
        int compLen = d.Comps.Count;
        if (outCh < compLen && OutIsLuma(d))
            compLen = outCh;
        if (d.IsInterleaved && d.OutCh != 1)
        {
            bool vert = false;
            foreach (var c in d.Comps)
            {
                if (c.Ratio is Jpeg8.SampleRatio.V or Jpeg8.SampleRatio.Hv)
                    vert = true;
            }
            foreach (var c in d.Comps)
                Upsample(ref d, c, mcuH, row, scratch, vert);
            if (vert && row > 0)
            {
                var first = new short[4][];
                for (int i = 0; i < 4; i++)
                    first[i] = [];
                for (int i = 0; i < d.Comps.Count; i++)
                    first[i] = d.Comps[i].FirstRow;
                written += ConvertRows(ref d, first, compLen, px.AsSpan(written), d.Coeff * d.VMax);
            }
            if (vert)
            {
                foreach (var c in d.Comps)
                {
                    if (c.Ratio == Jpeg8.SampleRatio.H)
                        continue;
                    int sample = c.Ratio switch
                    {
                        Jpeg8.SampleRatio.Hv => 4,
                        Jpeg8.SampleRatio.V or Jpeg8.SampleRatio.H => 2,
                        Jpeg8.SampleRatio.Generic => c.GenericH * c.GenericV,
                        _ => 1,
                    };
                    int size = c.Vs * c.WidthStride * sample;
                    var last = c.RawCoeff.AsSpan(c.RawCoeff.Length - size, size);
                    last.CopyTo(c.FirstRow.AsSpan());
                }
            }
            var cur = new short[4][];
            for (int i = 0; i < 4; i++)
                cur[i] = [];
            for (int i = 0; i < d.Comps.Count; i++)
            {
                var c = d.Comps[i];
                cur[i] = c.Ratio == Jpeg8.SampleRatio.None ? c.RawCoeff : c.UpsampleDest;
            }
            bool last2 = vert && row != mcuH - 1;
            int iters = (8 - (last2 ? 1 : 0)) * d.Coeff * d.VMax;
            written += ConvertRows(ref d, cur, compLen, px.AsSpan(written), iters);
        }
        else
        {
            var ch = new short[4][];
            for (int i = 0; i < 4; i++)
                ch[i] = [];
            for (int i = 0; i < d.Comps.Count; i++)
                ch[i] = d.Comps[i].RawCoeff;
            int iters = 8 * d.Coeff;
            if (RatioOf(ref d) == Jpeg8.SampleRatio.Generic)
                iters = 8 * GenericV(ref d) * d.Coeff;
            written += ConvertRows(ref d, ch, compLen, px.AsSpan(written), iters);
        }
    }

    private static bool OutIsLuma(Jpeg8.Decoder d) => d.OutCh == 1;

    private static int GenericV(ref Jpeg8.Decoder d)
    {
        int vm = 1;
        foreach (var c in d.Comps)
            vm = Math.Max(vm, c.Vs);
        return vm;
    }

    /// <summary>轉換 numIters 個寬度列，回傳寫入位元組數（zune color_convert 呼叫端）。</summary>
    private static int ConvertRows(ref Jpeg8.Decoder d, short[][] ch, int compLen, Span<byte> dst, int numIters)
    {
        int w = d.Width;
        int padded = Jpeg8.Decoder.PaddedWidth(w, HVOf(ref d), GenericH(ref d));
        int pos = 0;
        for (int r = 0; r < numIters; r++)
        {
            if (pos + w * d.OutCh > dst.Length)
                break;
            ConvertRow(ref d, ch, compLen, w, padded, r, dst.Slice(pos, w * d.OutCh));
            pos += w * d.OutCh;
        }
        return pos;
    }

    private static void ConvertRow(ref Jpeg8.Decoder d, short[][] ch, int compLen, int w, int padded, int row, Span<byte> dst)
    {
        if (d.Input == Jpeg8.InputCs.Luma || (NumIn(ref d) == 1))
        {
            var y = ch[0].AsSpan(row * padded, w);
            if (d.OutCh == 1)
            {
                for (int i = 0; i < w; i++)
                    dst[i] = (byte)y[i];
            }
            else
            {
                for (int i = 0; i < w; i++)
                {
                    dst[i * 3] = (byte)y[i];
                    dst[i * 3 + 1] = (byte)y[i];
                    dst[i * 3 + 2] = (byte)y[i];
                }
            }
            return;
        }
        if ((d.Input == Jpeg8.InputCs.YCbCr || d.Input == Jpeg8.InputCs.Rgb) && d.OutCh == 1)
        {
            var y = ch[0].AsSpan(row * padded, w);
            for (int i = 0; i < w; i++)
                dst[i] = (byte)y[i];
            return;
        }
        if (d.Input == Jpeg8.InputCs.YCbCr && d.OutCh == 3)
        {
            var y = ch[0].AsSpan(row * padded, w);
            var cb = ch[1].AsSpan(row * padded, w);
            var cr = ch[2].AsSpan(row * padded, w);
            JpegDsp.YcbcrToRgb(y, cb, cr, dst, 3);
            return;
        }
        if (d.Input == Jpeg8.InputCs.Rgb)
        {
            var r = ch[0].AsSpan(row * padded, w);
            var g = ch[1].AsSpan(row * padded, w);
            var b = ch[2].AsSpan(row * padded, w);
            for (int i = 0; i < w; i++)
            {
                dst[i * 3] = (byte)r[i];
                dst[i * 3 + 1] = (byte)g[i];
                dst[i * 3 + 2] = (byte)b[i];
            }
            return;
        }
        if (d.Input == Jpeg8.InputCs.Cmyk)
        {
            var c = ch[0].AsSpan(row * padded, w);
            var m = ch[1].AsSpan(row * padded, w);
            var y = ch[2].AsSpan(row * padded, w);
            var k = ch[3].AsSpan(row * padded, w);
            for (int i = 0; i < w; i++)
            {
                dst[i * 3] = JpegDsp.Blinn8x8((byte)c[i], (byte)k[i]);
                dst[i * 3 + 1] = JpegDsp.Blinn8x8((byte)m[i], (byte)k[i]);
                dst[i * 3 + 2] = JpegDsp.Blinn8x8((byte)y[i], (byte)k[i]);
            }
            return;
        }
        if (d.Input == Jpeg8.InputCs.Ycck)
        {
            var y = ch[0].AsSpan(row * padded, w);
            var cb = ch[1].AsSpan(row * padded, w);
            var cr = ch[2].AsSpan(row * padded, w);
            var k = ch[3].AsSpan(row * padded, w);
            JpegDsp.YcbcrToRgb(y, cb, cr, dst, 3);
            for (int i = 0; i < w; i++)
            {
                byte kk = (byte)k[i];
                dst[i * 3] = JpegDsp.Blinn8x8((byte)(255 - dst[i * 3]), kk);
                dst[i * 3 + 1] = JpegDsp.Blinn8x8((byte)(255 - dst[i * 3 + 1]), kk);
                dst[i * 3 + 2] = JpegDsp.Blinn8x8((byte)(255 - dst[i * 3 + 2]), kk);
            }
            return;
        }
        // 2 元件等 MultiBand：照通道數直拷（zune copy_removing_padding_generic）。
        for (int i = 0; i < w; i++)
        {
            for (int c = 0; c < Math.Min(compLen, d.OutCh); c++)
                dst[i * d.OutCh + c] = (byte)ch[c][row * padded + i];
        }
    }

    /// <summary>hasVertical 轉交 H 臂做 FirstRow 刷新（zune worker.rs upsample 的 has_vertical_sample 參數）。</summary>
    private static void Upsample(ref Jpeg8.Decoder d, Jpeg8.JpegComponent c, int mcuH, int row, short[] scratch, bool hasVertical)
    {
        switch (c.Ratio)
        {
            case Jpeg8.SampleRatio.V:
            {
                // zune worker.rs upsample 的 V 臂（vertical upsampler，dest 每次寫兩列）。
                UpsampleVertical(ref d, c, mcuH, row);
                break;
            }
            case Jpeg8.SampleRatio.Hv:
            {
                // zune choose_hv_samp_function／upsample_hv（scalar.rs）：先垂直內插成兩列暫存，
                // 再對每列分別水平內插。dest 每次寫四列；首列延遲（FirstRow）呼叫不可少。
                UpsampleHv(ref d, c, mcuH, row, scratch);
                break;
            }
            case Jpeg8.SampleRatio.H:
            {
                if (c.UpsampleDest.Length != c.RawCoeff.Length * 2)
                    Array.Resize(ref c.UpsampleDest, c.RawCoeff.Length * 2);
                if (hasVertical)
                {
                    // zune worker.rs H 臂（515-540）：有任一元件垂直採樣時，
                    // 先把剛上採樣的目的尾列抄回 FirstRow，再逐列水平內插。
                    int length = c.FirstRow.Length;
                    c.UpsampleDest.AsSpan(c.UpsampleDest.Length - length, length).CopyTo(c.FirstRow.AsSpan());
                }
                for (int r = 0; r < c.RawCoeff.Length / c.WidthStride; r++)
                {
                    JpegDsp.UpsampleH(
                        c.RawCoeff.AsSpan(r * c.WidthStride, c.WidthStride),
                        c.UpsampleDest.AsSpan(r * c.WidthStride * 2, c.WidthStride * 2));
                }
                break;
            }
            case Jpeg8.SampleRatio.Generic:
            {
                int f = c.GenericH;
                int v = c.GenericV;
                if (c.UpsampleDest.Length != c.RawCoeff.Length * f * v)
                    Array.Resize(ref c.UpsampleDest, c.RawCoeff.Length * f * v);
                int rows = c.RawCoeff.Length / c.WidthStride;
                for (int r = 0; r < rows; r++)
                {
                    var src = c.RawCoeff.AsSpan(r * c.WidthStride, c.WidthStride);
                    var dstAll = c.UpsampleDest.AsSpan(r * c.WidthStride * f * v, c.WidthStride * f * v);
                    for (int vv = 0; vv < v; vv++)
                    {
                        var dstRow = dstAll.Slice(vv * c.WidthStride * f, c.WidthStride * f);
                        for (int i = 0; i < c.WidthStride; i++)
                        {
                            var dd = dstRow.Slice(i * f, f);
                            for (int j = 0; j < f; j++)
                                dd[j] = src[i];
                        }
                    }
                }
                break;
            }
            case Jpeg8.SampleRatio.None:
                break;
        }
    }

    /// <summary>垂直內插（zune upsample_vertical；供 V 元件）。</summary>
    private static void UpsampleVertical(ref Jpeg8.Decoder d, Jpeg8.JpegComponent c, int mcuH, int row)
    {
        _ = d;
        int strideOut = c.WidthStride * SampleOf(c);
        int dest = 0;
        if (row > 0)
        {
            JpegDsp.UpsampleV(c.Row, c.RowUp, c.RawCoeff.AsSpan(0, c.WidthStride), c.FirstRow.AsSpan(0, strideOut));
        }
        int stride = c.WidthStride * c.Vs;
        int stop = c.RawCoeff.Length / c.WidthStride;
        bool up = true;
        for (int pos = 0; pos < stop; pos++)
        {
            ReadOnlySpan<short> cur = c.RawCoeff.AsSpan(pos * c.WidthStride, c.WidthStride);
            ReadOnlySpan<short> up2;
            ReadOnlySpan<short> down;
            up = true;
            if (row == 0 && pos == 0)
            {
                up2 = c.RawCoeff.AsSpan(0, stride);
                down = c.RawCoeff.AsSpan(stride, stride);
            }
            else if (row > 0 && pos == 0)
            {
                up2 = c.Row;
                down = c.RawCoeff.AsSpan(stride, stride);
            }
            else if (row == mcuH - 1 && pos == stop - 1)
            {
                up2 = c.RawCoeff.AsSpan((pos - 1) * stride, stride);
                down = c.RawCoeff.AsSpan(pos * stride, stride);
            }
            else if (pos > 0 && pos < stop - 1)
            {
                up2 = c.RawCoeff.AsSpan((pos - 1) * stride, stride);
                down = c.RawCoeff.AsSpan((pos + 1) * stride, stride);
            }
            else if (pos == stop - 1)
            {
                c.RawCoeff.AsSpan((pos - 1) * stride, stride).CopyTo(c.RowUp.AsSpan());
                cur.CopyTo(c.Row.AsSpan());
                up = false;
                up2 = default;
                down = default;
            }
            else
            {
                throw RawException.Corrupt("lossy JPEG tile: upsample boundary");
            }
            if (up)
            {
                var dst = c.UpsampleDest.AsSpan(dest, strideOut);
                dest += strideOut;
                JpegDsp.UpsampleV(cur, up2, down, dst);
            }
        }
    }

    /// <summary>HV 內插（zune upsample_hv：先垂直後每列水平；供 HV 元件）。</summary>
    private static void UpsampleHv(ref Jpeg8.Decoder d, Jpeg8.JpegComponent c, int mcuH, int row, short[] scratch)
    {
        _ = d;
        // 每列輸入經 HV 內插成四列（zune scalar.rs upsample_hv：垂直得兩列，
        // 再各自水平加倍），故輸出步幅為 WidthStride×4。
        int strideOut = c.WidthStride * 4;
        int dest = 0;
        // 延遲的首列（zune worker.rs「i > 0」分支）：row／row_up 為上一個 MCU 存的行，
        // row_down 為本 MCU 首列；結果寫入 FirstRow。
        if (row > 0)
        {
            HvOneRow(c, c.Row, c.RowUp, c.RawCoeff.AsSpan(0, c.WidthStride), c.FirstRow.AsSpan(0, strideOut), scratch);
        }
        int stride = c.WidthStride * c.Vs;
        int stop = c.RawCoeff.Length / c.WidthStride;
        for (int pos = 0; pos < stop; pos++)
        {
            ReadOnlySpan<short> cur = c.RawCoeff.AsSpan(pos * c.WidthStride, c.WidthStride);
            bool up = true;
            ReadOnlySpan<short> up2 = default;
            ReadOnlySpan<short> down = default;
            if (row == 0 && pos == 0)
            {
                up2 = c.RawCoeff.AsSpan(0, stride);
                down = c.RawCoeff.AsSpan(stride, stride);
            }
            else if (row > 0 && pos == 0)
            {
                up2 = c.Row;
                down = c.RawCoeff.AsSpan(stride, stride);
            }
            else if (row == mcuH - 1 && pos == stop - 1)
            {
                up2 = c.RawCoeff.AsSpan((pos - 1) * stride, stride);
                down = c.RawCoeff.AsSpan(pos * stride, stride);
            }
            else if (pos > 0 && pos < stop - 1)
            {
                up2 = c.RawCoeff.AsSpan((pos - 1) * stride, stride);
                down = c.RawCoeff.AsSpan((pos + 1) * stride, stride);
            }
            else if (pos == stop - 1)
            {
                // 尾列暫存，待下一個 MCU 到齊 row_down 再補（zune 同名分支）。
                c.RawCoeff.AsSpan((pos - 1) * stride, stride).CopyTo(c.RowUp.AsSpan());
                cur.CopyTo(c.Row.AsSpan());
                up = false;
            }
            else
            {
                throw RawException.Corrupt("lossy JPEG tile: upsample boundary");
            }
            if (up)
            {
                HvOneRow(c, cur, up2, down, c.UpsampleDest.AsSpan(dest, strideOut), scratch);
                dest += strideOut;
            }
        }
    }

    /// <summary>單列 HV 內插（zune scalar.rs upsample_hv：垂直得兩列暫存，再各自水平加倍）。</summary>
    private static void HvOneRow(Jpeg8.JpegComponent c, ReadOnlySpan<short> cur, ReadOnlySpan<short> up, ReadOnlySpan<short> down, Span<short> dst, short[] scratch)
    {
        int w = c.WidthStride;
        var tmp = scratch.AsSpan(0, w * 2);
        JpegDsp.UpsampleHv(cur, up, down, tmp, dst);
    }

    private static int SampleOf(Jpeg8.JpegComponent c) => c.Ratio switch
    {
        Jpeg8.SampleRatio.Hv => 4,
        Jpeg8.SampleRatio.H or Jpeg8.SampleRatio.V => 2,
        Jpeg8.SampleRatio.Generic => c.GenericH * c.GenericV,
        _ => 1,
    };

    private static int DivCeil(int a, int b) => checked((a + b - 1) / b);

    // ---- progressive（zune decode_mcu_ycbcr_progressive） ----

    public static void DecodeProgressive(ref Jpeg8.Decoder d, byte[] px)
    {
        SetupParams(ref d);
        int mcuW, mcuH;
        if (d.IsInterleaved)
        {
            d.SetUpsampling();
            mcuW = d.McuX;
            mcuH = d.McuY;
        }
        else
        {
            mcuW = DivCeil(d.Width, 8);
            mcuH = DivCeil(d.Height, 8);
        }
        if (d.IsInterleaved && NumIn(ref d) > 1 && d.OutCh == 1
            && (RatioOf(ref d) is Jpeg8.SampleRatio.V or Jpeg8.SampleRatio.Hv))
        {
            mcuH = mcuH * d.VMax / d.HMax;
            d.Coeff = 2;
        }
        if (d.Input == Jpeg8.InputCs.Luma && d.IsInterleaved)
            d.ResetParams();
        int w64 = mcuW * 64;
        var block = new short[4][];
        for (int i = 0; i < 4; i++)
            block[i] = [];
        for (int i = 0; i < NumIn(ref d); i++)
        {
            var c = d.Comps[i];
            long len = (long)w64 * c.Vs * c.Hs * mcuH;
            if (len > RawLimits.MaxSamples)
                throw RawException.Limit("image too large");
            block[i] = new short[(int)len];
        }
        d.UpdateProgressiveParams(d.SuccLow, d.SpecStart, d.SpecEnd);
        try
        {
            ParseEntropy(ref d, block);
        }
        catch (RawException)
        {
            FinishProgressive(ref d, block, px);
            return;
        }
        int marker = d.Marker;
        d.Marker = -1;
        if (marker == -1)
            throw RawException.Corrupt("lossy JPEG tile: marker missing where expected");
        int scans = 1;
        while (marker != 0xD9)
        {
            if (marker == 0xDA)
            {
                d.ParseMarker(0xDA);
                d.UpdateProgressiveParams(d.SuccLow, d.SpecStart, d.SpecEnd);
                try
                {
                    ParseEntropy(ref d, block);
                }
                catch (RawException)
                {
                    break;
                }
                try
                {
                    marker = GetMarker(ref d);
                }
                catch (RawException)
                {
                    break;
                }
                if (++scans > 100)
                    throw RawException.Corrupt("lossy JPEG tile: too many scans");
                d.BitsReset();
                continue;
            }
            if (marker is >= 0xD0 and <= 0xD7)
            {
                HandleRst(ref d);
            }
            else
            {
                d.PendingInterScan = marker;
                if (!AdvanceToSos(ref d))
                    break;
                d.UpdateProgressiveParams(d.SuccLow, d.SpecStart, d.SpecEnd);
                try
                {
                    ParseEntropy(ref d, block);
                }
                catch (RawException)
                {
                    break;
                }
                try
                {
                    marker = GetMarker(ref d);
                }
                catch (RawException)
                {
                    break;
                }
                if (++scans > 100)
                    throw RawException.Corrupt("lossy JPEG tile: too many scans");
                d.BitsReset();
                continue;
            }
            try
            {
                marker = GetMarker(ref d);
            }
            catch (RawException)
            {
                break;
            }
        }
        FinishProgressive(ref d, block, px);
    }

    private static void ParseEntropy(ref Jpeg8.Decoder d, short[][] buf)
    {
        d.BitsReset();
        foreach (var c in d.Comps)
            c.DcPred = 0;
        d.Todo = d.RestartInterval != 0 ? d.RestartInterval : int.MaxValue;
        if (d.NumScans > NumIn(ref d))
            throw RawException.Corrupt($"lossy JPEG tile: {d.NumScans} scans for {NumIn(ref d)} components");
        if (d.NumScans == 1)
        {
            if (d.SpecEnd != 0 && d.SpecStart == 0)
                throw RawException.Corrupt("lossy JPEG tile: merged DC and AC scan");
            int k = d.ZOrder[0];
            if ((uint)k >= (uint)d.Comps.Count)
                throw RawException.Corrupt($"lossy JPEG tile: bad scan component {k}");
            var comp = d.Comps[k];
            int mw = DivCeil(d.Width * comp.Hs, d.HMax * 8);
            int mh = DivCeil(d.Height * comp.Vs, d.VMax * 8);
            for (int i = 0; i < mh; i++)
            {
                for (int j = 0; j < mw; j++)
                {
                    if (d.SpecStart != 0 && d.SuccHigh == 0 && d.EobRun > 0)
                    {
                        d.EobRun--;
                    }
                    else
                    {
                        int start = 64 * (j + i * (comp.WidthStride / 8));
                        if ((uint)(start + 64) > (uint)buf[k].Length)
                            throw RawException.Corrupt("lossy JPEG tile: scan past buffer");
                        var seg = buf[k].AsSpan(start, 64);
                        if (d.SpecStart == 0)
                        {
                            var dc = ((uint)comp.DcTable < 4 ? d.DcHuff[comp.DcTable] : null) ?? throw RawException.Corrupt("lossy JPEG tile: missing DC Huffman table");
                            int pred = comp.DcPred;
                            if (d.SuccHigh == 0)
                            {
                                d.DecodeDc(dc, ref pred);
                                seg[0] = unchecked((short)(pred * d.LowMask));
                            }
                            else
                            {
                                short b = seg[0];
                                d.DecodeProgDcRefine(ref b);
                                seg[0] = b;
                            }
                            comp.DcPred = pred;
                        }
                        else
                        {
                            var ac = ((uint)comp.AcTable < 4 ? d.AcHuff[comp.AcTable] : null) ?? throw RawException.Corrupt("lossy JPEG tile: missing AC Huffman table");
                            var tmp = new short[64];
                            seg.CopyTo(tmp);
                            if (d.SuccHigh == 0)
                                d.DecodeMcuAcFirst(ac, tmp);
                            else
                                d.DecodeMcuAcRefine(ac, tmp);
                            tmp.CopyTo(seg);
                        }
                    }
                    d.Todo = unchecked(d.Todo - 1);
                    HandleRstMain(ref d);
                }
            }
        }
        else
        {
            if (d.SpecEnd != 0)
                throw RawException.Corrupt("lossy JPEG tile: merged DC and AC scan");
            for (int k = 0; k < d.NumScans; k++)
            {
                int n = d.ZOrder[k];
                if ((uint)n >= (uint)d.Comps.Count)
                    throw RawException.Corrupt($"lossy JPEG tile: bad scan component {n}");
                var c = d.Comps[n];
                if ((uint)c.DcTable >= 4 || d.DcHuff[c.DcTable] is null)
                    throw RawException.Corrupt("lossy JPEG tile: missing DC Huffman table");
            }
            for (int i = 0; i < d.McuY; i++)
            {
                for (int j = 0; j < d.McuX; j++)
                {
                    for (int k = 0; k < d.NumScans; k++)
                    {
                        int n = d.ZOrder[k];
                        var c = d.Comps[n];
                        var huff = d.DcHuff[c.DcTable]!;
                        for (int v = 0; v < c.Vs; v++)
                        {
                            for (int h = 0; h < c.Hs; h++)
                            {
                                int x2 = j * c.Hs + h;
                                int y2 = i * c.Vs + v;
                                int pos = 64 * (x2 + y2 * c.WidthStride / 8);
                                if ((uint)(pos + 1) > (uint)buf[n].Length)
                                    throw RawException.Corrupt("lossy JPEG tile: scan past buffer");
                                int pred = c.DcPred;
                                if (d.SuccHigh == 0)
                                {
                                    d.DecodeDc(huff, ref pred);
                                    buf[n][pos] = unchecked((short)(pred * d.LowMask));
                                }
                                else
                                {
                                    short b = buf[n][pos];
                                    d.DecodeProgDcRefine(ref b);
                                    buf[n][pos] = b;
                                }
                                c.DcPred = pred;
                            }
                        }
                    }
                    d.Todo = unchecked(d.Todo - 1);
                    HandleRstMain(ref d);
                }
            }
        }
    }

    private static void FinishProgressive(ref Jpeg8.Decoder d, short[][] block, byte[] px)
    {
        int mcuH = d.IsInterleaved ? d.McuY : DivCeil(d.Height, 8);
        int padded = Jpeg8.Decoder.PaddedWidth(d.Width, RatioOf(ref d), GenericH(ref d));
        var scratch = new short[d.IsInterleaved ? MaxStride(ref d) : 0];
        for (int p = 0; p < d.Comps.Count; p++)
        {
            var c = d.Comps[p];
            bool need = Math.Min(d.OutCh - 1, p) == p
                || d.Input == Jpeg8.InputCs.Ycck || d.Input == Jpeg8.InputCs.Cmyk;
            c.Needed = need;
            if (need)
            {
                long len = (long)c.WidthStride * c.Vs * 8;
                if (len > RawLimits.MaxSamples)
                    throw RawException.Limit("image too large");
                c.RawCoeff = new short[(int)len];
                c.IdctPos = 0;
            }
            else
            {
                c.Needed = false;
            }
        }
        int written = 0;
        for (int i = 0; i < mcuH; i++)
        {
            for (int p = 0; p < d.Comps.Count; p++)
            {
                var c = d.Comps[p];
                if (!c.Needed)
                    continue;
                int step = block[p].Length / mcuH;
                if ((uint)(i * step + step) > (uint)block[p].Length)
                    throw RawException.Corrupt("lossy JPEG tile: bad scan buffer");
                var slice = block[p].AsSpan(i * step, step);
                int mcuX = c.WidthStride / 8;
                for (int k = 0; k < c.Vs; k++)
                {
                    for (int j = 0; j < mcuX; j++)
                    {
                        int start = j * 64 + k * 8 * c.WidthStride;
                        if ((uint)(start + 64) > (uint)slice.Length)
                            throw RawException.Corrupt("lossy JPEG tile: bad scan buffer");
                        var tmp = d.Tmp;
                        for (int n = 0; n < 64; n++)
                            tmp[n] = unchecked((int)slice[start + n] * c.Qt[n]);
                        if ((uint)(c.IdctPos + 7 * c.WidthStride + 8) > (uint)c.RawCoeff.Length)
                            throw RawException.Corrupt("lossy JPEG tile: MCU past row buffer");
                        JpegDsp.Idct(tmp, c.RawCoeff.AsSpan(c.IdctPos), c.WidthStride);
                        c.IdctPos += 8;
                    }
                    c.IdctPos += 7 * c.WidthStride;
                }
                c.IdctPos = 0;
            }
            PostProcess(ref d, px, i, mcuH, padded, ref written, scratch);
        }
    }
}
