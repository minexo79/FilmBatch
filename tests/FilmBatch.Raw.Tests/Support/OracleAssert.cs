using System.Text.Json;
using FilmBatch.Raw.Color;

namespace FilmBatch.Raw.Tests.Support;

/// <summary>對照 LightCraft 參考輸出的逐位元斷言：浮點以位元比較，運算順序或精度不同也會失敗。</summary>
public static class OracleAssert
{
    public static void Bits(double actual, double expected) =>
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    public static void XyBits(Xy actual, Xy expected)
    {
        Bits(actual.X, expected.X);
        Bits(actual.Y, expected.Y);
    }

    public static void MatrixBits(Mat3 actual, Mat3 expected)
    {
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                Bits(actual[r, c], expected[r, c]);
    }

    /// <summary><c>color::camera_transform</c> 的序列化結果（矩陣、白平衡、白點、fallback 旗標、基準曝光）。</summary>
    public static void TransformBits(CameraTransform actual, JsonElement expected)
    {
        MatrixBits(actual.Matrix, OracleJson.Mat3(expected.GetProperty("matrix"))!.Value);
        var wb = expected.GetProperty("wb");
        for (var c = 0; c < 3; c++)
            Assert.Equal(BitConverter.SingleToInt32Bits(wb[c].GetSingle()), BitConverter.SingleToInt32Bits(actual.Wb[c]));
        XyBits(actual.WhiteXy, OracleJson.Xy(expected.GetProperty("white_xy"))!.Value);
        Assert.Equal(expected.GetProperty("matrix_is_fallback").GetBoolean(), actual.MatrixIsFallback);
        Bits(actual.BaselineExposure, expected.GetProperty("baseline_exposure").GetDouble());
    }

    /// <summary>解碼器讀出的色彩標籤與 LightCraft <c>ColorData</c> 逐欄相同（含描述檔表格與 Kelvin 預設）。</summary>
    public static void ColourBits(ColorData actual, ColorData expected)
    {
        Assert.Equal(expected.Illuminant, actual.Illuminant);
        MatricesBits(actual.ColorMatrix, expected.ColorMatrix);
        MatricesBits(actual.ForwardMatrix, expected.ForwardMatrix);
        MatricesBits(actual.CameraCalibration, expected.CameraCalibration);
        DoublesBits(actual.AnalogBalance, expected.AnalogBalance);
        DoublesBits(actual.AsShotNeutral, expected.AsShotNeutral);
        Assert.Equal(expected.AsShotWhiteXy.HasValue, actual.AsShotWhiteXy.HasValue);
        if (expected.AsShotWhiteXy is { } xy)
            XyBits(actual.AsShotWhiteXy!.Value, xy);
        Bits(actual.BaselineExposure, expected.BaselineExposure);
        Assert.Equal(expected.BaselineSharpness, actual.BaselineSharpness);
        Assert.Equal(expected.Profile.HueSatMap.Length, actual.Profile.HueSatMap.Length);
        for (var i = 0; i < expected.Profile.HueSatMap.Length; i++)
            TableEqual(actual.Profile.HueSatMap[i], expected.Profile.HueSatMap[i]);
        TableEqual(actual.Profile.LookTable, expected.Profile.LookTable);
        Assert.Equal(expected.Profile.ToneCurve?.Points, actual.Profile.ToneCurve?.Points);
        Assert.Equal(expected.KelvinPresets?.Points, actual.KelvinPresets?.Points);
    }

    private static void MatricesBits(Mat3?[] actual, Mat3?[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].HasValue, actual[i].HasValue);
            if (expected[i] is { } m)
                MatrixBits(actual[i]!.Value, m);
        }
    }

    private static void DoublesBits(double[]? actual, double[]? expected)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is null)
            return;
        Assert.Equal(expected.Length, actual!.Length);
        for (var i = 0; i < expected.Length; i++)
            Bits(actual[i], expected[i]);
    }

    private static void TableEqual(HsvTable? actual, HsvTable? expected)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is null)
            return;
        Assert.Equal((expected.HueDivisions, expected.SatDivisions, expected.ValDivisions, expected.SrgbValue),
            (actual!.HueDivisions, actual.SatDivisions, actual.ValDivisions, actual.SrgbValue));
        Assert.Equal(expected.Data, actual.Data);
    }
}
