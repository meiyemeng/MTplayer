using System.Globalization;
using System.Text.Json;
using MTPlayer.Contracts;
using Xunit;

namespace MTPlayer.Server.Tests.Contracts;

public sealed class ContractSerializationTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SyncMutation_round_trips_with_web_json_names()
    {
        var payload = JsonSerializer.SerializeToElement(new { title = "仙逆" });
        var value = new SyncMutation(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SyncEntityKind.Favorite,
            3,
            DateTimeOffset.Parse("2026-07-14T00:00:00Z", CultureInfo.InvariantCulture),
            false,
            payload);

        var json = JsonSerializer.Serialize(value, WebJson);
        var restored = JsonSerializer.Deserialize<SyncMutation>(json, WebJson);

        Assert.NotNull(restored);
        Assert.Equal(value with { Payload = default }, restored with { Payload = default });
        Assert.Equal(value.Payload.GetRawText(), restored.Payload.GetRawText());
    }

    [Fact]
    public void SyncMutation_serializes_with_stable_field_names_and_values()
    {
        var value = new SyncMutation(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SyncEntityKind.Favorite,
            3,
            DateTimeOffset.Parse("2026-07-14T00:00:00Z", CultureInfo.InvariantCulture),
            false,
            JsonSerializer.SerializeToElement(new { title = "仙逆" }));

        var json = JsonSerializer.Serialize(value, WebJson);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            ["id", "kind", "baseVersion", "modifiedAtUtc", "isDeleted", "payload"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("11111111-1111-1111-1111-111111111111", root.GetProperty("id").GetString());
        Assert.Equal("Favorite", root.GetProperty("kind").GetString());
        Assert.Equal(3, root.GetProperty("baseVersion").GetInt64());
        Assert.Equal("2026-07-14T00:00:00+00:00", root.GetProperty("modifiedAtUtc").GetString());
        Assert.False(root.GetProperty("isDeleted").GetBoolean());
        Assert.Equal("仙逆", root.GetProperty("payload").GetProperty("title").GetString());
    }

    [Fact]
    public void SyncMutation_deserializes_from_independent_fixed_json()
    {
        const string json = """
            {
              "id": "22222222-2222-2222-2222-222222222222",
              "kind": "SkipMarker",
              "baseVersion": 7,
              "modifiedAtUtc": "2026-07-14T08:30:00+08:00",
              "isDeleted": true,
              "payload": { "introSeconds": 90 }
            }
            """;

        var restored = JsonSerializer.Deserialize<SyncMutation>(json, WebJson);

        Assert.NotNull(restored);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), restored.Id);
        Assert.Equal(SyncEntityKind.SkipMarker, restored.Kind);
        Assert.Equal(7, restored.BaseVersion);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-14T08:30:00+08:00", CultureInfo.InvariantCulture),
            restored.ModifiedAtUtc);
        Assert.True(restored.IsDeleted);
        Assert.Equal(90, restored.Payload.GetProperty("introSeconds").GetInt32());
    }

    [Fact]
    public void SyncEntityKind_has_stable_numeric_values()
    {
        Assert.Equal(0, (int)SyncEntityKind.ConfigurationGroup);
        Assert.Equal(1, (int)SyncEntityKind.Favorite);
        Assert.Equal(2, (int)SyncEntityKind.PlaybackHistory);
        Assert.Equal(3, (int)SyncEntityKind.SkipMarker);
        Assert.Equal(4, (int)SyncEntityKind.Preference);
    }
}
