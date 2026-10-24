using System.CommandLine;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Cli;

/// <summary><c>remove-background</c> 子命令：批次產生本機 AI 去背遮罩並保存到專案，不匯出圖片檔。</summary>
internal static class ForegroundCommands
{
    public static Command Create()
    {
        var folderArg = new Argument<string>("folder") { Description = "照片資料夾（已含 .filmbatch/project.json 或即時掃描）" };
        var presetsFolderOption = new Option<string?>("--presets-folder") { Description = "預設庫資料夾（預設使用者 presets）" };
        var command = new Command("remove-background", "批次產生本機 AI 去背遮罩並保存（略過已有遮罩的照片）")
        {
            folderArg, presetsFolderOption,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            string folder;
            try
            {
                folder = Path.GetFullPath(result.GetValue(folderArg)!);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                Console.Error.WriteLine($"照片資料夾無效：{ex.Message}");
                return 2;
            }
            if (!Directory.Exists(folder))
            {
                Console.Error.WriteLine($"找不到照片資料夾：{folder}");
                return 2;
            }
            // 先驗證平台支援度：不支援時不建立任何預設或專案輸出。
            if (!ForegroundSelection.IsSupported)
            {
                Console.Error.WriteLine("此平台不支援本機 AI 去背（需 macOS 14+）。");
                return 2;
            }

            PresetLibrary library;
            try
            {
                library = PresetLibrary.Load(result.GetValue(presetsFolderOption));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"讀取預設庫失敗：{ex.Message}");
                return 2;
            }
            Project project;
            try
            {
                var defaultId = library.Presets.FirstOrDefault()?.Id ?? Guid.Empty;
                project = Project.Open(folder, defaultId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"開啟專案失敗：{ex.Message}");
                return 2;
            }
            // ProjectService.PresetFor 對重開專案本就保留快照直到明確同步；CLI 不做任何同步/更新。
            var service = new ProjectService(project, library, new CommandStack());
            var files = project.FileNames.ToList();
            if (files.Count == 0)
            {
                Console.Error.WriteLine("沒有符合的照片。");
                return 1;
            }

            IReadOnlyList<ForegroundBatchJob> jobs;
            try
            {
                jobs = ForegroundBatch.BuildJobs(service, files);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"準備去背工作失敗：{ex.Message}");
                return 3;
            }
            if (jobs.Count == 0)
            {
                Console.Error.WriteLine("沒有符合的照片。");
                return 1;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                var progress = new Progress<ForegroundBatchProgress>(p =>
                    Console.WriteLine($"[{p.Done}/{p.Total}] {p.CurrentFile}"));
                ForegroundBatchResult batch;
                try
                {
                    batch = await ForegroundBatch.RunAsync(jobs, progress, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("已取消。");
                    return 130;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"去背失敗：{ex.Message}");
                    return 3;
                }

                // 取消也會保存已完成的暫存結果；保存失敗不謊稱已套用。
                ForegroundBatchSummary summary;
                try
                {
                    summary = ForegroundBatch.Apply(service, batch);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"保存去背遮罩失敗：{ex.Message}");
                    return 3;
                }

                foreach (var item in summary.Items)
                {
                    var line = item.Status switch
                    {
                        ForegroundBatchStatus.Applied => $"{item.Job.FileName}：已套用去背遮罩",
                        ForegroundBatchStatus.Skipped => $"{item.Job.FileName}：已跳過（已有遮罩）",
                        ForegroundBatchStatus.NoSubject => $"{item.Job.FileName}：無主體，未產生遮罩",
                        ForegroundBatchStatus.Failed => $"{item.Job.FileName}：失敗：{item.Error ?? "未知錯誤"}",
                        ForegroundBatchStatus.Cancelled => $"{item.Job.FileName}：已取消",
                        _ => $"{item.Job.FileName}：未保存（{item.Status}），未寫入專案",
                    };
                    if (item.Status is ForegroundBatchStatus.Failed or ForegroundBatchStatus.NoSubject or ForegroundBatchStatus.Generated)
                        Console.Error.WriteLine(line);
                    else
                        Console.WriteLine(line);
                }
                Console.WriteLine($"完成：共 {summary.Total} 張，套用 {summary.Applied}、跳過 {summary.Skipped}、無主體 {summary.NoSubject}、失敗 {summary.Failed}。");
                if (summary.Cancelled)
                {
                    Console.Error.WriteLine("已取消。");
                    return 130;
                }
                return summary.Failed > 0 || summary.NoSubject > 0 || summary.Items.Any(item => item.Status == ForegroundBatchStatus.Generated) ? 3 : 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        });
        return command;
    }
}
