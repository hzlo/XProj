using System.Collections.Concurrent;
using System.Text;

namespace ProjectManager.Wpf.Infrastructure;

public sealed class AsyncLogFileWriter : IAsyncDisposable
{
    private readonly string _directory;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    public AsyncLogFileWriter(string directory)
    {
        _directory = directory;
        _worker = Task.Run(ProcessAsync);
    }

    public void Enqueue(Guid commandId, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        _pending.Enqueue(new LogEntry(commandId, text));
        _signal.Release();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _signal.Release();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _signal.Dispose();
        _shutdown.Dispose();
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            var batch = DrainPending();
            if (batch.Count > 0)
            {
                await WriteBatchAsync(batch).ConfigureAwait(false);
            }

            if (_shutdown.IsCancellationRequested)
            {
                var remaining = DrainPending();
                if (remaining.Count > 0)
                {
                    await WriteBatchAsync(remaining).ConfigureAwait(false);
                }

                return;
            }
        }
    }

    private List<LogEntry> DrainPending()
    {
        var entries = new List<LogEntry>();
        while (_pending.TryDequeue(out var entry))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private async Task WriteBatchAsync(IReadOnlyList<LogEntry> entries)
    {
        Directory.CreateDirectory(_directory);
        foreach (var group in entries.GroupBy(item => item.CommandId))
        {
            var path = Path.Combine(_directory, $"command-{group.Key:N}.log");
            var text = string.Concat(group.Select(item => item.Text));
            await File.AppendAllTextAsync(path, text, Encoding.UTF8).ConfigureAwait(false);
        }
    }

    private sealed record LogEntry(Guid CommandId, string Text);
}
