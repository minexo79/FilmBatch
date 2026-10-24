namespace FilmBatch.Engine.Denoising;

/// <summary>
/// 已驗證的上游 AI 降噪模型描述。正典建構子固定；<see cref="OnnxSha256"/>／
/// <see cref="OnnxBytes"/> 是安裝後檔案本身（ZIP 內 entry）的固定指紋，供啟用前驗證與
/// 偵測檔案被替換。模型檔是使用者資料、永不內嵌；安裝一律明確觸發並驗證 SHA-256。
/// </summary>
public sealed record DenoiseModelSpec(string Id, string Name, string FileName, Uri DownloadUri,
    string ArchiveSha256, long ArchiveBytes, string OnnxEntry, int TileSize, int Border,
    bool SceneLinear, bool MatchGain, string License, string Attribution)
{
    /// <summary>安裝後 ONNX 檔案的 SHA-256（十六進位小寫）。</summary>
    public string OnnxSha256 { get; init; } = "";
    /// <summary>安裝後 ONNX 檔案的位元組數。</summary>
    public long OnnxBytes { get; init; }
    /// <summary>ONNX 圖的輸入 tensor 名稱（已驗證：兩模型皆為 <c>input</c>）。</summary>
    public string InputName { get; init; } = "input";
    /// <summary>ONNX 圖的輸出 tensor 名稱（已驗證：兩模型皆為 <c>output</c>）。</summary>
    public string OutputName { get; init; } = "output";

    /// <summary>每個 tile 的中心寫回區邊長；tile 四周 <see cref="Border"/> 像素只做上下文、不寫回。</summary>
    public int CoreSize => TileSize - 2 * Border;
}
