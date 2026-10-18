// 移植自 LightCraft crates/engine/src/camera_profiles.rs（內建相機色彩描述檔查詢；僅讀取）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
// 描述檔本體（aggregate colour statistics only）見 Profiles/*.json；校準／寫入 API 不移植。
using System.Text.Json;
using FilmBatch.Raw.Color;

namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>
/// 依機型彙整多張照片擬合的色彩模型（camera_profiles 的讀取面）：
/// 白平衡後相機 RGB → 線性 Rec.2020 矩陣＋色相／飽和表。
internal sealed class CameraProfile
{
    /// <summary>描述檔格式版本（camera_profiles 的 <c>VERSION</c> = 1）。</summary>
    public const uint Version = 1;

    public string Model { get; }
    public int Files { get; }
    public int Samples { get; }
    private readonly double[] _matrix;
    public HsvTable? HueSatTable { get; }

    internal CameraProfile(string model, int files, int samples, double[] matrix, HsvTable? hueSat)
    {
        Model = model;
        Files = files;
        Samples = samples;
        _matrix = matrix;
        HueSatTable = hueSat;
    }

    public Mat3 Matrix() =>
        new(_matrix[0], _matrix[1], _matrix[2], _matrix[3], _matrix[4], _matrix[5], _matrix[6], _matrix[7], _matrix[8]);
}

/// <summary>內建八機型的描述檔查詢（camera_profiles 的 <c>get/bundled</c> 讀取面；不含寫入與使用者目錄）。</summary>
internal static class CameraProfiles
{
    private static readonly Dictionary<string, CameraProfile?> _cache = new(StringComparer.Ordinal);
    private static readonly object _lock = new();

    /// <summary>內建機型（camera_profiles 的 <c>BUNDLED</c> 鍵；EXIF Model 原樣）。</summary>
    public static readonly string[] BundledModels =
    [
        "ILCA-99M2", "ILCE-7CR", "ILCE-7M4", "NIKON D5300",
        "NIKON Z 6_2", "NIKON Z 8", "X-H2S", "X-T4",
    ];

    /// <summary>該機型的描述檔（camera_profiles 的 <c>get</c> 內建面；無則 null；程序內快取）。</summary>
    public static CameraProfile? Get(string? model)
    {
        model = model?.Trim();
        if (string.IsNullOrEmpty(model))
            return null;
        lock (_lock)
        {
            if (_cache.TryGetValue(model, out var hit))
                return hit;
            var profile = LoadBundled(model);
            _cache[model] = profile;
            return profile;
        }
    }

    /// <summary>描述檔檔名（camera_profiles 的 <c>file_name</c>；供 parent 診斷用）。</summary>
    public static string FileName(string model)
    {
        var name = new string(model.Trim().Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' ? c : '_').ToArray());
        return name + ".json";
    }

    private static CameraProfile? LoadBundled(string model)
    {
        var asm = typeof(CameraProfiles).Assembly;
        foreach (var key in BundledKeyCandidates(model))
        {
            var resource = $"FilmBatch.Engine.Imaging.Raw.Profiles.{key}.json";
            using var stream = asm.GetManifestResourceStream(resource);
            if (stream is null)
                continue;
            try
            {
                using var reader = new StreamReader(stream);
                var profile = Parse(reader.ReadToEnd(), $"built-in profile {model}");
                if (profile is not null && profile.Model.Trim() == model)
                    return profile;
                return null;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static IEnumerable<string> BundledKeyCandidates(string model)
    {
        // 資源檔名即 upstream assets 名稱；空格鍵（NIKON Z 8）對底線檔名
        yield return model.Replace(' ', '_');
    }

    private static CameraProfile? Parse(string json, string origin)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != (int)CameraProfile.Version)
                return null;
            if (!root.TryGetProperty("model", out var modelProp) || modelProp.GetString() is not { } model || model.Length == 0)
                return null;
            if (!root.TryGetProperty("matrix", out var matrixProp) || matrixProp.GetArrayLength() != 3)
                return null;
            var matrix = new double[9];
            var i = 0;
            foreach (var row in matrixProp.EnumerateArray())
            {
                if (row.GetArrayLength() != 3)
                    return null;
                foreach (var v in row.EnumerateArray())
                {
                    var d = v.GetDouble();
                    if (!double.IsFinite(d) || Math.Abs(d) >= 8.0)
                        return null;
                    matrix[i++] = d;
                }
            }
            var mat = new Mat3(matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5], matrix[6], matrix[7], matrix[8]);
            if (mat.Inverse() is null)
                return null;
            HsvTable? hueSat = null;
            if (root.TryGetProperty("hue_sat", out var hs) && hs.ValueKind != JsonValueKind.Null)
            {
                hueSat = ParseHsvTable(hs);
                if (hueSat is null)
                    return null;
            }
            var files = root.TryGetProperty("files", out var f) ? f.GetInt32() : 0;
            var samples = root.TryGetProperty("samples", out var s) ? s.GetInt32() : 0;
            return new CameraProfile(model, files, samples, matrix, hueSat);
        }
    }

    private static HsvTable? ParseHsvTable(JsonElement e)
    {
        if (!e.TryGetProperty("hue_divisions", out var h) || !e.TryGetProperty("sat_divisions", out var s) || !e.TryGetProperty("val_divisions", out var v))
            return null;
        var (hd, sd, vd) = (h.GetInt32(), s.GetInt32(), v.GetInt32());
        if (hd < 1 || hd > 4096 || sd < 2 || sd > 4096 || vd < 1 || vd > 4096)
            return null;
        if (!e.TryGetProperty("data", out var data) || data.GetArrayLength() != (long)hd * sd * vd)
            return null;
        var flat = new float[(long)hd * sd * vd * 3];
        var i = 0;
        foreach (var cell in data.EnumerateArray())
        {
            if (cell.GetArrayLength() != 3)
                return null;
            var j = 0;
            foreach (var n in cell.EnumerateArray())
            {
                var f = (float)n.GetDouble();
                if (!float.IsFinite(f))
                    return null;
                if (j == 1 && f < 0f) return null;
                if (j == 2 && f < 0f) return null;
                flat[i++] = f;
                j++;
            }
        }
        var srgb = e.TryGetProperty("srgb_value", out var sv) && sv.ValueKind == JsonValueKind.True;
        return new HsvTable { HueDivisions = hd, SatDivisions = sd, ValDivisions = vd, Data = flat, SrgbValue = srgb };
    }
}
