using System.Text.Json;

namespace FilmBatch.Core.Film;

/// <summary>
/// Sony 成像模擬的色模參數（計畫書 7.3）：創意風格基底、每格係數、白平衡偏移、DRO、照片效果。
/// 資料來自內嵌的 <c>creative_styles.json</c>；<see cref="Calibrated"/> 為 false 時不得宣稱已校正。
/// 與 EffectPipeline 共用：引擎的 film stage 以此解讀 <c>film.*</c> 內嵌欄位。
/// 風格基底在載入時一次解析為型別化快取（無逐幀配置、無已處置文件殘留）。
/// </summary>
public sealed class CreativeStyleTable
{
    /// <summary>風格基底（載入時解析；矩陣為 9 元列主序，不可變）。</summary>
    public sealed record StyleBase(double K, double Shift, double[] Matrix, double[]? MonoWeights, bool Sepia);

    private readonly Dictionary<string, StyleBase> _styles;
    private readonly double[] _contrastPerStep;
    private readonly double[] _saturationPerStep;
    private readonly HashSet<string> _supportedEffects;
    private readonly Dictionary<string, EffectParams> _effects;
    private readonly Dictionary<string, (double Shadow, double Highlight)> _dro;

    /// <summary>照片效果參數（載入時解析；Tints 為效果選項 → 3 元染色）。</summary>
    public sealed record EffectParams(
        double Desaturate,
        double WarmShift,
        double FadeBlack,
        double Lift,
        double ContrastScale,
        double GrainBoost,
        double[]? Weights,
        Dictionary<string, double[]> Tints);

    public bool Calibrated { get; }
    public double AmberBlueMiredPerStep { get; }
    public double GreenMagentaCcPerStep { get; }

    internal CreativeStyleTable(JsonDocument doc)
    {
        var root = doc.RootElement;
        Calibrated = root.GetProperty("calibrated").GetBoolean();
        AmberBlueMiredPerStep = root.GetProperty("amberBlueMiredPerStep").GetDouble();
        GreenMagentaCcPerStep = root.GetProperty("greenMagentaCcPerStep").GetDouble();
        _styles = root.GetProperty("styles").EnumerateObject().ToDictionary(
            p => p.Name, p => ReadStyle(p.Value), StringComparer.Ordinal);
        _contrastPerStep = ReadSteps(root.GetProperty("contrastPerStep"));
        _saturationPerStep = ReadSteps(root.GetProperty("saturationPerStep"));
        _supportedEffects = new(root.GetProperty("supportedEffects").EnumerateArray().Select(e => e.GetString()!), StringComparer.Ordinal);
        _effects = root.GetProperty("effects").EnumerateObject().ToDictionary(
            p => p.Name, p => ReadEffect(p.Value), StringComparer.Ordinal);
        _dro = root.GetProperty("dro").EnumerateObject().ToDictionary(
            p => p.Name,
            p => (p.Value.GetProperty("shadow").GetDouble(), p.Value.GetProperty("highlight").GetDouble()),
            StringComparer.Ordinal);
    }

    public static CreativeStyleTable LoadEmbedded()
    {
        using var stream = typeof(CreativeStyleTable).Assembly
            .GetManifestResourceStream("FilmBatch.Core.Film.creative_styles.json")
            ?? throw new InvalidOperationException("Missing embedded creative_styles.json.");
        // creative_styles.json 含 // 註解：必須跳過註解，否則預設設定會擲回例外。
        using var doc = JsonDocument.Parse(stream,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return new CreativeStyleTable(doc);
    }

    public bool HasStyle(string style) => _styles.ContainsKey(style);

    /// <summary>風格基底（快取，呼叫端不得修改回傳的矩陣）。未知風格回傳恆等。</summary>
    public StyleBase StyleBaseOf(string style) =>
        _styles.TryGetValue(style, out var s) ? s : _styles["standard"];


    public double ContrastFactor(int steps) => steps is >= -3 and <= 3 ? _contrastPerStep[steps + 3] : 1.0;

    public double SaturationFactor(int steps) => steps is >= -3 and <= 3 ? _saturationPerStep[steps + 3] : 1.0;

    /// <summary>白平衡微調 → (mired 偏移, CC 偏移)。</summary>
    public (double Mired, double Cc) WhiteBalanceShift(int amberBlue, int greenMagenta) =>
        (amberBlue * AmberBlueMiredPerStep, greenMagenta * GreenMagentaCcPerStep);

    public bool IsEffectSupported(string effect) => _supportedEffects.Contains(effect);

    public (double Shadow, double Highlight) DroLevels(string dro) =>
        _dro.TryGetValue(dro, out var v) ? v : (0.0, 0.0);

    public bool TryGetEffect(string effect, out EffectParams parameters) =>
        _effects.TryGetValue(effect, out parameters!);

    private static StyleBase ReadStyle(JsonElement el) => new(
        el.GetProperty("k").GetDouble(),
        el.GetProperty("shift").GetDouble(),
        el.GetProperty("matrix").EnumerateArray().Select(e => e.GetDouble()).ToArray(),
        el.TryGetProperty("monoWeights", out var weights) ? weights.EnumerateArray().Select(e => e.GetDouble()).ToArray() : null,
        el.TryGetProperty("sepia", out var sepia) && sepia.GetBoolean());

    private static double[] ReadSteps(JsonElement el)
    {
        var steps = new double[7];
        foreach (var p in el.EnumerateObject())
            if (int.TryParse(p.Name, out var s) && s is >= -3 and <= 3)
                steps[s + 3] = p.Value.GetDouble();
        return steps;
    }

    private static EffectParams ReadEffect(JsonElement el)
    {
        static double Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.GetDouble() : 0.0;
        static double[]? Vec(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
        var tints = new Dictionary<string, double[]>(StringComparer.Ordinal);
        if (el.TryGetProperty("tints", out var t))
            foreach (var p in t.EnumerateObject())
                tints[p.Name] = p.Value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        return new EffectParams(
            Num(el, "desaturate"), Num(el, "warmShift"), Num(el, "fadeBlack"),
            Num(el, "lift"), Num(el, "contrastScale"), Num(el, "grainBoost"),
            Vec(el, "weights") ?? Vec(el, "monoWeights"), tints);
    }
}
