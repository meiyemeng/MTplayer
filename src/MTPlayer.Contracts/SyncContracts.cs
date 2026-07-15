using System.Text.Json;
using System.Text.Json.Serialization;

namespace MTPlayer.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<SyncEntityKind>))]
public enum SyncEntityKind
{
    ConfigurationGroup = 0,
    Favorite = 1,
    PlaybackHistory = 2,
    SkipMarker = 3,
    Preference = 4,
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
    string? ErrorCode,
    SyncMutation? Current = null);

public sealed record SyncPullResponse(long Cursor, IReadOnlyList<SyncMutation> Changes);
