using System.Collections.Concurrent;

namespace FilmBatch.Raw;

/// <summary>
/// Background-only local budget for RAW parallel loops. Unscoped code keeps the default
/// unlimited options; a scope limits only the current execution context (AsyncLocal) to at
/// most <c>maximumParallelism</c> workers. Carries no cancellation token and never changes
/// pixels, masks, lens/color behavior, cache identity, or global ThreadPool settings.
/// </summary>
/// <remarks>
/// Synchronous use only: construct inside the background <c>Task.Run</c>, dispose on the same
/// flow to restore the previous options. Nested scopes never raise an outer limit
/// (<c>Math.Min</c>). Cached <see cref="ParallelOptions"/> instances are immutable in practice
/// and must not be mutated; <see cref="Options"/> never allocates per loop.
/// </remarks>
public readonly struct RawProcessingScope : IDisposable
{
    private static readonly ParallelOptions Unscoped = new();
    private static readonly AsyncLocal<ParallelOptions?> Current = new();
    private static readonly ConcurrentDictionary<int, ParallelOptions> Cached = new();
    private const int MaxCachedLimit = 256;

    private readonly ParallelOptions? _previous;
    private readonly bool _active;

    /// <summary>Limits RAW loops on this execution context to at most <paramref name="maximumParallelism"/> workers.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumParallelism"/> is less than 1.</exception>
    public RawProcessingScope(int maximumParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumParallelism, 1);
        var previous = Current.Value;
        int effective;
        if (previous is null || previous.MaxDegreeOfParallelism < 1)
            effective = maximumParallelism;
        else
            effective = Math.Min(previous.MaxDegreeOfParallelism, maximumParallelism);
        Current.Value = ForLimit(effective);
        _previous = previous;
        _active = true;
    }

    /// <summary>Current context-local RAW loop options: scoped limit or default unlimited. Never allocates.</summary>
    internal static ParallelOptions Options => Current.Value ?? Unscoped;

    private static ParallelOptions ForLimit(int limit)
    {
        if (limit > MaxCachedLimit)
            return new ParallelOptions { MaxDegreeOfParallelism = limit };
        return Cached.GetOrAdd(limit, static l => new ParallelOptions { MaxDegreeOfParallelism = l });
    }

    /// <summary>Restores the previous context-local options.</summary>
    public void Dispose()
    {
        if (!_active)
            return;
        Current.Value = _previous;
    }
}
