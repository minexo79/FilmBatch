using FilmBatch.Core.Params;
using FilmBatch.Core.Projects;

namespace FilmBatch.Engine.Imaging;

/// <summary>批次去背每張照片的終端狀態：<see cref="Generated"/> 為暫存、<see cref="Applied"/> 為已保存。</summary>
public enum ForegroundBatchStatus
{
    Generated,
    Applied,
    Skipped,
    NoSubject,
    Failed,
    Cancelled,
}

/// <summary>批次去背的一張工作：快照的檔名、來源路徑、照片項目與已解析設定。</summary>
public sealed record ForegroundBatchJob(string FileName, string SourcePath, PhotoEntry Entry, Settings Settings);

/// <summary>批次去背一張的結果：暫存或終端的狀態、遮罩與錯誤。</summary>
public sealed record ForegroundBatchItem(ForegroundBatchJob Job, ForegroundBatchStatus Status, ForegroundMask? Mask = null, string? Error = null);

/// <summary>批次去背的進度：已完成數、總數、目前檔名。</summary>
public sealed record ForegroundBatchProgress(int Done, int Total, string CurrentFile);

/// <summary>批次去背的暫存結果：RunAsync 從不改動專案，Apply 才保存。</summary>
public sealed record ForegroundBatchResult(IReadOnlyList<ForegroundBatchItem> Items, bool Cancelled);

/// <summary>批次去背的保存摘要：每張恰有一個終端項目。</summary>
public sealed record ForegroundBatchSummary(
    int Total, int Applied, int Skipped, int NoSubject, int Failed, bool Cancelled, IReadOnlyList<ForegroundBatchItem> Items);

/// <summary>
/// 多選批次前景去背：快照計算、同步套用。
/// RunAsync 只載入代理圖並呼叫原生產生器，絕不改動專案、不送 EntriesChanged；
/// Apply 在呼叫端執行緒驗證並一次保存全部合格遮罩。
/// </summary>
public static class ForegroundBatch
{
    /// <summary>由服務快照選取檔名：去重（Ordinal）、保留首次順序，快照來源路徑、照片項目與已解析設定。</summary>
    public static IReadOnlyList<ForegroundBatchJob> BuildJobs(ProjectService service, IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(files);
        var jobs = new List<ForegroundBatchJob>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ArgumentException.ThrowIfNullOrEmpty(file);
            if (!seen.Add(file))
                continue;
            var entry = service.Project[file];
            jobs.Add(new ForegroundBatchJob(file, service.Project.PathOf(file), entry, service.SettingsFor(file)));
        }
        return jobs;
    }

    /// <summary>
    /// 依序載入代理圖並產生前景遮罩；已有遮罩略過不做原生工作。
    /// 原生 null 為 NoSubject；載入／原生錯誤為 Failed 且不中斷後續；
    /// 取消丟棄進行中結果、未開始標 Cancelled，已暫存保留。永不重試、不放大整張。
    /// </summary>
    public static Task<ForegroundBatchResult> RunAsync(
        IReadOnlyList<ForegroundBatchJob> jobs,
        IProgress<ForegroundBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return RunCoreAsync(jobs, ForegroundSelection.Generate, progress, cancellationToken);
    }

    /// <summary>測試用多載：隔離外部非確定性 Vision 邊界；行為（狀態／持久化／計數）與正式路徑相同。</summary>
    internal static Task<ForegroundBatchResult> RunAsync(
        IReadOnlyList<ForegroundBatchJob> jobs,
        Func<SourceImage, CancellationToken, ForegroundMask?> generate,
        IProgress<ForegroundBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(generate);
        return RunCoreAsync(jobs, generate, progress, cancellationToken);
    }

    private static async Task<ForegroundBatchResult> RunCoreAsync(
        IReadOnlyList<ForegroundBatchJob> jobs,
        Func<SourceImage, CancellationToken, ForegroundMask?> generate,
        IProgress<ForegroundBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var items = new List<ForegroundBatchItem>(jobs.Count);
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (cancellationToken.IsCancellationRequested)
            {
                for (; i < jobs.Count; i++)
                    items.Add(new ForegroundBatchItem(jobs[i], ForegroundBatchStatus.Cancelled));
                return new ForegroundBatchResult(items, Cancelled: true);
            }
            if (job.Entry.ForegroundMask is not null)
            {
                items.Add(new ForegroundBatchItem(job, ForegroundBatchStatus.Skipped));
                progress?.Report(new ForegroundBatchProgress(items.Count, jobs.Count, job.FileName));
                continue;
            }
            ForegroundBatchItem item;
            try
            {
                // 背景任務載入＋推論；取消在投遞前檢查，進行中結果一律丟棄。
                var staged = await Task.Run(() =>
                {
                    var source = ImageLoader.Load(job.SourcePath, ForegroundSelection.MaxModelLongEdge,
                        RawLoadOptions.FromSettings(job.Settings));
                    var mask = generate(source, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return mask;
                }, cancellationToken).ConfigureAwait(false);
                item = staged is null
                    ? new ForegroundBatchItem(job, ForegroundBatchStatus.NoSubject)
                    : new ForegroundBatchItem(job, ForegroundBatchStatus.Generated, staged);
            }
            catch (OperationCanceledException)
            {
                items.Add(new ForegroundBatchItem(job, ForegroundBatchStatus.Cancelled));
                for (var j = i + 1; j < jobs.Count; j++)
                    items.Add(new ForegroundBatchItem(jobs[j], ForegroundBatchStatus.Cancelled));
                return new ForegroundBatchResult(items, Cancelled: true);
            }
            catch (Exception ex)
            {
                item = new ForegroundBatchItem(job, ForegroundBatchStatus.Failed, Error: ex.Message);
            }
            // 晚到取消：投遞前再次檢查，避免把已取消的工作發佈為 Generated。
            if (item.Status == ForegroundBatchStatus.Generated && cancellationToken.IsCancellationRequested)
            {
                items.Add(new ForegroundBatchItem(job, ForegroundBatchStatus.Cancelled));
                for (var j = i + 1; j < jobs.Count; j++)
                    items.Add(new ForegroundBatchItem(jobs[j], ForegroundBatchStatus.Cancelled));
                return new ForegroundBatchResult(items, Cancelled: true);
            }
            items.Add(item);
            progress?.Report(new ForegroundBatchProgress(items.Count, jobs.Count, job.FileName));
        }
        return new ForegroundBatchResult(items, Cancelled: false);
    }

    /// <summary>
    /// 同步驗證並保存：來源擁有權、快照項目一致、已解析設定一致、未被改動且無遮罩。
    /// 合格遮罩一次經 SetForegroundMasks 保存為一筆命令；保存失敗不謊稱 Applied。
    /// </summary>
    public static ForegroundBatchSummary Apply(ProjectService service, ForegroundBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(result);
        var project = service.Project;
        var eligible = new Dictionary<string, ForegroundMask>(StringComparer.Ordinal);
        var staged = new Dictionary<string, ForegroundBatchItem>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outputs = new List<ForegroundBatchItem>(result.Items.Count);

        foreach (var item in result.Items)
        {
            if (!seen.Add(item.Job.FileName))
                continue;
            var name = item.Job.FileName;
            if (item.Status != ForegroundBatchStatus.Generated)
                outputs.Add(item);
            else if (item.Mask is null)
                outputs.Add(item with { Status = ForegroundBatchStatus.Failed, Error = "去背結果缺少遮罩。" });
            else if (!IsOwned(project, item.Job))
                outputs.Add(item with { Status = ForegroundBatchStatus.Failed, Error = "照片不屬於此專案或已更動。" });
            else if (project[name].ForegroundMask is not null)
                outputs.Add(item with { Status = ForegroundBatchStatus.Skipped, Mask = null });
            else if (!item.Job.Entry.Equals(project[name]) || !item.Job.Settings.Equals(service.SettingsFor(name)))
                outputs.Add(item with { Status = ForegroundBatchStatus.Failed, Error = "照片或設定在批次期間已變更。" });
            else
                outputs.Add(Stage(item, eligible, staged));
        }

        if (eligible.Count == 0)
            return Summarize(outputs, result.Cancelled);
        // 原子保存：成功才標 Applied；例外直接上拋，不回報 Applied、不佔用復原。
        // 正常流程不會是 no-op（已驗證無遮罩），若為 no-op 誠實標 Failed。
        if (!service.SetForegroundMasks(eligible))
        {
            for (var i = 0; i < outputs.Count; i++)
                if (staged.ContainsKey(outputs[i].Job.FileName))
                    outputs[i] = outputs[i] with { Status = ForegroundBatchStatus.Failed, Error = "去背結果未保存。" };
            return Summarize(outputs, result.Cancelled);
        }
        for (var i = 0; i < outputs.Count; i++)
            if (staged.ContainsKey(outputs[i].Job.FileName))
                outputs[i] = outputs[i] with { Status = ForegroundBatchStatus.Applied };
        return Summarize(outputs, result.Cancelled);
    }

    private static ForegroundBatchItem Stage(
        ForegroundBatchItem item,
        Dictionary<string, ForegroundMask> eligible,
        Dictionary<string, ForegroundBatchItem> staged)
    {
        eligible[item.Job.FileName] = item.Mask!;
        staged[item.Job.FileName] = item;
        return item;
    }

    private static bool IsOwned(Project project, ForegroundBatchJob job)
    {
        if (!project.FileNames.Contains(job.FileName, StringComparer.Ordinal))
            return false;
        return string.Equals(project.PathOf(job.FileName), job.SourcePath, StringComparison.Ordinal)
            && string.Equals(Path.GetFullPath(project.Folder), Path.GetDirectoryName(job.SourcePath), StringComparison.Ordinal);
    }

    private static ForegroundBatchSummary Summarize(List<ForegroundBatchItem> outputs, bool cancelled)
    {
        var applied = 0;
        var skipped = 0;
        var noSubject = 0;
        var failed = 0;
        foreach (var item in outputs)
        {
            switch (item.Status)
            {
                case ForegroundBatchStatus.Applied: applied++; break;
                case ForegroundBatchStatus.Skipped: skipped++; break;
                case ForegroundBatchStatus.NoSubject: noSubject++; break;
                case ForegroundBatchStatus.Failed: failed++; break;
                case ForegroundBatchStatus.Generated: failed++; break;
                case ForegroundBatchStatus.Cancelled: break;
                default: failed++; break;
            }
        }
        for (var i = 0; i < outputs.Count; i++)
            if (outputs[i].Status == ForegroundBatchStatus.Generated)
                outputs[i] = outputs[i] with { Status = ForegroundBatchStatus.Failed, Error = "去背結果未保存。" };
        return new ForegroundBatchSummary(outputs.Count, applied, skipped, noSubject, failed, cancelled, outputs);
    }
}
