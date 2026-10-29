using System.Globalization;
using System.Text;
using FilmBatch.Core.Film;

namespace FilmBatch.Core.Tests;

public sealed class ClfFileTests
{
    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    private const string IdentityMatrix =
        "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\"><Array dim=\"3 3\">1 0 0 0 1 0 0 0 1</Array></Matrix>";

    private static string Doc(string nodes, string listAttrs = "", string decl = Decl) =>
        decl + "\n<ProcessList id=\"t\" compCLFversion=\"3.0\"" + listAttrs + ">\n" + nodes + "\n</ProcessList>\n";

    private static ColorTransform Parse(string xml) =>
        ClfFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), TestContext.Current.CancellationToken);

    private static ColorTransform Parse(byte[] bytes) =>
        ClfFile.Read(new MemoryStream(bytes), TestContext.Current.CancellationToken);

    private static string Array(string dim, params float[] values) =>
        "<Array dim=\"" + dim + "\">" +
        string.Join(' ', values.Select(v => v.ToString(CultureInfo.InvariantCulture))) +
        "</Array>";

    // ---------- 正規化（手算 fixture，AC-CT-01） ----------

    [Fact]
    public void Matrix_10iTo12i_NormalizesCoefficientsAndOffsets()
    {
        var t = Parse(Doc("""
            <Matrix inBitDepth="10i" outBitDepth="12i">
              <Array dim="3 4">
                4.002932551319648 0 0 409.5
                0 4.002932551319648 0 102.375
                0 0 4.002932551319648 0
              </Array>
            </Matrix>
            """));
        var op = t.Ops[0];
        Assert.Equal(ColorTransformOpCode.Matrix, op.Code);
        Assert.Equal(0, op.DataOffset);
        Assert.Equal(12, op.DataLength);
        Assert.Equal(1f, t.Data[0], 6);   // (4095/1023) 的 raw 係數正規化回 1
        Assert.Equal(0f, t.Data[1]);
        Assert.Equal(1f, t.Data[5], 6);
        Assert.Equal(1f, t.Data[10], 6);
        Assert.Equal(0.1f, t.Data[3], 6);   // 409.5 / 4095
        Assert.Equal(0.025f, t.Data[7], 6); // 102.375 / 4095
        Assert.Equal(0f, t.Data[11]);
    }

    [Theory]
    [InlineData("3 3")]
    [InlineData("3 3 3")]
    public void Matrix_3x3_CurrentAndLegacyDims_Accepted(string dim)
    {
        var t = Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array(dim, 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>"));
        Assert.Equal(12, t.Ops[0].DataLength);
        Assert.Equal(1f, t.Data[0]);
        Assert.Equal(0f, t.Data[3]); // 無 offset 欄位時為 0
        Assert.Equal(0f, t.Data[11]);
    }

    [Theory]
    [InlineData("3 4")]
    [InlineData("3 4 3")]
    public void Matrix_3x4_CurrentAndLegacyDims_Accepted(string dim)
    {
        var t = Parse(Doc(
            "<Matrix inBitDepth=\"32f\" outBitDepth=\"32f\">" +
            Array(dim, 1, 0, 0, 0.5f, 0, 1, 0, 0, 0, 0, 1, 0) + "</Matrix>"));
        Assert.Equal(0.5f, t.Data[3]);
    }

    [Fact]
    public void Range_TwoSided_NormalizesEachSideByOwnDepth()
    {
        var t = Parse(Doc("""
            <Range inBitDepth="10i" outBitDepth="16f">
              <minInValue>102.3</minInValue><maxInValue>1023</maxInValue>
              <minOutValue>0.05</minOutValue><maxOutValue>0.95</maxOutValue>
            </Range>
            """));
        var op = t.Ops[0];
        Assert.Equal(ColorTransformOpCode.Range, op.Code);
        Assert.Equal(4, op.DataLength);
        Assert.True(op.RangeHasMin);
        Assert.True(op.RangeHasMax);
        Assert.True(op.RangeClamp); // 省略 style 等於 Clamp
        Assert.Equal(0.1f, t.Data[0], 6);  // 102.3 / 1023
        Assert.Equal(1f, t.Data[1], 6);    // 1023 / 1023
        Assert.Equal(0.05f, t.Data[2]);
        Assert.Equal(0.95f, t.Data[3]);
    }

    [Fact]
    public void Range_TwoSided_NoClamp_KeepsExtendedContract()
    {
        var t = Parse(Doc("""
            <Range style="noClamp" inBitDepth="16f" outBitDepth="16f">
              <minInValue>0</minInValue><maxInValue>1</maxInValue>
              <minOutValue>0.2</minOutValue><maxOutValue>0.8</maxOutValue>
            </Range>
            """));
        var op = t.Ops[0];
        Assert.True(op.RangeHasMin && op.RangeHasMax);
        Assert.False(op.RangeClamp);
        Assert.Equal(0.2f, t.Data[2]);
        Assert.Equal(0.8f, t.Data[3]);
    }

    [Fact]
    public void Range_TwoSided_ConstantOutput_Accepted()
    {
        var t = Parse(Doc("""
            <Range inBitDepth="16f" outBitDepth="16f">
              <minInValue>0</minInValue><maxInValue>1</maxInValue>
              <minOutValue>0.4</minOutValue><maxOutValue>0.4</maxOutValue>
            </Range>
            """));
        Assert.Equal(0.4f, t.Data[2]);
        Assert.Equal(0.4f, t.Data[3]);
    }

    [Fact]
    public void Range_SingleSidedMin_NormalizedEqualityAcrossDepths()
    {
        var t = Parse(Doc("""
            <Range inBitDepth="10i" outBitDepth="12i">
              <minInValue>102.3</minInValue><minOutValue>409.5</minOutValue>
            </Range>
            """));
        var op = t.Ops[0];
        Assert.True(op.RangeHasMin);
        Assert.False(op.RangeHasMax);
        Assert.True(op.RangeClamp);
        Assert.Equal(0.1f, t.Data[0], 6);
        Assert.Equal(0f, t.Data[1]);
        Assert.Equal(0.1f, t.Data[2], 6);
        Assert.Equal(0f, t.Data[3]);
    }

    [Fact]
    public void Range_SingleSidedMax_Accepted()
    {
        var t = Parse(Doc("""
            <Range style="Clamp" inBitDepth="16f" outBitDepth="32f">
              <maxInValue>0.9</maxInValue><maxOutValue>0.9</maxOutValue>
            </Range>
            """));
        var op = t.Ops[0];
        Assert.False(op.RangeHasMin);
        Assert.True(op.RangeHasMax);
        Assert.True(op.RangeClamp);
        Assert.Equal(0.9f, t.Data[1]);
        Assert.Equal(0.9f, t.Data[3]);
    }

    [Fact]
    public void Lut1D_Broadcast_NormalizesByOutputDepth()
    {
        var t = Parse(Doc(
            "<LUT1D inBitDepth=\"8i\" outBitDepth=\"8i\">" +
            Array("3 1", 0, 127.5f, 255) + "</LUT1D>"));
        var op = t.Ops[0];
        Assert.Equal(ColorTransformOpCode.Lut1D, op.Code);
        Assert.Equal(3, op.Lut1DSize);
        Assert.Equal(1, op.Lut1DChannels);
        Assert.Equal(3, op.DataLength);
        Assert.Equal(0f, t.Data[0]);
        Assert.Equal(0.5f, t.Data[1]); // 中點 127.5 / 255
        Assert.Equal(1f, t.Data[2]);
    }

    [Fact]
    public void Lut1D_ThreeColumn_KeepsChannelOrder()
    {
        var t = Parse(Doc(
            "<LUT1D interpolation=\"linear\" inBitDepth=\"16f\" outBitDepth=\"32f\">" +
            Array("2 3", 0, 0.25f, 0.5f, 1, 0.75f, 0.125f) + "</LUT1D>"));
        var op = t.Ops[0];
        Assert.Equal(2, op.Lut1DSize);
        Assert.Equal(3, op.Lut1DChannels);
        Assert.Equal(6, op.DataLength);
        Assert.Equal(0.25f, t.Data[1]);
        Assert.Equal(0.75f, t.Data[4]);
    }

    [Theory]
    [InlineData("3.0")]
    [InlineData("3.00")]
    public void Version_3_Accepted(string version) =>
        Assert.Single(Parse(
            Decl + "\n<ProcessList id=\"t\" compCLFversion=\"" + version + "\">" +
            IdentityMatrix + "</ProcessList>").Ops.ToArray());
    [Fact]
    public void Lut3D_BlueFast_FileOrderPreservedVerbatim()
    {
        var t = Parse(Doc("""
            <LUT3D inBitDepth="32f" outBitDepth="32f"><Array dim="2 2 2 3">
            0 0 0 0 0 1 0 1 0 0 1 1 1 0 0 1 0 1 1 1 0 1 1 1
            </Array></LUT3D>
            """));
        var op = t.Ops[0];
        Assert.Equal(ColorTransformOpCode.Lut3D, op.Code);
        Assert.Equal(2, op.Lut3DSize);
        Assert.False(op.Lut3DTetrahedral); // 預設 trilinear
        Assert.Equal(24, op.DataLength);
        // ((r*N+g)*N+b)*3：第一個非零 triplet 是 b=1 角，不是 r=1
        Assert.Equal(0f, t.Data[3]);
        Assert.Equal(0f, t.Data[4]);
        Assert.Equal(1f, t.Data[5]);
        Assert.Equal(1f, t.Data[12]);
        Assert.Equal(0f, t.Data[13]);
        Assert.Equal(1f, t.Data[23]);
    }

    [Fact]
    public void Lut3D_ExplicitTetrahedral_Recorded()
    {
        var t = Parse(Doc(
            "<LUT3D interpolation=\"tetrahedral\" inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("2 2 2 3", 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1) +
            "</LUT3D>"));
        Assert.True(t.Ops[0].Lut3DTetrahedral);
    }

    [Fact]
    public void Chain_NodeOrderAndDataOffsets_Preserved()
    {
        var t = Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" + Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>" +
            "<Range inBitDepth=\"16f\" outBitDepth=\"16f\"><minInValue>0</minInValue><maxInValue>1</maxInValue><minOutValue>0</minOutValue><maxOutValue>1</maxOutValue></Range>" +
            "<LUT1D inBitDepth=\"16f\" outBitDepth=\"16f\">" + Array("2 1", 0, 1) + "</LUT1D>" +
            "<LUT3D inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("2 2 2 3", 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1) +
            "</LUT3D>"));
        Assert.Equal(4, t.Ops.Length);
        Assert.Equal(
            new[] { ColorTransformOpCode.Matrix, ColorTransformOpCode.Range, ColorTransformOpCode.Lut1D, ColorTransformOpCode.Lut3D },
            t.Ops.ToArray().Select(o => o.Code).ToArray());
        Assert.Equal(new[] { 0, 12, 16, 18 }, t.Ops.ToArray().Select(o => o.DataOffset).ToArray());
        Assert.Equal(new[] { 12, 4, 2, 24 }, t.Ops.ToArray().Select(o => o.DataLength).ToArray());
        Assert.Equal(42, t.Data.Length);
    }

    [Fact]
    public void Chain_ReversedOrder_NotReorderedOrFused()
    {
        var t = Parse(Doc(
            "<Range inBitDepth=\"16f\" outBitDepth=\"16f\"><minInValue>0</minInValue><maxInValue>1</maxInValue><minOutValue>0</minOutValue><maxOutValue>1</maxOutValue></Range>" +
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" + Array("3 3", 2, 0, 0, 0, 2, 0, 0, 0, 2) + "</Matrix>"));
        Assert.Equal(ColorTransformOpCode.Range, t.Ops[0].Code);
        Assert.Equal(ColorTransformOpCode.Matrix, t.Ops[1].Code);
    }

    [Fact]
    public void Id_Name_Descriptors_Metadata_AcceptedButDoNotChangeOps()
    {
        var nodes =
            "<Matrix id=\"m1\" name=\"mat\" xml:lang=\"zh-TW\" inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<Description>說明</Description>" + Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>";
        var bare = Parse(Doc(nodes));
        var rich = Parse(Doc(
            "<Description>整體描述</Description>" +
            "<InputDescriptor>ACES2065-1</InputDescriptor>" +
            "<OutputDescriptor>sRGB</OutputDescriptor>" +
            "<!-- a comment -->" +
            "<Info><ACEStransformID>x</ACEStransformID><Custom attr=\"1\"><Nested><Deep/></Nested></Custom>" +
            "<Reference path=\"/nonexistent/never-opened.clf\"/><Matrix/></Info>" +
            nodes,
            " name=\"顯示名\" inverseOf=\"other\" xml:lang=\"en\""));
        Assert.Equal(bare.Ops.ToArray(), rich.Ops.ToArray());
        Assert.Equal(bare.Data.ToArray(), rich.Data.ToArray());
        Assert.Equal("t", rich.Id);
        Assert.Equal("顯示名", rich.Name);
        Assert.Equal("整體描述", rich.Description);
        Assert.Equal("ACES2065-1", rich.InputDescriptor);
        Assert.Equal("sRGB", rich.OutputDescriptor);
    }

    [Fact]
    public void Descriptors_Absent_AreNull()
    {
        var t = Parse(Doc(IdentityMatrix));
        Assert.Null(t.Name);
        Assert.Null(t.Description);
        Assert.Null(t.InputDescriptor);
        Assert.Null(t.OutputDescriptor);
    }

    [Fact]
    public void NodeLevelDescription_DoesNotLeakToProcessList()
    {
        var t = Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<Description>節點說明</Description>" +
            Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>"));
        Assert.Null(t.Description);
    }

    // ---------- 版本／根元素／編碼 ----------

    [Theory]
    [InlineData("2.0")]
    [InlineData("3")]
    [InlineData("4.0")]
    [InlineData("v3.0")]
    [InlineData("3.0.1")]
    [InlineData("abc")]
    public void Version_Not3_Rejected(string version) =>
        Assert.Throws<InvalidDataException>(() =>
            Parse(Doc(IdentityMatrix).Replace("compCLFversion=\"3.0\"", $"compCLFversion=\"{version}\"")));

    [Fact]
    public void Version_Missing_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(IdentityMatrix).Replace(" compCLFversion=\"3.0\"", "")));

    [Fact]
    public void Id_Missing_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(IdentityMatrix).Replace(" id=\"t\"", "")));

    [Fact]
    public void Id_Empty_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(IdentityMatrix).Replace("id=\"t\"", "id=\"\"")));

    [Fact]
    public void Root_Namespaced_Rejected() =>
        Assert.Throws<InvalidDataException>(() =>
            Parse(Doc(IdentityMatrix).Replace("<ProcessList", "<ProcessList xmlns=\"urn:clf\"")));

    [Fact]
    public void Root_Prefixed_Rejected() =>
        Assert.Throws<InvalidDataException>(() =>
            Parse("<c:ProcessList xmlns:c=\"urn:clf\" id=\"t\" compCLFversion=\"3.0\">" + IdentityMatrix + "</c:ProcessList>"));

    [Fact]
    public void Root_WrongElement_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse("<Look id=\"t\" compCLFversion=\"3.0\">" + IdentityMatrix + "</Look>"));

    [Fact]
    public void Root_CaseSensitive_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse("<processlist id=\"t\" compCLFversion=\"3.0\">" + IdentityMatrix + "</processlist>"));

    [Fact]
    public void Root_UnknownAttribute_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Doc(IdentityMatrix, " version=\"3.0\"")));
        Assert.Throws<InvalidDataException>(() => Parse(Doc(IdentityMatrix, " foo=\"1\"")));
        Assert.Throws<InvalidDataException>(() => Parse(Doc(IdentityMatrix, " xmlns:x=\"urn:x\"")));
    }

    [Fact]
    public void ProcessList_NoNodes_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc("")));

    [Fact]
    public void UnknownElement_InProcessList_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc("<Foo/>" + IdentityMatrix)));

    [Fact]
    public void TrailingContent_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Doc(IdentityMatrix) + "<ProcessList/>"));
        Assert.Throws<InvalidDataException>(() => Parse(Doc(IdentityMatrix) + "junk"));
    }

    [Fact]
    public void EmptyStream_Rejected() =>
        Assert.Throws<InvalidDataException>(() => ClfFile.Read(new MemoryStream(), TestContext.Current.CancellationToken));

    // ---------- UTF-8 嚴格 ----------

    [Fact]
    public void Utf8_Bom_Accepted()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Doc(IdentityMatrix))).ToArray();
        Assert.Single(Parse(bytes).Ops.ToArray());
    }

    [Fact]
    public void Utf16_Bytes_Rejected() =>
        Assert.Throws<InvalidDataException>(() =>
            Parse(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Doc(IdentityMatrix))).ToArray()));

    [Fact]
    public void InvalidUtf8_Rejected()
    {
        var bytes = Encoding.UTF8.GetBytes(Doc(IdentityMatrix));
        bytes[40] = 0xC0;
        bytes[41] = 0xAF; // overlong encoding
        Assert.Throws<InvalidDataException>(() => Parse(bytes));
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("UTF-16")]
    [InlineData("latin1")]
    public void DeclaredNonUtf8_Rejected(string encoding) =>
        Assert.Throws<InvalidDataException>(() =>
            Parse(Doc(IdentityMatrix).Replace("encoding=\"UTF-8\"", $"encoding=\"{encoding}\"")));

    [Theory]
    [InlineData("utf-8")]
    [InlineData("UTF-8")]
    public void DeclaredUtf8_Accepted(string encoding) =>
        Assert.Single(Parse(Doc(IdentityMatrix).Replace("encoding=\"UTF-8\"", $"encoding=\"{encoding}\"")).Ops.ToArray());

    [Fact]
    public void NoDeclaration_StillParsed() =>
        Assert.Single(Parse(Doc(IdentityMatrix, "", "")).Ops.ToArray());

    // ---------- DTD／entity／PI／外部 I/O ----------

    [Fact]
    public void Dtd_InternalSubset_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(
            Decl + "\n<!DOCTYPE ProcessList [ <!ENTITY x \"y\"> ]>\n" +
            "<ProcessList id=\"t\" compCLFversion=\"3.0\">" + IdentityMatrix + "</ProcessList>"));

    [Fact]
    public void Dtd_External_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(
            Decl + "\n<!DOCTYPE ProcessList SYSTEM \"file:///etc/passwd\">\n" +
            "<ProcessList id=\"t\" compCLFversion=\"3.0\">" + IdentityMatrix + "</ProcessList>"));

    [Fact]
    public void EntityReference_Rejected() =>
        Assert.Throws<InvalidDataException>(() =>
            Parse(Doc("<Description>&undefined;</Description>" + IdentityMatrix)));

    [Theory]
    [InlineData("<?target data?><ProcessList id=\"t\" compCLFversion=\"3.0\">" + IdentityMatrix)]
    [InlineData(Decl + "\n<?xml-stylesheet href=\"x\"?>\n<ProcessList id=\"t\" compCLFversion=\"3.0\">" + IdentityMatrix)]
    public void ProcessingInstruction_Rejected(string prefix) =>
        Assert.Throws<InvalidDataException>(() => Parse(prefix + "</ProcessList>"));

    [Fact]
    public void ProcessingInstruction_InsideList_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc("<?pi data?>" + IdentityMatrix)));

    [Theory]
    [InlineData("Log")]
    [InlineData("Exponent")]
    [InlineData("ASC_CDL")]
    [InlineData("IndexMap")]
    [InlineData("Reference")]
    [InlineData("ProcessNode")]
    [InlineData("CalibrationInfo")]
    [InlineData("Gamma")]
    public void UnsupportedNode_RejectedAndNamesNode(string node)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parse(Doc(
            $"<{node} inBitDepth=\"16f\" outBitDepth=\"16f\"/>" + IdentityMatrix)));
        Assert.Contains(node, ex.Message);
    }

    [Fact]
    public void Reference_RejectedWithoutTouchingPath()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Reference inBitDepth=\"16f\" outBitDepth=\"16f\" path=\"/nonexistent/definitely-missing.clf\"/>" +
            IdentityMatrix)));
        Assert.Contains("Reference", ex.Message);
    }

    // ---------- 節點屬性／bit depth ----------

    [Fact]
    public void Node_UnknownAttribute_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\" direction=\"inverse\">" +
            Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")));
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\" style=\"x\">" +
            Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")));
    }

    [Theory]
    [InlineData("<Matrix outBitDepth=\"16f\">")]           // 缺 inBitDepth
    [InlineData("<Matrix inBitDepth=\"16f\">")]            // 缺 outBitDepth
    [InlineData("<Matrix inbitdepth=\"16f\" outBitDepth=\"16f\">")] // 大小寫不同即未知屬性
    public void Node_BitDepthMissingOrUnknownAttr_Rejected(string open) =>
        Assert.Throws<InvalidDataException>(() =>
            Parse(Doc(open + Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")));

    [Theory]
    [InlineData("12f")]
    [InlineData("64f")]
    [InlineData("8")]
    [InlineData("10ui")]
    [InlineData("FLOAT")]
    [InlineData("")]
    public void BitDepth_Unknown_Rejected(string depth) =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            $"<Matrix inBitDepth=\"{depth}\" outBitDepth=\"16f\">" +
            Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")));

    [Fact]
    public void BitDepth_AdjacentMismatch_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"12i\">" + Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>" +
            "<Matrix inBitDepth=\"10i\" outBitDepth=\"16f\">" + Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")));

    [Fact]
    public void Node_UnknownChild_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\"><Foo/>" + Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")));
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\"><Info/>" + Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")));
    }

    // ---------- Matrix 邊界 ----------

    [Theory]
    [InlineData("4 4")]
    [InlineData("3 5")]
    [InlineData("3 3 4")]
    [InlineData("2 2")]
    [InlineData("3 3 3 3")]
    [InlineData("3 3 x")]
    [InlineData("x 3")]
    [InlineData("3")]
    [InlineData("")]
    public void Matrix_InvalidDim_Rejected(string dim) =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array(dim, 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")));

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(13)]
    public void Matrix_TokenCountMismatch_Rejected(int count)
    {
        var values = Enumerable.Repeat(0.5f, count).ToArray();
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("3 3", values) + "</Matrix>")));
    }

    [Fact]
    public void Matrix_MissingArray_Rejected() =>
        Assert.Throws<InvalidDataException>(() =>
            Parse(Doc("<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\"></Matrix>")));

    [Fact]
    public void Matrix_TwoArrays_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) +
            Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")));

    [Fact]
    public void Array_UnknownAttribute_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<Array dim=\"3 3\" foo=\"1\">1 0 0 0 1 0 0 0 1</Array></Matrix>")));

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e39")]   // 超過 f32
    [InlineData("abc")]
    [InlineData("0x10")]
    [InlineData("1,5")]
    public void Matrix_BadToken_Rejected(string token)
    {
        var values = Enumerable.Repeat("0", 9).ToArray();
        values[4] = token;
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\"><Array dim=\"3 3\">" +
            string.Join(' ', values) + "</Array></Matrix>")));
    }

    [Fact]
    public void Array_NumericTokenAcrossTextCdataAndComment_ParsesAsOneValue()
    {
        var xml = Doc("<Matrix inBitDepth=\"32f\" outBitDepth=\"32f\"><Array dim=\"3 3\">"
            + new string(' ', 4094) + "1.2<![CDATA[e]]><!-- split -->-1 0 0 0 1 0 0 0 1"
            + "</Array></Matrix>");
        var result = Parse(xml);
        Assert.Equal(0.12f, result.Data[0], 6);
        Assert.Equal(1f, result.Data[5]);
        Assert.Equal(1f, result.Data[10]);
    }

    [Theory]
    [InlineData(4096, false)]
    [InlineData(4097, true)]
    public void Array_NumericTokenLength_IsBoundedAcrossChunks(int length, bool rejected)
    {
        var xml = Doc("<Matrix inBitDepth=\"32f\" outBitDepth=\"32f\"><Array dim=\"3 3\">"
            + new string('0', length) + " 0 0 0 1 0 0 0 1</Array></Matrix>");
        if (rejected) Assert.Throws<InvalidDataException>(() => Parse(xml));
        else Assert.Equal(0f, Parse(xml).Data[0]);
    }

    // ---------- Range 邊界 ----------

    [Theory]
    [InlineData("<minInValue>0.5</minInValue>")]
    [InlineData("<minOutValue>0.5</minOutValue>")]
    [InlineData("<maxInValue>0.5</maxInValue>")]
    [InlineData("<maxOutValue>0.5</maxOutValue>")]
    [InlineData("<minInValue>0</minInValue><maxInValue>1</maxInValue>")]
    [InlineData("<minOutValue>0</minOutValue><maxOutValue>1</maxOutValue>")]
    [InlineData("<minInValue>0</minInValue><maxOutValue>1</maxOutValue>")]
    [InlineData("<maxInValue>0</maxInValue><minOutValue>1</minOutValue>")]
    [InlineData("")]
    public void Range_IncompletePairs_Rejected(string inner) =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            $"<Range inBitDepth=\"16f\" outBitDepth=\"16f\">{inner}</Range>")));

    [Fact]
    public void Range_SingleSided_Offset_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Range inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<minInValue>0.5</minInValue><minOutValue>0.6</minOutValue></Range>")));

    [Fact]
    public void Range_SingleSided_NoClamp_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Range style=\"noClamp\" inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<minInValue>0.5</minInValue><minOutValue>0.5</minOutValue></Range>")));

    [Fact]
    public void Range_SingleSided_UnequalAcrossDepths_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Range inBitDepth=\"10i\" outBitDepth=\"12i\">" +
            "<minInValue>102.3</minInValue><minOutValue>409.4</minOutValue></Range>")));

    [Theory]
    // maxIn <= minIn
    [InlineData("<minInValue>0.5</minInValue><maxInValue>0.4</maxInValue><minOutValue>0</minOutValue><maxOutValue>1</maxOutValue>")]
    [InlineData("<minInValue>0.5</minInValue><maxInValue>0.5</maxInValue><minOutValue>0</minOutValue><maxOutValue>1</maxOutValue>")]
    // maxOut < minOut
    [InlineData("<minInValue>0</minInValue><maxInValue>1</maxInValue><minOutValue>0.9</minOutValue><maxOutValue>0.4</maxOutValue>")]
    public void Range_InvertedBounds_Rejected(string inner) =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            $"<Range inBitDepth=\"16f\" outBitDepth=\"16f\">{inner}</Range>")));

    [Theory]
    [InlineData("noclamp")]
    [InlineData("CLAMP")]
    [InlineData("linear")]
    [InlineData("")]
    public void Range_UnknownStyle_Rejected(string style) =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            $"<Range style=\"{style}\" inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<minInValue>0</minInValue><maxInValue>1</maxInValue><minOutValue>0</minOutValue><maxOutValue>1</maxOutValue></Range>")));

    [Fact]
    public void Range_EmptyBound_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Range inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<minInValue></minInValue><maxInValue>1</maxInValue><minOutValue>0</minOutValue><maxOutValue>1</maxOutValue></Range>")));

    [Fact]
    public void Range_NonNumericBound_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Range inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<minInValue>abc</minInValue><maxInValue>1</maxInValue><minOutValue>0</minOutValue><maxOutValue>1</maxOutValue></Range>")));

    [Fact]
    public void Range_DuplicateBound_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Range inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<minInValue>0</minInValue><minInValue>0</minInValue><maxInValue>1</maxInValue><minOutValue>0</minOutValue><maxOutValue>1</maxOutValue></Range>")));

    // ---------- LUT1D 邊界 ----------

    [Theory]
    [InlineData("2")]
    [InlineData("1 1")]
    [InlineData("65537 1")]
    [InlineData("4 2")]
    [InlineData("4 4")]
    [InlineData("2 1 1")]
    [InlineData("0 1")]
    [InlineData("-2 1")]
    public void Lut1D_InvalidDim_Rejected(string dim) =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<LUT1D inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array(dim, 0, 0) + "</LUT1D>")));

    [Theory]
    [InlineData("nearest")]
    [InlineData("trilinear")]
    [InlineData("cubic")]
    public void Lut1D_NonLinearInterpolation_Rejected(string interpolation) =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            $"<LUT1D interpolation=\"{interpolation}\" inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("2 1", 0, 1) + "</LUT1D>")));

    [Fact]
    public void Lut1D_HalfDomain_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<LUT1D halfDomain=\"true\" inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("2 1", 0, 1) + "</LUT1D>")));

    [Fact]
    public void Lut1D_RawHalfs_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<LUT1D rawHalfs=\"true\" inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("2 1", 0, 1) + "</LUT1D>")));

    [Fact]
    public void Lut1D_TokenCountMismatch_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<LUT1D inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("4 1", 0, 0.5f, 1) + "</LUT1D>")));

    // ---------- LUT3D 邊界 ----------

    [Theory]
    [InlineData("2 2 2")]
    [InlineData("2 3 2 3")]
    [InlineData("2 2 2 4")]
    [InlineData("2 2 2 2")]
    [InlineData("1 1 1 3")]
    [InlineData("129 129 129 3")]
    [InlineData("0 2 2 3")]
    [InlineData("2 2 2 3 3")]
    [InlineData("99999999 99999999 99999999 3")]
    public void Lut3D_InvalidDim_Rejected(string dim) =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<LUT3D inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<Array dim=\"" + dim + "\">0 0 0</Array></LUT3D>")));

    [Theory]
    [InlineData("nearest")]
    [InlineData("cubic")]
    [InlineData("linear")]
    public void Lut3D_UnknownInterpolation_Rejected(string interpolation) =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            $"<LUT3D interpolation=\"{interpolation}\" inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("2 2 2 3", 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1) +
            "</LUT3D>")));

    [Fact]
    public void Lut3D_TokenCountMismatch_Rejected() =>
        Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<LUT3D inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            Array("2 2 2 3", 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0) +
            "</LUT3D>")));

    // ---------- metadata 界線 ----------

    [Fact]
    public void Info_OpaqueContent_DoesNotAddNodesOrIo()
    {
        var t = Parse(Doc(
            "<Info xmlns:c=\"urn:c\"><c:Matrix/><c:Reference path=\"/nonexistent/x.clf\"/>" +
            "<Matrix inBitDepth=\"8i\" outBitDepth=\"8i\"/><Whatever deep=\"1\">text<![CDATA[more]]></Whatever></Info>" +
            IdentityMatrix));
        Assert.Single(t.Ops.ToArray());
        Assert.Equal(ColorTransformOpCode.Matrix, t.Ops[0].Code);
    }

    [Fact]
    public void Description_InNode_Accepted() =>
        Assert.Single(Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<Description xml:lang=\"zh-TW\">敘述</Description>" +
            Array("3 3", 1, 0, 0, 0, 1, 0, 0, 0, 1) + "</Matrix>")).Ops.ToArray());

    [Fact]
    public void Comment_InsideArray_Ignored() =>
        Assert.Single(Parse(Doc(
            "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">" +
            "<Array dim=\"3 3\">1 0 0 <!-- 註解 --> 0 1 0 0 0 1</Array></Matrix>")).Ops.ToArray());

    [Fact]
    public void Cdata_InDescription_Accepted() =>
        Assert.Single(Parse(Doc(
            "<Description><![CDATA[a < b && c]]></Description>" + IdentityMatrix)).Ops.ToArray());

    [Fact]
    public void Metadata_Over65536Chars_Rejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parse(Doc(
            "<Description>" + new string('x', 65537) + "</Description>" + IdentityMatrix)));
        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public void Metadata_ExactlyCap_Accepted() =>
        Assert.Single(Parse(Doc(
            "<Description>" + new string('x', 65535) + "</Description>" + IdentityMatrix)).Ops.ToArray());

    [Theory]
    [InlineData(65532, false)]
    [InlineData(65533, true)]
    public void Metadata_CombinedAttributesAndText_EnforcesCap(int length, bool rejected)
    {
        var xml = Doc("<Description>" + new string('x', length) + "</Description>" + IdentityMatrix,
            " name=\"abc\""); // id (1) + name (3) + text
        if (rejected) Assert.Throws<InvalidDataException>(() => Parse(xml));
        else Assert.Equal(length, Parse(xml).Description!.Length);
    }

    [Theory]
    [InlineData("Description")]
    [InlineData("Info")]
    public void Metadata_WhitespaceCountsTowardCap(string element)
    {
        var xml = Doc("<" + element + ">" + new string(' ', 65536) + "</" + element + ">" + IdentityMatrix);
        Assert.Throws<InvalidDataException>(() => Parse(xml));
    }

    [Fact]
    public void XmlDepth_Over16_Rejected()
    {
        var nest = string.Concat(Enumerable.Repeat("<a>", 15)) + string.Concat(Enumerable.Repeat("</a>", 15));
        Assert.Throws<InvalidDataException>(() => Parse(Doc("<Info>" + nest + "</Info>" + IdentityMatrix)));
    }

    [Fact]
    public void XmlDepth_Exactly16_Accepted()
    {
        var nest = string.Concat(Enumerable.Repeat("<a>", 14)) + string.Concat(Enumerable.Repeat("</a>", 14));
        Assert.Single(Parse(Doc("<Info>" + nest + "</Info>" + IdentityMatrix)).Ops.ToArray());
    }

    [Fact]
    public void NodeCount_Over64_Rejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parse(Doc(
            string.Concat(Enumerable.Repeat(IdentityMatrix, 65)))));
        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public void NodeCount_Exactly64_Accepted() =>
        Assert.Equal(64, Parse(Doc(string.Concat(Enumerable.Repeat(IdentityMatrix, 64)))).Ops.Length);

    [Fact]
    public void NormalizedData_Over32MiB_Rejected()
    {
        var tokens = string.Concat(Enumerable.Repeat("0 ", 128 * 128 * 128 * 3));
        var lut = "<LUT3D inBitDepth=\"16f\" outBitDepth=\"16f\"><Array dim=\"128 128 128 3\">" + tokens + "</Array></LUT3D>";
        var second = "<LUT3D inBitDepth=\"16f\" outBitDepth=\"16f\"><Array dim=\"100 100 100 3\">0 0 0</Array></LUT3D>";
        var ex = Assert.Throws<InvalidDataException>(() => Parse(Doc(lut + second)));
        Assert.Contains("上限", ex.Message);
    }

    // ---------- stream／取消／擁有權 ----------

    [Fact]
    public void Input_Over128MiB_Rejected() =>
        Assert.Throws<InvalidDataException>(() =>
            ClfFile.Read(new FloodStream(128L * 1024 * 1024 + 1, seekable: true), TestContext.Current.CancellationToken));

    [Fact]
    public void Input_NonSeekableOverCap_Rejected()
    {
        // 內容是合法 UTF-8/XML 空白，必須靠實際讀到的 bytes 計數才會命中 128 MiB 上限。
        var prefix = Encoding.UTF8.GetBytes("<ProcessList id=\"t\" compCLFversion=\"3.0\"><LUT1D inBitDepth=\"16f\" outBitDepth=\"16f\"><Array dim=\"65536 1\">");
        var ex = Assert.Throws<InvalidDataException>(() =>
            ClfFile.Read(new ValidPrefixFlood(prefix, 0x20), TestContext.Current.CancellationToken));
        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public void NonSeekableStream_Parses()
    {
        var inner = new MemoryStream(Encoding.UTF8.GetBytes(Doc(IdentityMatrix)));
        Assert.Single(ClfFile.Read(new NonSeekable(inner), TestContext.Current.CancellationToken).Ops.ToArray());
    }

    [Fact]
    public void Read_LeavesStreamOpen()
    {
        var ms = new MemoryStream(Encoding.UTF8.GetBytes(Doc(IdentityMatrix)));
        ClfFile.Read(ms, TestContext.Current.CancellationToken);
        Assert.True(ms.Length > 0); // 已讀完但沒有關閉；已 Dispose 會丟 ObjectDisposedException
    }

    [Fact]
    public void EarlyRejection_DoesNotDrainSource()
    {
        // 2 MiB 尾巴：串流 parser 必須在遇到不支援節點時就停止，不先把整份讀進記憶體。
        var xml = Doc("<Log inBitDepth=\"16f\" outBitDepth=\"16f\"/>" +
            new string(' ', 2 * 1024 * 1024) + IdentityMatrix);
        var bytes = Encoding.UTF8.GetBytes(xml);
        var counting = new CountingStream(new NonSeekable(new MemoryStream(bytes)));
        Assert.Throws<InvalidDataException>(() => ClfFile.Read(counting, TestContext.Current.CancellationToken));
        Assert.True(counting.BytesRead < bytes.Length,
            $"拒絕不支援節點時讀了 {counting.BytesRead} bytes，整份檔案 {bytes.Length} bytes 不該被讀完。");
        Assert.True(counting.BytesRead < bytes.Length / 2,
            "應在檔案前段就拒絕，超過一半仍算把尾巴讀進來。");
    }

    [Fact]
    public void SeekableStream_ParsesFromCurrentPosition()
    {
        var prefix = Encoding.ASCII.GetBytes("previous-file-tail junk 987654321");
        var body = Encoding.UTF8.GetBytes(Doc(IdentityMatrix));
        var ms = new MemoryStream(prefix.Concat(body).ToArray());
        ms.Position = prefix.Length;
        Assert.Single(ClfFile.Read(ms, TestContext.Current.CancellationToken).Ops.ToArray());
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
    }

    [Fact]
    public void NullSource_Throws() =>
        Assert.Throws<ArgumentNullException>(() => ClfFile.Read(null!, TestContext.Current.CancellationToken));

    [Fact]
    public void CancelledBeforeRead_Throws() =>
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ClfFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(Doc(IdentityMatrix))), new CancellationToken(true)));

    [Fact]
    public void CancelledMidRead_Throws()
    {
        var bytes = Encoding.UTF8.GetBytes(Doc(IdentityMatrix));
        var cts = new CancellationTokenSource();
        var stream = new CancellingStream(new MemoryStream(bytes), cts);
        Assert.ThrowsAny<OperationCanceledException>(() => ClfFile.Read(stream, cts.Token));
    }

    // ---------- 測試用 stream ----------

    private sealed class FloodStream(long length, bool seekable) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            _position += n;
            return n; // buffer 內容全 0
        }
    }

    /// <summary>先輸出一段合法 bytes，之後無窮輸出指定 byte；CanSeek=false，Length 不可知。</summary>
    private sealed class ValidPrefixFlood(byte[] prefix, byte fill) : Stream
    {
        private int _prefixPos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = 0;
            while (n < count && _prefixPos < prefix.Length)
                buffer[offset + n++] = prefix[_prefixPos++];
            while (n < count)
                buffer[offset + n++] = fill;
            return n;
        }
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    }

    private sealed class CancellingStream(Stream inner, CancellationTokenSource cts) : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (++_reads >= 1) cts.Cancel();
            return inner.Read(buffer, offset, count);
        }
    }
}
