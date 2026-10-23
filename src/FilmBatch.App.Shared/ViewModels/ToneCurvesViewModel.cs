using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Core.Curves;
using FilmBatch.Core.Params;
using FilmBatch.Engine.Imaging;

namespace FilmBatch.App.ViewModels;

/// <summary>共用曲線面板；通道選擇是 UI 狀態，實際曲線與分段參數仍由既有參數列保存。</summary>
public sealed partial class ToneCurvesViewModel : ObservableObject
{
    private readonly IParamHost _host;
    private readonly PreviewViewModel? _preview;
    private readonly CurveParamViewModel[] _curves;
    private string _channel = "master";
    private float[]? _parametricSamples;
    private static readonly CurvePoint[][] Presets =
    [
        [new(0, 0), new(1, 1)],
        [new(0, 0), new(.25, .2), new(.5, .5), new(.75, .8), new(1, 1)],
        [new(0, 0), new(.25, .125), new(.5, .5), new(.75, .875), new(1, 1)],
    ];
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#DD3333"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#33BB55"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#3377EE"));

    public ToneCurvesViewModel(IParamHost host, IReadOnlyList<ParamViewModel> parameters, PreviewViewModel? preview)
    {
        _host = host;
        _preview = preview;
        _curves = [.. new[] { "master", "red", "green", "blue" }
            .Select(key => parameters.OfType<CurveParamViewModel>().Single(p => p.Def.Key == key))];
        Regions = [.. new[] { "highlights", "lights", "darks", "shadows" }
            .Select(key => parameters.OfType<SliderParamViewModel>().Single(p => p.Def.Key == key))];
        Splits = [.. new[] { "split_shadows", "split_mid", "split_highlights" }
            .Select(key => parameters.OfType<SliderParamViewModel>().Single(p => p.Def.Key == key))];
        Refine = parameters.OfType<SliderParamViewModel>().Single(p => p.Def.Key == "refine_saturation");
        foreach (var curve in _curves) curve.PropertyChanged += OnCurveChanged;
        foreach (var slider in Regions.Concat(Splits)) slider.PropertyChanged += OnRegionChanged;
        if (_preview is not null) _preview.PropertyChanged += OnPreviewChanged;
    }

    /// <summary>切換通道、套用預設或重設前，讓畫布結束仍被捕捉的手勢。</summary>
    public event Action? FinishGestureRequested;

    public string Channel
    {
        get => _channel;
        set
        {
            if (value is not ("master" or "red" or "green" or "blue" or "parametric") || value == _channel) return;
            FinishGestureRequested?.Invoke();
            SetProperty(ref _channel, value);
            OnPropertyChanged(nameof(IsParametric));
            OnPropertyChanged(nameof(IsMaster));
            OnPropertyChanged(nameof(IsRed));
            OnPropertyChanged(nameof(IsGreen));
            OnPropertyChanged(nameof(IsBlue));
            OnPropertyChanged(nameof(StrokeBrush));
            OnPropertyChanged(nameof(SelectedCurve));
            NotifyCurve();
            UpdateParametricSamples();
        }
    }

    public bool IsParametric => Channel == "parametric";
    public bool IsMaster => Channel == "master";
    public bool IsRed => Channel == "red";
    public bool IsGreen => Channel == "green";
    public bool IsBlue => Channel == "blue";
    public IBrush? StrokeBrush => Channel switch { "red" => Red, "green" => Green, "blue" => Blue, _ => null };
    public CurveParamViewModel? SelectedCurve => Channel switch
    {
        "master" => _curves[0], "red" => _curves[1], "green" => _curves[2], "blue" => _curves[3], _ => null,
    };
    public IReadOnlyList<CurvePoint> Points => SelectedCurve?.Points ?? ToneCurve.Identity.Points;
    public bool IsOverridden => SelectedCurve?.IsOverridden == true;
    public float[]? ParametricSamples => _parametricSamples;
    public IReadOnlyList<SliderParamViewModel> Regions { get; }
    public IReadOnlyList<SliderParamViewModel> Splits { get; }
    public SliderParamViewModel Refine { get; }
    public ImageHistogram? Histogram => _preview?.Histogram;
    public IReadOnlyList<string> PresetOptions => [Loc.Get("Curves.Linear"), Loc.Get("Curves.Medium"), Loc.Get("Curves.Strong"), Loc.Get("Curves.Custom")];

    public int PresetIndex
    {
        get
        {
            for (var i = 0; i < Presets.Length; i++)
                if (Points.SequenceEqual(Presets[i])) return i;
            return 3;
        }
        set
        {
            if (IsParametric || value < 0 || value >= Presets.Length || value == PresetIndex) return;
            FinishGestureRequested?.Invoke();
            SelectedCurve!.OnEdited(Presets[value], isFinal: true);
        }
    }

    [RelayCommand]
    private void SelectChannel(string channel) => Channel = channel;

    [RelayCommand]
    private void ResetCurrent()
    {
        FinishGestureRequested?.Invoke();
        if (SelectedCurve is { } curve) curve.OnEdited(ToneCurve.Identity.Points, isFinal: true);
        else _host.SetValues(Regions.Concat(Splits).ToDictionary(p => p.FlatKey, p => p.Def.Default), null);
    }

    public void RefreshLabels() => OnPropertyChanged(nameof(PresetOptions));

    private void OnCurveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == SelectedCurve && e.PropertyName is nameof(CurveParamViewModel.Points) or nameof(CurveParamViewModel.IsOverridden))
            NotifyCurve();
    }

    private void NotifyCurve()
    {
        OnPropertyChanged(nameof(Points));
        OnPropertyChanged(nameof(PresetIndex));
        OnPropertyChanged(nameof(IsOverridden));
    }

    private void OnRegionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsParametric && e.PropertyName == nameof(SliderParamViewModel.Value)) UpdateParametricSamples();
    }

    private void UpdateParametricSamples()
    {
        _parametricSamples = IsParametric
            ? ToneCurveSet.BakeParametric(Regions[3].Value, Regions[2].Value, Regions[1].Value, Regions[0].Value,
                Splits[0].Value / 100, Splits[1].Value / 100, Splits[2].Value / 100)
            : null;
        OnPropertyChanged(nameof(ParametricSamples));
    }

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewViewModel.Histogram)) OnPropertyChanged(nameof(Histogram));
    }
}
