using System.Text.Json;
using MTPlayer.Contracts;
using Xunit;

namespace MTPlayer.Server.Tests.Contracts;

public sealed class ContractSerializationTests
{
    [Fact]
    public void SyncMutation_round_trips_with_web_json_names()
    {
        var payload = JsonSerializer.SerializeToElement(new { title = "仙逆" });
        var value = new SyncMutation(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SyncEntityKind.Favorite,
            3,
            DateTimeOffset.Parse("2026-07-14T00:00:00Z"),
            false,
            payload);

        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<SyncMutation>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(restored);
        Assert.Equal(value with { Payload = default }, restored with { Payload = default });
        Assert.Equal(value.Payload.GetRawText(), restored.Payload.GetRawText());
    }
}
