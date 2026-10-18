using System.CommandLine;
using System.Runtime.InteropServices;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using ImageMagick;

namespace FilmBatch.Cli;

/// <summary><c>raw</c> 子命令群:直接操作 RAW 檔,不經專案設定。</summary>
internal static class RawCommands
{
    public static Command Create()
    {
        var sourceArg = new Argument<string>("source") { Description = "RAW 來源檔" };
        var destinationArg = new Argument<string?>("destination")
        {
            Description = "輸出 DNG 路徑（必須不存在）",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var outOption = new Option<string>("--out") { Description = "輸出 DNG 路徑(必須不存在,不另取流水號)" };
        var compressionOption = new Option<string>("--compression")
        {
            Description = "uncompressed | lj92 | deflate | half | lossy(預設 lj92)",
            DefaultValueFactory = _ => "lj92",
        };
        var qualityOption = new Option<int>("--quality") { Description = "lossy JPEG 品質 1–100(預設 95)", DefaultValueFactory = _ => 95 };
        var longEdgeOption = new Option<int>("--long-edge") { Description = "lossy 縮放長邊像素;0 = 不縮(預設 0)", DefaultValueFactory = _ => 0 };

        var convert = new Command("convert", "把 RAW 感光資料原樣轉存為 DNG(不套用效果)")
        {
            sourceArg, destinationArg, outOption, compressionOption, qualityOption, longEdgeOption,
        };
        convert.SetAction(r =>
        {
            var source = Path.GetFullPath(r.GetValue(sourceArg)!);
            var destination = r.GetValue(destinationArg);
            var outPath = r.GetValue(outOption);
            if (destination is not null && outPath is not null)
            {
                Console.Error.WriteLine("輸出路徑只能指定一次：使用 destination 或 --out。");
                return 2;
            }
            var output = (destination ?? outPath) is { } o ? Path.GetFullPath(o) : null;
            if (!File.Exists(source))
            {
                Console.Error.WriteLine($"找不到 RAW 來源:{source}");
                return 2;
            }
            if (output is null)
            {
                Console.Error.WriteLine("必須指定輸出路徑或 --out（不沿用預設檔名，避免誤蓋）。");
                return 2;
            }
            if (File.Exists(output))
            {
                Console.Error.WriteLine($"輸出已存在,拒絕覆寫:{output}");
                return 2;
            }
            DngCompression compression;
            try
            {
                compression = Compression(r.GetValue(compressionOption)!.ToLowerInvariant(), r.GetValue(qualityOption), r.GetValue(longEdgeOption));
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            try
            {
                var bytes = File.ReadAllBytes(source);
                var raw = RawDecoder.Decode(bytes);
                // 上游 engine/cmd/convert.rs：轉存前補上檔案開啟時的起始色彩（無自有矩陣的
                // RAW 才有；鏡頭校正保留在 DNG 中）。不憑空捏造擬合：擬合失敗時只有
                // 保留缺少描述的狀態，沒有假外觀標籤。
                RawDngConversion.WithStartingColour(raw, bytes);
                if (!CameraColor.HasMatrix(raw.Color))
                    throw new InvalidDataException("來源沒有可用的相機色彩矩陣，且無法從預覽或實測模型合成；未寫入 DNG。");
                var dng = DngWriter.WriteDng(raw, new DngWriteOptions { Compression = compression });
                // 寫出的 DNG 先被依賴才可靠：必須解碼回同一感光資料（上游 convert.rs
                // write_dng_with；失敗不寫檔）。有損壓縮本就改變樣本，不在此檢查。
                if (BitExact(compression))
                {
                    var back = RawDecoder.Decode(dng);
                    if ((back.Width, back.Height, back.Cpp) != (raw.Width, raw.Height, raw.Cpp) ||
                        !SameRawData(back.Data, raw.Data))
                    {
                        Console.Error.WriteLine($"轉換失敗:寫出的 DNG 解碼後不含同一感光資料,來源保留:{source}");
                        return 1;
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(dng);
                Console.WriteLine(output);
                return 0;
            }
            catch (Exception ex) when (ex is RawException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"轉換失敗:{ex.Message}");
                return 1;
            }
        });
        var masks = CreateMasks();
        return new Command("raw", "RAW 檔案工具") { convert, masks };
    }

    private static DngCompression Compression(string name, int quality, int longEdge) => name switch
    {
        "uncompressed" => new DngCompression.Uncompressed(),
        "lj92" => new DngCompression.Lj92(256),
        "deflate" => new DngCompression.Deflate(256, Half: false),
        "half" => new DngCompression.Deflate(256, Half: true),
        "lossy" => new DngCompression.LossyJpeg(256,
            (byte)Math.Clamp(quality, 1, 100),
            longEdge > 0 ? (uint)longEdge : null),
        var other => throw new InvalidDataException($"未知壓縮:{other}(可用 uncompressed、lj92、deflate、half、lossy)。"),
    };

    /// <summary>寫出後感光資料必須逐位元相同的壓縮模式（有損 JPEG 與半浮點 Deflate 本就改變樣本）。</summary>
    private static bool BitExact(DngCompression compression) => compression is
        DngCompression.Uncompressed or DngCompression.Lj92 or DngCompression.Deflate { Half: false };

    /// <summary>同一感光資料（上游 convert.rs 的 <c>back.data == raw.data</c>；F32 逐位元比較含 NaN 位元組）。</summary>
    private static bool SameRawData(RawData a, RawData b) => (a, b) switch
    {
        (RawData.U16 x, RawData.U16 y) => x.Samples.AsSpan().SequenceEqual(y.Samples),
        (RawData.F32 x, RawData.F32 y) =>
            MemoryMarshal.AsBytes(x.Samples.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(y.Samples.AsSpan())),
        _ => false,
    };

    private static Command CreateMasks()
    {
        var sourceArg = new Argument<string>("source") { Description = "RAW 來源檔（目前 DNG 才可能攜帶語意遮罩）" };
        var outOption = new Option<string?>("--out") { Description = "輸出資料夾（預設 <來源>_masks/；既有 PNG 拒絕覆寫）" };
        var masks = new Command("masks", "匯出 DNG 語意遮罩為 8-bit PNG（顯影裁切座標、已轉正）") { sourceArg, outOption };
        masks.SetAction(r =>
        {
            var source = Path.GetFullPath(r.GetValue(sourceArg)!);
            if (!File.Exists(source))
            {
                Console.Error.WriteLine($"找不到 RAW 來源：{source}");
                return 2;
            }
            var outDir = Path.GetFullPath(r.GetValue(outOption) ?? Path.Combine(
                Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + "_masks"));
            try
            {
                var bytes = File.ReadAllBytes(source);
                var info = RawDecoder.ProbeInfo(bytes);
                var list = RawSemanticMasks.Read(bytes);
                if (list.Count == 0)
                {
                    Console.WriteLine("沒有可辨識的語意遮罩。");
                    return 0;
                }
                Directory.CreateDirectory(outDir);
                var written = 0;
                foreach (var (mask, i) in list.Select((m, i) => (m, i)))
                {
                    if (mask.Developed(info.ActiveArea, info.Crop) is not { } dev)
                    {
                        Console.WriteLine($"略過 {mask.Name}：裁切為空或超出大小上限。");
                        continue;
                    }
                    var output = Path.Combine(outDir, $"{MaskFileStem(mask, i)}.png");
                    if (File.Exists(output))
                    {
                        Console.Error.WriteLine($"輸出已存在，拒絕覆寫：{output}");
                        return 2;
                    }
                    WriteMaskPng(dev, info.Orientation, output);
                    Console.WriteLine(output);
                    written++;
                }
                Console.WriteLine($"共 {written} 個遮罩（{outDir}）。");
                return 0;
            }
            catch (Exception ex) when (ex is RawException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"遮罩匯出失敗：{ex.Message}");
                return 1;
            }
        });
        return masks;
    }

    /// <summary>檔名取語意名稱末段（Apple 的 aux 型別）加序號；無名時只用序號。</summary>
    private static string MaskFileStem(SemanticMask mask, int index)
    {
        var leaf = mask.Name.Split(':', '/').LastOrDefault(s => s.Length > 0);
        var stem = leaf is null ? $"mask-{index}" : $"{Sanitize(leaf)}-{index}";
        return mask.InstanceId is { } id ? $"{stem}-{Sanitize(id)}" : stem;
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars).Trim('-') is { Length: > 0 } s ? s : "mask";
    }

    /// <summary>顯影座標的 8-bit 遮罩依 EXIF 方向轉正後寫灰階 PNG（與 <c>RgbImage.Oriented</c> 同一映射）。</summary>
    private static void WriteMaskPng(DevelopedMask mask, Orientation orientation, string output)
    {
        var data = OrientMask(mask.Data, mask.Width, mask.Height, orientation, out var w, out var h);
        using var image = new MagickImage();
        image.ReadPixels(data, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "I"));
        image.Format = MagickFormat.Png;
        image.Write(output);
    }

    private static byte[] OrientMask(byte[] data, int w, int h, Orientation o, out int outW, out int outH)
    {
        var (flip, turns) = o.ToParts();
        if (flip)
        {
            var flipped = new byte[data.Length];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                flipped[y * w + (w - 1 - x)] = data[y * w + x];
            data = flipped;
        }
        for (var t = 0; t < turns; t++)
        {
            var rotated = new byte[data.Length];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                rotated[x * h + (h - 1 - y)] = data[y * w + x];
            (data, w, h) = (rotated, h, w);
        }
        (outW, outH) = (w, h);
        return data;
    }
}
