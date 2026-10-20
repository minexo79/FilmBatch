using Foundation;
using PhotosUI;
using UIKit;
using UniformTypeIdentifiers;

namespace FilmBatch.Mobile.iOS;

/// <summary>Imports only user-selected Photos representations without library-wide permission.</summary>
internal static class NativePhotosPicker
{
    internal static async Task<IReadOnlyList<string>> PickAsync(string incoming, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var presenter = UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>()
            .Where(scene => scene.ActivationState == UISceneActivationState.ForegroundActive)
            .SelectMany(scene => scene.Windows).FirstOrDefault(window => window.IsKeyWindow)?.RootViewController
            ?? throw new InvalidOperationException("There is no active iOS window.");
        while (presenter.PresentedViewController is { } presented) presenter = presented;
        using var configuration = new PHPickerConfiguration
        {
            Filter = PHPickerFilter.ImagesFilter, SelectionLimit = 0,
            PreferredAssetRepresentationMode = PHPickerConfigurationAssetRepresentationMode.Current,
        };
        using var picker = new PHPickerViewController(configuration);
        var completion = new TaskCompletionSource<PHPickerResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SelectionHandler(completion);
        picker.Delegate = handler;
        using var cancellation = cancellationToken.Register(() => UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            picker.DismissViewController(true, null);
            completion.TrySetCanceled(cancellationToken);
        }));
        presenter.PresentViewController(picker, true, null);
        var selected = await completion.Task;
        var result = new List<string>();
        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(await CopyRepresentationAsync(item.ItemProvider, incoming, cancellationToken));
        }
        return result;
    }

    private static async Task<string> CopyRepresentationAsync(NSItemProvider provider, string incoming,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var folder = Path.Combine(incoming, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        // The representation URL expires when this callback returns. Copy synchronously
        // inside the provider's callback; never hand the temporary external URL to Core.
        using var progress = provider.LoadFileRepresentation(UTTypes.Image.Identifier, (url, error) =>
        {
            string? destination = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (error is not null) throw new IOException(error.LocalizedDescription);
                if (url?.Path is not { } source) throw new IOException("Photos did not supply a file representation.");
                var extension = Path.GetExtension(source);
                var suggested = provider.SuggestedName;
                var stem = string.IsNullOrWhiteSpace(suggested) ? "Photo" : Path.GetFileNameWithoutExtension(suggested);
                foreach (var character in Path.GetInvalidFileNameChars()) stem = stem.Replace(character, '_');
                stem = stem.Replace('/', '_').Replace('\\', '_');
                if (string.IsNullOrWhiteSpace(stem) || stem is "." or "..") stem = "Photo";
                destination = Path.Combine(folder, stem + extension);
                using (var input = File.OpenRead(source))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Span<byte> buffer = stackalloc byte[65536];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = input.Read(buffer);
                        if (count == 0) break;
                        output.Write(buffer[..count]);
                    }
                    output.Flush();
                }
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(destination);
            }
            catch (OperationCanceledException) { if (destination is not null && File.Exists(destination)) File.Delete(destination); completion.TrySetCanceled(cancellationToken); }
            catch (Exception failure) { if (destination is not null && File.Exists(destination)) File.Delete(destination); completion.TrySetException(failure); }
        });
        // Wait for the provider callback even on cancellation so its temporary resource
        // and the managed provider stay alive until the callback has finished.
        return await completion.Task;
    }

    private sealed class SelectionHandler(TaskCompletionSource<PHPickerResult[]> completion) : PHPickerViewControllerDelegate
    {
        public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
        {
            picker.DismissViewController(true, null);
            completion.TrySetResult(results);
        }
    }
}
