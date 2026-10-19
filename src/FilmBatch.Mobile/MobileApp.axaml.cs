using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FilmBatch.Core;

namespace FilmBatch.Mobile;

/// <summary>Bootstraps the shared mobile editor on an iOS surface or its desktop companion.</summary>
public sealed partial class MobileApp : Application
{
    /// <summary>Gets or sets the platform's owned documents directory before application initialization.</summary>
    public static string WorkspaceRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FilmBatch");

    /// <summary>Gets or sets the platform's permission-limited selected-Photos picker.</summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<string>>>? PhotosPicker { get; set; }

    /// <summary>Gets or sets the desktop companion's initial width.</summary>
    public static double DesktopWidth { get; set; } = 1080;
    /// <summary>Gets or sets the desktop companion's initial height.</summary>
    public static double DesktopHeight { get; set; } = 800;

    /// <inheritdoc/>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        // Preferences and preset writes never escape this platform host's owned workspace.
        AppPaths.OverrideRoot = Path.Combine(Path.GetFullPath(WorkspaceRoot), "ApplicationData");
        var view = new MobileEditorView(new MobileWorkspace(WorkspaceRoot), PhotosPicker);
        if (ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = view;
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window
            {
                Title = "FilmBatch Mobile", Content = view, MinWidth = 360, MinHeight = 600,
                Width = DesktopWidth, Height = DesktopHeight, DataContext = view.Main,
            };
            window.Closing += (_, _) => view.Main.SaveProject();
            window.Closed += (_, _) => view.Dispose();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
