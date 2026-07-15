using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Contracts;
using MTPlayer.Server.Data;
using MTPlayer.Server.Sync;
using MTPlayer.Server.Tests.Auth;
using Xunit;

namespace MTPlayer.Server.Tests.Sync;

public sealed class SyncFlowTests(PostgreSqlAuthFixture fixture) : IClassFixture<PostgreSqlAuthFixture>
{
    [Fact]
    public void Every_sync_entity_kind_has_a_validated_payload_schema()
    {
        var now = DateTimeOffset.UtcNow;
        SyncMutation[] mutations =
        [
            new(Guid.NewGuid(), SyncEntityKind.ConfigurationGroup, 0, now, false,
                JsonSerializer.SerializeToElement(new { name = "默认", address = "https://example.com/config.json", isEnabled = true })),
            Favorite(Guid.NewGuid(), 0, now, "影片", "server"),
            new(Guid.NewGuid(), SyncEntityKind.PlaybackHistory, 0, now, false,
                JsonSerializer.SerializeToElement(new
                {
                    sourceKey = "source", contentId = "content", interfaceKey = "api",
                    lineName = "line", episodeIndex = 1, positionMs = 10_000, durationMs = 60_000,
                })),
            new(Guid.NewGuid(), SyncEntityKind.SkipMarker, 0, now, false,
                JsonSerializer.SerializeToElement(new
                {
                    sourceKey = "source", contentId = "content", interfaceKey = "api",
                    lineName = "line", introEndSeconds = 60, outroRemainingSeconds = 90,
                })),
            Preference(Guid.NewGuid(), 0, now, "defaultSpeed", 1.25),
        ];

        Assert.All(mutations, mutation => Assert.Null(SyncPayloadValidator.Validate(mutation)));
    }

    [DockerFact]
    public async Task Push_pull_paginates_and_updates_the_current_device_cursor()
    {
        var session = await CreateSessionAsync("page");
        var first = Preference(Guid.NewGuid(), 0, DateTimeOffset.UtcNow.AddMinutes(-2), "speed", 1.25);
        var second = Preference(Guid.NewGuid(), 0, DateTimeOffset.UtcNow.AddMinutes(-1), "volume", 80);

        var pushed = await PushAsync(session, [first, second]);
        Assert.All(pushed, result => Assert.True(result.Accepted));
        var page1 = await PullAsync(session, 0, 1);
        Assert.Single(page1.Changes);
        Assert.True(page1.Cursor > 0);
        var page2 = await PullAsync(session, page1.Cursor, 1);
        Assert.Single(page2.Changes);
        Assert.True(page2.Cursor > page1.Cursor);
        var empty = await PullAsync(session, page2.Cursor, 500);
        Assert.Empty(empty.Changes);
        Assert.Equal(page2.Cursor, empty.Cursor);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            page2.Cursor,
            await db.DeviceSessions.Where(device => device.Id == session.DeviceId)
                .Select(device => device.LastSyncCursor)
                .SingleAsync());
    }

    [DockerFact]
    public async Task Stale_change_returns_current_record_but_newer_client_and_favorite_merge_are_accepted()
    {
        var session = await CreateSessionAsync("conflict");
        var preferenceId = Guid.NewGuid();
        var originalTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var created = Assert.Single(await PushAsync(session,
            [Preference(preferenceId, 0, originalTime, "defaultSpeed", 1.25)]));
        Assert.Equal(1, created.Version);

        var stale = Assert.Single(await PushAsync(session,
            [Preference(preferenceId, 0, originalTime.AddMinutes(-1), "defaultSpeed", 2.0)]));
        Assert.False(stale.Accepted);
        Assert.Equal("version_conflict", stale.ErrorCode);
        Assert.NotNull(stale.Current);
        Assert.Equal(1.25, stale.Current.Payload.GetProperty("value").GetDouble());

        var newer = Assert.Single(await PushAsync(session,
            [Preference(preferenceId, 0, originalTime.AddMinutes(1), "defaultSpeed", 1.5)]));
        Assert.True(newer.Accepted);
        Assert.Equal(2, newer.Version);

        var favoriteId = Guid.NewGuid();
        await PushAsync(session, [Favorite(favoriteId, 0, originalTime, "原始标题", "server")]);
        var merged = Assert.Single(await PushAsync(session,
            [Favorite(favoriteId, 0, originalTime.AddMinutes(-1), "客户端标题", "client")]));
        Assert.True(merged.Accepted);
        var favorite = (await PullAsync(session, 0, 500)).Changes.Last(item => item.Id == favoriteId);
        Assert.Equal("客户端标题", favorite.Payload.GetProperty("title").GetString());
        Assert.Equal("server", favorite.Payload.GetProperty("serverOnly").GetString());
        Assert.Equal("client", favorite.Payload.GetProperty("clientOnly").GetString());
    }

    [DockerFact]
    public async Task Delete_is_pulled_as_tombstone_and_is_pruned_only_after_retention_and_cursor_advance()
    {
        var session = await CreateSessionAsync("delete");
        var id = Guid.NewGuid();
        var created = Assert.Single(await PushAsync(session,
            [Preference(id, 0, DateTimeOffset.UtcNow.AddMinutes(-2), "posterDensity", "normal")]));
        var deletedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var deletion = new SyncMutation(
            id,
            SyncEntityKind.Preference,
            created.Version,
            deletedAt,
            true,
            JsonSerializer.SerializeToElement(new { }));
        Assert.True(Assert.Single(await PushAsync(session, [deletion])).Accepted);

        var pulled = await PullAsync(session, 0, 500);
        Assert.Contains(pulled.Changes, change => change.Id == id && change.IsDeleted);
        await using (var db = fixture.CreateDbContext())
        {
            await db.SyncRecords.Where(record => record.UserId == session.UserId && record.Id == id)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    record => record.ModifiedAtUtc,
                    DateTimeOffset.UtcNow.AddDays(-181)));
        }

        await PullAsync(session, pulled.Cursor, 500);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.SyncRecords.AnyAsync(record => record.UserId == session.UserId && record.Id == id));
    }

    [DockerFact]
    public async Task Limits_schema_device_binding_and_user_isolation_are_enforced()
    {
        var owner = await CreateSessionAsync("limits-owner");
        var other = await CreateSessionAsync("limits-other");
        var id = Guid.NewGuid();
        await PushAsync(owner, [Preference(id, 0, DateTimeOffset.UtcNow.AddMinutes(-1), "volume", 90)]);
        Assert.DoesNotContain((await PullAsync(other, 0, 500)).Changes, change => change.Id == id);

        var mismatch = await owner.Client.PostAsJsonAsync(
            "/api/v1/sync/push",
            new SyncPushRequest(other.DeviceId, []));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal("device_mismatch", await ReadProblemCodeAsync(mismatch));

        var tooMany = Enumerable.Range(0, 501)
            .Select(index => Preference(Guid.NewGuid(), 0, DateTimeOffset.UtcNow.AddMinutes(-1), $"key-{index}", index))
            .ToArray();
        var tooManyResponse = await owner.Client.PostAsJsonAsync(
            "/api/v1/sync/push",
            new SyncPushRequest(owner.DeviceId, tooMany));
        Assert.Equal(HttpStatusCode.BadRequest, tooManyResponse.StatusCode);
        Assert.Equal("too_many_mutations", await ReadProblemCodeAsync(tooManyResponse));

        var invalid = new SyncMutation(
            Guid.NewGuid(),
            SyncEntityKind.SkipMarker,
            0,
            DateTimeOffset.UtcNow,
            false,
            JsonSerializer.SerializeToElement(new { introEndSeconds = -1 }));
        var invalidResponse = await owner.Client.PostAsJsonAsync(
            "/api/v1/sync/push",
            new SyncPushRequest(owner.DeviceId, [invalid]));
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal("invalid_skip_marker", await ReadProblemCodeAsync(invalidResponse));

        var oversized = new SyncMutation(
            Guid.NewGuid(),
            SyncEntityKind.Favorite,
            0,
            DateTimeOffset.UtcNow,
            false,
            JsonSerializer.SerializeToElement(new
            {
                sourceKey = "source",
                contentId = "content",
                title = "title",
                extensionData = new string('x', 2 * 1024 * 1024),
            }));
        var oversizedResponse = await owner.Client.PostAsJsonAsync(
            "/api/v1/sync/push",
            new SyncPushRequest(owner.DeviceId, [oversized]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);
        Assert.Equal("payload_too_large", await ReadProblemCodeAsync(oversizedResponse));
    }

    [DockerFact]
    public async Task Unverified_user_and_revoked_access_token_cannot_sync()
    {
        var session = await CreateSessionAsync("unverified");
        await fixture.SetEmailVerifiedAsync(session.Email, false);
        try
        {
            var response = await session.Client.GetAsync("/api/v1/sync/pull?cursor=0&limit=500");
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }
        finally
        {
            await fixture.SetEmailVerifiedAsync(session.Email, true);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await db.DeviceSessions.Where(device => device.Id == session.DeviceId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    device => device.RevokedAtUtc,
                    DateTimeOffset.UtcNow));
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await session.Client.GetAsync("/api/v1/sync/pull?cursor=0&limit=500")).StatusCode);
    }

    [DockerFact]
    public async Task Concurrent_first_writes_are_serialized_and_only_one_base_version_zero_write_wins()
    {
        var first = await CreateSessionAsync("race-a");
        var second = await LoginExistingAsync(first.Email, "race-b");
        var id = Guid.NewGuid();
        var time = DateTimeOffset.UtcNow.AddMinutes(-1);
        var responses = await Task.WhenAll(
            PushAsync(first, [Preference(id, 0, time, "speed", 1.0)]),
            PushAsync(second, [Preference(id, 0, time, "speed", 2.0)]));
        Assert.Single(responses.SelectMany(value => value), result => result.Accepted);
        Assert.Single(responses.SelectMany(value => value), result => !result.Accepted);
    }

    private async Task<Session> CreateSessionAsync(string prefix)
    {
        var email = $"sync-{prefix}-{Guid.NewGuid():N}@example.com";
        await fixture.RegisterAndVerifyAsync(email);
        return await LoginExistingAsync(email, prefix);
    }

    private async Task<Session> LoginExistingAsync(string email, string deviceName)
    {
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, deviceName, "windows"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        using var payload = ReadJwtPayload(tokens.AccessToken);
        var deviceId = Guid.Parse(payload.RootElement.GetProperty("sid").GetString()!);
        var userId = Guid.Parse(payload.RootElement.GetProperty("sub").GetString()!);
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return new Session(email, userId, deviceId, client);
    }

    private static async Task<IReadOnlyList<SyncPushResult>> PushAsync(
        Session session,
        IReadOnlyList<SyncMutation> mutations)
    {
        var response = await session.Client.PostAsJsonAsync(
            "/api/v1/sync/push",
            new SyncPushRequest(session.DeviceId, mutations));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<SyncPushResult>>())!;
    }

    private static async Task<SyncPullResponse> PullAsync(Session session, long cursor, int limit)
    {
        var response = await session.Client.GetAsync($"/api/v1/sync/pull?cursor={cursor}&limit={limit}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SyncPullResponse>())!;
    }

    private static SyncMutation Preference(Guid id, long version, DateTimeOffset modified, string key, object value) => new(
        id,
        SyncEntityKind.Preference,
        version,
        modified.ToUniversalTime(),
        false,
        JsonSerializer.SerializeToElement(new { key, value }));

    private static SyncMutation Favorite(
        Guid id,
        long version,
        DateTimeOffset modified,
        string title,
        string marker) => new(
        id,
        SyncEntityKind.Favorite,
        version,
        modified.ToUniversalTime(),
        false,
        marker == "server"
            ? JsonSerializer.SerializeToElement(new { sourceKey = "source", contentId = "content", title, serverOnly = marker })
            : JsonSerializer.SerializeToElement(new { sourceKey = "source", contentId = "content", title, clientOnly = marker }));

    private static JsonDocument ReadJwtPayload(string token)
    {
        var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + ((4 - part.Length % 4) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(part));
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private sealed record Session(string Email, Guid UserId, Guid DeviceId, HttpClient Client) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
