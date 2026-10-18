using Avalonia;
using Avalonia.Headless.XUnit;
using FilmBatch.App.Controls.Tools;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Rendering;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.App.Tests;

/// <summary>裁切工具（cut-and-mask 計畫 Task 1.7）：拖曳、固定比例、套用 / 取消、離開分頁自動套用、復原、原圖對比。</summary>
public sealed class CropToolTests
{
    private static (CropToolViewModel Vm, List<Settings> Commits) Create()
    {
        var commits = new List<Settings>();
        var vm = new CropToolViewModel(commits.Add, () => (400, 300));
        vm.Load(ModuleCatalog.Defaults);
        return (vm, commits);
    }

    [Fact]
    public void FixedAspect_ResizeKeepsRatio()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.Aspect = "1x1";
        vm.DragHandle(CropHandle.Right, new Point(-0.2, 0), fromCenter: false);
        var px = vm.Working.W * 400 / (vm.Working.H * 300);
        Assert.Equal(1.0, px, 3);
    }

    [Fact]
    public void FixedAspect_CornerDragKeepsRatio_AndStaysInside()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.Aspect = "3x2";
        vm.DragHandle(CropHandle.TopLeft, new Point(0.3, 0.1), fromCenter: false);
        Assert.Equal(1.5, vm.Working.W * 400 / (vm.Working.H * 300), 3);
        vm.DragHandle(CropHandle.BottomRight, new Point(0.8, 0.8), fromCenter: false);
        Assert.Equal(1.5, vm.Working.W * 400 / (vm.Working.H * 300), 3);
        Assert.True(CropGeometry.FitsInside(400, 300, vm.Working with { On = true }));
    }

    [Fact]
    public void DragHandle_ClampsDraggedEdge_WithoutMovingTheBox()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.DragHandle(CropHandle.Right, new Point(-0.5, 0), false);   // 右邊拉到 0.5
        vm.DragHandle(CropHandle.Left, new Point(-0.3, 0), false);    // 左邊已在 0，不能再往外
        Assert.Equal(0.25, vm.Working.Cx, 3);
        Assert.Equal(0.5, vm.Working.W, 3);
    }

    /// <summary>縮成左上角 0.5×0.5 的框（貼著上、左邊）。</summary>
    private static CropToolViewModel CornerBox()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.DragHandle(CropHandle.Right, new Point(-0.5, 0), false);
        vm.DragHandle(CropHandle.Bottom, new Point(0, -0.5), false);
        return vm;
    }

    [Fact]
    public void DragMove_AgainstEdge_SlidesAlongIt_WithoutPullingTheOtherAxis()
    {
        var vm = CornerBox();
        var tool = vm.CanvasTool;
        var map = new CanvasMapping(new Rect(0, 0, 400, 300));
        tool.OnPressed(new Point(100, 75), map, default);              // 框中心 → 移動
        foreach (var x in new[] { 300.0, 320, 340, 360 })              // 右邊早已頂到，持續往右推
        {
            tool.OnMoved(new Point(x, 105), map, default);              // 往下 0.1
            Assert.Equal(0.75, vm.Working.Cx, 6);
            Assert.Equal(0.35, vm.Working.Cy, 6);                       // y 不應被拉向中心而抖動
        }
    }

    [Fact]
    public void CornerDrag_PastOneEdge_KeepsFollowingOnTheOtherAxis()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.DragHandle(CropHandle.Right, new Point(-0.5, 0), false);
        vm.DragHandle(CropHandle.Bottom, new Point(0, -0.5), false);     // 左上角 0.5×0.5
        var start = vm.Working;
        foreach (var dx in new[] { 0.5, 0.6, 0.7, 0.55 })                // 右邊早已頂到，往右多推多少都一樣
        {
            vm.Restore(start);
            vm.DragHandle(CropHandle.BottomRight, new Point(dx, 0.1), false);
            Assert.Equal(1.0, vm.Working.W, 6);
            Assert.Equal(0.6, vm.Working.H, 6);                          // 下邊不可隨 x 推多遠而來回跳
        }
    }

    [Fact]
    public void Straighten_PointerBackToPress_ReturnsToStartAngle()
    {
        var vm = CornerBox();
        var tool = vm.CanvasTool;
        var map = new CanvasMapping(new Rect(0, 0, 400, 300));
        tool.OnPressed(new Point(350, 250), map, default);             // 框外 → 拉直（框會被推回影像內而移動中心）
        tool.OnMoved(new Point(250, 290), map, default);
        tool.OnMoved(new Point(120, 290), map, default);
        tool.OnMoved(new Point(350, 250), map, default);
        Assert.Equal(0, vm.Working.AngleDeg, 6);
    }

    [Fact]
    public void Cancel_RestoresOriginal_AndCommitsNothing()
    {
        var (vm, commits) = Create();
        vm.BeginCommand.Execute(null);
        vm.DragMove(new Point(0.1, 0));
        vm.CancelCommand.Execute(null);
        Assert.Empty(commits);
        Assert.Equal(0.5, vm.Working.Cx, 6);
    }

    [Fact]
    public void Apply_CommitsSingleSettingsWithEnabled()
    {
        var (vm, commits) = Create();
        vm.BeginCommand.Execute(null);
        vm.DragHandle(CropHandle.Left, new Point(0.25, 0), false);
        vm.ApplyCommand.Execute(null);
        var s = Assert.Single(commits);
        Assert.True(s.Bool("crop.enabled"));
        Assert.Equal(0.75, s.Number("crop.rect_w"), 3);
        Assert.False(vm.IsActive);
    }

    [Fact]
    public void Apply_WithoutChanges_CommitsNothing()
    {
        var (vm, commits) = Create();
        vm.BeginCommand.Execute(null);
        vm.ApplyCommand.Execute(null);
        Assert.Empty(commits);
    }

    [Fact]
    public void Rotate_ShrinksToStayInside()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.DragRotate(10);
        Assert.True(CropGeometry.FitsInside(400, 300, vm.Working with { On = true }));
        Assert.Equal(10, vm.Working.AngleDeg, 6);
    }

    [Fact]
    public void Straighten_BackToZero_RestoresTheBox()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.Angle = 20;
        Assert.True(vm.Working.W < 1);
        vm.Angle = 0;
        Assert.Equal(1, vm.Working.W, 6);
        Assert.Equal(1, vm.Working.H, 6);
    }

    [Fact]
    public void RotateRight_TurnsTheBoxWithTheImage()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.DragHandle(CropHandle.Left, new Point(0.5, 0), false);      // 右半邊：cx 0.75、w 0.5
        vm.RotateRightCommand.Execute(null);
        Assert.Equal(1, vm.Working.Quarter);
        // 順時針轉 90°：右半邊變成下半邊
        Assert.Equal(0.5, vm.Working.Cx, 6);
        Assert.Equal(0.75, vm.Working.Cy, 6);
        Assert.Equal(1, vm.Working.W, 6);
        Assert.Equal(0.5, vm.Working.H, 6);
        vm.RotateLeftCommand.Execute(null);
        Assert.Equal(0, vm.Working.Quarter);
        Assert.Equal(0.75, vm.Working.Cx, 6);
    }

    [Fact]
    public void FlipHorizontal_MirrorsBoxAndAngle()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.DragHandle(CropHandle.Left, new Point(0.5, 0), false);
        vm.DragRotate(3);
        var before = vm.Working;
        vm.FlipHorizontalCommand.Execute(null);
        Assert.True(vm.Working.FlipH);
        Assert.Equal(1 - before.Cx, vm.Working.Cx, 6);
        Assert.Equal(-before.AngleDeg, vm.Working.AngleDeg, 6);
    }

    [Fact]
    public void SwapOrientation_FlipsFixedRatio()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.Aspect = "3x2";
        vm.SwapOrientationCommand.Execute(null);
        Assert.True(vm.Portrait);
        Assert.Equal(2.0 / 3, vm.Working.W * 400 / (vm.Working.H * 300), 3);
    }

    [Fact]
    public void CanvasTool_HitTestsHandlesInsideAndOutside()
    {
        var (vm, _) = Create();
        vm.BeginCommand.Execute(null);
        vm.DragHandle(CropHandle.Left, new Point(0.25, 0), false);   // 框：x 0.25–1
        var tool = (Controls.Tools.CropTool)vm.CanvasTool;
        var map = new Controls.Tools.CanvasMapping(new Rect(10, 20, 400, 300));
        Assert.Equal(CropHandle.TopLeft, tool.HitTest(new Point(110, 20), map));
        Assert.Equal(CropHandle.Left, tool.HitTest(new Point(111, 170), map));
        Assert.Equal(CropHandle.Move, tool.HitTest(new Point(300, 170), map));
        Assert.Equal(CropHandle.Rotate, tool.HitTest(new Point(50, 170), map));
        Assert.Equal(new Point(0.5, 0.5), map.ToImage(map.ToCanvas(new Point(0.5, 0.5))));
    }

    [Fact]
    public void Straightened_ImageRotates_FrameStaysLevel()
    {
        var vm = CornerBox();
        vm.Angle = 10;
        var tool = vm.CanvasTool;
        Assert.Equal(-10, tool.ViewRotationDeg, 6);
        var map = new Controls.Tools.CanvasMapping(new Rect(10, 20, 400, 300), tool.ViewRotationDeg);
        var c = vm.Corners().Select(map.ToCanvas).ToArray();
        Assert.Equal(c[0].Y, c[1].Y, 6);                                  // 上邊水平
        Assert.Equal(c[0].X, c[3].X, 6);                                  // 左邊垂直
        var p = new Point(0.3, 0.7);
        var back = map.ToImage(map.ToCanvas(p));
        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Y, back.Y, 9);
    }

    [AvaloniaFact]
    public async Task LeavingCropTab_AppliesPendingCrop()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        main.Sidebar.Active = SidePanel.Crop;
        Assert.True(main.CropTool.IsActive);
        main.CropTool.DragHandle(CropHandle.Left, new Point(0.25, 0), false);
        main.Sidebar.Active = SidePanel.Inspector;
        Assert.False(main.CropTool.IsActive);
        Assert.True(main.FocusedResolved.Bool("crop.enabled"));
        Assert.Equal(0.75, main.FocusedResolved.Number("crop.rect_w"), 3);

        // 一次復原就還原整個裁切
        main.UndoCommand.Execute(null);
        Assert.False(main.FocusedResolved.Bool("crop.enabled"));
    }

    [AvaloniaFact]
    public async Task ToggleCrop_EntersAndReturnsToPreviousTab()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        main.Sidebar.Active = SidePanel.FilmEffects;
        main.ToggleCropCommand.Execute(null);
        Assert.Equal(SidePanel.Crop, main.Sidebar.Active);
        Assert.True(main.CropTool.IsActive);
        Assert.Same(main.CropTool.CanvasTool, main.Preview.ActiveTool);

        main.CropTool.DragHandle(CropHandle.Top, new Point(0, 0.2), false);
        main.ToggleCropCommand.Execute(null);
        Assert.Equal(SidePanel.FilmEffects, main.Sidebar.Active);
        Assert.Null(main.Preview.ActiveTool);
        Assert.Equal(0.8, main.FocusedResolved.Number("crop.rect_h"), 3);
    }

    [AvaloniaFact]
    public async Task ActiveTool_PreviewsFullFrame_ThenCroppedAfterApply()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var source = main.Preview.Source!;
        main.Sidebar.Active = SidePanel.Crop;
        main.CropTool.DragHandle(CropHandle.Left, new Point(0.5, 0), false);
        main.CropTool.ApplyCommand.Execute(null);
        var croppedW = (int)Math.Round(source.Width * 0.5);
        TestEnv.PumpUntil(() => !main.Preview.IsBusy && main.Preview.Image!.PixelSize.Width == croppedW);

        // 再進入裁切工具：預覽暫停裁切，顯示全幅
        main.CropTool.BeginCommand.Execute(null);
        TestEnv.PumpUntil(() => !main.Preview.IsBusy && main.Preview.Image!.PixelSize.Width == source.Width);
        Assert.Equal(source.Width, main.Preview.Original!.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task Compare_OriginalIsCroppedLikeTheResult()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var source = main.Preview.Source!;
        main.Service!.SetValues([main.FocusFile!], Settings.Empty.With("crop.enabled", true).With("crop.rect_w", 0.5)
            .With("crop.rect_h", 0.6).With("crop.angle", 4.0).With("crop.quarter", 1.0), "crop");
        TestEnv.PumpUntil(() => !main.Preview.IsBusy && main.Preview.Image!.PixelSize.Width != source.Width);

        var image = main.Preview.Image!.PixelSize;
        var original = main.Preview.Original!.PixelSize;
        Assert.Equal(image, original);
        // 轉 90° 後的定向影像寬 = 來源高
        Assert.Equal((int)Math.Round(source.Height * 0.5), original.Width);
    }

    [AvaloniaFact]
    public async Task RawOptionChange_PreservesPendingCropUntilApplyAndUndo()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(FilmBatch.Core.AppPaths.Root, "raw-crop");
        Directory.CreateDirectory(folder);
        var samples = new float[64 * 48 * 3];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = 0.02f + (i % 193) / 192f;
        File.WriteAllBytes(Path.Combine(folder, "scene.dng"),
            DngWriter.WriteRenderedDng(64, 48, samples, ColorSpaces.Srgb,
                new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "RAW crop preview" }));
        var opening = main.OpenFolderAsync(folder);
        TestEnv.PumpUntil(() => opening.IsCompleted);
        await opening;
        TestEnv.PumpUntil(() => main.Preview.Source is not null && main.Preview.Image is not null);
        var source = main.Preview.Source!;
        main.Sidebar.Active = SidePanel.Crop;
        main.CropTool.RotateRightCommand.Execute(null);
        main.CropTool.DragHandle(CropHandle.Left, new Point(0.25, 0), false);
        var working = main.CropTool.Working;
        Assert.Equal(0.75, working.W, 6);

        main.Inspector.SetValue("raw.enabled", true, null);
        main.Inspector.SetValue("raw.camera_local_tone", true, null);
        var rawOptions = FilmBatch.Engine.Imaging.RawLoadOptions.FromSettings(main.FocusedResolved);
        TestEnv.PumpUntil(() => main.Preview.Source is { } current
            && !ReferenceEquals(source, current) && current.RawOptions == rawOptions
            && !main.Preview.IsBusy && !main.Renderer.IsBusy);

        Assert.True(main.CropTool.IsActive);
        Assert.Equal(working, main.CropTool.Working);
        Assert.False(main.FocusedResolved.Bool("crop.enabled")); // 未套用的拖曳不可因 RAW 重載而寫入照片。
        Assert.Equal(new PixelSize(48, 64), main.Preview.Image!.PixelSize);
        Assert.Equal(new PixelSize(48, 64), main.Preview.Original!.PixelSize);
        main.Sidebar.Active = SidePanel.Inspector;
        TestEnv.PumpUntil(() => !main.Preview.IsBusy && main.Preview.Image?.PixelSize == new PixelSize(36, 64));

        // 真正變更裁切的復原仍須重載工具，不能把所有 EntriesChanged 都忽略。
        main.Sidebar.Active = SidePanel.Crop;
        main.CropTool.DragHandle(CropHandle.Left, new Point(0.1, 0), false);
        Assert.True(main.CropTool.HasChanges);
        main.UndoCommand.Execute(null);
        TestEnv.PumpUntil(() => !main.Preview.IsBusy && main.Preview.Image?.PixelSize == new PixelSize(64, 48));
        Assert.True(main.CropTool.IsActive);
        Assert.False(main.CropTool.HasChanges);
        Assert.Equal(1.0, main.CropTool.Working.W);
        Assert.Equal(0, main.CropTool.Working.Quarter);
    }
}
