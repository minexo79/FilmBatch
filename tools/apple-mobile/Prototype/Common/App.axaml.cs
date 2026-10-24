using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FilmBatch.Mobile.Probes;

namespace FilmBatch.Mobile.Prototype;

// Isolated PoV hosts only: the desktop companion and the iOS prototype share this App/view.
public sealed partial class PrototypeApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var options = RequestedOptions.Current;
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new PrototypeHostWindow(new PrototypeView(options, RequestedOptions.Error));
                break;
            case ISingleViewApplicationLifetime single:
                single.MainView = new PrototypeView(options, RequestedOptions.Error);
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }
    // Hosts hand real launch arguments to the shared view before the lifetime starts.
    public static class RequestedOptions
    {
        public static string? Error { get; set; }
        public static ProbeOptions Current { get; set; } =
            new(Path.Combine(Path.GetTempPath(), "filmbatch-prototype"));
    }
}
