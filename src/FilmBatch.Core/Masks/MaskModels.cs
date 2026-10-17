using System.Text.Json;
using System.Text.Json.Serialization;
using FilmBatch.Core.Curves;

namespace FilmBatch.Core.Masks;

/// <summary>形狀與圖層內前面結果的組合方式。</summary>
public enum MaskCombine { Add, Subtract, Intersect }

/// <summary>
/// 遮罩形狀。座標皆為定向後、裁切前影像的 0–1 正規化座標；長度（半徑、筆刷、羽化）為短邊比例。
/// JSON 以 <c>"t"</c> 區分型別（見 <see cref="MaskStackCodec"/>）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(LinearShape), "lin")]
[JsonDerivedType(typeof(RadialShape), "rad")]
[JsonDerivedType(typeof(PolygonShape), "poly")]
[JsonDerivedType(typeof(BrushShape), "brush")]
[JsonDerivedType(typeof(ColorShape), "color")]
public abstract record MaskShape
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MaskCombine Combine { get; init; } = MaskCombine.Add;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Invert { get; init; }
}

/// <summary>線性漸層：在 (X0,Y0) 為 1，到 (X1,Y1) 為 0，兩點之間 smoothstep。</summary>
public sealed record LinearShape(double X0, double Y0, double X1, double Y1) : MaskShape;

/// <summary>圓形 / 橢圓：半徑為短邊比例，Feather 0–1（1 = 從中心開始漸變）。</summary>
public sealed record RadialShape(double Cx, double Cy, double Rx, double Ry, double AngleDeg, double Feather) : MaskShape;

/// <summary>多邊形：3–64 個頂點（自動閉合），Feather 為短邊比例的羽化寬度（以邊界為中心）。</summary>
public sealed record PolygonShape(IReadOnlyList<MaskPoint> Points, double Feather) : MaskShape;

/// <summary>一筆筆觸。Size 為筆刷直徑（短邊比例）；Hardness 0–1；Flow 0–1；Erase 為擦除。</summary>
public sealed record BrushStroke(double Size, double Hardness, double Flow, bool Erase, IReadOnlyList<MaskPoint> Points);

/// <summary>筆刷：依序繪製 / 擦除的筆觸。</summary>
public sealed record BrushShape(IReadOnlyList<BrushStroke> Strokes) : MaskShape;

/// <summary>
/// 顏色範圍（作法參考 LightCraft 的 ColorRange）：1–5 個 OkLab 取樣色，像素與「最近的」取樣色比距離；
/// Refine 0–100 是唯一的滑桿，決定容許距離（見 MaskMath.ColorTolerance）。預設 50。
/// </summary>
public sealed record ColorShape(IReadOnlyList<OkLabColor> Samples, double Refine = 50) : MaskShape;

/// <summary>OkLab 色彩：L 0–1，A / B 約 −0.4–0.4。由局部調整 pass 輸入影像（線性 sRGB）轉換而來。</summary>
public readonly record struct OkLabColor(double L, double A, double B);

/// <summary>正規化座標點。JSON 寫成精簡的 <c>[x,y]</c>（筆刷可能有上萬個點）。</summary>
[JsonConverter(typeof(MaskPointJsonConverter))]
public readonly record struct MaskPoint(double X, double Y);

/// <summary>圖層的基本參數，數值範圍與 basic 模組相同（−100–100、曝光 −3–3 EV）。</summary>
public sealed record LocalAdjust(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double Exposure = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double Temperature = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double Tint = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double Brightness = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double Contrast = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double Highlights = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double Shadows = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double Saturation = 0)
{
    [JsonIgnore]
    public bool IsZero => this == new LocalAdjust();
}

/// <summary>一個遮罩圖層：形狀依序組合成透明度圖，再以 <see cref="Adjust"/> 與 <see cref="Curve"/> 做局部調整。</summary>
public sealed record MaskLayer(string Id, string Name, IReadOnlyList<MaskShape> Shapes)
{
    public bool Visible { get; init; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Invert { get; init; }

    public double Opacity { get; init; } = 1;

    public LocalAdjust Adjust { get; init; } = new();

    /// <summary><see cref="ToneCurve.Serialize"/> 的文字。</summary>
    public string Curve { get; init; } = ToneCurve.IdentityText;

    /// <summary>不影響影像（隱藏、不透明度 0、沒有形狀，或沒有任何調整）。</summary>
    [JsonIgnore]
    public bool IsNoOp => !Visible || Opacity <= 0 || Shapes.Count == 0 || (Adjust.IsZero && Curve == ToneCurve.IdentityText);
}

/// <summary>一張照片的所有遮罩圖層（<c>local.masks</c> 的內容）。</summary>
public sealed record MaskStack(IReadOnlyList<MaskLayer> Layers)
{
    public const int MaxLayers = 8, MaxShapesPerLayer = 8, MaxPolygonPoints = 64, MaxColorSamples = 5,
        MaxBrushPointsPerLayer = 20_000, MaxTextLength = 512 * 1024;

    public static MaskStack Empty { get; } = new([]);
}

/// <summary><see cref="MaskPoint"/> ↔ <c>[x,y]</c>。</summary>
public sealed class MaskPointJsonConverter : JsonConverter<MaskPoint>
{
    public override MaskPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected [x,y].");
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected x.");
        var x = reader.GetDouble();
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected y.");
        var y = reader.GetDouble();
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Expected ].");
        return new MaskPoint(x, y);
    }

    public override void Write(Utf8JsonWriter writer, MaskPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}
