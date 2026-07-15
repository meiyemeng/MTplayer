using MTPlayer.Client.Core.Library;
using MTPlayer.Client.Core.Sync;
using System.IO;

namespace WebHtv.Desktop;

internal sealed record FavoriteEntry(
    string SourceKey,
    string Id,
    string Category,
    string Title,
    string Caption,
    string CoverUrl,
    DateTimeOffset AddedAtUtc);

internal sealed record HistoryEntry(
    string SourceKey,
    string Id,
    string Category,
    string Title,
    string Caption,
    string CoverUrl,
    int SourceIndex,
    int EpisodeIndex,
    long PositionMs,
    long DurationMs,
    DateTimeOffset WatchedAtUtc);

internal sealed record SkipMarker(string SourceKey, string Id, long IntroEndMs, long OutroStartMs);

internal sealed class LibraryDocument
{
    public List<FavoriteEntry> Favorites { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    public List<SkipMarker> SkipMarkers { get; set; } = [];
}

internal sealed class LibraryStore : IDisposable
{
    private readonly JsonLibraryStore _store;
    private readonly SyncQueueStore _queue;

    public LibraryStore(string filePath)
    {
        _store = new JsonLibraryStore(filePath);
        _queue = new SyncQueueStore(Path.Combine(Path.GetDirectoryName(filePath)!, "sync-queue.json"));
    }

    public async Task<LibraryDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        var library = await _store.LoadAsync(cancellationToken);
        return new LibraryDocument
        {
            Favorites = library.Favorites.Where(item => !item.IsDeleted).Select(item => new FavoriteEntry(
                item.SourceKey,
                item.ContentId,
                item.Category,
                item.Title,
                item.Caption,
                item.CoverUrl,
                item.ModifiedAtUtc)).ToList(),
            History = library.PlaybackHistory.Where(item => !item.IsDeleted).Select(item => new HistoryEntry(
                item.SourceKey,
                item.ContentId,
                item.Category,
                item.Title,
                item.Caption,
                item.CoverUrl,
                item.SourceIndex,
                item.EpisodeIndex,
                item.PositionMs,
                item.DurationMs,
                item.WatchedAtUtc)).ToList(),
        };
    }

    public async Task<bool> ToggleFavoriteAsync(PosterCard card, CancellationToken cancellationToken = default)
    {
        var added = await _store.ToggleFavoriteAsync(new FavoriteRecord(
            JsonLibraryStore.StableId("favorite", card.SourceKey, card.Id),
            card.SourceKey,
            card.Id,
            card.Category,
            card.Title,
            card.Caption,
            card.CoverUrl,
            DateTimeOffset.UtcNow), cancellationToken);
        var stored = (await _store.LoadAsync(cancellationToken)).Favorites.Single(item =>
            item.SourceKey == card.SourceKey && item.ContentId == card.Id);
        await _queue.EnqueueAsync(SyncMapper.ToMutation(stored), cancellationToken);
        return added;
    }

    public async Task SaveHistoryAsync(
        PosterCard card,
        int sourceIndex,
        int episodeIndex,
        long positionMs,
        long durationMs,
        CancellationToken cancellationToken = default)
    {
        await _store.SavePlaybackAsync(new PlaybackRecord(
            JsonLibraryStore.StableId("playback", card.SourceKey, card.Id),
            card.SourceKey,
            card.Id,
            card.SourceKey,
            $"source-index:{Math.Max(0, sourceIndex)}",
            Math.Max(0, episodeIndex),
            Math.Max(0, positionMs),
            Math.Max(0, durationMs),
            DateTimeOffset.UtcNow)
        {
            SourceIndex = Math.Max(0, sourceIndex),
            Category = card.Category,
            Title = card.Title,
            Caption = card.Caption,
            CoverUrl = card.CoverUrl,
        }, cancellationToken: cancellationToken);
        var stored = (await _store.LoadAsync(cancellationToken)).PlaybackHistory.First(item =>
            item.SourceKey == card.SourceKey && item.ContentId == card.Id);
        await _queue.EnqueueAsync(SyncMapper.ToMutation(stored), cancellationToken);
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _store.ClearPlaybackHistoryAsync(cancellationToken);
        var deleted = (await _store.LoadAsync(cancellationToken)).PlaybackHistory.Where(item => item.IsDeleted);
        foreach (var item in deleted)
        {
            await _queue.EnqueueAsync(SyncMapper.ToMutation(item), cancellationToken);
        }
    }

    public async Task<SkipMarker?> GetSkipMarkerAsync(
        string sourceKey,
        string id,
        string lineName,
        CancellationToken cancellationToken = default)
    {
        var library = await _store.LoadAsync(cancellationToken);
        var marker = library.SkipMarkers
            .Where(item => !item.IsDeleted && item.SourceKey == sourceKey && item.ContentId == id)
            .OrderByDescending(item => item.LineName == lineName)
            .ThenByDescending(item => item.ModifiedAtUtc)
            .FirstOrDefault();
        if (marker is null)
        {
            return null;
        }

        var duration = library.PlaybackHistory
            .Where(item => !item.IsDeleted && item.SourceKey == sourceKey && item.ContentId == id)
            .OrderByDescending(item => item.WatchedAtUtc)
            .Select(item => item.DurationMs)
            .FirstOrDefault();
        var outroStart = marker.OutroRemainingSeconds > 0 && duration > 0
            ? Math.Max(0, duration - marker.OutroRemainingSeconds * 1000L)
            : 0;
        return new SkipMarker(sourceKey, id, marker.IntroEndSeconds * 1000L, outroStart);
    }

    public async Task SaveSkipMarkerAsync(
        string sourceKey,
        string id,
        string lineName,
        long introEndMs,
        long outroStartMs,
        long durationMs,
        CancellationToken cancellationToken = default)
    {
        SkipMarkerRecord? marker = null;
        if (introEndMs > 0 || outroStartMs > 0)
        {
            var remaining = outroStartMs > 0 && durationMs > outroStartMs
                ? (int)Math.Clamp((durationMs - outroStartMs + 999) / 1000, 0, int.MaxValue)
                : 0;
            marker = new SkipMarkerRecord(
                JsonLibraryStore.StableId("skip", sourceKey, id, sourceKey, lineName),
                sourceKey,
                id,
                sourceKey,
                lineName,
                (int)Math.Clamp(introEndMs / 1000, 0, int.MaxValue),
                remaining,
                DateTimeOffset.UtcNow);
        }

        await _store.SaveSkipMarkerAsync(marker, sourceKey, id, sourceKey, lineName, cancellationToken);
        var stored = (await _store.LoadAsync(cancellationToken)).SkipMarkers.SingleOrDefault(item =>
            item.SourceKey == sourceKey && item.ContentId == id &&
            item.InterfaceKey == sourceKey && item.LineName == lineName);
        if (stored is not null)
        {
            await _queue.EnqueueAsync(SyncMapper.ToMutation(stored), cancellationToken);
        }
    }

    public void Dispose()
    {
        _store.Dispose();
        _queue.Dispose();
    }
}
