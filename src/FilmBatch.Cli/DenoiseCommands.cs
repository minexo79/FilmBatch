using System.CommandLine;
using FilmBatch.Engine.Denoising;

namespace FilmBatch.Cli;

internal static class DenoiseCommands
{
    public static Command Create()
    {
        var status = new Command("status", "列出模型、授權及本機安裝狀態");
        status.SetAction(_ =>
        {
            var store = new DenoiseModelStore();
            Console.WriteLine(store.DirectoryPath);
            foreach (var model in DenoiseModelCatalog.All)
            {
                Console.WriteLine($"{model.Id}: {(store.IsInstalled(model) ? "installed" : "missing")} · {model.Name}");
                Console.WriteLine($"  {model.License} · {model.Attribution}");
                Console.WriteLine($"  {model.DownloadUri}");
            }
            return 0;
        });

        var installId = new Argument<string>("model") { Description = "rawnind-linear | nafnet-sidd | all" };
        var install = new Command("install", "明確下載官方模型並驗證 SHA-256（不修改照片設定）") { installId };
        install.SetAction(async (result, cancellationToken) =>
        {
            var id = result.GetValue(installId)!;
            var selected = DenoiseModelCatalog.All.Where(model => id == "all" || model.Id == id).ToArray();
            if (selected.Length == 0) return UnknownModel(id);
            var store = new DenoiseModelStore();
            try
            {
                foreach (var model in selected)
                {
                    Console.WriteLine($"{model.Name} · {model.License} · {model.Attribution}");
                    Console.WriteLine($"Downloading {model.DownloadUri}");
                    await store.InstallAsync(model, cancellationToken: cancellationToken);
                    Console.WriteLine($"Installed: {store.ModelPath(model)}");
                }
                return 0;
            }
            catch (OperationCanceledException) { Console.Error.WriteLine("已取消模型下載。"); return 130; }
            catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException)
            { Console.Error.WriteLine(error.Message); return 3; }
        });

        var importId = new Argument<string>("model") { Description = "rawnind-linear | nafnet-sidd" };
        var package = new Argument<string>("package") { Description = "官方 .dtmodel／ZIP 套件或符合固定雜湊的 ONNX 檔" };
        var import = new Command("import", "驗證並匯入已下載的模型；不連線") { importId, package };
        import.SetAction(async (result, cancellationToken) =>
        {
            var id = result.GetValue(importId)!;
            var model = DenoiseModelCatalog.All.FirstOrDefault(candidate => candidate.Id == id);
            if (model is null) return UnknownModel(id);
            var store = new DenoiseModelStore();
            try
            {
                Console.WriteLine($"{model.Name} · {model.License} · {model.Attribution}");
                await store.ImportAsync(model, Path.GetFullPath(result.GetValue(package)!), cancellationToken);
                Console.WriteLine($"Installed: {store.ModelPath(model)}");
                return 0;
            }
            catch (OperationCanceledException) { Console.Error.WriteLine("已取消模型匯入。"); return 130; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine(error.Message); return 3; }
        });
        return new Command("denoise-models", "管理本機 AI 降噪模型（推論不會自動下載）") { status, install, import };
    }

    private static int UnknownModel(string id)
    {
        Console.Error.WriteLine($"未知模型：{id}；可用：{string.Join(", ", DenoiseModelCatalog.All.Select(model => model.Id))}");
        return 2;
    }
}
