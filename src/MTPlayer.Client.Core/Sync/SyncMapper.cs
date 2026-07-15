using System.Text.Json;
using MTPlayer.Client.Core.Library;
using MTPlayer.Client.Core.Settings;
using MTPlayer.Contracts;

namespace MTPlayer.Client.Core.Sync;

public static class SyncMapper
{
    public static SyncMutation ToMutation(FavoriteRecord value) => new(
        value.Id,
        SyncEntityKind.Favorite,
        value.Version,
        value.ModifiedAtUtc.ToUniversalTime(),
        value.IsDeleted,
        JsonSerializer.SerializeToElement(new
        {
            sourceKey = value.SourceKey,
            contentId = value.ContentId,
            category = value.Category,
            title = value.Title,
            caption = value.Caption,
            coverUrl = value.CoverUrl,
        }));

    public static SyncMutation ToMutation(PlaybackRecord value) => new(
        value.Id,
        SyncEntityKind.PlaybackHistory,
        value.Version,
        value.WatchedAtUtc.ToUniversalTime(),
        value.IsDeleted,
        JsonSerializer.SerializeToElement(new
        {
            sourceKey = value.SourceKey,
            contentId = value.ContentId,
            interfaceKey = value.InterfaceKey,
            lineName = value.LineName,
            episodeIndex = value.EpisodeIndex,
            positionMs = value.PositionMs,
            durationMs = value.DurationMs,
            sourceIndex = value.SourceIndex,
            category = value.Category,
            title = value.Title,
            caption = value.Caption,
            coverUrl = value.CoverUrl,
        }));

    public static SyncMutation ToMutation(SkipMarkerRecord value) => new(
        value.Id,
        SyncEntityKind.SkipMarker,
        value.Version,
        value.ModifiedAtUtc.ToUniversalTime(),
        value.IsDeleted,
        JsonSerializer.SerializeToElement(new
        {
            sourceKey = value.SourceKey,
            contentId = value.ContentId,
            interfaceKey = value.InterfaceKey,
            lineName = value.LineName,
            introEndSeconds = value.IntroEndSeconds,
            outroRemainingSeconds = value.OutroRemainingSeconds,
        }));

    public static SyncMutation ToMutation(ConfigurationGroupRecord value) => new(
        value.Id,
        SyncEntityKind.ConfigurationGroup,
        value.Version,
        value.ModifiedAtUtc.ToUniversalTime(),
        value.IsDeleted,
        JsonSerializer.SerializeToElement(new
        {
            name = value.Name,
            address = value.Address,
            isEnabled = value.IsEnabled,
        }));

    public static SyncMutation Preference(
        ClientSettings settings,
        string key,
        object value,
        DateTimeOffset modifiedAtUtc)
    {
        settings.PreferenceStates.TryGetValue(key, out var state);
        return new SyncMutation(
            JsonLibraryStore.StableId("preference", key),
            SyncEntityKind.Preference,
            state?.Version ?? 0,
            modifiedAtUtc.ToUniversalTime(),
            false,
            JsonSerializer.SerializeToElement(new { key, value }));
    }

    public static void Apply(LibrarySnapshot library, ClientSettings settings, SyncMutation mutation)
    {
        if (mutation.IsDeleted)
        {
            ApplyTombstone(library, settings, mutation);
            return;
        }

        switch (mutation.Kind)
        {
            case SyncEntityKind.Favorite:
                Upsert(library.Favorites, ReadFavorite(mutation), item => item.Id, item => item.Version);
                break;
            case SyncEntityKind.PlaybackHistory:
                Upsert(library.PlaybackHistory, ReadPlayback(mutation), item => item.Id, item => item.Version);
                break;
            case SyncEntityKind.SkipMarker:
                Upsert(library.SkipMarkers, ReadSkipMarker(mutation), item => item.Id, item => item.Version);
                break;
            case SyncEntityKind.ConfigurationGroup:
                Upsert(settings.ConfigurationGroups, ReadConfiguration(mutation), item => item.Id, item => item.Version);
                break;
            case SyncEntityKind.Preference:
                ApplyPreference(settings, mutation);
                break;
            default:
                throw new InvalidDataException($"Unsupported sync entity kind: {mutation.Kind}.");
        }
    }

    private static void ApplyTombstone(
        LibrarySnapshot library,
        ClientSettings settings,
        SyncMutation mutation)
    {
        switch (mutation.Kind)
        {
            case SyncEntityKind.Favorite:
                MarkDeleted(
                    library.Favorites,
                    mutation,
                    item => item.Id,
                    item => item.Version,
                    item => item with
                    {
                        Version = mutation.BaseVersion,
                        ModifiedAtUtc = mutation.ModifiedAtUtc,
                        IsDeleted = true,
                    },
                    () => new FavoriteRecord(
                        mutation.Id, string.Empty, string.Empty, string.Empty, string.Empty,
                        string.Empty, string.Empty, mutation.ModifiedAtUtc, mutation.BaseVersion, true));
                break;
            case SyncEntityKind.PlaybackHistory:
                MarkDeleted(
                    library.PlaybackHistory,
                    mutation,
                    item => item.Id,
                    item => item.Version,
                    item => item with
                    {
                        Version = mutation.BaseVersion,
                        WatchedAtUtc = mutation.ModifiedAtUtc,
                        IsDeleted = true,
                    },
                    () => new PlaybackRecord(
                        mutation.Id, string.Empty, string.Empty, string.Empty, string.Empty,
                        0, 0, 0, mutation.ModifiedAtUtc, mutation.BaseVersion, true));
                break;
            case SyncEntityKind.SkipMarker:
                MarkDeleted(
                    library.SkipMarkers,
                    mutation,
                    item => item.Id,
                    item => item.Version,
                    item => item with
                    {
                        Version = mutation.BaseVersion,
                        ModifiedAtUtc = mutation.ModifiedAtUtc,
                        IsDeleted = true,
                    },
                    () => new SkipMarkerRecord(
                        mutation.Id, string.Empty, string.Empty, string.Empty, string.Empty,
                        0, 0, mutation.ModifiedAtUtc, mutation.BaseVersion, true));
                break;
            case SyncEntityKind.ConfigurationGroup:
                MarkDeleted(
                    settings.ConfigurationGroups,
                    mutation,
                    item => item.Id,
                    item => item.Version,
                    item => item with
                    {
                        Version = mutation.BaseVersion,
                        ModifiedAtUtc = mutation.ModifiedAtUtc,
                        IsDeleted = true,
                    },
                    () => new ConfigurationGroupRecord(
                        mutation.Id, string.Empty, string.Empty, false,
                        mutation.ModifiedAtUtc, mutation.BaseVersion, true));
                break;
            case SyncEntityKind.Preference:
                var key = KnownPreferenceKeys.FirstOrDefault(value =>
                    JsonLibraryStore.StableId("preference", value) == mutation.Id);
                if (key is not null)
                {
                    if (!settings.PreferenceStates.TryGetValue(key, out var current) ||
                        mutation.BaseVersion >= current.Version)
                    {
                        settings.PreferenceStates[key] = new PreferenceSyncState(
                            mutation.BaseVersion,
                            mutation.ModifiedAtUtc,
                            true);
                    }
                }

                break;
        }
    }

    private static FavoriteRecord ReadFavorite(SyncMutation mutation) => new(
        mutation.Id,
        String(mutation.Payload, "sourceKey"),
        String(mutation.Payload, "contentId"),
        OptionalString(mutation.Payload, "category"),
        OptionalString(mutation.Payload, "title"),
        OptionalString(mutation.Payload, "caption"),
        OptionalString(mutation.Payload, "coverUrl"),
        mutation.ModifiedAtUtc,
        mutation.BaseVersion,
        mutation.IsDeleted);

    private static PlaybackRecord ReadPlayback(SyncMutation mutation) => new(
        mutation.Id,
        String(mutation.Payload, "sourceKey"),
        String(mutation.Payload, "contentId"),
        String(mutation.Payload, "interfaceKey"),
        String(mutation.Payload, "lineName"),
        Int32(mutation.Payload, "episodeIndex"),
        Int64(mutation.Payload, "positionMs"),
        Int64(mutation.Payload, "durationMs"),
        mutation.ModifiedAtUtc,
        mutation.BaseVersion,
        mutation.IsDeleted)
    {
        SourceIndex = OptionalInt32(mutation.Payload, "sourceIndex"),
        Category = OptionalString(mutation.Payload, "category"),
        Title = OptionalString(mutation.Payload, "title"),
        Caption = OptionalString(mutation.Payload, "caption"),
        CoverUrl = OptionalString(mutation.Payload, "coverUrl"),
    };

    private static SkipMarkerRecord ReadSkipMarker(SyncMutation mutation) => new(
        mutation.Id,
        String(mutation.Payload, "sourceKey"),
        String(mutation.Payload, "contentId"),
        String(mutation.Payload, "interfaceKey"),
        String(mutation.Payload, "lineName"),
        Int32(mutation.Payload, "introEndSeconds"),
        Int32(mutation.Payload, "outroRemainingSeconds"),
        mutation.ModifiedAtUtc,
        mutation.BaseVersion,
        mutation.IsDeleted);

    private static ConfigurationGroupRecord ReadConfiguration(SyncMutation mutation) => new(
        mutation.Id,
        OptionalString(mutation.Payload, "name"),
        OptionalString(mutation.Payload, "address"),
        mutation.Payload.TryGetProperty("isEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
        mutation.ModifiedAtUtc,
        mutation.BaseVersion,
        mutation.IsDeleted);

    private static void ApplyPreference(ClientSettings settings, SyncMutation mutation)
    {
        if (mutation.IsDeleted || !mutation.Payload.TryGetProperty("value", out var value))
        {
            return;
        }

        var key = String(mutation.Payload, "key");
        if (settings.PreferenceStates.TryGetValue(key, out var current) &&
            mutation.BaseVersion < current.Version)
        {
            return;
        }

        settings.PreferenceStates[key] = new PreferenceSyncState(
            mutation.BaseVersion,
            mutation.ModifiedAtUtc,
            mutation.IsDeleted);
        switch (key)
        {
            case "defaultSpeed" when value.TryGetDouble(out var speed):
                settings.DefaultSpeed = Math.Clamp(speed, 0.5, 2.0);
                break;
            case "defaultVolume" when value.TryGetInt32(out var volume):
                settings.DefaultVolume = Math.Clamp(volume, 0, 100);
                break;
            case "useSourceCovers" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                settings.UseSourceCovers = value.GetBoolean();
                break;
            case "posterDensity" when value.ValueKind == JsonValueKind.String:
                settings.PosterDensity = value.GetString() ?? "standard";
                break;
        }
    }

    private static void Upsert<T>(
        List<T> values,
        T incoming,
        Func<T, Guid> id,
        Func<T, long> version)
    {
        var index = values.FindIndex(item => id(item) == id(incoming));
        if (index < 0)
        {
            values.Add(incoming);
        }
        else if (version(incoming) >= version(values[index]))
        {
            values[index] = incoming;
        }
    }

    private static void MarkDeleted<T>(
        List<T> values,
        SyncMutation mutation,
        Func<T, Guid> id,
        Func<T, long> version,
        Func<T, T> update,
        Func<T> create)
    {
        var index = values.FindIndex(item => id(item) == mutation.Id);
        if (index >= 0)
        {
            if (mutation.BaseVersion >= version(values[index]))
            {
                values[index] = update(values[index]);
            }
        }
        else
        {
            values.Add(create());
        }
    }

    private static readonly string[] KnownPreferenceKeys =
        ["defaultSpeed", "defaultVolume", "useSourceCovers", "posterDensity"];

    private static string String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : throw new InvalidDataException($"Sync payload is missing '{name}'.");

    private static string OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int Int32(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException($"Sync payload is missing '{name}'.");

    private static int OptionalInt32(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : 0;

    private static long Int64(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result)
            ? result
            : throw new InvalidDataException($"Sync payload is missing '{name}'.");
}
