using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MTPlayer.Contracts;
using MTPlayer.Server.Data;
using MTPlayer.Server.Security;
using MTPlayer.Server.Tests.Auth;
using Xunit;

namespace MTPlayer.Server.Tests.Devices;

public sealed class DeviceFlowTests(PostgreSqlAuthFixture fixture) : IClassFixture<PostgreSqlAuthFixture>
{
    [DockerFact]
    public async Task Tv_receives_tokens_only_after_user_approval_and_code_is_single_use()
    {
        await ConfigurePublicUrlAsync();
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var userPair = await LoginAsync(email, "手机", "android");
        using var userClient = fixture.Factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", userPair.AccessToken);

        var created = await fixture.Client.GetFromJsonAsync<DeviceCodeResponse>(
            "/api/v1/auth/tv/device-code?serverName=客厅电视");
        Assert.NotNull(created);
        Assert.Matches("^[A-HJ-NP-Z2-9]{8}$", created.UserCode);
        Assert.Equal(5, created.PollIntervalSeconds);
        Assert.Equal(new Uri("https://device.example.com/tv/activate"), created.VerificationUri);
        var verificationPage = await fixture.Client.GetStringAsync("/tv/activate");
        Assert.Contains("确认电视登录", verificationPage, StringComparison.Ordinal);
        Assert.DoesNotContain(created.DeviceCode, created.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(created.UserCode, created.ToString(), StringComparison.Ordinal);

        await using (var db = fixture.CreateDbContext())
        {
            var deviceHash = new TokenFactory().HashToken(created.DeviceCode);
            var stored = await db.DeviceCodes.SingleAsync(code => code.DeviceCodeHash == deviceHash);
            Assert.DoesNotContain(created.DeviceCode, stored.DeviceCodeHash, StringComparison.Ordinal);
            Assert.DoesNotContain(created.UserCode, stored.UserCodeHash, StringComparison.Ordinal);
        }

        var pending = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/tv/token",
            new { created.DeviceCode });
        Assert.Equal((HttpStatusCode)428, pending.StatusCode);

        var tooFast = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/tv/token",
            new { created.DeviceCode });
        Assert.Equal(HttpStatusCode.TooManyRequests, tooFast.StatusCode);
        Assert.NotNull(tooFast.Headers.RetryAfter?.Delta);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await userClient.PostAsJsonAsync(
                "/api/v1/auth/tv/approve",
                new { created.UserCode })).StatusCode);
        await AgeLastPollAsync(created.DeviceCode);

        var approved = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/tv/token",
            new { created.DeviceCode });
        var tvPair = await ReadTokenPairAsync(approved);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/tv/token",
                new { created.DeviceCode })).StatusCode);

        using var tvClient = fixture.Factory.CreateClient();
        tvClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tvPair.AccessToken);
        var devices = await tvClient.GetFromJsonAsync<List<DeviceListItem>>("/api/v1/devices");
        var tvDevice = Assert.Single(devices!, device => device.Name == "客厅电视");
        var serialized = JsonSerializer.Serialize(devices);
        Assert.DoesNotContain("tokenHash", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await tvClient.DeleteAsync($"/api/v1/devices/{tvDevice.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tvClient.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/refresh",
                new RefreshRequest(tvPair.RefreshToken))).StatusCode);
    }

    [DockerFact]
    public async Task Expired_codes_and_disabled_approvals_never_issue_tokens()
    {
        await ConfigurePublicUrlAsync();
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var pair = await LoginAsync(email, "手机", "android");
        using var userClient = fixture.Factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", pair.AccessToken);

        var expired = await fixture.Client.GetFromJsonAsync<DeviceCodeResponse>(
            "/api/v1/auth/tv/device-code?serverName=过期电视");
        await using (var db = fixture.CreateDbContext())
        {
            var tokenFactory = new TokenFactory();
            var hash = tokenFactory.HashToken(expired!.DeviceCode);
            await db.DeviceCodes.Where(code => code.DeviceCodeHash == hash)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    code => code.ExpiresAtUtc,
                    DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await userClient.PostAsJsonAsync(
                "/api/v1/auth/tv/approve",
                new { expired!.UserCode })).StatusCode);

        var disabledCode = await fixture.Client.GetFromJsonAsync<DeviceCodeResponse>(
            "/api/v1/auth/tv/device-code?serverName=禁用电视");
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await userClient.PostAsJsonAsync(
                "/api/v1/auth/tv/approve",
                new { disabledCode!.UserCode })).StatusCode);
        await fixture.SetDisabledAsync(email, true);
        try
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await fixture.Client.PostAsJsonAsync(
                    "/api/v1/auth/tv/token",
                    new { disabledCode.DeviceCode })).StatusCode);
        }
        finally
        {
            await fixture.SetDisabledAsync(email, false);
        }
    }

    [DockerFact]
    public async Task Concurrent_device_code_exchange_issues_exactly_one_token_pair()
    {
        await ConfigurePublicUrlAsync();
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var pair = await LoginAsync(email, "批准手机", "android");
        using var userClient = fixture.Factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", pair.AccessToken);
        var code = await fixture.Client.GetFromJsonAsync<DeviceCodeResponse>(
            "/api/v1/auth/tv/device-code?serverName=并发电视");
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await userClient.PostAsJsonAsync(
                "/api/v1/auth/tv/approve",
                new { code!.UserCode })).StatusCode);

        var responses = await Task.WhenAll(
            fixture.Client.PostAsJsonAsync("/api/v1/auth/tv/token", new { code.DeviceCode }),
            fixture.Client.PostAsJsonAsync("/api/v1/auth/tv/token", new { code.DeviceCode }));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        await using var db = fixture.CreateDbContext();
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
        Assert.Equal(
            2,
            await db.DeviceSessions.CountAsync(session =>
                session.User!.NormalizedEmail == normalizedEmail && session.RevokedAtUtc == null));
    }

    [DockerFact]
    public async Task Admin_can_disable_enable_and_revoke_all_user_devices()
    {
        var adminEmail = UniqueEmail();
        var userEmail = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(adminEmail);
        await fixture.RegisterAndVerifyAsync(userEmail);
        await using (var db = fixture.CreateDbContext())
        {
            var normalizedAdmin = PostgreSqlAuthFixture.NormalizeEmail(adminEmail);
            await db.Users.Where(user => user.NormalizedEmail == normalizedAdmin)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.Role, "admin"));
        }

        var adminPair = await LoginAsync(adminEmail, "管理电脑", "windows");
        var userPair = await LoginAsync(userEmail, "用户电脑", "windows");
        using var adminClient = fixture.Factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminPair.AccessToken);
        Guid userId;
        await using (var db = fixture.CreateDbContext())
        {
            var normalizedUser = PostgreSqlAuthFixture.NormalizeEmail(userEmail);
            userId = await db.Users.Where(user => user.NormalizedEmail == normalizedUser)
                .Select(user => user.Id)
                .SingleAsync();
        }

        var listed = await adminClient.GetFromJsonAsync<List<AdminUserListItem>>("/api/v1/admin/users");
        Assert.Contains(listed!, user => user.Id == userId && user.ActiveDeviceCount == 1);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await adminClient.PostAsync($"/api/v1/admin/users/{userId}/disable", null)).StatusCode);
        Assert.False(await fixture.IsJwtAcceptedAsync(userPair.AccessToken));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/refresh",
                new RefreshRequest(userPair.RefreshToken))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await adminClient.PostAsync($"/api/v1/admin/users/{userId}/enable", null)).StatusCode);
        var replacement = await LoginAsync(userEmail, "新电脑", "windows");
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await adminClient.PostAsync($"/api/v1/admin/users/{userId}/revoke-all", null)).StatusCode);
        Assert.False(await fixture.IsJwtAcceptedAsync(replacement.AccessToken));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/refresh",
                new RefreshRequest(replacement.RefreshToken))).StatusCode);

        Guid adminId;
        await using (var db = fixture.CreateDbContext())
        {
            adminId = await db.Users
                .Where(user => user.NormalizedEmail == PostgreSqlAuthFixture.NormalizeEmail(adminEmail))
                .Select(user => user.Id)
                .SingleAsync();
        }
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await adminClient.PostAsync($"/api/v1/admin/users/{adminId}/disable", null)).StatusCode);
    }

    private async Task ConfigurePublicUrlAsync()
    {
        await using var db = fixture.CreateDbContext();
        var protector = fixture.Factory.Services.GetRequiredService<ISecretProtector>();
        var setting = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == "PublicBaseUrl");
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSettingEntity
            {
                Key = "PublicBaseUrl",
                Value = protector.Protect("https://device.example.com"),
                IsEncrypted = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            setting.Value = protector.Protect("https://device.example.com");
            setting.IsEncrypted = true;
            setting.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private async Task AgeLastPollAsync(string deviceCode)
    {
        await using var db = fixture.CreateDbContext();
        var hash = new TokenFactory().HashToken(deviceCode);
        await db.DeviceCodes.Where(code => code.DeviceCodeHash == hash)
            .ExecuteUpdateAsync(update => update.SetProperty(
                code => code.LastPolledAtUtc,
                DateTimeOffset.UtcNow.AddSeconds(-6)));
    }

    private async Task<TokenResponse> LoginAsync(string email, string name, string platform)
    {
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, name, platform));
        return await ReadTokenPairAsync(response);
    }

    private static async Task<TokenResponse> ReadTokenPairAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static string UniqueEmail() => $"device-{Guid.NewGuid():N}@example.com";

    private sealed record DeviceListItem(
        Guid Id,
        string Name,
        string Platform,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset LastActivityAtUtc);

    private sealed record AdminUserListItem(
        Guid Id,
        string Email,
        string Role,
        bool EmailVerified,
        bool Disabled,
        DateTimeOffset CreatedAtUtc,
        int ActiveDeviceCount);
}
