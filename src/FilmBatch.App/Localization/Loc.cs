using System.ComponentModel;
using System.Globalization;
using FilmBatch.Core.Params;

namespace FilmBatch.App.Localization;

/// <summary>
/// 介面字串的執行期存取（計畫書 2.10）。XAML 以 <c>{l:Tr Key}</c> 繫結到索引子，切換語言時即時更新；
/// ViewModel 可用強型別的 <see cref="Strings"/> 或 <see cref="Format"/>。
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static readonly IReadOnlyList<(string Code, string Name)> Languages = [("zh-TW", "繁體中文"), ("en", "English")];

    private CultureInfo _culture = CultureInfo.GetCultureInfo(Services.AppSettings.DefaultLanguage);

    private Loc() => Core.CoreText.English = _culture.TwoLetterISOLanguageName == "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (Equals(_culture, value)) return;
            _culture = value;
            Strings.Culture = value;
            Core.CoreText.English = value.TwoLetterISOLanguageName == "en";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        }
    }

    /// <summary>macOS 上優先使用 <c>key.Mac</c>（快速鍵寫法不同的提示文字）。</summary>
    public string this[string key] =>
        (OperatingSystem.IsMacOS() ? Strings.ResourceManager.GetString(key + ".Mac", _culture) : null)
        ?? Strings.ResourceManager.GetString(key, _culture) ?? $"[{key}]";

    public static string Get(string key) => Instance[key];

    /// <summary>找不到時傳回 null（不顯示 [key]），供有預設文字的查詢使用。</summary>
    public static string? TryGet(string key) => Strings.ResourceManager.GetString(key, Instance._culture);

    // ── 參數 schema 的顯示名稱：Core 的 ModuleCatalog 只有中文，其他語言由資源檔以 key 對應 ──

    public static string ModuleLabel(ModuleSchema module) => TryGet($"Module.{module.Key}") ?? module.Label;

    /// <summary>先找 <c>Param.模組.參數</c>，再找通用的 <c>Param.參數</c>（如 enabled、amount）。</summary>
    public static string ParamLabel(string moduleKey, ParamDef def) =>
        TryGet($"Param.{moduleKey}.{def.Key}") ?? TryGet($"Param.{def.Key}") ?? def.Label;

    public static string ChoiceLabel(string moduleKey, string paramKey, ChoiceOption option)
    {
        var key = option.Key.Length == 0 ? "default" : option.Key;
        return TryGet($"Choice.{moduleKey}.{paramKey}.{key}") ?? TryGet($"Choice.{paramKey}.{key}") ?? option.Label;
    }

    public static string GroupLabel(ModuleGroup group) => TryGet($"Group.{group}") ?? ModuleSchema.GroupLabel(group);

    public static string PasteCategoryLabel(PasteCategory category) =>
        TryGet($"PasteCategory.{category}") ?? ModuleSchema.PasteCategoryLabel(category);

    /// <summary>
    /// 切換語言時呼叫 <paramref name="refresh"/>。以弱參考持有 <paramref name="target"/>，
    /// 不會讓短命的 ViewModel（對話框、選項）因靜態事件而無法回收。
    /// </summary>
    public static void OnCultureChanged<T>(T target, Action<T> refresh) where T : class
    {
        var weak = new WeakReference<T>(target);
        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.PropertyName != nameof(Culture)) return;
            if (weak.TryGetTarget(out var t)) refresh(t);
            else Instance.PropertyChanged -= handler;
        };
        Instance.PropertyChanged += handler;
    }

    /// <summary>目前語言的字串，切換語言時推送新值。</summary>
    public IObservable<string> Observe(string key) => new KeyObservable(this, key);

    private sealed class KeyObservable(Loc loc, string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            observer.OnNext(loc[key]);
            PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName == nameof(Culture)) observer.OnNext(loc[key]);
            };
            loc.PropertyChanged += handler;
            return new Unsubscriber(() => loc.PropertyChanged -= handler);
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    public static string Format(string key, params object[] args) => string.Format(Instance.Culture, Instance[key], args);
}
