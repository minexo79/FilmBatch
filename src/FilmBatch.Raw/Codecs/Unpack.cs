// 移植自 LightCraft crates/raw/src/unpack.rs（位元打包、位元組序、16/24-bit 浮點、Deflate、TIFF/DNG 預測器）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using System.Buffers.Binary;
using System.IO.Compression;

namespace FilmBatch.Raw.Codecs;

/// <summary>
/// 樣本解包與 TIFF/DNG 預測器逆運算（lightcraft-raw 的 <c>unpack.rs</c>）。
/// 位元組序以 <c>littleEndian</c> 布林表示（Rust 用 <c>lightcraft_tiff::ByteOrder</c>，FilmBatch 由 Tiff 模組持有該型別）。
/// </summary>
internal static class Unpack
{
    /// <summary>由 <paramref name="src"/> 依 MSB-first（TIFF 位元序）解出 <paramref name="dst"/>.Length 個 <paramref name="bits"/>（1..16）位元樣本。來源不足時補 0。</summary>
    public static void UnpackMsb(ReadOnlySpan<byte> src, int bits, Span<ushort> dst)
    {
        if (bits is < 1 or > 16)
            throw RawException.Corrupt($"unpack: bit depth {bits}");
        var acc = 0UL;
        var nacc = 0;
        var pos = 0;
        var mask = (1UL << bits) - 1;
        for (var i = 0; i < dst.Length; i++)
        {
            while (nacc < bits)
            {
                acc = (acc << 8) | (pos < src.Length ? src[pos] : (byte)0);
                pos++;
                nacc += 8;
            }
            dst[i] = (ushort)((acc >> (nacc - bits)) & mask);
            nacc -= bits;
        }
    }

    /// <summary>LSB-first 打包樣本解出（位元取自小端序字的低位端）。</summary>
    public static void UnpackLsb(ReadOnlySpan<byte> src, int bits, Span<ushort> dst)
    {
        if (bits is < 1 or > 16)
            throw RawException.Corrupt($"unpack: bit depth {bits}");
        var acc = 0UL;
        var nacc = 0;
        var pos = 0;
        var mask = (1UL << bits) - 1;
        for (var i = 0; i < dst.Length; i++)
        {
            while (nacc < bits)
            {
                acc |= (ulong)(pos < src.Length ? src[pos] : (byte)0) << nacc;
                pos++;
                nacc += 8;
            }
            dst[i] = (ushort)(acc & mask);
            acc >>= bits;
            nacc -= bits;
        }
    }

    /// <summary>
    /// 先依 <paramref name="littleEndian"/> 把 <paramref name="src"/> 讀成 32-bit 數字，再從每個數的高位端取 MSB-first 樣本
    /// （某些相機未壓縮的 12/14-bit 佈局；big-endian 時等同逐位元組的 <see cref="UnpackMsb"/>）。不足一個字時補 0。
    /// </summary>
    public static void UnpackWords32Msb(ReadOnlySpan<byte> src, bool littleEndian, int bits, Span<ushort> dst)
    {
        // 逐字轉成 BE 位元組流後走 UnpackMsb；Buffer.BlockCopy 供 Span<byte> → ushort[] 對齊拷貝
        var words = new byte[checked(4 * ((src.Length + 3) / 4))];
        Span<byte> padded = stackalloc byte[4];
        for (var w = 0; w < words.Length; w += 4)
        {
            var c = src.Slice(Math.Min(w, src.Length), Math.Min(4, Math.Max(0, src.Length - w)));
            padded.Clear();
            c.CopyTo(padded);
            var v = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(padded) : BinaryPrimitives.ReadUInt32BigEndian(padded);
            BinaryPrimitives.WriteUInt32BigEndian(words.AsSpan(w), v);
        }
        UnpackMsb(words, bits, dst);
    }

    /// <summary>依 <paramref name="littleEndian"/> 讀 u16 序列；<paramref name="dst"/> 比來源長時尾部保持原值（Rust 的 <c>zip</c> 語意）。</summary>
    public static void ReadU16s(ReadOnlySpan<byte> src, Span<ushort> dst, bool littleEndian)
    {
        var n = Math.Min(dst.Length, src.Length / 2);
        for (var i = 0; i < n; i++)
            dst[i] = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(2 * i)) : BinaryPrimitives.ReadUInt16BigEndian(src.Slice(2 * i));
    }

    /// <summary>IEEE 754 half → f32。</summary>
    public static float F16ToF32(ushort h)
    {
        var sign = (uint)(h >> 15) << 31;
        var exp = (uint)(h >> 10) & 0x1f;
        var man = (uint)h & 0x3ff;
        uint bits;
        if (exp == 0)
        {
            if (man == 0)
            {
                bits = sign;
            }
            else
            {
                // 次正規化：正規化
                var e = 127 - 15 + 1;
                var m = man;
                while ((m & 0x400) == 0)
                {
                    m <<= 1;
                    e -= 1;
                }
                bits = sign | ((uint)e << 23) | ((m & 0x3ff) << 13);
            }
        }
        else if (exp == 31)
        {
            bits = sign | 0x7f80_0000 | (man << 13);
        }
        else
        {
            bits = sign | ((exp + 127 - 15) << 23) | (man << 13);
        }
        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>DNG 24-bit 浮點（1 符號、7 指數 bias 63、16 尾數）→ f32。</summary>
    public static float F24ToF32(uint v)
    {
        var sign = (v >> 23) & 1;
        var exp = (v >> 16) & 0x7f;
        var man = v & 0xffff;
        uint bits;
        if (exp == 0 && man == 0)
        {
            bits = sign << 31;
        }
        else if (exp == 0x7f)
        {
            bits = (sign << 31) | 0x7f80_0000 | (man << 7);
        }
        else if (exp == 0)
        {
            // 次正規化：值 = man × 2^(1-63-16)；2^-78 以位元常數表示，避免 powf 路徑差異
            var f = man * BitConverter.UInt32BitsToSingle(0x1880_0000u);
            return sign == 1 ? -f : f;
        }
        else
        {
            bits = (sign << 31) | ((exp + 127 - 63) << 23) | (man << 7);
        }
        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>解 zlib 流，產出超過 <paramref name="limit"/> 位元組時拒絕（Rust <c>decompress_to_vec_zlib_with_limit</c>）。</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> src, int limit)
    {
        try
        {
            using var zs = new ZLibStream(new MemoryStream(src.ToArray(), writable: false), CompressionMode.Decompress);
            var ms = new MemoryStream(Math.Min(limit, 1 << 20));
            Span<byte> buf = stackalloc byte[8192];
            while (true)
            {
                var n = zs.Read(buf);
                if (n == 0)
                    break;
                if (ms.Length + n > limit)
                    throw RawException.Corrupt("deflate: output larger than the limit");
                ms.Write(buf[..n]);
            }
            return ms.ToArray();
        }
        catch (InvalidDataException e)
        {
            throw RawException.Corrupt($"deflate: {e.Message}");
        }
    }

    /// <summary>撤消一列整數樣本的水平差分，<paramref name="stride"/> 為樣本步距（cpp × factor）。</summary>
    public static void UndoDiffU16(Span<ushort> row, int stride)
    {
        if (stride < 0)
            throw RawException.Corrupt("unpack: negative stride");
        for (var i = stride; i < row.Length; i++)
            row[i] = (ushort)(row[i] + row[i - stride]);
    }

    /// <summary>撤消一列位元組樣本的水平差分。</summary>
    public static void UndoDiffU8(Span<byte> row, int stride)
    {
        if (stride < 0)
            throw RawException.Corrupt("unpack: negative stride");
        for (var i = stride; i < row.Length; i++)
            row[i] = (byte)(row[i] + row[i - stride]);
    }

    /// <summary>
    /// 撤消浮點預測器：<paramref name="row"/> 持有 <paramref name="n"/> 個各 <paramref name="bytesPer"/> 位元組的樣本，
    /// 先以 <paramref name="stride"/> 個樣本為步距做位元組差分，再以位元組平面儲存（高位元組在前）。結果以 big-endian 位元組群就寫回原處。
    /// </summary>
    public static void UndoFloatPredictor(Span<byte> row, int n, int bytesPer, int stride)
    {
        if (n < 0 || bytesPer < 0)
            throw RawException.Corrupt("unpack: negative float predictor size");
        UndoDiffU8(row, stride);
        var tmp = row.ToArray();
        for (var i = 0; i < n; i++)
        {
            for (var b = 0; b < bytesPer; b++)
            {
                var di = i * bytesPer + b;
                var si = b * n + i;
                if (di < row.Length && si < tmp.Length)
                    row[di] = tmp[si];
            }
        }
    }
}
