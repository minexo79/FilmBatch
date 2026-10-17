// 移植自 LightCraft crates/raw/src/lens_metadata.rs（內嵌鏡頭校正中繼資料；診斷用，不直接套用）。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

namespace FilmBatch.Raw;

/// <summary>RAW 檔記錄的鏡頭校正中繼資料（lightcraft <c>LensMetadata</c>）。可讀表格不代表可用校正：
/// 只有 <see cref="SonyLensStatus.Applied"/> 的元件才會轉成影像的 <c>OpcodeList3</c>。</summary>
public sealed class LensMetadata
{
    /// <summary>Sony ARW：raw IFD 的 shading／橫向色差／畸變記錄與其加密 SR2SubIFD 複本。兩目錄皆無時為 null。</summary>
    public SonyLensCorrections? Sony { get; set; }
}

/// <summary>Sony 機內鏡頭校正記錄：三個獨立元件，各為相機設定＋徑向表格。</summary>
public sealed class SonyLensCorrections
{
    /// <summary>Shading（暗角）：16 個徑向樣本；增益函數未驗證，僅記錄。</summary>
    public required SonyLensComponent<short[]> Shading { get; set; }

    /// <summary>橫向色差：兩組 16 樣本徑向曲線；未驗證，僅記錄。</summary>
    public required SonyLensComponent<short[][]> ChromaticAberration { get; set; }

    /// <summary>畸變：16 個徑向樣本（驗證過的解讀見 docs/sony-lens-corrections.md）。</summary>
    public required SonyLensComponent<short[]> Distortion { get; set; }

    /// <summary>是否找到並解密加密 SR2SubIFD。false：無複本可 fallback／比對。</summary>
    public bool Sr2Read { get; set; }

    /// <summary>解讀表格所需的取景與編碼證據。</summary>
    public required SonyLensGeometry Geometry { get; set; }
}

/// <summary>一個校正元件：相機設定、表格與是否套用。</summary>
public sealed class SonyLensComponent<T>
{
    public required SonyLensField<SonySetting> Setting { get; set; }

    /// <summary>前導樣本數之後的表格樣本；前導數必須等於樣本數。</summary>
    public required SonyLensField<T> Table { get; set; }

    public SonyLensStatus Status { get; set; }
}

/// <summary>一個欄位在 raw IFD 與 SR2SubIFD 的讀取結果。</summary>
public sealed class SonyLensField<T>
{
    /// <summary>使用中的值：raw IFD 有格式正確者用它；raw IFD 缺標籤時用 SR2SubIFD 者。
    /// raw IFD 複本損毀時回報，不以另一複本靜默取代。</summary>
    public T? Value { get; set; }

    public SonySource? Source { get; set; }

    public SonyCopy RawIfd { get; set; } = null!;

    public SonyCopy Sr2 { get; set; } = null!;

    /// <summary>兩複本皆格式正確時是否相等；不等時用 raw IFD 者。</summary>
    public bool? CopiesAgree { get; set; }

    /// <summary>診斷：raw IFD／SR2SubIFD 內標籤出現多次。只用第一個（TIFF 讀取器亦然）。</summary>
    public bool RawIfdRepeated { get; set; }

    public bool Sr2Repeated { get; set; }

    /// <summary>任一目錄是否持有該標籤（格式正確與否皆算）。</summary>
    public bool IsPresent() => RawIfd.Kind != SonyCopyKind.Absent || Sr2.Kind != SonyCopyKind.Absent;
}

/// <summary>值從何處讀得。</summary>
public enum SonySource
{
    /// <summary>一般 raw 影像 IFD。</summary>
    RawIfd,

    /// <summary>加密 SR2SubIFD。</summary>
    Sr2SubIfd,
}

/// <summary>一個複本的發現方式。</summary>
public sealed class SonyCopy : IEquatable<SonyCopy>
{
    public SonyCopyKind Kind { get; }

    public SonyFieldError? Error { get; }

    private SonyCopy(SonyCopyKind kind, SonyFieldError? error = null) => (Kind, Error) = (kind, error);

    public static SonyCopy Absent { get; } = new(SonyCopyKind.Absent);

    public static SonyCopy Valid { get; } = new(SonyCopyKind.Valid);

    public static SonyCopy Rejected(SonyFieldError error) => new(SonyCopyKind.Rejected, error);

    public bool Equals(SonyCopy? other) => other is not null && Kind == other.Kind && Error == other.Error;

    public override bool Equals(object? obj) => obj is SonyCopy other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, Error);

    public static bool operator ==(SonyCopy? a, SonyCopy? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(SonyCopy? a, SonyCopy? b) => !(a == b);
}

public enum SonyCopyKind
{
    /// <summary>該目錄無此標籤（或 SR2SubIFD 讀不出）。</summary>
    Absent,

    /// <summary>型別與版面完全符合支援的格式。</summary>
    Valid,

    /// <summary>有標籤但拒絕。</summary>
    Rejected,
}

/// <summary>存在的欄位被拒絕的原因。</summary>
public sealed record SonyFieldError(SonyFieldErrorKind Kind, ushort FieldType = 0, ulong Count = 0, short Prefix = 0, short Negative = 0)
{
    public static SonyFieldError OfType(ushort fieldType) => new(SonyFieldErrorKind.FieldType, FieldType: fieldType);

    public static SonyFieldError OfCount(ulong count) => new(SonyFieldErrorKind.Count, Count: count);

    public static SonyFieldError OfPrefix(short prefix) => new(SonyFieldErrorKind.CountPrefix, Prefix: prefix);

    public static SonyFieldError OutsideData { get; } = new(SonyFieldErrorKind.OutsideData);

    public static SonyFieldError PastReaderLimits { get; } = new(SonyFieldErrorKind.PastReaderLimits);

    public static SonyFieldError OfNegative(short value) => new(SonyFieldErrorKind.NegativeSetting, Negative: value);
}

public enum SonyFieldErrorKind
{
    /// <summary>表格非 SSHORT（8）、設定非 SHORT（3）／SSHORT。</summary>
    FieldType,

    /// <summary>值個數不是支援版面的：shading／畸變 17、CA 33、設定 1。</summary>
    Count,

    /// <summary>前導樣本數不是版面的 16（CA 為 32）。舊機身存 11（22）樣本填成同長度，不解讀；0 搭配 unavailable 設定。</summary>
    CountPrefix,

    /// <summary>值超出檔案（raw IFD）或解密區塊（SR2SubIFD）。</summary>
    OutsideData,

    /// <summary>在 raw IFD 但超出解碼影像的 TIFF 讀取器所讀（每目錄 8192 條、值預算），影像亦不用它。</summary>
    PastReaderLimits,

    /// <summary>以負 SSHORT 存的設定。</summary>
    NegativeSetting,
}

/// <summary>一個相機校正設定：存的值與文件記載的意義。</summary>
public sealed record SonySetting(ushort Value, SonySettingMeaning Meaning);

/// <summary>設定值的文件意義（ExifTool EXIF tag-name 文件）。整值比對，不當 bit mask 讀。</summary>
public enum SonySettingMeaning
{
    /// <summary>Shading 256、CA 0、畸變 0。表格仍記錄：Off 不代表缺席。</summary>
    Off,

    /// <summary>Shading 257 或 272、CA 1、畸變 1。</summary>
    Auto,

    /// <summary>畸變 17：Auto，由鏡頭固定。</summary>
    ForcedAuto,

    /// <summary>無校正參數：shading 511、CA 255、畸變 255。</summary>
    Unavailable,

    /// <summary>其他值（例如 shading Low，無文件編號）。絕不視為 Off。</summary>
    Unknown,
}

/// <summary>元件是否套用，若否則原因。</summary>
public enum SonyLensStatus
{
    /// <summary>已轉成影像的 OpcodeList3（現為 ILCE-7RM4A 與 ILCE-7RM6 Bayer 畸變，原生 3:2 取景）。</summary>
    Applied,

    /// <summary>無可用表格。</summary>
    NoTable,

    /// <summary>僅解碼：任何機身皆無此元件渲染語意的獨立驗證（shading、橫向 CA）。</summary>
    NotValidatedComponent,

    /// <summary>僅解碼：此元件在其他機身驗證過，不在此機身。</summary>
    NotValidatedModel,

    /// <summary>僅解碼：機身驗證過，但此編碼或取景未驗證（線性 YCbCr、非 3:2 裁切）或表格僅見於 SR2SubIFD。</summary>
    NotValidatedMode,

    /// <summary>驗證範圍內，但表格全零：合法的 no-op，無需套用。</summary>
    Neutral,

    /// <summary>驗證範圍內，但轉換拒絕此表格（超出驗證值域、退化裁切、擬合失準或折疊）。</summary>
    Rejected,
}

/// <summary>解讀表格所需的取景與編碼證據，皆為 raw-frame 像素。</summary>
public sealed class SonyLensGeometry
{
    /// <summary>儲存的 raw 框，含 padding（lossless L 檔有 padding）。</summary>
    public int StorageWidth { get; set; }

    public int StorageHeight { get; set; }

    /// <summary>Sony 裁切標籤（0x74c7／0x74c8）記錄值。</summary>
    public PixelRect? SonyCrop { get; set; }

    /// <summary>raw IFD 的 DefaultCropOrigin／DefaultCropSize 記錄值。</summary>
    public PixelRect? DefaultCrop { get; set; }

    /// <summary>影像裁切（解碼器優先序：Sony 標籤、預設裁切、記錄尺寸）。</summary>
    public required PixelRect ImageCrop { get; set; }

    /// <summary>驗證過的畸變轉換所對中／正規化的矩形：有預設裁切時用它，否則用影像裁切。夾於框內。</summary>
    public required PixelRect Reference { get; set; }

    /// <summary>raw 影像 TIFF compression（1 未壓縮、7 lossless、32767 ARW2、32766 ARW6）。</summary>
    public ushort Compression { get; set; }

    /// <summary>縮圖 lossless M/S 資料（線性 YCbCr，非感光 CFA）。</summary>
    public bool LinearYcbcr { get; set; }

    /// <summary>影像裁切與 reference 皆為 3:2（0.5% 內），唯一驗證過的取景。</summary>
    public bool Native32 { get; set; }
}
