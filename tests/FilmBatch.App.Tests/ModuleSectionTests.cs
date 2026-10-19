using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;

namespace FilmBatch.App.Tests;

public sealed class ModuleSectionTests
{
    [AvaloniaFact]
    public void TouchingTitle_OpensAndClosesParameterContent()
    {
        var section = new ModuleSection { Header = "Basic adjustments", Content = new TextBlock { Text = "Exposure" } };
        var window = new Window { Width = 360, Height = 300, Content = section };
        window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var title = section.GetVisualDescendants().OfType<Control>().Single(p => p.Name == "PART_Title");
            var point = title.TranslatePoint(new Point(title.Bounds.Width / 2, title.Bounds.Height / 2), window)!.Value;
            using (var touch = window.TouchBegin(point)) window.TouchEnd(touch, point);
            Dispatcher.UIThread.RunJobs();
            Assert.True(section.IsExpanded);
            using (var touch = window.TouchBegin(point)) window.TouchEnd(touch, point);
            Dispatcher.UIThread.RunJobs();
            Assert.False(section.IsExpanded);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AccessibleTitle_TogglesParameterContent()
    {
        var section = new ModuleSection { Header = "Basic adjustments", Content = new TextBlock { Text = "Exposure" } };
        var window = new Window { Width = 360, Height = 300, Content = section };
        window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var title = section.GetVisualDescendants().OfType<Control>().Single(p => p.Name == "PART_Title");
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(ControlAutomationPeer.CreatePeerForElement(title));
            toggle.Toggle();
            Dispatcher.UIThread.RunJobs();
            Assert.True(section.IsExpanded);
            toggle.Toggle();
            Dispatcher.UIThread.RunJobs();
            Assert.False(section.IsExpanded);
        }
        finally { window.Close(); }
    }
}
