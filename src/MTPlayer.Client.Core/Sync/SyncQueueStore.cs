using System.Text.Json;
using System.Collections.Concurrent;
using MTPlayer.Contracts;

namespace MTPlayer.Client.Core.Sync;

public sealed record SyncQueueItem(
    Guid QueueId,
    SyncMutation Mutation,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc);

public sealed class SyncQueueDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<SyncQueueItem> Items { get; set; } = [];
}

public sealed class SyncQueueStore(string filePath) : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private readonly SemaphoreSlim _gate = FileGates.GetOrAdd(
        Path.GetFullPath(filePath),
        _ => new SemaphoreSlim(1, 1));

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public async Task<SyncQueueDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnqueueAsync(SyncMutation mutation, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var queue = await LoadCoreAsync(cancellationToken);
            var index = queue.Items.FindIndex(item =>
                item.Mutation.Id == mutation.Id && item.Mutation.Kind == mutation.Kind);
            var item = new SyncQueueItem(
                index >= 0 ? queue.Items[index].QueueId : Guid.NewGuid(),
                mutation,
                0,
                DateTimeOffset.UnixEpoch);
            if (index >= 0)
            {
                queue.Items[index] = item;
            }
            else
            {
                queue.Items.Add(item);
            }

            await SaveCoreAsync(queue, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SyncQueueDocument queue, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(queue, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncQueueDocument> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new SyncQueueDocument();
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var queue = await JsonSerializer.DeserializeAsync<SyncQueueDocument>(stream, JsonOptions, cancellationToken) ??
                new SyncQueueDocument();
            queue.Items ??= [];
            return queue;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            PreserveCorruptFile();
            return new SyncQueueDocument();
        }
    }

    private async Task SaveCoreAsync(SyncQueueDocument queue, CancellationToken cancellationToken)
    {
        queue.SchemaVersion = 1;
        queue.Items ??= [];
        var directory = Path.GetDirectoryName(FilePath) ??
            throw new InvalidOperationException("Sync queue file must have a directory.");
        Directory.CreateDirectory(directory);
        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, queue, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void PreserveCorruptFile()
    {
        try
        {
            var backup = $"{FilePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(FilePath, backup, overwrite: false);
        }
        catch (IOException)
        {
            // Another caller may already have preserved the same corrupt queue.
        }
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
