using System.Security.Cryptography;
using FilmBatch.Core.Commands;
using FilmBatch.Core.Presets;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.Core.Tests;

/// <summary>
/// 批次去背行為迴歸：真實 Core 專案持久化、單一復原群組、既有遮罩保留、
/// 錯誤／無主體／取消誠實計數、快照變更與磁碟版本衝突拒絕。
/// Vision 為外部非確定性邊界，成功路徑以內部產生器多載隔離；
/// 斷言皆為真實狀態／持久化／計數／錯誤行為，不斷言模擬回傳本身。
/// </summary>
public sealed class ForegroundBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filmbatch-batch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string NewPhotoFolder(int count, int longEdge = 96)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        SampleImages.Generate(dir, count, longEdge);
        return dir;
    }

    private static ProjectService NewService(string folder, PresetLibrary library) =>
        new(Project.Open(folder, SamplePresets.PortraWarm), library, new CommandStack());

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class RecordingProgress : IProgress<ForegroundBatchProgress>
    {
        private readonly object _lock = new();
        public List<ForegroundBatchProgress> Reports { get; } = [];
        public void Report(ForegroundBatchProgress value)
        {
            lock (_lock)
                Reports.Add(value);
        }
    }

    private static ForegroundMask MaskForSource(SourceImage source, ushort fill)
    {
        var values = new ushort[source.Width * source.Height];
        Array.Fill(values, fill);
        return new ForegroundMask(source.Width, source.Height, source.ReferenceWidth, source.ReferenceHeight, values);
    }

    private static ForegroundMask FixedMask(ushort fill, int width = 4, int height = 3)
    {
        var values = new ushort[width * height];
        Array.Fill(values, fill);
        for (var i = 0; i < values.Length; i++)
            values[i] = (ushort)(fill + i);
        return new ForegroundMask(width, height, width * 2, height * 2, values);
    }

    [Fact]
    public void BuildJobs_CapturesDistinctJobsWithoutMutatingProject()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var changed = 0;
        service.Project.EntriesChanged += (_, _) => changed++;

        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif", "DSC_0012.tif"]);

        Assert.Equal(2, jobs.Count);
        Assert.Equal(["DSC_0012.tif", "DSC_0013.tif"], jobs.Select(j => j.FileName).ToArray());
        foreach (var job in jobs)
        {
            Assert.Equal(service.Project.PathOf(job.FileName), job.SourcePath);
            Assert.Equal(Path.GetFullPath(Path.Combine(folder, job.FileName)), job.SourcePath);
            Assert.Equal(service.Project[job.FileName], job.Entry);
            Assert.Equal(service.SettingsFor(job.FileName), job.Settings);
        }
        Assert.Equal(0, changed);
        Assert.False(service.Stack.CanUndo);
        Assert.False(service.Stack.CanRedo);
        Assert.All(service.Project.FileNames, f => Assert.Null(service.Project[f].ForegroundMask));
    }

    [Fact]
    public async Task RunAsync_GeneratesWithoutMutatingLiveProjectAndReportsProgress()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif"]);
        var calls = new List<string>();
        var progress = new RecordingProgress();
        var changed = 0;
        service.Project.EntriesChanged += (_, _) => changed++;
        var beforeA = HashFile(Path.Combine(folder, "DSC_0012.tif"));
        var beforeB = HashFile(Path.Combine(folder, "DSC_0013.tif"));

        var result = await ForegroundBatch.RunAsync(jobs, (source, _) =>
        {
            lock (calls)
                calls.Add(Path.GetFileName(source.FilePath)!);
            var fill = Path.GetFileName(source.FilePath) == "DSC_0012.tif" ? (ushort)1000 : (ushort)2000;
            return MaskForSource(source, fill);
        }, progress, TestContext.Current.CancellationToken);

        Assert.False(result.Cancelled);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(["DSC_0012.tif", "DSC_0013.tif"], calls.ToArray());
        Assert.All(result.Items, item =>
        {
            Assert.Equal(ForegroundBatchStatus.Generated, item.Status);
            Assert.NotNull(item.Mask);
            Assert.Null(item.Error);
        });
        Assert.Equal(jobs[0], result.Items[0].Job);
        Assert.Equal(jobs[1], result.Items[1].Job);
        Assert.NotEqual(result.Items[0].Mask, result.Items[1].Mask);
        Assert.Equal((ushort)1000, result.Items[0].Mask!.Values[0]);
        Assert.Equal((ushort)2000, result.Items[1].Mask!.Values[0]);

        Assert.Equal(2, progress.Reports.Count);
        Assert.Equal(new ForegroundBatchProgress(1, 2, "DSC_0012.tif"), progress.Reports[0]);
        Assert.Equal(new ForegroundBatchProgress(2, 2, "DSC_0013.tif"), progress.Reports[1]);

        Assert.Equal(0, changed);
        Assert.False(service.Stack.CanUndo);
        Assert.All(service.Project.FileNames, f => Assert.Null(service.Project[f].ForegroundMask));
        Assert.All(service.Project.FileNames, f => Assert.Null(Project.Open(folder, SamplePresets.PortraWarm)[f].ForegroundMask));
        Assert.Equal(beforeA, HashFile(Path.Combine(folder, "DSC_0012.tif")));
        Assert.Equal(beforeB, HashFile(Path.Combine(folder, "DSC_0013.tif")));
    }

    [Fact]
    public async Task RunAsync_SkipsAlreadyMaskedWithoutNativeWork()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var prior = FixedMask(7000);
        Assert.True(service.SetForegroundMask("DSC_0012.tif", prior));
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif"]);
        var calls = 0;
        var progress = new RecordingProgress();

        var result = await ForegroundBatch.RunAsync(jobs, (source, _) =>
        {
            Interlocked.Increment(ref calls);
            return MaskForSource(source, 2000);
        }, progress, TestContext.Current.CancellationToken);

        Assert.False(result.Cancelled);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(ForegroundBatchStatus.Skipped, result.Items[0].Status);
        Assert.Null(result.Items[0].Mask);
        Assert.Null(result.Items[0].Error);
        Assert.Equal(ForegroundBatchStatus.Generated, result.Items[1].Status);
        Assert.NotNull(result.Items[1].Mask);
        Assert.Equal(1, calls);
        Assert.Equal(2, progress.Reports.Count);
        Assert.Equal(prior, service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Null(service.Project["DSC_0013.tif"].ForegroundMask);
    }

    [Fact]
    public async Task RunAsync_UsesProxy2048AndSettingsDerivedRawOptions()
    {
        var folder = NewPhotoFolder(1, longEdge: 2400);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        Assert.True(service.SetValue(["DSC_0012.tif"], "raw.enabled", true));
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif"]);
        var job = Assert.Single(jobs);
        var seen = new List<SourceImage>();
        var original = ImageLoader.DecodeInfo(Path.Combine(folder, "DSC_0012.tif"));

        var result = await ForegroundBatch.RunAsync(jobs, (source, _) =>
        {
            lock (seen)
                seen.Add(source);
            return MaskForSource(source, 1234);
        }, null, TestContext.Current.CancellationToken);

        var single = Assert.Single(result.Items);
        Assert.Equal(ForegroundBatchStatus.Generated, single.Status);
        var source = Assert.Single(seen);
        Assert.Equal(job.SourcePath, source.FilePath);
        Assert.Equal(RawLoadOptions.FromSettings(job.Settings), source.RawOptions);
        Assert.True(source.RawOptions.IncludeSemanticMasks);
        Assert.Equal(original, (source.ReferenceWidth, source.ReferenceHeight));
        Assert.Equal(2048, Math.Max(source.Width, source.Height));
        Assert.True(source.Width <= original.Width && source.Height <= original.Height);
        Assert.Equal((source.Width, source.Height), (single.Mask!.Width, single.Mask.Height));
        Assert.Equal((source.ReferenceWidth, source.ReferenceHeight), (single.Mask.ReferenceWidth, single.Mask.ReferenceHeight));
    }

    [Fact]
    public async Task RunAsync_NullIsNoSubjectAndErrorsDoNotStopLaterFiles()
    {
        var folder = NewPhotoFolder(3);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif", "DSC_0014.tif"]);
        var calls = new List<string>();

        var result = await ForegroundBatch.RunAsync(jobs, (source, _) =>
        {
            var name = Path.GetFileName(source.FilePath)!;
            lock (calls)
                calls.Add(name);
            if (name == "DSC_0012.tif")
                return null;
            if (name == "DSC_0013.tif")
                throw new InvalidOperationException("vision boom");
            return MaskForSource(source, 3000);
        }, null, TestContext.Current.CancellationToken);

        Assert.False(result.Cancelled);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(ForegroundBatchStatus.NoSubject, result.Items[0].Status);
        Assert.Null(result.Items[0].Mask);
        Assert.Null(result.Items[0].Error);
        Assert.Equal(ForegroundBatchStatus.Failed, result.Items[1].Status);
        Assert.Null(result.Items[1].Mask);
        Assert.Contains("vision boom", result.Items[1].Error ?? "");
        Assert.Equal(ForegroundBatchStatus.Generated, result.Items[2].Status);
        Assert.NotNull(result.Items[2].Mask);
        Assert.Equal(3, calls.Count);
        Assert.All(service.Project.FileNames, f => Assert.Null(service.Project[f].ForegroundMask));
        Assert.False(service.Stack.CanUndo);
    }

    [Fact]
    public async Task RunAsync_MissingAndCorruptSourcesAreFailedWithoutStoppingLaterFiles()
    {
        var folder = NewPhotoFolder(2);
        File.WriteAllBytes(Path.Combine(folder, "bad.tif"), "not an image at all"u8.ToArray());
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "bad.tif", "DSC_0013.tif"]);
        File.Delete(Path.Combine(folder, "DSC_0013.tif"));
        var calls = new List<string>();

        var result = await ForegroundBatch.RunAsync(jobs, (source, _) =>
        {
            lock (calls)
                calls.Add(Path.GetFileName(source.FilePath)!);
            return MaskForSource(source, 1500);
        }, null, TestContext.Current.CancellationToken);

        Assert.False(result.Cancelled);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(ForegroundBatchStatus.Generated, result.Items[0].Status);
        Assert.Equal(ForegroundBatchStatus.Failed, result.Items[1].Status);
        Assert.NotNull(result.Items[1].Error);
        Assert.Null(result.Items[1].Mask);
        Assert.Equal(ForegroundBatchStatus.Failed, result.Items[2].Status);
        Assert.NotNull(result.Items[2].Error);
        Assert.Null(result.Items[2].Mask);
        Assert.Equal(["DSC_0012.tif"], calls.ToArray());
        Assert.All(["DSC_0012.tif", "bad.tif", "DSC_0013.tif"], f => Assert.Null(service.Project[f].ForegroundMask));
        Assert.False(service.Stack.CanUndo);
    }

    [Fact]
    public async Task RunAsync_PreCancelledReturnsCancelledWithoutNativeWork()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif"]);
        var calls = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await ForegroundBatch.RunAsync(jobs, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return FixedMask(1);
        }, null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(ForegroundBatchStatus.Cancelled, item.Status);
            Assert.Null(item.Mask);
        });
        Assert.Equal(0, calls);
        Assert.All(service.Project.FileNames, f => Assert.Null(service.Project[f].ForegroundMask));
        Assert.False(service.Stack.CanUndo);
    }

    [Fact]
    public async Task RunAsync_LateCancellationDropsActiveResultButKeepsStaged()
    {
        var folder = NewPhotoFolder(3);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif", "DSC_0014.tif"]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        using var cts = new CancellationTokenSource();

        ForegroundMask? Generate(SourceImage source, CancellationToken _)
        {
            var name = Path.GetFileName(source.FilePath)!;
            lock (calls)
                calls.Add(name);
            if (name == "DSC_0012.tif")
                return MaskForSource(source, 1111);
            if (name == "DSC_0013.tif")
            {
                entered.SetResult();
                release.Task.Wait(TimeSpan.FromSeconds(10));
                return MaskForSource(source, 2222);
            }
            return MaskForSource(source, 3333);
        }

        var runTask = Task.Run(() => ForegroundBatch.RunAsync(jobs, Generate, null, cts.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cts.Cancel();
        release.SetResult();
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(result.Cancelled);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(ForegroundBatchStatus.Generated, result.Items[0].Status);
        Assert.NotNull(result.Items[0].Mask);
        Assert.Equal(ForegroundBatchStatus.Cancelled, result.Items[1].Status);
        Assert.Null(result.Items[1].Mask);
        Assert.Equal(ForegroundBatchStatus.Cancelled, result.Items[2].Status);
        Assert.Null(result.Items[2].Mask);
        Assert.Equal(2, calls.Count);
        Assert.DoesNotContain("DSC_0014.tif", calls);
        Assert.All(service.Project.FileNames, f => Assert.Null(service.Project[f].ForegroundMask));
        Assert.False(service.Stack.CanUndo);
    }

    [Fact]
    public async Task Apply_PersistsEligibleMasksAsSingleUndoPreservingSourcesAndSettings()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        Assert.True(service.SetValue(["DSC_0012.tif"], "basic.exposure", 0.75));
        service.Stack.Clear();
        var beforeA = HashFile(Path.Combine(folder, "DSC_0012.tif"));
        var beforeB = HashFile(Path.Combine(folder, "DSC_0013.tif"));
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif"]);

        var result = await ForegroundBatch.RunAsync(jobs, (source, _) =>
            MaskForSource(source, Path.GetFileName(source.FilePath) == "DSC_0012.tif" ? (ushort)4100 : (ushort)4200),
            null, TestContext.Current.CancellationToken);
        var events = new List<IReadOnlyCollection<string>>();
        service.Project.EntriesChanged += (_, names) => events.Add(names);

        var summary = ForegroundBatch.Apply(service, result);

        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.Applied);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(0, summary.NoSubject);
        Assert.Equal(0, summary.Failed);
        Assert.False(summary.Cancelled);
        Assert.Equal(2, summary.Items.Count);
        Assert.All(summary.Items, item => Assert.Equal(ForegroundBatchStatus.Applied, item.Status));
        Assert.Equal(result.Items[0].Mask, service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Equal(result.Items[1].Mask, service.Project["DSC_0013.tif"].ForegroundMask);
        Assert.NotEqual(service.Project["DSC_0012.tif"].ForegroundMask, service.Project["DSC_0013.tif"].ForegroundMask);
        Assert.Equal((ushort)4100, service.Project["DSC_0012.tif"].ForegroundMask!.Values[0]);
        Assert.Equal((ushort)4200, service.Project["DSC_0013.tif"].ForegroundMask!.Values[0]);
        Assert.Single(events);
        Assert.Equal(["DSC_0012.tif", "DSC_0013.tif"], events[0].Order(StringComparer.Ordinal).ToArray());

        var reopened = Project.Open(folder, SamplePresets.PortraWarm);
        Assert.Equal(result.Items[0].Mask, reopened["DSC_0012.tif"].ForegroundMask);
        Assert.Equal(result.Items[1].Mask, reopened["DSC_0013.tif"].ForegroundMask);
        Assert.Equal(3, reopened.PipelineVersion);
        using var checkLibrary = new PresetLibrary(SamplePresets.Create());
        var check = new ProjectService(reopened, checkLibrary, new CommandStack());
        Assert.Equal(0.75, check.SettingsFor("DSC_0012.tif").Number("basic.exposure"));
        Assert.Equal(beforeA, HashFile(Path.Combine(folder, "DSC_0012.tif")));
        Assert.Equal(beforeB, HashFile(Path.Combine(folder, "DSC_0013.tif")));

        Assert.True(service.Stack.CanUndo);
        Assert.False(service.Stack.CanRedo);
        service.Stack.Undo();
        Assert.Null(service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Null(service.Project["DSC_0013.tif"].ForegroundMask);
        Assert.False(service.Stack.CanUndo);
        Assert.True(service.Stack.CanRedo);
        service.Stack.Redo();
        Assert.Equal(result.Items[0].Mask, service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Equal(result.Items[1].Mask, service.Project["DSC_0013.tif"].ForegroundMask);
    }

    [Fact]
    public async Task Apply_PreservesPriorMaskAndGroupsOnlyNewMasks()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var prior = FixedMask(9000);
        Assert.True(service.SetForegroundMask("DSC_0012.tif", prior));
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif"]);

        var result = await ForegroundBatch.RunAsync(jobs, (source, _) => MaskForSource(source, 5555),
            null, TestContext.Current.CancellationToken);
        var summary = ForegroundBatch.Apply(service, result);

        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Applied);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(ForegroundBatchStatus.Skipped, summary.Items[0].Status);
        Assert.Equal(ForegroundBatchStatus.Applied, summary.Items[1].Status);
        Assert.Equal(prior, service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Equal(result.Items[1].Mask, service.Project["DSC_0013.tif"].ForegroundMask);

        service.Stack.Undo();
        Assert.Equal(prior, service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Null(service.Project["DSC_0013.tif"].ForegroundMask);
        service.Stack.Undo();
        Assert.Null(service.Project["DSC_0012.tif"].ForegroundMask);
        service.Stack.Redo();
        service.Stack.Redo();
        Assert.Equal(prior, service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Equal(result.Items[1].Mask, Project.Open(folder, SamplePresets.PortraWarm)["DSC_0013.tif"].ForegroundMask);
    }

    [Fact]
    public async Task Apply_ReportsHonestCountsAndStillAppliesCancelledStagedWork()
    {
        var folder = NewPhotoFolder(3);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif", "DSC_0014.tif"]);

        var mixed = await ForegroundBatch.RunAsync(jobs, (source, _) =>
        {
            var name = Path.GetFileName(source.FilePath)!;
            if (name == "DSC_0012.tif")
                return MaskForSource(source, 6100);
            if (name == "DSC_0013.tif")
                return null;
            throw new InvalidOperationException("native failed");
        }, null, TestContext.Current.CancellationToken);
        var summary = ForegroundBatch.Apply(service, mixed);

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Applied);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(1, summary.NoSubject);
        Assert.Equal(1, summary.Failed);
        Assert.False(summary.Cancelled);
        Assert.DoesNotContain(summary.Items, item => item.Status == ForegroundBatchStatus.Generated);
        Assert.NotNull(service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Null(service.Project["DSC_0013.tif"].ForegroundMask);
        Assert.Null(service.Project["DSC_0014.tif"].ForegroundMask);
        Assert.Contains("native failed", summary.Items[2].Error ?? "");

        var cancelledResult = new ForegroundBatchResult(
        [
            new ForegroundBatchItem(jobs[0], ForegroundBatchStatus.Generated, service.Project["DSC_0012.tif"].ForegroundMask),
            new ForegroundBatchItem(jobs[1], ForegroundBatchStatus.Cancelled),
            new ForegroundBatchItem(jobs[2], ForegroundBatchStatus.Cancelled),
        ], Cancelled: true);
        service.Stack.Undo();
        Assert.All(service.Project.FileNames, f => Assert.Null(service.Project[f].ForegroundMask));
        var cancelledSummary = ForegroundBatch.Apply(service, cancelledResult);

        Assert.True(cancelledSummary.Cancelled);
        Assert.Equal(3, cancelledSummary.Total);
        Assert.Equal(1, cancelledSummary.Applied);
        Assert.Equal(ForegroundBatchStatus.Applied, cancelledSummary.Items[0].Status);
        Assert.Equal(ForegroundBatchStatus.Cancelled, cancelledSummary.Items[1].Status);
        Assert.Equal(ForegroundBatchStatus.Cancelled, cancelledSummary.Items[2].Status);
        Assert.NotNull(service.Project["DSC_0012.tif"].ForegroundMask);
        service.Stack.Undo();
        Assert.All(service.Project.FileNames, f => Assert.Null(service.Project[f].ForegroundMask));
    }

    [Fact]
    public async Task Apply_RejectsChangedEntryAndSettingsWithoutOverwriting()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif"]);
        var result = await ForegroundBatch.RunAsync(jobs, (source, _) => MaskForSource(source, 7777),
            null, TestContext.Current.CancellationToken);

        Assert.True(service.SetValue(["DSC_0012.tif"], "basic.exposure", 1.5));
        var summary = ForegroundBatch.Apply(service, result);

        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Applied);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(ForegroundBatchStatus.Failed, summary.Items[0].Status);
        Assert.NotNull(summary.Items[0].Error);
        Assert.Equal(ForegroundBatchStatus.Applied, summary.Items[1].Status);
        Assert.Null(service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.NotNull(service.Project["DSC_0013.tif"].ForegroundMask);
        Assert.Equal(1.5, service.SettingsFor("DSC_0012.tif").Number("basic.exposure"));

        service.Stack.Undo();
        Assert.Null(service.Project["DSC_0013.tif"].ForegroundMask);
        Assert.Equal(1.5, service.SettingsFor("DSC_0012.tif").Number("basic.exposure"));
        service.Stack.Undo();
        Assert.Equal(0.0, service.SettingsFor("DSC_0012.tif").Number("basic.exposure"));
    }

    [Fact]
    public async Task Apply_RefusesAlreadyMaskedChangeWithoutOverwriting()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var jobs = ForegroundBatch.BuildJobs(service, ["DSC_0012.tif", "DSC_0013.tif"]);
        var result = await ForegroundBatch.RunAsync(jobs, (source, _) => MaskForSource(source, 8888),
            null, TestContext.Current.CancellationToken);

        var intervening = FixedMask(111);
        Assert.True(service.SetForegroundMask("DSC_0012.tif", intervening));
        var summary = ForegroundBatch.Apply(service, result);

        Assert.Equal(1, summary.Applied);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(ForegroundBatchStatus.Skipped, summary.Items[0].Status);
        Assert.Equal(intervening, service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.NotEqual(intervening, service.Project["DSC_0013.tif"].ForegroundMask);
    }

    [Fact]
    public async Task Apply_RejectsWrongProjectOwnershipWithoutMutatingOtherProject()
    {
        var folderA = NewPhotoFolder(2);
        var folderB = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var serviceA = NewService(folderA, library);
        var serviceB = NewService(folderB, library);
        var jobs = ForegroundBatch.BuildJobs(serviceA, ["DSC_0012.tif", "DSC_0013.tif"]);
        var result = await ForegroundBatch.RunAsync(jobs, (source, _) => MaskForSource(source, 9999),
            null, TestContext.Current.CancellationToken);
        var changedB = 0;
        serviceB.Project.EntriesChanged += (_, _) => changedB++;

        var summary = ForegroundBatch.Apply(serviceB, result);

        Assert.Equal(2, summary.Total);
        Assert.Equal(0, summary.Applied);
        Assert.Equal(2, summary.Failed);
        Assert.All(summary.Items, item => Assert.Equal(ForegroundBatchStatus.Failed, item.Status));
        Assert.All(serviceB.Project.FileNames, f => Assert.Null(serviceB.Project[f].ForegroundMask));
        Assert.All(serviceA.Project.FileNames, f => Assert.Null(serviceA.Project[f].ForegroundMask));
        Assert.False(serviceB.Stack.CanUndo);
        Assert.Equal(0, changedB);
        Assert.All(serviceB.Project.FileNames, f => Assert.Null(Project.Open(folderB, SamplePresets.PortraWarm)[f].ForegroundMask));
    }

    [Fact]
    public async Task Apply_StaleRevisionThrowsWithoutApplyingOrConsumingUndo()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var stale = NewService(folder, library);
        var current = NewService(folder, library);
        var jobs = ForegroundBatch.BuildJobs(stale, ["DSC_0012.tif", "DSC_0013.tif"]);
        var result = await ForegroundBatch.RunAsync(jobs, (source, _) => MaskForSource(source, 4242),
            null, TestContext.Current.CancellationToken);
        current.SetValue(["DSC_0012.tif"], "basic.exposure", 0.5);

        Assert.Throws<IOException>(() => ForegroundBatch.Apply(stale, result));
        Assert.All(stale.Project.FileNames, f => Assert.Null(stale.Project[f].ForegroundMask));
        Assert.False(stale.Stack.CanUndo);
        var reopened = Project.Open(folder, SamplePresets.PortraWarm);
        Assert.All(reopened.FileNames, f => Assert.Null(reopened[f].ForegroundMask));
        using var checkLibrary = new PresetLibrary(SamplePresets.Create());
        var check = new ProjectService(reopened, checkLibrary, new CommandStack());
        Assert.Equal(0.5, check.SettingsFor("DSC_0012.tif").Number("basic.exposure"));
    }

    [Fact]
    public void SetForegroundMasks_SavesMultipleAtomicallyAsOneUndo()
    {
        var folder = NewPhotoFolder(2);
        using var library = new PresetLibrary(SamplePresets.Create());
        var service = NewService(folder, library);
        var beforeA = HashFile(Path.Combine(folder, "DSC_0012.tif"));
        var maskA = FixedMask(5001);
        var maskB = FixedMask(5002);
        var events = new List<IReadOnlyCollection<string>>();
        service.Project.EntriesChanged += (_, names) => events.Add(names);

        Assert.True(service.SetForegroundMasks(new Dictionary<string, ForegroundMask>(StringComparer.Ordinal)
        {
            ["DSC_0012.tif"] = maskA,
            ["DSC_0013.tif"] = maskB,
        }));

        Assert.Equal(maskA, service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Equal(maskB, service.Project["DSC_0013.tif"].ForegroundMask);
        Assert.Single(events);
        var reopened = Project.Open(folder, SamplePresets.PortraWarm);
        Assert.Equal(maskA, reopened["DSC_0012.tif"].ForegroundMask);
        Assert.Equal(maskB, reopened["DSC_0013.tif"].ForegroundMask);
        Assert.Equal(beforeA, HashFile(Path.Combine(folder, "DSC_0012.tif")));

        service.Stack.Undo();
        Assert.Null(service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Null(service.Project["DSC_0013.tif"].ForegroundMask);
        Assert.False(service.Stack.CanUndo);
        service.Stack.Redo();
        Assert.Equal(maskA, service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.Equal(maskB, service.Project["DSC_0013.tif"].ForegroundMask);

        Assert.False(service.SetForegroundMasks(new Dictionary<string, ForegroundMask>(StringComparer.Ordinal)
        {
            ["DSC_0012.tif"] = new ForegroundMask(maskA.Width, maskA.Height, maskA.ReferenceWidth, maskA.ReferenceHeight, maskA.Values),
            ["DSC_0013.tif"] = new ForegroundMask(maskB.Width, maskB.Height, maskB.ReferenceWidth, maskB.ReferenceHeight, maskB.Values),
        }));
        service.Stack.Undo();
        Assert.Null(service.Project["DSC_0012.tif"].ForegroundMask);
        Assert.False(service.Stack.CanUndo);
    }

    [Fact]
    public void SetForegroundMasks_FailedSaveDoesNotApplyOrConsumeUndo()
    {
        var folder = NewPhotoFolder(1);
        using var library = new PresetLibrary(SamplePresets.Create());
        var stale = NewService(folder, library);
        var current = NewService(folder, library);
        current.SetValue(["DSC_0012.tif"], "basic.exposure", 0.25);

        Assert.Throws<IOException>(() => stale.SetForegroundMasks(new Dictionary<string, ForegroundMask>(StringComparer.Ordinal)
        {
            ["DSC_0012.tif"] = FixedMask(6001),
        }));
        Assert.Null(stale.Project["DSC_0012.tif"].ForegroundMask);
        Assert.False(stale.Stack.CanUndo);
        Assert.Null(Project.Open(folder, SamplePresets.PortraWarm)["DSC_0012.tif"].ForegroundMask);
    }
}
