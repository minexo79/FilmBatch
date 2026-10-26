using System.Globalization;
using FilmBatch.Core.Params;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace FilmBatch.Core.Presets;

/// <summary>Versioned, scalar-only YAML presets. No reflection-based deserialization.</summary>
public static class PresetFile
{
    public static Preset Read(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Preset exceeds 1 MiB.");
        using var input = File.OpenText(path);
        var yaml = new YamlStream();
        yaml.Load(input);
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("Expected one preset mapping.");
        if (!Guid.TryParse(Scalar(root, "id"), out var id) || id == Guid.Empty)
            throw new InvalidDataException("Invalid preset id.");
        var name = Scalar(root, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Preset name is required.");
        if (!int.TryParse(Scalar(root, "pipeline_version", "1"), CultureInfo.InvariantCulture, out var version) || version != 1)
            throw new InvalidDataException("Unsupported preset pipeline version.");
        var values = new Dictionary<string, ParamValue>(StringComparer.Ordinal);
        // 舊檔的 film.hue_* 暫存（正規名稱 → 值）：正規 hsl.* key 一律優先，與來源順序無關。
        var legacyHue = new Dictionary<string, ParamValue>(StringComparer.Ordinal);
        if (root.Children.TryGetValue(new YamlScalarNode("modules"), out var modulesNode))
        {
            if (modulesNode is not YamlMappingNode modules) throw new InvalidDataException("Invalid modules mapping.");
            foreach (var (moduleNode, parametersNode) in modules.Children)
            {
                if (moduleNode is not YamlScalarNode { Value: { } module } || parametersNode is not YamlMappingNode parameters)
                    throw new InvalidDataException("Invalid module mapping.");
                foreach (var (keyNode, valueNode) in parameters.Children)
                {
                    if (keyNode is not YamlScalarNode { Value: { } key } || valueNode is not YamlScalarNode scalar)
                        throw new InvalidDataException("Preset parameters must be scalar values.");
                    var flatKey = module + "." + key;
                    if (RetiredParams.IsRetired(flatKey)) continue;
                    // 舊檔的 film.hue_* 遷移為 hsl.hue_*，值用正規定義驗證。
                    var canonical = HslMigration.CanonicalKey(flatKey);
                    var definition = ModuleCatalog.Param(canonical) ?? throw new InvalidDataException($"Unknown parameter: {flatKey}");
                    var text = scalar.Value ?? "";
                    var value = definition.Kind switch
                    {
                        ParamKind.Bool when bool.TryParse(text, out var boolean) => ParamValue.Of(boolean),
                        ParamKind.Choice or ParamKind.Path or ParamKind.Curve or ParamKind.Text => ParamValue.Of(
                            scalar.Style == ScalarStyle.Plain && text is "null" or "~" ? "" : text),
                        ParamKind.Float or ParamKind.Int or ParamKind.Seed when double.TryParse(text, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var number) => ParamValue.Of(number),
                        _ => throw new InvalidDataException($"Invalid value: {flatKey}"),
                    };
                    var normalized = SettingsJsonConverter.ValidateAndNormalize(canonical, value);
                    if (canonical == flatKey) values.Add(canonical, normalized);
                    else legacyHue.Add(canonical, normalized);
                }
            }
        }
        foreach (var (key, value) in legacyHue) values.TryAdd(key, value);
        HslMigration.InheritHslEnabled(values, legacyHue.Count > 0);
        var pinnedText = Scalar(root, "pinned", "false");
        if (!bool.TryParse(pinnedText, out var pinned)) throw new InvalidDataException("Invalid pinned flag.");
        var updatedText = Scalar(root, "updated_at", "");
        var updated = updatedText.Length == 0 ? new DateTimeOffset(File.GetLastWriteTimeUtc(path))
            : DateTimeOffset.Parse(updatedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new Preset
        {
            Id = id, Name = name, Category = Scalar(root, "category", "Uncategorized"),
            ColorLabel = Scalar(root, "color_label", "#7A7A7A"), PipelineVersion = version,
            Pinned = pinned, UpdatedAt = updated, Settings = new Settings(values),
        };
    }

    public static string Serialize(Preset preset)
    {
        if (preset.Id == Guid.Empty || string.IsNullOrWhiteSpace(preset.Name) || preset.PipelineVersion != 1)
            throw new InvalidDataException("Invalid preset identity or pipeline version.");
        var modules = new YamlMappingNode();
        foreach (var group in preset.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).GroupBy(pair => ModuleSchema.ModuleOf(pair.Key)))
        {
            var parameters = new YamlMappingNode();
            foreach (var (key, value) in group)
            {
                var normalized = SettingsJsonConverter.ValidateAndNormalize(key, value);
                parameters.Add(key[(key.IndexOf('.') + 1)..], new YamlScalarNode(normalized.ToString())
                { Style = normalized.Kind == ValueKind.Text ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain });
            }
            modules.Add(group.Key, parameters);
        }
        static YamlScalarNode Text(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };
        var root = new YamlMappingNode
        {
            { "id", Text(preset.Id.ToString()) }, { "name", Text(preset.Name) },
            { "category", Text(preset.Category) }, { "color_label", Text(preset.ColorLabel) },
            { "pipeline_version", "1" }, { "pinned", preset.Pinned ? "true" : "false" },
            { "updated_at", Text(preset.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)) }, { "modules", modules },
        };
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(output, assignAnchors: false);
        return output.ToString();
    }

    public static void Write(string path, Preset preset) => AppPaths.WriteAllTextAtomic(path, Serialize(preset));

    private static string Scalar(YamlMappingNode mapping, string key, string? fallback = null)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out var node))
            return fallback ?? throw new InvalidDataException($"Missing preset field: {key}");
        return node is YamlScalarNode scalar ? scalar.Value ?? "" : throw new InvalidDataException($"Invalid preset field: {key}");
    }
}
