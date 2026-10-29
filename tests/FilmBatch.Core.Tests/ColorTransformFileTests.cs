using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FilmBatch.Core.Film;

namespace FilmBatch.Core.Tests;

/// <summary>
/// CT-2 §3.3 儲存切片行為測試（T3）：16-byte FBLOOK envelope、內容定址 key、
/// 有界 owned-temp 匯入、原子發布與取消邊界、header-only 清單與不可變 snapshot。
/// 全部 fixtures 為測試自有合成 CLF（不引用第三方描述檔）；root 一律為本類別
/// 建立的獨立暫存目錄，不觸碰 AppPaths.OverrideRoot 或全域狀態。
/// </summary>
public sealed class ColorTransformFileTests : IDisposable
{
    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    // 合成 CLF：非 identity 矩陣＋多位元組 metadata，用來證明 bytes 逐位保存。
    private const string ClfDoc =
        Decl + "\n<ProcessList id=\"fixture-id\" name=\"測試底片\" compCLFversion=\"3.0\">\n"
        + "<Description>多國語 é中字</Description>\n"
        + "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\">"
        + "<Array dim=\"3 3\">2 0 0 0 2 0 0 0 2</Array></Matrix>\n"
        + "</ProcessList>\n";

    // 不同內容的第二份合成 CLF（節點順序不同 → chain 不同）。
    private const string OtherClfDoc =
        Decl + "\n<ProcessList id=\"other-id\" compCLFversion=\"3.0\">"
        + "<Range inBitDepth=\"16f\" outBitDepth=\"16f\">"
        + "<minInValue>0</minInValue><maxInValue>1</maxInValue>"
        + "<minOutValue>0</minOutValue><maxOutValue>1</maxOutValue></Range>"
        + "</ProcessList>\n";

    private static readonly byte[] Bom = Encoding.UTF8.GetPreamble();
    private static readonly ClfImportOptions DefaultOptions =
        new(ColorTransformStage.DisplayEncoded, ColorTransformEncoding.Srgb, ColorTransformEncoding.Srgb);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fb-ctfile-" + Guid.NewGuid().ToString("N"));

    public ColorTransformFileTests()
    {
        Directory.CreateDirectory(_root);
        UserTransformLibrary.Reset();
    }

    public void Dispose()
    {
        UserTransformLibrary.Reset();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---------- 測試自有的 envelope 建構（不經產品 Write，獨立核對線上格式） ----------

    private static byte[] Envelope(
        ClfImportOptions options, byte[] clf,
        int? declaredLength = null, byte reserved = 0,
        byte? stage = null, byte? input = null, byte? output = null,
        byte[]? trailer = null, string? magic = null)
    {
        var len = declaredLength ?? clf.Length;
        var bytes = new byte[16 + clf.Length + (trailer?.Length ?? 0)];
        Encoding.ASCII.GetBytes(magic ?? "FBLOOK1\0").CopyTo(bytes, 0);
        bytes[8] = stage ?? (byte)options.Stage;
        bytes[9] = input ?? (byte)options.Input;
        bytes[10] = output ?? (byte)options.Output;
        bytes[11] = reserved;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)len);
        clf.CopyTo(bytes, 16);
        trailer?.CopyTo(bytes, 16 + clf.Length);
        return bytes;
    }

    private static byte[] ClfBytes(string doc = ClfDoc, bool bom = false) =>
        bom ? [.. Bom, .. Encoding.UTF8.GetBytes(doc)] : Encoding.UTF8.GetBytes(doc);

    private static string ExpectedKey(byte[] envelope) =>
        "look-" + Convert.ToHexString(SHA256.HashData(envelope)).ToLowerInvariant();

    private static MemoryStream Stream(byte[] bytes) => new(bytes, writable: false);

    private string AssetPath(string key) => Path.Combine(_root, key + ".fblook");
    private string NamePath(string key) => Path.Combine(_root, key + ".name");

    private string ImportDefault(byte[] clf, ClfImportOptions? options = null, string name = "fixture") =>
        UserTransformLibrary.Import(_root, Stream(clf), isFblook: false, options ?? DefaultOptions, name, TestContext.Current.CancellationToken);

    // ---------- envelope 線上格式與 round-trip ----------

    [Fact]
    public void Write_ProducesExactEnvelopeBytes()
    {
        var clf = ClfBytes(bom: true); // BOM 必須原樣保存
        var options = new ClfImportOptions(
            ColorTransformStage.WorkingLinear, ColorTransformEncoding.LinearSrgb, ColorTransformEncoding.ProPhotoGamma18);
        var dest = new MemoryStream();
        ColorTransformFile.Write(dest, options, Stream(clf), TestContext.Current.CancellationToken);

        var expected = Envelope(options, clf);
        Assert.Equal(expected, dest.ToArray());
        Assert.Equal(16 + clf.Length, dest.Length);
        _ = dest.GetBuffer(); // Write 不關閉 destination
    }

    [Fact]
    public void Read_RoundTripsOptionsTransformAndKey()
    {
        var clf = ClfBytes();
        var dest = new MemoryStream();
        ColorTransformFile.Write(dest, DefaultOptions, Stream(clf), TestContext.Current.CancellationToken);

        var asset = ColorTransformFile.Read(Stream(dest.ToArray()), TestContext.Current.CancellationToken);
        Assert.Equal(DefaultOptions, asset.Options);
        Assert.Equal(ExpectedKey(dest.ToArray()), asset.Key);
        Assert.StartsWith("look-", asset.Key);
        Assert.Equal(69, asset.Key.Length);

        // chain 與直接解析同一份 CLF bytes 完全一致（不經過任何重序列化）。
        var direct = ClfFile.Read(Stream(clf), TestContext.Current.CancellationToken);
        Assert.Equal(direct.Ops.ToArray(), asset.Transform.Ops.ToArray());
        Assert.Equal(direct.Data.ToArray(), asset.Transform.Data.ToArray());
        Assert.Equal(direct.Id, asset.Transform.Id);
        Assert.Equal(direct.Name, asset.Transform.Name);
    }

    [Fact]
    public void Write_InvalidOptions_RejectedBeforeWrite()
    {
        var clf = ClfBytes();
        foreach (var options in new[]
        {
            new ClfImportOptions((ColorTransformStage)2, ColorTransformEncoding.Srgb, ColorTransformEncoding.Srgb),
            new ClfImportOptions(ColorTransformStage.WorkingLinear, (ColorTransformEncoding)3, ColorTransformEncoding.Srgb),
            new ClfImportOptions(ColorTransformStage.WorkingLinear, ColorTransformEncoding.Srgb, (ColorTransformEncoding)255),
        })
        {
            var dest = new MemoryStream();
            Assert.Throws<InvalidDataException>(() => ColorTransformFile.Write(dest, options, Stream(clf), TestContext.Current.CancellationToken));
            Assert.Equal(0, dest.Length); // 宣告不合法時不寫任何 byte
        }
    }

    [Fact]
    public void Write_InvalidClf_RejectedWithoutBytes()
    {
        var dest = new MemoryStream();
        Assert.Throws<InvalidDataException>(() =>
            ColorTransformFile.Write(dest, DefaultOptions, Stream("not xml"u8.ToArray()), TestContext.Current.CancellationToken));
        Assert.Equal(0, dest.Length);
    }

    [Fact]
    public void Write_NonSeekableSource_ProducesSameBytes()
    {
        var clf = ClfBytes();
        var seekable = new MemoryStream();
        ColorTransformFile.Write(seekable, DefaultOptions, Stream(clf), TestContext.Current.CancellationToken);
        var nonSeekable = new MemoryStream();
        ColorTransformFile.Write(nonSeekable, DefaultOptions, new NonSeekable(Stream(clf)), TestContext.Current.CancellationToken);
        Assert.Equal(seekable.ToArray(), nonSeekable.ToArray());
    }

    // ---------- header 嚴格性（16 bytes／宣告） ----------

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(7)] // 任一 magic byte 不同即拒絕
    public void Read_BadMagic_Rejected(int corruptAt)
    {
        var envelope = Envelope(DefaultOptions, ClfBytes());
        envelope[corruptAt] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(2)] [InlineData(255)]
    public void Read_InvalidStage_Rejected(byte value)
    {
        var envelope = Envelope(DefaultOptions, ClfBytes(), stage: value);
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(3)] [InlineData(255)]
    public void Read_InvalidInputEncoding_Rejected(byte value)
    {
        var envelope = Envelope(DefaultOptions, ClfBytes(), input: value);
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(3)] [InlineData(255)]
    public void Read_InvalidOutputEncoding_Rejected(byte value)
    {
        var envelope = Envelope(DefaultOptions, ClfBytes(), output: value);
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1)] [InlineData(255)]
    public void Read_NonzeroReserved_Rejected(byte reserved)
    {
        var envelope = Envelope(DefaultOptions, ClfBytes(), reserved: reserved);
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_DeclaredLengthLongerThanActual_Rejected()
    {
        var clf = ClfBytes();
        var envelope = Envelope(DefaultOptions, clf, declaredLength: clf.Length + 1);
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_DeclaredLengthShorter_Rejected()
    {
        var clf = ClfBytes();
        var envelope = Envelope(DefaultOptions, clf, declaredLength: clf.Length - 1);
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_DeclaredLengthOverCap_Rejected()
    {
        // header 宣告 >128 MiB：只寫 16 bytes，不需實際超量資料即拒絕。
        var envelope = Envelope(DefaultOptions, ClfBytes(), declaredLength: unchecked((int)(128L * 1024 * 1024 + 1)));
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_TrailingBytes_Rejected()
    {
        var envelope = Envelope(DefaultOptions, ClfBytes(), trailer: [0x00]);
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(15)]
    public void Read_ShortHeader_Rejected(int length)
    {
        var envelope = Envelope(DefaultOptions, ClfBytes())[..length];
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_CorruptPayload_Rejected()
    {
        var clf = ClfBytes();
        clf[clf.Length - 10] = 0xC0; // 破壞 UTF-8
        var envelope = Envelope(DefaultOptions, clf);
        Assert.Throws<InvalidDataException>(() => ColorTransformFile.Read(Stream(envelope), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_NonSeekable_And_LeavesStreamOpen()
    {
        var envelope = Envelope(DefaultOptions, ClfBytes());
        var asset = ColorTransformFile.Read(new NonSeekable(Stream(envelope)), TestContext.Current.CancellationToken);
        Assert.Equal(DefaultOptions, asset.Options);

        var inner = Stream(envelope);
        ColorTransformFile.Read(inner, TestContext.Current.CancellationToken);
        Assert.True(inner.CanRead); // 未關閉
    }

    [Fact]
    public void Read_CancelledBeforeRead_Throws()
    {
        var envelope = Envelope(DefaultOptions, ClfBytes());
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ColorTransformFile.Read(Stream(envelope), new CancellationToken(true)));
    }

    // ---------- 內容定址 key ----------

    [Fact]
    public void Import_SameBytesAndOptions_SameKeyAcrossRoots()
    {
        var clf = ClfBytes();
        var other = Path.Combine(Path.GetTempPath(), "fb-ctfile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keyA = ImportDefault(clf);
            var keyB = UserTransformLibrary.Import(
                other, Stream(clf), isFblook: false, DefaultOptions, "different name", TestContext.Current.CancellationToken);
            Assert.Equal(keyA, keyB);
            Assert.Equal(ExpectedKey(Envelope(DefaultOptions, clf)), keyA);
            Assert.Matches("^look-[0-9a-f]{64}$", keyA);
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, true);
        }
    }

    [Fact]
    public void Import_OptionChanges_ChangeKey()
    {
        var clf = ClfBytes();
        var keys = new[]
        {
            new ClfImportOptions(ColorTransformStage.WorkingLinear, ColorTransformEncoding.Srgb, ColorTransformEncoding.Srgb),
            new ClfImportOptions(ColorTransformStage.DisplayEncoded, ColorTransformEncoding.Srgb, ColorTransformEncoding.Srgb),
            new ClfImportOptions(ColorTransformStage.DisplayEncoded, ColorTransformEncoding.LinearSrgb, ColorTransformEncoding.Srgb),
            new ClfImportOptions(ColorTransformStage.DisplayEncoded, ColorTransformEncoding.Srgb, ColorTransformEncoding.ProPhotoGamma18),
        }.Select(o => UserTransformLibrary.Import(_root, Stream(clf), isFblook: false, o, "n", TestContext.Current.CancellationToken)).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Import_NameDoesNotAffectKey()
    {
        var clf = ClfBytes();
        var key1 = ImportDefault(clf, name: "first");
        var key2 = ImportDefault(clf, name: "renamed");
        Assert.Equal(key1, key2);
        // 第二次匯入更新 bounded name，但檔案 bytes（＝hash 來源）不變。
        Assert.Equal(Envelope(DefaultOptions, clf), File.ReadAllBytes(AssetPath(key1)));
        Assert.Equal("renamed", File.ReadAllText(NamePath(key1)));
    }

    [Fact]
    public void IsLookKey_ValidatesFormat()
    {
        Assert.True(UserTransformLibrary.IsLookKey("look-" + new string('a', 64)));
        Assert.False(UserTransformLibrary.IsLookKey("look-" + new string('a', 63)));
        Assert.False(UserTransformLibrary.IsLookKey("look-" + new string('A', 64)));
        Assert.False(UserTransformLibrary.IsLookKey("look-" + new string('g', 64)));
        Assert.False(UserTransformLibrary.IsLookKey("user-" + new string('a', 16)));
        Assert.False(UserTransformLibrary.IsLookKey(""));
    }

    // ---------- .fblook 重匯入：沿用內含 declarations ----------

    [Fact]
    public void Import_Fblook_PreservesEmbeddedDeclarations()
    {
        var options = new ClfImportOptions(
            ColorTransformStage.WorkingLinear, ColorTransformEncoding.LinearSrgb, ColorTransformEncoding.ProPhotoGamma18);
        var key = UserTransformLibrary.Import(_root, Stream(ClfBytes()), isFblook: false, options, "n", TestContext.Current.CancellationToken);
        var published = File.ReadAllBytes(AssetPath(key));

        var rootB = Path.Combine(Path.GetTempPath(), "fb-ctfile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keyB = UserTransformLibrary.Import(rootB, Stream(published), isFblook: true, options: null, "n", TestContext.Current.CancellationToken);
            Assert.Equal(key, keyB);
            Assert.Equal(published, File.ReadAllBytes(Path.Combine(rootB, keyB + ".fblook")));
            Assert.Equal(options, UserTransformLibrary.Resolve(rootB, keyB, TestContext.Current.CancellationToken).Options);
        }
        finally
        {
            if (Directory.Exists(rootB)) Directory.Delete(rootB, true);
        }
    }

    [Fact]
    public void Import_Fblook_MismatchedOptions_Rejected()
    {
        var key = ImportDefault(ClfBytes());
        var published = File.ReadAllBytes(AssetPath(key));
        var wrong = new ClfImportOptions(
            ColorTransformStage.WorkingLinear, DefaultOptions.Input, DefaultOptions.Output);
        Assert.Throws<InvalidDataException>(() =>
            UserTransformLibrary.Import(_root, Stream(published), isFblook: true, wrong, "n", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Stage_KindMismatch_Rejected()
    {
        var fblook = Envelope(DefaultOptions, ClfBytes());
        Assert.Throws<InvalidDataException>(() =>
            UserTransformLibrary.Stage(_root, Stream(fblook), isFblook: false, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() =>
            UserTransformLibrary.Stage(_root, Stream(ClfBytes()), isFblook: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Publish_StagedTempMutated_Rejected()
    {
        using var staged = UserTransformLibrary.Stage(_root, Stream(ClfBytes()), isFblook: false, TestContext.Current.CancellationToken);
        var temp = Assert.Single(Directory.GetFiles(_root, "*.tmp"));
        // 暫存標示唯讀；外部竄改封存內容後發布必須失敗，而不是發布分歧的 bytes。
        File.SetAttributes(temp, FileAttributes.Normal);
        File.WriteAllBytes(temp, ClfBytes(OtherClfDoc));
        Assert.Throws<InvalidDataException>(() => staged.Publish(DefaultOptions, "n", TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*.fblook"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Stage_Publish_UsesSealedBytesNotCurrentSource()
    {
        var clf = ClfBytes();
        var sourcePath = Path.Combine(_root, "src.clf");
        File.WriteAllBytes(sourcePath, clf);
        StagedColorImport staged;
        using (var fs = File.OpenRead(sourcePath))
            staged = UserTransformLibrary.Stage(_root, fs, isFblook: false, TestContext.Current.CancellationToken);
        using (staged)
        {
            // 封存後改寫來源：發布的必須仍是封存時的同一份 bytes。
            File.WriteAllBytes(sourcePath, ClfBytes(OtherClfDoc));
            var key = staged.Publish(DefaultOptions, "n", TestContext.Current.CancellationToken);
            Assert.Equal(clf, File.ReadAllBytes(AssetPath(key)).Skip(16).ToArray());
        }
    }

    [Fact]
    public void Stage_ExposesParsedTransformBeforeDeclarations()
    {
        var clf = ClfBytes();
        using var staged = UserTransformLibrary.Stage(_root, Stream(clf), isFblook: false, TestContext.Current.CancellationToken);
        Assert.False(staged.IsFblook);
        Assert.Null(staged.EmbeddedOptions);
        Assert.Equal("fixture-id", staged.Transform.Id);
        Assert.Single(staged.Transform.Ops.ToArray());
    }

    [Fact]
    public void Stage_ExposesDescriptorsWithoutReopeningSource()
    {
        var doc = Decl + "\n<ProcessList id=\"d\" compCLFversion=\"3.0\">"
            + "<Description>dtext</Description><InputDescriptor>idesc</InputDescriptor>"
            + "<OutputDescriptor>odesc</OutputDescriptor>"
            + "<Matrix inBitDepth=\"16f\" outBitDepth=\"16f\"><Array dim=\"3 3\">1 0 0 0 1 0 0 0 1</Array></Matrix>"
            + "</ProcessList>\n";
        var source = Stream(Encoding.UTF8.GetBytes(doc));
        using var staged = UserTransformLibrary.Stage(_root, source, isFblook: false, TestContext.Current.CancellationToken);
        source.Dispose(); // descriptor 已在封存時進入不可變模型，UI 展示不需重開來源
        Assert.Equal("dtext", staged.Transform.Description);
        Assert.Equal("idesc", staged.Transform.InputDescriptor);
        Assert.Equal("odesc", staged.Transform.OutputDescriptor);
    }

    [Fact]
    public void Stage_Fblook_ExposesEmbeddedOptions()
    {
        var options = new ClfImportOptions(
            ColorTransformStage.DisplayEncoded, ColorTransformEncoding.ProPhotoGamma18, ColorTransformEncoding.LinearSrgb);
        var envelope = Envelope(options, ClfBytes());
        using var staged = UserTransformLibrary.Stage(_root, Stream(envelope), isFblook: true, TestContext.Current.CancellationToken);
        Assert.True(staged.IsFblook);
        Assert.Equal(options, staged.EmbeddedOptions);
    }

    [Fact]
    public void Stage_Clf_PublishWithoutOptions_Rejected()
    {
        using var staged = UserTransformLibrary.Stage(_root, Stream(ClfBytes()), isFblook: false, TestContext.Current.CancellationToken);
        Assert.ThrowsAny<ArgumentException>(() => staged.Publish(options: null, "n", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Publish_AfterDispose_Throws()
    {
        var staged = UserTransformLibrary.Stage(_root, Stream(ClfBytes()), isFblook: false, TestContext.Current.CancellationToken);
        staged.Dispose();
        Assert.Throws<ObjectDisposedException>(() => staged.Publish(DefaultOptions, "n", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Resolve_RevalidatesFile_AfterEarlierSuccess()
    {
        var key = ImportDefault(ClfBytes());
        var asset = UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken);

        // 已回傳的不可變 snapshot 持有解析結果：檔案之後消失/腐壞不影響它。
        File.Delete(AssetPath(key));
        Assert.Equal(DefaultOptions, asset.Options);
        Assert.Equal("fixture-id", asset.Transform.Id);

        // 但每一次新的 Resolve 都要重新驗證目前檔案／hash：缺檔必須可見失敗。
        Assert.Throws<FileNotFoundException>(() => UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken));

        // 重新匯入同一份 bytes（同 key、有效檔案）→ Resolve 再次成功。
        UserTransformLibrary.Import(_root, Stream(ClfBytes()), isFblook: false, DefaultOptions, "n", TestContext.Current.CancellationToken);
        Assert.Equal("fixture-id", UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken).Transform.Id);
    }

    [Fact]
    public void Resolve_SameSizeSameTimestamp_CorruptionStillDetected()
    {
        var key = ImportDefault(ClfBytes());
        UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken); // 建立 (root,key) entry

        // 相同長度、沿用原 mtime 的覆寫：不能靠大小／時間戳跳過 hash 驗證。
        var path = AssetPath(key);
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[16] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Throws<InvalidDataException>(() => UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken));
    }


    [Fact]
    public void Resolve_CacheBounded_EvictedEntryRevalidatesFile()
    {
        var key1 = ImportDefault(ClfBytes());
        UserTransformLibrary.Resolve(_root, key1, TestContext.Current.CancellationToken);
        // 破壞已解析資產的檔案：若 cache 無上限，舊 snapshot 會掩蓋腐敗。
        var bytes = File.ReadAllBytes(AssetPath(key1));
        bytes[16] ^= 0xFF;
        File.WriteAllBytes(AssetPath(key1), bytes);
        var key2 = ImportDefault(ClfBytes(OtherClfDoc));
        UserTransformLibrary.Resolve(_root, key2, TestContext.Current.CancellationToken); // 佔用唯一 (root,key) entry
        Assert.Throws<InvalidDataException>(() => UserTransformLibrary.Resolve(_root, key1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Resolve_RootIsolation()
    {
        var other = Path.Combine(Path.GetTempPath(), "fb-ctfile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keyA = ImportDefault(ClfBytes());
            var keyB = UserTransformLibrary.Import(other, Stream(ClfBytes()), isFblook: false, DefaultOptions, "n", TestContext.Current.CancellationToken);
            Assert.Equal(keyA, keyB); // 內容定址：不同 root 產生相同 key
            // 破壞另一個 root 的同 key 檔案，不影響本 root 的解析結果。
            var otherPath = Path.Combine(other, keyB + ".fblook");
            var ob = File.ReadAllBytes(otherPath);
            ob[16] ^= 0xFF;
            File.WriteAllBytes(otherPath, ob);
            Assert.Equal(DefaultOptions, UserTransformLibrary.Resolve(_root, keyA, TestContext.Current.CancellationToken).Options);
            Assert.Throws<InvalidDataException>(() => UserTransformLibrary.Resolve(other, keyB, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, true);
        }
    }

    [Fact]
    public void Stage_NonSeekableOverCap_Rejected()
    {
        Directory.CreateDirectory(_root);
        var flood = new FloodStream(128L * 1024 * 1024 + 17, seekable: false);

        Assert.Throws<InvalidDataException>(() =>
            UserTransformLibrary.Stage(_root, flood, isFblook: true, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Stage_DeclaredLengthOverCap_RejectedWithoutCopy()
    {
        var flood = new FloodStream(128L * 1024 * 1024 + 17, seekable: true);
        Assert.Throws<InvalidDataException>(() =>
            UserTransformLibrary.Stage(_root, flood, isFblook: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Stage_SeekableSourceUsesRemainingLength()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "source");
        using var source = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        source.SetLength(ClfFile.MaxInputBytes + 1);
        source.Position = source.Length;
        var bytes = ClfBytes();
        source.Write(bytes);
        source.Position -= bytes.Length;
        using var staged = UserTransformLibrary.Stage(_root, source, false, TestContext.Current.CancellationToken);
        Assert.Equal("fixture-id", staged.Transform.Id);
    }

    [Fact]
    public void Resolve_ReleasesFileHandleAfterRepeatedReads()
    {
        var key = ImportDefault(ClfBytes());
        UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken);
        UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken);
        using var exclusive = new FileStream(AssetPath(key), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public void List_LargeNameIsBoundedWithoutAffectingOtherItems()
    {
        var key = ImportDefault(ClfBytes());
        using (var file = new FileStream(NamePath(key), FileMode.Create, FileAccess.Write))
            file.SetLength(16 * 1024 * 1024);
        var item = Assert.Single(UserTransformLibrary.List(_root));
        Assert.True(item.Name.Length <= 69);
        Assert.True(item.IsValid);
    }

    // ---------- Resolve：檔案／hash／CLF 驗證與不可變 snapshot ----------

    [Fact]
    public void Resolve_MissingFile_Throws()
    {
        var key = "look-" + new string('0', 64);
        Assert.Throws<FileNotFoundException>(() => UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Resolve_HashMismatch_Throws()
    {
        var key = ImportDefault(ClfBytes());
        var bytes = File.ReadAllBytes(AssetPath(key));
        bytes[16] ^= 0xFF; // 改內容 → envelope hash 不再等於 key
        File.WriteAllBytes(AssetPath(key), bytes);
        Assert.Throws<InvalidDataException>(() => UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Resolve_CorruptClfUnderValidHash_Throws()
    {
        // 內容 hash 合法（檔名等於內容雜湊）但 payload 不是合法 CLF → parse 失敗。
        var envelope = Envelope(DefaultOptions, "not xml"u8.ToArray());
        var key = ExpectedKey(envelope);
        File.WriteAllBytes(AssetPath(key), envelope);
        Assert.Throws<InvalidDataException>(() => UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Resolve_MalformedKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => UserTransformLibrary.Resolve(_root, "not-a-look-key", TestContext.Current.CancellationToken));
    }

    // ---------- 發布／取消邊界 ----------

    [Fact]
    public void Import_CancelledBeforePublish_LeavesNothing()
    {
        Directory.CreateDirectory(_root);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UserTransformLibrary.Import(_root, Stream(ClfBytes()), isFblook: false,
                DefaultOptions, "n", new CancellationToken(true)));
        Assert.Empty(Directory.GetFiles(_root, "*.fblook"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Stage_CancelledMidCopy_RemovesTemp()
    {
        var cts = new CancellationTokenSource();
        var stream = new CancellingStream(Stream(ClfBytes()), cts);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UserTransformLibrary.Stage(_root, stream, isFblook: false, cts.Token));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.Empty(Directory.GetFiles(_root, "*.fblook"));
    }

    [Fact]
    public void Publish_ValidAsset_SurvivesLaterCancellation()
    {
        var cts = new CancellationTokenSource();
        var staged = UserTransformLibrary.Stage(_root, Stream(ClfBytes()), isFblook: false, TestContext.Current.CancellationToken);
        var key = staged.Publish(DefaultOptions, "n", cts.Token);
        cts.Cancel();
        // 發布後的取消只屬於 caller context：已發布的合法共享資產保留，也不能被刪。
        Assert.ThrowsAny<OperationCanceledException>(() => staged.Publish(DefaultOptions, "n", cts.Token));
        staged.Dispose();
        Assert.Equal(Envelope(DefaultOptions, ClfBytes()), File.ReadAllBytes(AssetPath(key)));
        Assert.Equal(key, UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken).Key);
    }

    [Fact]
    public void Import_Duplicate_SucceedsAndKeepsVerifiedBytes()
    {
        var key1 = ImportDefault(ClfBytes(), name: "first");
        var key2 = ImportDefault(ClfBytes(), name: "second");
        Assert.Equal(key1, key2);
        Assert.Equal(Envelope(DefaultOptions, ClfBytes()), File.ReadAllBytes(AssetPath(key1)));
        Assert.Equal("second", File.ReadAllText(NamePath(key1)));
    }

    [Fact]
    public void Import_DoesNotClobberUnverifiedExistingFile()
    {
        var key = ImportDefault(ClfBytes());
        var corrupted = File.ReadAllBytes(AssetPath(key));
        corrupted[20] ^= 0x55; // 既存檔已損壞：不是「已驗證的相同內容」
        File.WriteAllBytes(AssetPath(key), corrupted);
        var before = File.ReadAllBytes(AssetPath(key));
        Assert.Throws<InvalidDataException>(() =>
            UserTransformLibrary.Import(_root, Stream(ClfBytes()), isFblook: false, DefaultOptions, "n", TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(AssetPath(key))); // 未驗證的既有檔不被覆寫
    }

    [Fact]
    public void Import_FailedValidation_PublishesNothingAndLeavesOthers()
    {
        var good = ImportDefault(ClfBytes());
        var goodBytes = File.ReadAllBytes(AssetPath(good));
        Assert.Throws<InvalidDataException>(() =>
            UserTransformLibrary.Import(_root, Stream("bad <xml"u8.ToArray()), isFblook: false, DefaultOptions, "n", TestContext.Current.CancellationToken));
        Assert.Equal(goodBytes, File.ReadAllBytes(AssetPath(good)));
        Assert.Single(Directory.GetFiles(_root, "*.fblook"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    // ---------- List：header-only、bounded name、腐敗隔離 ----------

    [Fact]
    public void List_ReadsOnlyHeaders_And_IsolatesCorruption()
    {
        var key1 = ImportDefault(ClfBytes(), name: "Good Look");
        // 合法 header 但 payload 未經驗證（hash／CLF 都要等 Resolve 才檢查）。
        var halfBad = ExpectedKey(Envelope(DefaultOptions, "payload ignored by list"u8.ToArray()));
        File.WriteAllBytes(AssetPath(halfBad), Envelope(DefaultOptions, "payload ignored by list"u8.ToArray()));
        // header 本身就壞的項目。
        var badHeader = "look-" + new string('f', 64);
        File.WriteAllBytes(AssetPath(badHeader), Envelope(DefaultOptions, ClfBytes(), reserved: 9));
        // 非 look- 檔與殘缺的 .name 不影響清單。
        File.WriteAllText(Path.Combine(_root, "user-0123456789abcdef.fblut"), "ignored");
        File.WriteAllText(Path.Combine(_root, "orphan.name"), "ignored");

        var items = UserTransformLibrary.List(_root);
        Assert.Equal(3, items.Count);

        var good = items.Single(i => i.Key == key1);
        Assert.True(good.IsValid);
        Assert.Equal(DefaultOptions, good.Options);
        Assert.Equal("Good Look", good.Name);

        var onlyHeader = items.Single(i => i.Key == halfBad);
        Assert.True(onlyHeader.IsValid);   // 16-byte header 合法即有效，不解析 chain
        Assert.Equal(halfBad, onlyHeader.Name); // 沒有 .name → 以 key 為名

        var invalid = items.Single(i => i.Key == badHeader);
        Assert.False(invalid.IsValid);
        Assert.Null(invalid.Options);
    }

    [Fact]
    public void List_ReadingInvalidItem_DoesNotPreventResolvingOthers()
    {
        var key = ImportDefault(ClfBytes());
        var bad = "look-" + new string('0', 64);
        File.WriteAllBytes(AssetPath(bad), [1, 2, 3]);
        var items = UserTransformLibrary.List(_root);
        Assert.Single(items, i => i.Key == bad && !i.IsValid);
        Assert.Single(items, i => i.Key == key && i.IsValid);
        Assert.Equal(key, UserTransformLibrary.Resolve(_root, key, TestContext.Current.CancellationToken).Key);
    }

    [Fact]
    public void List_NameBounded()
    {
        var longName = "  名稱\n換行 " + new string('x', 100);
        var key = ImportDefault(ClfBytes(), name: longName);
        var item = Assert.Single(UserTransformLibrary.List(_root));
        Assert.True(item.IsValid);
        Assert.Equal(48, item.Name.Length);
        Assert.DoesNotContain('\n', item.Name);
    }

    [Fact]
    public void List_EmptyRoot_IsEmpty()
    {
        Directory.CreateDirectory(_root);
        Assert.Empty(UserTransformLibrary.List(_root));
    }

    // ---------- FilmLookAsset 互斥容器 ----------

    [Fact]
    public void FilmLookAsset_CarriesExactlyOneKind()
    {
        var lut = FilmLut.Identity(2);
        var legacy = FilmLookAsset.FromLut(lut);
        Assert.Same(lut, legacy.Lut);
        Assert.Null(legacy.Transform);

        var asset = ColorTransformFile.Read(Stream(Envelope(DefaultOptions, ClfBytes())), TestContext.Current.CancellationToken);
        var transformed = FilmLookAsset.FromTransform(asset);
        Assert.Same(asset, transformed.Transform);
        Assert.Null(transformed.Lut);
    }

    // ---------- 測試用 stream ----------

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    }

    private sealed class FloodStream(long length, bool seekable) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? length : throw new NotSupportedException();
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            _position += n;
            return n;
        }
    }

    private sealed class CancellingStream(Stream inner, CancellationTokenSource cts) : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads++ > 0) cts.Cancel(); // 第一讀之後取消：複製途中
            return inner.Read(buffer, offset, count);
        }
    }
}
