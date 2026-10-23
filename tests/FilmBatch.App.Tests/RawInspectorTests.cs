using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;
using FilmBatch.App.ViewModels;
using FilmBatch.App.Views;
using FilmBatch.Core;
using FilmBatch.Engine.Imaging;
using FilmBatch.Raw;
using FilmBatch.Raw.Color;

namespace FilmBatch.App.Tests;

public sealed class RawInspectorTests
{
    [AvaloniaFact]
    public async Task StandardPhoto_OpensBasicAdjustmentsWithoutRawControls()
    {
        using var main = TestEnv.CreateViewModel();
        await TestEnv.OpenSamplesAsync(main);
        var window = new MainWindow(main);
        window.Show();
        try
        {
            Assert.True(main.Inspector.Modules.Single(m => m.Key == "basic").IsExpanded);
            Assert.False(main.Inspector.Modules.Single(m => m.Key == "raw").IsExpanded);
            Assert.False(RawControlsVisible(window));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RawControls_FollowDecodedPhotoType_AndRespectHiddenDisabledModules()
    {
        using var main = TestEnv.CreateViewModel();
        var folder = Path.Combine(AppPaths.Root, "raw-inspector");
        Directory.CreateDirectory(folder);
        const string rawName = "raw.dng", normalName = "standard.tif";
        File.WriteAllBytes(Path.Combine(folder, rawName), DngWriter.WriteRenderedDng(16, 12,
            Enumerable.Repeat(0.18f, 16 * 12 * 3).ToArray(), ColorSpaces.Srgb,
            new FilmBatch.Raw.Meta.Metadata { Make = "FilmBatch", Model = "RAW inspector" }));
        File.Copy(Directory.EnumerateFiles(TestEnv.Samples, "*.tif").First(), Path.Combine(folder, normalName));
        await main.OpenFolderAsync(folder);
        var window = new MainWindow(main);
        window.Show();
        try
        {
            Select(rawName);
            Assert.NotNull(main.Preview.Source!.RawInfo);
            Assert.True(RawControlsVisible(window));
            main.Inspector.HideDisabledModules = true;
            // RAW 原廠處理預設啟用：隱藏停用模組時仍顯示，關閉後才隱藏。
            Assert.True(RawControlsVisible(window));
            main.Inspector.SetValue("raw.enabled", false, null);
            WaitForSource(rawName);
            Assert.False(RawControlsVisible(window));
            main.Inspector.SetValue("raw.enabled", true, null);
            main.Inspector.SetValue("raw.camera_local_tone", true, null);
            WaitForSource(rawName);
            Assert.True(RawControlsVisible(window));
            var rawSettings = main.FocusedResolved;

            Select(normalName);
            Assert.Null(main.Preview.Source!.RawInfo);
            Assert.False(RawControlsVisible(window));
            // Persisted/copied RAW settings must not make RAW controls applicable to an ordinary image.
            main.Inspector.SetValue("raw.enabled", true, null);
            main.Inspector.HideDisabledModules = false;
            WaitForSource(normalName);
            Assert.False(RawControlsVisible(window));
            Assert.True(main.FocusedResolved.Bool("raw.enabled"));

            Select(rawName);
            Assert.True(RawControlsVisible(window));
            Assert.Equal(rawSettings, main.FocusedResolved);
            main.OnSelectionChanged([], null);
            Assert.False(RawControlsVisible(window));
        }
        finally { window.Close(); }

        void Select(string file)
        {
            main.Filmstrip.SelectContextTarget(main.Filmstrip.Items.Single(t => t.FileName == file));
            WaitForSource(file);
        }
        void WaitForSource(string file) => TestEnv.PumpUntil(() => main.Preview.Source is { } source
            && source.FilePath == Path.Combine(folder, file)
            && source.RawOptions == RawLoadOptions.FromSettings(main.FocusedResolved)
            && main.Preview.Image is not null && !main.Preview.IsBusy && !main.Renderer.IsBusy);
    }

    private static bool RawControlsVisible(Window window) => window.GetVisualDescendants().OfType<ModuleSection>()
        .Any(s => s.DataContext is ModuleSectionViewModel { Key: "raw" } && s.IsEffectivelyVisible);
}
