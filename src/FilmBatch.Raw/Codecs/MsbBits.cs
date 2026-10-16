// 移植自 LightCraft crates/raw/src/vendor/pef.rs（Bits、Huffman、diff）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Codecs;

/// <summary>
/// MSB 優先的位元讀取器，讀過來源結尾後傳回 0（lightcraft <c>pef::Bits</c>）。
/// 由 LLVC／ARW6、Nikon、Panasonic、Pentax 解碼器共用。結構體：以 <c>ref</c> 傳遞，不可複製後各自使用。
/// </summary>
internal ref struct MsbBits
{
    private readonly ReadOnlySpan<byte> _src;
    private int _pos;
    private ulong _acc;
    private int _n;

    public MsbBits(ReadOnlySpan<byte> src)
    {
        _src = src;
        _pos = 0;
        _acc = 0;
        _n = 0;
    }

    private void Fill()
    {
        while (_n <= 56)
        {
            var b = (uint)_pos < (uint)_src.Length ? _src[_pos] : (byte)0;
            _pos++;
            _acc |= (ulong)b << (56 - _n);
            _n += 8;
        }
    }

    /// <summary>不消耗地看接下來的 <paramref name="k"/> 位元（1..32）。</summary>
    public uint Peek(int k)
    {
        if (_n < k) Fill();
        return (uint)(_acc >> (64 - k));
    }

    /// <summary>消耗 <paramref name="k"/> 位元（必須先 <see cref="Peek"/> 過至少這麼多）。</summary>
    public void Skip(int k)
    {
        _acc <<= k;
        _n -= k;
    }

    /// <summary>讀取 <paramref name="k"/> 位元（0..32）。</summary>
    public uint Get(int k)
    {
        if (k == 0) return 0;
        var v = Peek(k);
        Skip(k);
        return v;
    }

    /// <summary>已交出的位元數（可能超過來源長度：結尾之後讀到的是補上的 0）。</summary>
    public readonly long ConsumedBits => (long)_pos * 8 - _n;

    /// <summary>交出的位元是否多於來源所有（結尾之後的 0 只是前瞻補位，拿來用會捏造樣本）。</summary>
    public readonly bool Overrun => ConsumedBits > (long)_src.Length * 8;
}

/// <summary>以表格建立的 Huffman 解碼器，12 位元查表（lightcraft <c>pef::Huffman</c>）。</summary>
internal sealed class PefHuffman
{
    /// <summary>以接下來的 12 位元索引：(符號, 長度)；長度 0 表示無效碼。</summary>
    private readonly (byte Symbol, byte Length)[] _lut;

    private PefHuffman((byte, byte)[] lut) => _lut = lut;

    /// <summary>由每個符號的（靠左對齊的 12 位元碼, 長度）建立。</summary>
    public static PefHuffman Create(ReadOnlySpan<(ushort Code, byte Length)> codes)
    {
        var lut = new (byte, byte)[4096];
        for (var sym = 0; sym < codes.Length; sym++)
        {
            var (code, len) = codes[sym];
            if (len == 0) continue;
            if (len > 12 || sym > 16) throw RawException.Unsupported($"PEF Huffman code of length {len}");
            var first = (code >> (12 - len)) << (12 - len);
            var count = 1 << (12 - len);
            for (var i = first; i < first + count; i++)
            {
                if (lut[i].Item2 == 0) lut[i] = ((byte)sym, len);
            }
        }
        return new PefHuffman(lut);
    }

    /// <summary>
    /// 解一個差值：Huffman 類別，再加上 ITU-T T.81 F.1.2.1 的附加位元（開頭 0 表示負值）。碼無效時為 null。
    /// （lightcraft <c>pef::diff</c>）
    /// </summary>
    public int? Diff(ref MsbBits bits)
    {
        var (sym, len) = _lut[bits.Peek(12)];
        if (len == 0) return null;
        bits.Skip(len);
        int s = sym;
        if (s == 0) return 0;
        if (s > 16) return null;
        var v = (int)bits.Get(s);
        return v < 1 << (s - 1) ? v - (1 << s) + 1 : v;
    }
}
