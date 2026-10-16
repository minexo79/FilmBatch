using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using FilmBatch.App.ViewModels;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Tests;

/// <summary>預設懸停預覽：焦點照片 ⊕ 懸停預設的實際引擎算圖，不改照片設定、無假圖、有界快取。</summary>
public class PresetHoverPreviewTests
{
    private static PresetItemViewModel FirstPreset(MainWindowViewModel vm) =>
        vm.Presets.Categories.SelectMany(c => c.Presets).First();

    private static PresetItemViewModel PresetByName(MainWindowViewModel vm, string fragment) =>
        vm.Presets.Categories.SelectMany(c => c.Presets).First(p => p.Name.Contains(fragment));

    [AvaloniaFact]
    public async Task Hover_RendersActualEffect_WithoutAlteringPhotoSettings()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var file = vm.FocusFile!;
        var before = vm.Service!.Project[file];
        var item = PresetByName(vm, "Vivid");

        using var renderer = new CpuFakeRenderer();
        Assert.True(vm.Presets.Hover.TryRenderNow(item, renderer));
        Assert.NotNull(item.HoverPreview);

        // 照片自己的設定完全沒動：快照比對
        Assert.Equal(before, vm.Service.Project[file]);
        // 與直接用同管線算出的結果逐位元組一致（不是假圖）
        var expected = renderer.Render(
            vm.Preview.Source!.Downscale(320),
            FilmBatch.Core.Projects.PhotoOperations.Resolve(
                vm.Service.BaseFor(item.Id), before.Overrides));
        var actual = ReadBgra(item.HoverPreview!);
        Assert.Equal(expected.Width, item.HoverPreview!.PixelSize.Width);
        Assert.Equal(expected.Bgra, actual);
    }

    [AvaloniaFact]
    public async Task Hover_WithoutFocusPhoto_RendersNothing_TextOnly()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var item = FirstPreset(vm);

        // 沒有開啟專案／沒有焦點照片：BeginHover 不送算圖，提示維持純文字
        vm.OnSelectionChanged([], null);
        Assert.Null(vm.FocusFile);
        PresetHoverCoordinator.HoverDelay = TimeSpan.FromMilliseconds(1);
        try
        {
            vm.Presets.Hover.BeginHover(item);
            TestEnv.PumpUntil(() => !item.HoverLoading, 2000);
        }
        finally
        {
            PresetHoverCoordinator.HoverDelay = TimeSpan.FromMilliseconds(350);
        }
        Assert.Null(item.HoverPreview);
        Assert.False(string.IsNullOrWhiteSpace(item.Summary));
    }

    [AvaloniaFact]
    public async Task Hover_StaleResult_AfterFocusSwitch_IsDiscarded()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.Filmstrip.Items.Count >= 2);
        var item = FirstPreset(vm);
        var first = vm.FocusFile!;
        var second = vm.Filmstrip.Items[1].FileName;

        // 先算好第一張焦點的縮圖
        using var renderer = new CpuFakeRenderer();
        Assert.True(vm.Presets.Hover.TryRenderNow(item, renderer));
        Assert.NotNull(item.HoverPreview);
        var stale = renderer.Render(
            vm.Preview.Source!.Downscale(320),
            FilmBatch.Core.Projects.PhotoOperations.Resolve(
                vm.Service!.BaseFor(item.Id), vm.Service.Project[second].Overrides));

        // 切換焦點 → 隱藏舊縮圖（設定變更才丟快取；此處只是焦點切換，快取保留可回命中）
        vm.Filmstrip.SelectOnly(second);
        vm.Presets.Hover.OnFocusChanged();
        Assert.Null(item.HoverPreview);

        // 之後才送達的舊焦點結果必須丟棄，不顯示
        vm.Presets.Hover.OnThumbnailReady(PresetHoverCoordinator.KeyFor(item.Id, first), stale);
        Assert.Null(item.HoverPreview);
    }

    [AvaloniaFact]
    public async Task Hover_SettingsChange_DropsCache_AndPhotoUnchanged()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var item = FirstPreset(vm);
        var file = vm.FocusFile!;
        using var renderer = new CpuFakeRenderer();
        Assert.True(vm.Presets.Hover.TryRenderNow(item, renderer));
        Assert.NotNull(item.HoverPreview);
        Assert.Equal(1, vm.Presets.Hover.CacheCount);

        // 照片設定變更 → 該照片快取丟棄、縮圖隱藏（舊圖不可再用）
        vm.Inspector.SetValue("basic.exposure", 0.5, null);
        vm.Presets.Hover.InvalidateForPhoto(file);
        Assert.Null(item.HoverPreview);
        Assert.Equal(0, vm.Presets.Hover.CacheCount);

        // 復原，設定回到原值（測試只驗失效語義，不驗復原後的重算）
        vm.UndoCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task Hover_DebouncedQueue_RendersThroughSharedThumbnailPipeline()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var item = FirstPreset(vm);
        var file = vm.FocusFile!;
        var key = PresetHoverCoordinator.KeyFor(item.Id, file);

        try
        {
            var oldDelay = PresetHoverCoordinator.HoverDelay;
            PresetHoverCoordinator.HoverDelay = TimeSpan.FromMilliseconds(1);
            try
            {
                vm.Presets.Hover.BeginHover(item);
                // 防抖 → 低優先佇列 → 實際引擎算圖 → ThumbnailReady 路由回 UI
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (item.HoverPreview is null && DateTime.UtcNow < deadline)
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                Assert.NotNull(item.HoverPreview);
                Assert.True(item.HasHoverPreview);
                Assert.False(item.HoverLoading);
            }
            finally
            {
                PresetHoverCoordinator.HoverDelay = oldDelay;
            }
        }
        finally
        {
            vm.Presets.Hover.EndHover();
        }
    }

    [AvaloniaFact]
    public async Task Hover_NonHoverKeys_AreIgnored_CacheIsBounded()
    {
        using var vm = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(vm).WaitAsync(TestContext.Current.CancellationToken);
        var file = vm.FocusFile!;
        using var renderer = new CpuFakeRenderer();
        // 縮圖列的檔名鍵直接通過協調器會被忽略（由 Filmstrip 處理，不在此顯示）
        var before = vm.Presets.Hover.CacheCount;
        vm.Presets.Hover.OnThumbnailReady(file, renderer.Render(
            vm.Preview.Source!.Downscale(160), vm.Service!.SettingsFor(file)));
        Assert.Equal(before, vm.Presets.Hover.CacheCount);
        Assert.All(vm.Presets.Categories.SelectMany(c => c.Presets), p => Assert.Null(p.HoverPreview));

        // 快取有界：反覆算不同預設也不會無限成長
        foreach (var item in vm.Presets.Categories.SelectMany(c => c.Presets))
            vm.Presets.Hover.TryRenderNow(item, renderer);
        Assert.True(vm.Presets.Hover.CacheCount <= PresetHoverCoordinator.MaxCacheEntries);

        // 失效鉤子：清掉該預設的快取並隱藏縮圖
        var target = FirstPreset(vm);
        Assert.True(vm.Presets.Hover.TryRenderNow(target, renderer));
        Assert.NotNull(target.HoverPreview);
        vm.Presets.Hover.InvalidateForPreset(target.Id);
        Assert.Null(target.HoverPreview);
    }

    private static byte[] ReadBgra(Bitmap bitmap)
    {
        var writeable = (WriteableBitmap)bitmap;
        using var fb = writeable.Lock();
        var bytes = new byte[fb.Size.Width * fb.Size.Height * 4];
        unsafe
        {
            var src = (byte*)fb.Address;
            for (var y = 0; y < fb.Size.Height; y++)
            {
                var row = new Span<byte>(src + y * fb.RowBytes, fb.Size.Width * 4);
                row.CopyTo(bytes.AsSpan(y * fb.Size.Width * 4));
            }
        }
        return bytes;
    }
}
