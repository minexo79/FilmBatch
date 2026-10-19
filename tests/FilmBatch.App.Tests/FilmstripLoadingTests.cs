using Avalonia.Headless.XUnit;
using FilmBatch.App.Services;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Rendering;
using System.Reflection;
using Avalonia.Threading;

namespace FilmBatch.App.Tests;

/// <summary>
/// 縮圖有界幫浦的對外行為：完成計數、失敗不中斷、取消後不再自動續載、可見範圍優先。
/// 不觀察內部佇列；以公開屬性（TotalThumbnails／CompletedThumbnails／FailedThumbnails／
/// IsLoadingThumbnails）與 ThumbnailReady 的送達順序驗證。
/// </summary>
public sealed class FilmstripLoadingTests
{
    private static string GenerateFolder(string tag, int count)
    {
        var folder = Path.Combine(AppPaths.Root, $"filmstrip-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var samples = SampleImages.Generate(folder, count: count, longEdge: 64);
        for (var i = samples.Count; i < count; i++)
            File.Copy(samples[i % samples.Count], Path.Combine(folder, $"extra-{i:D4}.tif"));
        return folder;
    }

    [AvaloniaFact]
    public async Task Load_EventuallyCompletesEveryPhoto_Once()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = GenerateFolder("all", 12);
        await vm.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails);

        Assert.Equal(12, vm.Filmstrip.TotalThumbnails);
        Assert.Equal(12, vm.Filmstrip.CompletedThumbnails);
        Assert.Equal(0, vm.Filmstrip.FailedThumbnails);
        Assert.All(vm.Filmstrip.Items, t => Assert.NotNull(t.Bitmap));
        Assert.True(vm.Filmstrip.LoadingElapsed > TimeSpan.Zero);
    }

    [AvaloniaFact]
    public async Task CorruptPhoto_FailsIndividually_AndLaterRowsStillLoad()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = GenerateFolder("corrupt", 6);
        var corrupt = Path.Combine(folder, "broken.jpg");
        File.WriteAllText(corrupt, "not a real image");
        await vm.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails);

        Assert.Equal(7, vm.Filmstrip.TotalThumbnails);
        Assert.Equal(7, vm.Filmstrip.CompletedThumbnails); // 完成含失敗終態
        Assert.Equal(1, vm.Filmstrip.FailedThumbnails);
        var broken = vm.Filmstrip.Items.Single(t => t.FileName == "broken.jpg");
        Assert.True(broken.HasError);
        Assert.Equal(6, vm.Filmstrip.Items.Count(t => t.Bitmap is not null));
    }

    [AvaloniaFact]
    public async Task Cancel_StopsScan_KeepsCounts_AndExplicitEditStillUpdates()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = GenerateFolder("cancel", 30);
        await vm.OpenFolderAsync(folder);
        vm.Filmstrip.CancelLoading();

        Assert.False(vm.Filmstrip.IsLoadingThumbnails);
        var completed = vm.Filmstrip.CompletedThumbnails;
        var total = vm.Filmstrip.TotalThumbnails;
        Assert.True(completed < total, $"expected incomplete scan, got {completed}/{total}");

        // 明確編輯仍可更新單張，不自動恢復未完成的掃描。
        var target = vm.Filmstrip.Items.Last(t => t.Bitmap is null && !t.HasError);
        vm.Filmstrip.RequestThumbnail(target.FileName);
        TestEnv.PumpUntil(() => target.Bitmap is not null || target.HasError, timeoutMs: 30000);
        Assert.False(vm.Filmstrip.IsLoadingThumbnails);
        Assert.Equal(total, vm.Filmstrip.TotalThumbnails);
        Assert.True(vm.Filmstrip.Items.Any(t => t.Bitmap is null && !t.HasError),
            "cancel must not resume the folder-order scan");
    }

    [AvaloniaFact]
    public async Task RawOptionEdit_ReloadsAlreadyCompletedThumbnail()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "filmstrip-raw-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        const string file = "photo.dng";
        File.WriteAllBytes(Path.Combine(folder, file), DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb, new FilmBatch.Raw.Meta.Metadata()));
        await vm.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails);
        var photo = Assert.Single(vm.Filmstrip.Items);
        var original = photo.Source;
        vm.Inspector.SetValue("raw.enabled", true, null);
        var expected = RawLoadOptions.FromSettings(vm.FocusedResolved);
        TestEnv.PumpUntil(() => photo.Source is { } source && source.RawOptions == expected
            && photo.Bitmap is not null && !vm.Renderer.IsBusy);
        Assert.NotSame(original, photo.Source);
        Assert.Equal(1, vm.Filmstrip.CompletedThumbnails);
        Assert.Equal(0, vm.Filmstrip.FailedThumbnails);
    }

    [AvaloniaFact]
    public async Task VisibleRange_FinishesAheadOfFolderOrder()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = GenerateFolder("visible", 30);
        var delivered = new List<string>();
        vm.Renderer.ThumbnailReady += (key, _) => delivered.Add(key);

        await vm.OpenFolderAsync(folder);
        var files = vm.Filmstrip.Items.Select(t => t.FileName).ToList();
        var fileSet = files.ToHashSet();
        vm.Filmstrip.SetVisibleRange(10, 12);
        TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails, timeoutMs: 60000);

        var photoDeliveries = delivered.Where(fileSet.Contains).ToList();
        // 焦點（第一張）先送達；接著是可見範圍的三張，早於資料夾順序的其他照片。
        Assert.Equal(files[0], photoDeliveries[0]);
        var visible = files.Skip(10).Take(3).ToHashSet();
        var early = photoDeliveries.Skip(1).Take(3).ToHashSet();
        Assert.True(visible.SetEquals(early),
            $"expected visible range first, got {string.Join(",", photoDeliveries.Take(4))}");
        Assert.Equal(30, vm.Filmstrip.CompletedThumbnails);
    }

    [AvaloniaFact]
    public async Task ForeignKeys_DoNotAffectProgress()
    {
        using var vm = TestEnv.CreateViewModel();
        var folder = GenerateFolder("foreign", 4);
        await vm.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails);

        vm.Filmstrip.SetError("film-look/film/x", "stale failure");
        vm.Filmstrip.SetError("hover-preview/x", "stale failure");
        Assert.Equal(0, vm.Filmstrip.FailedThumbnails);
        Assert.Equal(4, vm.Filmstrip.CompletedThumbnails);
        Assert.All(vm.Filmstrip.Items, t => Assert.False(t.HasError));
    }

    [AvaloniaFact]
    public async Task FailedRefresh_PreservesBitmap_AndNextPhotoStillUpdates()
    {
        using var vm = TestEnv.CreateViewModel();
        await vm.OpenFolderAsync(GenerateFolder("refresh-failure", 2));
        TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails);
        var broken = vm.Filmstrip.Items[1];
        var next = vm.Filmstrip.Items[0];
        var previousBroken = broken.Bitmap;
        var previousNext = next.Bitmap;
        var failed = false;
        vm.Renderer.Failed += failure => { if (failure.Key == broken.FileName) failed = true; };
        vm.Service!.SetValues([broken.FileName], Settings.Empty
            .With("double_exposure.enabled", true).With("double_exposure.source", "file")
            .With("double_exposure.source_path", Path.Combine(vm.Folder!, "missing.tif")), "Missing asset");
        TestEnv.PumpUntil(() => failed);
        vm.Service.SetValue([next.FileName], "basic.exposure", 1.0);
        TestEnv.PumpUntil(() => !ReferenceEquals(previousNext, next.Bitmap));
        Assert.Same(previousBroken, broken.Bitmap);
        Assert.Equal(2, vm.Filmstrip.CompletedThumbnails);
        Assert.Equal(0, vm.Filmstrip.FailedThumbnails);
    }

    [AvaloniaFact]
    public async Task PreviewSizeFailure_DoesNotPreventSmallerThumbnail()
    {
        TestEnv.IsolateUserFolder();
        using var vm = new ViewModels.MainWindowViewModel(new AppSettings(), new FakeDialogs(),
            () => new ThumbnailSizeRenderer(), action => Dispatcher.UIThread.Post(action));
        var folder = Path.Combine(AppPaths.Root, "preview-size-failure");
        SampleImages.Generate(folder, count: 3, longEdge: 600);
        var gate = (SemaphoreSlim)typeof(ImageCache).GetField("_backgroundGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm.Images)!;
        gate.Wait(TestContext.Current.CancellationToken);
        var held = true;
        var failures = new HashSet<string>();
        vm.Renderer.Failed += failure => failures.Add(failure.Key);
        try
        {
            await vm.OpenFolderAsync(folder);
            var target = vm.Filmstrip.Items.Last();
            vm.Filmstrip.SelectOnly(target.FileName);
            TestEnv.PumpUntil(() => failures.Contains(target.FileName));
            gate.Release();
            held = false;
            TestEnv.PumpUntil(() => !vm.Filmstrip.IsLoadingThumbnails);
            Assert.All(vm.Filmstrip.Items, photo => Assert.NotNull(photo.Bitmap));
            Assert.Equal(0, vm.Filmstrip.FailedThumbnails);
        }
        finally { if (held) gate.Release(); }
    }

    // Simulate a device's size boundary; supported thumbnail pixels use the real CPU pipeline.
    private sealed class ThumbnailSizeRenderer : IPreviewRenderer
    {
        private readonly CpuFakeRenderer _inner = new();
        public string DeviceName => _inner.DeviceName;
        public bool IsSoftware => _inner.IsSoftware;
        public RenderResult Render(SourceImage source, Settings settings, CancellationToken cancellationToken = default) =>
            Math.Max(source.Width, source.Height) > 200
                ? throw new InvalidOperationException("Preview exceeds device size")
                : _inner.Render(source, settings, cancellationToken);
        public SourceImage Render16(SourceImage source, Settings settings, CancellationToken token = default) =>
            _inner.Render16(source, settings, token);
        public void Dispose() => _inner.Dispose();
    }
}
