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
                    var definition = ModuleCatalog.Param(flatKey) ?? throw new InvalidDataException($"Unknown parameter: {flatKey}");
                    var text = scalar.Value ?? "";
                    var value = definition.Kind switch
                    {
                        ParamKind.Bool when bool.TryParse(text, out var boolean) => ParamValue.Of(boolean),
                        ParamKind.Choice or ParamKind.Path => ParamValue.Of(
                            scalar.Style == ScalarStyle.Plain && text is "null" or "~" ? "" : text),
                        ParamKind.Float or ParamKind.Int or ParamKind.Seed when double.TryParse(text, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var number) => ParamValue.Of(number),
                        _ => throw new InvalidDataException($"Invalid value: {flatKey}"),
                    };
                    SettingsJsonConverter.Validate(flatKey, value);
                    values.Add(flatKey, value);
                }
            }
        }
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
                SettingsJsonConverter.Validate(key, value);
                parameters.Add(key[(key.IndexOf('.') + 1)..], new YamlScalarNode(value.ToString())
                { Style = value.Kind == ValueKind.Text ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain });
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
