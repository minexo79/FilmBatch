using FilmBatch.Raw.Codecs;

namespace FilmBatch.Raw.Tests.Codecs;

public sealed class PortableJxlTests
{
    [Fact]
    public void BundledDecoderHasPinnedAbi()
    {
        Assert.True(JxlNative.IsAvailable);
        Assert.Equal(11002u, JxlNative.DecoderVersion);
    }

    [Fact]
    public void MissingBundleFailsExplicitlyWithoutSystemFallback()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var error = Assert.Throws<RawException>(() => JxlNative.LoadBundled(missing, "osx-arm64"));
        Assert.Contains("bundled", error.Message);
        Assert.Contains("libbrotlicommon", error.Message);
    }

    [Fact]
    public void InvalidBundledLibraryReportsDeploymentFailure()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())) return;
        var directory = Path.Combine(Path.GetTempPath(), $"filmbatch-jxl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var windows = OperatingSystem.IsWindows();
            File.WriteAllBytes(Path.Combine(directory, windows ? "libwinpthread-1.dll" : "libbrotlicommon.1.dylib"), [0, 1, 2, 3]);
            var error = Assert.Throws<RawException>(() => JxlNative.LoadBundled(directory, windows ? "win-x64" : "osx-arm64"));
            Assert.Contains("bundled runtime could not load", error.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(11001u)]
    [InlineData(12000u)]
    [InlineData(0u)]
    public void IncompatibleAbiFailsExplicitly(uint version)
    {
        var error = Assert.Throws<RawException>(() => JxlNative.RequireVersion(version));
        Assert.Contains("11002", error.Message);
        Assert.Contains(version.ToString(), error.Message);
    }
}
