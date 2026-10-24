using Avalonia;
using Avalonia.Markup.Xaml;

namespace FilmBatch.App.Localization;

/// <summary>
/// <c>{l:Tr Menu.File}</c>：繫結到 <see cref="Loc"/>，切換語言時即時更新。
/// 以 IObservable 建立繫結（不使用反射繫結），對 Trimming 安全。
/// </summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Instance.Observe(Key).ToBinding();
}
