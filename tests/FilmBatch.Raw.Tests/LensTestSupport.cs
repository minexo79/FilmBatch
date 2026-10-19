// 內嵌鏡頭校正測試的合成表格／區塊支援（手推夾具；CI 無外部 corpus 可跑）。
// 數值取自上游單元測試的公開常數（ILCE-7RM4A TELE／WIDE、X-T4 XT4 表、Nikon WIDE），
// 不是私有照片；僅供幾何／狀態閘門迴歸。
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。

using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Vendors.Fujifilm;
using FilmBatch.Raw.Vendors.Nikon;
using FilmBatch.Raw.Vendors.Panasonic;
using FilmBatch.Raw.Vendors.Sony;

namespace FilmBatch.Raw.Tests;

/// <summary>Sony 合成表格支援（上游 arw_lens.rs 測試的 TELE／WIDE 常數）。</summary>
internal static class SonyLensTests
{
    internal static readonly short[] Wide = [14, 0, -27, -59, -106, -158, -223, -291, -367, -440, -519, -593, -667, -731, -792, -839];
    internal static readonly short[] Tele = [-2, 1, 7, 17, 31, 48, 68, 93, 120, 152, 187, 225, 265, 309, 356, 403];
}

/// <summary>Sony 鏡頭記錄的測試建構子（直接給表格＋幾何，不經 TIFF；解析路徑由 vendor 解碼覆蓋）。</summary>
internal static class SonyLensRecord
{
    internal sealed class Record
    {
        public required SonyLensComponent<short[]> Distortion { get; init; }
        public List<Opcode> Opcodes { get; init; } = [];
        public SonyLensCorrections ToCorrections() => new()
        {
            Shading = new SonyLensComponent<short[]>
            {
                Setting = EmptySetting(),
                Table = EmptyTable<short[]>(),
                Status = SonyLensStatus.NoTable,
            },
            ChromaticAberration = new SonyLensComponent<short[][]>
            {
                Setting = EmptySetting(),
                Table = EmptyTable<short[][]>(),
                Status = SonyLensStatus.NoTable,
            },
            Distortion = Distortion,
            Sr2Read = false,
            Geometry = new SonyLensGeometry
            {
                StorageWidth = 32, StorageHeight = 24,
                ImageCrop = new PixelRect(0, 0, 32, 24),
                Reference = new PixelRect(2, 2, 30, 20),
            },
        };

        private static SonyLensField<SonySetting> EmptySetting() => new()
        {
            RawIfd = SonyCopy.Absent, Sr2 = SonyCopy.Absent,
        };

        private static SonyLensField<T> EmptyTable<T>() where T : class => new()
        {
            RawIfd = SonyCopy.Absent, Sr2 = SonyCopy.Absent,
        };
    }

    internal static Record? BuildRecord(
        string model, short[] table, PixelRect active, PixelRect reference,
        ushort Compression, bool LinearYcbcr, SonySource Source)
    {
        var validatedModel = SonyLens.DistortionModels.Contains(model, StringComparer.Ordinal);
        // 夾具：Sony 裁切標籤與預設裁切一致，影像裁切即 reference（上游 native_3_2 看影像裁切與 reference）。
        var native32 = SonyLens.Is32(reference);
        var inMode = !LinearYcbcr && native32 && Source == SonySource.RawIfd;
        Opcode? warp = null;
        if (inMode) warp = SonyLens.Distortion(model, table, active, reference);
        SonyLensStatus status = warp is not null ? SonyLensStatus.Applied
            : !validatedModel ? SonyLensStatus.NotValidatedModel
            : !inMode ? SonyLensStatus.NotValidatedMode
            : table.All(v => v == 0) ? SonyLensStatus.Neutral
            : SonyLensStatus.Rejected;
        var field = new SonyLensField<short[]>
        {
            Value = table,
            Source = Source,
            RawIfd = SonyCopy.Valid,
            Sr2 = SonyCopy.Absent,
            CopiesAgree = null,
        };
        return new Record
        {
            Distortion = new SonyLensComponent<short[]>
            {
                Setting = new SonyLensField<SonySetting>
                {
                    Value = new SonySetting(0, SonySettingMeaning.Off),
                    Source = Source, RawIfd = SonyCopy.Valid, Sr2 = SonyCopy.Absent,
                },
                Table = field,
                Status = status,
            },
            Opcodes = warp is null ? [] : [warp],
        };
    }
}

/// <summary>Fuji 合成表格支援（上游 raf_lens.rs 測試的 X-T4 常數）。</summary>
internal static class FujiLensTests
{
    internal static readonly double[] Knots9 = [0.3535211268, 0.5, 0.6126760563, 0.7070422535, 0.7908450704, 0.8661971831, 0.9352112676, 1.0, 1.06056338];
    internal static readonly double[] Xt4Dist = [-1.172515869, -2.354660034, -3.458114624, -4.434677124, -5.274719238, -5.918792725, -6.328369141, -6.426345825, -6.426345825];
    internal static readonly double[] Xt4Vig = [98.46142578, 96.87792969, 94.90234375, 90.98144531, 84.73291016, 78.43701172, 72.96044922, 67.45703125, 67.45703125];
    internal static readonly PixelRect Xt4Area = new(0, 0, 6240, 4160);

    internal static Ifd Xt4Ifd()
    {
        var scale = 3749.0 / 9.0;
        return RawTestIfd.FromDoubles(new Dictionary<ushort, double[]>
        {
            [0xf00b] = [scale, .. Knots9, .. Xt4Dist],
            [0xf010] = [scale, .. Knots9, .. Xt4Vig],
        });
    }
}

/// <summary>Nikon 合成區塊支援（上游 nef_lens.rs 測試的 WIDE 常數與區塊版面）。</summary>
internal static class NikonLensTests
{
    internal static readonly PixelRect Area = new(0, 0, 6064, 4040);
    internal static readonly PixelRect Crop = new(8, 8, 6048, 4024);

    internal static byte[] DistortionBlock(double norm, double[] k, double zoom, byte mode = 3, uint terms = 4)
    {
        var b = new byte[84];
        b[0] = (byte)'0'; b[1] = (byte)'1'; b[2] = (byte)'0'; b[3] = (byte)'0';
        b[4] = mode; b[5] = 1;
        Rat(b, 8, norm, 4096);
        BitConverter.GetBytes(terms).CopyTo(b, 16);
        for (var i = 0; i < 3; i++) Rat(b, 20 + 8 * i, k[i], 1 << 20);
        Rat(b, 44, 0.0, 1);
        Rat(b, 52, 0.0, 1);
        Rat(b, 64, zoom, 1 << 20);
        return b;

        static void Rat(byte[] b, int at, double v, int den)
        {
            BitConverter.GetBytes((int)Math.Round(v * den)).CopyTo(b, at);
            BitConverter.GetBytes(den).CopyTo(b, at + 4);
        }
    }

    internal static byte[] WideBlock() => DistortionBlock(21.6, [0.039895, 0.019211, -0.127218], -0.025043);

    internal static Ifd RawWith(byte[] distortionBlock, byte[]? offsetBlock = null)
    {
        var blob = NefInfoBlob(distortionBlock, offsetBlock);
        return RawTestIfd.FromBytes(new Dictionary<ushort, byte[]> { [0xc7d5] = blob });
    }

    private static byte[] NefInfoBlob(byte[] distortionBlock, byte[]? offsetBlock)
    {
        var entries = new List<(ushort Tag, byte[] Data)> { (0x0005, distortionBlock) };
        if (offsetBlock is not null) entries.Add((0x0008, offsetBlock));
        using var ms = new MemoryStream();
        // 最小 little-endian TIFF：header + 1 個 IFD。
        ms.Write([(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0]);
        ms.Write(BitConverter.GetBytes((ushort)entries.Count));
        var dataAt = 8 + 2 + entries.Count * 12 + 4;
        foreach (var (tag, data) in entries)
        {
            ms.Write(BitConverter.GetBytes(tag));
            ms.Write(BitConverter.GetBytes((ushort)7)); // UNDEFINED
            ms.Write(BitConverter.GetBytes((uint)data.Length));
            if (data.Length <= 4)
            {
                var inline = new byte[4];
                data.CopyTo(inline, 0);
                ms.Write(inline);
            }
            else
            {
                ms.Write(BitConverter.GetBytes((uint)dataAt));
                dataAt += data.Length;
            }
        }
        ms.Write(new byte[4]);
        foreach (var (_, data) in entries)
            if (data.Length > 4) ms.Write(data);
        var tiff = ms.ToArray();
        return [.. "Nikon\0"u8.ToArray(), 1, 3, 0, 0, .. tiff];
    }
}

/// <summary>Panasonic 合成區塊支援（word7 低 4 位開關；半徑／係數按上游文件語意）。</summary>
internal static class PanasonicLensTests
{
    internal static ushort[] OnBlock(int activeW, int activeH, double halfDiag)
    {
        // k2 = 0.01、zoom = 1.02、radius = halfDiag：可擬合的桶形。
        var w = new ushort[16];
        w[4] = unchecked((ushort)(short)Math.Round(0.005 * 32768));
        w[5] = unchecked((ushort)(short)Math.Round(0.02 * 32768));
        w[7] = 1;
        w[8] = unchecked((ushort)(short)Math.Round(0.01 * 32768));
        w[11] = 0;
        w[12] = (ushort)Math.Round(halfDiag);
        _ = (activeW, activeH);
        return w;
    }

    internal static ushort[] OffBlock()
    {
        var w = OnBlock(100, 100, 70);
        w[7] = 0;
        return w;
    }
}

/// <summary>以 double[]／byte[] 建最小 Ifd（測試夾具；只供鏡頭表格讀取）。</summary>
internal static class RawTestIfd
{
    internal static Ifd FromDoubles(Dictionary<ushort, double[]> tags)
    {
        var ifd = EmptyIfd();
        var entries = new List<TiffEntry>();
        foreach (var (tag, values) in tags)
        {
            var rats = values.Select(v => ((int)Math.Round(v * 1_000_000), 1_000_000)).ToArray();
            entries.Add(new TiffEntry(tag, new TiffValue.SRational(rats), 0));
        }
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        ifd.Entries = entries;
        return ifd;
    }

    private static Ifd EmptyIfd()
    {
        // TiffWriter 寫最小 TIFF 再解析，Entries 以新集合取代。
        var b = new Support.IfdBuilder().Set(272, new TiffValue.Short([1]));
        var bytes = new Support.TiffWriter().Write([b]);
        return TiffFile.Parse(new ReadOnlyMemory<byte>(bytes)).Ifds[0];
    }

    internal static Ifd FromBytes(Dictionary<ushort, byte[]> tags)
    {
        var ifd = EmptyIfd();
        var entries = tags.Select(kv => new TiffEntry(kv.Key, new TiffValue.Undefined(kv.Value), 0)).ToList();
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        ifd.Entries = entries;
        return ifd;
    }
}
