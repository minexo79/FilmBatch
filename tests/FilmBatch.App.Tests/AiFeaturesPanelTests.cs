using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;
using FilmBatch.App.Localization;
using FilmBatch.App.ViewModels;
using FilmBatch.App.ViewModels.Tools;
using FilmBatch.App.Views;
using FilmBatch.Core.Params;
using FilmBatch.Core.Projects;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.Tests;

/// <summary>基本參數的「AI 功能」把降噪與去背收成同一塊，收合只取消單張草稿。</summary>
public sealed class AiFeaturesPanelTests
{
    [AvaloniaFact]
    public void Denoise_StaysInModulesButLeavesTheBasicGroup()
    {
        using var main = TestEnv.CreateViewModel();
        Assert.True(main.Inspector.IsAiFeaturesExpanded);
        Assert.Equal("denoise", Assert.Single(main.Inspector.AiModules).Key);
        Assert.Contains(main.Inspector.Modules, module => module.Key == "denoise");
        Assert.DoesNotContain(main.Inspector.Groups.SelectMany(group => group.Modules), module => module.Key == "denoise");
        Assert.Contains(main.Inspector.Groups.SelectMany(group => group.Modules), module => module.Key == "basic");
        Assert.Empty(main.FilmEffects.AiModules);
        Assert.Equal([ModuleGroup.Basic, ModuleGroup.Color], main.Inspector.LeadingGroups.Select(group => group.Group));
        Assert.Equal(main.Inspector.Groups.Skip(2), main.Inspector.TrailingGroups);
        Assert.Contains(main.Inspector.TrailingGroups, group => group.Group == ModuleGroup.Optical);
        // 底片效果分頁沒有基本分組：全部分組照原順序排在後段，外觀不變。
        Assert.Empty(main.FilmEffects.LeadingGroups);
        Assert.Equal(main.FilmEffects.Groups, main.FilmEffects.TrailingGroups);
    }

    [AvaloniaFact]
    public async Task CollapsingFilmEffects_DoesNotCancelAnOpenDraft()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        main.BackgroundRemoval.BeginEditCommand.Execute(null);
        Assert.True(main.BackgroundRemoval.IsEditing);
        main.FilmEffects.IsAiFeaturesExpanded = false;
        Assert.True(main.BackgroundRemoval.IsEditing);
        Assert.True(main.Inspector.IsAiFeaturesExpanded);
    }

    [AvaloniaFact]
    public async Task Collapsing_CancelsInFlightSingleSelection()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var pending = new TaskCompletionSource<ForegroundMask?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        main.BackgroundRemoval.Dispose();
        main.BackgroundRemoval = new BackgroundRemovalViewModel(main, (_, cancellation) =>
        {
            token = cancellation;
            return pending.Task;
        });
        var work = main.BackgroundRemoval.GenerateCommand.ExecuteAsync(null);
        Assert.True(main.BackgroundRemoval.IsBusy);

        main.Inspector.IsAiFeaturesExpanded = false;

        Assert.True(token.IsCancellationRequested);
        Assert.False(main.BackgroundRemoval.IsBusy);
        Assert.False(main.BackgroundRemoval.IsEditing);
        pending.TrySetResult(null);
        TestEnv.PumpUntil(() => work.IsCompleted);
        await work;
        Assert.Null(main.Service!.Project[main.FocusFile!].ForegroundMask);
        Assert.False(main.Stack.CanUndo);
    }

    [AvaloniaFact]
    public async Task Collapsing_LeavesRunningBatchIntact()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var selected = main.Service!.Project.FileNames.Take(2).ToArray();
        main.OnSelectionChanged(selected, selected[0]);
        var completion = new TaskCompletionSource<ForegroundBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<ForegroundBatchJob>? captured = null;
        CancellationToken token = default;
        main.BackgroundRemoval.Dispose();
        main.BackgroundRemoval = new BackgroundRemovalViewModel(main, null, (jobs, _, cancellation) =>
        {
            captured = jobs;
            token = cancellation;
            return completion.Task;
        });
        var work = main.BackgroundRemoval.GenerateSelectedCommand.ExecuteAsync(null);
        Assert.True(main.BackgroundRemoval.IsBatchRunning);

        main.Inspector.IsAiFeaturesExpanded = false;

        Assert.True(main.BackgroundRemoval.IsBatchRunning);
        Assert.False(token.IsCancellationRequested);
        completion.SetResult(Generated(captured!));
        TestEnv.PumpUntil(() => work.IsCompleted);
        await work;
        Assert.Equal(selected, main.Service.Project.FileNames.Where(file => main.Service.Project[file].ForegroundMask is not null));
    }

    [AvaloniaFact]
    public async Task Panel_GroupsDenoiseAndCutout_AndCollapseHidesThem()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var window = new MainWindow(main) { Width = 1440, Height = 900 };
        window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Dispatcher.UIThread.RunJobs();
            var header = Assert.Single(ById(window, "AiFeatures.Panel"), control => control.IsEffectivelyVisible);
            var content = Assert.Single(ById(window, "AiFeatures.Content"),
                control => ReferenceEquals(control.GetVisualParent(), header.GetVisualParent()));
            var title = header.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("group-title"));
            Assert.Equal(Loc.Get("AiFeatures.Title"), title.Text);

            var denoise = Section(window, "denoise");
            var basic = Section(window, "basic");
            var model = One(window, "Denoise.Panel");
            var cutout = One(window, "BackgroundRemoval.Generate");
            var photoTools = One(window, "PhotoTools.Panel");
            Assert.True(denoise.IsEffectivelyVisible);
            Assert.True(IsInside(content, denoise));
            Assert.True(IsInside(content, model));
            Assert.True(IsInside(content, cutout));
            Assert.False(IsInside(content, photoTools));
            Assert.False(IsInside(content, basic));
            Assert.True(basic.IsEffectivelyVisible);

            double Y(Control control) => control.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            // 照片工具 → 基本分組 → AI 功能 → 光學等其餘分組。
            Assert.True(Y(photoTools) < Y(basic));
            Assert.True(Y(basic) < Y(header));
            Assert.True(Y(header) < Y(denoise));
            Assert.True(Y(denoise) < Y(model));
            Assert.True(Y(model) < Y(cutout));
            Assert.True(Y(cutout) < Y(Section(window, "vignette")));

            main.BackgroundRemoval.BeginEditCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(main.BackgroundRemoval.IsEditing);
            Assert.True(One(window, "BackgroundRemoval.Apply").IsEffectivelyVisible);

            var toggle = Assert.IsAssignableFrom<IToggleProvider>(ControlAutomationPeer.CreatePeerForElement(header));
            toggle.Toggle();
            Dispatcher.UIThread.RunJobs();
            Assert.False(main.Inspector.IsAiFeaturesExpanded);
            Assert.False(main.BackgroundRemoval.IsEditing);
            Assert.False(Section(window, "denoise").IsEffectivelyVisible);
            Assert.False(One(window, "Denoise.Panel").IsEffectivelyVisible);
            Assert.False(One(window, "BackgroundRemoval.Generate").IsEffectivelyVisible);
            Assert.True(Section(window, "basic").IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    private static ForegroundBatchResult Generated(IReadOnlyList<ForegroundBatchJob> jobs) => new(
        jobs.Select(job =>
        {
            var (width, height) = ImageLoader.DecodeInfo(job.SourcePath);
            return new ForegroundBatchItem(job, ForegroundBatchStatus.Generated,
                new ForegroundMask(1, 1, width, height, [32768]));
        }).ToArray(), false);

    private static IEnumerable<Control> ById(Control root, string id) =>
        root.GetVisualDescendants().OfType<Control>().Where(control => AutomationProperties.GetAutomationId(control) == id);

    private static Control One(Control root, string id) => Assert.Single(ById(root, id));

    private static ModuleSection Section(Control root, string key) =>
        Assert.Single(root.GetVisualDescendants().OfType<ModuleSection>(),
            section => section.DataContext is ModuleSectionViewModel { Key: var module } && module == key);

    private static bool IsInside(Control ancestor, Control control)
    {
        for (Visual? node = control; node is not null; node = node.GetVisualParent())
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }
}
