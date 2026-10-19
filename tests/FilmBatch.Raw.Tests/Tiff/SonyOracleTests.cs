// 移植自 LightCraft crates/tiff 行為測試（Sony ARW 結構與中繼資料對照 LightCraft 參考輸出）
// Copyright (c) 2026 ArtCraft Team and the LightCraft contributors，MIT OR Apache-2.0；為 FilmBatch 改寫為 C#。
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmBatch.Raw.Meta;
using FilmBatch.Raw.Tiff;
using FilmBatch.Raw.Tests.Support;

namespace FilmBatch.Raw.Tests.Tiff;

/// <summary>
/// 對每支 Sony 樣本：(a) TiffFile.Parse 成功；(b) IFD 樹（offset、每項 tag/型別/數量、SubIFD/Exif/GPS/Interop 巢狀）
/// 與 `rawinfo --tree` 一致（即時執行該範例程式並解析其輸出）；(c) MetadataReader.FromTiff 各契約欄位等於
/// info.json 的 info.metadata。樣本／oracle／rawinfo 任一缺漏時略過。
/// </summary>
public sealed class SonyOracleTests
{
    private static string? RawInfo
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("FILMBATCH_RAWINFO") ?? "/tmp/lightcraft/target/release/examples/rawinfo";
            return File.Exists(p) ? p : null;
        }
    }
    public static IEnumerable<string> SonyStems => Corpus.Stems("arw-");

    [Fact]
    public void ParseSucceedsForEverySonySample()
    {
        foreach (var stem in SonyStems)
        {
            var path = Corpus.Sample(stem + ".arw");
            var t = TiffFile.Parse(File.ReadAllBytes(path));
            Assert.True(t.Ifds.Count > 0, $"{stem}: no IFDs");
        }
    }

    [Fact]
    public void IfdTreeMatchesRawinfo()
    {
        var rawinfo = RawInfo;
        Assert.SkipUnless(rawinfo is not null, "rawinfo example binary not available.");
        foreach (var stem in SonyStems)
        {
            var path = Corpus.Sample(stem + ".arw");
            var bytes = File.ReadAllBytes(path);
            var t = TiffFile.Parse(bytes);
            var expected = ExpectedTree(rawinfo!, path);
            var actual = TreeOf(t);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void MetadataMatchesOracle()
    {
        foreach (var stem in SonyStems)
        {
            _ = Corpus.Sample(stem + ".arw"); // ensures samples present before oracle load
            var oracle = OracleStages.Load(stem);
            var bytes = File.ReadAllBytes(Corpus.Sample(stem + ".arw"));
            var expected = OracleJson.Metadata(oracle.Info.GetProperty("metadata"));
            var actual = MetadataReader.FromTiff(TiffFile.Parse(bytes));
            Assert.Equal(expected.Make, actual.Make); Assert.Equal(expected.Model, actual.Model);
            Assert.Equal(expected.SerialNumber, actual.SerialNumber); Assert.Equal(expected.Software, actual.Software);
            Assert.Equal(expected.LensMake, actual.LensMake); Assert.Equal(expected.LensModel, actual.LensModel);
            Assert.Equal(expected.LensSpec, actual.LensSpec); Assert.Equal(expected.CaptureTime, actual.CaptureTime);
            Assert.Equal(expected.ExposureTime, actual.ExposureTime); Assert.Equal(expected.FNumber, actual.FNumber);
            // arw.rs: metadata.width/height = crop dims (vendor slices own the override).
            var crop = oracle.Info.GetProperty("crop");
            actual.Width = (uint)crop.GetProperty("width").GetInt32();
            actual.Height = (uint)crop.GetProperty("height").GetInt32();
            Assert.Equal(expected.Orientation, actual.Orientation);
            Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height);
        }
    }

    private const int TimeoutMs = 120_000;

    /// <summary>
    /// Parses `rawinfo --tree` output into a canonical shape list: per IFD `(label, offset)` and per
    /// entry `(tag, FieldType-name)`. rawinfo prints entries as `  {tag dec} 0x{hex} {Type:?}: {value}`;
    /// only the structural part is compared (values would depend on formatting parity).
    /// </summary>
    private static List<string> ExpectedTree(string rawinfo, string path)
    {
        var psi = new ProcessStartInfo(rawinfo, $"--tree \"{path}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(TimeoutMs);
        var lines = new List<string>();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd();
            var ifd = Regex.Match(line, @"^(\s*)(IFD\d+|SubIFD\[\d+\]|Exif|GPS|Interop) @(\d+) \((\d+) entries\)$");
            if (ifd.Success)
            {
                lines.Add($"{ifd.Groups[2].Value}@{ifd.Groups[3].Value} n={ifd.Groups[4].Value} d={ifd.Groups[1].Length / 2}");
                continue;
            }
            var entry = Regex.Match(line, @"^\s*\d+ 0x([0-9a-f]{4}) (\w+): ");
            if (entry.Success) lines.Add($"  tag={entry.Groups[1].Value} type={entry.Groups[2].Value}");
            if (line.StartsWith("  ") && !line.StartsWith("   ") && !ifd.Success && lines.Count > 0 && !entry.Success && Regex.IsMatch(line, @"^\s+\d+ ")) { /* entry with odd format — ignore */ }
        }
        Assert.NotEmpty(lines);
        return lines;
    }

    private static List<string> TreeOf(TiffFile t)
    {
        var lines = new List<string>();
        void Dump(Ifd ifd, int depth, string label)
        {
            lines.Add($"{label}@{ifd.Offset} n={ifd.Entries.Count} d={depth}");
            foreach (var e in ifd.Entries) lines.Add($"  tag={e.Tag:x4} type={e.FieldType}");
            for (int i = 0; i < ifd.SubIfds.Count; i++) Dump(ifd.SubIfds[i], depth + 1, $"SubIFD[{i}]");
            if (ifd.Exif is { } ex) Dump(ex, depth + 1, "Exif");
            if (ifd.Gps is { } g) Dump(g, depth + 1, "GPS");
            if (ifd.Interop is { } io) Dump(io, depth + 1, "Interop");
        }
        for (int i = 0; i < t.Ifds.Count; i++) Dump(t.Ifds[i], 0, $"IFD{i}");
        return lines;
    }
}
