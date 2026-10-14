using System.Globalization;

namespace FilmBatch.Core.Params;

public enum ValueKind : byte { Number, Bool, Text }

/// <summary>參數值：數值、布林、字串（選項 key、相對路徑）三種之一。不可變、不裝箱。</summary>
public readonly record struct ParamValue
{
    public ValueKind Kind { get; }
    public double Number { get; }
    public string? Text { get; }

    private ParamValue(ValueKind kind, double number, string? text)
    {
        Kind = kind;
        Number = number;
        Text = text;
    }

    public static ParamValue Of(double v) => new(ValueKind.Number, v, null);
    public static ParamValue Of(bool v) => new(ValueKind.Bool, v ? 1 : 0, null);
    public static ParamValue Of(string v) => new(ValueKind.Text, 0, v);

    public static implicit operator ParamValue(double v) => Of(v);
    public static implicit operator ParamValue(bool v) => Of(v);
    public static implicit operator ParamValue(string v) => Of(v);

    public bool AsBool => Kind == ValueKind.Bool ? Number != 0 : Number != 0;
    public double AsNumber => Number;
    public string AsText => Text ?? string.Empty;

    public override string ToString() => Kind switch
    {
        ValueKind.Bool => AsBool ? "true" : "false",
        ValueKind.Text => AsText,
        _ => Number.ToString(CultureInfo.InvariantCulture),
    };
}
