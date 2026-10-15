// FilmBatch 建置腳本（.NET 10 檔案型程式，計畫書 9.3）
//
//   dotnet run build/Build.cs -- --rid win-x64 --smoke --zip
//   dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip
//
// macOS 在 Mac 上組裝 .app（含本機 ad-hoc 簽章）；正式散佈仍需 Developer ID 與公證。
// CLI 維持獨立的 self-contained 資料夾，避免覆寫 GUI 修剪後的框架元件。

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

var rid = Arg("--rid") ?? (OperatingSystem.IsMacOS()
    ? $"osx-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}" : "win-x64");
var smoke = args.Contains("--smoke");
var zip = args.Contains("--zip");
var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", ".."));
var dist = Path.Combine(root, "dist", rid);
var isMac = rid is "osx-arm64" or "osx-x64";
var bundle = Path.Combine(dist, "FilmBatch.app");
var gui = isMac ? Path.Combine(bundle, "Contents", "MacOS") : dist;
var cli = Path.Combine(dist, "cli");

if (rid is not ("win-x64" or "osx-arm64" or "osx-x64"))
{
    Console.Error.WriteLine($"不支援 {rid}；請使用 win-x64、osx-arm64 或 osx-x64。");
    return 2;
}
if (isMac && !OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("macOS 封裝需要在 Mac 執行（sips、iconutil、codesign、ditto）。");
    return 2;
}
if (smoke && !isMac && !OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Windows 冒煙測試需要在 Windows 執行。");
    return 2;
}

Step("清除輸出", () => { if (Directory.Exists(dist)) Directory.Delete(dist, recursive: true); });

// CLI 發佈到 cli 子資料夾：兩個各自 self-contained（且 GUI 有 trimming）的發佈疊在同一資料夾會互相覆寫框架組件，
// 導致 GUI 啟動時 TypeLoadException（M0 冒煙建置發現）。計畫書「同一資料夾」的做法於 M7 定案。
Run("dotnet", ["publish", Path.Combine(root, "src", "FilmBatch.App"), "-c", "Release", "-r", rid, "-o", gui]);
Run("dotnet", ["publish", Path.Combine(root, "src", "FilmBatch.Cli"), "-c", "Release", "-r", rid,
    "--self-contained", "-p:PublishReadyToRun=true", "-o", cli]);

Step("授權聲明", () =>
{
    var notices = Path.Combine(root, "THIRD-PARTY-NOTICES.txt");
    if (File.Exists(notices)) File.Copy(notices, Path.Combine(dist, "THIRD-PARTY-NOTICES.txt"), overwrite: true);
});

if (isMac)
{
    var resources = Path.Combine(bundle, "Contents", "Resources");
    Directory.CreateDirectory(resources);
    File.Copy(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"), Path.Combine(resources, "THIRD-PARTY-NOTICES.txt"));
    var version = AssemblyName.GetAssemblyName(Path.Combine(gui, "FilmBatch.dll")).Version!.ToString(3);
    File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleExecutable</key><string>FilmBatch</string>
            <key>CFBundleIdentifier</key><string>app.filmbatch.FilmBatch</string>
            <key>CFBundleName</key><string>FilmBatch</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
            <key>CFBundleVersion</key><string>{{version}}</string>
            <key>CFBundleShortVersionString</key><string>{{version}}</string>
            <key>CFBundleIconFile</key><string>FilmBatch.icns</string>
            <key>CFBundleDevelopmentRegion</key><string>zh_TW</string>
            <key>CFBundleLocalizations</key><array><string>zh_TW</string><string>en</string></array>
            <!-- WebGPUSharp 0.5.7 內附 Dawn 的 LC_BUILD_VERSION：兩個 Mac 架構皆為 minos 26.0。 -->
            <key>LSMinimumSystemVersion</key><string>26.0</string>
            <key>NSHighResolutionCapable</key><true/>
        </dict>
        </plist>
        """);

    // 重用現有圖示；中間檔留在暫存資料夾，不放進發佈封裝。
    var iconset = Path.Combine(Path.GetTempPath(), $"filmbatch-{Guid.NewGuid():N}.iconset");
    Directory.CreateDirectory(iconset);
    try
    {
        foreach (var size in new[] { 16, 32, 128, 256, 512 })
        foreach (var scale in new[] { 1, 2 })
        {
            var pixels = (size * scale).ToString();
            var name = $"icon_{size}x{size}{(scale == 2 ? "@2x" : "")}.png";
            Run("sips", ["-s", "format", "png", "-z", pixels, pixels,
                Path.Combine(root, "src", "FilmBatch.App", "Assets", "icon.ico"), "--out", Path.Combine(iconset, name)]);
        }
        Run("iconutil", ["-c", "icns", iconset, "-o", Path.Combine(resources, "FilmBatch.icns")]);
    }
    finally
    {
        Directory.Delete(iconset, recursive: true);
    }

    // MacOS 目錄中的受控 DLL 也會被 codesign 視為巢狀程式碼；逐檔簽署後再簽 bundle。
    // 非 Mach-O 檔的簽章存於延伸屬性，壓縮時必須用 ditto 保留。
    // 本機 ad-hoc 簽章不啟用 hardened runtime；正式散佈需另加 Developer ID、公證與 JIT entitlement。
    var appHost = Path.Combine(gui, "FilmBatch");
    foreach (var file in Directory.EnumerateFiles(gui, "*", SearchOption.AllDirectories))
        if (file != appHost) // 簽 apphost 會觸發整個 bundle 驗證，留到最後與 bundle 一起簽。
            Run("codesign", ["--force", "--sign", "-", file]);
    Run("codesign", ["--force", "--sign", "-", bundle]);
    Run("codesign", ["--verify", "--deep", "--strict", bundle]);
}

if (smoke)
{
    var result = Path.Combine(dist, "..", $"smoke-{rid}.txt");
    File.Delete(result);
    var code = Run(Path.Combine(gui, isMac ? "FilmBatch" : "FilmBatch.exe"), ["--smoke", result], check: false);
    Console.WriteLine(File.Exists(result) ? File.ReadAllText(result) : "（沒有冒煙測試輸出）");
    if (code != 0) return code;
    if (!File.Exists(result)) return 1;
    Run(Path.Combine(cli, isMac ? "filmbatch-cli" : "filmbatch-cli.exe"), ["gpu-info"]);
}

if (zip)
{
    var archive = Path.Combine(root, "dist", $"FilmBatch-{rid}.zip");
    Step("壓縮", () =>
    {
        File.Delete(archive);
        if (isMac)
            Run("ditto", ["-c", "-k", "--sequesterRsrc", dist, archive]);
        else
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

int Run(string file, string[] arguments, bool check = true)
{
    Console.WriteLine($"== {Path.GetFileName(file)} {string.Join(' ', arguments)}");
    var start = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = root };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var p = Process.Start(start)!;
    p.WaitForExit();
    if (check && p.ExitCode != 0) throw new Exception($"{file} 結束代碼 {p.ExitCode}");
    return p.ExitCode;
}
