using System.Text.Json;

namespace MTPlayer.Contracts;

public enum SyncEntityKind
{
    ConfigurationGroup,
    Favorite,
    PlaybackHistory,
    SkipMarker,
    Preference,
}

public sealed record SyncMutation(
    Guid Id,
    SyncEntityKind Kind,
    long BaseVersion,
    DateTimeOffset ModifiedAtUtc,
    bool IsDeleted,
    JsonElement Payload);

public sealed record SyncPushRequest(Guid DeviceId, IReadOnlyList<SyncMutation> Mutations);

public sealed record SyncPushResult(
    Guid Id,
    long Version,
    DateTimeOffset ServerModifiedAtUtc,
    bool Accepted,
    string? ErrorCode);

public sealed record SyncPullResponse(long Cursor, IReadOnlyList<SyncMutation> Changes);
