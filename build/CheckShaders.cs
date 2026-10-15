#:project ../src/FilmBatch.Gpu/FilmBatch.Gpu.csproj
// 驗證編譯後嵌入的 WGSL，使用產品的 ShaderLibrary 展開 #include。
// dotnet run build/CheckShaders.cs -- --naga <執行檔>
// --shaders <Shaders 根目錄> 可驗證開發中的 shader；省略時驗證嵌入資源。

using System.Diagnostics;
using FilmBatch.Gpu.ShaderSystem;

var naga = Arg("--naga") ?? "naga";
var devFolder = Arg("--shaders");
const string prefix = "FilmBatch.Shaders/";
var files = (devFolder is null
    ? typeof(ShaderLibrary).Assembly.GetManifestResourceNames()
        .Select(name => name.Replace('\\', '/'))
        .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".wgsl", StringComparison.Ordinal))
        .Select(name => name[prefix.Length..])
    : Directory.EnumerateFiles(devFolder, "*.wgsl", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(devFolder, file).Replace('\\', '/')))
    .Order(StringComparer.Ordinal).ToArray();
if (files.Length == 0)
{
    Console.Error.WriteLine("找不到可驗證的 WGSL shader。");
    return 1;
}

var temp = Path.Combine(Path.GetTempPath(), $"filmbatch-wgsl-{Guid.NewGuid():N}");
Directory.CreateDirectory(temp);
var ok = true;
try
{
    foreach (var file in files)
    {
        var separator = file.IndexOf('/');
        if (separator < 2 || file[0] != 'v' || !int.TryParse(file.AsSpan(1, separator - 1), out var version))
            throw new InvalidDataException($"Shader 缺少 vN 版本目錄：{file}");
        var expanded = new ShaderLibrary(version, devFolder).Load(file[(separator + 1)..]);
        var input = Path.Combine(temp, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        await File.WriteAllTextAsync(input, expanded);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(naga, [input])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("無法啟動 naga。");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await stdout + await stderr).Trim();
            if (process.ExitCode == 0)
                Console.WriteLine($"[PASS] {file}");
            else
            {
                ok = false;
                Console.Error.WriteLine($"[FAIL] {file}\n{output}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"無法執行 naga：{ex.Message}。請安裝 naga-cli 或使用 --naga 指定路徑。");
            return 2;
        }
    }
}
finally
{
    Directory.Delete(temp, recursive: true);
}
Console.WriteLine(ok ? $"WGSL 驗證通過（{files.Length} 個檔案）" : "WGSL 驗證失敗");
return ok ? 0 : 1;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return null;
    if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"{name} 需要值。");
    return args[index + 1];
}
