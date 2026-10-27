using System.Security.Cryptography;

namespace FilmBatch.Core.Film;

/// <summary>使用者匯入的底片 LUT。檔在 <see cref="AppPaths.UserLuts"/>，不進內建清單。</summary>
public static class UserLutLibrary
{
    private static readonly Dictionary<string, FilmLut> Luts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal);
    private static string? _loadedFrom;

    public static IReadOnlyList<(string Key, string Name)> All
    {
        get
        {
            EnsureLoaded();
            return Names.OrderBy(p => p.Value, StringComparer.Ordinal).Select(p => (p.Key, p.Value)).ToArray();
        }
    }

    public static bool IsUserKey(string key)
    {
        if (key.Length != 21 || !key.StartsWith("user-", StringComparison.Ordinal)) return false;
        for (var i = 5; i < key.Length; i++)
        {
            var c = key[i];
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }

    public static bool TryGet(string key, out FilmLut? lut)
    {
        EnsureLoaded();
        return Luts.TryGetValue(key, out lut);
    }

    public static string Import(FilmLut lut, string displayName)
    {
        EnsureLoaded();
        Directory.CreateDirectory(AppPaths.UserLuts);
        using var buffer = new MemoryStream();
        FilmLutFile.Write(buffer, lut);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
        var key = "user-" + hash;
        var name = Sanitize(displayName);
        File.WriteAllBytes(Path.Combine(AppPaths.UserLuts, key + ".fblut"), bytes);
        File.WriteAllText(Path.Combine(AppPaths.UserLuts, key + ".name"), name);
        Luts[key] = FilmLutFile.Read(new MemoryStream(bytes));
        Names[key] = name;
        return key;
    }

    /// <summary>測試在改 <see cref="AppPaths.OverrideRoot"/> 後呼叫。</summary>
    public static void Reset() => _loadedFrom = null;

    private static void EnsureLoaded()
    {
        var dir = AppPaths.UserLuts;
        if (_loadedFrom == dir) return;
        Luts.Clear();
        Names.Clear();
        _loadedFrom = dir;
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "user-*.fblut"))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (!IsUserKey(key)) continue;
            using var stream = File.OpenRead(file);
            Luts[key] = FilmLutFile.Read(stream);
            var nameFile = Path.Combine(dir, key + ".name");
            Names[key] = File.Exists(nameFile) ? Sanitize(File.ReadAllText(nameFile)) : key;
        }
    }

    private static string Sanitize(string name)
    {
        var trimmed = name.ReplaceLineEndings(" ").Trim();
        if (trimmed.Length == 0) return "LUT";
        return trimmed.Length <= 48 ? trimmed : trimmed[..48];
    }
}
