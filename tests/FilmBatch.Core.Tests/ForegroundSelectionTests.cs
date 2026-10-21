using FilmBatch.Engine.Imaging;

namespace FilmBatch.Core.Tests;

public sealed class ForegroundSelectionTests
{

    [Fact]
    public void Generate_NullSource_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => ForegroundSelection.Generate(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Generate_CancelledBeforeNativeWork_ThrowsCancellation()
    {
        Assert.SkipUnless(ForegroundSelection.IsSupported, "Requires macOS 14+ Vision.");
        var source = new SourceImage(2, 2, new ushort[2 * 2 * 4]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ForegroundSelection.Generate(source, cancelled.Token));
    }

    [Fact]
    public void Generate_UniformImage_ReturnsNullAndLeavesSourceUntouched()
    {
        Assert.SkipUnless(ForegroundSelection.IsSupported, "Requires macOS 14+ Vision.");
        // 64×64 均勻灰：實測回傳 results.count == 0，屬於明確的無主體（null），不是偽造遮罩。
        var rgba = new ushort[64 * 64 * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 32768; rgba[i + 1] = 32768; rgba[i + 2] = 32768; rgba[i + 3] = 65535;
        }
        var snapshot = (ushort[])rgba.Clone();
        var source = new SourceImage(64, 64, rgba) { ReferenceWidth = 64, ReferenceHeight = 64 };
        Assert.Null(ForegroundSelection.Generate(source, TestContext.Current.CancellationToken));
        Assert.Equal(snapshot, rgba);
    }

}
