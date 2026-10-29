using FilmBatch.Core.Film;
using System.Text.Json;
using System.Text.Json.Serialization;
using FilmBatch.Core.Curves;

namespace FilmBatch.Core.Params;

/// <summary>Persist scalar values without reflection or losing boolean/string types.</summary>
public sealed class SettingsJsonConverter : JsonConverter<Settings>
{
    public override Settings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Settings must be an object.");
        var values = new Dictionary<string, ParamValue>(StringComparer.Ordinal);
        // 舊檔的 film.hue_* 暫存（正規名稱 → 值）：正規 hsl.* key 一律優先，與來源順序無關。
        var legacyHue = new Dictionary<string, ParamValue>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a parameter key.");
            var key = reader.GetString()!;
            if (!reader.Read()) throw new JsonException("Missing parameter value.");
            var value = reader.TokenType switch
            {
                JsonTokenType.True => ParamValue.Of(true),
                JsonTokenType.False => ParamValue.Of(false),
                JsonTokenType.Number when reader.TryGetDouble(out var number) && double.IsFinite(number) => ParamValue.Of(number),
                JsonTokenType.String => ParamValue.Of(reader.GetString()!),
                _ => throw new JsonException($"Invalid scalar parameter: {key}"),
            };
            if (RetiredParams.IsRetired(key)) continue;
            var canonical = HslMigration.CanonicalKey(key);
            var target = canonical == key ? values : legacyHue;
            if (!target.TryAdd(canonical, ValidateAndNormalize(canonical, value)))
                throw new JsonException($"Duplicate parameter: {key}");
        }
        foreach (var (key, value) in legacyHue) values.TryAdd(key, value);
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Incomplete settings.");
        HslMigration.InheritHslEnabled(values, legacyHue.Count > 0);
        return new Settings(values);
    }

    internal static ParamValue ValidateAndNormalize(string key, ParamValue value)
    {
        var definition = ModuleCatalog.Param(key);
        if (definition is null) throw new InvalidDataException($"Unknown parameter: {key}");
        var expected = definition.Kind switch
        {
            ParamKind.Bool => ValueKind.Bool,
            ParamKind.Choice or ParamKind.Path or ParamKind.Curve or ParamKind.Text => ValueKind.Text,
            _ => ValueKind.Number,
        };
        if (value.Kind != expected) throw new InvalidDataException($"Invalid value type: {key}");
        // Older projects and presets allowed 1000–50000 K. Only custom WB settings
        // are bounded here; the source's as-shot temperature is not a setting.
        if (key == "raw.temperature" && double.IsFinite(value.Number)) value = definition.Coerce(value);
        if (value.Kind == ValueKind.Number && (!double.IsFinite(value.Number)
            || value.Number < definition.Min || value.Number > definition.Max
            || (definition.Kind is ParamKind.Int or ParamKind.Seed && value.Number != Math.Truncate(value.Number))))
            throw new InvalidDataException($"Parameter out of range: {key}");
        if (definition.Kind == ParamKind.Curve && !ToneCurve.TryParse(value.AsText, out _))
            throw new InvalidDataException($"Invalid curve: {key}");
        // Asset choices can be supplied by the user's library, not only the static catalog.
        if (definition.Kind == ParamKind.Choice && key is not "light_leak.texture"
            && !(key == "film.look" && (UserLutLibrary.IsUserKey(value.AsText) || UserTransformLibrary.IsLookKey(value.AsText)))
            && !(key == "raw.camera_profile" && CameraProfileKey.IsUserKey(value.AsText))
            && !definition.Choices.Any(choice => choice.Key == value.AsText))
            throw new InvalidDataException($"Unknown option for {key}: {value.AsText}");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, Settings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, parameter) in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var normalized = ValidateAndNormalize(key, parameter);
            switch (normalized.Kind)
            {
                case ValueKind.Bool: writer.WriteBoolean(key, normalized.AsBool); break;
                case ValueKind.Text: writer.WriteString(key, normalized.AsText); break;
                default: writer.WriteNumber(key, normalized.Number); break;
            }
        }
        writer.WriteEndObject();
    }
}
