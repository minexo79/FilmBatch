// 移植自 LightCraft crates/raw/src/llvc.rs（SMPTE RDD 34:2015 LLVC codec 核心，Sony ARW6 所用）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
// Clean-room：依 SMPTE RDD 34:2015 §6.3（圖 6.5–6.8）與其 worked example（圖 6.9）及 Phase 0 oracle 向量寫成。

using System.Numerics;

namespace FilmBatch.Raw.Codecs;

/// <summary>RDD 34:2015 LLVC codec 核心（Sony ARW6 使用；見 LightCraft docs/arw6-compression.md）。</summary>
internal static class Llvc
{
    /// <summary>串流可選的最大係數位元深度（DPT）；讓 <see cref="MsbBits.Get"/> 維持在範圍內。</summary>
    public const int MaxDpt = 24;

    private static RawException Corrupt(string why) => RawException.Corrupt($"ARW6 VLD: {why}");

    /// <summary>讀一個位元；讀取器越過其半部的 <paramref name="sourceBits"/> 時失敗。</summary>
    private static uint Bit(ref MsbBits bits, int sourceBits)
    {
        var b = bits.Get(1);
        if (bits.ConsumedBits > sourceBits)
            throw Corrupt("past the end of its half");
        return b;
    }

    /// <summary>數連續的 0 位元直到結尾的 1（<c>0^n 1</c> 碼），以 <see cref="MaxDpt"/> 為上限。</summary>
    private static uint ZerosThenOne(ref MsbBits bits, int sourceBits)
    {
        // 一次看接下來 MAX_DPT + 1 個位元（更多的 0 怎樣都是損毀）
        var n = BitOperations.LeadingZeroCount(bits.Peek(MaxDpt + 1)) - (32 - (MaxDpt + 1));
        bits.Skip(Math.Min(n + 1, MaxDpt + 1));
        if (bits.ConsumedBits > sourceBits)
            throw Corrupt("past the end of its half");
        if (n > MaxDpt)
            throw Corrupt("DPT code exceeds MAX_DPT");
        return (uint)n;
    }

    /// <summary>解碼一條係數線（<c>ceil(width/4)</c> 個 set，RDD 34 §6.3.6），回傳恰好 <paramref name="width"/> 個值。</summary>
    public static int[] VldDecodeLine(ref MsbBits bits, int sourceBits, int width)
    {
        if (width == 0)
            return []; // 無 set：下面的狀態機永遠不會終止
        var sets = (width + 3) / 4;
        var abs = new uint[4];
        var dpt = 0u;
        var cnt = sets;
        var dpts = 0;
        var absV = false;
        var absE = false;
        var dptV = true;
        var dptE = false;
        var zr = 0L;
        var outList = new List<int>(sets * 4);
        while (true)
        {
            // DPT 值解碼（圖 6.6）
            if (cnt == 0)
                dptV = false;
            if (cnt == 1)
                dptE = true;
            cnt = Math.Max(0, cnt - 1);
            zr = Math.Max(0, zr - 1);
            if (dptV)
            {
                switch (dpts)
                {
                    case 0:
                        if (Bit(ref bits, sourceBits) == 0)
                        {
                            // Sony 偏差 1（Phase 0 oracle 驗證）：DPT 0 的零碼是零 set，進入 run mode
                            if (dpt == 0)
                                dpts = 1;
                        }
                        else if (Bit(ref bits, sourceBits) == 0)
                        {
                            // 「1 0 0^n 1」：DPT 上升 n+1
                            dpt = Math.Min(uint.MaxValue, dpt + ZerosThenOne(ref bits, sourceBits) + 1);
                        }
                        else
                        {
                            // 「1 1 0^n 1」（DPT 下降 n+1）或「1 1 0^(DPT-1)」（DPT 歸零）
                            var n = 0u;
                            while (n + 1 < dpt && Bit(ref bits, sourceBits) == 0)
                                n += 1;
                            if (n + 1 >= dpt)
                            {
                                dpt = 0;
                                dpts = 1;
                            }
                            else
                            {
                                dpt -= n + 1;
                            }
                        }
                        break;
                    case 1:
                        if (Bit(ref bits, sourceBits) == 1)
                        {
                            // 「1 0^n 1」：離開 run mode，DPT 為 n+1
                            dpt = ZerosThenOne(ref bits, sourceBits) + 1;
                            dpts = 0;
                        }
                        else
                        {
                            // run 碼，長度為 ceil(log2(Cnt+2)) 位元（圖 6.6）
                            var cnt2 = (ulong)cnt + 2;
                            var lim = 0;
                            while (1uL << lim < cnt2)
                                lim += 1;
                            if (lim > 32)
                                throw Corrupt("line too wide");
                            var m = 1;
                            while (m < lim && Bit(ref bits, sourceBits) == 0)
                                m += 1;
                            if (m == lim)
                            {
                                // 「0^lim」：本線其餘全為零
                                zr = cnt + 2L;
                            }
                            else
                            {
                                zr = 1;
                                for (var j = 0; j < m; j++)
                                    zr = (zr << 1) | Bit(ref bits, sourceBits);
                            }
                            dpts = 2;
                        }
                        break;
                    default:
                        // Sony 偏差 2（Phase 0 oracle 驗證）：run 在 ZR == 1 結束（RDD 34 說是 0）
                        if (zr == 1)
                        {
                            dpt = ZerosThenOne(ref bits, sourceBits) + 1;
                            dpts = 0;
                        }
                        break;
                }
                if (dpt > MaxDpt)
                    throw Corrupt("DPT exceeds MAX_DPT");
            }
            // SGN 值解碼（圖 6.7）：前一個 set 的正負號
            if (absV)
            {
                foreach (var v in abs)
                    outList.Add(v != 0 && Bit(ref bits, sourceBits) == 1 ? -(int)v : (int)v);
            }
            if (absE)
            {
                if (outList.Count > width)
                    outList.RemoveRange(width, outList.Count - width);
                return [.. outList];
            }
            absV = dptV;
            absE = dptE;
            // ABS 值解碼（圖 6.8）
            if (dptV)
            {
                for (var i = 0; i < 4; i++)
                    abs[i] = bits.Get((int)dpt);
                if (bits.ConsumedBits > sourceBits)
                    throw Corrupt("past the end of its half");
            }
        }
    }

    /// <summary>
    /// 奇偶去量化器（Phase 0，oracle 驗證）：0 → 0，qi = 0 → q，否則
    /// <c>sign(q) * (((2|q| + 1) &lt;&lt; (qi - 1)) - (|q| &amp; 1))</c>。惡意輸入時飽和而非溢位。
    /// </summary>
    public static int Dequant(int q, uint qi)
    {
        if (q == 0 || qi == 0)
            return q;
        var a = Math.Abs((long)q);
        var m = Math.Min(((2 * a + 1) << (int)Math.Min(qi - 1, 30)) - (a & 1), int.MaxValue);
        return q < 0 ? -(int)m : (int)m;
    }

    /// <summary>一個 row-major 的整數樣本平面（Rust <c>llvc::Plane</c>）。</summary>
    public sealed class Plane
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int[] Data { get; init; } = [];

        public static Plane Zeros(int width, int height) =>
            new() { Width = Math.Max(0, width), Height = Math.Max(0, height), Data = new int[(long)Math.Max(0, width) * Math.Max(0, height)] };

        /// <summary>第 <paramref name="y"/> 列；超出範圍時為空。</summary>
        public ReadOnlySpan<int> Row(int y)
        {
            if (y < 0 || y >= Height)
                return [];
            var start = y * Width;
            return start + Width <= Data.Length ? Data.AsSpan(start, Width) : [];
        }

        internal bool Ok() => (long)Width * Height == Data.Length;
    }

    /// <summary>一條 <paramref name="n"/> 樣本訊號的 (low, high) 樣本數；low-pass 樣本位於 ≡ <paramref name="phase"/>（mod 2）的索引。</summary>
    public static (int Low, int High) BandRows(int n, int phase)
    {
        var low = Math.Max(0, n + 1 - Math.Max(0, phase)) / 2;
        return (low, n - low);
    }

    /// <summary>飽和窄化：惡意係數被夾住而非溢位。</summary>
    private static int Sat(long v) => v < int.MinValue ? int.MinValue : v > int.MaxValue ? int.MaxValue : (int)v;

    /// <summary>取寬為 <paramref name="w"/> 的頻帶中第 <paramref name="i"/> 個樣本（夾進範圍的整體對稱延伸）；空頻帶回傳 <paramref name="zeros"/>。</summary>
    private static ReadOnlySpan<int> BandSample(int[] v, int w, int n, int i, int[] zeros)
    {
        if (n == 0)
            return zeros;
        var c = Math.Clamp(i, 0, n - 1);
        var start = c * w;
        return start + w <= v.Length ? v.AsSpan(start, w) : zeros;
    }

    /// <summary>
    /// 逆 5/3 lifting（RDD 34 §6.5），X[-1] := X[1]，X[n] := X[n-2]；訊號的樣本寬為 <paramref name="w"/> 個值
    /// （一次對頻帶所有欄位做，逐列；每欄獨立也會得到相同結果）。單線（w = 1）走 <see cref="LiftRow"/>。
    /// </summary>
    private static int[] InverseLift(int[] l, int[] h, int w, int phase)
    {
        var ww = Math.Max(w, 1);
        var (nl, nh) = (l.Length / ww, h.Length / ww);
        var n = nl + nh;
        if (BandRows(n, phase) != (nl, nh) || phase is < 0 or > 1)
            throw RawException.Corrupt("ARW6 wavelet: band sizes do not match");
        var zeros = new int[ww];
        var even = new int[nl * ww];
        Parallel.For(0, nl, RawParallel.Options, j =>
        {
            var e = even.AsSpan(j * ww, ww);
            var lv = l.AsSpan(j * ww, ww);
            var a = BandSample(h, ww, nh, j - 1 + phase, zeros);
            var b = BandSample(h, ww, nh, j + phase, zeros);
            for (var i = 0; i < ww; i++)
                e[i] = Sat(lv[i] - ((a[i] + (long)b[i] + 2) >> 2));
        });
        var outArr = new int[n * ww];
        var lo = phase;
        Parallel.For(0, n, RawParallel.Options, t =>
        {
            var o = outArr.AsSpan(t * ww, ww);
            if (t % 2 == lo)
            {
                var src = (t - lo) / 2 * ww;
                if (src + ww <= even.Length)
                    even.AsSpan(src, ww).CopyTo(o);
                return;
            }
            var k = (t - (1 - lo)) / 2;
            var hs = k * ww;
            if (hs + ww > h.Length)
                return;
            var a = BandSample(even, ww, nl, k - phase, zeros);
            var b = BandSample(even, ww, nl, k + 1 - phase, zeros);
            var hv = h.AsSpan(hs, ww);
            for (var i = 0; i < ww; i++)
                o[i] = Sat(hv[i] + ((a[i] + (long)b[i]) >> 1));
        });
        return outArr;
    }

    /// <summary>單線（w = 1）的 <see cref="InverseLift"/>：結果寫入 <paramref name="output"/>（<c>l.Length + h.Length</c> 個值），<paramref name="even"/> 為暫存。呼叫端已檢查頻帶大小。</summary>
    private static void LiftRow(ReadOnlySpan<int> l, ReadOnlySpan<int> h, int phase, List<int> even, Span<int> output)
    {
        var (nl, nh, p) = (l.Length, h.Length, phase);
        var ha = h.ToArray();
        long Hs(int i) => nh == 0 ? 0 : ha[Math.Clamp(i, 0, nh - 1)];
        even.Clear();
        for (var j = 0; j < nl; j++)
            even.Add(Sat(l[j] - ((Hs(j - 1 + p) + Hs(j + p) + 2) >> 2)));
        long Es(int i) => nl == 0 ? 0 : even[Math.Clamp(i, 0, nl - 1)];
        var lo = phase;
        var hi = 1 - phase;
        for (int t = lo, e = 0; t < output.Length && e < even.Count; t += 2, e++)
            output[t] = even[e];
        for (int t = hi, k = 0; t < output.Length && k < nh; t += 2, k++)
            output[t] = Sat(h[k] + ((Es(k - p) + Es(k + 1 - p)) >> 1));
    }

    /// <summary>沿一軸對 <paramref name="low"/>/<paramref name="high"/> 做逆轉換：<paramref name="vertical"/> 時沿欄（逐列全部一起），否則沿各列。</summary>
    private static Plane Lines(Plane low, Plane high, int phase, bool vertical)
    {
        if (!low.Ok() || !high.Ok())
            throw RawException.Corrupt("ARW6 wavelet: plane size mismatch");
        var (acrossL, acrossH) = vertical ? (low.Width, high.Width) : (low.Height, high.Height);
        if (acrossL != acrossH)
            throw RawException.Corrupt("ARW6 wavelet: band sizes do not match");
        var (nl, nh) = vertical ? (low.Height, high.Height) : (low.Width, high.Width);
        var n = nl + nh;
        var (w, h) = vertical ? (acrossL, n) : (n, acrossL);
        if (acrossL == 0)
            return Plane.Zeros(w, h);
        if (vertical)
            return new Plane { Width = w, Height = h, Data = InverseLift(low.Data, high.Data, w, phase) };
        if (BandRows(n, phase) != (nl, nh) || phase is < 0 or > 1)
            throw RawException.Corrupt("ARW6 wavelet: band sizes do not match");
        var outP = Plane.Zeros(w, h);
        Parallel.For(0, h, RawParallel.Options, i =>
        {
            var l = low.Row(i);
            var hi2 = high.Row(i);
            if (!l.IsEmpty && !hi2.IsEmpty)
                LiftRow(l, hi2, phase, ThreadLocals.Even ??= new List<int>(), outP.Data.AsSpan(i * w, w));
        });
        return outP;
    }

    /// <summary>垂直逆 5/3（沿樣本列）：<paramref name="low"/>/<paramref name="high"/> 為等寬的上下頻帶。</summary>
    public static Plane Inverse53_1dRows(Plane low, Plane high, int phase) => Lines(low, high, phase, true);

    /// <summary>水平逆 5/3：<paramref name="low"/>/<paramref name="high"/> 為等高的左右頻帶。</summary>
    public static Plane Inverse53_1dCols(Plane low, Plane high, int phase) => Lines(low, high, phase, false);

    /// <summary>2-D 逆轉換，RDD 34 §6.5 順序：先對 (ll, lh) 與 (hl, hh) 做垂直 IWT（phase <paramref name="vphase"/>），再做水平（phase 0）。</summary>
    public static Plane Inverse53_2d(Plane ll, Plane lh, Plane hl, Plane hh, int vphase)
    {
        var lo = Inverse53_1dRows(ll, lh, vphase);
        var hi = Inverse53_1dRows(hl, hh, vphase);
        return Inverse53_1dCols(lo, hi, 0);
    }

    /// <summary>3 層分解的十個頻帶（LL 只在最深層）。</summary>
    public sealed class Bands3
    {
        public Plane Ll3 = null!;
        public Plane Hl3 = null!;
        public Plane Lh3 = null!;
        public Plane Hh3 = null!;
        public Plane Hl2 = null!;
        public Plane Lh2 = null!;
        public Plane Hh2 = null!;
        public Plane Hl1 = null!;
        public Plane Lh1 = null!;
        public Plane Hh1 = null!;
    }

    /// <summary>完整逆轉換：第 3 層用 <paramref name="phases"/>[2]，第 2 層 phases[1]，第 1 層 phases[0]。</summary>
    public static Plane Reconstruct3(Bands3 b, byte[] phases)
    {
        var ll2 = Inverse53_2d(b.Ll3, b.Lh3, b.Hl3, b.Hh3, phases[2]);
        var ll1 = Inverse53_2d(ll2, b.Lh2, b.Hl2, b.Hh2, phases[1]);
        return Inverse53_2d(ll1, b.Lh1, b.Hl1, b.Hh1, phases[0]);
    }

    /// <summary>每執行緒的暫存（避免在平行迴圈中重複配置 <see cref="LiftRow"/> 的 scratch）。</summary>
    private static class ThreadLocals
    {
        [ThreadStatic]
        public static List<int>? Even;
    }
}
