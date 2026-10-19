// Apple official Simulator automation for the iOS diagnostic apps.
// Usage: dotnet run --project tools/apple-mobile/Simulator -c Release -- <command> [options]
//    or: dotnet tools/apple-mobile/Simulator/bin/Release/net10.0/filmbatch-mobile-simulator.dll <command> [options]
//   preflight [--out dir]
//   devices
//   run --out dir [--target native|prototype|all] [--udid X | --name N [--device-type T --runtime R]]
//       [--keep-simulator] [--install-only] [--timeout S] [--app path.app]
//       [--width N --height N --raw path --model path --provider CPU|CoreML --no-gpu]
//   uitest --out dir [--udid X | --name N ...] [--class FilmBatchProbeUiTests/ProbeUiTests]
//   validate-report <report.json> <tag>
//   verify                                               in-process validate-report boundary checks
// Exit: 0 all-probes-pass run; 1 report has fail; 2 report has blocked/skipped only;
// 3 harness failure (missing tools, build, install, launch, timeout). User simulators are never
// deleted; only simulators created by this invocation are removed unless --keep-simulator.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
    ?? (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath : null)
    ?? "dotnet";

const string NativeBundleId = "org.filmbatch.mobile.probe";
const string PrototypeBundleId = "org.filmbatch.mobile.prototype";

// ---------- argument parsing ----------
var queue = new Queue<string>(args);
if (queue.Count == 0) return Usage();
var command = queue.Dequeue();
if (command is "-h" or "--help" or "help") return Usage();

var options = new Dictionary<string, string?>(StringComparer.Ordinal);
var positional = new List<string>();
while (queue.Count > 0)
{
    var arg = queue.Dequeue();
    if (!arg.StartsWith("--", StringComparison.Ordinal)) { positional.Add(arg); continue; }
    var name = arg[2..];
    options[name] = name is "keep-simulator" or "install-only" or "no-gpu" ? "true"
        : queue.Count > 0 ? queue.Dequeue() : null;
}
string? Opt(string name) => options.TryGetValue(name, out var v) ? v : null;
bool Flag(string name) => Opt(name) is "true";
string Require(string name)
    => Opt(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"Missing required --{name}.");

// The runner must locate the repo-owned app and UI test projects. --source overrides;
// otherwise walk up from the executing assembly until this directory's csproj is found.
// Resolved lazily: validate-report/verify do not touch the repository.
string? sourceCache = null;
string Source()
{
    sourceCache ??= Opt("source") ?? FindSource();
    return sourceCache;
}
string FindSource()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "FilmBatch.Mobile.Simulator.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Cannot locate the Simulator source directory; pass --source.");
}
int timeoutSeconds = int.TryParse(Opt("timeout"), out var t) && t > 0 ? t : 300;
var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
var receipt = new JsonObject
{
    ["invocation"] = string.Join(' ', args),
    ["host"] = Environment.MachineName,
};

// ---------- commands ----------
try
{
switch (command)
{
    case "validate-report":
    {
        if (positional.Count != 2) return Usage();
        return ValidateReport(positional[0], positional[1]);
    }
    case "verify":
    {
        // Managed boundary checks for validate-report on real report shapes; no simctl/Xcode.
        var root = Directory.CreateTempSubdirectory("filmbatch-runner-verify-").FullName;
        var verified = 0;
        try
        {
            JsonNode Fixture(params (string Name, string Status)[] results) => new JsonObject
            {
                ["Runtime"] = ".NET 10.0.13",
                ["Platform"] = "iOS 26.0 arm64 iPhoneSimulator",
                ["OperatingSystem"] = "iOS",
                ["Architecture"] = "Arm64",
                ["Results"] = new JsonArray(results.Select(r =>
                    (JsonNode)new JsonObject { ["Name"] = r.Name, ["Status"] = r.Status, ["Detail"] = "", ["Metrics"] = new JsonObject() }).ToArray()),
            };
            void Check(string name, int expected, JsonNode? report, string? tag = "sim-verify")
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "report.json");
                if (report is not null) File.WriteAllText(path, report.ToJsonString());
                if (tag is not null) File.WriteAllText(Path.Combine(directory, "run-tag.txt"), tag);
                var code = ValidateReport(path, "sim-verify");
                if (code != expected)
                    throw new InvalidDataException($"{name}: expected exit {expected}, got {code}.");
                Console.WriteLine("PASS " + name);
                verified++;
            }
            var passAll = Fixture(("core-raw", "pass"), ("apple-imageio", "pass"), ("onnx", "pass"), ("gpu", "pass"));
            Check("all-pass", 0, passAll);
            Check("probe-fail", 1, Fixture(("core-raw", "pass"), ("apple-imageio", "fail"), ("onnx", "pass"), ("gpu", "pass")));
            Check("gpu-blocked", 2, Fixture(("core-raw", "pass"), ("apple-imageio", "pass"), ("onnx", "pass"), ("gpu", "blocked")));
            Check("gpu-skipped", 2, Fixture(("core-raw", "pass"), ("apple-imageio", "pass"), ("onnx", "pass"), ("gpu", "skipped")));
            Check("fail-beats-blocked", 1, Fixture(("core-raw", "fail"), ("apple-imageio", "pass"), ("onnx", "pass"), ("gpu", "blocked")));
            Check("missing-probe", 3, Fixture(("core-raw", "pass"), ("apple-imageio", "pass"), ("onnx", "pass")));
            Check("cancelled-report-discarded", 3, Fixture(("ios-host", "blocked")));
            var macReport = (JsonObject)JsonNode.Parse(Fixture(("core-raw", "pass"), ("apple-imageio", "pass"), ("onnx", "pass"), ("gpu", "pass")).ToJsonString())!;
            macReport["Platform"] = "Darwin 25.6.0 macOS";
            Check("mac-provenance-rejected", 3, macReport);
            var macRuntime = (JsonObject)JsonNode.Parse(Fixture(("core-raw", "pass"), ("apple-imageio", "pass"), ("onnx", "pass"), ("gpu", "pass")).ToJsonString())!;
            macRuntime["Runtime"] = ".NET 10.0.13 macOS";
            Check("mac-runtime-rejected", 3, macRuntime);
            Check("missing-file", 3, null);
            var wrongOs = passAll.DeepClone();
            wrongOs["OperatingSystem"] = "macOS";
            Check("explicit-mac-os-rejected", 3, wrongOs);
            var darwinIos = passAll.DeepClone();
            darwinIos["Platform"] = "Darwin 25.0.0";
            Check("ios-darwin-kernel-accepted", 0, darwinIos);
            var unknownOs = passAll.DeepClone();
            ((JsonObject)unknownOs).Remove("OperatingSystem");
            Check("missing-os-identity-rejected", 3, unknownOs);
            Check("stale-invocation-rejected", 3, passAll, "sim-old");
            Check("missing-invocation-rejected", 3, passAll, tag: null);
            void StateCheck(string name, bool pass)
            {
                if (!pass) throw new InvalidDataException(name);
                Console.WriteLine("PASS " + name);
                verified++;
            }
            var completed = new FilmBatch.Mobile.iOS.RunEpoch(1);
            StateCheck("completion-accepted-once", completed.TryComplete() && !completed.TryComplete());
            completed.Dispose();
            var cancelled = new FilmBatch.Mobile.iOS.RunEpoch(2);
            var token = cancelled.Token;
            cancelled.Cancel();
            StateCheck("cancelled-result-discarded", token.IsCancellationRequested && !cancelled.TryComplete());
            cancelled.Dispose();
            var fresh = new FilmBatch.Mobile.iOS.RunEpoch(3);
            StateCheck("fresh-run-after-cancel", !fresh.Token.IsCancellationRequested && fresh.TryComplete());
            fresh.Dispose();
            StateCheck("disposed-result-discarded", !fresh.TryComplete());
            var raced = new FilmBatch.Mobile.iOS.RunEpoch(4);
            var accepted = 0;
            Parallel.For(0, 16, _ => { if (raced.TryComplete()) Interlocked.Increment(ref accepted); });
            StateCheck("concurrent-completion-has-one-winner", accepted == 1);
            raced.Dispose();
            Console.WriteLine($"PASS {verified} runner boundary cases.");
            return 0;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    case "preflight":
    {
        CheckTools();
        var runtimes = IosRuntimes();
        Console.WriteLine("iOS Simulator runtimes: " + string.Join(", ", runtimes));
        Console.WriteLine("iPhoneSimulator SDK: " + Capture("xcrun", "--sdk", "iphonesimulator", "--show-sdk-version").Trim());
        if (Opt("out") is { } preflightOut)
            WriteReceipt(Path.Combine(preflightOut, "preflight-receipt.json"), receipt, "preflight-ok");
        return 0;
    }
    case "devices":
    {
        CheckTools();
        Console.WriteLine(Capture("xcrun", "simctl", "list", "devices", "-j"));
        return 0;
    }
    case "uitest":
    {
        CheckTools();
        var (udid, created) = ResolveUdid();
        var xcresult = Path.Combine(Require("out"), "uitests.xcresult");
        var testClass = Opt("class") ?? "FilmBatchProbeUiTests/ProbeUiTests";
        var proj = Path.Combine(Source(), "..", "iOS", "UiTests", "FilmBatchProbeUiTests.xcodeproj");
        var expectedBundle = testClass.Contains("Prototype", StringComparison.Ordinal) ? PrototypeBundleId : NativeBundleId;
        try
        {
            EnsureBooted(udid);
            Exec("xcrun", "simctl", "get_app_container", udid, expectedBundle, "app");
            Directory.CreateDirectory(Path.GetDirectoryName(xcresult)!);
            Exec("xcodebuild", "test", "-project", proj, "-scheme", "FilmBatchProbeUiTests",
                "-destination", $"platform=iOS Simulator,id={udid}", "-only-testing:" + testClass,
                "-resultBundlePath", xcresult);
        }
        finally
        {
            CleanupSimulator(udid, created);
        }
        WriteReceipt(Path.Combine(Require("out"), "uitest-receipt.json"), receipt, "uitest-finished", udid);
        return 0;
    }
    case "run":
    {
        CheckTools();
        Directory.CreateDirectory(Require("out"));
        var targets = (Opt("target") ?? "all") switch
        {
            "native" => new[] { ("native", Path.Combine(Source(), "..", "iOS", "FilmBatch.Mobile.iOS.csproj"), NativeBundleId) },
            "prototype" => new[] { ("prototype", Path.Combine(Source(), "..", "Prototype", "iOS", "FilmBatch.Mobile.Prototype.iOS.csproj"), PrototypeBundleId) },
            "all" => new[]
            {
                ("native", Path.Combine(Source(), "..", "iOS", "FilmBatch.Mobile.iOS.csproj"), NativeBundleId),
                ("prototype", Path.Combine(Source(), "..", "Prototype", "iOS", "FilmBatch.Mobile.Prototype.iOS.csproj"), PrototypeBundleId),
            },
            var other => throw new InvalidOperationException($"Unknown --target {other}; expected native, prototype or all."),
        };
        if (Opt("app") is not null && targets.Length != 1)
            throw new InvalidOperationException("--app applies to a single target; pass --target native or --target prototype.");
        var worst = 0;
        foreach (var target in targets)
        {
            try
            {
                var code = RunTarget(target);
                worst = Math.Max(worst, code);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException
                or FileNotFoundException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[{target.Item1}] {error.Message}");
                WriteReceipt(Path.Combine(Require("out"), $"{target.Item1}-receipt.json"), receipt,
                    "harness-failure: " + error.Message);
                worst = 3;
            }
        }
        return worst;
    }
    default:
        Console.Error.WriteLine($"Unknown command: {command}");
        return Usage();
}
}
catch (Exception error) when (error is InvalidOperationException or IOException
    or FileNotFoundException or InvalidDataException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(error.Message);
    if (Opt("out") is { } failOut)
        WriteReceipt(Path.Combine(failOut, "failure-receipt.json"), receipt, "harness-failure: " + error.Message);
    return 3;
}

// ---------- target run ----------
int RunTarget((string Name, string Project, string BundleId) target)
{
    var targetOut = Path.Combine(Require("out"), target.Name);
    Directory.CreateDirectory(targetOut);
    var (udid, created) = ResolveUdid();
    try
    {
        var rid = "iossimulator-arm64";
        receipt["buildRid"] = rid;
        receipt["udid"] = udid;
        receipt["bundleId"] = target.BundleId;

        var app = Opt("app") ?? BuildApp(target.Project, rid);
        receipt["app"] = app;
        EnsureBooted(udid);
        // terminate may find nothing to stop; its failure is not evidence.
        Run("xcrun", ["simctl", "terminate", udid, target.BundleId]);
        Exec("xcrun", "simctl", "install", udid, app);

        if (Flag("install-only"))
        {
            Exec("xcrun", "simctl", "launch", udid, target.BundleId);
            WriteReceipt(Path.Combine(Require("out"), $"{target.Name}-receipt.json"), receipt, "installed-launched", udid);
            return 0;
        }

        var tag = $"sim-{target.Name}-{Guid.NewGuid():N}";
        var container = Capture("xcrun", "simctl", "get_app_container", udid, target.BundleId, "data").Trim();
        var docs = Path.Combine(container, "Documents");
        var results = Path.Combine(docs, "results");
        var reportSrc = Path.Combine(results, "report.json");

        if (target.Name == "prototype")
        {
            // The Avalonia host exposes real Run/Cancel controls only; drive them through the
            // official UI test runner, then require a fresh report written after this install.
            // probe-options.json is the prototype's option channel (mirrors the native contract).
            Directory.CreateDirectory(docs);
            File.WriteAllText(Path.Combine(docs, "probe-options.json"),
                new JsonObject
                {
                    ["OutputDirectory"] = "results",
                    ["RunTag"] = tag,
                    ["Width"] = int.TryParse(Opt("width"), out var w) ? w : 768,
                    ["Height"] = int.TryParse(Opt("height"), out var h) ? h : 512,
                    ["RawPath"] = Opt("raw"),
                    ["ModelPath"] = Opt("model"),
                    ["Provider"] = Opt("provider") ?? "CPU",
                    ["IncludeGpu"] = !Flag("no-gpu"),
                }.ToJsonString());
            var proj = Path.Combine(Source(), "..", "iOS", "UiTests", "FilmBatchProbeUiTests.xcodeproj");
            var xcresult = Path.Combine(targetOut, "prototype-uitests.xcresult");
            Exec("xcodebuild", "test", "-project", proj, "-scheme", "FilmBatchProbeUiTests",
                "-destination", $"platform=iOS Simulator,id={udid}",
                "-only-testing:FilmBatchProbeUiTests/PrototypeUiTests/testRunCompletesWithReportAndPreview",
                "-resultBundlePath", xcresult);
            var tagPath = Path.Combine(results, "run-tag.txt");
            if (!File.Exists(reportSrc) || !File.Exists(tagPath) || File.ReadAllText(tagPath).Trim() != tag)
                throw new InvalidOperationException("Prototype report does not belong to this invocation; stale result rejected.");
        }
        else
        {
            Directory.CreateDirectory(docs);
            File.WriteAllText(Path.Combine(docs, "pending-run.json"),
                new JsonObject
                {
                    ["RunTag"] = tag,
                    ["AutoRun"] = true,
                    ["Width"] = int.TryParse(Opt("width"), out var w) ? w : 768,
                    ["Height"] = int.TryParse(Opt("height"), out var h) ? h : 512,
                    ["RawPath"] = Opt("raw"),
                    ["ModelPath"] = Opt("model"),
                    ["Provider"] = Opt("provider") ?? "CPU",
                    ["IncludeGpu"] = !Flag("no-gpu"),
                }.ToJsonString());

            Exec("xcrun", "simctl", "launch", udid, target.BundleId);
            var tagSrc = Path.Combine(results, "run-tag.txt");
            if (!WaitFor(() => File.Exists(tagSrc) && File.Exists(reportSrc)
                && File.ReadAllText(tagSrc).Trim() == tag, deadline))
            {
                Run("xcrun", ["simctl", "terminate", udid, target.BundleId]);
                throw new InvalidOperationException(
                    $"Timed out waiting for fresh report (tag {tag}) after {timeoutSeconds}s; cancelled or crashed runs are not accepted.");
            }
        }

        foreach (var artifact in Directory.EnumerateFiles(results))
            File.Copy(artifact, Path.Combine(targetOut, Path.GetFileName(artifact)), overwrite: true);
        var code = ValidateReport(Path.Combine(targetOut, "report.json"), tag);
        // The Duo has multiple integrated screens; simctl's default can capture an inactive one.
        Exec("xcrun", "simctl", "io", udid, "screenshot", "--display=primary", Path.Combine(targetOut, "screenshot.png"));
        Run("xcrun", ["simctl", "terminate", udid, target.BundleId]);
        WriteReceipt(Path.Combine(Require("out"), $"{target.Name}-receipt.json"), receipt,
            code switch { 0 => "all-probes-pass", 1 => "probe-failures", 2 => "blocked-or-skipped", _ => "unknown" }, udid);
        return code;
    }
    finally
    {
        CleanupSimulator(udid, created);
    }
}

// ---------- report validation (pure; no simctl) ----------
int ValidateReport(string reportPath, string expectedTag)
{
    if (!File.Exists(reportPath))
    {
        Console.Error.WriteLine($"Report missing: {reportPath}");
        return 3;
    }
    var tagPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "run-tag.txt");
    if (!File.Exists(tagPath) || File.ReadAllText(tagPath).Trim() != expectedTag)
    {
        Console.Error.WriteLine("Report invocation tag is missing or stale.");
        return 3;
    }
    JsonNode report;
    try { report = JsonNode.Parse(File.ReadAllText(reportPath)) ?? throw new InvalidDataException("empty report"); }
    catch (Exception error) when (error is JsonException or IOException)
    {
        Console.Error.WriteLine($"Report unreadable: {error.Message}");
        return 3;
    }
    var results = (report["Results"] ?? report["results"])?.AsArray();
    if (results is null)
    {
        Console.Error.WriteLine("Report has no Results array.");
        return 3;
    }
    string? Field(JsonNode? node, string name) => node?[name]?.GetValue<string>();
    var names = results.Select(r => Field(r, "Name") ?? Field(r, "name")).ToList();
    foreach (var required in new[] { "core-raw", "apple-imageio", "onnx", "gpu" })
        if (!names.Contains(required))
        {
            Console.Error.WriteLine($"Report missing required probe result: {required}");
            return 3;
        }
    var statuses = results.Select(r => Field(r, "Status") ?? Field(r, "status")).ToList();
    var platform = Field(report, "Platform") ?? Field(report, "platform") ?? "";
    var runtime = Field(report, "Runtime") ?? Field(report, "runtime") ?? "";
    receipt["reportPlatform"] = platform;
    receipt["reportRuntime"] = runtime;
    receipt["reportArchitecture"] = Field(report, "Architecture") ?? Field(report, "architecture");
    receipt["runTag"] = expectedTag;
    // Darwin is a shared kernel name, not an iOS/macOS discriminator.
    // The app records its OS using OperatingSystem.IsIOS(); simctl/build RID
    // in the separate receipt establishes that this run was a Simulator launch.
    if (Field(report, "OperatingSystem") != "iOS"
        || platform.Contains("macOS", StringComparison.OrdinalIgnoreCase)
        || runtime.Contains("macOS", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Report provenance is not iOS (platform '{platform}', runtime '{runtime}'); rejecting.");
        return 3;
    }
    if (statuses.Any(status => status is not ("pass" or "fail" or "blocked" or "skipped"))) return 3;
    if (statuses.Any(s => s == "fail")) return 1;
    // blocked/skipped (expected native gaps such as missing iOS Dawn) stay visible, not pass.
    if (statuses.Any(s => s is "blocked" or "skipped")) return 2;
    if (statuses.All(s => s == "pass")) return 0;
    return 3;
}

// ---------- simctl helpers ----------
void CheckTools()
{
    foreach (var tool in new[] { "xcrun", "xcodebuild" })
        Exec("which", tool);
    var xcodebuild = Capture("xcrun", "--find", "xcodebuild").Trim();
    var devDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(xcodebuild)!, "..", ".."));
    receipt["developerDir"] = devDir;
    receipt["xcodeVersion"] = Capture("xcodebuild", "-version").Trim();
    receipt["simulatorSdk"] = Capture("xcrun", "--sdk", "iphonesimulator", "--show-sdk-version").Trim();
    if (!File.Exists(xcodebuild) || !Directory.Exists(Path.Combine(devDir, "Platforms", "iPhoneSimulator.platform")))
        throw new InvalidOperationException($"Selected developer directory is not a complete iOS Xcode: {devDir}.");
    _ = IosRuntimes();
}

List<string> IosRuntimes()
{
    var runtimes = SimCtlJson("list", "runtimes", "-j")["runtimes"]!.AsArray()
        .Where(r => r?["identifier"]?.GetValue<string>().Contains("SimRuntime.iOS", StringComparison.Ordinal) == true
            && r["isAvailable"]?.GetValue<bool>() == true)
        .Select(r => r!["identifier"]!.GetValue<string>()).ToList();
    if (runtimes.Count == 0)
        throw new InvalidOperationException("No available iOS Simulator runtime (xcrun simctl list runtimes).");
    return runtimes;
}

(string Udid, bool Created) ResolveUdid()
{
    if (Opt("udid") is { Length: > 0 } given)
    {
        receipt["simulatorSource"] = "caller-provided";
        return (given, false);
    }
    var name = Opt("name") ?? "filmbatch-mobile-probe";
    var found = FindDevice(name);
    if (found is not null)
    {
        receipt["simulatorSource"] = "reused-existing-by-name";
        return (found, false);
    }
    var deviceType = Opt("device-type") ?? LatestDeviceType("iPhone");
    var runtime = Opt("runtime") ?? IosRuntimes()[^1];
    var createdUdid = Capture("xcrun", "simctl", "create", name, deviceType, runtime).Trim();
    receipt["simulatorSource"] = "created-this-invocation";
    receipt["deviceType"] = deviceType;
    receipt["simRuntime"] = runtime;
    return (createdUdid, true);
}


void CleanupSimulator(string udid, bool created)
{
    // Invocation-created simulators are removed unless --keep-simulator; caller-provided
    // or pre-existing devices are only ever shut down by the user, never deleted here.
    if (!created || Flag("keep-simulator")) return;
    Run("xcrun", ["simctl", "shutdown", udid]);
    Run("xcrun", ["simctl", "delete", udid]);
    receipt["simulatorDeleted"] = true;
}

string? FindDevice(string name)
{
    var devices = SimCtlJson("list", "devices", "-j")["devices"]!.AsObject();
    foreach (var (_, list) in devices)
        foreach (var device in list!.AsArray())
            if (device?["name"]?.GetValue<string>() == name && device["isAvailable"]?.GetValue<bool>() == true)
                return device["udid"]!.GetValue<string>();
    return null;
}

string? DeviceState(string udid)
{
    var devices = SimCtlJson("list", "devices", "-j")["devices"]!.AsObject();
    foreach (var (_, list) in devices)
        foreach (var device in list!.AsArray())
            if (device?["udid"]?.GetValue<string>() == udid)
                return device["state"]?.GetValue<string>();
    return null;
}

string LatestDeviceType(string family)
{
    var types = SimCtlJson("list", "devicetypes", "-j")["devicetypes"]!.AsArray()
        .Where(d => d?["name"]?.GetValue<string>().StartsWith(family, StringComparison.Ordinal) == true)
        .Select(d => d!["identifier"]!.GetValue<string>()).ToList();
    return types.Count == 0
        ? throw new InvalidOperationException($"No {family} device type available.")
        : types[^1];
}

void EnsureBooted(string udid)
{
    // 'simctl boot' exits nonzero when already booted; that outcome is fine.
    Run("xcrun", ["simctl", "boot", udid]);
    var booted = DeviceState(udid) == "Booted";
    if (!booted)
    {
        // bootstatus -b blocks until booted; bound it and fall back to state polling.
        try { TryCapture("xcrun", true, "simctl", "bootstatus", udid, "-b"); }
        catch (InvalidOperationException) { }
        booted = DeviceState(udid) == "Booted";
    }
    if (!booted && !WaitFor(() => DeviceState(udid) == "Booted", deadline))
        throw new InvalidOperationException("Simulator did not reach Booted state.");
}

string BuildApp(string project, string rid)
{
    if (!File.Exists(project)) throw new FileNotFoundException("iOS app project missing.", project);
    Exec(dotnet, "build", project, "-c", "Release", "-r", rid);
    var output = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release");
    return Directory.GetDirectories(output, "*.app", SearchOption.AllDirectories)
        .Where(d => d.Contains("iossimulator", StringComparison.Ordinal))
        .OrderByDescending(Directory.GetLastWriteTimeUtc)
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"No .app produced for {rid} under {output}.");
}

void WriteReceipt(string path, JsonObject body, string outcome, string? udid = null)
{
    if (udid is not null) body["udid"] = udid;
    body["outcome"] = outcome;
    body["finishedUtc"] = DateTimeOffset.UtcNow.ToString("O");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, body.ToJsonString());
}

// ---------- process + wait helpers ----------
string Capture(string file, params string[] arguments) => TryCapture(file, false, arguments) ?? "";

string? TryCapture(string file, bool allowFailure, params string[] arguments)
{
    var (code, stdout, stderr) = Run(file, arguments);
    if (code != 0 && !allowFailure)
        throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} exited {code}: {stderr.Trim()}");
    return stdout;
}

void Exec(string file, params string[] arguments)
{
    var (code, stdout, stderr) = Run(file, arguments);
    if (code != 0)
        throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} exited {code}: {stdout.Trim()}\n{stderr.Trim()}");
}

JsonNode SimCtlJson(params string[] arguments)
    => JsonNode.Parse(Capture("xcrun", arguments.Prepend("simctl").ToArray()))
        ?? throw new InvalidDataException("simctl returned no JSON.");

(int Code, string Out, string Err) Run(string file, string[] arguments)
{
    var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in arguments) start.ArgumentList.Add(a);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    var remaining = (int)Math.Max(1000, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds);
    if (!process.WaitForExit(remaining))
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        throw new InvalidOperationException($"{file} timed out at {timeoutSeconds}s.");
    }
    return (process.ExitCode, stdout.Result, stderr.Result);
}

bool WaitFor(Func<bool> condition, DateTimeOffset until)
{
    while (DateTimeOffset.UtcNow < until)
    {
        if (condition()) return true;
        Thread.Sleep(1000);
    }
    return condition();
}

int Usage()
{
    Console.Error.WriteLine("""
        Runner commands:
          preflight [--out dir]                          check xcode-select/xcrun/xcodebuild/dotnet, iOS runtime, SDK
          devices                                        dump xcrun simctl list devices -j
          run --out dir [options]                        build, install, autorun, collect report+screenshot+receipt
          uitest --out dir [--udid X|--name N] [--class FilmBatchProbeUiTests/ProbeUiTests]
          validate-report <report.json> <tag>            deterministic report check (no simctl)
        Run options: --target native|prototype|all, --udid|--name/--device-type/--runtime,
          --keep-simulator, --install-only, --timeout S, --app path.app,
          --width N --height N --raw path --model path --provider CPU|CoreML --no-gpu
        Exit codes: 0 pass; 1 probe fail; 2 blocked/skipped only; 3 harness failure.
        """);
    return 3;
}
