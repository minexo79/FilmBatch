// 移植自 LightCraft crates/raw 的顯影階段（對照 LightCraft 的 stagedump 參考輸出）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using FilmBatch.Raw.Develop;
using FilmBatch.Raw.Raster;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests.Develop;

/// <summary>
/// 參考輸出同位對照（輸出 bit-exact 的 FNV-1a-64）：normalized、develop-bilinear/ppg/ahd、
/// highlight-ahd、binned2/binned4（含遮罩的 FNV）。有 rawdump/&lt;stem&gt;.bin 時以參考感光資料重建，
/// 隔離顯影與解碼；其餘樣本用 C# 解碼結果（感光資料已由 VendorCorpusTests／SonyCorpusTests 以 FNV 對照）。
/// 沒有樣本資料夾時（CI）整組略過。
/// </summary>
public sealed class OracleStagesTests
{
    public static IEnumerable<object[]> Stems() => Corpus.Stems("").Select(s => new object[] { s });

    private static OracleStages LoadStages(string stem)
    {
        // 缺少 stages/<stem>/info.json：上游拒解或未轉儲的樣本 → 整個測試略過。
        // 注意：Assert.Skip 丟出跳過例外，不可用 try/catch 包住 Load，否則會把跳過吃掉。
        var info = Corpus.OracleDir is { } dir ? Path.Combine(dir, "stages", stem, "info.json") : null;
        if (info is null || !File.Exists(info)) Assert.Skip($"{stem}: no LightCraft stages (upstream refusal or undumped sample).");
        var stages = OracleStages.Load(stem);
        if (!File.Exists(Path.Combine(stages.Directory, "normalized.tl.bin"))) Assert.Skip($"{stem}: no stage windows.");
        return stages;
    }

    /// <summary>stagedump 的輸入：解碼後移除鏡頭校正（files::load_bytes_now 同樣不把它烘進來源）。</summary>
    private static RawImage RawOf(OracleStages stages)
    {
        if (File.Exists(Path.Combine(Corpus.OracleDir!, "rawdump", stages.Stem + ".bin")))
            return stages.ToRawImage();
        var raw = RawDecoder.Decode(File.ReadAllBytes(Corpus.Sample(stages.Root.GetProperty("file").GetString()!)));
        raw.Opcodes.List3.RemoveAll(op => op.IsLensCorrection());
        return raw;
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Stems))]
    public void NormalizedIsBitExact(string stem)
    {
        var stages = LoadStages(stem);
        var n = RawOf(stages).Normalized();
        var stage = stages.Stage("normalized");
        Assert.Equal((stage.Width, stage.Height, stage.Cpp), (n.Width, n.Height, n.Cpp));
        Assert.Equal(stage.Fnv1a64, Fnv.Hash(n.Data));
        foreach (var window in stage.Windows)
            Assert.Equal(stages.F32Window("normalized", window.Name), Window(n.Data, n.Width, n.Cpp, window));
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Stems))]
    public void DevelopMethodsAreBitExact(string stem)
    {
        var stages = LoadStages(stem);
        var raw = RawOf(stages);
        foreach (var (name, method) in new[] { ("develop-bilinear", DemosaicMethod.Bilinear), ("develop-ppg", DemosaicMethod.Ppg), ("develop-ahd", DemosaicMethod.Ahd) })
        {
            if (!stages.HasStage(name)) continue;
            var img = raw.Develop(method);
            var stage = stages.Stage(name);
            Assert.Equal((stage.Width, stage.Height, stage.Cpp), (img.Width, img.Height, 3));
            Assert.Equal(stage.Fnv1a64, Fnv.Hash(img.Data));
        }
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Stems))]
    public void HighlightReconstructIsBitExact(string stem)
    {
        var stages = LoadStages(stem);
        if (!stages.HasStage("highlight-ahd")) return;
        var img = RawOf(stages).Develop(DemosaicMethod.Ahd);
        var wb = stages.Root.GetProperty("transform_file_only").GetProperty("wb").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        var rebuilt = Highlight.Reconstruct(img, wb, .99f);
        var stage = stages.Stage("highlight-ahd");
        Assert.Equal(stage.Fnv1a64, Fnv.Hash(img.Data));
        if (stages.Root.GetProperty("stages").GetProperty("highlight-ahd").TryGetProperty("rebuilt", out var r)) Assert.Equal(r.GetInt32(), rebuilt);
    }

    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(Stems))]
    public void BinnedIsBitExactWithMask(string stem)
    {
        var stages = LoadStages(stem);
        var raw = RawOf(stages);
        foreach (var k in new[] { 2, 4 })
        {
            var name = $"binned{k}";
            if (!stages.HasStage(name)) continue;
            if (raw.DevelopBinnedMasked(k, .99f) is not { } pair) { Assert.Fail($"no binned{k} for {stem} (oracle has {name})"); return; }

            var stage = stages.Stage(name);
            Assert.Equal((stage.Width, stage.Height), (pair.Image.Width, pair.Image.Height));
            Assert.Equal(stage.Fnv1a64, Fnv.Hash(pair.Image.Data));
            if (stages.Root.GetProperty("stages").GetProperty(name).TryGetProperty("mask_fnv1a64", out var m)) Assert.Equal(Convert.ToUInt64(m.GetString(), 16), Fnv.Hash(pair.Mask));
        }
    }

    private static float[] Window(float[] data, int width, int cpp, OracleWindow w)
    {
        var outp = new float[w.Width * w.Height * cpp];
        for (var y = 0; y < w.Height; y++)
            Array.Copy(data, ((w.Y + y) * width + w.X) * cpp, outp, y * w.Width * cpp, w.Width * cpp);
        return outp;
    }
}
