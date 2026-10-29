// 移植自 LightCraft crates/tiff/src/tags.rs（TIFF／EXIF／GPS／DNG 標籤）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
namespace FilmBatch.Raw.Tiff;

/// <summary>Tag numbers (TIFF 6.0, TIFF/EP, Exif 2.32, GPS, DNG 1.7). Public specification constants.</summary>
public static class Tags
{
    // TIFF baseline / extensions
    public const ushort NewSubfileType = 254;
    public const ushort SubfileType = 255;
    public const ushort ImageWidth = 256;
    public const ushort ImageLength = 257;
    public const ushort BitsPerSample = 258;
    public const ushort Compression = 259;
    public const ushort Photometric = 262;
    public const ushort FillOrder = 266;
    public const ushort DocumentName = 269;
    public const ushort ImageDescription = 270;
    public const ushort Make = 271;
    public const ushort Model = 272;
    public const ushort StripOffsets = 273;
    public const ushort Orientation = 274;
    public const ushort SamplesPerPixel = 277;
    public const ushort RowsPerStrip = 278;
    public const ushort StripByteCounts = 279;
    public const ushort XResolution = 282;
    public const ushort YResolution = 283;
    public const ushort PlanarConfiguration = 284;
    public const ushort ResolutionUnit = 296;
    public const ushort Software = 305;
    public const ushort DateTime = 306;
    public const ushort Artist = 315;
    public const ushort Predictor = 317;
    public const ushort TileWidth = 322;
    public const ushort TileLength = 323;
    public const ushort TileOffsets = 324;
    public const ushort TileByteCounts = 325;
    public const ushort SubIfds = 330;
    public const ushort ExtraSamples = 338;
    public const ushort SampleFormat = 339;
    public const ushort JpegInterchangeFormat = 513;
    public const ushort JpegInterchangeFormatLength = 514;
    public const ushort YCbCrSubsampling = 530;
    public const ushort Xmp = 700;
    public const ushort Rating = 18246;
    public const ushort RatingPercent = 18249;
    public const ushort CfaRepeatPatternDim = 33421;
    public const ushort CfaPatternEp = 33422;
    public const ushort Copyright = 33432;
    public const ushort ExposureTimeEp = 33434;
    public const ushort IptcNaa = 33723;
    public const ushort ExifIfd = 34665;
    public const ushort IccProfile = 34675;
    public const ushort GpsIfd = 34853;
    public const ushort InteropIfd = 40965;

    // Exif private IFD
    public const ushort ExposureTime = 33434;
    public const ushort FNumber = 33437;
    public const ushort ExposureProgram = 34850;
    public const ushort IsoSpeed = 34855;
    public const ushort SensitivityType = 34864;
    public const ushort RecommendedExposureIndex = 34866;
    public const ushort IsoSpeedRatingsExt = 34867;
    public const ushort ExifVersion = 36864;
    public const ushort DateTimeOriginal = 36867;
    public const ushort DateTimeDigitized = 36868;
    public const ushort OffsetTime = 36880;
    public const ushort OffsetTimeOriginal = 36881;
    public const ushort OffsetTimeDigitized = 36882;
    public const ushort ShutterSpeedValue = 37377;
    public const ushort ApertureValue = 37378;
    public const ushort ExposureBias = 37380;
    public const ushort MaxAperture = 37381;
    public const ushort MeteringMode = 37383;
    public const ushort LightSource = 37384;
    public const ushort Flash = 37385;
    public const ushort FocalLength = 37386;
    public const ushort MakerNote = 37500;
    public const ushort UserComment = 37510;
    public const ushort SubsecTime = 37520;
    public const ushort SubsecTimeOriginal = 37521;
    public const ushort SubsecTimeDigitized = 37522;
    public const ushort PixelXDimension = 40962;
    public const ushort PixelYDimension = 40963;
    public const ushort FocalPlaneXResolution = 41486;
    public const ushort ExposureMode = 41986;
    public const ushort WhiteBalance = 41987;
    public const ushort FocalLength35mm = 41989;
    public const ushort SceneCaptureType = 41990;
    public const ushort BodySerialNumber = 42033;
    public const ushort LensSpecification = 42034;
    public const ushort LensMake = 42035;
    public const ushort LensModel = 42036;
    public const ushort LensSerialNumber = 42037;

    // GPS IFD
    public const ushort GpsVersionId = 0;
    public const ushort GpsLatitudeRef = 1;
    public const ushort GpsLatitude = 2;
    public const ushort GpsLongitudeRef = 3;
    public const ushort GpsLongitude = 4;
    public const ushort GpsAltitudeRef = 5;
    public const ushort GpsAltitude = 6;
    public const ushort GpsTimeStamp = 7;
    public const ushort GpsImgDirectionRef = 16;
    public const ushort GpsImgDirection = 17;
    public const ushort GpsDateStamp = 29;

    // DNG
    public const ushort DngVersion = 50706;
    public const ushort DngBackwardVersion = 50707;
    public const ushort UniqueCameraModel = 50708;
    public const ushort LocalizedCameraModel = 50709;
    public const ushort CfaPlaneColor = 50710;
    public const ushort CfaLayout = 50711;
    public const ushort LinearizationTable = 50712;
    public const ushort BlackLevelRepeatDim = 50713;
    public const ushort BlackLevel = 50714;
    public const ushort BlackLevelDeltaH = 50715;
    public const ushort BlackLevelDeltaV = 50716;
    public const ushort WhiteLevel = 50717;
    public const ushort DefaultScale = 50718;
    public const ushort DefaultCropOrigin = 50719;
    public const ushort DefaultCropSize = 50720;
    public const ushort ColorMatrix1 = 50721;
    public const ushort ColorMatrix2 = 50722;
    public const ushort CameraCalibration1 = 50723;
    public const ushort CameraCalibration2 = 50724;
    public const ushort ReductionMatrix1 = 50725;
    public const ushort ReductionMatrix2 = 50726;
    public const ushort AnalogBalance = 50727;
    public const ushort AsShotNeutral = 50728;
    public const ushort AsShotWhiteXy = 50729;
    public const ushort BaselineExposure = 50730;
    public const ushort BaselineNoise = 50731;
    public const ushort BaselineSharpness = 50732;
    public const ushort BayerGreenSplit = 50733;
    public const ushort LinearResponseLimit = 50734;
    public const ushort CameraSerialNumber = 50735;
    public const ushort LensInfo = 50736;
    public const ushort ChromaBlurRadius = 50737;
    public const ushort AntiAliasStrength = 50738;
    public const ushort ShadowScale = 50739;
    public const ushort DngPrivateData = 50740;
    public const ushort MakerNoteSafety = 50741;
    public const ushort CalibrationIlluminant1 = 50778;
    public const ushort CalibrationIlluminant2 = 50779;
    public const ushort BestQualityScale = 50780;
    public const ushort RawDataUniqueId = 50781;
    public const ushort OriginalRawFileName = 50827;
    public const ushort ActiveArea = 50829;
    public const ushort MaskedAreas = 50830;
    public const ushort AsShotIccProfile = 50831;
    public const ushort ColorimetricReference = 50879;
    public const ushort CameraCalibrationSignature = 50931;
    public const ushort ProfileCalibrationSignature = 50932;
    public const ushort AsShotProfileName = 50934;
    public const ushort ProfileName = 50936;
    public const ushort ProfileHueSatMapDims = 50937;
    public const ushort ProfileHueSatMapData1 = 50938;
    public const ushort ProfileHueSatMapData2 = 50939;
    public const ushort ProfileToneCurve = 50940;
    public const ushort ProfileEmbedPolicy = 50941;
    public const ushort ProfileCopyright = 50942;
    public const ushort ProfileLookTableDims = 50981;
    public const ushort ProfileLookTableData = 50982;
    public const ushort ProfileHueSatMapEncoding = 51107;
    public const ushort ProfileLookTableEncoding = 51108;
    public const ushort BaselineExposureOffset = 51109;
    public const ushort DefaultBlackRender = 51110;
    public const ushort ForwardMatrix1 = 50964;
    public const ushort ForwardMatrix2 = 50965;
    public const ushort PreviewColorSpace = 50970;
    public const ushort CurrentPreviewDateTime = 50971;
    public const ushort OpcodeList1 = 51008;
    public const ushort OpcodeList2 = 51009;
    public const ushort OpcodeList3 = 51022;
    public const ushort NoiseProfile = 51041;
    public const ushort DefaultUserCrop = 51125;
    public const ushort ProfileGainTableMap = 52525;
    public const ushort RowInterleaveFactor = 50975;
    public const ushort ColumnInterleaveFactor = 52547;
    public const ushort CalibrationIlluminant3 = 52529;
    public const ushort CameraCalibration3 = 52530;
    public const ushort ColorMatrix3 = 52531;
    public const ushort ForwardMatrix3 = 52532;
    public const ushort IlluminantData1 = 52533;
    public const ushort IlluminantData2 = 52534;
    public const ushort IlluminantData3 = 52535;
    public const ushort SemanticName = 52526;
    public const ushort SemanticInstanceId = 52528;
    public const ushort MaskSubArea = 52536;
    /// NewSubFileType of a semantic mask IFD.
    public const uint SubfileSemanticMask = 0x10004;
    public const ushort ProfileGainTableMap2 = 52544;
}

/// <summary>Compression codes (TIFF 6.0, DNG, and widely used registrations).</summary>
public static class TiffCompression
{
    public const ushort None = 1;
    public const ushort Lzw = 5;
    public const ushort OldJpeg = 6;
    public const ushort Jpeg = 7;
    public const ushort AdobeDeflate = 8;
    public const ushort Deflate = 32946;
    public const ushort Packbits = 32773;
    public const ushort LossyJpeg = 34892;
    public const ushort JpegXl = 52546;
    /// SMPTE ST 2073 (VC-5) as written by GoPro's GPR files.
    public const ushort Vc5 = 9;
    /// Nikon NEF Huffman compression.
    public const ushort Nikon = 34713;
    /// Sony ARW compressed.
    public const ushort SonyArw = 32767;
    /// Sony ARW6 compressed.
    public const ushort SonyArw6 = 32766;
}

/// <summary>PhotometricInterpretation codes.</summary>
public static class TiffPhotometric
{
    public const ushort WhiteIsZero = 0;
    public const ushort BlackIsZero = 1;
    public const ushort Rgb = 2;
    public const ushort YCbCr = 6;
    public const ushort Cfa = 32803;
    public const ushort LinearRaw = 34892;
    /// DNG 1.6 semantic mask.
    public const ushort Mask = 52527;
}
