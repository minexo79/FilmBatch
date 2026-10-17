using System.Text;

namespace FilmBatch.Core.Film;

/// <summary>
/// Recipe Lab 自訂配方格式 1 的讀寫（計畫書 7.5）。
/// 規則照搬上游 <c>RecipeFormats</c> / <c>RecipeFormatV1</c> / <c>CustomRecipes</c>：
/// 扁平 YAML（每行 <c>key: value</c>、<c>#</c> 註解、UTF-8、忽略 BOM）；不認得的 key 忽略；
/// 任何一個值超出範圍就略過整個檔案並說明原因（不截斷）；<c>format</c> 比支援的新就整檔略過；
/// 名稱重複（不分大小寫）略過；超過 8KB 或隱藏檔忽略；副檔名 <c>.yml</c>（大小寫不分）。
/// 上游相機端另接受 <c>.txt</c>；FilmBatch 讀寫一律 <c>.YML</c>（見計畫書 7.5 流程圖與 8.3 檔名）。
/// </summary>
public static class CustomRecipeFile
{
    public const int FormatVersion = 1;
    public const int MaxBytes = 8192;
    public const int NameMaxLength = 24;
    /// <summary>飽和 / 對比 / 銳利度的上下限（Sony 四代機選單為 ±9，見 <see cref="CreativeStyleTable.MaxSteps"/>）。</summary>
    public const int AdjustmentLimit = CreativeStyleTable.MaxSteps;
    /// <summary>名稱允許的字元：上游 <c>CustomRecipes.NAME_PUNCT</c>。</summary>
    public const string NamePunct = " -.'()&+";

    public static readonly IReadOnlyList<string> KnownStyles =
        ["standard", "vivid", "neutral", "portrait", "landscape", "mono", "clear", "deep", "light", "sunset", "night", "red-leaves", "sepia"];

    public static readonly IReadOnlyList<string> KnownEffects =
        ["off", "toy-camera", "pop-color", "posterization", "retro-photo", "soft-high-key", "part-color", "rough-mono", "soft-focus", "hdr-art", "richtone-mono", "miniature", "illust", "watercolor"];

    /// <summary>各效果的子參數（上游 <c>Recipes.subValues</c>）。</summary>
    public static IReadOnlyList<string>? EffectOptions(string effect) => effect switch
    {
        "soft-high-key" => ["blue", "pink", "green"],
        "toy-camera" => ["normal", "cool", "warm", "green", "magenta"],
        "part-color" => ["red", "green", "blue", "yellow"],
        "posterization" => ["posterization-color", "posterization-bw"],
        _ => null,
    };

    /// <summary>名稱是否合法（1–24 字元，見 <see cref="NamePunct"/>）。</summary>
    public static bool IsValidName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > NameMaxLength) return false;
        return name.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || NamePunct.Contains(c));
    }

    /// <summary>匯出檔名：名稱前 8 個英數字轉大寫；重複時尾部加數字（上游 <c>CustomRecipes.fileName</c>）。</summary>
    public static string FileNameFor(string name, IEnumerable<string> takenFiles)
    {
        var stem = new string(name.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());
        if (stem.Length == 0) stem = "RECIPE";
        var base8 = stem[..Math.Min(8, stem.Length)];
        var taken = new HashSet<string>(takenFiles, StringComparer.OrdinalIgnoreCase);
        var file = base8 + ".YML";
        for (var n = 2; taken.Contains(file); n++)
        {
            var num = n.ToString();
            file = base8[..Math.Min(base8.Length, 8 - num.Length)] + num + ".YML";
        }
        return file;
    }

    /// <summary>讀取單一檔案文字；<paramref name="fileName"/> 供無 name 行時取名。</summary>
    public static CustomRecipeParseResult Parse(string text, string fileName)
    {
        Dictionary<string, string> kv;
        try
        {
            kv = SplitEnvelope(text);
        }
        catch (InvalidDataException ex)
        {
            return CustomRecipeParseResult.Fail(ex.Message);
        }

        if (kv.TryGetValue("format", out var formatText))
        {
            if (!formatText.All(char.IsAsciiDigit) || formatText.Length is 0 or > 4)
                return CustomRecipeParseResult.Fail(Bad("format", formatText));
            var version = int.Parse(formatText);
            if (version != FormatVersion)
                return CustomRecipeParseResult.Fail(version > FormatVersion
                    ? CoreText.Format("Recipe.Error.NewerFormat", version)
                    : Bad("format", formatText));
        }

        var name = kv.GetValueOrDefault("name", "").Trim();
        if (name.Length == 0) name = Stem(fileName).Trim();
        if (!IsValidName(name)) return CustomRecipeParseResult.Fail(Bad("name", name));

        var style = "standard";
        if (kv.TryGetValue("style", out var styleText) && styleText.Length > 0)
        {
            if (!KnownStyles.Contains(styleText, StringComparer.OrdinalIgnoreCase))
                return CustomRecipeParseResult.Fail(Bad("style", styleText));
            style = KnownStyles.First(s => s.Equals(styleText, StringComparison.OrdinalIgnoreCase));
        }

        var adj = new Dictionary<string, int>();
        foreach (var key in (string[])(["saturation", "contrast", "sharpness"]))
        {
            adj[key] = 0;
            if (!kv.TryGetValue(key, out var adjText) || adjText.Length == 0) continue;
            if (!TrySigned(adjText, out var n) || n is < -AdjustmentLimit or > AdjustmentLimit)
                return CustomRecipeParseResult.Fail(Bad(key, adjText));
            adj[key] = n;
        }

        var effect = "off";
        if (kv.TryGetValue("effect", out var effectText) && effectText.Length > 0)
        {
            // 計畫書 7.2 以 richtone-mono 列出 HC Mono；上游 PE_KEYS 實為 rough-mono。讀入時相容，寫出統一為 rough-mono。
            if (effectText.Equals("richtone-mono", StringComparison.OrdinalIgnoreCase)) effectText = "rough-mono";
            if (!KnownEffects.Contains(effectText, StringComparer.OrdinalIgnoreCase))
                return CustomRecipeParseResult.Fail(Bad("effect", effectText));
            effect = KnownEffects.First(s => s.Equals(effectText, StringComparison.OrdinalIgnoreCase));
        }

        var effectOption = "";
        var options = EffectOptions(effect);
        if (options is not null)
        {
            effectOption = options[0];
            if (kv.TryGetValue("effect-option", out var optionText) && optionText.Length > 0)
            {
                if (!options.Contains(optionText, StringComparer.OrdinalIgnoreCase))
                    return CustomRecipeParseResult.Fail(Bad("effect-option", optionText));
                effectOption = options.First(s => s.Equals(optionText, StringComparison.OrdinalIgnoreCase));
            }
        }

        var whiteBalance = "auto";
        if (kv.TryGetValue("white-balance", out var wbText) && wbText.Length > 0)
        {
            var w = wbText.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
            if (w is "auto" or "keep") whiteBalance = w;
            else if (w.Length is 5 or 6 && w.EndsWith('k') && int.TryParse(w[..^1], out var kelvin)
                && kelvin is >= 2500 and <= 9900 && kelvin % 100 == 0)
                whiteBalance = kelvin + "K";
            else return CustomRecipeParseResult.Fail(Bad("white-balance", wbText));
        }

        var fine = new Dictionary<string, int>();
        foreach (var key in (string[])(["amber-blue", "green-magenta"]))
        {
            fine[key] = 0;
            if (!kv.TryGetValue(key, out var fineText) || fineText.Length == 0) continue;
            if (!TrySigned(fineText, out var n) || n is < -7 or > 7)
                return CustomRecipeParseResult.Fail(Bad(key, fineText));
            fine[key] = n;
        }

        var exposure = "0";
        if (kv.TryGetValue("exposure", out var evText) && evText.Length > 0)
        {
            var thirds = ParseExposure(evText);
            if (thirds is null || thirds is < -15 or > 15)
                return CustomRecipeParseResult.Fail(Bad("exposure", evText));
            exposure = FormatExposure(thirds.Value);
        }

        var dro = "auto";
        if (kv.TryGetValue("dro", out var droText) && droText.Length > 0)
        {
            var d = droText.ToLowerInvariant();
            if (d is "auto" or "off") dro = d;
            else if (d is "1" or "2" or "3" or "4" or "5") dro = d;
            else return CustomRecipeParseResult.Fail(Bad("dro", droText));
        }

        return CustomRecipeParseResult.Ok(new FilmRecipe(
            "custom:" + name, name, "", style, adj["saturation"], adj["contrast"], adj["sharpness"],
            whiteBalance, fine["amber-blue"], fine["green-magenta"], effect, effectOption, exposure, dro));
    }

    /// <summary>載入資料夾內全部自訂配方（依名稱排序；略過的檔案逐一說明原因）。</summary>
    public static CustomRecipeLoadResult LoadFolder(string folder)
    {
        var entries = new List<CustomRecipeEntry>();
        var skipped = new List<string>();
        if (!Directory.Exists(folder)) return new(entries, skipped);
        string[] files;
        try
        {
            files = Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(entries, [CoreText.Format("Recipe.Skip.CannotRead", folder, ex.Message)]);
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            var file = Path.GetFileName(path);
            if (file.StartsWith('.')) continue;
            if (!file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try
            {
                if (new FileInfo(path).Length > MaxBytes)
                {
                    skipped.Add(CoreText.Format("Recipe.Skip.TooLarge", file));
                    continue;
                }
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(CoreText.Format("Recipe.Skip.CannotRead", file, ex.Message));
                continue;
            }
            var parsed = Parse(text, file);
            if (!parsed.Success)
            {
                skipped.Add(CoreText.Format("Recipe.Skip.Reason", file, parsed.Error));
                continue;
            }
            var recipe = parsed.Recipe!;
            if (!names.Add(recipe.Name))
            {
                skipped.Add(CoreText.Format("Recipe.Skip.AlreadyLoaded", file, recipe.Name));
                continue;
            }
            entries.Add(new(recipe, file, path));
        }
        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Recipe.Name, b.Recipe.Name));
        return new(entries, skipped);
    }

    /// <summary>寫出格式 1 檔案（名稱一律加引號；FilmBatch 自有參數不寫入）。</summary>
    public static string Write(FilmRecipe recipe, string? madeOn = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Recipe Lab custom recipe (format 1).");
        sb.AppendLine("# Edit with care: a value outside the range beside it makes the app skip this file.");
        Line(sb, "format", FormatVersion.ToString(), null);
        Line(sb, "name", Quoted(recipe.Name), null);
        if (!string.IsNullOrWhiteSpace(madeOn)) Line(sb, "made-on", Quoted(madeOn.Trim()), "the computer it was saved on");
        Line(sb, "style", recipe.Style, string.Join(' ', KnownStyles));
        Line(sb, "saturation", Signed(recipe.Saturation), "-9 .. +9");
        Line(sb, "contrast", Signed(recipe.Contrast), "-9 .. +9");
        Line(sb, "sharpness", Signed(recipe.Sharpness), "-9 .. +9");
        Line(sb, "effect", recipe.Effect, string.Join(' ', KnownEffects));
        if (EffectOptions(recipe.Effect) is not null)
            Line(sb, "effect-option", recipe.EffectOption, string.Join(' ', EffectOptions(recipe.Effect)!));
        Line(sb, "white-balance", recipe.WhiteBalance, "auto, keep, or 2500K .. 9900K in 100K steps");
        Line(sb, "amber-blue", Signed(recipe.AmberBlue), "-7 (blue) .. +7 (amber)");
        Line(sb, "green-magenta", Signed(recipe.GreenMagenta), "-7 (magenta) .. +7 (green)");
        Line(sb, "exposure", recipe.Exposure, "-5.0 .. +5.0 in thirds: .0 .3 .7");
        Line(sb, "dro", recipe.Dro, "off, auto, 1 .. 5");
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string key, string value, string? comment)
    {
        var at = sb.Length;
        sb.Append(key).Append(": ").Append(value);
        if (comment is not null)
        {
            while (sb.Length - at < 28) sb.Append(' ');
            sb.Append(" # ").Append(comment);
        }
        sb.Append('\n');
    }

    private static string Quoted(string value) =>
        '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static string Signed(int value) => value > 0 ? "+" + value : value.ToString();

    private static string Bad(string key, object value) => CoreText.Format("Recipe.Error.NotAllowed", key, value);

    private static string Stem(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length == 0 ? fileName : stem;
    }

    /// <summary>扁平 YAML 信封：每行 <c>key: value</c>；其他寫法整檔略過並回報行號。</summary>
    private static Dictionary<string, string> SplitEnvelope(string text)
    {
        if (text.StartsWith("﻿", StringComparison.Ordinal)) text = text[1..];
        var kv = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            var raw = lines[n];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed is "---" or "...") continue;
            var colon = IndexOfEntryColon(raw);
            if (colon < 0) throw new InvalidDataException(CoreText.Format("Recipe.Error.NotKeyValue", n + 1));
            var key = raw[..colon].Trim();
            if (!IsKey(key)) throw new InvalidDataException(CoreText.Format("Recipe.Error.NotKeyValue", n + 1));
            var scalar = Scalar(raw[(colon + 1)..]);
            if (scalar is null) throw new InvalidDataException(CoreText.Format("Recipe.Error.NotKeyValue", n + 1));
            key = key.ToLowerInvariant();
            if (!kv.TryAdd(key, scalar)) throw new InvalidDataException(CoreText.Format("Recipe.Error.SetTwice", key));
        }
        return kv;
    }

    private static int IndexOfEntryColon(string raw)
    {
        // 上游 ENTRY 正則：key 後的冒號必須接「空白 + 值」或行尾；"2500K" 這類值內的冒號不存在，簡化為第一個後接空白/行尾的冒號。
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != ':') continue;
            if (i + 1 == raw.Length || raw[i + 1] is ' ' or '\t') return i;
        }
        return -1;
    }

    private static bool IsKey(string key) =>
        key.Length > 0 && char.IsAsciiLetter(key[0]) && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>單行純量：單引號、雙引號或 plain（<c> #</c> 起為註解）。格式錯誤回傳 null。</summary>
    private static string? Scalar(string value)
    {
        value = value.Trim();
        if (value.StartsWith('\'') || value.StartsWith('"'))
        {
            var quote = value[0];
            var end = value.LastIndexOf(quote);
            if (end <= 0) return null;
            var rest = value[(end + 1)..].Trim();
            if (rest.Length > 0 && !rest.StartsWith('#')) return null;
            var inner = value[1..end];
            return quote == '\''
                ? inner.Replace("''", "'", StringComparison.Ordinal)
                : inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? value[..hash] : value).Trim();
    }

    private static bool TrySigned(string value, out int result)
    {
        result = 0;
        var digits = value.StartsWith('+') ? value[1..] : value;
        return digits.Length is >= 1 and <= 3 && int.TryParse(digits, out result) && digits.TrimStart('-').All(char.IsAsciiDigit);
    }

    /// <summary>曝光顯示（+0.7）→ 1/3 EV 格數；非「整數 .0/.3/.7」回傳 null。</summary>
    public static int? ParseExposure(string value)
    {
        var v = value.Trim();
        var negative = v.StartsWith('-');
        var unsigned = v.TrimStart('+').TrimStart('-');
        var dot = unsigned.IndexOf('.');
        string wholeText, fracChar;
        if (dot < 0)
        {
            wholeText = unsigned;
            fracChar = "0";
        }
        else
        {
            wholeText = unsigned[..dot];
            fracChar = unsigned.Length == dot + 2 ? unsigned[dot + 1].ToString() : "";
        }
        if (wholeText.Length is 0 or > 2 || !wholeText.All(char.IsAsciiDigit)) return null;
        var frac = fracChar switch { "0" => 0, "3" => 1, "7" => 2, _ => -1 };
        if (frac < 0) return null;
        var thirds = int.Parse(wholeText) * 3 + frac;
        return negative ? -thirds : thirds;
    }

    /// <summary>1/3 EV 格數 → 曝光顯示（上游 <c>Recipes.evLabel</c>）。</summary>
    public static string FormatExposure(int thirds)
    {
        if (thirds == 0) return "0";
        var abs = Math.Abs(thirds);
        var frac = abs % 3 == 0 ? ".0" : abs % 3 == 1 ? ".3" : ".7";
        return (thirds > 0 ? "+" : "-") + abs / 3 + frac;
    }
}
