using System.ComponentModel;
using System.Globalization;

namespace FilmBatch.App.Localization;

/// <summary>
/// 介面字串的執行期存取（計畫書 2.10）。XAML 以 <c>{l:Tr Key}</c> 繫結到索引子，切換語言時即時更新；
/// ViewModel 可用強型別的 <see cref="Strings"/> 或 <see cref="Format"/>。
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static readonly IReadOnlyList<(string Code, string Name)> Languages = [("zh-TW", "繁體中文"), ("en", "English")];

    private CultureInfo _culture = CultureInfo.GetCultureInfo("zh-TW");

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (Equals(_culture, value)) return;
            _culture = value;
            Strings.Culture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        }
    }

    public string this[string key] => Strings.ResourceManager.GetString(key, _culture) ?? $"[{key}]";

    public static string Get(string key) => Instance[key];

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
