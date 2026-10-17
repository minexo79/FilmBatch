using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilmBatch.Core.Params;

/// <summary>Persist scalar values without reflection or losing boolean/string types.</summary>
public sealed class SettingsJsonConverter : JsonConverter<Settings>
{
    public override Settings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Settings must be an object.");
        var values = new Dictionary<string, ParamValue>(StringComparer.Ordinal);
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
            if (!values.TryAdd(key, value)) throw new JsonException($"Duplicate parameter: {key}");
            Validate(key, value);
        }
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Incomplete settings.");
        return new Settings(values);
    }

    internal static void Validate(string key, ParamValue value)
    {
        var definition = ModuleCatalog.Param(key);
        if (definition is null) throw new InvalidDataException($"Unknown parameter: {key}");
        var expected = definition.Kind switch
        {
            ParamKind.Bool => ValueKind.Bool,
            ParamKind.Choice or ParamKind.Path => ValueKind.Text,
            _ => ValueKind.Number,
        };
        if (value.Kind != expected) throw new InvalidDataException($"Invalid value type: {key}");
        if (value.Kind == ValueKind.Number && (!double.IsFinite(value.Number)
            || value.Number < definition.Min || value.Number > definition.Max
            || (definition.Kind is ParamKind.Int or ParamKind.Seed && value.Number != Math.Truncate(value.Number))))
            throw new InvalidDataException($"Parameter out of range: {key}");
        // Asset choices can be supplied by the user's library, not only the static catalog.
        if (definition.Kind == ParamKind.Choice && key is not "light_leak.texture"
            && !definition.Choices.Any(choice => choice.Key == value.AsText))
            throw new InvalidDataException($"Unknown option for {key}: {value.AsText}");
    }

    public override void Write(Utf8JsonWriter writer, Settings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, parameter) in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Validate(key, parameter);
            switch (parameter.Kind)
            {
                case ValueKind.Bool: writer.WriteBoolean(key, parameter.AsBool); break;
                case ValueKind.Text: writer.WriteString(key, parameter.AsText); break;
                default: writer.WriteNumber(key, parameter.Number); break;
            }
        }
        writer.WriteEndObject();
    }
}
