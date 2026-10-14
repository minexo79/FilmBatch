using System.IO.Hashing;
using System.Text;

namespace FilmBatch.Core.Random;

/// <summary>
/// 與 shader 相同演算法的整數雜湊（PCG 雜湊）。不使用 System.Random，因其演算法可能隨 .NET 版本改變。
/// </summary>
public static class SeedHash
{
    /// <summary>照片的基礎種子：檔名的 CRC32。同一張照片每次結果相同，不同照片自然不同。</summary>
    public static uint ForFileName(string fileName) => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(fileName));

    /// <summary>PCG 雜湊（Jarzynski &amp; Olano 2020），WGSL 端實作需逐位元相同。</summary>
    public static uint Pcg(uint v)
    {
        var state = unchecked(v * 747796405u + 2891336453u);
        var word = unchecked(((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u);
        return (word >> 22) ^ word;
    }

    /// <summary>模組實際使用的種子 = 照片種子 ⊕ 模組種子參數。</summary>
    public static uint Combine(uint photoSeed, uint moduleSeed) => Pcg(photoSeed ^ Pcg(moduleSeed));

    /// <summary>[0,1) 浮點亂數。</summary>
    public static float ToUnitFloat(uint h) => (h >> 8) * (1.0f / 16777216.0f);
}
