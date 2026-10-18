// 移植自 jpeg-decoder 0.3.2（MIT OR Apache-2.0）`decoder.rs::decode_internal` 的標記迴圈
// （SOF/DHT/DQT/DRI/APP/SOS/EOI 分派）、`parser.rs`（parse_sof/parse_sos/parse_dqt/parse_dht/
// parse_dri/parse_app）、`decoder.rs::decode_planes`（漸進式未完成分量補算、lossless/DCT 組圖）、
// `determine_color_transform`（色彩判定）、`scale`＋`update_idct_size`（DCT 縮放）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Buffers.Binary;
using static FilmBatch.Raw.Codecs.JpegDecoderPort;

namespace FilmBatch.Raw.Codecs;

/// <summary>完整標記迴圈＋組圖（上游 `Decoder` 去泛型：記憶體游標）。</summary>
internal sealed class JpegFrameDecoder
{
    private readonly ReadOnlyMemory<byte> _src;
    private int _pos;

    private Component[] _comps = [];
    private CodingProcess _process;
    private bool _isBaseline;
    private bool _differential;
    private bool _arithmetic;
    private int _precision = 8;
    private int _imgW;
    private int _imgH;
    private int _outW;
    private int _outH;
    private int _mcuW;
    private int _mcuH;

    private readonly ushort[]?[] _qts = new ushort[]?[4];
    private readonly HuffTable?[] _dcHuff = new HuffTable?[4];
    private readonly HuffTable?[] _acHuff = new HuffTable?[4];
    private ushort _restartInterval;
    private AdobeTransform? _adobe;
    private bool _isJfif;
    private bool _isMjpeg;

    private short[][] _coeffs = [];
    private readonly ulong[] _coeffsFinished = new ulong[4];

    private byte[][] _planes = [];
    private ushort[][] _planesU16 = [];
    private int _pendingMarker = -1;
    private int _scansProcessed;

    public JpegFrameDecoder(ReadOnlyMemory<byte> src)
    {
        _src = src;
    }

    public JpegPreviewPixels Run(ushort reqW, ushort reqH, bool wantScale)
    {
        var span = _src.Span;
        if (span.Length < 2 || span[0] != 0xFF || span[1] != 0xD8)
            throw RawException.Corrupt("preview JPEG: missing SOI");
        _pos = 2;
        bool haveFrame = false;
        var worker = new JpegScanPort.RowWorker();
        var ctx = new JpegScanPort.ScanContext();
        int prevMarker = 0xD8;
        while (true)
        {
            int marker = _pendingMarker >= 0 ? _pendingMarker : ReadMarker();
            _pendingMarker = -1;
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (haveFrame)
                    throw RawException.Unsupported("preview JPEG: hierarchical JPEG");
                ParseSof(marker);
                haveFrame = true;
                if (wantScale && reqW > 0 && reqH > 0 && _process != CodingProcess.Lossless)
                {
                    int idct = JpegIdct.ChooseIdctSize(_imgW, _imgH, reqW, reqH);
                    UpdateIdctSize(idct);
                }
                _planes = new byte[_comps.Length][];
                _planesU16 = new ushort[_comps.Length][];
                for (int i = 0; i < _comps.Length; i++)
                {
                    _planes[i] = [];
                    _planesU16[i] = [];
                }
                SyncCtx(ctx);
                prevMarker = marker;
                continue;
            }
            switch (marker)
            {
                case 0xDA:
                {
                    if (!haveFrame)
                        throw RawException.Corrupt("preview JPEG: scan encountered before frame");
                    var scan = ParseSos();
                    SyncCtx(ctx);
                    if (_process == CodingProcess.DctProgressive && _coeffs.Length == 0)
                    {
                        _coeffs = new short[_comps.Length][];
                        for (int i = 0; i < _comps.Length; i++)
                        {
                            long n = (long)_comps[i].BlockW * _comps[i].BlockH * 64;
                            if (n > RawLimits.MaxSamples)
                                throw RawException.Limit("preview JPEG: image too large");
                            _coeffs[i] = new short[(int)n];
                        }
                    }
                    var br = new BitReader(_src.Span, _pos);
                    if (_process == CodingProcess.Lossless)
                    {
                        var (m, data) = JpegScanPort.DecodeScanLossless(ref br, ctx, scan, _imgW, _imgH, _precision);
                        _pos = br.Position;
                        for (int i = 0; i < data.Length; i++)
                        {
                            if (data[i].Length != 0)
                                _planesU16[scan.CompIdx[i]] = data[i];
                        }
                        _pendingMarker = m;
                    }
                    else
                    {
                        var finished = ComputeFinished(scan);
                        SyncCtx(ctx);
                        var comps = new Component[scan.CompIdx.Length];
                        for (int i = 0; i < comps.Length; i++)
                            comps[i] = _comps[scan.CompIdx[i]];
                        var (m, data) = JpegScanPort.DecodeScan(ref br, ctx, scan, comps, finished, worker);
                        _pos = br.Position;
                        if (data is not null)
                        {
                            for (int k = 0; k < scan.CompIdx.Length; k++)
                            {
                                // data 以框分量序排列（DecodeScan 尾段语义），data[CompIdx[k]] 对应 scan.CompIdx[k]。
                                int fi = scan.CompIdx[k];
                                if (data[fi].Length != 0)
                                    _planes[fi] = data[fi];
                            }
                        }
                        _pendingMarker = m;
                    }
                    _scansProcessed++;
                    prevMarker = marker;
                    break;
                }
                case 0xC4:
                {
                    var (dc, ac) = ParseDht();
                    for (int i = 0; i < 4; i++)
                    {
                        if (dc[i] is not null)
                            _dcHuff[i] = dc[i];
                        if (ac[i] is not null)
                            _acHuff[i] = ac[i];
                    }
                    SyncCtx(ctx);
                    prevMarker = marker;
                    break;
                }
                case 0xCC:
                    throw RawException.Unsupported("preview JPEG: arithmetic entropy coding");
                case 0xDB:
                    ParseDqt();
                    SyncCtx(ctx);
                    prevMarker = marker;
                    break;
                case 0xDD:
                    _restartInterval = ParseDri();
                    SyncCtx(ctx);
                    prevMarker = marker;
                    break;
                case 0xFE:
                    ParseCom();
                    prevMarker = marker;
                    break;
                case >= 0xE0 and <= 0xEF:
                    ParseApp(marker);
                    SyncCtx(ctx);
                    prevMarker = marker;
                    break;
                case >= 0xD0 and <= 0xD7:
                    // 上游：熵編碼外的 RST 只有前一 marker 為 SOS 才忽略（掃描尾多餘 RST）；
                    // 其餘視為格式錯誤。
                    if (prevMarker != 0xDA)
                        throw RawException.Corrupt("preview JPEG: RST found outside of entropy-coded data");
                    prevMarker = marker;
                    break;
                case 0xDC:
                    // 上游：DNL 只允許緊跟首掃描，否則格式錯誤；支援一律不支援。
                    if (prevMarker != 0xDA || _scansProcessed != 1)
                        throw RawException.Corrupt("preview JPEG: DNL is only allowed immediately after the first scan");
                    throw RawException.Unsupported("preview JPEG: DNL");
                case 0xDE:
                case 0xDF:
                    throw RawException.Unsupported("preview JPEG: hierarchical JPEG");
                case 0xD9:
                    goto Done;
                case 0xD8:
                case 0x01:
                    // SOI/TEM：無長度，忽略（上游 read_marker 不會回傳；此處游標已對齊）。
                    prevMarker = marker;
                    break;
                default:
                    throw RawException.Corrupt($"preview JPEG: marker 0xFF{marker:X2} found where not allowed");
            }
        }
    Done:
        if (!haveFrame)
            throw RawException.Corrupt("preview JPEG: end of image encountered before frame");
        SyncCtx(ctx);
        return Finish(ctx, worker);
    }

    private void SyncCtx(JpegScanPort.ScanContext ctx)
    {
        ctx.FrameComps = _comps;
        ctx.Process = _process;
        ctx.ImageW = _imgW;
        ctx.ImageH = _imgH;
        ctx.McuW = _mcuW;
        ctx.McuH = _mcuH;
        ctx.RestartInterval = _restartInterval;
        ctx.IsMjpeg = _isMjpeg;
        for (int i = 0; i < 4; i++)
        {
            ctx.Qts[i] = _qts[i];
            ctx.DcHuff[i] = _dcHuff[i];
            ctx.AcHuff[i] = _acHuff[i];
        }
        ctx.Coeffs = _coeffs;
        for (int i = 0; i < 4; i++)
            ctx.CoeffsFinished[i] = _coeffsFinished[i];
    }

    private bool[] ComputeFinished(ScanInfo scan)
    {
        var finished = new bool[scan.CompIdx.Length];
        if (_process == CodingProcess.DctProgressive)
        {
            // 上游 decode_internal 的完成追蹤（Al==0 才推進；全 64 係數到齊才完成）。
            if (scan.SuccLow == 0)
            {
                for (int k = 0; k < scan.CompIdx.Length; k++)
                {
                    int i = scan.CompIdx[k];
                    if (_coeffsFinished[i] == ulong.MaxValue)
                        continue;
                    for (int j = scan.SpecStart; j < scan.SpecEnd; j++)
                        _coeffsFinished[i] |= 1UL << j;
                    if (_coeffsFinished[i] == ulong.MaxValue)
                        finished[k] = true;
                }
            }
        }
        else
        {
            for (int k = 0; k < finished.Length; k++)
                finished[k] = true;
        }
        return finished;
    }

    private JpegPreviewPixels Finish(JpegScanPort.ScanContext ctx, JpegScanPort.RowWorker worker)
    {
        long area = (long)_outW * _outH * _comps.Length;
        if (area > (long)RawLimits.MaxSamples * Math.Max(_comps.Length, 1))
            throw RawException.Limit("preview JPEG: image too large");
        if (_process == CodingProcess.Lossless)
        {
            foreach (var p in _planesU16)
            {
                if (p.Length == 0)
                    throw RawException.Corrupt("preview JPEG: not all components have data");
            }
            byte[] raw = JpegUpSample.FinishLossless(_planesU16, _outW, _outH, _precision);
            return BuildPixels(raw);
        }
        // 漸進式未完成分量：以上游 decode_planes 語意補算（worker 即時 IDCT）。
        if (_process == CodingProcess.DctProgressive && _coeffs.Length == _comps.Length)
        {
            for (int i = 0; i < _comps.Length; i++)
            {
                if (_coeffsFinished[i] == ulong.MaxValue)
                    continue;
                var c = _comps[i];
                var qt = _qts[c.Tq];
                if (qt is null)
                    continue;
                worker.Start(i, c, qt);
                int perRow = c.BlockW * c.V * 64;
                for (int my = 0; my < _mcuH; my++)
                {
                    int off = my * perRow;
                    var row = new short[perRow];
                    Array.Copy(_coeffs[i], off, row, 0, perRow);
                    worker.AppendRow(i, row);
                }
                _planes[i] = worker.Take(i);
            }
        }
        foreach (var p in _planes)
        {
            if (p.Length == 0)
                throw RawException.Corrupt("preview JPEG: not all components have data");
        }
        if (_comps.Length == 1)
        {
            var c = _comps[0];
            int stride = c.BlockW * c.DctScale;
            byte[] g = JpegUpSample.FinishGray(_planes[0], c.Width, c.Height, stride);
            if (g.Length < (long)_outW * _outH)
                throw RawException.Corrupt("preview JPEG: short output");
            return new JpegPreviewPixels(_outW, _outH, 1, g, null);
        }
        var ct = DetermineColorTransform();
        var planes = JpegUpSample.Build(_comps, (ushort)_outW, (ushort)_outH);
        int lineSize = _outW * _comps.Length;
        if ((long)lineSize * _outH > RawLimits.MaxSamples)
            throw RawException.Limit("preview JPEG: image too large");
        var image = new byte[lineSize * _outH];
        int bufSize = JpegUpSample.LineBufferSize(_comps);
        var lines = new byte[_comps.Length][];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = new byte[bufSize];
        for (int row = 0; row < _outH; row++)
        {
            var line = new byte[lineSize];
            JpegUpSample.UpsampleRow(planes, _planes, row, _outW, lines, line, ct);
            Array.Copy(line, 0, image, row * lineSize, lineSize);
        }
        return new JpegPreviewPixels(_outW, _outH, _comps.Length, image, null);
    }

    private JpegPreviewPixels BuildPixels(byte[] raw)
    {
        // 上游 decode_jpeg_decoder 的模型／緩衝對應（含 precision>8 的 U16 左對齊、
        // precision<=8 多分量 lossless 取原生位元組的原樣行為）。
        if (_comps.Length == 1)
        {
            if (_precision >= 9)
            {
                var u16 = new ushort[raw.Length / 2];
                for (int i = 0; i < u16.Length; i++)
                {
                    ushort v = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2, 2));
                    u16[i] = unchecked((ushort)(v << (16 - _precision)));
                }
                if (u16.Length < (long)_outW * _outH)
                    throw RawException.Corrupt("preview JPEG: short output");
                return new JpegPreviewPixels(_outW, _outH, 1, null, u16);
            }
            if (raw.Length < (long)_outW * _outH)
                throw RawException.Corrupt("preview JPEG: short output");
            return new JpegPreviewPixels(_outW, _outH, 1, raw, null);
        }
        if (_precision > 8)
        {
            var u16 = new ushort[raw.Length / 2];
            for (int i = 0; i < u16.Length; i++)
            {
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2, 2));
                u16[i] = unchecked((ushort)(v << (16 - _precision)));
            }
            if (u16.Length < (long)_outW * _outH * _comps.Length)
                throw RawException.Corrupt("preview JPEG: short output");
            return new JpegPreviewPixels(_outW, _outH, _comps.Length, null, u16);
        }
        if (raw.Length < (long)_outW * _outH * _comps.Length)
            throw RawException.Corrupt("preview JPEG: short output");
        return new JpegPreviewPixels(_outW, _outH, _comps.Length, raw, null);
    }

    private ColorTransform DetermineColorTransform()
    {
        if (_comps.Length == 1)
            return ColorTransform.Grayscale;
        if (_comps.Length == 3)
        {
            if (_comps[0].Id == 1 && _comps[1].Id == 2 && _comps[2].Id == 3)
                return ColorTransform.YCbCr;
            if (!(_comps[0].Id == 1 && _comps[1].Id == 34 && _comps[2].Id == 35)
                && !(_comps[0].Id == 82 && _comps[1].Id == 71 && _comps[2].Id == 66)
                && !(_comps[0].Id == 114 && _comps[1].Id == 103 && _comps[2].Id == 98)
                && _isJfif)
                return ColorTransform.YCbCr;
            if (_comps[0].Id == 82 && _comps[1].Id == 71 && _comps[2].Id == 66)
                return ColorTransform.Rgb;
            if ((_comps[0].Id == 1 && _comps[1].Id == 34 && _comps[2].Id == 35)
                || (_comps[0].Id == 114 && _comps[1].Id == 103 && _comps[2].Id == 98))
                throw RawException.Unsupported("preview JPEG: big-gamut JPEG colour transform");
        }
        if (_adobe.HasValue)
        {
            switch (_adobe.Value)
            {
                case AdobeTransform.Unknown:
                    if (_comps.Length == 3)
                        return ColorTransform.Rgb;
                    if (_comps.Length == 4)
                        return ColorTransform.Cmyk;
                    break;
                case AdobeTransform.YCbCr:
                    return ColorTransform.YCbCr;
                case AdobeTransform.YccK:
                    return ColorTransform.YccK;
            }
        }
        else if (_comps.Length == 4)
        {
            return ColorTransform.Cmyk;
        }
        if (_comps.Length == 4)
            return ColorTransform.YccK;
        if (_comps.Length == 3)
            return ColorTransform.YCbCr;
        throw RawException.Corrupt("preview JPEG: unknown colour transform");
    }

    private void UpdateIdctSize(int idct)
    {
        foreach (var c in _comps)
            c.DctScale = idct;
        UpdateComponentSizes();
        _outW = (_imgW * idct + 7) / 8; // 上游 ceil(w*s/8)（f32 ceil；整數語意一致）
        _outH = (_imgH * idct + 7) / 8;
    }

    // ---- 游標＋標記 ----

    private byte ReadU8()
    {
        var span = _src.Span;
        if ((uint)_pos >= (uint)span.Length)
            throw RawException.Corrupt("preview JPEG: truncated stream");
        return span[_pos++];
    }

    private ushort ReadU16()
    {
        var span = _src.Span;
        if ((uint)_pos + 1 >= (uint)span.Length)
            throw RawException.Corrupt("preview JPEG: truncated stream");
        ushort v = (ushort)((span[_pos] << 8) | span[_pos + 1]);
        _pos += 2;
        return v;
    }

    private void Skip(int n)
    {
        if (n < 0 || (uint)(_pos + n) > (uint)_src.Span.Length)
            throw RawException.Corrupt("preview JPEG: truncated stream");
        _pos += n;
    }

    private int ReadMarker()
    {
        var span = _src.Span;
        while (true)
        {
            // 上游 read_marker：容忍段間垃圾（libjpeg 行為；Sony Ericsson P990i 前例）。
            if ((uint)_pos >= (uint)span.Length)
                throw RawException.Corrupt("preview JPEG: truncated stream");
            while (span[_pos] != 0xFF)
            {
                _pos++;
                if ((uint)_pos >= (uint)span.Length)
                    throw RawException.Corrupt("preview JPEG: truncated stream");
            }
            _pos++;
            if ((uint)_pos >= (uint)span.Length)
                throw RawException.Corrupt("preview JPEG: truncated stream");
            byte b = span[_pos++];
            while (b == 0xFF)
            {
                if ((uint)_pos >= (uint)span.Length)
                    throw RawException.Corrupt("preview JPEG: truncated stream");
                b = span[_pos++];
            }
            if (b != 0x00 && b != 0xFF)
                return b;
        }
    }

    private int ReadLength()
    {
        int len = ReadU16();
        if (len < 2)
            throw RawException.Corrupt("preview JPEG: bad marker length");
        return len - 2;
    }

    // ---- 段解析 ----

    private void ParseSof(int marker)
    {
        int sof = marker - 0xC0;
        int length = ReadLength();
        // 上游：length（已扣自身 2 位元組）<= 6 即錯。
        if (length <= 6)
            throw RawException.Corrupt("preview JPEG: invalid length in SOF");
        _isBaseline = sof == 0;
        _differential = (sof >= 5 && sof <= 7) || sof >= 13;
        _arithmetic = (sof >= 9 && sof <= 11) || sof >= 13;
        _process = sof switch
        {
            0 or 1 or 5 or 9 or 13 => CodingProcess.DctSequential,
            2 or 6 or 10 or 14 => CodingProcess.DctProgressive,
            _ => CodingProcess.Lossless,
        };
        if (_differential)
            throw RawException.Unsupported("preview JPEG: differential/hierarchical JPEG");
        if (_arithmetic)
            throw RawException.Unsupported("preview JPEG: arithmetic entropy coding");
        int prec = ReadU8();
        if (prec == 8)
        {
        }
        else if (prec == 12)
        {
            if (_isBaseline)
                throw RawException.Corrupt("preview JPEG: 12 bit sample precision is not allowed in baseline");
            if (_process != CodingProcess.Lossless)
                throw RawException.Unsupported("preview JPEG: 12-bit DCT JPEG");
        }
        else if (_process != CodingProcess.Lossless || prec > 16 || prec < 1)
        {
            throw RawException.Corrupt($"preview JPEG: invalid precision {prec} in frame header");
        }
        int h = ReadU16();
        int w = ReadU16();
        if (h == 0)
            throw RawException.Unsupported("preview JPEG: DNL");
        if (w == 0)
            throw RawException.Corrupt("preview JPEG: zero width in frame header");
        int n = ReadU8();
        if (n == 0)
            throw RawException.Corrupt("preview JPEG: zero component count in frame header");
        if (_process == CodingProcess.DctProgressive && n > 4)
            throw RawException.Corrupt("preview JPEG: progressive frame with more than 4 components");
        if (n != 1 && n != 3 && n != 4)
            throw RawException.Unsupported($"preview JPEG: {n} components");
        // 上游 length（已扣自身 2 位元組）== 6 + 3*n。
        if (length != 6 + 3 * n)
            throw RawException.Corrupt("preview JPEG: invalid length in SOF");
        _precision = prec;
        _comps = new Component[n];
        for (int i = 0; i < n; i++)
        {
            byte id = ReadU8();
            for (int k = 0; k < i; k++)
            {
                if (_comps[k].Id == id)
                    throw RawException.Corrupt($"preview JPEG: duplicate frame component identifier {id}");
            }
            byte hv = ReadU8();
            byte hh = (byte)(hv >> 4), vv = (byte)(hv & 0x0F);
            if (hh == 0 || hh > 4)
                throw RawException.Corrupt($"preview JPEG: invalid horizontal sampling factor {hh}");
            if (vv == 0 || vv > 4)
                throw RawException.Corrupt($"preview JPEG: invalid vertical sampling factor {vv}");
            int tq = ReadU8();
            if (tq > 3 || (_process == CodingProcess.Lossless && tq != 0))
                throw RawException.Corrupt($"preview JPEG: invalid quantization table index {tq}");
            _comps[i] = new Component { Id = id, H = hh, V = vv, Tq = tq };
        }
        _imgW = w;
        _imgH = h;
        _outW = w;
        _outH = h;
        UpdateComponentSizes();
        // 取樣比檢查（上游 SOF 後的 Upsampler::new；DCT 縮放前全尺寸檢查）。
        JpegUpSample.Build(_comps, _outW > 65535 ? 65535 : _outW, _outH > 65535 ? 65535 : _outH);
    }

    private void UpdateComponentSizes()
    {
        int hMax = 0, vMax = 0;
        foreach (var c in _comps)
        {
            hMax = Math.Max(hMax, c.H);
            vMax = Math.Max(vMax, c.V);
        }
        _mcuW = CeilDiv((uint)_imgW, (uint)(hMax * 8));
        _mcuH = CeilDiv((uint)_imgH, (uint)(vMax * 8));
        foreach (var c in _comps)
        {
            c.Width = CeilDiv((uint)_imgW * c.H * (uint)c.DctScale, (uint)(hMax * 8));
            c.Height = CeilDiv((uint)_imgH * c.V * (uint)c.DctScale, (uint)(vMax * 8));
            c.BlockW = _mcuW * c.H;
            c.BlockH = _mcuH * c.V;
        }
    }

    private static int CeilDiv(uint x, uint y)
    {
        if (x == 0 || y == 0)
            throw RawException.Corrupt("preview JPEG: invalid dimensions");
        return (int)(1 + ((x - 1) / y));
    }

    private ScanInfo ParseSos()
    {
        int length = ReadLength();
        if (length == 0)
            throw RawException.Corrupt("preview JPEG: zero length in SOS");
        int n = ReadU8();
        if (n == 0 || n > 4)
            throw RawException.Corrupt($"preview JPEG: invalid component count {n} in scan header");
        // 上游 length（已扣自身 2 位元組）== 4 + 2*n。
        if (length != 4 + 2 * n)
            throw RawException.Corrupt("preview JPEG: invalid length in SOS");
        var scan = new ScanInfo
        {
            CompIdx = new int[n],
            DcTbl = new int[n],
            AcTbl = new int[n],
        };
        int maxSeen = -1;
        var seen = new bool[_comps.Length];
        for (int i = 0; i < n; i++)
        {
            byte id = ReadU8();
            int ci = -1;
            for (int k = 0; k < _comps.Length; k++)
            {
                if (_comps[k].Id == id)
                {
                    ci = k;
                    break;
                }
            }
            if (ci < 0)
                throw RawException.Corrupt($"preview JPEG: scan component identifier {id} does not match any frame component");
            if (seen[ci])
                throw RawException.Corrupt($"preview JPEG: duplicate scan component identifier {id}");
            seen[ci] = true;
            if (ci < maxSeen)
                throw RawException.Corrupt("preview JPEG: scan component order does not follow frame order");
            maxSeen = Math.Max(maxSeen, ci);
            byte tbl = ReadU8();
            int dc = tbl >> 4, ac = tbl & 0x0F;
            if (dc > 3 || (_isBaseline && dc > 1))
                throw RawException.Corrupt($"preview JPEG: invalid dc table index {dc}");
            if (ac > 3 || (_isBaseline && ac > 1))
                throw RawException.Corrupt($"preview JPEG: invalid ac table index {ac}");
            scan.CompIdx[i] = ci;
            scan.DcTbl[i] = dc;
            scan.AcTbl[i] = ac;
        }
        int blocksPerMcu = 0;
        foreach (int i in scan.CompIdx)
            blocksPerMcu += _comps[i].H * _comps[i].V;
        if (n > 1 && blocksPerMcu > 10)
            throw RawException.Corrupt("preview JPEG: scan with more than 10 blocks per MCU");
        int ss = ReadU8();
        int se = ReadU8();
        byte ab = ReadU8();
        int ah = ab >> 4, al = ab & 0x0F;
        if (al >= _precision)
            throw RawException.Corrupt("preview JPEG: invalid point transform");
        scan.SuccHigh = ah;
        scan.SuccLow = al;
        scan.PointTransform = al;
        if (_process == CodingProcess.DctProgressive)
        {
            scan.Predictor = 0;
            if (se > 63 || ss > se || (ss == 0 && se != 0))
                throw RawException.Corrupt($"preview JPEG: invalid spectral selection ss={ss} se={se}");
            if (ss != 0 && n != 1)
                throw RawException.Corrupt("preview JPEG: AC spectral scan with more than one component");
            if (ah > 13 || al > 13)
                throw RawException.Corrupt($"preview JPEG: invalid successive approximation ah={ah} al={al}");
            if (ah != 0 && ah != al + 1)
                throw RawException.Corrupt("preview JPEG: successive approximation improves more than one bit");
        }
        else if (_process == CodingProcess.Lossless)
        {
            if (se != 0)
                throw RawException.Corrupt("preview JPEG: spectral selection end shall be zero in lossless scan");
            if (ah != 0)
                throw RawException.Corrupt("preview JPEG: successive approximation high shall be zero in lossless scan");
            scan.Predictor = ss switch
            {
                <= 7 => ss,
                _ => throw RawException.Corrupt($"preview JPEG: invalid predictor selection {ss}"),
            };
        }
        else
        {
            scan.Predictor = 0;
            if (se == 0)
                se = 63;
            if (ss != 0 || se != 63)
                throw RawException.Corrupt("preview JPEG: spectral selection not allowed in sequential scan");
            if (ah != 0 || al != 0)
                throw RawException.Corrupt("preview JPEG: successive approximation not allowed in sequential scan");
        }
        scan.SpecStart = ss;
        scan.SpecEnd = se + 1;
        return scan;
    }

    private void ParseDqt()
    {
        int length = ReadLength();
        while (length > 0)
        {
            byte info = ReadU8();
            int prec = info >> 4, idx = info & 0x0F;
            if (prec > 1)
                throw RawException.Corrupt($"preview JPEG: invalid precision {prec} in DQT");
            if (idx > 3)
                throw RawException.Corrupt($"preview JPEG: invalid destination identifier {idx} in DQT");
            if (length < 65 + 64 * prec)
                throw RawException.Corrupt("preview JPEG: invalid length in DQT");
            // 上游 parse_dqt：段內為 zigzag 序，存成自然序（unzigzagged_table[UNZIGZAG[j]] = table[j]）。
            var t = new ushort[64];
            for (int j = 0; j < 64; j++)
                t[JpegScanPort.UnZigZag[j]] = prec == 0 ? ReadU8() : ReadU16();
            foreach (ushort v in t)
            {
                if (v == 0)
                    throw RawException.Corrupt("preview JPEG: quantization table contains zero value");
            }
            _qts[idx] = t;
            length -= 65 + 64 * prec;
        }
    }

    private (HuffTable?[] Dc, HuffTable?[] Ac) ParseDht()
    {
        int length = ReadLength();
        var dc = new HuffTable?[4];
        var ac = new HuffTable?[4];
        while (length > 17)
        {
            byte info = ReadU8();
            int cls = info >> 4, idx = info & 0x0F;
            if (cls != 0 && cls != 1)
                throw RawException.Corrupt($"preview JPEG: invalid class {cls} in DHT");
            if (_isBaseline && idx > 1)
                throw RawException.Corrupt("preview JPEG: baseline allows at most two Huffman tables per class");
            if (idx > 3)
                throw RawException.Corrupt($"preview JPEG: invalid destination identifier {idx} in DHT");
            var counts = new byte[16];
            for (int i = 0; i < 16; i++)
                counts[i] = ReadU8();
            int size = 0;
            foreach (byte c in counts)
                size += c;
            if (size == 0)
                throw RawException.Corrupt("preview JPEG: zero-length table in DHT");
            if (size > 256)
                throw RawException.Corrupt("preview JPEG: excessive-length table in DHT");
            if (size > length - 17)
                throw RawException.Corrupt("preview JPEG: invalid length in DHT");
            var values = new byte[size];
            // 上游 DHT 重複定義覆蓋（此處直接建表；重複段後出現者勝，由呼叫端 or-merge）。
            var seenVals = new bool[256];
            for (int i = 0; i < size; i++)
                values[i] = ReadU8();
            var fullBits = new byte[16];
            Array.Copy(counts, fullBits, 16);
            var table = HuffTable.Create(Prefixed(fullBits), values, cls == 0);
            if (cls == 0)
                dc[idx] = table;
            else
                ac[idx] = table;
            length -= 17 + size;
        }
        if (length != 0)
            throw RawException.Corrupt("preview JPEG: invalid length in DHT");
        return (dc, ac);
    }

    private static byte[] Prefixed(byte[] counts16)
    {
        var b = new byte[16];
        Array.Copy(counts16, b, 16);
        return b;
    }

    private ushort ParseDri()
    {
        int length = ReadLength();
        if (length != 2)
            throw RawException.Corrupt("preview JPEG: DRI with invalid length");
        return ReadU16();
    }

    private void ParseCom()
    {
        int length = ReadLength();
        Skip(length);
    }

    private void ParseApp(int marker)
    {
        int length = ReadLength();
        int read = 0;
        var span = _src.Span;
        switch (marker)
        {
            case 0xE0:
                if (length >= 5 && _pos + 5 <= span.Length)
                {
                    bool jfif = span[_pos] == (byte)'J' && span[_pos + 1] == (byte)'F'
                        && span[_pos + 2] == (byte)'I' && span[_pos + 3] == (byte)'F' && span[_pos + 4] == 0;
                    bool avi1 = span[_pos] == (byte)'A' && span[_pos + 1] == (byte)'V'
                        && span[_pos + 2] == (byte)'I' && span[_pos + 3] == (byte)'1' && span[_pos + 4] == 0;
                    if (jfif)
                        _isJfif = true;
                    if (avi1)
                        _isMjpeg = true;
                    Skip(5);
                    read = 5;
                }
                Skip(length - read);
                break;
            case 0xEE:
                if (length >= 12 && _pos + 12 <= span.Length
                    && span[_pos] == (byte)'A' && span[_pos + 1] == (byte)'d' && span[_pos + 2] == (byte)'o'
                    && span[_pos + 3] == (byte)'b' && span[_pos + 4] == (byte)'e' && span[_pos + 5] == 0)
                {
                    byte tr = span[_pos + 11];
                    _adobe = tr switch
                    {
                        0 => AdobeTransform.Unknown,
                        1 => AdobeTransform.YCbCr,
                        2 => AdobeTransform.YccK,
                        _ => throw RawException.Corrupt("preview JPEG: invalid color transform in Adobe segment"),
                    };
                }
                Skip(length);
                break;
            default:
                Skip(length);
                break;
        }
    }
}
