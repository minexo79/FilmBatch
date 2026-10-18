namespace FilmBatch.Core;

/// <summary>使用者資料夾（計畫書 8.1、9.2）。</summary>
public static class AppPaths
{
    public const string AppName = "FilmBatch";

    /// <summary>可由測試或可攜模式覆寫。</summary>
    public static string? OverrideRoot { get; set; }

    public static string Root => OverrideRoot ?? DefaultRoot();

    private static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", AppName);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var config = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(config, AppName);
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LayoutFile => Path.Combine(Root, "layout.json");
    public static string Presets => Path.Combine(Root, "presets");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Textures => Path.Combine(Root, "textures");

    public static string ProjectFolder(string photoFolder) => Path.Combine(photoFolder, ".filmbatch");

    /// <summary>專案內引用的路徑一律存相對路徑並以 / 分隔。</summary>
    public static string ToProjectRelative(string photoFolder, string fullPath) =>
        Path.GetRelativePath(photoFolder, fullPath).Replace('\\', '/');

    /// <summary>原子寫檔：先寫暫存檔再取代，避免當機時檔案損毀。</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
