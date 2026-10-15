using System.Text.Json;
using System.Text.Json.Serialization;
using FilmBatch.Core;

namespace FilmBatch.App.Services;

public enum PreviewBackground { DarkGray, MidGray, Black }

/// <summary>指定預設時目標照片已有覆寫的處理方式（「記住我的選擇」）。</summary>
public enum AssignOverridesChoice { Ask, Keep, Clear }

/// <summary>偏好設定與視窗狀態（settings.json，計畫書 8.1）。</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "zh-TW";
    public PreviewBackground PreviewBackground { get; set; } = PreviewBackground.DarkGray;
    public int PreviewLongEdge { get; set; } = 1600;
    public string GpuBackend { get; set; } = "Auto";
    public string? GpuAdapterName { get; set; }
    public bool ForceSoftwareGpu { get; set; }
    public int GpuMemoryLimitMb { get; set; } = 2048;
    public string? TexturesFolder { get; set; }
    public AssignOverridesChoice AssignOverrides { get; set; } = AssignOverridesChoice.Ask;
    public bool HideDisabledModules { get; set; }
    public bool HistogramExpanded { get; set; } = true;
    public string? LastFolder { get; set; }
    public List<string> SelectivePasteModules { get; set; } = [];
    public bool SelectivePasteIncludeSeeds { get; set; }
    public bool SelectivePasteMirror { get; set; }

    // 視窗位置與大小
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJson.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            AppLog.Warn($"無法讀取設定檔，使用預設值：{ex.Message}");
        }
        return new AppSettings();
    }

    public void Save(string? path = null) =>
        AppPaths.WriteAllTextAtomic(path ?? AppPaths.SettingsFile, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
