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
    /// <summary>未在偏好設定選過語言時使用的介面語言。</summary>
    public const string DefaultLanguage = "en";

    /// <summary>使用者選擇的介面語言；null 表示未選過（跟隨 <see cref="DefaultLanguage"/>，不寫入設定檔）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    /// <summary>
    /// 實際使用的介面語言（啟動與偏好設定共用）：未選過，或設定檔中的值不是支援的語言代碼
    /// （例如手動改成 en-US）時使用 <see cref="DefaultLanguage"/>；只差大小寫仍視為同一語言。
    /// </summary>
    public string EffectiveLanguage
    {
        get
        {
            foreach (var (code, _) in Localization.Loc.Languages)
                if (string.Equals(code, Language, StringComparison.OrdinalIgnoreCase)) return code;
            return DefaultLanguage;
        }
    }

    public PreviewBackground PreviewBackground { get; set; } = PreviewBackground.DarkGray;
    public int PreviewLongEdge { get; set; } = 1600;

    private int _backgroundCpuPercent = 70;

    /// <summary>背景載入的 CPU 軟性目標（全部邏輯核心的 10–100%；100% 不主動節流）。</summary>
    public int BackgroundCpuPercent
    {
        get => _backgroundCpuPercent;
        set => _backgroundCpuPercent = Math.Clamp(value, 10, 100);
    }
    public string GpuBackend { get; set; } = "Auto";
    public string? GpuAdapterName { get; set; }
    public bool ForceSoftwareGpu { get; set; }
    public int GpuMemoryLimitMb { get; set; } = 2048;
    public string? TexturesFolder { get; set; }
    /// <summary>外部 Lensfun 格式鏡頭資料庫資料夾（RAW 無內嵌校正時的備援）；null 表示不使用。</summary>
    public string? LensDatabaseFolder { get; set; }
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
