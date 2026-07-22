using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Contracts;
using MTPlayer.Server.Auth;
using MTPlayer.Server.Data;
using MTPlayer.Server.Devices;
using MTPlayer.Server.Membership;
using MTPlayer.Server.Tests.Auth;
using Xunit;

namespace MTPlayer.Server.Tests.Admin;

public sealed class MembershipAndSourceTests(PostgreSqlAuthFixture fixture) : IClassFixture<PostgreSqlAuthFixture>
{
    [DockerFact]
    public async Task Admin_sees_login_city_and_user_sources_and_member_receives_eligible_pushes()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";
        var userEmail = $"member-{Guid.NewGuid():N}@example.com";
        await fixture.RegisterAndVerifyAsync(adminEmail);
        await fixture.RegisterAndVerifyAsync(userEmail);

        Guid userId;
        await using (var db = fixture.CreateDbContext())
        {
            await db.Users
                .Where(user => user.NormalizedEmail == PostgreSqlAuthFixture.NormalizeEmail(adminEmail))
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.Role, "admin"));
            userId = await db.Users
                .Where(user => user.NormalizedEmail == PostgreSqlAuthFixture.NormalizeEmail(userEmail))
                .Select(user => user.Id)
                .SingleAsync();
            db.SyncRecords.Add(new SyncRecordEntity
            {
                UserId = userId,
                Id = Guid.NewGuid(),
                Kind = SyncEntityKind.ConfigurationGroup,
                Version = 1,
                ModifiedAtUtc = DateTimeOffset.UtcNow,
                IsDeleted = false,
                PayloadJson = """{"name":"测试仓库","address":"https://config.example/tv.json","sites":[{"api":"https://cms.example/api.php"}],"lives":[{"address":"https://live.example/channels.m3u"}]}""",
            });
            await db.SaveChangesAsync();
        }

        var userLoginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(userEmail, PostgreSqlAuthFixture.Password, "用户电脑", "windows")),
        };
        userLoginRequest.Headers.TryAddWithoutValidation("CF-Connecting-IP", "203.0.113.18");
        userLoginRequest.Headers.TryAddWithoutValidation("CF-IPCity", "杭州市");
        var userLogin = await fixture.Client.SendAsync(userLoginRequest);
        Assert.Equal(HttpStatusCode.OK, userLogin.StatusCode);
        var userTokens = (await userLogin.Content.ReadFromJsonAsync<TokenResponse>())!;

        var adminLogin = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(adminEmail, PostgreSqlAuthFixture.Password, "管理电脑", "windows"));
        var adminTokens = (await adminLogin.Content.ReadFromJsonAsync<TokenResponse>())!;
        using var adminClient = fixture.Factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminTokens.AccessToken);

        var users = await adminClient.GetFromJsonAsync<List<AdminUserSummary>>("/api/v1/admin/users");
        var listed = Assert.Single(users!, user => user.Id == userId);
        Assert.Equal("203.0.113.18", listed.LastLoginIp);
        Assert.Equal("杭州市", listed.LastLoginCity);
        Assert.Contains("https://config.example/tv.json", listed.ConfigurationSourceAddresses);
        Assert.Contains("https://cms.example/api.php", listed.VideoInterfaceAddresses);
        Assert.Contains("https://live.example/channels.m3u", listed.LiveSourceAddresses);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await adminClient.PutAsJsonAsync(
                $"/api/v1/admin/members/{userId}",
                new MembershipUpdate("member", DateTimeOffset.UtcNow.AddMonths(1)))).StatusCode);
        var created = await adminClient.PostAsJsonAsync(
            "/api/v1/admin/member-pushes",
            new MemberPushUpdate(
                "会员片源",
                "member",
                [new MemberSource("会员仓库", "https://member.example/config.json")],
                [new MemberSource("会员直播", "https://member.example/live.m3u")],
                true,
                "会员可使用本期片源。",
                "1.3.2",
                "https://downloads.example/MTPlayer-Android-1.3.2.apk",
                true));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var push = (await created.Content.ReadFromJsonAsync<MemberPushView>())!;

        using var userClient = fixture.Factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userTokens.AccessToken);
        var visible = await userClient.GetFromJsonAsync<List<MemberPushView>>("/api/v1/member/pushes");
        var memberPush = Assert.Single(visible!);
        Assert.Equal(push.Id, memberPush.Id);
        Assert.Equal("https://member.example/config.json", Assert.Single(memberPush.ConfigurationSources).Address);
        Assert.Equal("https://member.example/live.m3u", Assert.Single(memberPush.LiveSources).Address);
        Assert.Equal("会员可使用本期片源。", memberPush.Message);
        Assert.Equal("1.3.2", memberPush.AndroidVersion);
        Assert.Equal("https://downloads.example/MTPlayer-Android-1.3.2.apk", memberPush.AndroidDownloadUrl);
        Assert.True(memberPush.ForceAndroidUpdate);

        Assert.Equal(HttpStatusCode.NoContent, (await adminClient.DeleteAsync($"/api/v1/admin/member-pushes/{push.Id}")).StatusCode);
    }
}
