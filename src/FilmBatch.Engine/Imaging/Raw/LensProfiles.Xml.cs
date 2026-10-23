using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace FilmBatch.Engine.Imaging.Raw;

internal static partial class LensProfiles
{
    private const long MaxFileBytes = 16 << 20;
    private const long MaxDatabaseBytes = 64 << 20;

    private const int MaxCachedDatabases = 8;
    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, (string Stamp, Database Db)> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 以資料夾內 XML 的名稱、大小與修改時間為戳記快取解析結果：每張 RAW 讀取都會查詢資料庫，
    /// 重複解析整個 Lensfun 資料夾（數 MB XML、兩趟）是預覽變慢的主因之一。讀取失敗不快取，下次照常回報。
    /// </summary>
    private static Database Load(string directory)
    {
        var files = Directory.EnumerateFiles(directory).Where(p => Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            .Take(2001).ToArray();
        if (files.Length > 2000) throw new InvalidDataException("Lens database exceeds 2000 XML files.");
        Array.Sort(files, StringComparer.Ordinal);
        var key = Path.GetFullPath(directory);
        var stamp = string.Join("\n", files.Select(f =>
        {
            var info = new FileInfo(f);
            return $"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }));
        lock (CacheLock)
            if (Cache.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit.Db;
        var db = Parse(files);
        lock (CacheLock)
        {
            if (Cache.Count >= MaxCachedDatabases && !Cache.ContainsKey(key)) Cache.Clear();
            Cache[key] = (stamp, db);
        }
        return db;
    }

    private static Database Parse(string[] files)
    {
        var db = new Database([], []);
        long total = 0;
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            if (stream.Length > MaxFileBytes || total + stream.Length > MaxDatabaseBytes)
                throw new InvalidDataException("Lens database exceeds the input size limit.");
            // Read at most the byte cap even if the file grows after opening it.
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (memory.Length + count > MaxFileBytes || total + memory.Length + count > MaxDatabaseBytes) throw new InvalidDataException("Lens XML exceeds the input size limit.");
                memory.Write(buffer, 0, count);
            }
            total += memory.Length;
            memory.Position = 0;
            // The official database starts every file with <!DOCTYPE lensdatabase SYSTEM "lensfun-database.dtd">.
            // Ignore skips the DOCTYPE without resolving it (no resolver) or defining its entities,
            // so entity references still fail as undeclared.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
                MaxCharactersInDocument = MaxFileBytes, MaxCharactersFromEntities = 1024,
                IgnoreComments = true, IgnoreProcessingInstructions = true,
            };
            // Validate nesting and node count before allocating the tree.
            using (var reader = XmlReader.Create(memory, settings))
            {
                var nodes = 0;
                while (reader.Read())
                    if (reader.Depth > 32 || ++nodes > 500000) throw new InvalidDataException("Lens XML is too deeply nested or has too many nodes.");
            }
            memory.Position = 0;
            using var treeReader = XmlReader.Create(memory, settings);
            var root = XDocument.Load(treeReader).Root;
            if (root?.Name != "lensdatabase") throw new InvalidDataException("Lens XML must contain a lensdatabase root.");
            foreach (var c in root.Elements("camera"))
            {
                var maker = Text(c, "maker"); var model = Text(c, "model"); var mount = Text(c, "mount");
                var crop = Number(c.Element("cropfactor")?.Value, 1);
                if (maker.Length > 0 && model.Length > 0 && mount.Length > 0 && CropValid(crop)) db.Cameras.Add(new(maker, model, mount, crop));
            }
            foreach (var lens in root.Elements("lens"))
            {
                var maker = Text(lens, "maker");
                var models = lens.Elements("model").Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToArray();
                var mounts = lens.Elements("mount").Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToArray();
                if (maker.Length == 0 || models.Length == 0 || mounts.Length == 0) continue;
                foreach (var cal in lens.Elements("calibration"))
                {
                    var crop = Number(cal.Attribute("cropfactor")?.Value ?? lens.Element("cropfactor")?.Value, 1);
                    var aspect = Aspect(cal.Attribute("aspect-ratio")?.Value ?? lens.Element("aspect-ratio")?.Value);
                    if (!CropValid(crop) || !double.IsFinite(aspect) || aspect < 1 || aspect > 10) continue;
                    var distortion = new List<Point>(); var tca = new List<Point>(); var vignette = new List<Point>();
                    foreach (var point in cal.Elements())
                    {
                        var focal = Number(point.Attribute("focal")?.Value, double.NaN);
                        if (!Positive(focal)) continue;
                        var model = point.Attribute("model")?.Value;
                        string[]? names = (point.Name.LocalName, model) switch
                        {
                            ("distortion", "poly3") => ["k1"],
                            ("distortion", "poly5") => ["k1", "k2"],
                            ("distortion", "ptlens") => ["a", "b", "c"],
                            ("tca", "linear") => ["kr", "kb"],
                            ("tca", "poly3") => ["vr", "vb", "cr", "cb", "br", "bb"],
                            ("vignetting", "pa") => ["k1", "k2", "k3"],
                            _ => null,
                        };
                        if (names is null) continue;
                        var k = names.Select(n => Number(point.Attribute(n)?.Value,
                            n is "kr" or "kb" or "vr" or "vb" ? 1 : 0)).ToArray();
                        if (k.Any(x => !double.IsFinite(x) || Math.Abs(x) > 100)) continue;
                        var aperture = Number(point.Attribute("aperture")?.Value, 0);
                        var distance = Number(point.Attribute("distance")?.Value, 1000);
                        var value = new Point(focal, model!, k, aperture, distance);
                        if (point.Name == "distortion") distortion.Add(value);
                        else if (point.Name == "tca") tca.Add(value);
                        else if (Positive(aperture) && Positive(distance)) vignette.Add(value);
                    }
                    var type = Text(lens, "type");
                    db.Profiles.Add(new(maker, models, mounts, crop, aspect,
                        type.Length == 0 || type.Equals("rectilinear", StringComparison.OrdinalIgnoreCase),
                        distortion.OrderBy(p => p.Focal).ToArray(), tca.OrderBy(p => p.Focal).ToArray(), vignette.OrderBy(p => p.Focal).ToArray()));
                }
            }
        }
        return db;
    }
    private static string Text(XElement parent, string name) => parent.Elements(name).FirstOrDefault(e => e.Attribute("lang") is null)?.Value.Trim() ?? "";
    private static double Number(string? text, double fallback) => text is null ? fallback :
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
    private static bool CropValid(double crop) => double.IsFinite(crop) && crop >= .05 && crop <= 20;
    private static double Aspect(string? text)
    {
        if (text is null) return 1.5;
        var parts = text.Split(':');
        var a = Number(parts[0], double.NaN);
        if (parts.Length == 2) a /= Number(parts[1], double.NaN);
        else if (parts.Length != 1) return double.NaN;
        return a < 1 ? 1 / a : a;
    }
}
