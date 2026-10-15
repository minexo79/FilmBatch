// 校正參數模型：creative_styles.json 中參與擬合的數值子集。
// 完整檔案（含註解、effects、dro、supportedEffects）的讀寫見 Program.EmitCreativeStyles。
// effects / dro / 白平衡外其餘係數不在 7.6 擬合範圍內，原樣保留。
using System.Text.Json;

sealed class CalibModel
{
    public static readonly string[] StyleOrder =
        ["standard", "vivid", "neutral", "portrait", "landscape", "mono",
         "clear", "deep", "light", "sunset", "night", "red-leaves", "sepia"];

    // Factory Standard stays identity: the input is already a processed photograph.
    public static readonly string[] KShiftStyles =
        ["vivid", "neutral", "portrait", "mono", "light", "sepia", "clear", "deep"];
    public static readonly string[] MatrixStyles =
        ["vivid", "neutral", "portrait", "light", "clear", "deep"];

    public readonly Dictionary<string, double[]> KShift = new(StringComparer.Ordinal);
    // 全 9 元列主序；擬合以「列和歸一」投影維持中性灰，不以 (a,b) 參數化
    // （後者會把現有非和-1 列硬壓成和 1，等值即分叉）。
    public readonly Dictionary<string, double[]> Matrix = new(StringComparer.Ordinal);
    public double[] Contrast = new double[7];
    public double[] Saturation = new double[7];
    public double AbMired;
    public double GmCc;

    public static CalibModel Load(string srcJson)
    {
        using var doc = JsonDocument.Parse(srcJson,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var m = new CalibModel();
        foreach (var name in StyleOrder)
        {
            var el = root.GetProperty("styles").GetProperty(name);
            m.KShift[name] = [el.GetProperty("k").GetDouble(), el.GetProperty("shift").GetDouble()];
            m.Matrix[name] = el.GetProperty("matrix").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        }
        foreach (var p in root.GetProperty("contrastPerStep").EnumerateObject())
            m.Contrast[int.Parse(p.Name) + 3] = p.Value.GetDouble();
        foreach (var p in root.GetProperty("saturationPerStep").EnumerateObject())
            m.Saturation[int.Parse(p.Name) + 3] = p.Value.GetDouble();
        m.AbMired = root.GetProperty("amberBlueMiredPerStep").GetDouble();
        m.GmCc = root.GetProperty("greenMagentaCcPerStep").GetDouble();
        return m;
    }

    public CalibModel Clone()
    {
        var c = new CalibModel();
        foreach (var (k, v) in KShift) c.KShift[k] = (double[])v.Clone();
        foreach (var (k, v) in Matrix) c.Matrix[k] = (double[])v.Clone();
        c.Contrast = (double[])Contrast.Clone();
        c.Saturation = (double[])Saturation.Clone();
        c.AbMired = AbMired;
        c.GmCc = GmCc;
        return c;
    }

    static string F(double v) => Math.Round(v, 6).ToString("0.######",
        System.Globalization.CultureInfo.InvariantCulture);

    // Candidate coefficients, decoded by the same table reader as production.
    public string ToCandidateJson(string srcJson)
    {
        using var doc = JsonDocument.Parse(srcJson,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"calibrated\":false,\"amberBlueMiredPerStep\":").Append(F(AbMired));
        sb.Append(",\"greenMagentaCcPerStep\":").Append(F(GmCc));
        sb.Append(",\"styles\":{");
        var first = true;
        foreach (var name in StyleOrder)
        {
            if (!first) sb.Append(',');
            first = false;
            var el = root.GetProperty("styles").GetProperty(name);
            var ks = KShift[name];
            sb.Append('"').Append(name).Append("\":{\"k\":").Append(F(ks[0]));
            sb.Append(",\"shift\":").Append(F(ks[1])).Append(",\"matrix\":[");
            // 擬合模型即存全 9 元（歸一投影後仍是 9 元），逐值輸出；等值即與內嵌一致。
            sb.Append(string.Join(",", Matrix[name].Select(F)));
            sb.Append(']');
            if (el.TryGetProperty("monoWeights", out var w))
                sb.Append(",\"monoWeights\":[").Append(string.Join(",", w.EnumerateArray().Select(e => F(e.GetDouble())))).Append(']');
            if (el.TryGetProperty("sepia", out var s) && s.GetBoolean())
                sb.Append(",\"sepia\":true");
            sb.Append('}');
        }
        sb.Append("},\"contrastPerStep\":{");
        for (var i = 0; i < 7; i++)
            sb.Append('"').Append(i - 3).Append("\":").Append(F(Contrast[i])).Append(i < 6 ? "," : "");
        sb.Append("},\"saturationPerStep\":{");
        for (var i = 0; i < 7; i++)
            sb.Append('"').Append(i - 3).Append("\":").Append(F(Saturation[i])).Append(i < 6 ? "," : "");
        sb.Append('}');
        sb.Append(",\"supportedEffects\":").Append(root.GetProperty("supportedEffects").GetRawText());
        sb.Append(",\"effects\":").Append(root.GetProperty("effects").GetRawText());
        sb.Append(",\"dro\":").Append(root.GetProperty("dro").GetRawText());
        if (root.TryGetProperty("calibration", out var cal))
            sb.Append(",\"calibration\":").Append(cal.GetRawText());
        sb.Append('}');
        return sb.ToString();
    }
}

