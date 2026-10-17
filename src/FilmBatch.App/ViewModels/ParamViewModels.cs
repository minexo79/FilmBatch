using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmBatch.App.Localization;
using FilmBatch.Core.Params;

namespace FilmBatch.App.ViewModels;

/// <summary>模組面板接收參數修改的對象（由 <see cref="InspectorViewModel"/> 實作）。</summary>
public interface IParamHost
{
    /// <param name="mergeKey">非 null 時，連續相同 key 的修改合併為一筆復原紀錄（拖曳中）。</param>
    void SetValue(string flatKey, ParamValue value, string? mergeKey);
    void ResetParam(string flatKey);
    /// <summary>連續編輯結束（放開滑桿）：結束復原紀錄的合併。</summary>
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

    /// <summary>此參數被目前照片覆寫（名稱粗體 + ●）。</summary>
    [ObservableProperty]
    private bool _isOverridden;

    /// <summary>雙擊名稱：重設回預設值。</summary>
    [RelayCommand]
    private void Reset() => Host.ResetParam(FlatKey);

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

    public static ParamViewModel Create(string moduleKey, ParamDef def, IParamHost host) => def.Kind switch
    {
        ParamKind.Bool => new ToggleParamViewModel(moduleKey, def, host),
        ParamKind.Choice => new ChoiceParamViewModel(moduleKey, def, host),
        ParamKind.Path => new TextParamViewModel(moduleKey, def, host),
        ParamKind.Curve => new CurveParamViewModel(moduleKey, def, host),
        _ => new SliderParamViewModel(moduleKey, def, host),
    };
}

/// <summary>數值參數：滑桿 + 數值框。</summary>
public sealed partial class SliderParamViewModel(string moduleKey, ParamDef def, IParamHost host) : ParamViewModel(moduleKey, def, host)
{
    private double _value;
    private string? _editSession;

    public double Minimum => Def.Min;
    public double Maximum => Def.Max;
    public double Step => Def.Step;
    public string Unit => Def.Unit;
    public string FormatString => Def.Step >= 1 ? "0" : Def.Step >= 0.1 ? "0.0" : "0.00";

    public double Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value)) return;
            OnPropertyChanged(nameof(NumericValue));
            Push(value, _editSession);
        }
    }

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
public sealed partial class ChoiceParamViewModel(string moduleKey, ParamDef def, IParamHost host) : ParamViewModel(moduleKey, def, host)
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
            Push(value.Key);
        }
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

/// <summary>路徑參數（相對路徑）。</summary>
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
