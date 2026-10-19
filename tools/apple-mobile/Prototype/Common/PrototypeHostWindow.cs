using Avalonia.Controls;

namespace FilmBatch.Mobile.Prototype;

// Desktop companion shell for the same view; resizable for small and large layout checks.
public sealed class PrototypeHostWindow : Window
{
    public PrototypeHostWindow(PrototypeView view)
    {
        Title = "FilmBatch Mobile Prototype (diagnostic, not the editor)";
        Width = PrototypeHostOptions.WindowWidth;
        Height = PrototypeHostOptions.WindowHeight;
        MinWidth = 360;
        MinHeight = 520;
        Content = view;
    }

    public static class PrototypeHostOptions
    {
        public static double WindowWidth { get; set; } = 1024;
        public static double WindowHeight { get; set; } = 720;
    }
}
