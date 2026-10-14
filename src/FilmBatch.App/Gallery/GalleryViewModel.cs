using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FilmBatch.App.ViewModels;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Rendering;

namespace FilmBatch.App.Gallery;

/// <summary>元件展示視窗的假資料：不需要專案或 GPU。</summary>
public sealed class GalleryViewModel : IParamHost
{
    public IReadOnlyList<GallerySwatch> Swatches { get; } =
    [
        new GallerySwatch("FB.WindowBrush", "#2D2D2D"), new GallerySwatch("FB.TextBrush", "#DCDCDC"), new GallerySwatch("FB.BaseBrush", "#1E1E1E"),
        new GallerySwatch("FB.AlternateBaseBrush", "#262626"), new GallerySwatch("FB.ButtonBrush", "#353535"), new GallerySwatch("FB.PlaceholderBrush", "#7A7A7A"),
        new GallerySwatch("FB.ToolTipBackgroundBrush", "#3A3A3A"), new GallerySwatch("FB.BorderLightBrush", "#4A4A4A"), new GallerySwatch("FB.BorderBrush", "#3E3E3E"),
        new GallerySwatch("FB.ShadowBrush", "#1A1A1A"), new GallerySwatch("FB.DisabledTextBrush", "#6E6E6E"), new GallerySwatch("FB.DisabledAccentBrush", "#505050"),
        new GallerySwatch("FB.AccentBrush", "#D98E2B"), new GallerySwatch("FB.AccentTextBrush", "#111111"), new GallerySwatch("FB.LinkBrush", "#E6A44A"),
        new GallerySwatch("FB.WarningTextBrush", "#FF6B6B"), new GallerySwatch("FB.PresetUpdatedBrush", "#5AA9E6"), new GallerySwatch("FB.ErrorBrush", "#E05555"),
        new GallerySwatch("FB.SuccessBrush", "#6BBF59"),
    ];

    public SliderParamViewModel NormalParam { get; }
    public SliderParamViewModel OverriddenParam { get; }
    public ChoiceParamViewModel ChoiceParam { get; }
    public ToggleParamViewModel ToggleParam { get; }
    public ModuleSectionViewModel EnabledModule { get; }
    public ModuleSectionViewModel DisabledModule { get; }
    public ModuleSectionViewModel CollapsedModule { get; }
    public IReadOnlyList<ThumbnailViewModel> Thumbnails { get; }
    public IReadOnlyList<PresetItemViewModel> PresetItems { get; }
    public Bitmap Sample { get; }
    public Bitmap SampleOriginal { get; }

    public GalleryViewModel()
    {
        var basic = ModuleCatalog.Module("basic");
        var film = ModuleCatalog.Module("film");
        NormalParam = new SliderParamViewModel("basic", basic.Params[1], this);
        NormalParam.Load(0.0, overridden: false);
        OverriddenParam = new SliderParamViewModel("basic", basic.Params[4], this);
        OverriddenParam.Load(25.0, overridden: true);
        ChoiceParam = new ChoiceParamViewModel("film", film.Params[1], this);
        ChoiceParam.Load("portra400", overridden: true);
        ToggleParam = new ToggleParamViewModel("lens_distortion", ModuleCatalog.Module("lens_distortion").Params[4], this);
        ToggleParam.Load(true, overridden: false);

        var defaults = ModuleCatalog.Defaults;
        var overrides = new Settings([new("vignette.amount", ParamValue.Of(30.0)), new("vignette.size", ParamValue.Of(70.0))]);
        EnabledModule = Section("vignette", defaults.With("vignette.enabled", true).Overlay(overrides), overrides, expanded: true);
        DisabledModule = Section("halation", defaults, Settings.Empty, expanded: true);
        CollapsedModule = Section("grain", defaults.With("grain.enabled", true), Settings.Empty, expanded: false);

        var synthetic = GpuSpikeBenchmark.Synthetic(132, 88);
        var cpu = new CpuFakeRenderer();
        var effect = cpu.Render(synthetic, new SpikeParams(0.3f, 0.1f, 0.4f, 0.01f));
        var plain = cpu.Render(synthetic, new SpikeParams());
        Sample = ToBitmap(effect);
        SampleOriginal = ToBitmap(plain);

        ThumbnailViewModel Thumb(string name, bool overrides, bool updated, string? error, string color) => new(name)
        {
            Bitmap = error is null ? Sample : null,
            PresetLabel = "Portra 暖…",
            PresetBrush = new SolidColorBrush(Color.Parse(color)),
            HasOverrides = overrides,
            PresetUpdated = updated,
            Error = error,
        };
        Thumbnails =
        [
            Thumb("一般", false, false, null, "#D98E2B"),
            Thumb("有覆寫", true, false, null, "#D98E2B"),
            Thumb("預設已更新", false, true, null, "#5AA9E6"),
            Thumb("覆寫＋已更新", true, true, null, "#6BBF59"),
            Thumb("讀檔失敗", false, false, "無法解碼：檔案損毀", "#7A7A7A"),
        ];
        PresetItems = FilmBatch.Core.Presets.SamplePresets.Create().Take(4)
            .Select((p, i) => new PresetItemViewModel(p) { PinKey = (i + 1).ToString() }).ToList();
    }

    private ModuleSectionViewModel Section(string key, Settings resolved, Settings overrides, bool expanded)
    {
        var m = new ModuleSectionViewModel(ModuleCatalog.Module(key), this, _ => { }, _ => { }) { IsExpanded = expanded };
        m.Load(resolved, overrides, hideDisabled: false);
        return m;
    }

    private static WriteableBitmap ToBitmap(RenderResult r)
    {
        var bmp = new WriteableBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        for (var y = 0; y < r.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(r.Bgra, y * r.Width * 4, fb.Address + y * fb.RowBytes, r.Width * 4);
        return bmp;
    }

    // 展示用：修改不寫入任何地方
    public void SetValue(string flatKey, ParamValue value, string? mergeKey) { }
    public void ResetParam(string flatKey) { }
    public void EndEdit() { }
}

public sealed record GallerySwatch(string Token, string Hex)
{
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(Hex));
    public bool IsNeutral => Color.Parse(Hex) is var c && c.R == c.G && c.G == c.B;
    public string Check => IsNeutral ? "R = G = B" : "彩色";
}
