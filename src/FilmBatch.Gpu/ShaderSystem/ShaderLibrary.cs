using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace FilmBatch.Gpu.ShaderSystem;

/// <summary>
/// 讀取嵌入的 WGSL（<c>FilmBatch.Shaders/v1/...</c>）並處理 <c>#include "path"</c>。
/// WGSL 本身沒有 include 機制，因此由這個簡易前處理器展開；同一檔案只會展開一次。
/// </summary>
public sealed partial class ShaderLibrary
{
    private readonly Assembly _assembly = typeof(ShaderLibrary).Assembly;
    private readonly string _version;
    private readonly string? _devFolder;

    /// <param name="pipelineVersion">shader 版本資料夾（v1、v2…）。</param>
    /// <param name="devFolder">開發模式：從原始碼資料夾讀取（--shader-dev）。</param>
    public ShaderLibrary(int pipelineVersion = 1, string? devFolder = null)
    {
        _version = $"v{pipelineVersion}";
        _devFolder = devFolder;
    }

    public string Load(string path)
    {
        var sb = new StringBuilder();
        Expand(path, sb, new HashSet<string>(StringComparer.Ordinal));
        return sb.ToString();
    }

    private void Expand(string path, StringBuilder sb, HashSet<string> seen)
    {
        if (!seen.Add(path)) return;
        foreach (var line in ReadRaw(path).Split('\n'))
        {
            var m = IncludeRegex().Match(line);
            if (m.Success) Expand(m.Groups[1].Value, sb, seen);
            else sb.Append(line.TrimEnd('\r')).Append('\n');
        }
    }

    private string ReadRaw(string path)
    {
        if (_devFolder != null)
            return File.ReadAllText(Path.Combine(_devFolder, _version, path));
        var name = $"FilmBatch.Shaders/{_version}/{path}";
        // LogicalName 中的 %(RecursiveDir) 在 Windows 上會帶入反斜線，比對時統一成 /
        var actual = _assembly.GetManifestResourceNames()
            .FirstOrDefault(n => string.Equals(n.Replace('\\', '/'), name, StringComparison.Ordinal));
        using var s = (actual is null ? null : _assembly.GetManifestResourceStream(actual))
            ?? throw new FileNotFoundException($"找不到嵌入的 shader：{name}");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    [GeneratedRegex("""^\s*#include\s+"([^"]+)"\s*$""")]
    private static partial Regex IncludeRegex();
}
