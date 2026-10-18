// 移植自 jpeg-decoder 0.3.2（MIT OR Apache-2.0；Copyright (c) jpeg-decoder contributors）
// 的解碼器本體：`decoder.rs`（標記迴圈、掃描分派、DCT/無損掃描、色彩判定、組圖）、
// `parser.rs`（SOF/SOS/DQT/DHT/DRI/APP）、`huffman.rs`（Huffman 解碼＋MJPEG 預設表）、
// `idct.rs`（8x8 純量 stb 路徑＋4x4/2x2/1x1 縮放 IDCT、choose_idct_size）、
// `upsampler.rs`（H1V1/H2V1/H1V2/H2V2/Generic 上採樣）、`worker/immediate.rs`（列 worker）、
// `decoder/lossless.rs`（SOF3 無損掃描＋組圖）。只取純量路徑（上游 `platform_independent`
// feature 語意；x86 SSSE3/NEON 向量路徑與純量路徑位元一致，見 JpegPreview.cs 說明）。
// 不含光柵平行（`rayon`/`multithreaded` worker）：單執行緒依序執行，位元與立即 worker 一致。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Buffers.Binary;

namespace FilmBatch.Raw.Codecs;

/// <summary>
/// jpeg-decoder 0.3.2 埠（上游 `Decoder&lt;R&gt;`，讀取器固定為記憶體位元組游標）：
/// 由 <see cref="JpegPreview"/> 在分派條件成立時呼叫。呼叫端已做標記前走訪與尺寸上限檢查；
/// 此處重做完整標記迴圈（含 SOS 後熵編碼），語意與上游 `decode_internal` 一致。
/// </summary>
internal static class JpegDecoderPort
{
    // 入口見 JpegPreview.Decode → JpegFrameDecoder；此類別只收共用列型與位元讀取器。

    internal enum CodingProcess : byte
    {
        DctSequential,
        DctProgressive,
        Lossless,
    }

    internal enum AdobeTransform : byte
    {
        Unknown = 0,
        YCbCr = 1,
        YccK = 2,
    }

    /// <summary>解碼器內色彩轉換（上游 `ColorTransform` 中實際會遇到的子集）。</summary>
    internal enum ColorTransform : byte
    {
        Grayscale,
        Rgb,
        YCbCr,
        Cmyk,
        YccK,
    }

    internal sealed class Component
    {
        public byte Id;
        public byte H;
        public byte V;
        public int Tq;
        public int DctScale = 8;
        public int Width;
        public int Height;
        public int BlockW;
        public int BlockH;
    }

    internal sealed class ScanInfo
    {
        public int[] CompIdx = [];
        public int[] DcTbl = [];
        public int[] AcTbl = [];
        public int SpecStart;
        public int SpecEnd; // 不含尾（上游 Range 的 end = Se + 1）
        public int SuccHigh;
        public int SuccLow;
        public int Predictor; // lossless 用（上游 Predictor，存 Ss 值 0..7）
        public int PointTransform; // lossless 用（Al）
    }

    /// <summary>Huffman 表（上游 `HuffmanTable`：8-bit LUT＋慢速 maxcode/delta＋AC 快速表）。</summary>
    internal sealed class HuffTable
    {
        public const int LutBits = 8;
        public byte[] Values = [];
        public int[] Delta = new int[16];
        public int[] MaxCode = new int[16];
        public (byte Value, byte Size)[] Lut = new (byte, byte)[1 << LutBits];
        // AC 快速表：(解碼值, run<<4 | 總位元)；run_size==0 表未命中（上游 ac_lut 語意）。
        public (short Value, byte RunSize)[]? AcLut;

        public static HuffTable Create(byte[] bits, byte[] values, bool isDc)
        {
            var (codes, sizes) = DeriveCodes(bits);
            var t = new HuffTable();
            t.Values = values;
            int j = 0;
            for (int i = 0; i < 16; i++)
            {
                if (bits[i] != 0)
                {
                    t.Delta[i] = j - codes[j];
                    j += bits[i];
                    t.MaxCode[i] = codes[j - 1];
                }
                else
                {
                    t.Delta[i] = 0;
                    t.MaxCode[i] = -1;
                }
            }
            for (int i = 0; i < t.Lut.Length; i++)
                t.Lut[i] = (0, 0);
            for (int i = 0; i < sizes.Count; i++)
            {
                int size = sizes[i];
                if (size > LutBits)
                    continue;
                int start = codes[i] << (LutBits - size);
                var val = (values[i], (byte)size);
                int count = 1 << (LutBits - size);
                for (int k = 0; k < count; k++)
                    t.Lut[start + k] = val;
            }
            if (!isDc)
            {
                var ac = new (short, byte)[1 << LutBits];
                for (int i = 0; i < ac.Length; i++)
                {
                    var (value, size) = t.Lut[i];
                    byte run = (byte)(value >> 4);
                    byte mag = (byte)(value & 0x0F);
                    if (mag > 0 && size + mag <= LutBits)
                    {
                        int unext = (int)(((uint)(i << size) & ((1 << LutBits) - 1)) >> (LutBits - mag));
                        short av = Extend((ushort)unext, mag);
                        ac[i] = (av, (byte)((run << 4) | (size + mag)));
                    }
                }
                t.AcLut = ac;
            }
            return t;
        }

        private static (List<ushort> Codes, List<byte> Sizes) DeriveCodes(byte[] bits)
        {
            var sizes = new List<byte>();
            for (int i = 0; i < 16; i++)
                for (int k = 0; k < bits[i]; k++)
                    sizes.Add((byte)(i + 1));
            var codes = new List<ushort>(new ushort[sizes.Count]);
            if (sizes.Count == 0)
                return (codes, sizes);
            int codeSize = sizes[0];
            uint code = 0;
            for (int i = 0; i < sizes.Count; i++)
            {
                int size = sizes[i];
                while (codeSize < size)
                {
                    code <<= 1;
                    codeSize++;
                }
                if (code >= (1u << size))
                    throw RawException.Corrupt("preview JPEG: bad Huffman code length");
                codes[i] = (ushort)code;
                code++;
            }
            return (codes, sizes);
        }
    }

    /// <summary>位元讀取器（上游 `HuffmanDecoder`：64-bit 緩衝＋marker 暫存）。</summary>
    internal ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _src;
        private int _pos;
        private ulong _bits;
        private int _numBits;
        private int _marker = -1; // -1 無，其餘為 marker byte

        public BitReader(ReadOnlySpan<byte> src, int pos)
        {
            _src = src;
            _pos = pos;
        }

        public int Position => _pos;
        public bool HasMarker => _marker >= 0;

        public int Decode(HuffTable t)
        {
            if (_numBits < 16)
                Fill();
            var (value, size) = t.Lut[Peek(HuffTable.LutBits)];
            if (size > 0)
            {
                Consume(size);
                return value;
            }
            int bits16 = Peek(16);
            for (int i = HuffTable.LutBits; i < 16; i++)
            {
                int code = bits16 >> (15 - i);
                if (code <= t.MaxCode[i])
                {
                    Consume(i + 1);
                    int index = code + t.Delta[i];
                    return t.Values[index];
                }
            }
            throw RawException.Corrupt("preview JPEG: failed to decode Huffman code");
        }

        public bool TryFastAc(HuffTable t, out short value, out byte run)
        {
            value = 0;
            run = 0;
            var ac = t.AcLut;
            if (ac is null)
                return false;
            if (_numBits < HuffTable.LutBits)
                Fill();
            var (v, rs) = ac[Peek(HuffTable.LutBits)];
            if (rs != 0)
            {
                run = (byte)(rs >> 4);
                byte size = (byte)(rs & 0x0F);
                Consume(size);
                value = v;
                return true;
            }
            return false;
        }

        public ushort GetBits(int count)
        {
            if (_numBits < count)
                Fill();
            int v = Peek(count);
            Consume(count);
            return (ushort)v;
        }

        public short ReceiveExtend(byte count)
        {
            ushort v = GetBits(count);
            return Extend(v, count);
        }

        public void Reset()
        {
            _bits = 0;
            _numBits = 0;
        }

        /// <summary>讀到 marker 為止（上游 `take_marker`：先 Fill 再取暫存 marker）。</summary>
        public int TakeMarker()
        {
            Fill();
            int m = _marker;
            _marker = -1;
            return m;
        }

        private int Peek(int count)
        {
            // 調用端保證 _numBits >= count（與上游 debug_assert 對應；不足即 Corrupt 而非斷言）。
            if (_numBits < count)
                throw RawException.Corrupt("preview JPEG: truncated entropy data");
            ulong mask = count == 16 ? 0xFFFFUL : (1UL << count) - 1;
            return (int)((_bits >> (64 - count)) & mask);
        }

        private void Consume(int count)
        {
            if (_numBits < count)
                throw RawException.Corrupt("preview JPEG: truncated entropy data");
            _bits <<= count;
            _numBits -= count;
        }

        private void Fill()
        {
            while (_numBits <= 56)
            {
                byte b;
                if (_marker >= 0)
                {
                    b = 0;
                }
                else
                {
                    if ((uint)_pos >= (uint)_src.Length)
                        throw RawException.Corrupt("preview JPEG: truncated entropy data");
                    b = _src[_pos++];
                }
                if (b == 0xFF)
                {
                    if ((uint)_pos >= (uint)_src.Length)
                        throw RawException.Corrupt("preview JPEG: truncated entropy data");
                    byte nb = _src[_pos++];
                    if (nb != 0x00)
                    {
                        while (nb == 0xFF)
                        {
                            if ((uint)_pos >= (uint)_src.Length)
                                throw RawException.Corrupt("preview JPEG: truncated entropy data");
                            nb = _src[_pos++];
                        }
                        _marker = nb; // 上游 from_u8 unwrap：0x00/0xFF 不可能（已排除）
                        continue;
                    }
                }
                _bits |= (ulong)b << (56 - _numBits);
                _numBits += 8;
            }
        }
    }

    internal static short Extend(ushort value, byte count)
    {
        // 上游 huffman::extend（F.12）：count==0 不會被呼叫（呼叫端保證）。
        int vt = 1 << (count - 1);
        if (value < vt)
            return (short)(value + (-1 << count) + 1);
        return (short)value;
    }
}
