// 移植自 LightCraft crates/raw/src/vendor/arw_lens.rs（Sony 機內鏡頭校正記錄：raw IFD 欄位＋加密 SR2SubIFD 複本的
// 有界解碼 → SonyLensCorrections；唯一驗證過的轉換：ILCE-7RM4A 畸變表 → DNG warp，走共用光學管線）。
//
// 標籤身份／版面：ExifTool EXIF／Sony 文件（raw-IFD 0x7031／0x7032 shading、0x7034／0x7035 橫向 CA、
// 0x7036／0x7037 畸變；SR2SubIFD 表 0x797d、0x7980、0x7982；其旁設定 0x797c、0x797f、0x7981 在文件中無名，
// 由檔案檢視確認等於 raw-IFD 設定）。幾何由檔案與 Sony Imaging Edge 匯出獨立推得；見 docs/sony-lens-corrections.md。
// 無鏡頭資料庫、無外部解碼器程式碼。MakerNote 同名表格尺度／版面不同，永不讀取。
//
// raw-IFD 欄位即 TIFF 讀取器所解析者（影像所用：首見、讀取器上限內）。
// 目錄另行掃描，僅作診斷：標籤是否重複、讀取器漏掉的條目為何丟棄。
// SR2SubIFD 欄位取其首條目。只接受精確版面：設定 SHORT[1]（或非負 SSHORT[1]）、
// shading／畸變 SSHORT[17]（首值 16）、CA SSHORT[33]（首值 32）。不以檔案 count 分配。
// raw IFD 複本優先；SR2SubIFD 僅在 raw IFD 缺標籤時用；raw-IFD 複本損毀只回報，不靜默取代。
// 解碼不授權任何事：只有模型＋模式獨立驗證過的元件才轉換（SonyLensStatus），
// 轉換吃進與此記錄存在前解碼器所套用完全相同的 raw IFD 表格。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Raw.Vendors.Sony;

/// <summary>
/// Sony ARW 機內鏡頭校正（上游 <c>arw_lens::corrections</c>）：
/// 檔案記錄解碼＋驗證過的畸變 warp。<paramref name="raw"/> 為 TIFF 讀取器解析的 raw 影像 IFD；
/// <paramref name="sr2"/> 為解密的 SR2SubIFD（無時為 null）。兩目錄皆無六欄位時記錄為 null。
/// </summary>
internal static class SonyLens
{
    private const ushort ShadingSetting = 0x7031;
    private const ushort ShadingTable = 0x7032;
    private const ushort CaSetting = 0x7034;
    private const ushort CaTable = 0x7035;
    private const ushort DistortionSetting = 0x7036;
    private const ushort DistortionTable = 0x7037;
    private const ushort Sr2ShadingSetting = 0x797c;
    private const ushort Sr2ShadingTable = 0x797d;
    private const ushort Sr2CaSetting = 0x797f;
    private const ushort Sr2CaTable = 0x7980;
    private const ushort Sr2DistortionSetting = 0x7981;
    private const ushort Sr2DistortionTable = 0x7982;

    private const ushort ShortType = 3;
    private const ushort SShortType = 8;

    /// <summary>raw-IFD 畸變表獨立驗證過的相機。精確名稱：ILCE-7RM4 與 ILCE-7RM4A 不同，不可前綴比對。</summary>
    internal static readonly string[] DistortionModels = ["ILCE-7RM4A"];

    /// <summary>掃描的最大目錄條目數：classic TIFF 目錄的最大 count，亦約束 BigTIFF。</summary>
    private const int MaxEntries = 1 << 16;

    private const int WarpSamples = 1024;

    private const double MaxWarpError = 0.0005;

    internal enum Kind { Shading, Ca, Distortion }

    /// <summary>設定值的文件意義（ExifTool EXIF tag-name 文件），整值比對。</summary>
    internal static SonySettingMeaning MeaningOf(Kind kind, ushort value) => (kind, value) switch
    {
        (Kind.Shading, 256) or (Kind.Ca or Kind.Distortion, 0) => SonySettingMeaning.Off,
        (Kind.Shading, 257 or 272) or (Kind.Ca or Kind.Distortion, 1) => SonySettingMeaning.Auto,
        (Kind.Distortion, 17) => SonySettingMeaning.ForcedAuto,
        (Kind.Shading, 511) or (Kind.Ca or Kind.Distortion, 255) => SonySettingMeaning.Unavailable,
        _ => SonySettingMeaning.Unknown,
    };

    /// <summary>矩形是否為原生 3:2 取景（0.5% 內）。</summary>
    internal static bool Is32(PixelRect r) =>
        r.Height > 0 && Math.Abs((double)r.Width / r.Height - 1.5) <= 0.005;

    private enum Outcome { Absent, Valid, Rejected }


    private sealed class ReaderCopy<T>(Outcome State, T? Value, SonyFieldError? Error, bool Repeated) where T : class
    {
        public Outcome State { get; } = State;
        public T? Value { get; } = Value;
        public SonyFieldError? Error { get; } = Error;
        public bool Repeated { get; } = Repeated;
    }

    /// <summary>raw IFD 目錄的低階掃描（診斷用）：首條目、是否重複、讀取器漏掉條目的原因。</summary>
    private sealed class Directory
    {
        public byte[] Entries = null!;
        public byte[] Data = null!;
        public int Base;
        public ByteOrder Order;
        public bool Big;

        public (byte[]? Entry, bool Repeated) FindEntry(ushort tag)
        {
            byte[]? found = null;
            int n;
            int head, size;
            if (Big)
            {
                if (Order.ReadU64(Entries, 0) is not { } nn || nn > int.MaxValue) return (null, false);
                n = (int)nn; head = 8; size = 20;
            }
            else
            {
                if (Order.ReadU16(Entries, 0) is not { } nn) return (null, false);
                n = nn; head = 2; size = 12;
            }
            var count = Math.Min(n, MaxEntries);
            for (var i = 0; i < count; i++)
            {
                long at;
                try { at = checked((long)i * size + head); }
                catch (OverflowException) { break; }
                if (at < 0 || at + size > Entries.Length) break;
                var e = Entries.AsSpan((int)at, size).ToArray();
                if (Order.ReadU16(e, 0) != tag) continue;
                if (found is not null) return (found, true);
                found = e;
            }
            return (found, false);
        }

        public (ushort Type, ulong Count) Field(byte[] e)
        {
            var type = Order.ReadU16(e, 2) ?? 0;
            ulong? count = Big ? Order.ReadU64(e, 4) : Order.ReadU32(e, 4);
            return (type, count ?? ulong.MaxValue);
        }

        public byte[]? Values(byte[] e, int len)
        {
            var (field, width) = Big ? (12, 8) : (8, 4);
            if (len <= width) return e.AsSpan(field, Math.Min(len, e.Length - field)).ToArray();
            ulong? offset = Big ? Order.ReadU64(e, field) : Order.ReadU32(e, field);
            if (offset is not { } o || o > int.MaxValue) return null;
            var at = (int)o - Base;
            if (at < 0 || (long)at + len > Data.Length) return null;
            return Data.AsSpan(at, len).ToArray();
        }

        /// <summary>目錄持有但 TIFF 讀取器漏掉的條目被丟棄的原因。</summary>
        public SonyFieldError Unread(byte[] e)
        {
            var (kind, count) = Field(e);
            if (FieldTypeExtensions.FromU16(kind) is not { } type) return SonyFieldError.OfType(kind);
            ulong size = (ulong)type.Size();
            if (count > 0 && size > 0 && count > ulong.MaxValue / size) return SonyFieldError.OutsideData;
            var len = (long)(count * size);
            if (len > int.MaxValue || Values(e, (int)len) is null) return SonyFieldError.OutsideData;
            return SonyFieldError.PastReaderLimits;
        }

        /// <summary>N 樣本的徑向表：SSHORT[N+1]，首值 N。</summary>
        public short[]? Table(byte[] e, int n, out SonyFieldError? error)
        {
            error = null;
            var (kind, count) = Field(e);
            if (kind != SShortType) { error = SonyFieldError.OfType(kind); return null; }
            if (count != (ulong)(n + 1)) { error = SonyFieldError.OfCount(count); return null; }
            if (Values(e, 2 * (n + 1)) is not { } bytes) { error = SonyFieldError.OutsideData; return null; }
            short At(int i) => unchecked((short)(Order.ReadU16(bytes, 2 * i) ?? 0));
            if (At(0) != n) { error = SonyFieldError.OfPrefix(At(0)); return null; }
            var table = new short[n];
            for (var i = 0; i < n; i++) table[i] = At(i + 1);
            return table;
        }

        public SonySetting? Setting(byte[] e, Kind kind, out SonyFieldError? error)
        {
            error = null;
            var (type, count) = Field(e);
            if (type != ShortType && type != SShortType) { error = SonyFieldError.OfType(type); return null; }
            if (count != 1) { error = SonyFieldError.OfCount(count); return null; }
            if (Values(e, 2) is not { } bytes || Order.ReadU16(bytes, 0) is not { } value)
            { error = SonyFieldError.OutsideData; return null; }
            if (type == SShortType && unchecked((short)value) < 0)
            { error = SonyFieldError.OfNegative(unchecked((short)value)); return null; }
            return new SonySetting(value, MeaningOf(kind, value));
        }
    }


    private static short[]? ParsedTable(TiffValue value, int n, out SonyFieldError? error)
    {
        error = null;
        if (value is not TiffValue.SShort s) { error = SonyFieldError.OfType((ushort)value.FieldType); return null; }
        if (s.Values.Length != n + 1) { error = SonyFieldError.OfCount((ulong)s.Values.Length); return null; }
        if (s.Values[0] != n) { error = SonyFieldError.OfPrefix(s.Values[0]); return null; }
        return s.Values[1..];
    }

    private static SonySetting? ParsedSetting(TiffValue value, Kind kind, out SonyFieldError? error)
    {
        error = null;
        ushort? number = value switch
        {
            TiffValue.Short s when s.Values.Length != 1 => null,
            TiffValue.SShort s when s.Values.Length != 1 => null,
            TiffValue.Short s => s.Values[0],
            TiffValue.SShort s when s.Values[0] < 0 => null,
            TiffValue.SShort s => unchecked((ushort)s.Values[0]),
            _ => null,
        };
        if (value is TiffValue.SShort ss && ss.Values.Length == 1 && ss.Values[0] < 0)
        { error = SonyFieldError.OfNegative(ss.Values[0]); return null; }
        if (value is not (TiffValue.Short or TiffValue.SShort))
        { error = SonyFieldError.OfType((ushort)value.FieldType); return null; }
        if (number is null)
        {
            var count = (ulong)value.Count;
            error = SonyFieldError.OfCount(count == 1 ? 0 : count);
            return null;
        }
        return new SonySetting(number.Value, MeaningOf(kind, number.Value));
    }

    private static ReaderCopy<T> RawCopy<T>(Ifd ifd, Directory? dir, ushort tag, Func<TiffValue, (T?, SonyFieldError?)> read) where T : class
    {
        var (entry, repeated) = dir is null ? ((byte[]?)null, false) : dir.FindEntry(tag);
        if (ifd.Value(tag) is { } parsed)
        {
            var (value, error) = read(parsed);
            return error is null
                ? new ReaderCopy<T>(Outcome.Valid, value, null, repeated)
                : new ReaderCopy<T>(Outcome.Rejected, null, error, repeated);
        }
        if (entry is not null && dir is not null)
            return new ReaderCopy<T>(Outcome.Rejected, null, dir.Unread(entry), repeated);
        return new ReaderCopy<T>(Outcome.Absent, null, null, repeated);
    }

    private static ReaderCopy<T> Sr2Copy<T>(Directory? dir, ushort tag, Func<Directory, byte[], (T?, SonyFieldError?)> read) where T : class
    {
        if (dir is null) return new ReaderCopy<T>(Outcome.Absent, null, null, false);
        var (entry, repeated) = dir.FindEntry(tag);
        if (entry is null) return new ReaderCopy<T>(Outcome.Absent, null, null, repeated);
        var (value, error) = read(dir, entry);
        return error is null
            ? new ReaderCopy<T>(Outcome.Valid, value, null, repeated)
            : new ReaderCopy<T>(Outcome.Rejected, null, error, repeated);
    }

    private static SonyLensField<T> Combine<T>(ReaderCopy<T> raw, ReaderCopy<T> sr2) where T : class
    {
        static SonyCopy CopyOf(ReaderCopy<T> c) => c.State switch
        {
            Outcome.Absent => SonyCopy.Absent,
            Outcome.Valid => SonyCopy.Valid,
            _ => SonyCopy.Rejected(c.Error ?? SonyFieldError.OutsideData),
        };
        bool? agree = raw.State == Outcome.Valid && sr2.State == Outcome.Valid
            ? ValuesEqual(raw.Value, sr2.Value)
            : null;
        (T? value, SonySource? source) = (raw.State, sr2.State) switch
        {
            (Outcome.Valid, _) => (raw.Value, (SonySource?)SonySource.RawIfd),
            (Outcome.Absent, Outcome.Valid) => (sr2.Value, SonySource.Sr2SubIfd),
            _ => ((T?)null, (SonySource?)null),
        };
        return new SonyLensField<T>
        {
            Value = value, Source = source,
            RawIfd = CopyOf(raw), Sr2 = CopyOf(sr2),
            CopiesAgree = agree, RawIfdRepeated = raw.Repeated, Sr2Repeated = sr2.Repeated,
        };
    }

    private static bool ValuesEqual<T>(T? a, T? b)
    {
        if (a is short[] x && b is short[] y) return x.SequenceEqual(y);
        if (a is short[][] m && b is short[][] n)
            return m.Length == n.Length && m.Zip(n, (p, q) => p.SequenceEqual(q)).All(e => e);
        return Equals(a, b);
    }

    /// <summary>
    /// 檔案記錄與驗證過的畸變 warp（上游 <c>arw_lens::corrections</c>）。
    /// 回傳 (record, warp)：兩目錄皆無六欄位時 record 為 null。
    /// </summary>
    internal static (SonyLensCorrections? Record, Opcode? Warp) Corrections(
        ReadOnlyMemory<byte> bytes, ByteOrder order, Ifd raw, bool big, Sr2SubIfd? sr2,
        string model, SonyLensGeometry geometry)
    {
        Directory? plain = null;
        try
        {
            if (raw.Offset >= 0 && raw.Offset <= bytes.Length)
                plain = new Directory { Entries = bytes.Slice((int)raw.Offset).ToArray(), Data = bytes.ToArray(), Base = 0, Order = order, Big = big };
        }
        catch { plain = null; }
        Directory? encrypted = null;
        if (sr2 is not null)
        {
            var (block, start) = sr2.RawBlock;
            encrypted = new Directory { Entries = block, Data = block, Base = start, Order = sr2.Order, Big = false };
        }
        SonyLensField<short[]> Table16(ushort rawTag, ushort sr2Tag) => Combine(
            RawCopy<short[]>(raw, plain, rawTag, v =>
            {
                var t = ParsedTable(v, 16, out var e);
                return (t, e);
            }),
            Sr2Copy<short[]>(encrypted, sr2Tag, (d, e) =>
            {
                var t = d.Table(e, 16, out var err);
                return (t, err);
            }));
        SonyLensField<short[][]> TableCa() => Combine(
            RawCopy<short[][]>(raw, plain, CaTable, v =>
            {
                var flat = ParsedTable(v, 32, out var e);
                if (flat is null) return ((short[][]?)null, e);
                return (Split(flat), (SonyFieldError?)null);
            }),
            Sr2Copy<short[][]>(encrypted, Sr2CaTable, (d, e) =>
            {
                var flat = d.Table(e, 32, out var err);
                if (flat is null) return ((short[][]?)null, err);
                return (Split(flat), (SonyFieldError?)null);
            }));
        SonyLensField<SonySetting> Setting(ushort rawTag, ushort sr2Tag, Kind kind) => Combine(
            RawCopy<SonySetting>(raw, plain, rawTag, v =>
            {
                var s = ParsedSetting(v, kind, out var e);
                return (s, e);
            }),
            Sr2Copy<SonySetting>(encrypted, sr2Tag, (d, e) =>
            {
                var s = d.Setting(e, kind, out var err);
                return (s, err);
            }));
        var shadingTable = Table16(ShadingTable, Sr2ShadingTable);
        var distortionTable = Table16(DistortionTable, Sr2DistortionTable);
        var caTable = TableCa();
        var shadingSetting = Setting(ShadingSetting, Sr2ShadingSetting, Kind.Shading);
        var caSetting = Setting(CaSetting, Sr2CaSetting, Kind.Ca);
        var distortionSetting = Setting(DistortionSetting, Sr2DistortionSetting, Kind.Distortion);
        if (!shadingTable.IsPresent() && !caTable.IsPresent() && !distortionTable.IsPresent()
            && !shadingSetting.IsPresent() && !caSetting.IsPresent() && !distortionSetting.IsPresent())
            return (null, null);
        // 只有驗證範圍進轉換：raw IFD 表格（TIFF 讀取器所解析者）、Bayer、影像裁切與 reference 皆原生 3:2
        //（DefaultCrop 可保原生幾何而 Sony 標籤選 aspect crop）。
        var validatedModel = DistortionModels.Contains(model, StringComparer.Ordinal);
        var inMode = !geometry.LinearYcbcr && geometry.Native32 && distortionTable.Source == SonySource.RawIfd;
        Opcode? warp = null;
        if (distortionTable.Value is { } table && inMode)
            warp = Distortion(model, table, new PixelRect(0, 0, geometry.StorageWidth, geometry.StorageHeight), geometry.Reference);
        SonyLensStatus distortionStatus = distortionTable.Value switch
        {
            null => SonyLensStatus.NoTable,
            _ when warp is not null => SonyLensStatus.Applied,
            _ when !validatedModel => SonyLensStatus.NotValidatedModel,
            _ when !inMode => SonyLensStatus.NotValidatedMode,
            _ when distortionTable.Value is { } tv && tv.All(v => v == 0) => SonyLensStatus.Neutral,
            _ => SonyLensStatus.Rejected,
        };
        // shading／CA 語意在任何機身皆無獨立驗證：只解碼，永不套用。
        static SonyLensStatus Unvalidated(bool present) => present ? SonyLensStatus.NotValidatedComponent : SonyLensStatus.NoTable;
        var record = new SonyLensCorrections
        {
            Shading = new SonyLensComponent<short[]> { Setting = shadingSetting, Table = shadingTable, Status = Unvalidated(shadingTable.Value is not null) },
            ChromaticAberration = new SonyLensComponent<short[][]> { Setting = caSetting, Table = caTable, Status = Unvalidated(caTable.Value is not null) },
            Distortion = new SonyLensComponent<short[]> { Setting = distortionSetting, Table = distortionTable, Status = distortionStatus },
            Sr2Read = sr2 is not null,
            Geometry = geometry,
        };
        return (record, warp);

        static short[][] Split(short[] flat) =>
            [flat[..16].ToArray(), flat[16..32].ToArray()];
    }

    /// <summary>
    /// 驗證過的 raw-IFD 畸變表轉 WarpRectilinear（上游 <c>arw_lens::distortion</c>）。
    /// 只收獨立驗證過的相機與 16 樣本版面；他身、不合理或非平滑映照不校正。
    /// 相機 Off 設定不抹除鏡頭資料：可在顯影器開啟。
    /// </summary>
    internal static Opcode? Distortion(string model, short[] table, PixelRect active, PixelRect crop)
    {
        // 表格形狀相合不代表他身的尺度／半徑正規化相同。
        if (!DistortionModels.Contains(model, StringComparer.Ordinal)) return null;
        if (table.Length != 16 || table.All(v => v == 0) || table.Any(v => v is < -4096 or > 4096)) return null;
        crop = crop.Clipped(active.Width, active.Height);
        if (active.Width < 2 || active.Height < 2 || crop.Width < 2 || crop.Height < 2) return null;
        // 只驗證原生 3:2 取景。機內 aspect crop 可能保留全幅正規化的表格；不靜默重解讀。
        if (Math.Abs((double)crop.Width / crop.Height - 1.5) > 0.005) return null;
        // 中心到裁切角等距半徑上的分段線性徑向尺度。
        double Scale(double r)
        {
            var at = Math.Clamp(r, 0.0, 1.0) * 15.0;
            var i = Math.Min((int)Math.Floor(at), 14);
            return 1.0 + (table[i] + (table[i + 1] - table[i]) * (at - i)) / 16384.0;
        }
        var (cw, ch) = (crop.Width - 1.0, crop.Height - 1.0);
        var diagonal = LensFit.Hypot(cw, ch);
        // 放到整個矩形輸出邊界落在來源內。分段線性尺度在邊界上的最大值落在最近半徑或其餘 knot。
        var nearest = Math.Min(cw, ch) / diagonal;
        var maxScale = Scale(nearest);
        for (var i = 0; i < 16; i++)
        {
            var r = i / 15.0;
            if (r >= nearest) maxScale = Math.Max(maxScale, Scale(r));
        }
        var zoom = 1.0 / maxScale;
        static double[] Basis(double r)
        {
            var r2 = r * r;
            return [r, r * r2, r * r2 * r2, r * r2 * r2 * r2];
        }
        var matrix = new double[4, 4];
        var rhs = new double[4];
        var prev = -1.0;
        for (var j = 0; j <= WarpSamples; j++)
        {
            var r = (double)j / WarpSamples;
            var source = r * Scale(r) * zoom;
            if (source <= prev) return null;
            prev = source;
            var terms = Basis(r);
            for (var i = 0; i < 4; i++)
            {
                rhs[i] += terms[i] * source;
                for (var c = 0; c < 4; c++) matrix[i, c] += terms[i] * terms[c];
            }
        }
        if (LensFit.Solve4(matrix, rhs) is not { } k) return null;
        // 多項式與檔案表格差不到預覽半像素，且禁折疊。
        for (var j = 0; j <= WarpSamples; j++)
        {
            var r = (double)j / WarpSamples;
            var terms = Basis(r);
            var fit = terms[0] * k[0] + terms[1] * k[1] + terms[2] * k[2] + terms[3] * k[3];
            var r2 = r * r;
            var derivative = k[0] + r2 * (3.0 * k[1] + r2 * (5.0 * k[2] + r2 * 7.0 * k[3]));
            if (Math.Abs(fit - r * Scale(r) * zoom) > MaxWarpError || derivative <= 0.0) return null;
        }
        // DNG 用最遠 active-area 角，而非裁切對角線。引擎把 opcode 平移到預設裁切＋EXIF 方向座標後映照不變。
        var (ax, ay) = (active.Width - 1.0, active.Height - 1.0);
        var (cx, cy) = (crop.X + cw / 2.0, crop.Y + ch / 2.0);
        var farthest = LensFit.Hypot(Math.Max(cx, ax - cx), Math.Max(cy, ay - cy));
        var ratio2 = Math.Pow(2.0 * farthest / diagonal, 2);
        for (var i = 0; i < 4; i++) k[i] *= Math.Pow(ratio2, i);
        return new WarpRectilinear([[k[0], k[1], k[2], k[3], 0.0, 0.0]], [cx / ax, cy / ay]);
    }
}
