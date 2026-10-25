using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using FilmBatch.Engine.Imaging;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Raw.Opcodes;
using FilmBatch.Raw.Tiff;

namespace FilmBatch.Core.Tests;

public sealed class RawDecodeSharingTests
{
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"filmbatch-shared-{Guid.NewGuid():N}.dng");

        public Fixture()
        {
            const int w = 512, h = 384;
            var samples = new ushort[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++) samples[y * w + x] = (ushort)(256 + (x * 7 + y * 3) % 2400);
            var raw = new RawImage
            {
                Format = RawFormat.Dng, Width = w, Height = h, Cpp = 1,
                Data = new RawData.U16(samples), Bits = 12, Cfa = Cfa.Bayer("RGGB"),
                Black = BlackLevel.Uniform(128), White = [4095],
                ActiveArea = new(0, 0, w, h), Crop = new(4, 6, w - 8, h - 12),
                Color = new ColorData { ColorMatrix = [Mat3.Identity, null], AsShotNeutral = [1, 1, 1] },
            };
            raw.Opcodes.List3.Add(new WarpRectilinear([[.94, .04, 0, 0, 0, 0]], [.47, .53]));
            raw.Opcodes.List3.Add(new FixVignetteRadial([.2, 0, 0, 0, 0], [.5, .5]));
            File.WriteAllBytes(Path, DngWriter.WriteDng(raw, new DngWriteOptions { Compression = new DngCompression.Uncompressed() }));
        }

        public void Dispose() => File.Delete(Path);
    }

    [Fact]
    public async Task ConcurrentLoads_KeepLensOptionsAndEdgePixelsIndependent()
    {
        using var file = new Fixture();
        var requests = (from lens in new[] { false, true }
                        from edge in new[] { 120, 240, int.MaxValue }
                        select (Edge: edge, Options: new RawLoadOptions(EmbeddedLensCorrections: lens, IncludeSemanticMasks: true))).ToArray();
        var expected = requests.Select(r => ImageLoader.Load(file.Path, r.Edge, r.Options)).ToArray();
        using var start = new ManualResetEventSlim();
        var tasks = requests.Select(r => Task.Run(() => { start.Wait(); return ImageLoader.Load(file.Path, r.Edge, r.Options); })).ToArray();
        start.Set();
        var actual = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.True(actual[i].IsSceneLinear);
            Assert.Equal((expected[i].Width, expected[i].Height), (actual[i].Width, actual[i].Height));
            Assert.Equal(expected[i].Scene!.Image.Data, actual[i].Scene!.Image.Data);
            Assert.Equal(expected[i].Scene!.WhiteBalance!.AsShotTemperature, actual[i].Scene!.WhiteBalance!.AsShotTemperature);
        }
        Assert.NotEqual(expected[0].Scene!.Image.Data, expected[3].Scene!.Image.Data);
    }

    [Fact]
    public void FailedDecode_DoesNotPoisonReplacementAtSamePath()
    {
        using var file = new Fixture();
        var bytes = File.ReadAllBytes(file.Path);
        var expected = ImageLoader.Load(file.Path, 120, new RawLoadOptions(IncludeSemanticMasks: true));
        File.WriteAllBytes(file.Path, [0, 1, 2, 3]);
        Assert.ThrowsAny<Exception>(() => ImageLoader.Load(file.Path, 120, new RawLoadOptions(IncludeSemanticMasks: true)));
        File.WriteAllBytes(file.Path, bytes);
        var actual = ImageLoader.Load(file.Path, 120, new RawLoadOptions(IncludeSemanticMasks: true));
        Assert.Equal(expected.Scene!.Image.Data, actual.Scene!.Image.Data);
    }

    [Fact]
    public void OverlappingConsumers_ShareSamplesNotMutableDevelopmentState()
    {
        using var file = new Fixture();
        using var first = new RawSourceLoader.DecodeLease(file.Path);
        using var second = new RawSourceLoader.DecodeLease(file.Path);
        var a = first.CreateRaw();
        var b = second.CreateRaw();
        Assert.Same(a.Data, b.Data);
        a.Color.ColorMatrix[0] = null;
        a.Opcodes.List3.Clear();
        a.Data = new RawData.U16([]);
        first.Dispose();
        Assert.Equal(256, b.Data.Get(0));
        Assert.Equal(Mat3.Identity, b.Color.ColorMatrix[0]);
        Assert.Equal(2, b.Opcodes.List3.Count);
        Assert.Equal(263, second.CreateRaw().Data.Get(1));
    }

    [Fact]
    public void SameLengthAndTimestampReplacement_DoesNotReuseActiveSamples()
    {
        using var file = new Fixture();
        using var oldLease = new RawSourceLoader.DecodeLease(file.Path);
        var oldRaw = oldLease.CreateRaw();
        var timestamp = File.GetLastWriteTimeUtc(file.Path);
        var bytes = File.ReadAllBytes(file.Path);
        var offset = checked((int)TiffFile.Parse(bytes).Ifds[0].Value(Tags.StripOffsets)!.ToU64Vec()[0]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), 768);
        File.WriteAllBytes(file.Path, bytes);
        File.SetLastWriteTimeUtc(file.Path, timestamp);
        using var newLease = new RawSourceLoader.DecodeLease(file.Path);
        Assert.Equal(256, oldRaw.Data.Get(0));
        Assert.Equal(768, newLease.CreateRaw().Data.Get(0));
    }

    [Fact]
    public void FinalLeaseRelease_AllowsDecodedSamplesAndBytesToCollect()
    {
        using var file = new Fixture();
        var (samples, bytes) = ReleasedData(file.Path);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(samples.IsAlive, "completed RAW decode retains sensor samples");
        Assert.False(bytes.IsAlive, "completed RAW decode retains file bytes");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Samples, WeakReference Bytes) ReleasedData(string path)
    {
        using var first = new RawSourceLoader.DecodeLease(path);
        using var second = new RawSourceLoader.DecodeLease(path);
        var samples = new WeakReference(((RawData.U16)first.CreateRaw().Data).Samples);
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(first.Bytes, out var bytes);
        return (samples, new WeakReference(bytes.Array!));
    }
}
