// Run after building Host: dotnet run tools/apple-mobile/VerifyBoundaries.cs
// Consumer-visible regressions only: immutable input paths, reruns, and single-pixel dimensions.
using System.Diagnostics;
using System.Security.Cryptography;

var source = AppContext.GetData("EntryPointFileDirectoryPath") as string
    ?? throw new InvalidOperationException("Run this file with the .NET 10 file-based app runner.");
var host = Path.Combine(source, "Host", "bin", "Release", "net10.0", "filmbatch-mobile-probe.dll");
if (!File.Exists(host)) throw new FileNotFoundException("Build tools/apple-mobile/Host -c Release first.", host);
var root = Directory.CreateTempSubdirectory("filmbatch-mobile-boundaries-").FullName;
var passed = 0;
try
{
    var output = Path.Combine(root, "output");
    Directory.CreateDirectory(Path.Combine(output, "core-raw"));
    // Only this owned temporary copy may be damaged if the regression comes back.
    var protectedFile = Path.Combine(output, "core-raw", "generated-linear.dng");
    byte[] original = [0x46, 0x69, 0x6c, 0x6d, 0x42, 0x61, 0x74, 0x63, 0x68];
    File.WriteAllBytes(protectedFile, original);
    await RejectInput("direct output/input overlap", output, protectedFile);

    if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
    {
        var inputAlias = Path.Combine(root, "input-alias.dng");
        File.CreateSymbolicLink(inputAlias, protectedFile);
        await RejectInput("input symlink into output", output, inputAlias);
        var outputAlias = Path.Combine(root, "output-alias");
        Directory.CreateSymbolicLink(outputAlias, output);
        await RejectInput("output directory symlink", outputAlias, protectedFile);
    }
    else Console.WriteLine("SKIP symlink cases: require a Unix host; no Windows symlink-permission claim.");

    var repeatedOutput = Path.Combine(root, "repeat");
    await Succeed("first run", repeatedOutput, 2, 2);
    await Succeed("same output directory again", repeatedOutput, 2, 2);
    foreach (var (width, height) in new[] { (1, 1), (1, 7), (7, 1) })
        await Succeed($"single-pixel dimension {width}x{height}", Path.Combine(root, $"tiny-{width}x{height}"), width, height);
    Console.WriteLine($"PASS {passed} consumer boundary cases.");
    return 0;

    async Task RejectInput(string name, string outputPath, string input)
    {
        var before = SHA256.HashData(File.ReadAllBytes(protectedFile));
        var result = await Run("--out", outputPath, "--raw", input, "--no-gpu");
        if (!before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(protectedFile))))
            throw new InvalidDataException(name + ": source bytes were overwritten.");
        if (result.ExitCode != 2 || !result.Error.Contains("separate", StringComparison.Ordinal))
            throw new InvalidDataException($"{name}: expected a pre-write path rejection (2), got {result.ExitCode}: {result.Error}");
        Console.WriteLine("PASS " + name);
        passed++;
    }

    async Task Succeed(string name, string outputPath, int width, int height)
    {
        var result = await Run("--out", outputPath, "--width", width.ToString(), "--height", height.ToString(), "--no-gpu");
        if (result.ExitCode != 0) throw new InvalidDataException($"{name}: exit {result.ExitCode}: {result.Output}\n{result.Error}");
        Console.WriteLine("PASS " + name);
        passed++;
    }

    async Task<(int ExitCode, string Output, string Error)> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(host);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start diagnostic host.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}
finally
{
    // The directory was created by this invocation; never delete caller-selected output directories.
    Directory.Delete(root, recursive: true);
}
