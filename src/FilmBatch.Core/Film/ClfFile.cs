using System.Globalization;
using System.Text;
using System.Xml;

namespace FilmBatch.Core.Film;

/// <summary>
/// Academy CLF 3.0 的受限串流 parser（CT-2 §3.2）。只接受無 namespace 的
/// <c>ProcessList</c>、數值 3.0 的 <c>compCLFversion</c>、非空 id、嚴格 UTF-8（容忍 BOM）；
/// 節點白名單是 Matrix／Range／LUT1D／LUT3D。DTD、entity、外部 Reference、
/// 非宣告 processing instruction、未知節點／屬性一律在建構前拒絕，不產生部分結果。
/// </summary>
public static class ClfFile
{
    public const long MaxInputBytes = 128L * 1024 * 1024;
    public const int MaxNodes = ColorTransform.MaxNodes;
    public const int MaxDepth = 16;
    public const int MaxMetadataChars = 65536;
    private const int MaxDataFloats = (int)(ColorTransform.MaxDataBytes / sizeof(float));

    /// <summary>串流讀取一份 CLF；caller 保有 stream 所有權。所有內容錯誤都是 <see cref="InvalidDataException"/>，取消不包裝。</summary>
    public static ColorTransform Read(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var input = OpenBounded(source, cancellationToken);
        using var text = new StreamReader(input, new UTF8Encoding(false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CheckCharacters = true,
            IgnoreWhitespace = false,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            CloseInput = true,
        };
        try
        {
            using var reader = XmlReader.Create(text, settings);
            return new Parser(reader, cancellationToken).Parse();
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"CLF XML 格式錯誤：{ex.Message}", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("CLF 必須是 UTF-8。", ex);
        }
    }

    /// <summary>
    /// 把 caller stream 包成有界唯讀串流：seekable 先用 Length-Position 快速拒絕超限，
    /// 之後以實際讀到的 bytes 計數、超過上限立刻拒絕；不把整份檔案讀進記憶體，也不關閉 caller stream。
    /// 前 3 bytes 用來判斷 UTF-8 BOM（有就略過，沒有就回放），讓 UTF-16 等位元組直接解碼失敗。
    /// </summary>
    private static Stream OpenBounded(Stream source, CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            long remaining;
            try { remaining = source.Length - source.Position; }
            catch (NotSupportedException) { remaining = -1; }
            if (remaining > MaxInputBytes)
                throw new InvalidDataException($"CLF 超過 {MaxInputBytes} bytes 上限。");
        }
        var head = new byte[3];
        var got = ReadUpTo(source, head, cancellationToken);
        var bom = got == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        byte[]? prefix = null;
        if (!bom && got > 0)
        {
            prefix = new byte[got];
            Array.Copy(head, prefix, got);
        }
        return new BoundedStream(source, got, prefix, cancellationToken);
    }

    private static int ReadUpTo(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = source.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>唯讀、不可 seek、不擁有 inner 的有界串流；先回放 prefix，再轉發 inner 並計數。</summary>
    private sealed class BoundedStream(Stream inner, long alreadyRead, byte[]? prefix, CancellationToken cancellationToken)
        : Stream
    {
        private int _prefixPos;
        private long _read = alreadyRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) { } // inner 屬於 caller，不關閉

        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prefix != null && _prefixPos < prefix.Length)
            {
                var replay = Math.Min(count, prefix.Length - _prefixPos);
                Array.Copy(prefix, _prefixPos, buffer, offset, replay);
                _prefixPos += replay;
                return replay;
            }
            var n = inner.Read(buffer, offset, count);
            if (n > 0)
            {
                _read += n;
                if (_read > MaxInputBytes)
                    throw new InvalidDataException($"CLF 超過 {MaxInputBytes} bytes 上限。");
            }
            return n;
        }
    }

    private sealed class Parser(XmlReader reader, CancellationToken cancellationToken)
    {
        private static readonly string[] NodeBaseAttrs =
            ["id", "name", "inBitDepth", "outBitDepth", "xml:lang"];

        private readonly List<ColorTransformOp> _ops = new(4);
        private readonly List<float> _data = new(64);
        private long _dataFloats;
        private int _metadataChars;
        private string? _prevOutBitDepth;
        private readonly char[] _pending = new char[4096];
        private int _pendingLength;
        private readonly char[] _chunk = new char[4096];
        private int _tokensEmitted;
        private int _tokenCapacity;
        private string? _id;
        private string? _name;
        private string? _description;
        private string? _inputDescriptor;
        private string? _outputDescriptor;

        private static InvalidDataException Fail(string reason) => new($"CLF 不支援或格式錯誤：{reason}");

        public ColorTransform Parse()
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.XmlDeclaration:
                        CheckDeclaredEncoding(reader.Value);
                        break;
                    case XmlNodeType.Comment:
                        ReadChunked(null, metadata: true);
                        break;
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        throw Fail($"不接受 processing instruction 「{reader.Name}」。");
                    case XmlNodeType.Element:
                        CheckDepth();
                        if (reader.NamespaceURI.Length != 0 || reader.Name != "ProcessList")
                            throw Fail($"根元素必須是無 namespace 的 ProcessList，卻是 「{reader.Name}」。");
                        var transform = ParseProcessList();
                        EnsureDocumentEnd();
                        return transform;
                    default:
                        throw Fail($"根元素前出現 {reader.NodeType}。");
                }
            }
            throw Fail("檔案沒有 ProcessList 根元素。");
        }

        private void EnsureDocumentEnd()
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.Comment:
                        ReadChunked(null, metadata: true);
                        break;
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        break;
                    default:
                        throw Fail($"ProcessList 之後出現 {reader.NodeType}，CLF 只能有一個根元素。");
                }
            }
        }

        private ColorTransform ParseProcessList()
        {
            var attrs = ReadAttributes();
            RequireAttrs(attrs, "id", "name", "inverseOf", "compCLFversion", "xml:lang");
            var id = Attr(attrs, "id") ?? throw Fail("ProcessList 缺少 id。");
            var version = Attr(attrs, "compCLFversion")
                ?? throw Fail("ProcessList 缺少 compCLFversion。");
            if (!double.TryParse(version, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                || v != 3.0 || !version.Contains('.'))
                throw Fail($"compCLFversion 必須是數值 3.0，卻是 「{version}」。");
            _id = id;
            _name = Attr(attrs, "name");
            CountMetadata(id);
            CountMetadata(_name);
            CountMetadata(Attr(attrs, "inverseOf"));
            CountMetadata(Attr(attrs, "xml:lang"));

            if (reader.IsEmptyElement)
                return Finish();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.EndElement:
                        return Finish();
                    case XmlNodeType.Comment:
                        ReadChunked(null, metadata: true);
                        break;
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        throw Fail($"不接受 processing instruction 「{reader.Name}」。");
                    case XmlNodeType.Element:
                        CheckDepth();
                        CheckNoNamespace();
                        switch (reader.Name)
                        {
                            case "Description":
                                var text = ReadTextElement();
                                _description = _description == null ? text : _description + "\n" + text;
                                break;
                            case "InputDescriptor":
                                _inputDescriptor = ReadTextElement();
                                break;
                            case "OutputDescriptor":
                                _outputDescriptor = ReadTextElement();
                                break;
                            case "Info":
                                SkipOpaqueSubtree();
                                break;
                            case "Matrix":
                                ParseMatrix();
                                break;
                            case "Range":
                                ParseRange();
                                break;
                            case "LUT1D":
                                ParseLut1D();
                                break;
                            case "LUT3D":
                                ParseLut3D();
                                break;
                            default:
                                throw Fail($"不支援的節點 「{reader.Name}」。");
                        }
                        break;
                    default:
                        throw Fail($"ProcessList 內出現 {reader.NodeType}。");
                }
            }
            throw Fail("ProcessList 未關閉。");
        }

        private ColorTransform Finish() =>
            ColorTransform.FromOwned(_ops.ToArray(), _data.ToArray(), _id!, _name,
                _description, _inputDescriptor, _outputDescriptor);

        // ---------- ProcessNode 共用 ----------

        private (double inScale, double outScale) ReadNodeHeader(
            Dictionary<string, string> attrs, string node, params string[] extraAttrs)
        {
            if (_ops.Count >= MaxNodes)
                throw Fail($"節點數超過上限 {MaxNodes}。");
            RequireAttrs(attrs, [.. NodeBaseAttrs, .. extraAttrs]);
            var inDepth = Attr(attrs, "inBitDepth") ?? throw Fail($"{node} 缺少 inBitDepth。");
            var outDepth = Attr(attrs, "outBitDepth") ?? throw Fail($"{node} 缺少 outBitDepth。");
            var inScale = BitDepthScale(node, "inBitDepth", inDepth);
            var outScale = BitDepthScale(node, "outBitDepth", outDepth);
            if (_prevOutBitDepth != null && !string.Equals(_prevOutBitDepth, inDepth, StringComparison.Ordinal))
                throw Fail($"{node} 的 inBitDepth 「{inDepth}」與前一節點 outBitDepth 「{_prevOutBitDepth}」不接續。");
            _prevOutBitDepth = outDepth;
            CountMetadata(Attr(attrs, "id"));
            CountMetadata(Attr(attrs, "name"));
            CountMetadata(Attr(attrs, "xml:lang"));
            return (inScale, outScale);
        }

        private static double BitDepthScale(string node, string attr, string depth) => depth switch
        {
            "8i" => 255.0,
            "10i" => 1023.0,
            "12i" => 4095.0,
            "16i" => 65535.0,
            "16f" => 1.0,
            "32f" => 1.0,
            _ => throw Fail($"{node} 的 {attr} 「{depth}」不是 8i/10i/12i/16i/16f/32f。"),
        };

        // ---------- Matrix ----------

        private void ParseMatrix()
        {
            var attrs = ReadAttributes();
            var (inScale, outScale) = ReadNodeHeader(attrs, "Matrix");
            var offset = _data.Count;
            var found = false;
            var cols = 0;
            foreach (var (kind, name) in NodeChildren("Matrix"))
            {
                if (kind == NodeKind.Description) continue;
                if (name != "Array") throw Fail($"Matrix 不接受子元素 「{name}」。");
                if (found) throw Fail("Matrix 只能有一個 Array。");
                found = true;
                var dims = ParseDims("Matrix");
                cols = dims.Length switch
                {
                    2 when dims[0] == 3 && dims[1] is 3 or 4 => dims[1],
                    3 when dims[0] == 3 && dims[1] is 3 or 4 && dims[2] == 3 => dims[1],
                    _ => throw Fail($"Matrix 的 dim 「{string.Join(' ', dims)}」必須是 3 3、3 4、3 3 3 或 3 4 3。"),
                };
                var expected = checked(3 * cols);
                ReserveData(12); // 不管幾欄，正規化後都佔 12 個 float
                BeginTokens(expected);
                ReadArrayText();
                if (_tokensEmitted != expected)
                    throw Fail($"Matrix 需要 {expected} 個數值，卻有 {_tokensEmitted} 個。");
            }
            if (!found) throw Fail("Matrix 缺少 Array。");
            if (cols == 3)
            {
                // 3×3 展開成固定 3×4 版面：每列第四欄 offset 為 0。
                _data.Insert(offset + 3, 0f);
                _data.Insert(offset + 7, 0f);
                _data.Add(0f);
            }
            var coeffScale = inScale / outScale;
            var offsetScale = 1.0 / outScale;
            for (var i = 0; i < 12; i++)
                _data[offset + i] = (float)(_data[offset + i] * (i % 4 == 3 ? offsetScale : coeffScale));
            _ops.Add(ColorTransformOp.Matrix(offset, 12));
        }

        // ---------- Range ----------

        private void ParseRange()
        {
            var attrs = ReadAttributes();
            var clamp = Attr(attrs, "style") switch
            {
                null or "Clamp" => true,
                "noClamp" => false,
                var other => throw Fail($"Range 的 style 「{other}」未知。"),
            };
            var (inScale, outScale) = ReadNodeHeader(attrs, "Range", "style");
            var bounds = new double?[4]; // minIn, maxIn, minOut, maxOut
            foreach (var (kind, name) in NodeChildren("Range"))
            {
                if (kind == NodeKind.Description) continue;
                var i = name switch
                {
                    "minInValue" => 0,
                    "maxInValue" => 1,
                    "minOutValue" => 2,
                    "maxOutValue" => 3,
                    _ => throw Fail($"Range 不接受子元素 「{name}」。"),
                };
                if (bounds[i] != null)
                    throw Fail($"Range 的 {name} 重複。");
                bounds[i] = ReadNumberElement(name) / (i < 2 ? inScale : outScale);
            }
            var hasMin = bounds[0] != null || bounds[2] != null;
            var hasMax = bounds[1] != null || bounds[3] != null;
            if (bounds[0] == null != (bounds[2] == null) || bounds[1] == null != (bounds[3] == null))
                throw Fail("Range 的 min/max bounds 必須成對出現。");
            var offset = _data.Count;
            ReserveData(4);
            for (var i = 0; i < 4; i++)
            {
                var value = (float)(bounds[i] ?? 0.0);
                if (!float.IsFinite(value)) throw Fail("Range bound 非有限值。");
                _data.Add(value);
            }
            _ops.Add(ColorTransformOp.Range(offset, 4, hasMin, hasMax, clamp));
        }

        // ---------- LUT1D / LUT3D ----------

        private void ParseLut1D()
        {
            var attrs = ReadAttributes();
            var interpolation = Attr(attrs, "interpolation");
            if (interpolation != null && interpolation != "linear")
                throw Fail($"LUT1D 的 interpolation 「{interpolation}」不是 linear。");
            var (_, outScale) = ReadNodeHeader(attrs, "LUT1D", "interpolation");
            var offset = _data.Count;
            var found = false;
            foreach (var (kind, name) in NodeChildren("LUT1D"))
            {
                if (kind == NodeKind.Description) continue;
                if (name != "Array") throw Fail($"LUT1D 不接受子元素 「{name}」。");
                if (found) throw Fail("LUT1D 只能有一個 Array。");
                found = true;
                var dims = ParseDims("LUT1D");
                if (dims.Length != 2 || dims[0] is < 2 or > 65536 || dims[1] is not (1 or 3))
                    throw Fail($"LUT1D 的 dim 「{string.Join(' ', dims)}」必須是 N 1 或 N 3，N 為 2–65536。");
                var expected = checked(dims[0] * dims[1]);
                ReserveData(expected);
                BeginTokens(expected);
                ReadArrayText();
                if (_tokensEmitted != expected)
                    throw Fail($"LUT1D 需要 {expected} 個數值，卻有 {_tokensEmitted} 個。");
                for (var i = 0; i < expected; i++)
                    _data[offset + i] = (float)(_data[offset + i] / outScale);
                _ops.Add(ColorTransformOp.Lut1D(offset, expected, dims[0], dims[1]));
            }
            if (!found) throw Fail("LUT1D 缺少 Array。");
        }

        private void ParseLut3D()
        {
            var attrs = ReadAttributes();
            var tetrahedral = Attr(attrs, "interpolation") switch
            {
                null or "trilinear" => false,
                "tetrahedral" => true,
                var other => throw Fail($"LUT3D 的 interpolation 「{other}」未知。"),
            };
            var (_, outScale) = ReadNodeHeader(attrs, "LUT3D", "interpolation");
            var offset = _data.Count;
            var found = false;
            foreach (var (kind, name) in NodeChildren("LUT3D"))
            {
                if (kind == NodeKind.Description) continue;
                if (name != "Array") throw Fail($"LUT3D 不接受子元素 「{name}」。");
                if (found) throw Fail("LUT3D 只能有一個 Array。");
                found = true;
                var dims = ParseDims("LUT3D");
                if (dims.Length != 4 || dims[0] != dims[1] || dims[1] != dims[2]
                    || dims[0] is < 2 or > 128 || dims[3] != 3)
                    throw Fail($"LUT3D 的 dim 「{string.Join(' ', dims)}」必須是 N N N 3，N 為 2–128。");
                var expected = checked(dims[0] * dims[0] * dims[0] * 3);
                ReserveData(expected);
                BeginTokens(expected);
                ReadArrayText();
                if (_tokensEmitted != expected)
                    throw Fail($"LUT3D 需要 {expected} 個數值，卻有 {_tokensEmitted} 個。");
                for (var i = 0; i < expected; i++)
                    _data[offset + i] = (float)(_data[offset + i] / outScale);
                _ops.Add(ColorTransformOp.Lut3D(offset, expected, dims[0], tetrahedral));
            }
            if (!found) throw Fail("LUT3D 缺少 Array。");
        }

        // ---------- 節點子元素巡訪 ----------

        private enum NodeKind { Child, Description }

        /// <summary>
        /// 走訪目前節點的內容。Description 就地計入 metadata 後產生標記項目；
        /// 其他子元素產生給呼叫端，呼叫端讀完它（游標停在其 EndElement）再繼續巡訪。
        /// </summary>
        private IEnumerable<(NodeKind, string)> NodeChildren(string node)
        {
            if (reader.IsEmptyElement)
                yield break;
            var baseDepth = reader.Depth;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.EndElement when reader.Depth == baseDepth:
                        yield break;
                    case XmlNodeType.Comment:
                        ReadChunked(null, metadata: true);
                        break;
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        throw Fail($"{node} 內不接受 processing instruction。");
                    case XmlNodeType.Element:
                        CheckDepth();
                        CheckNoNamespace();
                        if (reader.Name == "Description")
                        {
                            ReadTextElement();
                            yield return (NodeKind.Description, "Description");
                        }
                        else
                        {
                            yield return (NodeKind.Child, reader.Name);
                        }
                        break;
                    default:
                        throw Fail($"{node} 內出現 {reader.NodeType}。");
                }
            }
            throw Fail($"{node} 未關閉。");
        }

        /// <summary>在 yield 回來時讀 Array 的數字 token；結束時游標停在其 EndElement。</summary>
        private void ReadArrayText()
        {
            if (reader.IsEmptyElement) return; // <Array/>：呼叫端以數量不符拒絕
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.EndElement:
                        FlushToken();
                        return;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        ReadNumericChunks();
                        break;
                    case XmlNodeType.Comment:
                        ReadChunked(null, metadata: true);
                        break;
                    default:
                        throw Fail($"Array 內出現 {reader.NodeType}。");
                }
            }
            throw Fail("Array 未關閉。");
        }

        private double ReadNumberElement(string name)
        {
            var text = ReadTextElement(metadata: false);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value))
                throw Fail($"{name} 的數值 「{text}」無法解析。");
            return value;
        }

        /// <summary>讀取有界元素文字；metadata 文字包含空白，運算數值不計入 metadata。</summary>
        private string ReadTextElement(bool metadata = true)
        {
            var attrs = ReadAttributes();
            RequireAttrs(attrs, "xml:lang");
            CountMetadata(Attr(attrs, "xml:lang"));
            var builder = new StringBuilder();
            if (reader.IsEmptyElement) return string.Empty;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.EndElement:
                        return builder.ToString();
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        ReadChunked(builder, metadata);
                        break;
                    case XmlNodeType.Comment:
                        ReadChunked(null, metadata: true);
                        break;
                    default:
                        throw Fail($"{reader.Name} 內不接受 {reader.NodeType}。");
                }
            }
            throw Fail("元素未關閉。");
        }

        /// <summary>Info 子樹是有界 opaque metadata：只計字數與深度，不產生節點、不觸發 I/O。</summary>
        private void SkipOpaqueSubtree()
        {
            var infoDepth = reader.Depth;
            CountAttributesAsMetadata();
            if (reader.IsEmptyElement)
                return;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        CheckDepth();
                        CountAttributesAsMetadata();
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.Comment:
                        ReadChunked(null, metadata: true);
                        break;
                    case XmlNodeType.ProcessingInstruction:
                        throw Fail("Info 內不接受 processing instruction。");
                    case XmlNodeType.EndElement:
                        if (reader.Depth == infoDepth) return;
                        break;
                }
            }
            throw Fail("Info 未關閉。");
        }

        // ---------- 數字 token ----------

        private void BeginTokens(int expected)
        {
            _pendingLength = 0;
            _tokensEmitted = 0;
            _tokenCapacity = expected;
        }

        private void FeedTokens(ReadOnlySpan<char> span)
        {
            var pos = 0;
            while (pos < span.Length)
            {
                if (_pendingLength == 0 && char.IsWhiteSpace(span[pos])) { pos++; continue; }
                var start = pos;
                while (pos < span.Length && !char.IsWhiteSpace(span[pos])) pos++;
                var fragment = span[start..pos];
                if (_pendingLength == 0 && pos < span.Length)
                {
                    EmitToken(fragment);
                    continue;
                }
                if (_pendingLength + fragment.Length > _pending.Length)
                    throw Fail("Array 數值 token 過長。");
                fragment.CopyTo(_pending.AsSpan(_pendingLength));
                _pendingLength += fragment.Length;
                if (pos < span.Length) FlushToken();
            }
        }

        private void FlushToken()
        {
            if (_pendingLength == 0) return;
            var length = _pendingLength;
            _pendingLength = 0;
            EmitToken(_pending.AsSpan(0, length));
        }

        private void EmitToken(ReadOnlySpan<char> token)
        {
            if (_tokensEmitted >= _tokenCapacity)
                throw Fail("Array 數值超過 dim 宣告的數量。");
            _tokensEmitted++;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw Fail($"數值 「{token.ToString()}」無法解析。");
            var f = (float)value;
            if (!float.IsFinite(f)) throw Fail($"數值 「{token.ToString()}」不是有限 float。");
            _data.Add(f);
        }

        // ---------- 屬性／維度／計量 ----------

        private Dictionary<string, string> ReadAttributes()
        {
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                    attrs[reader.Name] = reader.Value;
                reader.MoveToElement();
            }
            return attrs;
        }

        private static string? Attr(Dictionary<string, string> attrs, string name) =>
            attrs.TryGetValue(name, out var value) ? value : null;

        private static void RequireAttrs(Dictionary<string, string> attrs, params string[] allowed)
        {
            var set = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var name in attrs.Keys)
                if (!set.Contains(name))
                    throw Fail($"不支援的屬性 「{name}」。");
        }

        private int[] ParseDims(string node)
        {
            var attrs = ReadAttributes();
            RequireAttrs(attrs, "dim", "xml:lang");
            CountMetadata(Attr(attrs, "xml:lang"));
            var dim = Attr(attrs, "dim") ?? throw Fail($"{node} 的 Array 缺少 dim。");
            var parts = dim.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var dims = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out dims[i]))
                    throw Fail($"{node} 的 dim 「{dim}」含有非整數。");
            return dims;
        }

        private void ReserveData(int additional)
        {
            if (_dataFloats + additional > MaxDataFloats)
                throw Fail($"正規化資料超過 {ColorTransform.MaxDataBytes} bytes 上限。");
            _dataFloats += additional;
        }

        private void CheckDepth()
        {
            if (reader.Depth >= MaxDepth)
                throw Fail($"XML 深度超過上限 {MaxDepth}。");
        }

        private void CheckNoNamespace()
        {
            if (reader.NamespaceURI.Length != 0)
                throw Fail($"元素 「{reader.Name}」不允許 namespace。");
        }

        private void CountAttributesAsMetadata()
        {
            if (!reader.HasAttributes) return;
            while (reader.MoveToNextAttribute())
                ReadChunked(null, metadata: true);
            reader.MoveToElement();
        }

        private void CountMetadata(string? text) => CountMetadataLength(text?.Length ?? 0);

        private void CountMetadataLength(int count)
        {
            if (count > MaxMetadataChars - _metadataChars)
                throw Fail($"metadata 超過 {MaxMetadataChars} 字元上限。");
            _metadataChars += count;
        }

        private void ReadChunked(StringBuilder? output, bool metadata)
        {
            int count;
            while ((count = reader.ReadValueChunk(_chunk, 0, _chunk.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (metadata) CountMetadataLength(count);
                if (output != null)
                {
                    if (!metadata && output.Length + count > 4096)
                        throw Fail("數值過長。");
                    output.Append(_chunk, 0, count);
                }
            }
        }

        private void ReadNumericChunks()
        {
            int count;
            while ((count = reader.ReadValueChunk(_chunk, 0, _chunk.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FeedTokens(_chunk.AsSpan(0, count));
            }
        }

        private static void CheckDeclaredEncoding(string declaration)
        {
            const string key = "encoding";
            var index = declaration.IndexOf(key, StringComparison.Ordinal);
            if (index < 0) return;
            var eq = declaration.IndexOf('=', index + key.Length);
            if (eq < 0) throw Fail("XML 宣告的 encoding 格式錯誤。");
            var rest = declaration.AsSpan(eq + 1).TrimStart();
            if (rest.Length < 2 || (rest[0] != '"' && rest[0] != '\''))
                throw Fail("XML 宣告的 encoding 格式錯誤。");
            var quote = rest[0];
            var closing = rest.Slice(1).IndexOf(quote);
            if (closing < 0) throw Fail("XML 宣告的 encoding 格式錯誤。");
            var encoding = rest.Slice(1, closing).ToString();
            if (!string.Equals(encoding, "utf-8", StringComparison.OrdinalIgnoreCase))
                throw Fail($"CLF 必須是 UTF-8，宣告卻是 「{encoding}」。");
        }
    }
}
