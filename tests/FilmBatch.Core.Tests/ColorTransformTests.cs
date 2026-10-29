using FilmBatch.Core.Film;

namespace FilmBatch.Core.Tests;

public sealed class ColorTransformTests
{
    private static readonly float[] MatrixData =
        [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];

    private static readonly float[] RangeData =
        [0.1f, 0.9f, 0.1f, 0.9f];

    private static readonly float[] Lut1Data = [0, 1];

    private static readonly float[] Lut3Data =
        [0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1];


    // ---------- Create：可觀察的不變模型 ----------
    // （enum 數值與 factory 欄位複製屬於 wiring-only，已由 parser 消費端測試涵蓋）

    [Fact]
    public void Create_ExposesImmutableOpsAndData()
    {
        var t = ColorTransform.Create(
            [ColorTransformOp.Matrix(0, 12), ColorTransformOp.Range(12, 4, true, true, true)],
            [.. MatrixData, .. RangeData],
            "chain",
            "名稱");
        Assert.Equal("chain", t.Id);
        Assert.Equal("名稱", t.Name);
        Assert.Equal(2, t.Ops.Length);
        Assert.Equal(16, t.Data.Length);
        Assert.Equal(12, t.Ops[1].DataOffset);
        Assert.Equal(0.9f, t.Data[15]);
    }

    [Fact]
    public void Create_ClonesInputs()
    {
        var ops = new[] { ColorTransformOp.Matrix(0, 12) };
        var data = (float[])MatrixData.Clone();
        var t = ColorTransform.Create(ops, data, "id");
        ops[0] = ColorTransformOp.Range(0, 4, true, true, true);
        data[0] = 42f;
        Assert.Equal(ColorTransformOpCode.Matrix, t.Ops[0].Code);
        Assert.Equal(1f, t.Data[0]);
    }

    [Fact]
    public void Create_ValidatesBeforeConstructing()
    {
        // offset 越界：建構不得成功，也沒有半成品
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Matrix(4, 12)], MatrixData, "id"));
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Matrix(0, 13)], MatrixData, "id"));
    }

    [Fact]
    public void Create_RejectsNonFiniteData()
    {
        var bad = (float[])MatrixData.Clone();
        bad[5] = float.NaN;
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Matrix(0, 12)], bad, "id"));
        bad[5] = float.PositiveInfinity;
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Matrix(0, 12)], bad, "id"));
    }

    [Fact]
    public void Create_RejectsBadOps()
    {
        // 未知 opcode
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([new ColorTransformOp((ColorTransformOpCode)99, 0, 12, 0)], MatrixData, "id"));
        // Matrix 資料長度必須是 12
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Matrix(0, 9)],
                [1, 0, 0, 0, 1, 0, 0, 0, 1], "id"));
        // 空白 id
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Matrix(0, 12)], MatrixData, ""));
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Matrix(0, 12)], MatrixData, "   "));
        // 沒有節點
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([], MatrixData, "id"));
    }

    [Fact]
    public void Create_RejectsBadRangeFlags()
    {
        // 兩側都缺
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Range(0, 4, false, false, true)], RangeData, "id"));
        // 單側卻 noClamp
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Range(0, 4, true, false, false)], RangeData, "id"));
        // 單側正規化不相等
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create(
                [ColorTransformOp.Range(0, 4, true, false, true)],
                [0.5f, 0, 0.6f, 0], "id"));
    }

    [Fact]
    public void Create_RejectsInvertedTwoSidedRange()
    {
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create(
                [ColorTransformOp.Range(0, 4, true, true, false)],
                [0.8f, 0.2f, 0.1f, 0.9f], "id")); // maxIn <= minIn
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create(
                [ColorTransformOp.Range(0, 4, true, true, false)],
                [0.1f, 0.9f, 0.8f, 0.2f], "id")); // maxOut < minOut
    }

    [Fact]
    public void Create_AcceptsTwoSidedConstantRange() =>
        Assert.Single(ColorTransform.Create(
            [ColorTransformOp.Range(0, 4, true, true, true)],
            [0, 1, 0.4f, 0.4f], "id").Ops.ToArray());

    [Fact]
    public void Create_RejectsBadLutOps()
    {
        // 1D size 太少
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Lut1D(0, 1, 1, 1)], [0.5f], "id"));
        // 1D 資料長度與 size*channels 不符
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Lut1D(0, 3, 4, 1)], Lut1Data, "id"));
        // 3D 資料長度不是 n³×3
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Lut3D(0, 23, 2, false)], Lut3Data[..23], "id"));
        // 3D size 超過 128
        var big = new float[129 * 129 * 129 * 3];
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create([ColorTransformOp.Lut3D(0, big.Length, 129, false)], big, "id"));
    }

    [Fact]
    public void Create_RejectsOverLimits()
    {
        // 超過 64 個節點
        var ops = new ColorTransformOp[65];
        var data = new float[65 * 12];
        for (var i = 0; i < ops.Length; i++) ops[i] = ColorTransformOp.Matrix(i * 12, 12);
        Assert.Throws<InvalidDataException>(() => ColorTransform.Create(ops, data, "id"));
    }

    [Fact]
    public void Data_Over32MiB_Rejected()
    {
        const int lut = 128 * 128 * 128 * 3; // 24 MiB
        var data = new float[lut * 2];       // 48 MiB 的合法形狀
        Assert.Throws<InvalidDataException>(() =>
            ColorTransform.Create(
                [ColorTransformOp.Lut3D(0, lut, 128, false), ColorTransformOp.Lut3D(lut, lut, 128, false)],
                data, "id"));
    }

    [Fact]
    public void TwoChains_ParsedAndConstructed_CanMatch()
    {
        // 程式化建構與解析走同一套驗證：同樣的非法模型兩邊都拒絕
        var badOps = new[] { ColorTransformOp.Range(0, 4, true, false, true) };
        var badData = new[] { 0.5f, 0f, 0.6f, 0f };
        Assert.Throws<InvalidDataException>(() => ColorTransform.Create(badOps, badData, "id"));

        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ProcessList id="t" compCLFversion="3.0">
            <Range inBitDepth="16f" outBitDepth="16f">
            <minInValue>0.5</minInValue><minOutValue>0.6</minOutValue></Range>
            </ProcessList>
            """;
        using var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.Throws<InvalidDataException>(() => ClfFile.Read(ms, TestContext.Current.CancellationToken));
    }
}
