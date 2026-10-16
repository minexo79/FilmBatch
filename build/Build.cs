// FilmBatch 建置腳本（.NET 10 檔案型程式，計畫書 9.3）
//
//   dotnet run build/Build.cs -- --rid win-x64 --smoke --zip
//   dotnet run build/Build.cs -- --rid osx-arm64 --smoke --zip
//   dotnet run build/Build.cs -- --rid osx-arm64 --sign "Developer ID Application: ..." --notarize --zip
//
// macOS 在 Mac 上組裝 .app：
//   · 預設 ad-hoc 簽章（本機開發／測試用；Gatekeeper 不接受 ad-hoc，只供自行解壓使用）。
//   · --sign <identity>：Developer ID 簽章 + hardened runtime + entitlements（允許 JIT/未簽章可執行記憶體，
//     .NET CLR 需要）。找不到指定簽名身分時立即失敗並列出可用身分，不會靜默退回 ad-hoc。
//   · --notarize / --notary-profile <keychain-profile>：以 notarytool 公證並 stapler 訂票。
//     要求 Developer ID 簽章與預先儲存的 keychain profile；不在命令列傳遞或記錄密碼。
//   · artifacts/dawn/<rid>/webgpu_dawn.dylib 存在時取代 NuGet 內附的 Dawn（見 build/build-dawn.sh：
//     以 WebGPUSharp 0.5.7 同源 Dawn commit 4178cb7 用較低 MACOSX_DEPLOYMENT_TARGET 重建），
//     並以取代後的最低系統版本寫入 LSMinimumSystemVersion。直接改 Mach-O minos 是禁止的。
// CLI 維持獨立的 self-contained 資料夾，避免覆寫 GUI 的框架元件。

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;

var rid = Arg("--rid") ?? (OperatingSystem.IsMacOS() ? "osx-arm64" : "win-x64");
var smoke = args.Contains("--smoke");
var zip = args.Contains("--zip");
var signIdentity = Arg("--sign") ?? Env("FILMBATCH_SIGN_IDENTITY");
var notaryProfile = Arg("--notary-profile") ?? Env("FILMBATCH_NOTARY_PROFILE");
var notarize = args.Contains("--notarize") || notaryProfile is not null;
var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", ".."));
var dist = Path.Combine(root, "dist", rid);
var isMac = rid is "osx-arm64";
var bundle = Path.Combine(dist, "FilmBatch.app");
var gui = isMac ? Path.Combine(bundle, "Contents", "MacOS") : dist;
var cli = Path.Combine(dist, "cli");

if (rid is not ("win-x64" or "osx-arm64"))
{
    Console.Error.WriteLine($"不支援的 RID：{rid}（支援 win-x64 / osx-arm64）");
    return 1;
}
if (isMac && !OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("macOS 封裝（Info.plist、簽章、公證）必須在 macOS 上執行。");
    return 1;
}
if (!isMac && (signIdentity is not null || notarize))
{
    Console.Error.WriteLine("--sign / --notarize 只適用於 macOS 封裝。");
    return 1;
}
if (notarize && signIdentity is null)
{
    Console.Error.WriteLine("公證需要 Developer ID 簽章：請提供 --sign 或 FILMBATCH_SIGN_IDENTITY。");
    return 1;
}
if (smoke && !isMac && !OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("冒煙測試需在本機平台執行（win-x64 需在 Windows、osx-* 需在 macOS）。");
    return 1;
}

if (isMac && signIdentity is not null)
    RequireIdentity(signIdentity);
if (isMac && notarize)
    ResolveNotaryCredentials(); // 缺憑證時在此明確失敗，不等到打包完成

Step("清除輸出", () => { if (Directory.Exists(dist)) Directory.Delete(dist, recursive: true); });

// GUI 未啟用 Trimming（相依的 Avalonia/Dock 以反射為主，見 csproj），CLI 也維持 untrimmed；
// 兩個各自 self-contained 的發佈疊在同一資料夾會互相覆寫框架組件（M0 冒煙建置發現），因此 CLI 放 cli/ 子資料夾。
Run("dotnet", ["publish", Path.Combine(root, "src", "FilmBatch.App"), "-c", "Release", "-r", rid, "-o", gui]);
Run("dotnet", ["publish", Path.Combine(root, "src", "FilmBatch.Cli"), "-c", "Release", "-r", rid,
    "--self-contained", "-p:PublishReadyToRun=true", "-o", cli]);

Step("授權聲明", () =>
{
    var notice = Path.Combine(root, "THIRD-PARTY-NOTICES.txt");
    File.Copy(notice, Path.Combine(dist, Path.GetFileName(notice)), overwrite: true);
});

// 以 build/build-dawn.sh 重建的較低 minos Dawn 取代 NuGet 內附檔（ABI 已由腳本驗證為同源 commit 的 superset）。
// 不修改 Mach-O 載入命令；只允許整檔替換且 minos 必須不高於內附檔。
if (isMac)
    ReplaceDawnIfPresent();

if (isMac)
{
    var resources = Path.Combine(bundle, "Contents", "Resources");
    Directory.CreateDirectory(resources);
    File.Copy(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"), Path.Combine(resources, "THIRD-PARTY-NOTICES.txt"));
    var version = AssemblyName.GetAssemblyName(Path.Combine(gui, "FilmBatch.dll")).Version!.ToString(3);
    var minos = MacMinos(bundle);
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
            <!-- 最低系統版本 = 封裝內所有 Mach-O 的最高 minos，但不低於 14.0
                 （.NET 10 / Magick.NET 原生需求；NuGet 內附 Dawn 為 26.0，
                 以 build/build-dawn.sh 產生的 artifacts/dawn/<rid>/webgpu_dawn.dylib 可降到更低版本）。 -->
            <key>LSMinimumSystemVersion</key><string>{{minos}}</string>
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

    SignMac();

    if (notarize)
        NotarizeMac();
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

// ───────────────────────── macOS：Dawn 替換、簽章、公證 ─────────────────────────

/// <summary>判斷檔案是否為 Mach-O（thin / fat / dylib）。</summary>
static bool IsMachO(string path)
{
    try
    {
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        if (fs.Read(magic) < 4) return false;
        var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(magic);
        return v is 0xFEEDFACE or 0xFEEDFACF // 32/64-bit little-endian（big-endian 讀法）
            or 0xCEFAEDFE or 0xCFFAEDFE     // big-endian 位元組序
            or 0xCAFEBABE or 0xCAFEBABF;    // fat / fat64
    }
    catch (IOException) { return false; }
}

/// <summary>以 otool 讀出檔案的最低 macOS 版本（minos）；讀不到時回傳 null。</summary>
string? FileMinos(string path)
{
    var output = Capture("otool", ["-l", path]);
    var m = Regex.Match(output, @"\bminos\s+(\d+\.\d+(\.\d+)?)");
    if (m.Success) return m.Groups[1].Value;
    // 舊版 Mach-O 用 LC_VERSION_MIN_MACOSX 的 version 欄位
    m = Regex.Match(output, @"LC_VERSION_MIN_MACOSX[\s\S]*?version\s+(\d+\.\d+(\.\d+)?)");
    return m.Success ? m.Groups[1].Value : null;
}

/// <summary>整個 bundle 內所有 Mach-O 的最高 minos；找不到任何 Mach-O 時失敗（表示封裝不完整）。</summary>
string MacMinos(string dir)
{
    var versions = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .Where(IsMachO)
        .Select(FileMinos)
        .OfType<string>()
        .ToList();
    if (versions.Count == 0)
        throw new Exception("封裝內找不到 Mach-O 檔案，無法決定 LSMinimumSystemVersion。");
    var max = versions.Select(v => Version.Parse(v)).Max()!;
    // .NET 10 與 Magick.NET 原生需求最低 macOS 14.0：即使 Dawn 重建到更低版本，
    // LSMinimumSystemVersion 仍不得低於 14.0（只提高宣告，不改任何 Mach-O）。
    var floor = new Version(14, 0);
    var effective = max < floor ? floor : max;
    Console.WriteLine($"== 最低系統版本 {effective}（{versions.Count} 個 Mach-O，最高 {max}）");
    return effective.Build > 0 ? $"{effective.Major}.{effective.Minor}.{effective.Build}" : $"{effective.Major}.{effective.Minor}";
}

/// <summary>artifacts/dawn/&lt;rid&gt;/webgpu_dawn.dylib 存在時整檔取代發佈輸出（GUI 與 CLI 各一份）。</summary>
void ReplaceDawnIfPresent()
{
    var replacement = Path.Combine(root, "artifacts", "dawn", rid, "webgpu_dawn.dylib");
    if (!File.Exists(replacement)) return;

    foreach (var target in new[] { Path.Combine(gui, "webgpu_dawn.dylib"), Path.Combine(cli, "webgpu_dawn.dylib") })
    {
        if (!File.Exists(target))
            throw new Exception($"找不到要取代的 Dawn：{target}（發佈輸出不如預期）");
        var bundled = FileMinos(target) ?? throw new Exception($"無法讀取內附 Dawn 的 minos：{target}");
        var newer = FileMinos(replacement) ?? throw new Exception($"無法讀取替代 Dawn 的 minos：{replacement}");
        if (Version.Parse(newer) > Version.Parse(bundled))
            throw new Exception($"替代的 Dawn minos {newer} 高於內附的 {bundled}，不應採用。");
        File.Copy(replacement, target, overwrite: true);
        Console.WriteLine($"== 已取代 {target}（minos {bundled} → {newer}）");
    }
}

/// <summary>確認指定的程式碼簽章身分存在；不存在時列出可用身分並失敗。</summary>
void RequireIdentity(string identity)
{
    var output = Capture("security", ["find-identity", "-v", "-p", "codesigning"]);
    var identities = Regex.Matches(output, @"(?m)^\s*\d+\)\s+([A-Fa-f0-9]{40})\s+""(Developer ID Application: [^""]+)""");
    if (!identities.Any(match =>
            match.Groups[1].Value.Equals(identity, StringComparison.OrdinalIgnoreCase) ||
            match.Groups[2].Value.Equals(identity, StringComparison.Ordinal)))
        throw new Exception(
            $"找不到程式碼簽章身分「{identity}」。可用的 codesigning 身分：\n{output}\n" +
            "（需要 Developer ID Application 憑證；不指定 --sign 時預設為本機 ad-hoc 簽章。）");
}

/// <summary>公證使用預先儲存的 keychain profile，密碼不進入參數或建置紀錄。</summary>
string[] ResolveNotaryCredentials()
{
    if (notaryProfile is null)
        throw new Exception(
            "公證需要 --notary-profile 或 FILMBATCH_NOTARY_PROFILE。" +
            "請先使用 xcrun notarytool store-credentials 互動式儲存憑證。");
    return ["--keychain-profile", notaryProfile];
}

/// <summary>簽署所有 Mach-O（先內後外），再簽 apphost 與整個 bundle。</summary>
void SignMac()
{
    var release = signIdentity is not null;
    var sign = release ? signIdentity! : "-";
    var entitlements = Path.Combine(root, "build", "FilmBatch.entitlements");
    var appHost = Path.Combine(gui, "FilmBatch");
    var cliHost = Path.Combine(cli, "filmbatch-cli");

    string[] Flags(string file)
    {
        // Developer ID：hardened runtime + 安全時間戳 + entitlements（只有可執行檔需要 entitlements）
        var flags = release
            ? new List<string> { "--force", "--sign", sign, "--options", "runtime", "--timestamp" }
            : new List<string> { "--force", "--sign", sign };
        if (release && (file == appHost || file == cliHost || file == bundle))
            flags.AddRange(["--entitlements", entitlements]);
        return [.. flags];
    }

    // MacOS 目錄中的受控 DLL 也會被 codesign 視為巢狀程式碼；由內而外逐檔簽署，
    // 主執行檔（apphost）最後簽，再簽整個 bundle。非 Mach-O 檔的簽章存於延伸屬性，壓縮時必須用 ditto 保留。
    foreach (var file in Directory.EnumerateFiles(gui, "*", SearchOption.AllDirectories))
        if (file != appHost)
            Run("codesign", [.. Flags(file), file]);

    // 主執行檔最後簽（release 含 hardened runtime + entitlements；ad-hoc 只有本機簽章）。
    Run("codesign", [.. Flags(appHost), appHost]);

    // CLI 的巢狀 Mach-O 也簽署（本機 ad-hoc 照簽才能啟動；release 以同一 Developer ID 簽，主執行檔最後簽）。
    foreach (var file in Directory.EnumerateFiles(cli, "*", SearchOption.AllDirectories))
        if (file != cliHost && IsMachO(file))
            Run("codesign", [.. Flags(file), file]);
    if (File.Exists(cliHost))
        Run("codesign", [.. Flags(cliHost), cliHost]);

    Run("codesign", [.. Flags(bundle), bundle]);
    Run("codesign", ["--verify", "--deep", "--strict", bundle]);

    if (release)
        Console.WriteLine($"== 已以「{signIdentity}」簽署（hardened runtime + entitlements）");
    else
        Console.WriteLine("== ad-hoc 簽章（本機測試用；散佈需 --sign Developer ID）");
}

/// <summary>ditto 打包 → notarytool 公證 → stapler 訂票 → spctl 評估。</summary>
void NotarizeMac()
{
    var credentials = ResolveNotaryCredentials();
    var submissionZip = Path.Combine(dist, "..", $"notarize-{rid}.zip");
    File.Delete(submissionZip);
    // 公證完整交付內容，包含獨立的 CLI 與其原生相依程式庫。
    Run("ditto", ["-c", "-k", "--sequesterRsrc", dist, submissionZip]);
    Run("xcrun", ["notarytool", "submit", submissionZip, "--wait", .. credentials]);
    Run("xcrun", ["stapler", "staple", bundle]);
    Run("xcrun", ["stapler", "validate", bundle]);
    Run("spctl", ["--assess", "--type", "execute", "-vv", bundle]);
    Console.WriteLine("== 公證完成並已訂票");
}

// ───────────────────────── 小工具 ─────────────────────────

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string? Env(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

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

string Capture(string file, string[] arguments)
{
    var start = new ProcessStartInfo(file)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = root,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var p = Process.Start(start)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return output;
}
