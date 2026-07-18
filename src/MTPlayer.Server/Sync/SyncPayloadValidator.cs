using System.Text.Json;
using MTPlayer.Contracts;

namespace MTPlayer.Server.Sync;

public static class SyncPayloadValidator
{
    public const int MaximumMutationCount = 500;
    public const int MaximumRequestBytes = 2 * 1024 * 1024;

    public static string? Validate(SyncMutation mutation)
    {
        if (mutation.Id == Guid.Empty || mutation.BaseVersion < 0)
        {
            return "invalid_mutation";
        }

        if (mutation.ModifiedAtUtc == default ||
            mutation.ModifiedAtUtc.Offset != TimeSpan.Zero ||
            mutation.ModifiedAtUtc > DateTimeOffset.UtcNow.AddMinutes(10))
        {
            return "invalid_modified_time";
        }

        if (!Enum.IsDefined(mutation.Kind))
        {
            return "invalid_entity_kind";
        }

        if (mutation.IsDeleted)
        {
            return mutation.Payload.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : "invalid_payload";
        }

        if (mutation.Payload.ValueKind != JsonValueKind.Object)
        {
            return "invalid_payload";
        }

        return mutation.Kind switch
        {
            SyncEntityKind.ConfigurationGroup =>
                String(mutation.Payload, "name", 200) &&
                HttpAddress(mutation.Payload, "address") &&
                Boolean(mutation.Payload, "isEnabled") &&
                OptionalSites(mutation.Payload) &&
                OptionalLives(mutation.Payload) ? null : "invalid_configuration_group",
            SyncEntityKind.Favorite =>
                String(mutation.Payload, "sourceKey", 500) &&
                String(mutation.Payload, "contentId", 500) &&
                String(mutation.Payload, "title", 500) ? null : "invalid_favorite",
            SyncEntityKind.PlaybackHistory =>
                String(mutation.Payload, "sourceKey", 500) &&
                String(mutation.Payload, "contentId", 500) &&
                String(mutation.Payload, "interfaceKey", 500) &&
                String(mutation.Payload, "lineName", 500) &&
                Integer(mutation.Payload, "episodeIndex", 0) &&
                Integer(mutation.Payload, "positionMs", 0) &&
                Integer(mutation.Payload, "durationMs", 0) ? null : "invalid_playback_history",
            SyncEntityKind.SkipMarker =>
                String(mutation.Payload, "sourceKey", 500) &&
                String(mutation.Payload, "contentId", 500) &&
                String(mutation.Payload, "interfaceKey", 500) &&
                String(mutation.Payload, "lineName", 500) &&
                Integer(mutation.Payload, "introEndSeconds", 0) &&
                Integer(mutation.Payload, "outroRemainingSeconds", 0) ? null : "invalid_skip_marker",
            SyncEntityKind.Preference =>
                String(mutation.Payload, "key", 200) &&
                mutation.Payload.TryGetProperty("value", out _) ? null : "invalid_preference",
            _ => "invalid_entity_kind",
        };
    }

    private static bool String(JsonElement value, string name, int maximumLength) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString()) &&
        property.GetString()!.Length <= maximumLength;

    private static bool Boolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool Integer(JsonElement value, string name, long minimum) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt64(out var number) &&
        number >= minimum;

    private static bool HttpAddress(JsonElement value, string name) =>
        String(value, name, 4096) &&
        Uri.TryCreate(value.GetProperty(name).GetString(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool OptionalSites(JsonElement value)
    {
        if (!value.TryGetProperty("sites", out var sites)) return true;
        return sites.ValueKind == JsonValueKind.Array && sites.GetArrayLength() <= 500 &&
            sites.EnumerateArray().All(site =>
                site.ValueKind == JsonValueKind.Object &&
                String(site, "key", 500) &&
                String(site, "name", 500) &&
                HttpAddress(site, "api"));
    }

    private static bool OptionalLives(JsonElement value)
    {
        if (!value.TryGetProperty("lives", out var lives)) return true;
        return lives.ValueKind == JsonValueKind.Array && lives.GetArrayLength() <= 2_000 &&
            lives.EnumerateArray().All(live =>
                live.ValueKind == JsonValueKind.Object &&
                String(live, "name", 500) &&
                HttpAddress(live, "address"));
    }
}
