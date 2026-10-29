using FilmBatch.Core.Film;
using FilmBatch.Mobile;
using FilmBatch.Raw.Color;

namespace FilmBatch.App.Tests;

public class MobileWorkspaceTests
{
    [Theory]
    [InlineData("oversized.clf", ClfFile.MaxInputBytes)]
    [InlineData("oversized.fblook", ColorTransformFile.MaxFileBytes)]
    [InlineData("oversized.dcp", DcpProfile.MaxSourceBytes)]
    public async Task ProfileImport_StopsNonSeekableSourceAtCap_AndDeletesPartialCopy(string name, long limit)
    {
        var root = Path.Combine(Path.GetTempPath(), "filmbatch-mobile-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stream = new NonSeekableStream(limit + 8 * 1024 * 1024);
            await Assert.ThrowsAsync<InvalidDataException>(() => MobileWorkspace.CopyAssetAsync(stream, name, root, TestContext.Current.CancellationToken));
            Assert.InRange(stream.BytesRead, limit + 1, limit + 65536);
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class NonSeekableStream(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var size = (int)Math.Min(count, length - BytesRead);
            Array.Clear(buffer, offset, size);
            BytesRead += size;
            return size;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..size].Clear();
            BytesRead += size;
            return ValueTask.FromResult(size);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
