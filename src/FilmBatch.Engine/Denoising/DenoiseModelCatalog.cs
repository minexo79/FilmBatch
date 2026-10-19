using FilmBatch.Engine.Imaging;

namespace FilmBatch.Engine.Denoising;

/// <summary>
/// 已驗證模型的固定目錄。模型選擇只看來源資料域：場景線性 Rec.2020（RAW）走
/// RawNIND linear，一般 16-bit sRGB 走 NAFNet-SIDD；不提供不安全的手動替換。
/// 雜湊／位元組數取自實際下載的官方封存與解出的 ONNX（見實作計畫與調查記錄）。
/// </summary>
public static class DenoiseModelCatalog
{
    /// <summary>RawNIND linear：RAW 場景線性 Rec.2020（已套用白平衡、不量化）。FP32 [1,3,512,512]，tile 512／border 64／core 384。</summary>
    public static readonly DenoiseModelSpec RawNindLinear = new(
        Id: "rawnind-linear",
        Name: "RawNIND Linear",
        FileName: "rawnind-linear.onnx",
        DownloadUri: new Uri("https://github.com/darktable-org/darktable-ai/releases/download/nightly-5.7.0/rawdenoise-nind.dtmodel"),
        ArchiveSha256: "1a0db3dc4a4db13ce5eee7aa2fed097914be28a3f628dc6071dc6aa8d16377bc",
        ArchiveBytes: 57700134,
        OnnxEntry: "rawdenoise-nind/model_linear.onnx",
        TileSize: 512,
        Border: 64,
        SceneLinear: true,
        MatchGain: true,
        License: "CC-BY-4.0 (weights; GPL-3.0 alternative per author)",
        Attribution: "RawNIND by Benoit Brummer / Brummer & Christophe De Vleeschouwer")
    {
        OnnxSha256 = "df957efadcc152c007d5d3b0917bdff9e41c0d4a0efe56584ef30b36393cd181",
        OnnxBytes = 31053823,
    };

    /// <summary>NAFNet-SIDD width32：一般 sRGB（gamma 編碼、[0,1]）。FP32 [1,3,768,768]，tile 768／border 64／core 640。</summary>
    public static readonly DenoiseModelSpec NafNetSidd = new(
        Id: "nafnet-sidd",
        Name: "NAFNet SIDD",
        FileName: "nafnet-sidd.onnx",
        DownloadUri: new Uri("https://github.com/darktable-org/darktable-ai/releases/download/nightly-5.7.0/denoise-nafnet.dtmodel"),
        ArchiveSha256: "027961b3e6d1d4d2ba2d7a412688c0542e6a6e2cf7ec2f8e16030cf0a311fb0a",
        ArchiveBytes: 108413592,
        OnnxEntry: "denoise-nafnet/model.onnx",
        TileSize: 768,
        Border: 64,
        SceneLinear: false,
        MatchGain: false,
        License: "MIT",
        Attribution: "NAFNet by Megvii Research; SIDD dataset")
    {
        OnnxSha256 = "8b437280db2f9f0ef5c733fa0fec70fc10012a62d19dac24a25b80ec8c529230",
        OnnxBytes = 117341957,
    };

    public static IReadOnlyList<DenoiseModelSpec> All { get; } = [RawNindLinear, NafNetSidd];

    /// <summary>依來源域選模型；場景線性（RAW）→ RawNIND linear，一般 sRGB → NAFNet。</summary>
    public static DenoiseModelSpec ForSource(SourceImage source) =>
        source.IsSceneLinear ? RawNindLinear : NafNetSidd;
}
