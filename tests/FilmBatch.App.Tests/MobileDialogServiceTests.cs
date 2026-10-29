using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FilmBatch.App.Controls;
using FilmBatch.Core.Film;
using FilmBatch.Mobile;
using FilmBatch.Raw.Color;

namespace FilmBatch.App.Tests;

public sealed class MobileDialogServiceTests
{
    [AvaloniaFact]
    public async Task ColorDeclaration_ConfirmReturnsChosenValues_AndCancellationClosesOverlay()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <ProcessList id="mobile" compCLFversion="3.0">
              <InputDescriptor>Linear camera</InputDescriptor>
              <Matrix inBitDepth="32f" outBitDepth="32f"><Array dim="3 3">1 0 0 0 1 0 0 0 1</Array></Matrix>
            </ProcessList>
            """));
        var transform = ClfFile.Read(stream, TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "filmbatch-mobile-dialog-" + Guid.NewGuid().ToString("N"));
        var overlay = new Border { IsVisible = false };
        var window = new Window { Width = 400, Height = 600, Content = overlay };
        window.Show();
        try
        {
            var service = new MobileDialogService(() => window, overlay, new MobileWorkspace(root));
            var confirm = service.ShowColorTransformImportAsync(transform, TestContext.Current.CancellationToken);
            window.CaptureRenderedFrame()?.Dispose();
            var panel = Assert.Single(window.GetVisualDescendants().OfType<ColorTransformImportPanel>());
            Assert.Contains(panel.Children.OfType<TextBlock>(), text => text.Text?.Contains("Linear camera", StringComparison.Ordinal) == true);
            var choices = panel.Children.OfType<ComboBox>().ToArray();
            choices[0].SelectedIndex = (int)ColorTransformStage.WorkingLinear;
            choices[1].SelectedIndex = (int)ColorTransformEncoding.LinearSrgb;
            choices[2].SelectedIndex = (int)ColorTransformEncoding.ProPhotoGamma18;
            Invoke(window, "Mobile.ModalChoice0");
            var result = await confirm.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(new ClfImportOptions(ColorTransformStage.WorkingLinear, ColorTransformEncoding.LinearSrgb,
                ColorTransformEncoding.ProPhotoGamma18), result);
            Assert.False(overlay.IsVisible);

            var pending = service.ShowColorTransformImportAsync(transform, TestContext.Current.CancellationToken);
            Assert.True(overlay.IsVisible);
            service.CancelPending();
            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(overlay.IsVisible);
            Assert.Null(overlay.Child);
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task CameraConfirmation_ShowsModel_AndCancellationDoesNotConfirm()
    {
        var profile = DcpProfile.Read(SyntheticProfile(), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "filmbatch-mobile-dialog-" + Guid.NewGuid().ToString("N"));
        var overlay = new Border { IsVisible = false };
        var window = new Window { Width = 400, Height = 600, Content = overlay };
        window.Show();
        try
        {
            var service = new MobileDialogService(() => window, overlay, new MobileWorkspace(root));
            using var cancellation = new CancellationTokenSource();
            var confirmation = service.ShowCameraProfileImportAsync(profile, cancellation.Token);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("FilmBatch RAW controls", StringComparison.Ordinal) == true);
            cancellation.Cancel();
            Assert.False(await confirmation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(overlay.IsVisible);
            Assert.Null(overlay.Child);

            var accepted = service.ShowCameraProfileImportAsync(profile, TestContext.Current.CancellationToken);
            window.CaptureRenderedFrame()?.Dispose();
            Invoke(window, "Mobile.ModalChoice0");
            Assert.True(await accepted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(overlay.IsVisible);
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Invoke(Window window, string id)
    {
        var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), item => AutomationProperties.GetAutomationId(item) == id);
        Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(button)).Invoke();
    }

    private static byte[] SyntheticProfile()
    {
        var model = Encoding.UTF8.GetBytes("FilmBatch RAW controls\0");
        const uint data = 8 + 2 + 3 * 12 + 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)0x4352); writer.Write(8u);
        writer.Write((ushort)3);
        void Entry(ushort tag, ushort type, uint count, uint value) { writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value); }
        Entry(50708, 2, (uint)model.Length, data);
        Entry(50721, 10, 9, data + (uint)model.Length);
        Entry(50778, 3, 1, 21);
        writer.Write(0u); writer.Write(model);
        for (var i = 0; i < 9; i++) { writer.Write(i % 4 == 0 ? 1 : 0); writer.Write(1); }
        writer.Flush();
        return stream.ToArray();
    }
}
