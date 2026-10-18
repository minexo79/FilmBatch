using System.Runtime.InteropServices;
using System.Text.Json;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;

namespace FilmBatch.Core.Tests;

/// <summary>
/// RAW 來源顯影（解碼 → 起始外觀擬合 → 顯影 → 色彩 → 縮放 → 轉正）對照 LightCraft <c>loaddump</c>
/// （<c>load&lt;edge&gt;.bin</c>：線性 Rec.2020 f32；<c>load&lt;edge&gt;.json</c>：尺寸與擬合出的相機色調）。
/// 抓得到相機 JPEG 外觀擬合、色彩矩陣、高光重建、縮放或轉正的錯誤；需要 FILMBATCH_RAW_SAMPLES 與 FILMBATCH_RAW_ORACLE（CI 略過）。
/// </summary>
public sealed class RawRenderOracleTests
{
    // JPEG 解碼器不同會移動分位數節點；比較實際曲線作用後的 RGB，而不是不同 x 位置的節點座標。
    // 保留線性影像 2% NRMS 上限，顯示端平均絕對誤差上限為 4 個 8-bit 色階。
    private const double MaxImageNrms = 0.02;
    private const double MaxDisplayMean8 = 4;

    public static IEnumerable<object[]> Loads()
    {
        if (Environment.GetEnvironmentVariable("FILMBATCH_RAW_ORACLE") is not { Length: > 0 } oracle || !Directory.Exists(Path.Combine(oracle, "load")))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(oracle, "load")).Order(StringComparer.Ordinal))
            foreach (var edge in new[] { 200, 1600 })
                if (File.Exists(Path.Combine(dir, $"load{edge}.json")))
                    yield return [Path.GetFileName(dir), edge];
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Loads))]
    public void DevelopLinear_MatchesLightCraftLoad(string stem, int edge)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("FILMBATCH_RAW_ORACLE")!, "load", stem);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, $"load{edge}.json")));
        var expected = doc.RootElement;
        var bytes = File.ReadAllBytes(Sample(expected.GetProperty("file").GetString()!));

        var (image, tone) = RawSourceLoader.DevelopLinear(RawDecoder.Decode(bytes), bytes, edge);

        Assert.Equal((expected.GetProperty("width").GetInt32(), expected.GetProperty("height").GetInt32()), (image.Width, image.Height));
        var expectedTone = expected.GetProperty("camera_tone");
        Assert.Equal(expectedTone.ValueKind == JsonValueKind.Object, tone is not null);
        CameraTone? referenceTone = null;
        if (expectedTone.ValueKind == JsonValueKind.Object)
        {
            var knots = expectedTone.GetProperty("knots").EnumerateArray().SelectMany(k => k.EnumerateArray()).Select(v => v.GetSingle()).ToArray();
            var chroma = expectedTone.GetProperty("chroma").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            referenceTone = CameraTone.Create(knots)?.WithChroma(chroma);
            Assert.NotNull(referenceTone);
        }
        var reference = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(dir, $"load{edge}.bin")).AsSpan(8));
        Assert.Equal(reference.Length, image.Data.Length);
        double diff2 = 0, ref2 = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            var d = (double)image.Data[i] - reference[i];
            diff2 += d * d;
            ref2 += (double)reference[i] * reference[i];
        }
        Assert.InRange(Math.Sqrt(diff2 / Math.Max(ref2, 1e-30)), 0, MaxImageNrms);

        // 同一顯示編碼套在各自的線性影像／色調，隔離來源顯影與擬合差異（編碼本身另有 RawToneTests）。
        var actualDisplay = RawDisplay.Encode(image, tone);
        var expectedDisplay = RawDisplay.Encode(new(image.Width, image.Height, reference.ToArray()), referenceTone);
        long displayError = 0;
        for (var i = 0; i < actualDisplay.Length; i += 4)
            for (var c = 0; c < 3; c++)
                displayError += Math.Abs(actualDisplay[i + c] - expectedDisplay[i + c]);
        Assert.InRange((double)displayError / (image.Data.Length * 257L), 0, MaxDisplayMean8);
    }

    private static string Sample(string name)
    {
        var root = Environment.GetEnvironmentVariable("FILMBATCH_RAW_SAMPLES");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "Set FILMBATCH_RAW_SAMPLES to the pinned CC0 RAW corpus.");
        var path = Path.Combine(root!, name);
        Assert.SkipUnless(File.Exists(path), $"RAW sample absent: {name}");
        return path;
    }
}
