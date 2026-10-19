using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using Foundation;
using UIKit;

namespace FilmBatch.Mobile.iOS;

/// <summary>Hosts the real editor in Avalonia's native scene-based iOS lifetime.</summary>
[Register("FilmBatchAppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<MobileApp>
{
    /// <summary>Initializes a new instance of the <see cref="AppDelegate"/> class.</summary>
    public AppDelegate()
    {
        var lifetime = (IAvaloniaAppDelegate)this;
        lifetime.Deactivated += (_, args) =>
        {
            if (args.Kind == ActivationKind.Background) Editor?.Suspend();
        };
        lifetime.Activated += (_, _) => Editor?.Resume();
    }

    private static MobileEditorView? Editor =>
        (Application.Current?.ApplicationLifetime as ISingleViewApplicationLifetime)?.MainView as MobileEditorView;

    /// <inheritdoc/>
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        MobileApp.WorkspaceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FilmBatch");
        MobileApp.PhotosPicker = NativePhotosPicker.PickAsync;
        return base.CustomizeAppBuilder(builder);
    }
}

/// <summary>Starts the native iOS application rather than the diagnostic probe host.</summary>
public static class Program
{
    /// <summary>Starts UIKit with FilmBatch's scene-aware application delegate.</summary>
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
