using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Shop.TestSupport;

public sealed record LogEntry(LogLevel Level, string Category, string Message, IReadOnlyDictionary<string, object?> State, Exception? Exception);

/// <summary>A small in-memory log collector (no extra package) that tests can also await without sleeping.</summary>
public sealed class LogSink
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];
    private TaskCompletionSource _changed = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public IReadOnlyList<LogEntry> Where(Func<LogEntry, bool> predicate) => Entries.Where(predicate).ToArray();

    public void Add(LogEntry entry)
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            _entries.Add(entry);
            previous = _changed;
            _changed = NewSignal();
        }
        previous.TrySetResult();
    }

    /// <summary>Completes when at least <paramref name="count"/> entries match; the timeout is only a failure guard, never a pacing sleep.</summary>
    public async Task<IReadOnlyList<LogEntry>> WaitForAsync(Func<LogEntry, bool> predicate, int count = 1, TimeSpan? guard = null)
    {
        using var cts = new CancellationTokenSource(guard ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var matches = _entries.Where(predicate).ToArray();
                if (matches.Length >= count) return matches;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cts.Token);
        }
    }
}

public sealed class CollectingLogger(LogSink sink, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(p => p.Key, p => p.Value)
            : new Dictionary<string, object?>();
        sink.Add(new LogEntry(logLevel, category, formatter(state, exception), values, exception));
    }
}

public sealed class CollectingLogger<T>(LogSink sink) : ILogger<T>
{
    private readonly CollectingLogger _inner = new(sink, typeof(T).FullName!);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}

public sealed class CollectingLoggerProvider(LogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CollectingLogger(sink, categoryName);
    public void Dispose()
    {
    }
}
