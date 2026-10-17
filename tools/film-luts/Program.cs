// 從 RawTherapee HaldCLUT.zip 以 HTTP Range 只抓 looks.json 列出的 PNG，轉成 33³ .fblut。
// dotnet run --project tools/film-luts -- <looks.json> <輸出資料夾>
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using FilmBatch.Core.Film;
using ImageMagick;

const string Url = "http://rawtherapee.com/shared/HaldCLUT.zip";
const long ExpectedLength = 421_602_289;
if (args.Length != 2) { Console.Error.WriteLine("用法：film-luts <looks.json> <輸出資料夾>"); return 2; }
var manifest = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
var size = manifest.GetProperty("size").GetInt32();
Directory.CreateDirectory(args[1]);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
async Task<byte[]> Range(long from, long to)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, Url);
    request.Headers.Range = new RangeHeaderValue(from, to);
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    // 伺服器若忽略 Range 會回 200 並送出整包 402 MB，直接中止。
    if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        throw new InvalidDataException($"伺服器未支援 Range（狀態 {(int)response.StatusCode}）。");
    if (response.Content.Headers.ContentRange?.Length is { } total && total != ExpectedLength)
        throw new InvalidDataException($"HaldCLUT.zip 大小 {total} 與預期 {ExpectedLength} 不同，來源可能已更新。");
    return await response.Content.ReadAsByteArrayAsync();
}

// 讀 zip 中央目錄（檔尾 64 KB 內的 End of Central Directory）
var tail = await Range(ExpectedLength - 65536, ExpectedLength - 1);
var eocd = tail.AsSpan().LastIndexOf("PK\x05\x06"u8);
var count = BitConverter.ToUInt16(tail, eocd + 10);
var cdSize = BitConverter.ToUInt32(tail, eocd + 12);
var cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
var cd = await Range(cdOffset, cdOffset + cdSize - 1);
var entries = new Dictionary<string, (int Method, long Compressed, long Offset)>(StringComparer.Ordinal);
for (int p = 0, i = 0; i < count; i++)
{
    var method = BitConverter.ToUInt16(cd, p + 10);
    var compressed = BitConverter.ToUInt32(cd, p + 20);
    int nameLength = BitConverter.ToUInt16(cd, p + 28), extra = BitConverter.ToUInt16(cd, p + 30), comment = BitConverter.ToUInt16(cd, p + 32);
    var offset = BitConverter.ToUInt32(cd, p + 42);
    entries[System.Text.Encoding.UTF8.GetString(cd, p + 46, nameLength)] = (method, compressed, offset);
    p += 46 + nameLength + extra + comment;
}

foreach (var look in manifest.GetProperty("looks").EnumerateArray())
{
    var key = look.GetProperty("key").GetString()!;
    var source = look.GetProperty("source").GetString()!;
    if (!entries.TryGetValue(source, out var entry)) throw new InvalidDataException($"zip 內找不到 {source}");
    var header = await Range(entry.Offset, entry.Offset + 29);
    var dataStart = entry.Offset + 30 + BitConverter.ToUInt16(header, 26) + BitConverter.ToUInt16(header, 28);
    var raw = await Range(dataStart, dataStart + entry.Compressed - 1);
    byte[] png = entry.Method switch
    {
        0 => raw,
        8 => Inflate(raw),
        _ => throw new InvalidDataException($"不支援的壓縮方式 {entry.Method}：{source}"),
    };
    using var image = new MagickImage(png);
    // 多數為 level 12（1728²，144³）；Kodak Ektar 100 為 level 16（4096²，256³）。
    var level = (int)Math.Round(Math.Cbrt(image.Width));
    if (image.Width != image.Height || level * level * level != image.Width)
        throw new InvalidDataException($"{source}（{image.Width}×{image.Height}）不是 Hald CLUT。");
    var pixels = image.GetPixelsUnsafe().ToShortArray(PixelMapping.RGB)!;
    var rgb = new float[pixels.Length];
    for (var i = 0; i < rgb.Length; i++) rgb[i] = pixels[i] / 65535f;
    var lut = FilmLut.FromHald(rgb, level).Resample(size);
    await using var output = File.Create(Path.Combine(args[1], key + ".fblut"));
    FilmLutFile.Write(output, lut);
    Console.WriteLine($"{key} ← {source}（level {level}）");
}
return 0;

static byte[] Inflate(byte[] data)
{
    using var input = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
    using var output = new MemoryStream();
    input.CopyTo(output);
    return output.ToArray();
}
