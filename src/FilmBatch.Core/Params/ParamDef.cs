using FilmBatch.Core.Curves;

using FilmBatch.Core.Film;

namespace FilmBatch.Core.Params;

/// <summary>參數型別。<see cref="Curve"/> 的值是正規化的文字（見 <see cref="ToneCurve.Serialize"/>）。</summary>
public enum ParamKind { Float, Int, Bool, Choice, Path, Seed, Curve, Text }

/// <summary>位置類參數的座標軸，用於「貼上時鏡像位置」。</summary>
public enum PositionAxis { None, X, Y }

public sealed record ChoiceOption(string Key, string Label);

/// <summary>單一參數的 schema。<see cref="Key"/> 為模組內的區域 key（例如 <c>amount</c>）。</summary>
public sealed record ParamDef
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required ParamKind Kind { get; init; }
    public required ParamValue Default { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    public string Unit { get; init; } = "";
    public bool IsSeed => Kind == ParamKind.Seed;
    public PositionAxis Axis { get; init; }
    public bool IsPosition => Axis != PositionAxis.None;
    /// <summary>位置以 0 為中心（例如位移 −0.5 ~ 0.5），鏡像時取負值；否則以 1 − x 鏡像。</summary>
    public bool IsCenteredOffset { get; init; }
    public IReadOnlyList<ChoiceOption> Choices { get; init; } = [];

    public static ParamDef Bool(string key, string label, bool def = false) =>
        new() { Key = key, Label = label, Kind = ParamKind.Bool, Default = def, Max = 1 };

    public static ParamDef Float(string key, string label, double def, double min, double max, double step = 1, string unit = "") =>
        new() { Key = key, Label = label, Kind = ParamKind.Float, Default = def, Min = min, Max = max, Step = step, Unit = unit };

    public static ParamDef Int(string key, string label, int def, int min, int max) =>
        new() { Key = key, Label = label, Kind = ParamKind.Int, Default = def, Min = min, Max = max, Step = 1 };

    public static ParamDef Position(string key, string label, double def, PositionAxis axis) =>
        new() { Key = key, Label = label, Kind = ParamKind.Float, Default = def, Min = 0, Max = 1, Step = 0.01, Axis = axis };

    public static ParamDef Offset(string key, string label, PositionAxis axis) =>
        new() { Key = key, Label = label, Kind = ParamKind.Float, Default = 0.0, Min = -0.5, Max = 0.5, Step = 0.01, Axis = axis, IsCenteredOffset = true };

    /// <summary>以 0 為中心的角度（例如拉直）：水平鏡像時取負值。</summary>
    public static ParamDef MirrorAngle(string key, string label, double def, double min, double max, double step = 0.1) =>
        new() { Key = key, Label = label, Kind = ParamKind.Float, Default = def, Min = min, Max = max, Step = step,
                Unit = "°", Axis = PositionAxis.X, IsCenteredOffset = true };

    public static ParamDef Choice(string key, string label, params ChoiceOption[] options) =>
        new() { Key = key, Label = label, Kind = ParamKind.Choice, Default = options[0].Key, Choices = options };

    /// <summary>自由文字（例如語意遮罩名稱）：任意值原樣保留，不做選項或路徑正規化。</summary>
    public static ParamDef Text(string key, string label, string def = "") =>
        new() { Key = key, Label = label, Kind = ParamKind.Text, Default = def };

    public static ParamDef Path(string key, string label) =>
        new() { Key = key, Label = label, Kind = ParamKind.Path, Default = "" };

    public static ParamDef Seed(string key = "seed", string label = "種子") =>
        new() { Key = key, Label = label, Kind = ParamKind.Seed, Default = 0.0, Min = 0, Max = 9999 };

    /// <summary>色調曲線（文字值 "x,y;x,y"，預設恆等）。</summary>
    public static ParamDef Curve(string key, string label) =>
        new() { Key = key, Label = label, Kind = ParamKind.Curve, Default = ToneCurve.IdentityText };

    /// <summary>把值限制在合法範圍。</summary>
    public ParamValue Coerce(ParamValue v) => Kind switch
    {
        ParamKind.Bool => ParamValue.Of(v.AsBool),
        ParamKind.Choice when Key == "look" && UserLutLibrary.IsUserKey(v.AsText) => ParamValue.Of(v.AsText),
        ParamKind.Choice => Choices.Any(c => c.Key == v.AsText) ? ParamValue.Of(v.AsText) : Default,
        ParamKind.Path or ParamKind.Text => ParamValue.Of(v.AsText),
        ParamKind.Curve => ToneCurve.TryParse(v.AsText, out var curve) ? ParamValue.Of(curve.Serialize()) : Default,
        ParamKind.Int or ParamKind.Seed => ParamValue.Of(Math.Round(Math.Clamp(v.AsNumber, Min, Max))),
        _ => ParamValue.Of(Math.Clamp(v.AsNumber, Min, Max)),
    };
}
