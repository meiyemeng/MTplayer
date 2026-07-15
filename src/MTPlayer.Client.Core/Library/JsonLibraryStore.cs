using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MTPlayer.Client.Core.Library;

public sealed class JsonLibraryStore(string filePath) : ILibraryStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public async Task<LibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default)
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

    public async Task SaveAsync(LibrarySnapshot library, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(Normalize(library), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ToggleFavoriteAsync(
        FavoriteRecord favorite,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var library = await LoadCoreAsync(cancellationToken);
            var existing = library.Favorites.FindIndex(item =>
                item.SourceKey == favorite.SourceKey && item.ContentId == favorite.ContentId && !item.IsDeleted);
            if (existing >= 0)
            {
                library.Favorites.RemoveAt(existing);
            }
            else
            {
                library.Favorites.Insert(0, favorite);
            }

            await SaveCoreAsync(library, cancellationToken);
            return existing < 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePlaybackAsync(
        PlaybackRecord playback,
        int maximumHistory = 200,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var library = await LoadCoreAsync(cancellationToken);
            library.PlaybackHistory.RemoveAll(item =>
                item.SourceKey == playback.SourceKey && item.ContentId == playback.ContentId);
            library.PlaybackHistory.Insert(0, playback);
            if (library.PlaybackHistory.Count > maximumHistory)
            {
                library.PlaybackHistory.RemoveRange(maximumHistory, library.PlaybackHistory.Count - maximumHistory);
            }

            await SaveCoreAsync(library, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearPlaybackHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var library = await LoadCoreAsync(cancellationToken);
            library.PlaybackHistory.Clear();
            await SaveCoreAsync(library, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSkipMarkerAsync(
        SkipMarkerRecord? marker,
        string sourceKey,
        string contentId,
        string interfaceKey,
        string lineName,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var library = await LoadCoreAsync(cancellationToken);
            library.SkipMarkers.RemoveAll(item =>
                item.SourceKey == sourceKey && item.ContentId == contentId &&
                item.InterfaceKey == interfaceKey && item.LineName == lineName);
            if (marker is not null)
            {
                library.SkipMarkers.Add(marker);
            }

            await SaveCoreAsync(library, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static Guid StableId(string kind, params string[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{string.Join('\n', values)}"));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private async Task<LibrarySnapshot> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new LibrarySnapshot();
        }

        try
        {
            await using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) &&
                schemaVersion.TryGetInt32(out var version) && version >= 2)
            {
                return Normalize(document.RootElement.Deserialize<LibrarySnapshot>(JsonOptions) ?? new LibrarySnapshot());
            }

            return MigrateLegacy(document.RootElement);
        }
        catch (JsonException)
        {
            return new LibrarySnapshot();
        }
        catch (IOException)
        {
            return new LibrarySnapshot();
        }
    }

    private async Task SaveCoreAsync(LibrarySnapshot library, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath) ??
            throw new InvalidOperationException("Library file must have a directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, Normalize(library), JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static LibrarySnapshot Normalize(LibrarySnapshot library)
    {
        library.SchemaVersion = 2;
        library.Favorites ??= [];
        library.PlaybackHistory ??= [];
        library.SkipMarkers ??= [];
        return library;
    }

    private static LibrarySnapshot MigrateLegacy(JsonElement root)
    {
        var legacy = root.Deserialize<LegacyLibrary>(JsonOptions) ?? new LegacyLibrary();
        var migrated = new LibrarySnapshot();
        foreach (var favorite in legacy.Favorites)
        {
            migrated.Favorites.Add(new FavoriteRecord(
                StableId("favorite", favorite.SourceKey, favorite.Id),
                favorite.SourceKey,
                favorite.Id,
                favorite.Category,
                favorite.Title,
                favorite.Caption,
                favorite.CoverUrl,
                favorite.AddedAtUtc == default ? DateTimeOffset.UnixEpoch : favorite.AddedAtUtc));
        }

        foreach (var history in legacy.History)
        {
            migrated.PlaybackHistory.Add(new PlaybackRecord(
                StableId("playback", history.SourceKey, history.Id),
                history.SourceKey,
                history.Id,
                $"source-index:{Math.Max(0, history.SourceIndex)}",
                string.Empty,
                Math.Max(0, history.EpisodeIndex),
                Math.Max(0, history.PositionMs),
                Math.Max(0, history.DurationMs),
                history.WatchedAtUtc == default ? DateTimeOffset.UnixEpoch : history.WatchedAtUtc)
            {
                SourceIndex = Math.Max(0, history.SourceIndex),
                Category = history.Category,
                Title = history.Title,
                Caption = history.Caption,
                CoverUrl = history.CoverUrl,
            });
        }

        foreach (var marker in legacy.SkipMarkers)
        {
            migrated.SkipMarkers.Add(new SkipMarkerRecord(
                StableId("skip", marker.SourceKey, marker.Id, string.Empty, string.Empty),
                marker.SourceKey,
                marker.Id,
                string.Empty,
                string.Empty,
                (int)Math.Clamp(marker.IntroEndMs / 1000, 0, int.MaxValue),
                0,
                DateTimeOffset.UnixEpoch));
            migrated.RequiresDurationRepair |= marker.OutroStartMs > 0;
        }

        return migrated;
    }

    public void Dispose() => _gate.Dispose();

    private sealed class LegacyLibrary
    {
        public List<LegacyFavorite> Favorites { get; set; } = [];
        public List<LegacyHistory> History { get; set; } = [];
        public List<LegacySkipMarker> SkipMarkers { get; set; } = [];
    }

    private sealed record LegacyFavorite(
        string SourceKey,
        string Id,
        string Category,
        string Title,
        string Caption,
        string CoverUrl,
        DateTimeOffset AddedAtUtc);

    private sealed record LegacyHistory(
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

    private sealed record LegacySkipMarker(string SourceKey, string Id, long IntroEndMs, long OutroStartMs);
}
