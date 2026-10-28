using FilmBatch.Core.Film;
#if !FILMBATCH_APPLE_IMAGING
using ImageMagick;
#endif

namespace FilmBatch.Engine.Film;

/// <summary>把使用者的 .cube、.fblut 或 Hald PNG 轉成底片槽 LUT。.cube 使用呼叫端指定的色彩空間；.fblut 與 Hald PNG 的色彩空間記錄在檔案本身，不看 <paramref name="colorSpace"/>。</summary>
public static class LutFileImport
{
    public static FilmLut Open(string path, FilmLutColorSpace colorSpace = FilmLutColorSpace.Srgb)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".cube", StringComparison.OrdinalIgnoreCase))
            return CubeLut.Parse(File.ReadAllText(path), colorSpace);
        if (ext.Equals(".fblut", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return FilmLutFile.Read(stream);
        }
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
            return OpenHaldPng(path); // Hald PNG 固定是 sRGB，不看 colorSpace。
        throw new InvalidDataException("只支援 .cube、.fblut 與 Hald PNG。");
    }

    private static FilmLut OpenHaldPng(string path)
    {
#if FILMBATCH_APPLE_IMAGING
        throw new InvalidDataException("此平台不能讀 Hald PNG。");
#else
        using var image = new MagickImage(path);
        image.Depth = 16;
        var level = (int)Math.Round(Math.Cbrt(image.Width));
        if (image.Width != image.Height || level < 2 || level * level * level != image.Width)
            throw new InvalidDataException($"{image.Width}×{image.Height} 不是 Hald CLUT。");
        var pixels = image.GetPixelsUnsafe().ToShortArray(PixelMapping.RGB)!;
        var rgb = new float[pixels.Length];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = Math.Clamp(pixels[i] / 65535f, 0, 1);
        var lut = FilmLut.FromHald(rgb, level);
        return lut.Size > FilmLutFile.MaxSize ? lut.Resample(FilmLutFile.MaxSize) : lut;
#endif
    }
}
