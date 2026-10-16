using FilmBatch.Core;
using FilmBatch.Core.Random;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Engine.Rendering;

internal sealed class EffectImage(SourceImage image)
{
    private float[]? _linear;
    public SourceImage Image { get; } = image;
    public float[] Linear => _linear ??= CpuPipeline.DecodeLinear(Image);
}

/// <summary>兩個算圖後端共用的素材解析；只有空材質庫依規格改用程序化，指定檔案失敗必須回報。</summary>
internal sealed class EffectAssets(EffectRendererOptions options)
{
    private readonly string _textures = Path.GetFullPath(options.TexturesFolder ?? AppPaths.Textures);
    private readonly Dictionary<(string Path, int Edge), (long Stamp, long Length, EffectImage Image)> _images = [];

    public void Prepare(SourceImage source, ModuleParams parameters)
    {
        var edge = Math.Max(source.Width, source.Height);
        if (parameters.DoubleOn && parameters.Opacity > 0 && parameters.DoubleSource != 0)
        {
            if (string.IsNullOrWhiteSpace(parameters.DoublePath))
                throw new InvalidDataException(CoreText.T("雙重曝光需要來源路徑。", "Double exposure needs a source path."));
            var path = parameters.DoublePath;
            if (!Path.IsPathRooted(path))
            {
                var folder = source.FilePath is null ? null : Path.GetDirectoryName(source.FilePath);
                if (folder is null) throw new InvalidDataException(CoreText.T("相對素材路徑需要照片的來源路徑。", "A relative asset path needs the photo's source path."));
                path = Path.Combine(folder, path);
            }
            parameters.DoubleAsset = Load(path, edge);
        }
        if (parameters.LeakOn && parameters.LeakAmount > 0 && parameters.LeakMode == 1)
        {
            parameters.LeakAsset = Texture("leaks", parameters.LeakTexture, parameters.SeedLightLeak, edge);
            if (parameters.LeakAsset is null) parameters.LeakMode = 0;
        }
        if (parameters.DirtOn && parameters.DirtStrength > 0 && parameters.DirtMode == 1)
        {
            parameters.DirtAsset = Texture("dust", "random", parameters.SeedDirt, edge);
            if (parameters.DirtAsset is null) parameters.DirtMode = 0;
        }
    }

    private EffectImage? Texture(string category, string name, uint seed, int edge)
    {
        var folder = Path.Combine(_textures, category);
        if (name is not ("random" or ""))
            return Load(Path.IsPathRooted(name) ? name : Path.Combine(folder, name), edge);
        if (!Directory.Exists(folder)) return null;
        var files = Directory.EnumerateFiles(folder)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff")
            .Order(StringComparer.Ordinal).ToArray();
        return files.Length == 0 ? null : Load(files[SeedHash.Pcg(seed) % (uint)files.Length], edge);
    }

    private EffectImage Load(string path, int edge)
    {
        path = Path.GetFullPath(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(CoreText.T("找不到效果素材。", "Effect asset not found."), path);
        var key = (path, edge);
        if (_images.TryGetValue(key, out var cached) && cached.Stamp == info.LastWriteTimeUtc.Ticks && cached.Length == info.Length)
            return cached.Image;
        var image = new EffectImage(ImageLoader.Load(path, edge));
        if (_images.Count >= 8) _images.Clear();
        _images[key] = (info.LastWriteTimeUtc.Ticks, info.Length, image);
        return image;
    }
}
