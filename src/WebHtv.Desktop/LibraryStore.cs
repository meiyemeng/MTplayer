using System.Text.Json;
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

internal sealed class LibraryStore(string filePath) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath = filePath;

    public async Task<LibraryDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await LoadCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<bool> ToggleFavoriteAsync(PosterCard card, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            var existing = document.Favorites.FindIndex(item => item.SourceKey == card.SourceKey && item.Id == card.Id);
            if (existing >= 0) document.Favorites.RemoveAt(existing);
            else document.Favorites.Insert(0, new FavoriteEntry(card.SourceKey, card.Id, card.Category, card.Title, card.Caption, card.CoverUrl, DateTimeOffset.UtcNow));
            await SaveCoreAsync(document, cancellationToken);
            return existing < 0;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveHistoryAsync(PosterCard card, int sourceIndex, int episodeIndex, long positionMs, long durationMs, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            document.History.RemoveAll(item => item.SourceKey == card.SourceKey && item.Id == card.Id);
            document.History.Insert(0, new HistoryEntry(card.SourceKey, card.Id, card.Category, card.Title, card.Caption, card.CoverUrl, sourceIndex, episodeIndex, Math.Max(0, positionMs), Math.Max(0, durationMs), DateTimeOffset.UtcNow));
            if (document.History.Count > 200) document.History.RemoveRange(200, document.History.Count - 200);
            await SaveCoreAsync(document, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            document.History.Clear();
            await SaveCoreAsync(document, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<SkipMarker?> GetSkipMarkerAsync(string sourceKey, string id, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(cancellationToken);
        return document.SkipMarkers.FirstOrDefault(item => item.SourceKey == sourceKey && item.Id == id);
    }

    public async Task SaveSkipMarkerAsync(string sourceKey, string id, long introEndMs, long outroStartMs, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCoreAsync(cancellationToken);
            document.SkipMarkers.RemoveAll(item => item.SourceKey == sourceKey && item.Id == id);
            if (introEndMs > 0 || outroStartMs > 0)
                document.SkipMarkers.Add(new SkipMarker(sourceKey, id, Math.Max(0, introEndMs), Math.Max(0, outroStartMs)));
            await SaveCoreAsync(document, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<LibraryDocument> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new LibraryDocument();
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<LibraryDocument>(stream, JsonOptions, cancellationToken) ?? new LibraryDocument();
        }
        catch (JsonException) { return new LibraryDocument(); }
    }

    private async Task SaveCoreAsync(LibraryDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("媒体库文件没有目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, _filePath, true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public void Dispose() => _gate.Dispose();
}
