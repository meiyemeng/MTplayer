namespace MTPlayer.Client.Core.Library;

public sealed record FavoriteRecord(
    Guid Id,
    string SourceKey,
    string ContentId,
    string Category,
    string Title,
    string Caption,
    string CoverUrl,
    DateTimeOffset ModifiedAtUtc,
    long Version = 0,
    bool IsDeleted = false);

public sealed record PlaybackRecord(
    Guid Id,
    string SourceKey,
    string ContentId,
    string InterfaceKey,
    string LineName,
    int EpisodeIndex,
    long PositionMs,
    long DurationMs,
    DateTimeOffset WatchedAtUtc,
    long Version = 0,
    bool IsDeleted = false)
{
    public int SourceIndex { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public string CoverUrl { get; init; } = string.Empty;
}

public sealed record SkipMarkerRecord(
    Guid Id,
    string SourceKey,
    string ContentId,
    string InterfaceKey,
    string LineName,
    int IntroEndSeconds,
    int OutroRemainingSeconds,
    DateTimeOffset ModifiedAtUtc,
    long Version = 0,
    bool IsDeleted = false);

public sealed record ConfigurationGroupRecord(
    Guid Id,
    string Name,
    string Address,
    bool IsEnabled,
    DateTimeOffset ModifiedAtUtc,
    long Version = 0,
    bool IsDeleted = false);

public sealed class LibrarySnapshot
{
    public int SchemaVersion { get; set; } = 2;
    public List<FavoriteRecord> Favorites { get; set; } = [];
    public List<PlaybackRecord> PlaybackHistory { get; set; } = [];
    public List<SkipMarkerRecord> SkipMarkers { get; set; } = [];
    public bool RequiresDurationRepair { get; set; }
}

public interface ILibraryStore
{
    Task<LibrarySnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LibrarySnapshot library, CancellationToken cancellationToken = default);
}
