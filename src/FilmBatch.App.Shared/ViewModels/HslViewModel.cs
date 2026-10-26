using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using FilmBatch.App.Controls;
using FilmBatch.App.Localization;
using FilmBatch.Core.Film;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>色相修正的一個色相帶：三個滑桿直接是 hsl 模組的 ParamViewModel（覆寫標記、復原合併照舊）。</summary>
public sealed partial class HueBandViewModel(int index, IBrush swatch, ParamViewModel hue, ParamViewModel saturation, ParamViewModel lightness)
    : ObservableObject
{
    public int Index { get; } = index;
    public string Label => Loc.TryGet($"Hsl.Band.{HueBands.Keys[Index]}") ?? HueBands.Labels[Index];
    public IBrush Swatch { get; } = swatch;
    public ParamViewModel Hue { get; } = hue;
    public ParamViewModel Saturation { get; } = saturation;
    public ParamViewModel Lightness { get; } = lightness;
    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>色相修正預設下拉的一項（純文字，無縮圖；切換語言時更新名稱）。</summary>
public sealed partial class HslPresetViewModel(HuePreset preset) : ObservableObject
{
    public string Key { get; } = preset.Key;
    public HuePreset Preset { get; } = preset;
    public string Name => Loc.TryGet($"Hsl.Preset.{Key}") ?? Preset.Name;
    public void RefreshLabel() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// hsl 模組的獨立色相編輯器：七個內建預設的文字下拉＋八個色相帶選取器與每帶三個滑桿。
/// 預設套用走 <see cref="IParamHost.SetValues"/> 的一筆可復原寫入（mergeKey=null），
/// 與底片效果無關：不寫 film.*，也不要求 film 啟用。
/// </summary>
public sealed partial class HslViewModel : ObservableObject
{
    // 色塊顏色只是辨識用的示意色（各色相帶中心附近），與主題無關。
    private static readonly IBrush[] Swatches = [.. ParamTrackBrushes.BandColors.Select(c => new ImmutableSolidColorBrush(c))];
    private readonly IParamHost _host;
    private bool _loading;

    /// <param name="hueParams">hsl 模組的 24 個色相參數列，順序為色相帶 × (h, s, l)。</param>
    public HslViewModel(IParamHost host, IReadOnlyList<ParamViewModel> hueParams)
    {
        _host = host;
        Bands = [.. Enumerable.Range(0, HueBands.Keys.Count).Select(b => new HueBandViewModel(b, Swatches[b],
            hueParams[b * 3], hueParams[b * 3 + 1], hueParams[b * 3 + 2]))];
        Presets = [.. HuePresets.All.Select(p => new HslPresetViewModel(p))];
        SelectedBand = Bands[0];
    }

    public IReadOnlyList<HueBandViewModel> Bands { get; }
    public IReadOnlyList<HslPresetViewModel> Presets { get; }

    [ObservableProperty] private HueBandViewModel? _selectedBand;
    [ObservableProperty] private HslPresetViewModel? _selectedPreset;

    /// <summary>目前 24 個值不符合任何內建預設（使用者自訂）。</summary>
    [ObservableProperty] private bool _isCustomHue;

    /// <summary>選擇預設：以 host 的一筆可復原寫入設定全部 24 個 hsl.hue_* 值；null（自訂/取消）不寫入。</summary>
    partial void OnSelectedPresetChanged(HslPresetViewModel? value)
    {
        if (_loading || value is null) return;
        _host.SetValues(value.Preset.ToSettings(), null);
    }

    /// <summary>以焦點照片的最終參數更新預設選取與「自訂」標記；載入不寫回設定。</summary>
    public void Load(Settings resolved)
    {
        _loading = true;
        try
        {
            var preset = HuePresets.Find(resolved);
            SelectedPreset = preset is null ? null : Presets.FirstOrDefault(p => p.Key == preset.Key);
            IsCustomHue = preset is null;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>切換語言後更新預設與色相帶名稱。</summary>
    public void RefreshLabels()
    {
        foreach (var p in Presets) p.RefreshLabel();
        foreach (var b in Bands) b.RefreshLabel();
    }
}
