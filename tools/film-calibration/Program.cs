// 7.6 樣張校正：以樣張 01 為輸入基準，經實際 CpuPipeline 套用各配方，
// 與樣張 n 比較 ΔE00，座標下降法擬合 K/shift、3x3 矩陣、每格係數與白平衡步進。
// 成像數學一律走 Engine 實作；本檔只有係數載入、度量與搜尋。
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;
using System.Text.Json;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Rendering;



static class Program
{
    const int LongEdge = 128;

    static readonly HashSet<string> Overrange = new(StringComparer.Ordinal)
    {
        "sony-fl-film-like", "sony-in-instant", "velvia", "eterna",
        "eterna-bleach-bypass", "fuji-fortia-50", "gr-bleach-bypass",
        "olympus-pop-art", "agfa-ultra-100",
    };
    static readonly HashSet<string> Mtx = new(StringComparer.Ordinal)
    {
        "sony-vv2", "velvia", "fuji-fortia-50", "olympus-pop-art", "agfa-ultra-100",
    };

    sealed record Row(int Frame, FilmRecipe Recipe, string File, string Cls);

    static string _samples = "";
    static string _src = "";
    static string _out = "";
    static string _sourceJson = "";
    static CalibModel _init = null!;
    static readonly List<Row> Rows = new();
    static SourceImage _base = null!;
    static readonly Dictionary<int, SourceImage> Targets = new();
    static readonly Dictionary<int, Settings> RecipeSettings = new();
    static readonly Dictionary<int, double> BaseDE = new();
    static readonly Dictionary<int, double> FinalDE = new();
    static bool _extrapolatedMinimumSaturation;

    static int Main(string[] args)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var wantAssess = args.Contains("--assess");
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--samples" && i + 1 < args.Length) _samples = args[++i];
            else if (args[i] == "--src" && i + 1 < args.Length) _src = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) _out = args[++i];
            else if (args[i] is "--assess" or "--selftest") { }
            else { Console.Error.WriteLine("usage: film-calibrate --samples dir --src creative_styles.json --out dir [--assess|--selftest]"); return 2; }
        if (_samples.Length == 0) { Console.Error.WriteLine("--samples is required."); return 2; }
        if (args.Contains("--selftest")) return SelfTest();
        if (_src.Length == 0 || _out.Length == 0)
        {
            Console.Error.WriteLine("usage: film-calibrate --samples dir --src creative_styles.json --out dir");
            return 2;
        }

        _sourceJson = File.ReadAllText(_src);
        _init = CalibModel.Load(_sourceJson);
        if (wantAssess) return Assess();
        LoadRows();
        DecodeAll();
        BuildSettings();
        Baseline();
        Split(out var train, out var held);
        var fitted = FitStage(train, "train", rounds: 3);
        EvaluateFinal(fitted);
        var trainBefore = train.Average(r => BaseDE[r.Frame]);
        var trainAfter = train.Average(r => FinalDE[r.Frame]);
        var heldBefore = held.Average(r => BaseDE[r.Frame]);
        var heldAfter = held.Average(r => FinalDE[r.Frame]);
        Console.WriteLine($"train meanDE {trainBefore:F3} -> {trainAfter:F3} (n={train.Count})");
        Console.WriteLine($"held  meanDE {heldBefore:F3} -> {heldAfter:F3} (n={held.Count})");
        Directory.CreateDirectory(_out);
        EmitResults();
        EmitCreativeStyles(_sourceJson, fitted, trainBefore, trainAfter, heldBefore, heldAfter);
        Console.WriteLine("done.");
        return 0;
    }

    // Evaluate the supplied table, not whichever version happens to be embedded.
    static int Assess()
    {
        LoadRows();
        DecodeAll();
        BuildSettings();
        Split(out _, out _);
        Baseline();
        Directory.CreateDirectory(_out);
        EmitBaselineOnly();
        Console.WriteLine("assess done.");
        return 0;
    }
    static void EmitBaselineOnly()
    {
        using var w = new StreamWriter(Path.Combine(_out, "baseline.csv"));
        w.WriteLine("frame,key,class,style,sat,con,wb,ab,gm,ev,dro,effect,baseline_DE00");
        foreach (var r in Rows)
        {
            var p = r.Recipe;
            w.WriteLine($"{r.Frame},{p.Key},{r.Cls},{p.Style},{p.Saturation},{p.Contrast}," +
                $"{p.WhiteBalance},{p.AmberBlue},{p.GreenMagenta},{p.Exposure},{p.Dro},{p.Effect},{BaseDE[r.Frame]:F3}");
        }
        var fit = Rows.Where(r => r.Cls is "Fit" or "Held").ToList();
        Console.WriteLine($"baseline fit-set meanDE={fit.Average(r => BaseDE[r.Frame]):F3} (n={fit.Count})");
    }
    static int SelfTest()
    {
        // CIEDE2000 對照 Sharma et al. 測試資料（Gaurav Sharma 實作驗證組）。
        var d1 = ColorMetric.DeltaE00(50.0, 2.6772, -79.7751, 50.0, 0.0, -82.7485);
        var d2 = ColorMetric.DeltaE00(50.0, 3.1571, -77.2803, 50.0, 0.0, -82.7485);
        Console.WriteLine($"de00 ref1={d1:F4} (expect 2.0425) ref2={d2:F4} (expect 2.8615)");
        if (Math.Abs(d1 - 2.0425) > 0.001 || Math.Abs(d2 - 2.8615) > 0.001)
        {
            Console.Error.WriteLine("SELFTEST FAIL: DeltaE00 mismatch");
            return 1;
        }
        // 以 frame01 為輸入、ST 配方走實際 CpuPipeline（目前內嵌表，不換表）。
        var img = ImageLoader.Load(Path.Combine(_samples, "01-factory-st.jpg"), LongEdge);
        Console.WriteLine($"frame01 decoded {img.Width}x{img.Height} short={img.ShortSide}");
        if (img.Width != 128 || img.Height != 85) { Console.Error.WriteLine("SELFTEST FAIL: size"); return 1; }
        var lib = FilmRecipeLibrary.Create();
        if (!lib.TryGet("factory-st", out var st)) { Console.Error.WriteLine("SELFTEST FAIL: recipe"); return 1; }
        var settings = ModuleCatalog.Defaults
            .Overlay(FilmRecipeLibrary.ToSettings(st))
            .With("film.enabled", true);
        var p = FilmBatch.Engine.Rendering.ModuleParams.From(img, settings);
        var rendered = CpuPipeline.Process(img, p, null, CancellationToken.None);
        var de = MeanDE(rendered, img);
        Console.WriteLine($"self-render factory-st vs frame01 meanDE={de:F3}");
        if (de > 0.02) { Console.Error.WriteLine("SELFTEST FAIL: factory neutral drift"); return 1; }
        Console.WriteLine("SELFTEST PASS");
        return 0;
    }

    static void LoadRows()
    {
        var lib = FilmRecipeLibrary.Create();
        var files = Directory.EnumerateFiles(_samples, "*.jpg")
            .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToList();
        if (lib.Builtin.Count != files.Count)
            throw new InvalidOperationException($"recipes {lib.Builtin.Count} != files {files.Count}");
        for (var i = 0; i < lib.Builtin.Count; i++)
        {
            if (!files[i].StartsWith($"{i + 1:00}-", StringComparison.Ordinal))
                throw new InvalidDataException($"Unexpected sample sequence: {files[i]}");
            var r = lib.Builtin[i];
            string cls;
            if (Overrange.Contains(r.Key) || Mtx.Contains(r.Key)) cls = "Excl";
            else if (r.WhiteBalance != "auto") cls = "Kelvin";
            else if (r.Effect != "off") cls = "Effect";
            else cls = "Fit";
            Rows.Add(new Row(i + 1, r, files[i], cls));
        }
        Console.WriteLine($"fit={Rows.Count(r => r.Cls == "Fit")} kelvin={Rows.Count(r => r.Cls == "Kelvin")} " +
            $"effect={Rows.Count(r => r.Cls == "Effect")} excl={Rows.Count(r => r.Cls == "Excl")}");
    }

    static void DecodeAll()
    {
        foreach (var row in Rows)
            Targets[row.Frame] = ImageLoader.Load(Path.Combine(_samples, row.File), LongEdge);
        _base = Targets[1];
        Console.WriteLine($"decoded {Targets.Count} frames at long-edge {LongEdge}");
    }

    static void BuildSettings()
    {
        foreach (var row in Rows)
        {
            var s = ModuleCatalog.Defaults
                .Overlay(FilmRecipeLibrary.ToSettings(row.Recipe))
                .With("film.enabled", true);
            RecipeSettings[row.Frame] = s;
        }
    }

    static CreativeStyleTable? _activeTable;

    static CreativeStyleTable TableFor(CalibModel model)
    {
        using var doc = JsonDocument.Parse(model.ToCandidateJson(_sourceJson));
        return new CreativeStyleTable(doc);
    }

    static SourceImage Render(int frame)
    {
        var p = FilmBatch.Engine.Rendering.ModuleParams.From(_base, RecipeSettings[frame]);
        if (_activeTable is not null)
            p.Film = FilmParameters.From(p, _base.ShortSide, _activeTable);
        return CpuPipeline.Process(_base, p, null, CancellationToken.None);
    }

    static double MeanDE(SourceImage rendered, SourceImage target)
    {
        var a = rendered.Rgba16;
        var b = target.Rgba16;
        if (a.Length != b.Length)
            throw new InvalidOperationException($"size mismatch {a.Length} != {b.Length}");
        double sum = 0;
        var n = a.Length / 4;
        for (var i = 0; i < n; i++)
        {
            var o = i * 4;
            ColorMetric.SrgbToLab(a[o] / 65535.0, a[o + 1] / 65535.0, a[o + 2] / 65535.0,
                out var l1, out var a1, out var b1);
            ColorMetric.SrgbToLab(b[o] / 65535.0, b[o + 1] / 65535.0, b[o + 2] / 65535.0,
                out var l2, out var a2, out var b2);
            sum += ColorMetric.DeltaE00(l1, a1, b1, l2, a2, b2);
        }
        return sum / n;
    }

    static void Baseline()
    {
        _activeTable = TableFor(_init);
        foreach (var row in Rows)
            BaseDE[row.Frame] = MeanDE(Render(row.Frame), Targets[row.Frame]);
        Console.WriteLine($"baseline self frame01 DE={BaseDE[1]:F3}");
        Console.WriteLine($"baseline fit-set meanDE={Rows.Where(r => r.Cls is "Fit" or "Held").Average(r => BaseDE[r.Frame]):F3}");
    }

    // 以 frame 排序、每組第 2/4/... 個進 held-out，保證每風格 train 非空；
    // 單樣本風格（sepia/clear/deep）強制留 train。
    static void Split(out List<Row> train, out List<Row> held)
    {
        train = new List<Row>();
        held = new List<Row>();
        var soloStyles = Rows.Where(r => r.Cls == "Fit").GroupBy(r => r.Recipe.Style)
            .Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        foreach (var g in Rows.Where(r => r.Cls == "Fit").GroupBy(r => r.Recipe.Style).OrderBy(g => g.Key))
        {
            var ordered = g.OrderBy(r => r.Frame).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                var idx = Rows.IndexOf(row);
                var cls = (i % 2 == 1 && !soloStyles.Contains(row.Recipe.Style)) ? "Held" : "Fit";
                Rows[idx] = row with { Cls = cls };
                (cls == "Held" ? held : train).Add(Rows[idx]);
            }
        }
        Console.WriteLine($"train={train.Count} held={held.Count}");
        foreach (var g in train.Concat(held).GroupBy(r => r.Recipe.Style).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key}: train={g.Count(r => r.Cls == "Fit")} held={g.Count(r => r.Cls == "Held")}");
    }

    static double Ridge(CalibModel m)
    {
        double s = 0;
        foreach (var name in CalibModel.KShiftStyles)
        {
            s += Math.Pow((m.KShift[name][0] - _init.KShift[name][0]) / 0.5, 2);
            s += Math.Pow((m.KShift[name][1] - _init.KShift[name][1]) / 0.25, 2);
        }
        foreach (var name in CalibModel.MatrixStyles)
            for (var i = 0; i < 9; i++)
                s += Math.Pow((m.Matrix[name][i] - _init.Matrix[name][i]) / 0.5, 2);
        for (var i = 0; i < 7; i++)
        {
            s += Math.Pow((m.Contrast[i] - _init.Contrast[i]) / 0.5, 2);
            s += Math.Pow((m.Saturation[i] - _init.Saturation[i]) / 0.5, 2);
        }
        s += Math.Pow((m.AbMired - _init.AbMired) / 2.0, 2);
        s += Math.Pow((m.GmCc - _init.GmCc) / 1.5, 2);
        return 0.02 * s;
    }

    // 矩陣元搜索後以列和歸一投影（中性灰不變）；歸一保持連續可微方向，不硬壓和為 1 的初值。
    static void ProjectRows(CalibModel m, string style)
    {
        var v = m.Matrix[style];
        for (var r = 0; r < 3; r++)
        {
            var sum = v[r * 3] + v[r * 3 + 1] + v[r * 3 + 2];
            if (Math.Abs(sum) < 1e-9) continue;
            v[r * 3] /= sum; v[r * 3 + 1] /= sum; v[r * 3 + 2] /= sum;
        }
    }

    static double CostOf(CalibModel m, List<Row> set)
    {
        _activeTable = TableFor(m);
        double sum = 0;
        foreach (var row in set)
            sum += MeanDE(Render(row.Frame), Targets[row.Frame]);
        return sum / set.Count + Ridge(m);
    }

    static CalibModel LineSearch(CalibModel m, List<Row> set, Func<CalibModel, double> get,
        Action<CalibModel, double> put, double lo, double hi, double radius)
    {
        var cur = get(m);
        var a = Math.Max(lo, cur - radius);
        var b = Math.Min(hi, cur + radius);
        if (b - a < 1e-9) return m;
        var best = m;
        var bestCost = CostOf(m, set);
        double Evaluate(double value)
        {
            // Matrix row projection must start from the same candidate each time.
            var candidate = m.Clone();
            put(candidate, value);
            var cost = CostOf(candidate, set);
            if (cost < bestCost) { best = candidate; bestCost = cost; }
            return cost;
        }
        const double gr = 0.618033988749895;
        var c = b - gr * (b - a);
        var d = a + gr * (b - a);
        var fc = Evaluate(c);
        var fd = Evaluate(d);
        for (var i = 0; i < 7; i++)
        {
            if (fc < fd) { b = d; d = c; fd = fc; c = b - gr * (b - a); fc = Evaluate(c); }
            else { a = c; c = d; fc = fd; d = a + gr * (b - a); fd = Evaluate(d); }
        }
        return best;
    }

    static CalibModel FitStage(List<Row> set, string tag, int rounds)
    {
        var m = _init.Clone();
        // 單樣本步進凍結於初始值，避免單點過擬合。
        var conSteps = Enumerable.Range(0, 7)
            .Where(s => s != 3 && set.Count(r => r.Recipe.Contrast == s - 3) >= 2).ToList();
        var satSteps = Enumerable.Range(0, 7)
            .Where(s => s != 3 && set.Count(r => r.Recipe.Saturation == s - 3) >= 2).ToList();
        Console.WriteLine($"[{tag}] conSteps=[{string.Join(",", conSteps.Select(s => s - 3))}] " +
            $"satSteps=[{string.Join(",", satSteps.Select(s => s - 3))}]");
        var prev = CostOf(m, set);
        Console.WriteLine($"[{tag}] start cost={prev:F4}");
        for (var round = 0; round < rounds; round++)
        {
            var shrink = Math.Pow(0.6, round);
            foreach (var s in conSteps)
            {
                var i = s;
                m = LineSearch(m, set.Where(r => r.Recipe.Contrast == i - 3).ToList(),
                    x => x.Contrast[i], (x, v) => x.Contrast[i] = v,
                    i == 0 ? 0.3 : m.Contrast[i - 1], i == 6 ? 2.0 : m.Contrast[i + 1], 0.12 * shrink);
            }
            foreach (var s in satSteps)
            {
                var i = s;
                m = LineSearch(m, set.Where(r => r.Recipe.Saturation == i - 3).ToList(),
                    x => x.Saturation[i], (x, v) => x.Saturation[i] = v,
                    i == 0 ? 0.0 : m.Saturation[i - 1], i == 6 ? 2.2 : m.Saturation[i + 1], 0.12 * shrink);
            }
            m = LineSearch(m, set, x => x.AbMired, (x, v) => x.AbMired = v, 0.0, 10.0, 1.0 * shrink);
            m = LineSearch(m, set, x => x.GmCc, (x, v) => x.GmCc = v, 0.0, 6.0, 0.6 * shrink);
            foreach (var style in CalibModel.KShiftStyles)
            {
                var group = set.Where(r => r.Recipe.Style == style).ToList();
                if (group.Count == 0) continue;
                m = LineSearch(m, group, x => x.KShift[style][0], (x, v) => x.KShift[style][0] = v,
                    0.5, 2.0, 0.15 * shrink);
                m = LineSearch(m, group, x => x.KShift[style][1], (x, v) => x.KShift[style][1] = v,
                    -0.5, 0.5, 0.10 * shrink);
                if (CalibModel.MatrixStyles.Contains(style))
                {
                    // 對角元（增益）先行，非對角元（串擾）隨後；每次寫入後列歸一。
                    for (var k = 0; k < 9; k += 4)
                    {
                        var j = k;
                        m = LineSearch(m, group,
                            x => x.Matrix[style][j],
                            (x, v) => { x.Matrix[style][j] = v; ProjectRows(x, style); },
                            0.5, 1.6, 0.08 * shrink);
                    }
                    for (var k = 0; k < 9; k++)
                    {
                        if (k % 4 == 0) continue;
                        var j = k;
                        m = LineSearch(m, group,
                            x => x.Matrix[style][j],
                            (x, v) => { x.Matrix[style][j] = v; ProjectRows(x, style); },
                            -0.3, 0.3, 0.05 * shrink);
                    }
                }
            }
            var now = CostOf(m, set);
            Console.WriteLine($"[{tag}] round {round} cost={now:F4}");
            if ((prev - now) / Math.Max(prev, 1e-9) < 1e-3) break;
            prev = now;
        }
        // An underconstrained endpoint must not keep an arbitrary near-gray initial value
        // after its two neighboring steps have been fitted. Use their slope, not held-out photos.
        if (!satSteps.Contains(0) && satSteps.Contains(1) && satSteps.Contains(2))
        {
            m.Saturation[0] = Math.Clamp(2 * m.Saturation[1] - m.Saturation[2], 0, m.Saturation[1]);
            _extrapolatedMinimumSaturation = true;
            Console.WriteLine($"[{tag}] underconstrained saturation -3 extrapolated to {m.Saturation[0]:F6}");
        }
        return m;
    }

    static void EvaluateFinal(CalibModel m)
    {
        _activeTable = TableFor(m);
        foreach (var row in Rows)
            FinalDE[row.Frame] = MeanDE(Render(row.Frame), Targets[row.Frame]);
        var fit = Rows.Where(r => r.Cls is "Fit" or "Held").ToList();
        Console.WriteLine($"final fit-set {fit.Average(r => BaseDE[r.Frame]):F3} -> {fit.Average(r => FinalDE[r.Frame]):F3}");
    }

    static void EmitResults()
    {
        using var w = new StreamWriter(Path.Combine(_out, "per-sample.csv"));
        w.WriteLine("frame,key,class,style,sat,con,wb,ab,gm,ev,dro,effect,baseline_DE00,fitted_DE00");
        foreach (var r in Rows)
        {
            var p = r.Recipe;
            w.WriteLine($"{r.Frame},{p.Key},{r.Cls},{p.Style},{p.Saturation},{p.Contrast}," +
                $"{p.WhiteBalance},{p.AmberBlue},{p.GreenMagenta},{p.Exposure},{p.Dro},{p.Effect}," +
                $"{BaseDE[r.Frame]:F3},{FinalDE[r.Frame]:F3}");
        }
    }

    static string F6(double v) => Math.Round(v, 6).ToString("0.######",
        System.Globalization.CultureInfo.InvariantCulture);

    static void EmitCreativeStyles(string srcJson, CalibModel m,
        double trainBefore, double trainAfter, double heldBefore, double heldAfter)
    {
        using var doc = JsonDocument.Parse(srcJson,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // Sony 成像模擬參數（FilmBatch 自有實作，計畫書 7.3）。與配方數值分開存放：");
        sb.AppendLine("  // 配方數值永遠與 Recipe Lab 一致，只有這裡的模擬參數會隨校正更新。");
        sb.AppendLine("  // calibrated:true —— 7.6 樣張擬合已執行（見 docs/film-calibration.md；單一室內場景近似，非相機重建）。");
        sb.AppendLine("  \"calibrated\": true,");
        sb.AppendLine("  \"calibration\": {");
        sb.AppendLine("    \"status\": \"calibrated-scene-approximation\",");
        sb.AppendLine($"    \"date\": \"{DateTime.UtcNow:yyyy-MM-dd}\",");
        sb.AppendLine("    \"samplesCommit\": \"fd97eec77caf56bb2740a89b503e1037fdb24877\",");
        sb.AppendLine("    \"method\": \"coordinate-descent on mean-DeltaE00 vs sample frames, input=frame01, actual CpuPipeline\",");
        sb.AppendLine($"    \"trainMeanDE\": {F6(trainAfter)},");
        sb.AppendLine($"    \"trainBaselineMeanDE\": {F6(trainBefore)},");
        sb.AppendLine($"    \"heldoutMeanDE\": {F6(heldAfter)},");
        sb.AppendLine($"    \"heldoutBaselineMeanDE\": {F6(heldBefore)},");
        sb.AppendLine($"    \"heldoutCount\": {Rows.Count(r => r.Cls == "Held")},");
        if (_extrapolatedMinimumSaturation)
            sb.AppendLine("    \"satMinus3Guard\": \"Unfitted -3 extrapolated from fitted -2/-1 slope, clamped to [0,-2]; no held-out photos used\",");
        sb.AppendLine("    \"excluded\": \"9 overrange + 5 PP3-matrix (union 10) fit-excluded; 8 kelvin direction-check only; 5 supported-effect measured-only\"");
        sb.AppendLine("  },");
        sb.AppendLine("  // 每種創意風格的基底：logit 空間色調曲線 y = sigmoid(k * logit(x) - shift)（0→0、1→1），");
        sb.AppendLine("  // 擬合矩陣以列歸一維持中性灰，未覆蓋風格保留初值；黑白權重 / sepia 染色。");
        sb.AppendLine("  // Standard 與零檔係數保持恆等；僅 train 子集參與擬合。");
        sb.AppendLine("  \"styles\": {");
        for (var si = 0; si < CalibModel.StyleOrder.Length; si++)
        {
            var name = CalibModel.StyleOrder[si];
            var el = root.GetProperty("styles").GetProperty(name);
            var ks = m.KShift[name];
            var mx = m.Matrix[name];
            var mat = $"{F6(mx[0])},{F6(mx[1])},{F6(mx[2])}, " +
                      $"{F6(mx[3])},{F6(mx[4])},{F6(mx[5])}, " +
                      $"{F6(mx[6])},{F6(mx[7])},{F6(mx[8])}";
            var extra = "";
            if (el.TryGetProperty("monoWeights", out var mw))
                extra = ", \"monoWeights\": [" + string.Join(",", mw.EnumerateArray().Select(e => F6(e.GetDouble()))) + "]";
            if (el.TryGetProperty("sepia", out var sp) && sp.GetBoolean())
                extra += ", \"sepia\": true";
            var pad = name.PadRight(11);
            var comma = si < CalibModel.StyleOrder.Length - 1 ? "," : "";
            sb.Append("    \"" + name + "\":" + new string(' ', 12 - name.Length) +
                "{ \"k\": " + F6(ks[0]) + ", \"shift\": " + F6(ks[1]) + ", \"matrix\": [" + mat + "]" + extra + " }" + comma);
            sb.AppendLine();
        }
        sb.AppendLine("  },");
        sb.AppendLine("  // 對比 / 飽和逐格係數：零檔固定為 1，保持單調，單樣本檔位不擬合。");
        if (_extrapolatedMinimumSaturation)
            sb.AppendLine("  // 飽和 -3 樣本不足時，延續已擬合 -2/-1 的斜率；不沿用任意近灰初值。");
        sb.Append("  \"contrastPerStep\": { ");
        sb.Append(string.Join(", ", Enumerable.Range(0, 7).Select(i => $"\"{i - 3}\": {F6(m.Contrast[i])}")));
        sb.AppendLine(" },");
        sb.Append("  \"saturationPerStep\": { ");
        sb.Append(string.Join(", ", Enumerable.Range(0, 7).Select(i => $"\"{i - 3}\": {F6(m.Saturation[i])}")));
        sb.AppendLine(" },");
        sb.AppendLine("  // 白平衡微調：每格 mired / CC 偏移，Kelvin 樣張不參與擬合。");
        sb.AppendLine($"  \"amberBlueMiredPerStep\": {F6(m.AbMired)},");
        sb.AppendLine($"  \"greenMagentaCcPerStep\": {F6(m.GmCc)},");
        sb.AppendLine("  // 第一版實作的照片效果（計畫書 7.3）；其餘讀入但以 off 算圖並提示。");
        sb.AppendLine($"  \"supportedEffects\": {root.GetProperty("supportedEffects").GetRawText()},");
        sb.AppendLine("  \"effects\": {");
        var names = root.GetProperty("effects").EnumerateObject().Select(p => p.Name).ToList();
        for (var ei = 0; ei < names.Count; ei++)
        {
            var v = root.GetProperty("effects").GetProperty(names[ei]);
            sb.Append($"    \"{names[ei]}\": {v.GetRawText()}{(ei < names.Count - 1 ? "," : "")}");
            sb.AppendLine();
        }
        sb.AppendLine("  },");
        sb.AppendLine("  // DRO 各級相對於 auto 的暗部提升 / 亮部壓縮（初始值，待校正）。");
        sb.AppendLine("  \"dro\": {");
        var first = true;
        foreach (var p in root.GetProperty("dro").EnumerateObject())
        {
            if (!first) sb.AppendLine(",");
            first = false;
            sb.Append($"    \"{p.Name}\": {p.Value.GetRawText()}");
        }
        sb.AppendLine();
        sb.AppendLine("  }");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(_out, "creative_styles.fitted.json"), sb.ToString());
    }
}
