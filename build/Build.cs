// FilmBatch 建置腳本（.NET 10 檔案型程式，計畫書 9.3）
//
//   dotnet run build/Build.cs -- --rid win-x64 --smoke
//
// 步驟：發佈 GUI → 發佈 CLI（cli 子資料夾）→ 複製授權聲明 → 冒煙測試 → 壓縮。
// M0 只支援 Windows；macOS（.app 組裝、簽章）與 Linux 於跨平台建置時加入。
// naga 驗證 WGSL 待 CI 安裝 naga-cli 後加入。

using System.Diagnostics;
using System.IO.Compression;

var rid = Arg("--rid") ?? "win-x64";
var smoke = args.Contains("--smoke");
var zip = args.Contains("--zip");
var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", ".."));
var dist = Path.Combine(root, "dist", rid);

if (!rid.StartsWith("win-", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"M0 只支援 Windows（收到 {rid}）。");
    return 2;
}

Step("清除輸出", () => { if (Directory.Exists(dist)) Directory.Delete(dist, recursive: true); });

// CLI 發佈到 cli 子資料夾：兩個各自 self-contained（且 GUI 有 trimming）的發佈疊在同一資料夾會互相覆寫框架組件，
// 導致 GUI 啟動時 TypeLoadException（M0 冒煙建置發現）。計畫書「同一資料夾」的做法於 M7 定案。
Run("dotnet", $"publish \"{Path.Combine(root, "src", "FilmBatch.App")}\" -c Release -r {rid} -o \"{dist}\"");
Run("dotnet", $"publish \"{Path.Combine(root, "src", "FilmBatch.Cli")}\" -c Release -r {rid} --self-contained -p:PublishReadyToRun=true -o \"{Path.Combine(dist, "cli")}\"");

Step("授權聲明", () =>
{
    var notices = Path.Combine(root, "THIRD-PARTY-NOTICES.txt");
    if (File.Exists(notices)) File.Copy(notices, Path.Combine(dist, "THIRD-PARTY-NOTICES.txt"), overwrite: true);
});

if (smoke)
{
    var result = Path.Combine(dist, "..", $"smoke-{rid}.txt");
    var code = Run(Path.Combine(dist, "FilmBatch.exe"), $"--smoke \"{result}\"", check: false);
    Console.WriteLine(File.Exists(result) ? File.ReadAllText(result) : "（沒有冒煙測試輸出）");
    if (code != 0) return code;
    Run(Path.Combine(dist, "cli", "filmbatch-cli.exe"), "gpu-info");
}

if (zip)
{
    var archive = Path.Combine(root, "dist", $"FilmBatch-{rid}.zip");
    Step("壓縮", () =>
    {
        File.Delete(archive);
        ZipFile.CreateFromDirectory(dist, archive, CompressionLevel.Optimal, includeBaseDirectory: false);
    });
}

Console.WriteLine($"完成：{dist}");
return 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void Step(string name, Action action)
{
    Console.WriteLine($"== {name}");
    action();
}

int Run(string file, string arguments, bool check = true)
{
    Console.WriteLine($"== {Path.GetFileName(file)} {arguments}");
    using var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false })!;
    p.WaitForExit();
    if (check && p.ExitCode != 0) throw new Exception($"{file} 結束代碼 {p.ExitCode}");
    return p.ExitCode;
}
