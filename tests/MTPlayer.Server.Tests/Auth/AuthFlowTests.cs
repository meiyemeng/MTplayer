using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MTPlayer.Contracts;
using MTPlayer.Server.Data;
using MTPlayer.Server.Security;
using Npgsql;
using Xunit;

namespace MTPlayer.Server.Tests.Auth;

public sealed class AuthFlowTests(PostgreSqlAuthFixture fixture) : IClassFixture<PostgreSqlAuthFixture>
{
    [DockerFact]
    public void Fixture_runs_against_real_PostgreSQL_16()
    {
        Assert.Equal(16, fixture.PostgreSqlVersion.Major);
    }

    [DockerFact]
    public async Task Verified_user_can_login_and_rotated_refresh_token_cannot_be_reused()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);

        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "测试电脑", "windows"));
        var pair = await ReadTokenPairAsync(login);
        Assert.InRange(pair.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        Assert.True(await fixture.IsJwtAcceptedAsync(pair.AccessToken));
        Assert.True(await fixture.CanSyncAsync(pair.AccessToken));
        await using (var db = fixture.CreateDbContext())
        {
            var refreshHash = new TokenFactory().HashToken(pair.RefreshToken);
            var refreshExpiry = await db.DeviceSessions
                .Where(session => session.RefreshTokenHash == refreshHash)
                .Select(session => session.ExpiresAtUtc)
                .SingleAsync();
            Assert.InRange(refreshExpiry, DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));
        }

        var firstRefresh = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(pair.RefreshToken));
        var rotated = await ReadTokenPairAsync(firstRefresh);
        Assert.NotEqual(pair.RefreshToken, rotated.RefreshToken);

        var reuse = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(pair.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [DockerFact]
    public async Task Registration_normalizes_email_and_database_constraint_rejects_duplicates()
    {
        var local = $"CASE-{Guid.NewGuid():N}";
        var first = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest($"  {local}@Example.com  ", PostgreSqlAuthFixture.Password));
        var duplicate = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest($"{local.ToLowerInvariant()}@example.COM", PostgreSqlAuthFixture.Password));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await using var db = fixture.CreateDbContext();
        var expectedNormalized = PostgreSqlAuthFixture.NormalizeEmail($"{local}@Example.com");
        var user = await db.Users.SingleAsync(user => user.NormalizedEmail == expectedNormalized);
        Assert.Equal($"{local}@Example.com", user.Email);
        Assert.Equal(user.Email.Trim().ToUpperInvariant(), user.NormalizedEmail);
    }

    [DockerFact]
    public async Task Concurrent_duplicate_registration_has_exactly_one_database_winner()
    {
        var email = UniqueEmail();
        var responses = await Task.WhenAll(
            fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/register",
                new RegisterRequest(email, PostgreSqlAuthFixture.Password)),
            fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/register",
                new RegisterRequest($"  {email.ToUpperInvariant()}  ", PostgreSqlAuthFixture.Password)));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        await using var db = fixture.CreateDbContext();
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
        var user = await db.Users.SingleAsync(value => value.NormalizedEmail == normalizedEmail);
        Assert.Equal(1, await db.EmailTokens.CountAsync(token => token.UserId == user.Id));
        Assert.Equal(1, await db.MailOutbox.CountAsync(message => message.RecipientEmail == user.Email));
    }

    [DockerFact]
    public async Task Password_length_is_limited_to_10_through_128_characters()
    {
        foreach (var invalid in new[] { new string('a', 9), new string('a', 129) })
        {
            var response = await fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/register",
                new RegisterRequest(UniqueEmail(), invalid));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        foreach (var valid in new[] { new string('a', 10), new string('z', 128) })
        {
            var response = await fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/register",
                new RegisterRequest(UniqueEmail(), valid));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
    }

    [DockerFact]
    public async Task Verification_tokens_are_hashed_expiring_and_single_use()
    {
        var email = UniqueEmail();
        var register = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, PostgreSqlAuthFixture.Password));
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var token = await fixture.ReadLatestEmailTokenAsync(email, "verify");
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);

        await using (var db = fixture.CreateDbContext())
        {
            var stored = await db.EmailTokens.SingleAsync(value => value.Purpose == "verify" && value.User!.NormalizedEmail == normalizedEmail);
            Assert.NotEqual(token, stored.TokenHash);
            Assert.True(new TokenFactory().VerifyToken(token, stored.TokenHash));
            Assert.InRange(stored.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(55), DateTimeOffset.UtcNow.AddMinutes(65));
        }

        var first = await fixture.Client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token });
        var second = await fixture.Client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        var expiredEmail = UniqueEmail();
        await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(expiredEmail, PostgreSqlAuthFixture.Password));
        var expiredToken = await fixture.ReadLatestEmailTokenAsync(expiredEmail, "verify");
        await fixture.ExpireEmailTokenAsync(expiredEmail, "verify");
        var expired = await fixture.Client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = expiredToken });
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
    }

    [DockerFact]
    public async Task Unverified_login_has_no_refresh_session_or_sync_capability()
    {
        var email = UniqueEmail();
        await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, PostgreSqlAuthFixture.Password));

        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "未验证电脑", "windows"));
        var pair = await ReadTokenPairAsync(login);
        var claims = ReadJwtPayload(pair.AccessToken);

        Assert.False(pair.EmailVerified);
        Assert.Empty(pair.RefreshToken);
        Assert.Equal("false", claims.GetProperty("email_verified").GetString());
        Assert.DoesNotContain("sync", claims.GetProperty("scope").GetString()!, StringComparison.Ordinal);
        Assert.False(await fixture.CanSyncAsync(pair.AccessToken));
        await using var db = fixture.CreateDbContext();
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
        var userId = await db.Users.Where(user => user.NormalizedEmail == normalizedEmail).Select(user => user.Id).SingleAsync();
        Assert.False(await db.DeviceSessions.AnyAsync(session => session.UserId == userId));
        Assert.Equal(2, await db.EmailTokens.CountAsync(
            token => token.UserId == userId && token.Purpose == "verify"));
    }

    [DockerFact]
    public async Task Disabled_user_is_rejected_for_login_and_refresh()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "电脑", "windows"));
        var pair = await ReadTokenPairAsync(login);
        await fixture.SetDisabledAsync(email, true);
        Assert.False(await fixture.CanSyncAsync(pair.AccessToken));

        var disabledLogin = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "电脑", "windows"));
        var disabledRefresh = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(pair.RefreshToken));

        Assert.Equal(HttpStatusCode.Forbidden, disabledLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabledRefresh.StatusCode);
    }

    [DockerFact]
    public async Task Forgot_password_does_not_reveal_whether_email_exists()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var missing = UniqueEmail();

        var existingResponse = await fixture.Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        var missingResponse = await fixture.Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = missing });

        Assert.Equal(existingResponse.StatusCode, missingResponse.StatusCode);
        Assert.Equal(await existingResponse.Content.ReadAsStringAsync(), await missingResponse.Content.ReadAsStringAsync());
        await using var db = fixture.CreateDbContext();
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
        var normalizedMissing = PostgreSqlAuthFixture.NormalizeEmail(missing);
        Assert.True(await db.EmailTokens.AnyAsync(token => token.Purpose == "reset" && token.User!.NormalizedEmail == normalizedEmail));
        Assert.False(await db.EmailTokens.AnyAsync(token => token.Purpose == "reset" && token.User!.NormalizedEmail == normalizedMissing));
    }

    [DockerFact]
    public async Task Password_reset_is_single_use_and_revokes_existing_refresh_sessions()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "电脑", "windows"));
        var oldPair = await ReadTokenPairAsync(login);
        await fixture.Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        var resetToken = await fixture.ReadLatestEmailTokenAsync(email, "reset");
        await using (var db = fixture.CreateDbContext())
        {
            var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
            var stored = await db.EmailTokens
                .SingleAsync(token => token.Purpose == "reset" && token.User!.NormalizedEmail == normalizedEmail);
            Assert.NotEqual(resetToken, stored.TokenHash);
            Assert.True(new TokenFactory().VerifyToken(resetToken, stored.TokenHash));
            Assert.InRange(stored.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(25), DateTimeOffset.UtcNow.AddMinutes(35));
        }
        const string newPassword = "New-Correct-Horse-2026";

        var reset = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new { token = resetToken, password = newPassword });
        var reuse = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new { token = resetToken, password = "Another-Password-2026" });
        var oldRefresh = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(oldPair.RefreshToken));
        var oldLogin = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "电脑", "windows"));
        var newLogin = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, newPassword, "电脑", "windows"));

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [DockerFact]
    public async Task System_settings_control_email_token_expiry_with_safe_defaults()
    {
        await fixture.SetSettingAsync("EmailVerificationTokenExpiryMinutes", "7");
        var email = UniqueEmail();
        await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, PostgreSqlAuthFixture.Password));

        await using (var db = fixture.CreateDbContext())
        {
            var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
            var expiry = await db.EmailTokens
                .Where(token => token.Purpose == "verify" && token.User!.NormalizedEmail == normalizedEmail)
                .Select(token => token.ExpiresAtUtc)
                .SingleAsync();
            Assert.InRange(expiry, DateTimeOffset.UtcNow.AddMinutes(6), DateTimeOffset.UtcNow.AddMinutes(8));
        }

        await fixture.DeleteSettingAsync("EmailVerificationTokenExpiryMinutes");

        await fixture.SetSettingAsync("PasswordResetTokenExpiryMinutes", "9");
        await fixture.Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        await using (var db = fixture.CreateDbContext())
        {
            var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
            var expiry = await db.EmailTokens
                .Where(token => token.Purpose == "reset" && token.User!.NormalizedEmail == normalizedEmail)
                .OrderByDescending(token => token.CreatedAtUtc)
                .Select(token => token.ExpiresAtUtc)
                .FirstAsync();
            Assert.InRange(expiry, DateTimeOffset.UtcNow.AddMinutes(8), DateTimeOffset.UtcNow.AddMinutes(10));
        }

        await fixture.DeleteSettingAsync("PasswordResetTokenExpiryMinutes");
    }

    [DockerFact]
    public async Task Concurrent_refresh_allows_exactly_one_success()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "并发电脑", "windows"));
        var pair = await ReadTokenPairAsync(login);

        var responses = await Task.WhenAll(
            fixture.Client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(pair.RefreshToken)),
            fixture.Client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(pair.RefreshToken)));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
    }

    [DockerFact]
    public async Task Expired_refresh_token_is_rejected()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "过期电脑", "windows"));
        var pair = await ReadTokenPairAsync(login);
        await fixture.ExpireRefreshTokenAsync(pair.RefreshToken);

        var response = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(pair.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [DockerFact]
    public void All_auth_endpoints_have_the_expected_rate_limiting_policies()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/v1/auth/register"] = "registration",
            ["/api/v1/auth/verify-email"] = "email-token",
            ["/api/v1/auth/login"] = "login",
            ["/api/v1/auth/refresh"] = "refresh",
            ["/api/v1/auth/forgot-password"] = "email-token",
            ["/api/v1/auth/reset-password"] = "email-token",
        };

        var endpoints = fixture.Factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .ToDictionary(endpoint => endpoint.RoutePattern.RawText!, StringComparer.Ordinal);

        foreach (var (route, policy) in expected)
        {
            var metadata = endpoints[route].Metadata.GetMetadata<EnableRateLimitingAttribute>();
            Assert.NotNull(metadata);
            Assert.Equal(policy, metadata.PolicyName);
        }
    }

    [DockerFact]
    public async Task Email_token_rate_limit_returns_429_after_the_default_limit()
    {
        await using var factory = fixture.CreateFactory(useHighRateLimits: false);
        using var client = factory.CreateClient();
        for (var requestNumber = 1; requestNumber <= 5; requestNumber++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/forgot-password",
                new { email = $"missing-{requestNumber}@example.com" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var rejected = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new { email = "missing-6@example.com" });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.GetProperty("status").GetInt32());
        Assert.Equal("请求过于频繁，请稍后再试。", problem.GetProperty("title").GetString());
        Assert.Equal("rate_limit_exceeded", problem.GetProperty("code").GetString());
        var traceId = problem.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));
        Assert.Equal(traceId, Assert.Single(rejected.Headers.GetValues("X-Request-ID")));
        Assert.NotNull(rejected.Headers.RetryAfter?.Delta);
        Assert.InRange(rejected.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
    }

    [DockerFact]
    public async Task Passwords_and_tokens_are_not_written_to_application_logs()
    {
        fixture.Logs.Clear();
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "日志电脑", "windows"));
        var pair = await ReadTokenPairAsync(login);
        await fixture.Client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(pair.RefreshToken));
        var allLogs = string.Join('\n', fixture.Logs.Messages);

        Assert.DoesNotContain(PostgreSqlAuthFixture.Password, allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(pair.AccessToken, allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(pair.RefreshToken, allLogs, StringComparison.Ordinal);
    }

    private static string UniqueEmail() => $"auth-{Guid.NewGuid():N}@example.com";

    private static async Task<TokenResponse> ReadTokenPairAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static JsonElement ReadJwtPayload(string token)
    {
        var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + ((4 - part.Length % 4) % 4), '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(part));
        return document.RootElement.Clone();
    }
}

public sealed class PostgreSqlAuthFixture : IAsyncLifetime
{
    public const string Password = "Correct-Horse-2026";
    private const string DataEncryptionKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private string? _containerName;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public CapturingLoggerProvider Logs { get; } = new();
    public string ConnectionString { get; private set; } = string.Empty;
    public Version PostgreSqlVersion { get; private set; } = new();

    public async Task InitializeAsync()
    {
        if (!DockerProcess.IsAvailable())
        {
            return;
        }

        _containerName = $"mtplayer-auth-tests-{Guid.NewGuid():N}";
        var run = await DockerProcess.RunAsync(
            "run", "--rm", "-d", "--name", _containerName,
            "-e", "POSTGRES_PASSWORD=postgres",
            "-e", "POSTGRES_DB=mtplayer_auth_tests",
            "-p", "127.0.0.1::5432", "postgres:16-alpine");
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to start PostgreSQL 16 test container: {run.Error}");
        }

        var portResult = await DockerProcess.RunAsync("port", _containerName, "5432/tcp");
        var port = int.Parse(portResult.Output.Trim().Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
        ConnectionString = $"Host=127.0.0.1;Port={port};Database=mtplayer_auth_tests;Username=postgres;Password=postgres;Pooling=false";
        await WaitForPostgreSqlAsync();

        Factory = CreateFactory(useHighRateLimits: true);
        Client = Factory.CreateClient();
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApiDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_containerName is not null)
        {
            await DockerProcess.RunAsync("rm", "-f", _containerName);
        }
    }

    public ApiDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApiDbContext>().UseNpgsql(ConnectionString).Options;
        return new ApiDbContext(options);
    }

    public WebApplicationFactory<Program> CreateFactory(bool useHighRateLimits) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:PostgreSQL", ConnectionString);
            builder.UseSetting("DATA_ENCRYPTION_KEY", DataEncryptionKey);
            if (useHighRateLimits)
            {
                builder.UseSetting("RateLimiting:registration:PermitLimit", "1000");
                builder.UseSetting("RateLimiting:login:PermitLimit", "1000");
                builder.UseSetting("RateLimiting:refresh:PermitLimit", "1000");
                builder.UseSetting("RateLimiting:email-token:PermitLimit", "1000");
            }

            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        });

    public async Task RegisterAndVerifyAsync(string email)
    {
        var registered = await Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, Password));
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var token = await ReadLatestEmailTokenAsync(email, "verify");
        var verified = await Client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
    }

    public async Task<string> ReadLatestEmailTokenAsync(string email, string purpose)
    {
        await using var db = CreateDbContext();
        var body = await db.MailOutbox
            .Where(message => message.RecipientEmail == email.Trim())
            .OrderByDescending(message => message.Id)
            .Select(message => message.BodyHtml)
            .FirstAsync(message => message.StartsWith($"{purpose}:"));
        return body[(purpose.Length + 1)..];
    }

    public async Task ExpireEmailTokenAsync(string email, string purpose)
    {
        await using var db = CreateDbContext();
        var normalizedEmail = NormalizeEmail(email);
        await db.EmailTokens
            .Where(token => token.Purpose == purpose && token.User!.NormalizedEmail == normalizedEmail)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    public async Task SetDisabledAsync(string email, bool disabled)
    {
        await using var db = CreateDbContext();
        var normalizedEmail = NormalizeEmail(email);
        await db.Users
            .Where(user => user.NormalizedEmail == normalizedEmail)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.Disabled, disabled));
    }

    public async Task SetSettingAsync(string key, string value)
    {
        await using var db = CreateDbContext();
        db.SystemSettings.Add(new SystemSettingEntity
        {
            Key = key,
            Value = value,
            IsEncrypted = false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task DeleteSettingAsync(string key)
    {
        await using var db = CreateDbContext();
        await db.SystemSettings.Where(setting => setting.Key == key).ExecuteDeleteAsync();
    }

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public async Task ExpireRefreshTokenAsync(string refreshToken)
    {
        var refreshHash = new TokenFactory().HashToken(refreshToken);
        await using var db = CreateDbContext();
        await db.DeviceSessions
            .Where(session => session.RefreshTokenHash == refreshHash)
            .ExecuteUpdateAsync(update => update.SetProperty(
                session => session.ExpiresAtUtc,
                DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    public async Task<bool> CanSyncAsync(string accessToken)
    {
        var payload = ReadPayload(accessToken);
        var claims = payload.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .Select(property => new System.Security.Claims.Claim(property.Name, property.Value.GetString()!));
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "Bearer"));
        await using var scope = Factory.Services.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(principal, null, "sync-access");
        return result.Succeeded;
    }

    public async Task<bool> IsJwtAcceptedAsync(string accessToken)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Headers.Authorization = $"Bearer {accessToken}";
        var result = await context.AuthenticateAsync();
        return result.Succeeded;
    }

    private static JsonElement ReadPayload(string token)
    {
        var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + ((4 - part.Length % 4) % 4), '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(part));
        return document.RootElement.Clone();
    }

    private async Task WaitForPostgreSqlAsync()
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                PostgreSqlVersion = connection.PostgreSqlVersion;
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
            {
                lastError = exception;
                await Task.Delay(500);
            }
        }

        throw new InvalidOperationException("PostgreSQL 16 test container did not become ready.", lastError);
    }
}

public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> DockerAvailable = new(DockerProcess.IsAvailable);

    public DockerFactAttribute()
    {
        if (!DockerAvailable.Value)
        {
            Skip = "Docker is unavailable; real PostgreSQL 16 integration test skipped.";
        }
    }
}

internal static class DockerProcess
{
    public static bool IsAvailable()
    {
        try
        {
            return RunAsync("info", "--format", "{{.ServerVersion}}").GetAwaiter().GetResult().ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<ProcessResult> RunAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();
    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
    public void Dispose() { }
    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
