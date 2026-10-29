// D2／AC-DCP-02 的儲存行為：合成 little-endian DCP，不使用第三方描述檔。
// key 是原始位元組的 SHA-256；顯示名稱、policy 判斷與機型相容都不參與 key。
using System.Security.Cryptography;
using System.Text;
using FilmBatch.Core;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging.Raw;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 使用者 DCP 封存：明確 root、有界暫存、同一份位元組驗證後才原子發布，不覆寫未驗證目標。
/// </summary>
public sealed class CameraProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FilmBatch-dcpstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void IsUserKey_IsExactlyDcpPrefixPlus64LowerHex()
    {
        var hex = new string('a', 64);
        Assert.True(CameraProfileKey.IsUserKey("dcp-" + hex));
        Assert.True(CameraProfileKey.IsUserKey("dcp-" + string.Concat(Enumerable.Repeat("0123456789abcdef", 4))));
        Assert.False(CameraProfileKey.IsUserKey(null));
        Assert.False(CameraProfileKey.IsUserKey(""));
        Assert.False(CameraProfileKey.IsUserKey("auto"));
        Assert.False(CameraProfileKey.IsUserKey("dcp-"));
        Assert.False(CameraProfileKey.IsUserKey("dcp-" + new string('a', 63)));
        Assert.False(CameraProfileKey.IsUserKey("dcp-" + new string('a', 65)));
        Assert.False(CameraProfileKey.IsUserKey("dcp-" + new string('A', 64)));
        Assert.False(CameraProfileKey.IsUserKey("dcp-" + new string('a', 63) + "A"));
        Assert.False(CameraProfileKey.IsUserKey("dcp-" + new string('a', 63) + "g"));
        Assert.False(CameraProfileKey.IsUserKey("DCP-" + hex));
        Assert.False(CameraProfileKey.IsUserKey("dcp-" + hex + " "));
        Assert.False(CameraProfileKey.IsUserKey(" " + "dcp-" + hex));
        Assert.False(CameraProfileKey.IsUserKey("dcp-" + hex + ".dcp"));
        Assert.False(CameraProfileKey.IsUserKey("../../profile.dcp"));
    }

    [Fact]
    public void StagePublish_PreservesExactBytesKeyPolicyAndCopyright()
    {
        var pinnedRoot = AppPaths.OverrideRoot;
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, _root);
        using var draft = CameraProfileStore.Stage(relative, new MemoryStream(bytes), "Desk", CancellationToken.None);
        Assert.Equal(Path.GetFullPath(_root), draft.Root);
        Assert.Equal(KeyOf(bytes), draft.Key);
        Assert.Equal(2, draft.Profile.ProfileEmbedPolicy);
        Assert.Equal("Copyright Acme 2026", draft.Profile.ProfileCopyright);
        Assert.Equal("Acme Model A", draft.Profile.UniqueCameraModel);
        Assert.False(File.Exists(DcpPath(_root, draft.Key)));

        var key = draft.Publish(CancellationToken.None);
        Assert.Equal(draft.Key, key);
        Assert.Equal(bytes, File.ReadAllBytes(DcpPath(_root, key)));
        Assert.Equal(Encoding.UTF8.GetBytes("Desk"), File.ReadAllBytes(NamePath(_root, key)));

        var resolved = CameraProfileStore.Resolve(_root, key, CancellationToken.None);
        Assert.Equal(2, resolved.ProfileEmbedPolicy);
        Assert.Equal("Copyright Acme 2026", resolved.ProfileCopyright);
        Assert.Equal("Acme Model A", resolved.UniqueCameraModel);
        Assert.Equal(pinnedRoot, AppPaths.OverrideRoot);
    }

    [Fact]
    public void Import_RoundTripsExactBytesKeyAndProfile()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var token = TestContext.Current.CancellationToken;
        var key = CameraProfileStore.Import(_root, new MemoryStream(bytes), "Desk", token);
        Assert.Equal(KeyOf(bytes), key);
        Assert.Equal(bytes, File.ReadAllBytes(DcpPath(_root, key)));
        var resolved = CameraProfileStore.Resolve(_root, key, token);
        Assert.Equal("Acme Model A", resolved.UniqueCameraModel);
        Assert.Equal("Copyright Acme 2026", resolved.ProfileCopyright);
        Assert.Equal(2, resolved.ProfileEmbedPolicy);
    }

    [Fact]
    public void Resolve_CancelledTokenDoesNotReturnSnapshot()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var key = Publish(bytes, "Kept");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            CameraProfileStore.Resolve(_root, key, cts.Token);
        });
        Assert.Equal(bytes, File.ReadAllBytes(DcpPath(_root, key)));
    }

    [Fact]
    public void OversizedExistingTarget_IsNeverReplaced()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var key = KeyOf(bytes);
        Directory.CreateDirectory(_root);
        var path = DcpPath(_root, key);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            file.SetLength((long)DcpProfile.MaxSourceBytes + 1);
        using var draft = CameraProfileStore.Stage(_root, new MemoryStream(bytes), "Name", TestContext.Current.CancellationToken);
        var error = Assert.Throws<CameraProfileException>(() => { draft.Publish(TestContext.Current.CancellationToken); });
        Assert.Equal(CameraProfileFault.UnverifiedTarget, error.Fault);
        Assert.Equal((long)DcpProfile.MaxSourceBytes + 1, new FileInfo(path).Length);
        Assert.False(Assert.Single(CameraProfileStore.List(_root)).Valid);
    }

    [Fact]
    public void DisplayName_IsBoundedAndExcludedFromTheKey()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var longName = "  Alpha\r\nBeta  " + new string('x', 80);
        using (var draft = CameraProfileStore.Stage(_root, new MemoryStream(bytes), longName, CancellationToken.None))
        {
            Assert.Equal("Alpha Beta  " + new string('x', 36), draft.Name);
            Assert.Equal(48, draft.Name.Length);
            Assert.Equal(48, CameraProfileStore.MaxDisplayNameLength);
            Assert.DoesNotContain("\n", draft.Name);
            Assert.DoesNotContain("\r", draft.Name);
            draft.Publish(CancellationToken.None);
        }

        using (var empty = CameraProfileStore.Stage(_root, new MemoryStream(bytes), " \r\n\t ", CancellationToken.None))
        {
            Assert.Equal("DCP", empty.Name);
            Assert.Equal(KeyOf(bytes), empty.Key);
        }

        var renamed = Publish(bytes, "Other name");
        Assert.Equal(KeyOf(bytes), renamed);
        Assert.Equal("Alpha Beta  " + new string('x', 36), File.ReadAllText(NamePath(_root, renamed)));
        Assert.Equal(bytes, File.ReadAllBytes(DcpPath(_root, renamed)));
    }

    [Fact]
    public void DifferentContent_HasADifferentKey()
    {
        var first = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var second = Profile("Acme Model B", "Copyright Acme 2026", 2);
        Assert.Equal(first.Length, second.Length);
        Assert.NotEqual(first, second);
        var left = Publish(first, "A");
        var right = Publish(second, "A");
        Assert.Equal(KeyOf(first), left);
        Assert.Equal(KeyOf(second), right);
        Assert.NotEqual(left, right);
        Assert.Equal(first, File.ReadAllBytes(DcpPath(_root, left)));
        Assert.Equal(second, File.ReadAllBytes(DcpPath(_root, right)));
    }

    [Fact]
    public void RootsStayIsolated()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var other = _root + "-other";
        try
        {
            var here = Publish(bytes, "Here");
            var there = Publish(bytes, "There", other);
            Assert.Equal(here, there);
            Assert.Equal(bytes, File.ReadAllBytes(DcpPath(_root, here)));
            Assert.Equal(bytes, File.ReadAllBytes(DcpPath(other, there)));

            File.Delete(DcpPath(_root, here));
            var missing = Assert.Throws<CameraProfileException>(() =>
            {
                CameraProfileStore.Resolve(_root, here, CancellationToken.None);
            });
            Assert.Equal(CameraProfileFault.Missing, missing.Fault);
            Assert.Equal("Acme Model A", CameraProfileStore.Resolve(other, there, CancellationToken.None).UniqueCameraModel);
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, true);
        }
    }

    [Fact]
    public void DuplicatePublish_DoesNotClobberValidBytesOrName()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var key = Publish(bytes, "Alpha");
        var dcp = DcpPath(_root, key);
        var name = NamePath(_root, key);
        var stamp = new DateTime(2026, 10, 28, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(dcp, stamp);
        File.SetLastWriteTimeUtc(name, stamp);
        var dcpTime = File.GetLastWriteTimeUtc(dcp);
        var nameTime = File.GetLastWriteTimeUtc(name);

        Assert.Equal(key, Publish(bytes, "Beta"));
        Assert.Equal(dcpTime, File.GetLastWriteTimeUtc(dcp));
        Assert.Equal(nameTime, File.GetLastWriteTimeUtc(name));
        Assert.Equal(bytes, File.ReadAllBytes(dcp));
        Assert.Equal("Alpha", File.ReadAllText(name));
    }

    [Fact]
    public void Publish_LeavesAnUnverifiedTargetUntouched()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var key = KeyOf(bytes);
        Directory.CreateDirectory(_root);
        var corrupt = (byte[])bytes.Clone();
        corrupt[0] ^= 0xFF;
        File.WriteAllBytes(DcpPath(_root, key), corrupt);
        File.WriteAllText(NamePath(_root, key), "DO-NOT-REPLACE");

        using var draft = CameraProfileStore.Stage(_root, new MemoryStream(bytes), "New", CancellationToken.None);
        var error = Assert.Throws<CameraProfileException>(() =>
        {
            draft.Publish(CancellationToken.None);
        });
        Assert.Equal(CameraProfileFault.UnverifiedTarget, error.Fault);
        Assert.Equal(corrupt, File.ReadAllBytes(DcpPath(_root, key)));
        Assert.Equal("DO-NOT-REPLACE", File.ReadAllText(NamePath(_root, key)));
        Assert.Equal(new[] { key + ".dcp", key + ".name" }, Files(_root));
    }

    [Fact]
    public void Resolve_RejectsInvalidMissingAndMismatchedBytes()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var key = Publish(bytes, "A");
        var invalid = Assert.Throws<CameraProfileException>(() =>
        {
            CameraProfileStore.Resolve(_root, "auto", CancellationToken.None);
        });
        Assert.Equal(CameraProfileFault.InvalidKey, invalid.Fault);

        var missing = Assert.Throws<CameraProfileException>(() =>
        {
            CameraProfileStore.Resolve(_root, "dcp-" + new string('b', 64), CancellationToken.None);
        });
        Assert.Equal(CameraProfileFault.Missing, missing.Fault);

        var flipped = File.ReadAllBytes(DcpPath(_root, key));
        flipped[^1] ^= 0xFF;
        File.WriteAllBytes(DcpPath(_root, key), flipped);
        var mismatch = Assert.Throws<CameraProfileException>(() =>
        {
            CameraProfileStore.Resolve(_root, key, CancellationToken.None);
        });
        Assert.Equal(CameraProfileFault.HashMismatch, mismatch.Fault);
    }

    [Fact]
    public void Resolve_SameLengthAndTimestampReplacement_DoesNotReuseTheSnapshot()
    {
        var firstBytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var secondBytes = Profile("Acme Model B", "Copyright Acme 2026", 3);
        Assert.Equal(firstBytes.Length, secondBytes.Length);
        var key = Publish(firstBytes, "A");
        var path = DcpPath(_root, key);
        var held = CameraProfileStore.Resolve(_root, key, CancellationToken.None);
        var stamp = File.GetLastWriteTimeUtc(path);

        File.WriteAllBytes(path, secondBytes);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(firstBytes.Length, new FileInfo(path).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

        var mismatch = Assert.Throws<CameraProfileException>(() =>
        {
            CameraProfileStore.Resolve(_root, key, CancellationToken.None);
        });
        Assert.Equal(CameraProfileFault.HashMismatch, mismatch.Fault);
        Assert.Equal("Acme Model A", held.UniqueCameraModel);
        Assert.Equal(2, held.ProfileEmbedPolicy);
        Assert.Equal("Copyright Acme 2026", held.ProfileCopyright);
    }

    [Fact]
    public void Resolve_ParsesHashMatchedBytesInsteadOfTrustingTheName()
    {
        var junk = new byte[] { (byte)'I', (byte)'I', 0x52, 0x43, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var key = KeyOf(junk);
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(DcpPath(_root, key), junk);
        var listed = Assert.Single(CameraProfileStore.List(_root));
        Assert.Equal(key, listed.Key);
        Assert.Equal("DCP", listed.Name);
        Assert.True(listed.Valid);
        Assert.Throws<RawException>(() =>
        {
            CameraProfileStore.Resolve(_root, key, CancellationToken.None);
        });
    }

    [Fact]
    public void CancelledStage_DeletesOnlyItsTemporary()
    {
        var keptBytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var kept = Publish(keptBytes, "Kept");
        var before = File.ReadAllBytes(DcpPath(_root, kept));
        using var cts = new CancellationTokenSource();
        var stream = new CancelAfterStream(Profile("Acme Model B", "Copyright Acme 2026", 2), cts, 8);

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            CameraProfileStore.Stage(_root, stream, "Nope", cts.Token);
        });

        Assert.Equal(before, File.ReadAllBytes(DcpPath(_root, kept)));
        Assert.Equal(new[] { kept + ".dcp", kept + ".name" }, Files(_root));
    }

    [Fact]
    public void CancelledPublish_KeepsThePreviousAsset()
    {
        var keptBytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var kept = Publish(keptBytes, "Kept");
        var before = File.ReadAllBytes(DcpPath(_root, kept));
        var incoming = Profile("Acme Model B", "Copyright Acme 2026", 2);
        using var draft = CameraProfileStore.Stage(_root, new MemoryStream(incoming), "Next", CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            draft.Publish(cts.Token);
        });
        draft.Dispose();

        Assert.False(File.Exists(DcpPath(_root, KeyOf(incoming))));
        Assert.Equal(before, File.ReadAllBytes(DcpPath(_root, kept)));
        Assert.Equal(new[] { kept + ".dcp", kept + ".name" }, Files(_root));
    }

    [Fact]
    public void MalformedInput_PublishesNothingAndKeepsExistingBytes()
    {
        var keptBytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var kept = Publish(keptBytes, "Kept");
        var before = File.ReadAllBytes(DcpPath(_root, kept));

        Assert.Throws<RawException>(() =>
        {
            CameraProfileStore.Stage(_root, new MemoryStream(), "Empty", CancellationToken.None);
        });
        Assert.Throws<RawException>(() =>
        {
            CameraProfileStore.Stage(_root, new MemoryStream([1, 2, 3, 4, 5, 6, 7]), "Short", CancellationToken.None);
        });

        Assert.Equal(before, File.ReadAllBytes(DcpPath(_root, kept)));
        Assert.Equal(new[] { kept + ".dcp", kept + ".name" }, Files(_root));
    }

    [Fact]
    public void SeekableStream_OnePastCap_IsRejected()
    {
        Assert.Equal(64 * 1024 * 1024, DcpProfile.MaxSourceBytes);
        var keptBytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var kept = Publish(keptBytes, "Kept");
        var before = File.ReadAllBytes(DcpPath(_root, kept));
        var stream = new LengthReportedStream(DcpProfile.MaxSourceBytes + 1);

        var error = Assert.Throws<CameraProfileException>(() =>
        {
            CameraProfileStore.Stage(_root, stream, "Huge", CancellationToken.None);
        });

        Assert.Equal(CameraProfileFault.TooLarge, error.Fault);
        Assert.Equal(before, File.ReadAllBytes(DcpPath(_root, kept)));
        Assert.Equal(new[] { kept + ".dcp", kept + ".name" }, Files(_root));
    }

    [Fact]
    public void NonSeekableStream_OverLimit_StopsAndPublishesNothing()
    {
        var keptBytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var kept = Publish(keptBytes, "Kept");
        var before = File.ReadAllBytes(DcpPath(_root, kept));
        var stream = new UnboundedZeroStream();

        var error = Assert.Throws<CameraProfileException>(() =>
        {
            CameraProfileStore.Stage(_root, stream, "Huge", CancellationToken.None);
        });

        Assert.Equal(CameraProfileFault.TooLarge, error.Fault);
        Assert.True(stream.Served <= DcpProfile.MaxSourceBytes + (1 << 20));
        Assert.Equal(before, File.ReadAllBytes(DcpPath(_root, kept)));
        Assert.Equal(new[] { kept + ".dcp", kept + ".name" }, Files(_root));
    }

    [Fact]
    public void NonSeekableSource_PublishesTheBytesThatWereRead()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        using var draft = CameraProfileStore.Stage(_root, new ForwardOnlyStream(bytes), "Forward", CancellationToken.None);
        var key = draft.Publish(CancellationToken.None);
        Assert.Equal(KeyOf(bytes), key);
        Assert.Equal(bytes, File.ReadAllBytes(DcpPath(_root, key)));
        Assert.Equal(2, CameraProfileStore.Resolve(_root, key, CancellationToken.None).ProfileEmbedPolicy);
    }

    [Fact]
    public void SourceMutatedAfterStage_PublishKeepsTheStagedBytes()
    {
        var original = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var wrapped = (byte[])original.Clone();
        var stream = new MemoryStream(wrapped);
        using var draft = CameraProfileStore.Stage(_root, stream, "Stable", CancellationToken.None);
        wrapped[8] ^= 0x5A;
        stream.Position = 0;

        var key = draft.Publish(CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(DcpPath(_root, key)));
        Assert.NotEqual(original, wrapped);
        Assert.Equal("Copyright Acme 2026", draft.Profile.ProfileCopyright);
    }

    [Fact]
    public void List_ReportsBoundedNameAndValidityWithoutDroppingSiblings()
    {
        var good = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var goodKey = Publish(good, "Good Name");
        var brokenKey = "dcp-" + new string('c', 64);
        File.WriteAllBytes(DcpPath(_root, brokenKey), [1, 2, 3, 4]);
        File.WriteAllText(NamePath(_root, brokenKey), new string('字', 10000));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "ignore");
        File.WriteAllBytes(Path.Combine(_root, "DCP-" + new string('d', 64) + ".dcp"), good);
        var hugeKey = "dcp-" + new string('e', 64);
        var hugePath = DcpPath(_root, hugeKey);
        using (var huge = new FileStream(hugePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            huge.SetLength(DcpProfile.MaxSourceBytes + 1);

        var items = CameraProfileStore.List(_root);
        Assert.Equal(3, items.Count);
        var ok = Assert.Single(items, item => item.Key == goodKey);
        Assert.True(ok.Valid);
        Assert.Equal("Good Name", ok.Name);
        var broken = Assert.Single(items, item => item.Key == brokenKey);
        Assert.False(broken.Valid);
        Assert.Equal(new string('字', CameraProfileStore.MaxDisplayNameLength), broken.Name);
        var oversized = Assert.Single(items, item => item.Key == hugeKey);
        Assert.False(oversized.Valid);
        Assert.Equal("DCP", oversized.Name);
        Assert.Empty(CameraProfileStore.List(_root + "-missing"));
    }

    [Fact]
    public void DisposeBeforePublish_RemovesTheTemporaryAndPublishIsIdempotent()
    {
        var bytes = Profile("Acme Model A", "Copyright Acme 2026", 2);
        var staged = CameraProfileStore.Stage(_root, new MemoryStream(bytes), "Once", CancellationToken.None);
        staged.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            staged.Publish(CancellationToken.None);
        });
        AssertNoFiles(_root);

        using var draft = CameraProfileStore.Stage(_root, new MemoryStream(bytes), "Once", CancellationToken.None);
        var key = draft.Publish(CancellationToken.None);
        var dcp = DcpPath(_root, key);
        var stamp = new DateTime(2026, 10, 28, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(dcp, stamp);
        var observed = File.GetLastWriteTimeUtc(dcp);
        Assert.Equal(key, draft.Publish(CancellationToken.None));
        Assert.Equal(observed, File.GetLastWriteTimeUtc(dcp));
        Assert.Equal(bytes, File.ReadAllBytes(dcp));
        draft.Dispose();
        Assert.Equal(bytes, File.ReadAllBytes(dcp));
    }

    private string Publish(byte[] bytes, string name, string? root = null)
    {
        using var draft = CameraProfileStore.Stage(root ?? _root, new MemoryStream(bytes), name, CancellationToken.None);
        return draft.Publish(CancellationToken.None);
    }

    private static string KeyOf(byte[] bytes) => "dcp-" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string DcpPath(string root, string key) => Path.Combine(root, key + ".dcp");

    private static string NamePath(string root, string key) => Path.Combine(root, key + ".name");

    private static string[] Files(string root) =>
        Directory.EnumerateFiles(root).Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static void AssertNoFiles(string root)
    {
        if (Directory.Exists(root)) Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    /// <summary>最小合法 little-endian DCP：模型、已知可逆矩陣、光源 21、policy 與 copyright。</summary>
    private static byte[] Profile(string model, string copyright, uint policy)
    {
        var modelBytes = Encoding.ASCII.GetBytes(model + "\0");
        var copyBytes = Encoding.ASCII.GetBytes(copyright + "\0");
        var matrix = MatrixBytes();
        const int entries = 5;
        var data = 8 + 2 + entries * 12 + 4;
        var modelAt = data;
        var matrixAt = modelAt + modelBytes.Length;
        var copyAt = matrixAt + matrix.Length;
        var output = new List<byte>();
        output.Add((byte)'I');
        output.Add((byte)'I');
        PutU16(output, 0x4352);
        PutU32(output, 8);
        PutU16(output, entries);
        Entry(output, 50708, 2, (uint)modelBytes.Length, (uint)modelAt);   // UniqueCameraModel
        Entry(output, 50721, 10, 9, (uint)matrixAt);                      // ColorMatrix1 (SRATIONAL)
        Entry(output, 50778, 3, 1, 21);                                   // CalibrationIlluminant1 = D65
        Entry(output, 50941, 4, 1, policy);                               // ProfileEmbedPolicy
        Entry(output, 50942, 2, (uint)copyBytes.Length, (uint)copyAt);    // ProfileCopyright
        PutU32(output, 0);
        output.AddRange(modelBytes);
        output.AddRange(matrix);
        output.AddRange(copyBytes);
        return output.ToArray();
    }

    private static readonly (int Num, int Den)[] Matrix =
    [
        (1, 2), (1, 4), (-1, 8), (1, 10), (1, 1), (1, 20), (1, 50), (-1, 10), (3, 4),
    ];

    private static byte[] MatrixBytes()
    {
        var output = new List<byte>(72);
        foreach (var (num, den) in Matrix)
        {
            PutI32(output, num);
            PutI32(output, den);
        }
        return output.ToArray();
    }

    private static void Entry(List<byte> output, ushort tag, ushort type, uint count, uint valueOrOffset)
    {
        PutU16(output, tag);
        PutU16(output, type);
        PutU32(output, count);
        PutU32(output, valueOrOffset);
    }

    private static void PutU16(List<byte> output, ushort value)
    {
        output.Add((byte)value);
        output.Add((byte)(value >> 8));
    }

    private static void PutU32(List<byte> output, uint value)
    {
        output.Add((byte)value);
        output.Add((byte)(value >> 8));
        output.Add((byte)(value >> 16));
        output.Add((byte)(value >> 24));
    }

    private static void PutI32(List<byte> output, int value) => PutU32(output, unchecked((uint)value));

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly byte[] _data;
        private int _pos;
        public ForwardOnlyStream(byte[] data) => _data = data;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _data.Length - _pos);
            if (n <= 0) return 0;
            Buffer.BlockCopy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancelAfterStream : Stream
    {
        private readonly byte[] _data;
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfter;
        private int _pos;
        public CancelAfterStream(byte[] data, CancellationTokenSource cts, int cancelAfter)
        {
            _data = data;
            _cts = cts;
            _cancelAfter = cancelAfter;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _cancelAfter)
            {
                _cts.Cancel();
                throw new OperationCanceledException(_cts.Token);
            }
            var n = Math.Min(count, Math.Min(8, _data.Length - _pos));
            Buffer.BlockCopy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LengthReportedStream : Stream
    {
        public LengthReportedStream(long length) => Length = length;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var room = Length - Position;
            if (room <= 0) return 0;
            var n = (int)Math.Min(count, Math.Min(room, 65536));
            buffer.AsSpan(offset, n).Clear();
            Position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnboundedZeroStream : Stream
    {
        public long Served;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, 65536);
            buffer.AsSpan(offset, n).Clear();
            Served += n;
            if (Served > DcpProfile.MaxSourceBytes + (1 << 20))
                throw new InvalidOperationException("read past the 64 MiB cap window");
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
