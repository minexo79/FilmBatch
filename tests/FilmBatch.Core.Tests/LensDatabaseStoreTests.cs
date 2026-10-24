using System.Text;
using FilmBatch.Engine.Imaging.Raw;

namespace FilmBatch.Core.Tests;

/// <summary>Lensfun 下載版本的離線行為：git blob 雜湊、指標檔解析與防路徑竄改。</summary>
public sealed class LensDatabaseStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FilmBatch-lensstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void GitBlobSha1_MatchesGit()
    {
        // `printf 'hello\n' | git hash-object --stdin`
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", LensDatabaseStore.GitBlobSha1(Encoding.ASCII.GetBytes("hello\n")));
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", LensDatabaseStore.GitBlobSha1([]));
    }

    [Fact]
    public void Installed_ReadsPointerToVersionFolder()
    {
        var folder = Path.Combine(_root, "db-0123456789ab");
        Directory.CreateDirectory(folder);
        WritePointer("db-0123456789ab");
        var store = new LensDatabaseStore(_root);
        Assert.Equal(folder, store.InstalledDirectory);
        Assert.Equal("0123456", store.Installed!.ShortCommit);
        Assert.Equal(59, store.Installed.FileCount);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    [InlineData("C:\\\\Windows")]
    [InlineData("db-missing00000")]
    public void Installed_RejectsForeignOrMissingFolders(string folder)
    {
        Directory.CreateDirectory(_root);
        WritePointer(folder);
        var store = new LensDatabaseStore(_root);
        Assert.Null(store.Installed);
        Assert.Null(store.InstalledDirectory);
    }

    [Fact]
    public void Installed_IsNullWithoutPointerOrWhenCorrupt()
    {
        var store = new LensDatabaseStore(_root);
        Assert.Null(store.Installed);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "installed.json"), "{ not json");
        Assert.Null(store.Installed);
    }

    private void WritePointer(string folder) => File.WriteAllText(Path.Combine(_root, "installed.json"), $$"""
        { "folder": "{{folder}}", "commit": "0123456789abcdef0123456789abcdef01234567",
          "commit_date": "2026-10-23T17:36:00Z", "files": 59, "downloaded_at": "2026-10-24T00:00:00Z" }
        """);
}
