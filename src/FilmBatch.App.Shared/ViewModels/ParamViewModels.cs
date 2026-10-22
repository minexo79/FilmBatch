using System.Collections.ObjectModel;
using FilmBatch.Raw;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Controls;
using FilmBatch.App.Localization;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>模組面板接收參數修改的對象（由 <see cref="InspectorViewModel"/> 實作）。</summary>
public interface IParamHost
{
    /// <param name="mergeKey">非 null 時識別一次連續手勢：先預覽，結束時提交一筆可復原修改。</param>
    void SetValue(string flatKey, ParamValue value, string? mergeKey);
    /// <summary>一筆可復原的多欄位寫入（例如遮罩名稱＋實例）。預設逐 key 呼叫 <see cref="SetValue"/>；
    /// 專案實作以單一命令寫入，復原一次還原全部欄位。</summary>
    void SetValues(IReadOnlyDictionary<string, ParamValue> values, string? mergeKey)
    {
        foreach (var (key, value) in values) SetValue(key, value, mergeKey);
    }
    void ResetParam(string flatKey);
    /// <summary>一筆可復原的多欄位重設。預設逐 key 呼叫 <see cref="ResetParam"/>；專案實作以單一命令重設。</summary>
    void ResetParams(IReadOnlyList<string> flatKeys)
    {
        foreach (var key in flatKeys) ResetParam(key);
    }
    /// <summary>提交連續編輯（放開滑桿），並結束復原紀錄的合併。</summary>
    void EndEdit();
}

/// <summary>
/// 一個參數列的 ViewModel。由 schema 的 <see cref="ParamDef"/> 產生，View 依型別以 DataTemplate 選擇，
/// 新增參數不需要寫 XAML（計畫書 2.4.5）。
/// </summary>
public abstract partial class ParamViewModel(string moduleKey, ParamDef def, IParamHost host) : ObservableObject
{
    private bool _loading;

    public ParamDef Def { get; } = def;
    public string ModuleKey { get; } = moduleKey;
    public string FlatKey { get; } = $"{moduleKey}.{def.Key}";
    public string Label => Loc.ParamLabel(ModuleKey, Def);

    /// <summary>切換語言後更新顯示名稱。</summary>
    public virtual void RefreshLabels() => OnPropertyChanged(nameof(Label));
    protected IParamHost Host { get; } = host;

    /// <summary>此情況下參數列是否顯示（RAW 自訂白平衡／遮罩子控制項依設定收合）；一般參數恆為 true。</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>此參數被目前照片覆寫（名稱粗體 + ●）。</summary>
    [ObservableProperty]
    private bool _isOverridden;

    /// <summary>雙擊名稱：重設回預設值。</summary>
    [RelayCommand]
    private void Reset() => OnReset();

    protected virtual void OnReset() => Host.ResetParam(FlatKey);

    /// <summary>正在從設定載入顯示值（此時的屬性變更不可寫回）。</summary>
    protected bool IsLoading => _loading;

    /// <summary>從照片的最終參數載入（不觸發修改）。</summary>
    public void Load(ParamValue value, bool overridden)
    {
        _loading = true;
        try
        {
            OnLoad(value);
            IsOverridden = overridden;
        }
        finally
        {
            _loading = false;
        }
    }

    protected abstract void OnLoad(ParamValue value);

    protected void Push(ParamValue value, string? mergeKey = null)
    {
        if (!_loading) Host.SetValue(FlatKey, value, mergeKey);
    }

    public static ParamViewModel Create(string moduleKey, ParamDef def, IParamHost host) => (moduleKey, def.Key) switch
    {
        ("raw", "wb_mode") => new RawWbParamViewModel(moduleKey, def, host),
        ("raw", "mask_name") => new RawMaskParamViewModel(moduleKey, def, host),
        _ => CreateDefault(moduleKey, def, host),
    };

    private static ParamViewModel CreateDefault(string moduleKey, ParamDef def, IParamHost host) => def.Kind switch
    {
        ParamKind.Bool => new ToggleParamViewModel(moduleKey, def, host),
        ParamKind.Choice => new ChoiceParamViewModel(moduleKey, def, host),
        ParamKind.Path or ParamKind.Text => new TextParamViewModel(moduleKey, def, host),
        ParamKind.Curve => new CurveParamViewModel(moduleKey, def, host),
        _ => new SliderParamViewModel(moduleKey, def, host),
    };
}

/// <summary>數值參數：滑桿 + 數值框。</summary>
public partial class SliderParamViewModel(string moduleKey, ParamDef def, IParamHost host) : ParamViewModel(moduleKey, def, host)
{
    private double _value;
    private string? _editSession;

    public double Minimum => Def.Min;
    public double Maximum => Def.Max;
    public double Step => Def.Step;
    public string Unit => Def.Unit;
    public string FormatString => Def.Step >= 1 ? "0" : Def.Step >= 0.1 ? "0.0" : "0.00";

    /// <summary>讓參數列可套用與色彩語意相符的滑桿軌道樣式：冷暖軸（含遮罩色溫、底片暖色）與綠洋紅軸。</summary>
    public bool IsTemperature => Def.Key is "temperature" or "mask_temperature" or "warmth";
    public bool IsTint => Def.Key is "tint" or "mask_tint";

    /// <summary>其他具色彩語意的軌道漸層（色相、各色強度、色相帶）；null 表示一般軌道。</summary>
    public IBrush? TrackBrush => ParamTrackBrushes.For(FlatKey);

    public double Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value)) return;
            OnPropertyChanged(nameof(NumericValue));
            PushValue(value, _editSession);
        }
    }

    protected virtual void PushValue(double value, string? editSession) => Push(value, editSession);

    /// <summary>給 NumericUpDown（decimal?）使用。</summary>
    public decimal? NumericValue
    {
        get => (decimal)_value;
        set
        {
            if (value is { } v) Value = Math.Clamp((double)v, Minimum, Maximum);
        }
    }

    /// <summary>滑桿開始拖曳或滾動：之後的修改合併為一筆復原紀錄。</summary>
    public void BeginEdit() => _editSession = $"{FlatKey}#{Guid.NewGuid():N}";

    public void EndEdit()
    {
        _editSession = null;
        Host.EndEdit();
    }

    protected override void OnLoad(ParamValue value)
    {
        if (SetProperty(ref _value, value.AsNumber, nameof(Value)))
            OnPropertyChanged(nameof(NumericValue));
    }
}

/// <summary>選項參數：未知值（如舊版或手動編輯檔案留下的 key）保留顯示，不強制跳回首項。</summary>
public partial class ChoiceParamViewModel(string moduleKey, ParamDef def, IParamHost host) : ParamViewModel(moduleKey, def, host)
{
    private ChoiceItem? _selected;

    public IReadOnlyList<ChoiceItem> Options { get; } = def.Choices.Select(c => new ChoiceItem(moduleKey, def.Key, c)).ToList();

    public override void RefreshLabels()
    {
        base.RefreshLabels();
        foreach (var o in Options) o.RefreshLabel();
    }

    public ChoiceItem? Selected
    {
        get => _selected;
        set
        {
            if (value is null || !SetProperty(ref _selected, value)) return;
            PushChoice(value);
        }
    }

    protected virtual void PushChoice(ChoiceItem value) => Push(value.Key);

    /// <summary>不寫回地改變目前選項（寫入已由其他列一併送出時同步顯示）。</summary>
    protected void ShowChoice(string key)
    {
        if (Options.FirstOrDefault(o => o.Key == key) is { } match) SetProperty(ref _selected, match, nameof(Selected));
    }

    /// <summary>目前值不在選項中時顯示原文（例如已移除的底片 key）。</summary>
    public string UnknownValue { get; private set; } = "";

    public bool HasUnknownValue => UnknownValue.Length > 0;

    protected override void OnLoad(ParamValue value)
    {
        var match = Options.FirstOrDefault(o => o.Key == value.AsText);
        UnknownValue = match is null ? value.AsText : "";
        OnPropertyChanged(nameof(UnknownValue));
        OnPropertyChanged(nameof(HasUnknownValue));
        SetProperty(ref _selected, match ?? Options[0], nameof(Selected));
    }
}

/// <summary>選項參數的一個選項；顯示名稱依目前語言。</summary>
public sealed class ChoiceItem(string moduleKey, string paramKey, ChoiceOption option) : ObservableObject
{
    public ChoiceOption Option { get; } = option;
    public string Key => Option.Key;
    public string Label => Loc.ChoiceLabel(moduleKey, paramKey, Option);

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));

    public override string ToString() => Label;
}

public sealed partial class ToggleParamViewModel(string moduleKey, ParamDef def, IParamHost host) : ParamViewModel(moduleKey, def, host)
{
    private bool _value;

    public bool Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value)) Push(value);
        }
    }

    protected override void OnLoad(ParamValue value) => SetProperty(ref _value, value.AsBool, nameof(Value));
}

/// <summary>文字／路徑參數（相對路徑）。</summary>
public sealed partial class TextParamViewModel(string moduleKey, ParamDef def, IParamHost host) : ParamViewModel(moduleKey, def, host)
{
    private string _value = "";

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? "")) Push(_value);
        }
    }

    protected override void OnLoad(ParamValue value) => SetProperty(ref _value, value.AsText, nameof(Value));
}

/// <summary>語意遮罩下拉的一個項目；<c>IsNone</c> 為「不使用」項目（清空設定）。</summary>
public sealed class SemanticMaskOption(string name, string instance, string label)
{
    public string Name { get; } = name;
    /// <summary>實例識別（<see cref="SemanticMask.InstanceId"/> 或空字串）。</summary>
    public string Instance { get; } = instance;
    public string Label { get; } = label;
    public bool IsNone => Name.Length == 0;
    public override string ToString() => Label;
}

/// <summary>
/// raw.wb_mode（拍攝值｜自訂）下拉：額外在同一列顯示來源的拍攝白平衡（As Shot）。
/// RAW 照片時與 <see cref="RawWbSliderParamViewModel"/> 一起顯示在「基本調整」的色溫／色調位置；
/// 由拍攝值切到自訂時，以目前顯示的拍攝值作為起點一併寫入（不跳回 schema 預設 6500 K）。
/// </summary>
public sealed partial class RawWbParamViewModel(string moduleKey, ParamDef def, IParamHost host)
    : ChoiceParamViewModel(moduleKey, def, host)
{
    public const string Custom = "custom";
    public static readonly string[] FlatKeys = ["raw.wb_mode", "raw.temperature", "raw.tint"];

    private double? _lastTemperature;
    private double? _lastTint;

    /// <summary>同區塊的 Kelvin 色溫與色調列（由 <see cref="ModuleSectionViewModel"/> 連結）。</summary>
    internal RawWbSliderParamViewModel? Temperature { get; set; }
    internal RawWbSliderParamViewModel? Tint { get; set; }

    public bool IsCustom => Selected?.Key == Custom;

    /// <summary>來源的拍攝白平衡（未知時為 null）。</summary>
    internal (double Temperature, double Tint)? AsShot =>
        _lastTemperature is { } t ? (t, _lastTint ?? 0) : null;

    protected override void PushChoice(ChoiceItem value)
    {
        if (IsLoading) return;
        if (value.Key == Custom && !IsCustomStored && Temperature is { } t && Tint is { } ti && AsShot is not null)
            Host.SetValues(new Dictionary<string, ParamValue>
            {
                ["raw.wb_mode"] = ParamValue.Of(Custom),
                [t.FlatKey] = ParamValue.Of(t.Value),
                [ti.FlatKey] = ParamValue.Of(ti.Value),
            }, null);
        else
            base.PushChoice(value);
        IsCustomStored = value.Key == Custom;
    }

    /// <summary>設定中目前的模式是否為自訂（下拉的顯示值在寫入前可能已先變更）。</summary>
    internal bool IsCustomStored { get; private set; }

    protected override void OnLoad(ParamValue value)
    {
        base.OnLoad(value);
        IsCustomStored = value.AsText == Custom;
    }

    /// <summary>色溫／色調列已把模式一併寫成自訂：同步顯示，之後的拖曳只寫該列。</summary>
    internal void MarkCustom()
    {
        ShowChoice(Custom);
        IsCustomStored = true;
    }

    /// <summary>「拍攝值 5500K・色偏 +8」；來源尚未解碼或不含白平衡資料時為空。</summary>
    public string AsShotText { get; private set; } = "";

    public bool HasAsShot => AsShotText.Length > 0;

    /// <summary>更新來源的拍攝白平衡顯示；未知時傳 null。</summary>
    public void SetAsShot(double? temperature, double? tint)
    {
        _lastTemperature = temperature;
        _lastTint = tint;
        AsShotText = temperature is not { } t ? ""
            : tint is { } ti && ti != 0
                ? Loc.Format("Raw.AsShotTint", (int)Math.Round(t), (int)Math.Round(ti))
                : Loc.Format("Raw.AsShot", (int)Math.Round(t));
        OnPropertyChanged(nameof(AsShotText));
        OnPropertyChanged(nameof(HasAsShot));
    }

    public override void RefreshLabels()
    {
        base.RefreshLabels();
        SetAsShot(_lastTemperature, _lastTint);
    }
}

/// <summary>
/// raw.mask_name 的語意遮罩選取列：列出來源實際嵌入的 SemanticMask（名稱＋實例）。
/// 選取一次寫入 mask_name＋mask_instance（host 端成一筆復原紀錄）；來源缺少已選名稱時顯示不可用，
/// 設定值保留、絕不自動改選其他遮罩（不變更檔案、不做全圖調整）。
/// </summary>
public sealed partial class RawMaskParamViewModel : ParamViewModel
{
    private string _name = "";
    private string _instance = "";
    private bool _decoded;
    private bool _updatingOptions;
    private SemanticMaskOption? _selected;
    private SemanticMaskOption _none;
    private SemanticMaskOption? _unavailable;
    private IReadOnlyList<SemanticMask>? _lastMasks;

    public ObservableCollection<SemanticMaskOption> Options { get; }

    public RawMaskParamViewModel(string moduleKey, ParamDef def, IParamHost host) : base(moduleKey, def, host)
    {
        _none = new SemanticMaskOption("", "", Loc.Get("Raw.MaskNone"));
        Options = [_none];
    }

    /// <summary>更新來源遮罩清單（null 表示一般影像或尚未解碼 RAW）。</summary>
    public void SetMasks(IReadOnlyList<SemanticMask>? masks)
    {
        _lastMasks = masks;
        _decoded = masks is not null;
        _updatingOptions = true;
        Options.Clear();
        Options.Add(_none);
        if (masks is not null)
            foreach (var mask in masks)
                Options.Add(new SemanticMaskOption(mask.Name, mask.InstanceId ?? "", DisplayLabel(mask)));
        ResolveSelection();
        _updatingOptions = false;
    }

    /// <summary>下拉選取；寫入名稱與實例兩個 key（host 端成一筆復原紀錄）。</summary>
    public SemanticMaskOption? Selected
    {
        get => _selected ?? _unavailable ?? _none;
        set
        {
            if (_updatingOptions || value is null || !Options.Contains(value)) return;
            var option = value;
            if (ReferenceEquals(option, _unavailable) || ReferenceEquals(_selected, option)
                || (option.IsNone && _name.Length == 0 && _instance.Length == 0)) return;
            _selected = option.IsNone ? null : option;
            _name = _selected?.Name ?? "";
            _instance = _selected?.Instance ?? "";
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(SelectedInstance));
            ResolveSelection();
            PushSelection();
        }
    }

    /// <summary>目前選取的實例 id；無選取或不可用時為 null。</summary>
    public string? SelectedInstance => _selected?.Instance;

    /// <summary>已保存的遮罩名稱＋實例在這個來源中不存在。</summary>
    public bool IsUnavailable { get; private set; }

    /// <summary>不可用時顯示的已保存名稱。</summary>
    public string UnavailableName { get; private set; } = "";

    /// <summary>狀態提示：找不到已選遮罩或來源沒有遮罩；正常時為空。</summary>
    public string MaskStatusText { get; private set; } = "";

    public bool HasMaskStatus => MaskStatusText.Length > 0;

    /// <summary>由模組區塊在讀取所有參數後補上同層的 mask_instance 值（不觸發寫入）。</summary>
    public void ApplyInstance(string instance)
    {
        _instance = instance;
        ResolveSelection();
    }

    public override void RefreshLabels()
    {
        base.RefreshLabels();
        _none = new SemanticMaskOption("", "", Loc.Get("Raw.MaskNone"));
        SetMasks(_lastMasks);
    }

    protected override void OnLoad(ParamValue value)
    {
        _name = value.AsText;
        ResolveSelection();
    }

    private void PushSelection()
    {
        Host.SetValues(new Dictionary<string, ParamValue>
        {
            ["raw.mask_name"] = ParamValue.Of(_name),
            ["raw.mask_instance"] = ParamValue.Of(_instance),
        }, null);
    }

    /// <summary>與算圖端相同：僅使用精確的名稱＋實例；缺席時保留獨立的不可用項目。</summary>
    private void ResolveSelection()
    {
        var updating = _updatingOptions;
        _updatingOptions = true;
        try
        {
            if (_unavailable is not null) Options.Remove(_unavailable);
            _unavailable = null;
            var match = _name.Length == 0 ? null
                : Options.FirstOrDefault(o => o.Name == _name && o.Instance == _instance);
            if (_name.Length > 0 && match is null)
            {
                _unavailable = new SemanticMaskOption(_name, _instance, Loc.Format("Raw.MaskUnavailable", _name));
                Options.Add(_unavailable);
            }
            _selected = match;
            IsUnavailable = _decoded && _name.Length > 0 && match is null;
            UnavailableName = IsUnavailable ? _name : "";
            MaskStatusText = IsUnavailable ? Loc.Format("Raw.MaskUnavailable", _name)
                : _decoded && _name.Length == 0 && Options.Count <= 1 ? Loc.Get("Raw.NoMasks")
                : "";
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(SelectedInstance));
            OnPropertyChanged(nameof(IsUnavailable));
            OnPropertyChanged(nameof(UnavailableName));
            OnPropertyChanged(nameof(MaskStatusText));
            OnPropertyChanged(nameof(HasMaskStatus));
        }
        finally { _updatingOptions = updating; }
    }

    /// <summary>保留來源完整名稱，並以實例識別消歧；不以顯示標籤回推設定值。</summary>
    private static string DisplayLabel(SemanticMask mask) =>
        mask.InstanceId is { Length: > 0 } id ? $"{mask.Name} · {id}" : mask.Name;
}

/// <summary>
/// raw.temperature（K）／raw.tint：RAW 照片時取代「基本調整」的相對色溫／色調。
/// 拍攝值模式下顯示來源拍攝值；拖曳時把模式＋色溫＋色調一起寫成自訂（同一筆復原紀錄），
/// 雙擊名稱則三者一起重設回拍攝值。
/// </summary>
public sealed class RawWbSliderParamViewModel(string moduleKey, ParamDef def, IParamHost host)
    : SliderParamViewModel(moduleKey, def, host)
{
    /// <summary>白平衡模式列（由 <see cref="ModuleSectionViewModel"/> 連結）。</summary>
    internal RawWbParamViewModel? Mode { get; set; }

    /// <summary>另一個白平衡軸（色溫 ↔ 色調）。</summary>
    internal RawWbSliderParamViewModel? Partner { get; set; }

    protected override void PushValue(double value, string? editSession)
    {
        if (IsLoading) return;
        if (Mode is not { IsCustomStored: false } mode || Partner is not { } partner)
        {
            base.PushValue(value, editSession);
            return;
        }
        Host.SetValues(new Dictionary<string, ParamValue>
        {
            ["raw.wb_mode"] = ParamValue.Of(RawWbParamViewModel.Custom),
            [FlatKey] = ParamValue.Of(value),
            [partner.FlatKey] = ParamValue.Of(partner.Value),
        }, editSession);
        mode.MarkCustom();
    }

    protected override void OnReset() => Host.ResetParams(RawWbParamViewModel.FlatKeys);
}
