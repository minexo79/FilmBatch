namespace FilmBatch.Mobile.iOS;

// Single in-flight run coordinator shared by the UIKit controller and the file-based
// autorun entry. A cancelled epoch never completes successfully, so a late-finished
// report is discarded instead of being accepted as a fresh result.
internal sealed class RunEpoch
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private bool _completed;
    private bool _disposed;

    internal RunEpoch(int id) => Id = id;

    internal int Id { get; }
    internal CancellationToken Token => _cancellation.Token;
    internal bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    // Only the first consumer-visible completion wins; cancelled or superseded epochs
    // return false and their report must be dropped by the caller.
    internal bool TryComplete()
    {
        lock (_gate)
            return !_completed && !_disposed && !_cancellation.IsCancellationRequested && (_completed = true);
    }

    internal void Cancel() => _cancellation.Cancel();

    internal void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
