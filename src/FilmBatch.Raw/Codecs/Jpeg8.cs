// 移植自 LightCraft crates/raw/src/tiffraw.rs（lossy_jpeg，呼叫 zune-jpeg 解 baseline lossy JPEG）
// 與 zune-jpeg 0.5.15（MIT OR Apache-2.0 OR Zlib；Copyright (c) 2023 zune-jpeg contributors）的解碼器。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Codecs;

/// <summary>
/// Baseline／progressive DCT JPEG（8 位元）解碼器，給 lossy DNG 磁磚與內嵌預覽的白平衡估計用。
/// 對應 <c>tiffraw::lossy_jpeg</c> 底層的 zune-jpeg：純量 IDCT／雙線性上採樣／BT.601 全幅轉換，
/// 與上游在各平台的純量／向量路徑位元一致（上游自帶 scalar==vector 測試）。
/// 檔頭尺寸先做上限檢查才配置；損毀或不支援的串流只丟 <see cref="RawException"/>。
/// </summary>
internal static partial class Jpeg8
{
    /// <summary>
    /// 解 JPEG 串流。requestedChannels 為 1 時回傳灰階（Luma），否則回傳 RGB（3 通道），
    /// 與 <c>lossy_jpeg(src, cpp)</c> 的 <c>if cpp == 1 { Luma } else { RGB }</c> 一致。
    /// </summary>
    internal static (byte[] Samples, int Width, int Height, int Channels) Decode(ReadOnlySpan<byte> source, int requestedChannels, int maxSamples = RawLimits.MaxSamples)
    {
        try
        {
            if (requestedChannels < 1)
                throw RawException.Corrupt("lossy JPEG tile: invalid channel count");
            int outCh = requestedChannels == 1 ? 1 : 3;
            var d = new Decoder(source, outCh);
            return d.Run(maxSamples);
        }
        catch (RawException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException
            or ArgumentOutOfRangeException
            or ArgumentException
            or OverflowException
            or NullReferenceException
            or InvalidOperationException)
        {
            throw RawException.Corrupt($"lossy JPEG tile: {ex.GetType().Name}");
        }
    }

    /// <summary>輸入色彩空間（zune <c>ColorSpace</c> 中解碼器會遇到的子集）。</summary>
    internal enum InputCs
    {
        YCbCr,
        Luma,
        Rgb,
        Cmyk,
        Ycck,
    }

    /// <summary>取樣比（zune <c>SampleRatios</c>）。</summary>
    internal enum SampleRatio
    {
        None,
        H,
        V,
        Hv,
        Generic,
    }

    /// <summary>SOF 讀到的元件（zune <c>Components</c>，只需要解碼用的欄位）。</summary>
    internal sealed class JpegComponent
    {
        public int FileId;
        public int Hs = 1;
        public int Vs = 1;
        public int DcTable;
        public int AcTable;
        public int QtNumber;
        public int[] Qt = new int[64];
        public int DcPred;
        public int WidthStride;
        public SampleRatio Ratio;
        public int GenericH = 1;
        public int GenericV = 1;
        public int FixBug = 1;
        public bool Needed;
        public short[] RawCoeff = [];
        public short[] UpsampleDest = [];
        public short[] RowUp = [];
        public short[] Row = [];
        public short[] FirstRow = [];
        public int IdctPos;
    }

    /// <summary>解碼器本體（zune <c>JpegDecoder</c>＋<c>BitStream</c>）：游標、位元緩衝與檔頭狀態放同一 ref struct，避免 span 跨類別傳遞。</summary>
    internal ref struct Decoder
    {
        public ReadOnlySpan<byte> Src;
        public int Pos;
        public int OutCh;
        public int Width;
        public int Height;
        public InputCs Input = InputCs.YCbCr;
        public List<JpegComponent> Comps;
        public int SofComps;
        public int NumScans;
        public int[] ZOrder = null!;
        public int HMax = 1;
        public int VMax = 1;
        public int McuX;
        public int McuY;
        public bool IsInterleaved;
        public bool IsProgressive;
        public bool SeenSof;
        public bool IsMjpeg;
        public bool SeenEoi;
        public int SpecStart;
        public int SpecEnd;
        public int SuccHigh;
        public int SuccLow;
        public bool ScanSubsampled;
        public int RestartInterval;
        public int Todo = 0x7fff_ffff;
        public int Coeff = 1;
        public int[][] QtTables;
        public JpegHuffman?[] DcHuff;
        public JpegHuffman?[] AcHuff;
        // 位元串流狀態（zune BitStream）
        public ulong Buf;
        public ulong Aligned;
        public int BitsLeft;
        public int Marker = -1; // -1 無，其餘為 FF 之後的 marker byte
        public int PendingInterScan = -1;
        public short LowMask = 1;
        public int EobRun;
        public int OverreadBy;
        public readonly int[] Tmp;

        public Decoder(ReadOnlySpan<byte> src, int outCh)
        {
            Src = src;
            Pos = 0;
            OutCh = outCh;
            Width = 0;
            Height = 0;
            Comps = new List<JpegComponent>();
            ZOrder = new int[4];
            QtTables = new int[4][];
            DcHuff = new JpegHuffman?[4];
            AcHuff = new JpegHuffman?[4];
            Tmp = new int[64];
        }

        public (byte[] Samples, int Width, int Height, int Channels) Run(int maxSamples)
        {
            DecodeHeaders();
            long total = (long)Width * Height * OutCh;
            if (total > maxSamples)
                throw RawException.Limit("image too large");
            var px = new byte[(int)total];
            if (IsProgressive)
                JpegScan.DecodeProgressive(ref this, px);
            else
                JpegScan.DecodeBaseline(ref this, px);
            return (px, Width, Height, OutCh);
        }

        // ---- 游標（檔頭區讀取失敗即 Corrupt；entropy 區用 ReadRaw 容錯） ----

        public byte ReadU8() =>
            (uint)Pos < (uint)Src.Length ? Src[Pos++] : throw RawException.Corrupt("lossy JPEG tile: truncated stream");

        public ushort ReadU16()
        {
            if ((uint)Pos + 1 >= (uint)Src.Length)
                throw RawException.Corrupt("lossy JPEG tile: truncated stream");
            ushort v = (ushort)((Src[Pos] << 8) | Src[Pos + 1]);
            Pos += 2;
            return v;
        }

        public void Skip(int n)
        {
            if (n < 0 || (uint)(Pos + n) > (uint)Src.Length)
                throw RawException.Corrupt("lossy JPEG tile: truncated stream");
            Pos += n;
        }

        public bool AtEnd => (uint)Pos >= (uint)Src.Length;

        public ulong ReadRaw()
        {
            if ((uint)Pos >= (uint)Src.Length)
            {
                OverreadBy++;
                return 0;
            }
            return Src[Pos++];
        }

        public bool PeekIs(ReadOnlySpan<byte> prefix)
        {
            if ((uint)(Pos + prefix.Length) > (uint)Src.Length)
                return false;
            return Src.Slice(Pos, prefix.Length).SequenceEqual(prefix);
        }

        // ---- 位元串流（zune bitstream.rs；get_bits 等價於 peek＋drop） ----

        public int Peek(int n) => (int)(Aligned >> (64 - n));

        public void Drop(int n)
        {
            BitsLeft = Math.Max(0, BitsLeft - n);
            Aligned <<= n;
        }

        public int GetBits(int n)
        {
            int r = n == 0 ? 0 : Peek(n);
            unchecked
            {
                Aligned <<= n;
                BitsLeft -= n;
            }
            return r;
        }

        public int GetBit()
        {
            int k = (int)(Aligned >> 63) & 1;
            Drop(1);
            return k;
        }

        public void Realign() => Aligned = BitsLeft == 0 ? 0 : Buf << (64 - BitsLeft);

        public void BitsReset()
        {
            BitsLeft = 0;
            Marker = -1;
            Buf = 0;
            Aligned = 0;
            EobRun = 0;
        }

        /// <summary>补充 32 位元（zune BitStream::refill；含 FF 跳脫與 marker 偵測）。回傳值上游多半忽略。</summary>
        public bool Refill()
        {
            if (BitsLeft < 32)
            {
                if (Marker != -1 || SeenEoi)
                {
                    Buf <<= 32;
                    BitsLeft += 32;
                    Realign();
                    return true;
                }
                if (OverreadBy > 0)
                {
                    if (BitsLeft == 0)
                        throw RawException.Corrupt("lossy JPEG tile: truncated stream");
                    return true;
                }
                if ((uint)(Pos + 4) <= (uint)Src.Length)
                {
                    uint w = ((uint)Src[Pos] << 24) | ((uint)Src[Pos + 1] << 16) | ((uint)Src[Pos + 2] << 8) | Src[Pos + 3];
                    if (!HasByte(w, 0xFF))
                    {
                        BitsLeft += 32;
                        Buf = (Buf << 32) | w;
                        Realign();
                        Pos += 4;
                        return true;
                    }
                }
                for (int i = 0; i < 4; i++)
                {
                    RefillOne();
                    if (Marker != -1)
                        break;
                }
                Realign();
            }
            return true;
        }

        private void RefillOne()
        {
            ulong b = ReadRaw();
            Buf = (Buf << 8) | b;
            BitsLeft += 8;
            if (b == 0xFF)
            {
                ulong nx = ReadRaw();
                while (nx == 0xFF)
                    nx = ReadRaw();
                if (nx != 0)
                {
                    Buf >>= 8;
                    BitsLeft -= 8;
                    if (BitsLeft != 0)
                        Realign();
                    int m = (int)nx;
                    if (!IsKnownEntropyMarker(m))
                        throw RawException.Corrupt("lossy JPEG tile: unknown marker in bit stream");
                    Marker = m;
                    if (m == 0xD9)
                    {
                        Buf <<= 8;
                        BitsLeft += 8;
                        Realign();
                    }
                }
            }
        }

        private static bool IsKnownEntropyMarker(int m) =>
            m is 0xC0 or 0xC1 or 0xC2 or 0xC4 or 0xCC or >= 0xD0 and <= 0xD9
                or 0xDA or 0xDB or 0xDC or 0xDD or >= 0xE0 and <= 0xEF or 0xFE or 0x01;

        private static bool HasZero(uint v) =>
            (((((v & 0x7F7F_7F7Fu) + 0x7F7F_7F7Fu) | v) | 0x7F7F_7F7Fu) != 0xFFFF_FFFFu);

        private static bool HasByte(uint b, byte val) =>
            HasZero(b ^ (0x0101_0101u * val));

        public static int HuffExtend(int x, int s)
        {
            unchecked
            {
                return x + ((((x - (1 << (s - 1))) >> 31) & (((-1) << s) + 1)));
            }
        }

        public int DecodeHuff(JpegHuffman t)
        {
            int symbol = Peek(9);
            int entry = t.Lookup[symbol];
            int codeLen = entry >> 9;
            symbol = entry & 0x1FF;
            if (codeLen > 9)
            {
                symbol = Peek(16);
                while (codeLen < 17)
                {
                    if (symbol < t.Maxcode[codeLen])
                        break;
                    codeLen++;
                }
                if (codeLen == 17)
                    throw RawException.Corrupt($"lossy JPEG tile: bad Huffman code 0x{symbol:X}");
                symbol >>= 16 - codeLen;
                symbol = t.Values[(symbol + t.Offset[codeLen]) & 0xFF];
            }
            if (codeLen > BitsLeft)
                throw RawException.Corrupt($"lossy JPEG tile: code length {codeLen} more than bits left {BitsLeft}");
            Drop(codeLen);
            return symbol;
        }

        public void DecodeDc(JpegHuffman t, ref int pred)
        {
            Refill();
            int s = DecodeHuff(t);
            if (s != 0)
                s = HuffExtend(GetBits(s), s);
            unchecked { pred += s; }
        }

        public void DiscardDc(JpegHuffman t)
        {
            Refill();
            int s = DecodeHuff(t);
            if (s != 0)
                GetBits(s);
        }

        /// <summary>解一個 baseline MCU 區塊（zune decode_mcu_block；含快速 AC 路徑），回傳寫入的係數數。</summary>
        public int DecodeMcuBlock(JpegHuffman dc, JpegHuffman ac, int[] qt, int[] block, ref int pred)
        {
            int[] fast = ac.AcLookup!;
            int pos = 1;
            if (BitsLeft < 1 && Marker != -1)
                throw RawException.Corrupt("lossy JPEG tile: no more bytes left in stream before marker");
            DecodeDc(dc, ref pred);
            unchecked { block[0] = pred * qt[0]; }
            while (pos < 64)
            {
                Refill();
                int sym = Peek(9);
                int fac = fast[sym];
                if (fac != 0)
                {
                    pos += (fac >> 4) & 15;
                    int tp = JpegDsp.UnZigZag[Math.Min(pos, 63)] & 63;
                    unchecked { block[tp] = (fac >> 8) * qt[tp]; }
                    Drop(fac & 15);
                    pos++;
                }
                else
                {
                    int symbol = DecodeHuff(ac);
                    int r = symbol >> 4;
                    symbol &= 15;
                    if (symbol != 0)
                    {
                        pos += r;
                        symbol = HuffExtend(GetBits(symbol), symbol);
                        int tp = JpegDsp.UnZigZag[pos & 63] & 63;
                        unchecked { block[tp] = symbol * qt[tp]; }
                        pos++;
                    }
                    else if (r != 15)
                    {
                        return pos;
                    }
                    else
                    {
                        pos += 16;
                    }
                }
            }
            return 64;
        }

        public int DiscardMcuBlock(JpegHuffman dc, JpegHuffman ac)
        {
            int[] fast = ac.AcLookup!;
            int pos = 1;
            DiscardDc(dc);
            while (pos < 64)
            {
                Refill();
                int sym = Peek(9);
                int fac = fast[sym];
                if (fac != 0)
                {
                    pos += (fac >> 4) & 15;
                    Drop(fac & 15);
                    pos++;
                }
                else
                {
                    int symbol = DecodeHuff(ac);
                    int r = symbol >> 4;
                    symbol &= 15;
                    if (symbol != 0)
                    {
                        pos += r;
                        GetBits(symbol);
                        pos++;
                    }
                    else if (r != 15)
                    {
                        return pos;
                    }
                    else
                    {
                        pos += 16;
                    }
                }
            }
            return 64;
        }

        // ---- progressive entropy（zune bitstream.rs 後半） ----

        public void DecodeProgDcFirst(JpegHuffman t, ref short block, ref int pred)
        {
            DecodeDc(t, ref pred);
            unchecked { block = (short)(pred * LowMask); }
        }

        public void DecodeProgDcRefine(ref short block)
        {
            if (BitsLeft < 1)
            {
                Refill();
                if (BitsLeft < 1)
                    throw RawException.Corrupt("lossy JPEG tile: marker found where not expected in refine bit");
            }
            if (GetBit() == 1)
                unchecked { block += LowMask; }
        }

        public void DecodeMcuAcFirst(JpegHuffman ac, short[] block)
        {
            int[] fast = ac.AcLookup!;
            short bit = LowMask;
            int k = SpecStart;
            while (true)
            {
                Refill();
                int sym = Peek(9);
                int fac = fast[sym];
                if (fac != 0)
                {
                    k += (fac >> 4) & 15;
                    unchecked { block[JpegDsp.UnZigZag[Math.Min(k, 63)] & 63] = (short)((fac >> 8) * bit); }
                    Drop(fac & 15);
                    k++;
                }
                else
                {
                    int symbol = DecodeHuff(ac);
                    int r = symbol >> 4;
                    symbol &= 15;
                    if (symbol != 0)
                    {
                        k += r;
                        symbol = HuffExtend(GetBits(symbol), symbol);
                        unchecked { block[JpegDsp.UnZigZag[k & 63] & 63] = (short)(symbol * bit); }
                        k++;
                    }
                    else
                    {
                        if (r != 15)
                        {
                            EobRun = (1 << r) + GetBits(r) - 1;
                            break;
                        }
                        k += 16;
                    }
                }
                if (k > SpecEnd)
                    break;
            }
        }

        public void DecodeMcuAcRefine(JpegHuffman t, short[] block)
        {
            short bit = LowMask;
            int k = SpecStart;
            int symbol = 0, r = 0;
            if (EobRun == 0)
            {
                while (true)
                {
                    Refill();
                    symbol = Peek(9);
                    symbol = t.Lookup[symbol];
                    symbol = DecodeHuffSlow(t, symbol);
                    r = symbol >> 4;
                    symbol &= 15;
                    if (symbol == 0)
                    {
                        if (r != 15)
                        {
                            EobRun = (1 << r) + GetBits(r);
                            break;
                        }
                    }
                    else
                    {
                        if (GetBit() == 1)
                            symbol = bit;
                        else
                            symbol = -bit;
                    }
                    if (k <= SpecEnd)
                    {
                        while (true)
                        {
                            short c = block[JpegDsp.UnZigZag[k & 63] & 63];
                            if (c != 0)
                            {
                                if (BitsLeft < 1)
                                {
                                    Refill();
                                    if (BitsLeft < 1 && Marker != -1)
                                        throw RawException.Corrupt("lossy JPEG tile: marker found where not expected in refine bit");
                                }
                                if (GetBit() == 1 && (c & bit) == 0)
                                {
                                    unchecked
                                    {
                                        block[JpegDsp.UnZigZag[k & 63] & 63] = c > 0 ? (short)(c + bit) : (short)(c - bit);
                                    }
                                }
                            }
                            else
                            {
                                r -= 1;
                                if (r < 0)
                                    break;
                            }
                            if (k == SpecEnd)
                                break;
                            k++;
                        }
                    }
                    if (symbol != 0)
                        block[JpegDsp.UnZigZag[k & 63] & 63] = (short)symbol;
                    k++;
                    if (k > SpecEnd)
                        break;
                }
            }
            if (EobRun > 0)
            {
                bool any = false;
                for (int i = 1; i < 64; i++)
                {
                    if (block[i] != 0) { any = true; break; }
                }
                if (any)
                {
                    Refill();
                    while (k <= SpecEnd)
                    {
                        int idx = JpegDsp.UnZigZag[k & 63] & 63;
                        short c = block[idx];
                        if (c != 0 && GetBit() == 1 && (c & bit) == 0)
                        {
                            unchecked
                            {
                                block[idx] = c >= 0 ? (short)(c + bit) : (short)(c - bit);
                            }
                        }
                        if (BitsLeft < 1)
                            Refill();
                        k++;
                    }
                }
                EobRun -= 1;
            }
        }

        private int DecodeHuffSlow(JpegHuffman t, int entry)
        {
            int codeLen = entry >> 9;
            int symbol = entry & 0x1FF;
            if (codeLen > 9)
            {
                symbol = Peek(16);
                while (codeLen < 17)
                {
                    if (symbol < t.Maxcode[codeLen])
                        break;
                    codeLen++;
                }
                if (codeLen == 17)
                    throw RawException.Corrupt($"lossy JPEG tile: bad Huffman code 0x{symbol:X}");
                symbol >>= 16 - codeLen;
                symbol = t.Values[(symbol + t.Offset[codeLen]) & 0xFF];
            }
            if (codeLen > BitsLeft)
                throw RawException.Corrupt($"lossy JPEG tile: code length {codeLen} more than bits left {BitsLeft}");
            Drop(codeLen);
            return symbol;
        }

        public void UpdateProgressiveParams(int al, int specStart, int specEnd)
        {
            unchecked { LowMask = (short)(1 << al); }
            SpecStart = specStart;
            SpecEnd = specEnd;
        }

        // ---- 檔頭（zune decoder.rs decode_headers_internal＋headers.rs） ----

        public void DecodeHeaders()
        {
            if (ReadU16() != 0xFFD8)
                throw RawException.Corrupt("lossy JPEG tile: bad SOI marker");
            int last = 0;
            while (true)
            {
                int m = ReadU8();
                if ((m == 0xFF || m == 0x00) && last == 0xFF)
                {
                    while (m == 0xFF || m == 0x00)
                    {
                        last = m;
                        m = ReadU8();
                    }
                }
                if (last == 0xFF)
                {
                    if (m == 0xD9)
                        throw RawException.Corrupt("lossy JPEG tile: premature end of image");
                    if (m == 0xD8 || (m >= 0xD0 && m <= 0xD7) || m == 0x01)
                    {
                        last = m; // SOI／RST／TEM：無長度，忽略（上游會誤讀長度；此處直接略過）
                        continue;
                    }
                    if (ParseMarker(m))
                    {
                        AfterSosHeaders();
                        return;
                    }
                }
                last = m;
            }
        }

        /// <summary>回傳 true 表示遇到 SOS（檔頭結束）。</summary>
        public bool ParseMarker(int m)
        {
            switch (m)
            {
                case 0xC0:
                case 0xC1:
                    ParseStartOfFrame(false);
                    return false;
                case 0xC2:
                    ParseStartOfFrame(true);
                    return false;
                case 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    throw RawException.Unsupported($"lossy JPEG tile: SOF marker 0xFF{m:X2} (lossless/arithmetic/differential)");
                case 0xC4:
                    ParseHuffman();
                    return false;
                case 0xCC:
                    throw RawException.Unsupported("lossy JPEG tile: arithmetic conditioning (DAC)");
                case 0xDA:
                    ParseSos();
                    return true;
                case 0xDB:
                    ParseDqt();
                    return false;
                case 0xDC:
                    throw RawException.Unsupported("lossy JPEG tile: DNL marker");
                case 0xDD:
                    ParseDri();
                    return false;
                case 0xE0:
                    ParseApp0();
                    return false;
                case 0xE1:
                case 0xE2:
                case 0xED:
                    SkipSegment();
                    return false;
                case 0xEE:
                    ParseApp14();
                    return false;
                case 0xFE:
                    SkipSegment();
                    return false;
                default:
                    SkipSegment(); // 未知 APP／JPG／其他：按長度略過（與上游 _ 分支一致）
                    return false;
            }
        }

        public void SkipSegment()
        {
            int len = ReadU16();
            if (len < 2)
                throw RawException.Corrupt("lossy JPEG tile: bad marker length");
            Skip(len - 2);
        }

        public void ParseApp0()
        {
            int len = ReadU16();
            if (len < 2)
                throw RawException.Corrupt("lossy JPEG tile: bad APP0 length");
            len -= 2;
            if (len > 5)
            {
                if (PeekIs("AVI1\0"u8))
                    IsMjpeg = true;
                Skip(5);
                len -= 5;
            }
            Skip(len);
        }

        public void ParseApp14()
        {
            int len = ReadU16();
            if (len < 2)
                throw RawException.Corrupt("lossy JPEG tile: bad APP14 length");
            if (PeekIs("Adobe"u8))
            {
                if (len < 14)
                    throw RawException.Corrupt("lossy JPEG tile: short APP14 segment");
                Skip(6 + 5);
                int transform = ReadU8();
                Input = transform switch
                {
                    0 => InputCs.Cmyk,
                    1 => InputCs.YCbCr,
                    2 => InputCs.Ycck,
                    _ => throw RawException.Corrupt($"lossy JPEG tile: unknown Adobe colorspace {transform}"),
                };
                Skip(len - 14);
            }
            else
            {
                Skip(len - 2);
            }
        }

        public void ParseDri()
        {
            if (ReadU16() != 4)
                throw RawException.Corrupt("lossy JPEG tile: bad DRI length");
            RestartInterval = ReadU16();
            Todo = RestartInterval;
        }

        public void ParseDqt()
        {
            int qtLen = ReadU16();
            if (qtLen < 2)
                throw RawException.Corrupt("lossy JPEG tile: bad DQT length");
            qtLen -= 2;
            while (qtLen > 0)
            {
                int info = ReadU8();
                int prec = info >> 4;
                int pos = info & 0x0F;
                int need = 64 * (prec + 1);
                if (prec > 1)
                    throw RawException.Corrupt($"lossy JPEG tile: bad QT precision {prec}");
                if (need + 1 > qtLen)
                    throw RawException.Corrupt("lossy JPEG tile: short QT table");
                var t = new int[64];
                if (prec == 0)
                {
                    for (int i = 0; i < 64; i++)
                        t[JpegDsp.UnZigZag[i]] = ReadU8();
                }
                else
                {
                    for (int i = 0; i < 64; i++)
                        t[JpegDsp.UnZigZag[i]] = ReadU16();
                }
                qtLen -= need + 1;
                if ((uint)pos >= 4)
                    throw RawException.Corrupt($"lossy JPEG tile: bad QT position {pos}");
                QtTables[pos] = t;
            }
        }

        public void ParseHuffman()
        {
            int dhtLen = ReadU16();
            if (dhtLen < 2)
                throw RawException.Corrupt("lossy JPEG tile: bad DHT length");
            dhtLen -= 2;
            while (dhtLen > 16)
            {
                int info = ReadU8();
                int cls = (info >> 4) & 0xF;
                int idx = info & 0xF;
                if (idx >= 4)
                    throw RawException.Corrupt($"lossy JPEG tile: invalid DHT index {idx}");
                if (cls > 1)
                    throw RawException.Corrupt($"lossy JPEG tile: invalid DHT class {cls}");
                var bits = new byte[17];
                for (int i = 1; i <= 16; i++)
                    bits[i] = ReadU8();
                dhtLen -= 17;
                int sum = 0;
                for (int i = 1; i <= 16; i++)
                    sum += bits[i];
                if (sum > 256)
                    throw RawException.Corrupt("lossy JPEG tile: oversized DHT");
                if (sum > dhtLen)
                    throw RawException.Corrupt("lossy JPEG tile: short DHT");
                dhtLen -= sum;
                var vals = new byte[256];
                for (int i = 0; i < sum; i++)
                    vals[i] = ReadU8();
                var t = JpegHuffman.Create(bits, vals, cls == 0);
                if (cls == 0)
                    DcHuff[idx] = t;
                else
                    AcHuff[idx] = t;
            }
            if (dhtLen != 0)
                throw RawException.Corrupt("lossy JPEG tile: bogus Huffman table definition");
        }

        public void ParseStartOfFrame(bool progressive)
        {
            if (SeenSof)
                throw RawException.Corrupt("lossy JPEG tile: two start of frame markers");
            int len = ReadU16();
            int prec = ReadU8();
            if (prec != 8)
                throw RawException.Unsupported($"lossy JPEG tile: {prec}-bit JPEG precision");
            int h = ReadU16();
            int w = ReadU16();
            if (w == 0 || h == 0)
                throw RawException.Corrupt("lossy JPEG tile: zero image dimensions");
            // zune headers.rs（max 1<<16，見 tiffraw.rs lossy_jpeg 的 DecoderOptions）：超限即拒。
            if (w > 1 << 16 || h > 1 << 16)
                throw RawException.Limit("lossy JPEG tile: image too large");
            Width = w;
            Height = h;
            int n = ReadU8();
            if (n < 1 || n > 4)
            {
                if (n == 0)
                    throw RawException.Corrupt("lossy JPEG tile: zero components");
                throw RawException.Unsupported($"lossy JPEG tile: {n} components");
            }
            if (len != 8 + 3 * n)
                throw RawException.Corrupt("lossy JPEG tile: bad SOF length");
            if (n == 1)
                Input = InputCs.Luma;
            if (n == 4 && Input == InputCs.YCbCr)
                Input = InputCs.Cmyk;
            SofComps = n;
            IsProgressive = progressive;
            Comps.Clear();
            for (int i = 0; i < n; i++)
            {
                int a0 = ReadU8();
                int a1 = ReadU8();
                int a2 = ReadU8();
                Comps.Add(JpegComponentFrom(a0, a1, a2, i));
            }
            SeenSof = true;
        }

        private static JpegComponent JpegComponentFrom(int a0, int a1, int a2, int pos)
        {
            int hs = (a1 >> 4) & 15;
            int vs = a1 & 15;
            if (hs > 4)
                throw RawException.Corrupt($"lossy JPEG tile: bogus horizontal sampling factor {hs}");
            if (vs > 4)
                throw RawException.Corrupt($"lossy JPEG tile: bogus vertical sampling factor {vs}");
            if (hs == 0 || (hs & (hs - 1)) != 0)
                throw RawException.Corrupt($"lossy JPEG tile: horizontal sample is not a power of two ({hs})");
            if (vs == 0)
                throw RawException.Corrupt("lossy JPEG tile: vertical sample is zero");
            if ((uint)a2 >= 4)
                throw RawException.Corrupt($"lossy JPEG tile: bad quantization number {a2}");
            return new JpegComponent { FileId = a0, Hs = hs, Vs = vs, QtNumber = a2 };
        }

        public void ParseSos()
        {
            int ls = ReadU16();
            int ns = ReadU8();
            NumScans = ns;
            // zune headers.rs parse_sos：每遇到 SOS 即重設（多掃描時各掃描獨立判定）。
            ScanSubsampled = false;
            if (ls != 6 + 2 * ns)
                throw RawException.Corrupt($"lossy JPEG tile: bad SOS length {ls}");
            if (ns < 1 || ns > 4)
                throw RawException.Corrupt($"lossy JPEG tile: bad scan component count {ns}");
            if (SofComps == 0)
                throw RawException.Corrupt("lossy JPEG tile: SOS before SOF");
            var seen = new int[ns];
            for (int i = 0; i < ns; i++)
                seen[i] = -1;
            for (int i = 0; i < ns; i++)
            {
                int id = ReadU8();
                for (int k = 0; k < i; k++)
                {
                    if (seen[k] == id)
                        throw RawException.Corrupt($"lossy JPEG tile: duplicate SOS id {id}");
                }
                seen[i] = id;
                int y = ReadU8();
                int ci = -1;
                for (int k = 0; k < Comps.Count; k++)
                {
                    if (Comps[k].FileId == id) { ci = k; break; }
                }
                if (ci < 0)
                    throw RawException.Corrupt($"lossy JPEG tile: unknown SOS component id {id}");
                Comps[ci].DcTable = (y >> 4) & 0xF;
                Comps[ci].AcTable = y & 0xF;
                ZOrder[i] = ci;
                if (Comps[ci].Vs != 1 || Comps[ci].Hs != 1)
                    ScanSubsampled = true;
            }
            SpecStart = ReadU8();
            SpecEnd = ReadU8();
            int approx = ReadU8();
            SuccHigh = approx >> 4;
            SuccLow = approx & 0xF;
            if (SpecEnd > 63)
                throw RawException.Corrupt($"lossy JPEG tile: bad Se {SpecEnd}");
            if (SpecStart > 63)
                throw RawException.Corrupt($"lossy JPEG tile: bad Ss {SpecStart}");
            if (SuccHigh > 13)
                throw RawException.Corrupt($"lossy JPEG tile: bad Ah {SuccHigh}");
            if (SuccLow > 13)
                throw RawException.Corrupt($"lossy JPEG tile: bad Al {SuccLow}");
        }

        public void AfterSosHeaders()
        {
            if (Comps.Count == 3
                && Comps[0].FileId == (byte)'R'
                && Comps[1].FileId == (byte)'G'
                && Comps[2].FileId == (byte)'B')
            {
                Input = InputCs.Rgb;
            }
        }

        // ---- 元件參數（zune setup_component_params） ----

        public void SetupComponentParams()
        {
            if (Comps.Count == 3 && Input == InputCs.Cmyk)
                Input = InputCs.Rgb;
            foreach (var c in Comps)
            {
                HMax = Math.Max(HMax, c.Hs);
                VMax = Math.Max(VMax, c.Vs);
                if (HMax != 1 || VMax != 1)
                    IsInterleaved = true;
                McuX = DivCeil(Width, HMax * 8);
                McuY = DivCeil(Height, VMax * 8);
                int[]? qt = (uint)c.QtNumber < 4 ? QtTables[c.QtNumber] : null;
                if (qt is null)
                    throw RawException.Corrupt("lossy JPEG tile: missing quantization table");
                checked
                {
                    c.Qt = qt;
                    c.WidthStride = c.Hs * McuX * 8;
                }
            }
            bool yTwo = false;
            foreach (var c in Comps)
            {
                if (Comps.IndexOf(c) == 0 && (c.Hs == 2 || c.Vs == 2))
                    yTwo = true;
            }
            if (yTwo)
            {
                for (int i = 1; i < Comps.Count; i++)
                {
                    var c = Comps[i];
                    if (c.Hs != 1 || c.Vs != 1)
                        c.FixBug = 2;
                }
            }
            if (IsMjpeg)
                JpegDsp.FillMjpegTables(DcHuff, AcHuff);
            int need = Input switch
            {
                InputCs.YCbCr or InputCs.Rgb => 3,
                InputCs.Luma => 1,
                InputCs.Cmyk or InputCs.Ycck => 4,
                _ => 3,
            };
            if (need > Comps.Count)
            {
                if (Input == InputCs.Ycck)
                {
                    Input = InputCs.YCbCr;
                }
                else if (Comps.Count is 1 or 3 or 4)
                {
                    Input = Comps.Count == 1 ? InputCs.Luma : InputCs.YCbCr;
                }
                else
                {
                    throw RawException.Unsupported($"lossy JPEG tile: {Comps.Count}-component {Input}");
                }
            }
            if (Comps.Count is not (1 or 3 or 4))
                throw RawException.Unsupported($"lossy JPEG tile: {Comps.Count} components");
        }

        public void CheckTables()
        {
            foreach (var c in Comps)
            {
                if ((uint)c.DcTable >= 4 || DcHuff[c.DcTable] is null)
                    throw RawException.Corrupt("lossy JPEG tile: missing DC Huffman table");
                if ((uint)c.AcTable >= 4 || AcHuff[c.AcTable] is null)
                    throw RawException.Corrupt("lossy JPEG tile: missing AC Huffman table");
            }
        }

        public void SetUpsampling()
        {
            if (HMax == 1 && VMax == 1)
                return;
            foreach (var c in Comps)
            {
                checked
                {
                    int hs = HMax / c.Hs;
                    int vs = VMax / c.Vs;
                    if (hs == 1 && vs == 1)
                    {
                        c.Ratio = SampleRatio.None;
                    }
                    else if (hs == 2 && vs == 1)
                    {
                        c.Ratio = SampleRatio.H;
                    }
                    else if (hs == 1 && vs == 2)
                    {
                        c.Ratio = SampleRatio.V;
                    }
                    else if (hs == 2 && vs == 2)
                    {
                        c.Ratio = SampleRatio.Hv;
                    }
                    else
                    {
                        c.Ratio = SampleRatio.Generic;
                        c.GenericH = hs;
                        c.GenericV = vs;
                    }
                    c.Row = new short[c.WidthStride * c.Vs];
                    c.RowUp = new short[c.WidthStride * c.Vs];
                    int sample = c.Ratio switch
                    {
                        SampleRatio.Hv => 4,
                        SampleRatio.H or SampleRatio.V => 2,
                        SampleRatio.Generic => c.GenericH * c.GenericV,
                        _ => 1,
                    };
                    long first = (long)c.Vs * c.WidthStride * sample;
                    long dest = first * c.FixBug * 8;
                    if (first > RawLimits.MaxSamples || dest > RawLimits.MaxSamples)
                        throw RawException.Limit("image too large");
                    c.FirstRow = new short[(int)first];
                    Array.Fill(c.FirstRow, (short)128);
                    c.UpsampleDest = new short[(int)dest];
                }
            }
        }

        public void ResetParams()
        {
            HMax = 1;
            VMax = 1;
            IsInterleaved = false;
            var c = Comps[0];
            c.Vs = 1;
            c.Hs = 1;
            c.WidthStride = DivCeil(Width, 8) * 8;
        }

        public static int DivCeil(int a, int b) => checked((a + b - 1) / b);

        public static int PaddedWidth(int w, SampleRatio r, int gh = 1)
        {
            return r switch
            {
                SampleRatio.H or SampleRatio.Hv => DivCeil(w, 16) * 16,
                SampleRatio.Generic => DivCeil(w, gh * 8) * (gh * 8),
                _ => DivCeil(w, 8) * 8,
            };
        }
    }
}
