using System.Text.Json.Serialization;

namespace FilmBatch.Engine.Exporting;

/// <summary>匯出 JPEG、16-bit TIFF／PNG，或保留已算圖線性資料的 half-float DNG。</summary>
public enum ExportFormat { Jpeg, Tiff16, Png16, Dng }

/// <summary>匯出尺寸：原尺寸 / 長邊指定像素。</summary>
public enum ExportSize { Original, LongEdge }

/// <summary>匯出選項（UI 與 CLI 共用；不可變快照）。</summary>
public sealed record ExportOptions
{
    public required string Destination { get; init; }
    public string FileNameTemplate { get; init; } = "{name}_{preset}";
    public ExportFormat Format { get; init; } = ExportFormat.Jpeg;
    /// <summary>JPEG 品質 1–100（非 JPEG 時忽略）。超出範圍由呼叫端驗證。</summary>
    public int Quality { get; init; } = 95;
    public ExportSize Size { get; init; } = ExportSize.Original;
    /// <summary>長邊像素（Size == LongEdge 時使用；&lt;=0 視為原尺寸）。</summary>
    public int LongEdgePx { get; init; }

    /// <summary>暫時覆蓋照片的原廠鏡頭校正選項；不修改專案或預設。</summary>
    public bool? EmbeddedLensCorrections { get; init; }

    /// <summary>暫時覆蓋照片的相機局部色調選項；不修改專案或預設。</summary>
    public bool? CameraLocalTone { get; init; }
}

/// <summary>單張匯出的不可變快照（匯出期間的編輯不影響已排入的照片，計畫書 2.6）。</summary>
public sealed record ExportJob
{
    public required string FileName { get; init; }
    public required string SourcePath { get; init; }
    public required FilmBatch.Core.Params.Settings Settings { get; init; }
    /// <summary>此照片的確定性種子（檔名 CRC32；算圖器再與模組種子結合）。</summary>
    public required uint Seed { get; init; }
    public required string PresetName { get; init; }
    public required int Index { get; init; }
    public required int PipelineVersion { get; init; }
}

/// <summary>單張匯出的 IProgress 回報。</summary>
public sealed record BatchExportProgress(int Done, int Total, string CurrentFile, int Failed);

/// <summary>整批匯出的結果摘要（成功/失敗/取消皆誠實回報；失敗的照片沒有假輸出）。</summary>
public sealed record BatchExportSummary(
    int Total,
    int Succeeded,
    int Failed,
    bool Cancelled,
    IReadOnlyList<string> OutputFiles,
    IReadOnlyDictionary<string, string> Errors);

/// <summary>匯出請求：服務 + 檔名清單 + 選項（Jobs 於執行前由快照建立）。</summary>
public sealed record ExportRequest
{
    public required FilmBatch.Core.Projects.ProjectService Service { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required ExportOptions Options { get; init; }
}

/// <summary>export_log.json 的單筆紀錄（計畫書 10：完整參數、演算法版本、GPU 資訊，方便重現）。</summary>
public sealed class ExportLogEntry
{
    public required string Source { get; set; }
    public required string Output { get; set; }
    public required string Preset { get; set; }
    public required Dictionary<string, string> Settings { get; set; }
    public required uint Seed { get; set; }
    public required int PipelineVersion { get; set; }
    public required int Width { get; set; }
    public required int Height { get; set; }
    public required string Format { get; set; }
    public int Quality { get; set; }
    public required string Renderer { get; set; }
    public required string Status { get; set; }
    public string? Error { get; set; }
    /// <summary>Pixels were exported, but source EXIF was omitted or reduced; null when preserved.</summary>
    public string? MetadataWarning { get; set; }
    /// <summary>Pixels were exported without external Lensfun corrections because the database could not be read; null otherwise.</summary>
    public string? LensDatabaseWarning { get; set; }
}

public sealed class ExportLogDocument
{
    public required string ExportedAt { get; set; }
    public required string Device { get; set; }
    public required bool IsSoftwareRenderer { get; set; }
    public required IReadOnlyList<ExportLogEntry> Entries { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ExportLogDocument))]
internal sealed partial class ExportLogJson : JsonSerializerContext;
