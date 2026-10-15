using System.Text;

namespace FilmBatch.Engine.Exporting;

/// <summary>
/// 安全檔名：範本展開 + 非法字元清理 + 碰撞編號（計畫書第 10 章）。
/// 保證：絕不覆寫任何既有檔案（來源或舊輸出）；不產生路徑分隔字元（不逃出目的地）。
/// 支援的 token：{name} {preset} {index} {index:000} {ext}；未知 token 報錯（不靜默吞掉）。
/// </summary>
public static class ExportFileNames
{
    public static string ExtensionFor(ExportFormat format) => format switch
    {
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Tiff16 => ".tif",
        ExportFormat.Png16 => ".png",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>展開範本（不含副檔名）。{index} 為該張在批次中的 1-based 序號。</summary>
    public static string RenderTemplate(string template, string baseName, string presetName, int index)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw new InvalidDataException("檔名範本不可為空。");
        var sb = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                sb.Append(template[i]);
                continue;
            }
            var end = template.IndexOf('}', i + 1);
            if (end < 0) throw new InvalidDataException($"檔名範本缺少 '}}'：{template}");
            var token = template.Substring(i + 1, end - i - 1);
            sb.Append(token switch
            {
                "name" => baseName,
                "preset" => presetName,
                "ext" => throw new InvalidDataException("範本不可包含 {ext}（副檔名由格式決定）。"),
                "index" => index.ToString(),
                var t when t.StartsWith("index:", StringComparison.Ordinal) => FormatIndex(index, t["index:".Length..]),
                _ => throw new InvalidDataException($"未知的檔名 token：{{{token}}}（可用 name、preset、index、index:000）。"),
            });
            i = end;
        }
        var rendered = sb.ToString();
        if (rendered.Length == 0) throw new InvalidDataException("檔名範本展開後為空。");
        return Sanitize(rendered);
    }

    private static string FormatIndex(int index, string format)
    {
        if (format.Length == 0 || format.Any(c => c != '0'))
            throw new InvalidDataException($"無效的 index 格式：{{index:{format}}}（只支援 0 填充，如 {{index:000}}）。");
        return index.ToString().PadLeft(format.Length, '0');
    }

    /// <summary>移除路徑分隔與各平台非法字元；全非法時回退為 "export"。</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is '/' or '\\' || c == '\0' || char.IsControl(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*')
                sb.Append('_');
            else sb.Append(c);
        }
        var clean = sb.ToString().Trim().TrimEnd('.');
        if (clean.Length == 0) return "export";
        // Windows 保留裝置名
        if (clean is "." or "..") return "export";
        var upper = clean.ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        var stem = upper.Contains('.') ? upper[..upper.IndexOf('.')] : upper;
        if (reserved.Contains(stem)) return "_" + clean;
        return clean.Length > 120 ? clean[..120] : clean;
    }

    /// <summary>
    /// 在目的地內決定不碰撞的輸出路徑。taken 為同批已配置的檔名（OrdinalIgnoreCase）。
    /// 碰撞時加 _001、_002……；既有檔案一律保留（不覆寫、不刪除）。
    /// </summary>
    public static string ResolveOutputPath(string destination, string stem, string extension, ISet<string> taken)
    {
        var candidate = stem + extension;
        var n = 0;
        while (taken.Contains(candidate) || File.Exists(Path.Combine(destination, candidate)))
        {
            n++;
            if (n > 9999) throw new IOException("輸出檔名碰撞過多，無法配置安全檔名。");
            candidate = $"{stem}_{n:000}{extension}";
        }
        taken.Add(candidate);
        return Path.Combine(destination, candidate);
    }
}
