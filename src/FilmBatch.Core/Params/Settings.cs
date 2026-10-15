using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace FilmBatch.Core.Params;

/// <summary>扁平 key（<c>模組.參數</c>）→ 值 的不可變字典，可安全地跨執行緒傳遞。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SettingsJsonConverter))]
public sealed class Settings : IReadOnlyDictionary<string, ParamValue>, IEquatable<Settings>
{
    private readonly ImmutableDictionary<string, ParamValue> _values;

    public static Settings Empty { get; } = new(ImmutableDictionary<string, ParamValue>.Empty.WithComparers(StringComparer.Ordinal));

    private Settings(ImmutableDictionary<string, ParamValue> values) => _values = values;

    public Settings(IEnumerable<KeyValuePair<string, ParamValue>> values)
        : this(ImmutableDictionary.CreateRange(StringComparer.Ordinal, values)) { }

    public ParamValue this[string key] => _values[key];
    public int Count => _values.Count;
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<ParamValue> Values => _values.Values;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out ParamValue value) => _values.TryGetValue(key, out value);

    public Settings With(string key, ParamValue value) => new(_values.SetItem(key, value));
    public Settings Without(string key) => new(_values.Remove(key));
    public Settings WithoutRange(IEnumerable<string> keys) => new(_values.RemoveRange(keys));

    /// <summary>以 <paramref name="over"/> 覆蓋本層（三層合併的一步）。</summary>
    public Settings Overlay(Settings over) => over.Count == 0 ? this : new(_values.SetItems(over._values));

    public Settings Where(Func<string, ParamValue, bool> predicate) =>
        new(_values.Where(kv => predicate(kv.Key, kv.Value)));

    public double Number(string key) => _values.TryGetValue(key, out var v) ? v.AsNumber : 0;
    public bool Bool(string key) => _values.TryGetValue(key, out var v) && v.AsBool;
    public string Text(string key) => _values.TryGetValue(key, out var v) ? v.AsText : "";

    public IEnumerator<KeyValuePair<string, ParamValue>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(Settings? other) =>
        other is not null && other.Count == Count && _values.All(kv => other._values.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override bool Equals(object? obj) => Equals(obj as Settings);
    public override int GetHashCode() => Count;
}
