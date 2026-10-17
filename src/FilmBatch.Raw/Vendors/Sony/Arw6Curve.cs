// 移植自 LightCraft crates/raw/src/vendor/arw6_curve.rs（ARW6 壓擴曲線：12-bit tile 碼 → 2×14-bit DN 輸出值；黑 1024、白 32766）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw.Vendors.Sony;

/// <summary>
/// ARW6 壓擴曲線：分段線性通過節點 (c_k, 128k)；c_k = 128k（k ≤ 11），否則 356.89 + 655.646·ln(k−6)；
/// 每個碼值是四捨五入的插值。由 LightCraft 以 ILCE-7RM6 的 CC0 lossless/ARW6 檔對量得，檔案本身不儲存；
/// 見 LightCraft docs/arw6-compression.md（Output range）。
/// </summary>
internal static class Arw6Curve
{
    /// <summary>節點 <paramref name="k"/> 的碼，其輸出值為 128k。</summary>
    private static double Knot(uint k) => k <= 11 ? 128.0 * k : 356.89 + 655.646 * Math.Log(k - 6);

    private static readonly ushort[] Table = Build();

    private static ushort[] Build()
    {
        var t = new ushort[4096];
        var k = 0u;
        for (var code = 0; code < t.Length; code++)
        {
            var c = (double)code;
            while (Knot(k + 1) < c)
                k += 1;
            var c0 = Knot(k);
            var c1 = Knot(k + 1);
            var u0 = 128.0 * k;
            t[code] = (ushort)Math.Floor(u0 + 128.0 * (c - c0) / (c1 - c0) + 0.5);
        }
        return t;
    }

    /// <summary>壓擴曲線：12-bit 碼（夾到 0..=4095）→ 輸出值。</summary>
    public static ushort Curve(int code12) => Table[Math.Clamp(code12, 0, 4095)];
}
