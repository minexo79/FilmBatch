// 移植自 jpeg-decoder 0.3.2（MIT OR Apache-2.0）`decoder.rs::decode_scan`（DCT 掃描迴圈）、
// `decode_block`／`decode_block_successive_approximation`／`refine_non_zeroes`（係數解碼）、
// `worker/immediate.rs`（列 worker：start/append_row/get_result）、`huffman.rs`
// `fill_default_mjpeg_tables`（MJPEG 預設表），以及 `decoder/lossless.rs::decode_scan_lossless`
//（無損掃描＋預測）。單執行緒：上游把 MCU 列送 worker 的批次行為改為掃完即時 IDCT，
// 但係數緩衝的送出行為（交錯／非交錯何時送列）與輸出位元組完全一致。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using static FilmBatch.Raw.Codecs.JpegDecoderPort;

namespace FilmBatch.Raw.Codecs;

/// <summary>DCT／無損掃描解碼（上游 `decode_scan*`＋列 worker）。</summary>
internal static class JpegScanPort
{
    internal static readonly byte[] UnZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>
    /// 列 worker 狀態（上游 `ImmediateWorker`）：每分量一塊輸出＋偏移＋分量／量化表。
    /// Non-interleaved 漸進式在 `finished` 前不送列（上游 `finished[i]` 語意由呼叫端處理）。
    /// </summary>
    internal sealed class RowWorker
    {
        public int[] Offsets = new int[4];
        public byte[][] Results = [Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>()];
        public Component?[] Comps = new Component?[4];
        public ushort[]?[] Qts = new ushort[]?[4];

        public void Start(int index, Component c, ushort[] qt)
        {
            if (Results[index].Length != 0)
                throw RawException.Corrupt("preview JPEG: worker restarted without result");
            Offsets[index] = 0;
            long size = (long)c.BlockW * c.BlockH * c.DctScale * c.DctScale;
            if (size > RawLimits.MaxSamples)
                throw RawException.Limit("preview JPEG: image too large");
            Results[index] = new byte[(int)size];
            Comps[index] = c;
            Qts[index] = qt;
        }

        public void AppendRow(int index, short[] data)
        {
            var c = Comps[index] ?? throw RawException.Corrupt("preview JPEG: worker row without start");
            var qt = Qts[index] ?? throw RawException.Corrupt("preview JPEG: worker row without start");
            int blockCount = c.BlockW * c.V;
            int lineStride = c.BlockW * c.DctScale;
            if (data.Length != blockCount * 64)
                throw RawException.Corrupt("preview JPEG: bad MCU row length");
            for (int i = 0; i < blockCount; i++)
            {
                int x = (i % c.BlockW) * c.DctScale;
                int y = (i / c.BlockW) * c.DctScale;
                var coeff = new short[64];
                Array.Copy(data, i * 64, coeff, 0, 64);
                JpegIdct.DequantizeAndIdctBlock(c.DctScale, coeff, qt, lineStride, Results[index], Offsets[index] + y * lineStride + x);
            }
            Offsets[index] += blockCount * c.DctScale * c.DctScale;
        }

        public byte[] Take(int index)
        {
            var r = Results[index];
            Results[index] = [];
            Comps[index] = null;
            Qts[index] = null;
            return r;
        }
    }

    internal sealed class ScanContext
    {
        public Component[] FrameComps = [];
        public CodingProcess Process;
        public ushort[]?[] Qts = new ushort[]?[4];
        public HuffTable?[] DcHuff = new HuffTable?[4];
        public HuffTable?[] AcHuff = new HuffTable?[4];
        public ushort RestartInterval;
        public bool IsMjpeg;
        public int ImageW;
        public int ImageH;
        public int McuW;
        public int McuH;
        // 漸進式全域係數（上游 Decoder.coefficients／coefficients_finished）。
        public short[][] Coeffs = [];
        public ulong[] CoeffsFinished = new ulong[4];
    }

    /// <summary>
    /// DCT 掃描（上游 `decode_scan`）：回傳掃描後的 marker（上游 `take_marker` 語意：
    /// 暫存 marker；若為 RST 則再讀下一個）與完成分量的平面（未完成為 null）。
    /// <c>finished</c> 為呼叫端按上游 `decode_internal` 計算的完成旗標（本掃描完成的分量）。
    /// </summary>
    internal static (int Marker, byte[][]? Planes) DecodeScan(
        ref BitReader br, ScanContext ctx, ScanInfo scan, Component[] comps, bool[] finished, RowWorker worker)
    {
        foreach (var c in comps)
        {
            if (ctx.Qts[c.Tq] is null)
                throw RawException.Corrupt("preview JPEG: use of unset quantization table");
        }
        if (ctx.IsMjpeg)
            FillMjpegTables(scan, ctx);
        if (scan.SpecStart == 0)
        {
            foreach (int t in scan.DcTbl)
            {
                if (ctx.DcHuff[t] is null)
                    throw RawException.Corrupt("preview JPEG: scan makes use of unset dc Huffman table");
            }
        }
        if (scan.SpecEnd > 1)
        {
            foreach (int t in scan.AcTbl)
            {
                if (ctx.AcHuff[t] is null)
                    throw RawException.Corrupt("preview JPEG: scan makes use of unset ac Huffman table");
            }
        }
        for (int i = 0; i < comps.Length; i++)
        {
            if (finished[i])
                worker.Start(i, comps[i], ctx.Qts[comps[i].Tq]!);
        }
        bool progressive = ctx.Process == CodingProcess.DctProgressive;
        bool interleaved = comps.Length > 1;
        var dcPred = new short[4];
        int mcusLeft = ctx.RestartInterval;
        int expectedRst = 0;
        ushort eobRun = 0;
        short[][] rowCoeffs = new short[comps.Length][];
        if (!progressive)
        {
            for (int i = 0; i < comps.Length; i++)
            {
                if (!finished[i])
                    continue;
                int n = comps[i].BlockW * comps[i].V * 64;
                if (n > RawLimits.MaxSamples)
                    throw RawException.Limit("preview JPEG: image too large");
                rowCoeffs[i] = new short[n];
            }
        }
        int[] mcuHs = new int[comps.Length];
        int[] mcuVs = new int[comps.Length];
        for (int i = 0; i < comps.Length; i++)
        {
            if (interleaved)
            {
                mcuHs[i] = comps[i].H;
                mcuVs[i] = comps[i].V;
            }
            else
            {
                mcuHs[i] = 1;
                mcuVs[i] = 1;
            }
        }
        int maxMcuX = interleaved ? ctx.McuW : comps[0].BlockW;
        int maxMcuY = interleaved ? ctx.McuH : comps[0].BlockH;
        var dummy = new short[64];
        for (int mcuY = 0; mcuY < maxMcuY; mcuY++)
        {
            if (mcuY * 8 >= ctx.ImageH)
                break;
            for (int mcuX = 0; mcuX < maxMcuX; mcuX++)
            {
                if (mcuX * 8 >= ctx.ImageW)
                    break;
                if (ctx.RestartInterval > 0)
                {
                    if (mcusLeft == 0)
                    {
                        int m = br.TakeMarker();
                        if (m < 0xD0 || m > 0xD7)
                        {
                            throw RawException.Corrupt(m < 0
                                ? $"preview JPEG: no marker found where RST{expectedRst} was expected"
                                : $"preview JPEG: found marker 0xFF{m:X2} inside scan where RST{expectedRst} was expected");
                        }
                        if ((m & 7) != expectedRst)
                            throw RawException.Corrupt($"preview JPEG: found RST{m & 7} where RST{expectedRst} was expected");
                        br.Reset();
                        Array.Clear(dcPred);
                        eobRun = 0;
                        expectedRst = (expectedRst + 1) % 8;
                        mcusLeft = ctx.RestartInterval;
                    }
                    mcusLeft--;
                }
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    for (int v = 0; v < mcuVs[i]; v++)
                    {
                        for (int hh = 0; hh < mcuHs[i]; hh++)
                        {
                            short[] block;
                            if (progressive)
                            {
                                int by = mcuY * mcuVs[i] + v;
                                int bx = mcuX * mcuHs[i] + hh;
                                int off = (by * c.BlockW + bx) * 64;
                                var global = ctx.Coeffs[scan.CompIdx[i]];
                                block = new short[64];
                                Array.Copy(global, off, block, 0, 64);
                                if (scan.SuccHigh == 0)
                                    DecodeBlock(ref br, block,
                                        ctx.DcHuff[scan.DcTbl[i]], ctx.AcHuff[scan.AcTbl[i]],
                                        scan.SpecStart, scan.SpecEnd, scan.SuccLow, ref eobRun, ref dcPred[i]);
                                else
                                    DecodeBlockRefine(ref br, block,
                                        ctx.AcHuff[scan.AcTbl[i]],
                                        scan.SpecStart, scan.SpecEnd, scan.SuccLow, ref eobRun);
                                Array.Copy(block, 0, global, off, 64);
                            }
                            else if (finished[i])
                            {
                                int batchRow = interleaved ? 0 : mcuY % c.V;
                                int by = batchRow * mcuVs[i] + v;
                                int bx = mcuX * mcuHs[i] + hh;
                                int off = (by * c.BlockW + bx) * 64;
                                block = new short[64];
                                Array.Copy(rowCoeffs[i], off, block, 0, 64);
                                if (scan.SuccHigh == 0)
                                    DecodeBlock(ref br, block,
                                        ctx.DcHuff[scan.DcTbl[i]], ctx.AcHuff[scan.AcTbl[i]],
                                        scan.SpecStart, scan.SpecEnd, scan.SuccLow, ref eobRun, ref dcPred[i]);
                                else
                                    DecodeBlockRefine(ref br, block,
                                        ctx.AcHuff[scan.AcTbl[i]],
                                        scan.SpecStart, scan.SpecEnd, scan.SuccLow, ref eobRun);
                                Array.Copy(block, 0, rowCoeffs[i], off, 64);
                            }
                            else
                            {
                                Array.Clear(dummy);
                                if (scan.SuccHigh == 0)
                                    DecodeBlock(ref br, dummy,
                                        ctx.DcHuff[scan.DcTbl[i]], ctx.AcHuff[scan.AcTbl[i]],
                                        scan.SpecStart, scan.SpecEnd, scan.SuccLow, ref eobRun, ref dcPred[i]);
                                else
                                    DecodeBlockRefine(ref br, dummy,
                                        ctx.AcHuff[scan.AcTbl[i]],
                                        scan.SpecStart, scan.SpecEnd, scan.SuccLow, ref eobRun);
                            }
                        }
                    }
                }
            }
            for (int i = 0; i < comps.Length; i++)
            {
                if (!finished[i])
                    continue;
                var c = comps[i];
                if (!interleaved && (mcuY + 1) * 8 < ctx.ImageH && (mcuY + 1) % c.V > 0)
                    continue;
                int perRow = c.BlockW * c.V * 64;
                short[] row;
                if (progressive)
                {
                    int workerY = interleaved ? mcuY : mcuY / c.V;
                    int off = workerY * perRow;
                    var global = ctx.Coeffs[scan.CompIdx[i]];
                    row = new short[perRow];
                    Array.Copy(global, off, row, 0, perRow);
                }
                else
                {
                    row = rowCoeffs[i];
                    int n = c.BlockW * c.V * 64;
                    if (n > RawLimits.MaxSamples)
                        throw RawException.Limit("preview JPEG: image too large");
                    rowCoeffs[i] = new short[n];
                }
                worker.AppendRow(i, row);
            }
        }
        // 掃描尾 marker 原樣回傳（上游 take_marker 語意）：RST 由主迴圈忽略（前一 marker 為 SOS），
        // EOI 結束，其餘由主迴圈分派。不可在此把 RST 壓成 -1，否則主迴圈會越過 EOI 繼續讀檔。
        int marker = br.TakeMarker();
        bool any = false;
        foreach (bool f in finished)
            any |= f;
        if (!any)
            return (marker, null);
        var planes = new byte[ctx.FrameComps.Length][];
        for (int i = 0; i < planes.Length; i++)
            planes[i] = [];
        for (int i = 0; i < scan.CompIdx.Length; i++)
        {
            if (finished[i])
                planes[scan.CompIdx[i]] = worker.Take(i);
        }
        return (marker, planes);
    }

    internal static void DecodeBlock(ref BitReader br, short[] coeff, HuffTable? dcTable, HuffTable? acTable,
        int specStart, int specEnd, int succLow, ref ushort eobRun, ref short dcPred)
    {
        if (specStart == 0)
        {
            int value = br.Decode(dcTable!);
            short diff = value switch
            {
                0 => 0,
                >= 1 and <= 11 => br.ReceiveExtend((byte)value),
                _ => throw RawException.Corrupt("preview JPEG: invalid DC difference magnitude category"),
            };
            dcPred = unchecked((short)(dcPred + diff));
            coeff[0] = unchecked((short)(dcPred << succLow));
        }
        int index = Math.Max(specStart, 1);
        if (index < specEnd && eobRun > 0)
        {
            eobRun--;
            return;
        }
        while (index < specEnd)
        {
            if (br.TryFastAc(acTable!, out short v, out byte run))
            {
                index += run;
                if (index >= specEnd)
                    break;
                coeff[UnZigZag[index]] = unchecked((short)(v << succLow));
                index++;
            }
            else
            {
                int b = br.Decode(acTable!);
                int r = b >> 4, s = b & 0x0F;
                if (s == 0)
                {
                    if (r == 15)
                    {
                        index += 16;
                    }
                    else
                    {
                        eobRun = (ushort)((1 << r) - 1);
                        if (r > 0)
                            eobRun += br.GetBits(r);
                        break;
                    }
                }
                else
                {
                    index += r;
                    if (index >= specEnd)
                        break;
                    coeff[UnZigZag[index]] = unchecked((short)(br.ReceiveExtend((byte)s) << succLow));
                    index++;
                }
            }
        }
    }

    internal static void DecodeBlockRefine(ref BitReader br, short[] coeff, HuffTable? acTable,
        int specStart, int specEnd, int succLow, ref ushort eobRun)
    {
        short bit = (short)(1 << succLow);
        if (specStart == 0)
        {
            if (br.GetBits(1) == 1)
                coeff[0] |= bit;
            return;
        }
        if (eobRun > 0)
        {
            eobRun--;
            RefineNonZeroes(ref br, coeff, specStart, specEnd, 64, bit);
            return;
        }
        int index = specStart;
        while (index < specEnd)
        {
            int b = br.Decode(acTable!);
            int r = b >> 4, s = b & 0x0F;
            int zrl = r;
            short value = 0;
            switch (s)
            {
                case 0 when r == 15:
                    break;
                case 0:
                    eobRun = (ushort)((1 << r) - 1);
                    if (r > 0)
                        eobRun += br.GetBits(r);
                    zrl = 64;
                    break;
                case 1:
                    value = br.GetBits(1) == 1 ? bit : unchecked((short)-bit);
                    break;
                default:
                    throw RawException.Corrupt("preview JPEG: unexpected Huffman code");
            }
            index = RefineNonZeroes(ref br, coeff, index, specEnd, (byte)zrl, bit);
            if (value != 0)
                coeff[UnZigZag[index]] = value;
            index++;
        }
    }

    private static int RefineNonZeroes(ref BitReader br, short[] coeff, int start, int end, byte zrl, short bit)
    {
        int last = end - 1;
        int run = zrl;
        for (int i = start; i < end; i++)
        {
            int idx = UnZigZag[i];
            if (coeff[idx] == 0)
            {
                if (run == 0)
                    return i;
                run--;
            }
            else if (br.GetBits(1) == 1 && (coeff[idx] & bit) == 0)
            {
                coeff[idx] = coeff[idx] > 0
                    ? checked((short)(coeff[idx] + bit))
                    : checked((short)(coeff[idx] - bit));
            }
        }
        return last;
    }

    private static void FillMjpegTables(ScanInfo scan, ScanContext ctx)
    {
        // 上游 fill_default_mjpeg_tables（K.3–K.6；只補 scan 用到的缺表）。
        if (ctx.DcHuff[0] is null && Array.IndexOf(scan.DcTbl, 0) >= 0)
            ctx.DcHuff[0] = HuffTable.Create(
                [0, 0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
                [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B], true);
        if (ctx.DcHuff[1] is null && Array.IndexOf(scan.DcTbl, 1) >= 0)
            ctx.DcHuff[1] = HuffTable.Create(
                [0, 0x00, 0x03, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00],
                [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B], true);
        if (ctx.AcHuff[0] is null && Array.IndexOf(scan.AcTbl, 0) >= 0)
            ctx.AcHuff[0] = HuffTable.Create(
                [0, 0x00, 0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01],
                K5, false);
        if (ctx.AcHuff[1] is null && Array.IndexOf(scan.AcTbl, 1) >= 0)
            ctx.AcHuff[1] = HuffTable.Create(
                [0, 0x00, 0x02, 0x01, 0x02, 0x04, 0x04, 0x03, 0x04, 0x07, 0x05, 0x04, 0x04, 0x00, 0x01, 0x02],
                K6, false);
    }

    // 上游 huffman.rs K.5／K.6（values；bits 見上）。
    private static readonly byte[] K5 =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    private static readonly byte[] K6 =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    /// <summary>
    /// 無損掃描（上游 `decode_scan_lossless`）：逐像素 Huffman 差值（SSSS=16 表 +32768），
    /// 再按 predictor 重建（Ra 快徑與通用 `predict` 一致，含 restart 行為）。
    /// 回傳掃描後 marker 與每分量平面（`results` 順序為掃描分量順序）。
    /// </summary>
    internal static (int Marker, ushort[][] Planes) DecodeScanLossless(
        ref BitReader br, ScanContext ctx, ScanInfo scan, int width, int height, int precision)
    {
        int ncomp = scan.CompIdx.Length;
        foreach (int t in scan.DcTbl)
        {
            if (ctx.DcHuff[t] is null)
                throw RawException.Corrupt("preview JPEG: scan makes use of unset dc Huffman table");
        }
        int npixel = width * height;
        if (ncomp == 0 || ncomp > 4 || npixel == 0 || (long)npixel * ncomp > RawLimits.MaxSamples)
            throw RawException.Corrupt("preview JPEG: bad lossless scan dimensions");
        var diffs = new int[ncomp][];
        for (int i = 0; i < ncomp; i++)
        {
            if (npixel > RawLimits.MaxSamples)
                throw RawException.Limit("preview JPEG: image too large");
            diffs[i] = new int[npixel];
        }
        int mcusLeft = ctx.RestartInterval;
        int expectedRst = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (ctx.RestartInterval > 0)
                {
                    if (mcusLeft == 0)
                    {
                        int m = br.TakeMarker();
                        if (m < 0xD0 || m > 0xD7)
                        {
                            throw RawException.Corrupt(m < 0
                                ? $"preview JPEG: no marker found where RST{expectedRst} was expected"
                                : $"preview JPEG: found marker 0xFF{m:X2} inside scan where RST{expectedRst} was expected");
                        }
                        if ((m & 7) != expectedRst)
                            throw RawException.Corrupt($"preview JPEG: found RST{m & 7} where RST{expectedRst} was expected");
                        br.Reset();
                        expectedRst = (expectedRst + 1) % 8;
                        mcusLeft = ctx.RestartInterval;
                    }
                    mcusLeft--;
                }
                for (int i = 0; i < ncomp; i++)
                {
                    int value = br.Decode(ctx.DcHuff[scan.DcTbl[i]]!);
                    int diff = value switch
                    {
                        0 => 0,
                        >= 1 and <= 15 => br.ReceiveExtend((byte)value),
                        16 => 32768,
                        _ => throw RawException.Corrupt("preview JPEG: invalid DC difference magnitude category"),
                    };
                    diffs[i][y * width + x] = diff;
                }
            }
        }
        var results = new ushort[ncomp][];
        for (int i = 0; i < ncomp; i++)
        {
            if (npixel > RawLimits.MaxSamples)
                throw RawException.Limit("preview JPEG: image too large");
            results[i] = new ushort[npixel];
        }
        if (scan.Predictor == 1)
        {
            for (int i = 0; i < ncomp; i++)
            {
                int prediction = 1 << (precision - scan.PointTransform - 1);
                int r0 = unchecked(prediction + diffs[i][0]) & 0xFFFF;
                ushort v0 = (ushort)(r0 << scan.PointTransform);
                results[i][0] = v0;
                ushort prev = v0;
                for (int yy = 1; yy < height; yy++)
                {
                    int r = unchecked(prev + diffs[i][yy * width]) & 0xFFFF;
                    ushort v = (ushort)(r << scan.PointTransform);
                    results[i][yy * width] = v;
                    prev = v;
                }
                for (int yy = 0; yy < height; yy++)
                {
                    for (int xx = 1; xx < width; xx++)
                    {
                        int r = unchecked(results[i][yy * width + xx - 1] + diffs[i][yy * width + xx]) & 0xFFFF;
                        results[i][yy * width + xx] = (ushort)(r << scan.PointTransform);
                    }
                }
            }
        }
        else
        {
            var ra = new ushort[4];
            var rb = new ushort[4];
            var rc = new ushort[4];
            for (int yy = 0; yy < height; yy++)
            {
                for (int xx = 0; xx < width; xx++)
                {
                    for (int i = 0; i < ncomp; i++)
                    {
                        int diff = diffs[i][yy * width + xx];
                        if (xx > 0)
                            ra[i] = results[i][yy * width + xx - 1];
                        if (yy > 0)
                        {
                            rb[i] = results[i][(yy - 1) * width + xx];
                            if (xx > 0)
                                rc[i] = results[i][(yy - 1) * width + xx - 1];
                        }
                        bool restart = ctx.RestartInterval > 0 && mcusLeft == ctx.RestartInterval - 1;
                        int pred = Predict(ra[i], rb[i], rc[i], scan.Predictor, scan.PointTransform, precision, xx, yy, restart);
                        int r = unchecked(pred + diff) & 0xFFFF;
                        results[i][yy * width + xx] = (ushort)(r << scan.PointTransform);
                    }
                }
            }
        }
        int marker = br.TakeMarker();
        // 上游：掃描尾多餘 RST 略過（此處 TakeMarker 已取暫存；RST 視為終止，不再前進）。
        return (marker, results);
    }

    private static int Predict(int ra, int rb, int rc, int predictor, int pointTransform, int precision, int ix, int iy, bool restart)
    {
        if ((ix == 0 && iy == 0) || restart)
        {
            if (precision > 1 + pointTransform)
                return 1 << (precision - pointTransform - 1);
            return 0;
        }
        if (iy == 0)
            return ra;
        if (ix == 0)
            return rb;
        return predictor switch
        {
            0 => 0,
            1 => ra,
            2 => rb,
            3 => rc,
            4 => unchecked(ra + rb - rc),
            5 => unchecked(ra + ((rb - rc) >> 1)),
            6 => unchecked(rb + ((ra - rc) >> 1)),
            7 => unchecked((ra + rb) / 2),
            _ => throw RawException.Corrupt("preview JPEG: bad predictor"),
        };
    }
}
