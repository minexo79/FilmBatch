using System.CommandLine;
using System.Text.Json;
using FilmBatch.Engine.Imaging.Raw;

namespace FilmBatch.Cli;

internal static class LensDatabaseCommands
{
    public static Command Create()
    {
        var status = new Command("status", "顯示已下載的 Lensfun 資料庫版本、來源與授權");
        status.SetAction(_ =>
        {
            var store = new LensDatabaseStore();
            Console.WriteLine(store.RootPath);
            Console.WriteLine(store.Installed is { } info
                ? $"installed: {info.ShortCommit} ({info.CommitDate:yyyy-MM-dd}) · {info.FileCount} files · {store.InstalledDirectory}"
                : "missing");
            Console.WriteLine($"  {LensDatabaseStore.License} · {LensDatabaseStore.Attribution}");
            Console.WriteLine($"  {LensDatabaseStore.SourceUrl}");
            return 0;
        });

        var download = new Command("download", "明確下載最新的 Lensfun data/db 並逐檔驗證（不修改照片設定）");
        download.SetAction(async (_, cancellationToken) =>
        {
            var store = new LensDatabaseStore();
            try
            {
                Console.WriteLine($"{LensDatabaseStore.License} · {LensDatabaseStore.Attribution}");
                Console.WriteLine($"Downloading {LensDatabaseStore.SourceUrl}");
                var info = await store.InstallAsync(cancellationToken: cancellationToken);
                Console.WriteLine($"Installed: {info.ShortCommit} ({info.CommitDate:yyyy-MM-dd}) · {info.FileCount} files · {store.InstalledDirectory}");
                return 0;
            }
            catch (OperationCanceledException) { Console.Error.WriteLine("已取消資料庫下載。"); return 130; }
            catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException
                or InvalidDataException or JsonException or System.Xml.XmlException)
            { Console.Error.WriteLine(error.Message); return 3; }
        });
        return new Command("lens-database", "管理本機 Lensfun 鏡頭資料庫（RAW 讀取不會自動下載）") { status, download };
    }
}
