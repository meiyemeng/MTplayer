using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MTPlayer.Contracts;
using MTPlayer.Server.Auth;
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
        Assert.True(await fixture.IsJwtAcceptedAsync(pair.AccessToken), fixture.LastAuthenticationFailure);
        Assert.Equal(HttpStatusCode.OK, await fixture.AuthorizeWithDefaultPolicyAsync(pair.AccessToken));
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
        await using (var db = fixture.CreateDbContext())
        {
            var consumedHash = new TokenFactory().HashToken(pair.RefreshToken);
            Assert.True(await db.ConsumedRefreshTokens.AnyAsync(token => token.TokenHash == consumedHash));
        }

        var reuse = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(pair.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var familyRevoked = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, familyRevoked.StatusCode);
    }

    [DockerFact]
    public async Task Registration_normalizes_email_and_does_not_expose_duplicates()
    {
        var local = $"CASE-{Guid.NewGuid():N}";
        var firstStopwatch = Stopwatch.StartNew();
        var first = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest($"  {local}@Example.com  ", PostgreSqlAuthFixture.Password));
        firstStopwatch.Stop();
        var duplicateStopwatch = Stopwatch.StartNew();
        var duplicate = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest($"{local.ToLowerInvariant()}@example.COM", PostgreSqlAuthFixture.Password));
        duplicateStopwatch.Stop();

        await AssertAcceptedResponseAsync(first, "如果该邮箱可注册，将收到验证邮件。");
        await AssertAcceptedResponseAsync(duplicate, "如果该邮箱可注册，将收到验证邮件。");
        Assert.True((firstStopwatch.Elapsed - duplicateStopwatch.Elapsed).Duration() < TimeSpan.FromSeconds(1));
        await using var db = fixture.CreateDbContext();
        var expectedNormalized = PostgreSqlAuthFixture.NormalizeEmail($"{local}@Example.com");
        var user = await db.Users.SingleAsync(user => user.NormalizedEmail == expectedNormalized);
        Assert.Equal($"{local}@Example.com", user.Email);
        Assert.Equal(user.Email.Trim().ToUpperInvariant(), user.NormalizedEmail);
    }

    [DockerFact]
    public async Task Concurrent_duplicate_registration_has_one_database_winner_and_identical_external_status()
    {
        var email = UniqueEmail();
        var responses = await Task.WhenAll(
            fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/register",
                new RegisterRequest(email, PostgreSqlAuthFixture.Password)),
            fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/register",
                new RegisterRequest($"  {email.ToUpperInvariant()}  ", PostgreSqlAuthFixture.Password)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        foreach (var response in responses)
        {
            await AssertAcceptedResponseAsync(response, "如果该邮箱可注册，将收到验证邮件。");
        }
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
    public async Task Argon2_work_has_a_process_wide_concurrency_ceiling()
    {
        var service = fixture.Factory.Services.GetRequiredService<Argon2PasswordService>();
        service.ResetPeakConcurrencyForTests();
        await Assert.ThrowsAsync<ArgumentException>(() => service.HashAsync("short", CancellationToken.None));
        Assert.Equal(0, service.PeakObservedConcurrency);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            service.HashAsync($"Concurrent-Password-{index}-2026", CancellationToken.None)));

        Assert.InRange(service.PeakObservedConcurrency, 1, service.MaximumConcurrency);
        Assert.Equal(2, service.MaximumConcurrency);
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
            var outbox = await db.MailOutbox.SingleAsync(message => message.RecipientEmail == email);
            Assert.StartsWith("enc:v1:", outbox.BodyHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(token, outbox.BodyHtml, StringComparison.Ordinal);
            Assert.Equal($"verify:{token}", fixture.DecryptOutboxBody(outbox.BodyHtml));
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
    public async Task Unverified_login_returns_verification_required_without_any_tokens()
    {
        var email = UniqueEmail();
        await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, PostgreSqlAuthFixture.Password));

        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "未验证电脑", "windows"));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        var problem = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("verification_required", problem.GetProperty("code").GetString());
        var body = problem.GetRawText();
        Assert.DoesNotContain("accessToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", body, StringComparison.OrdinalIgnoreCase);
        await using var db = fixture.CreateDbContext();
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
        var userId = await db.Users.Where(user => user.NormalizedEmail == normalizedEmail).Select(user => user.Id).SingleAsync();
        Assert.False(await db.DeviceSessions.AnyAsync(session => session.UserId == userId));
        Assert.Equal(1, await db.EmailTokens.CountAsync(
            token => token.UserId == userId && token.Purpose == "verify"));
    }

    [DockerFact]
    public async Task Verification_cooldown_prevents_flooding_and_reissue_invalidates_the_old_token()
    {
        var email = UniqueEmail();
        await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(email, PostgreSqlAuthFixture.Password));
        var firstToken = await fixture.ReadLatestEmailTokenAsync(email, "verify");

        var withinCooldown = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "冷却电脑", "windows"));
        Assert.Equal(HttpStatusCode.Forbidden, withinCooldown.StatusCode);
        await fixture.AgeEmailTokensAsync(email, "verify", TimeSpan.FromSeconds(61));
        var afterCooldown = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "冷却电脑", "windows"));
        Assert.Equal(HttpStatusCode.Forbidden, afterCooldown.StatusCode);
        var secondToken = await fixture.ReadLatestEmailTokenAsync(email, "verify");

        Assert.NotEqual(firstToken, secondToken);
        var oldVerification = await fixture.Client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = firstToken });
        var newVerification = await fixture.Client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = secondToken });
        Assert.Equal(HttpStatusCode.BadRequest, oldVerification.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, newVerification.StatusCode);
        await using var db = fixture.CreateDbContext();
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
        var userId = await db.Users.Where(user => user.NormalizedEmail == normalizedEmail).Select(user => user.Id).SingleAsync();
        Assert.Equal(2, await db.EmailTokens.CountAsync(token => token.UserId == userId && token.Purpose == "verify"));
        Assert.Equal(2, await db.MailOutbox.CountAsync(message => message.RecipientEmail == email));
    }

    [DockerFact]
    public async Task Missing_and_existing_login_paths_both_run_bounded_password_verification()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var samples = new List<(HttpResponseMessage Response, TimeSpan Elapsed)>();
        foreach (var candidate in new[] { email, UniqueEmail() })
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new LoginRequest(candidate, "Wrong-Password-2026", "电脑", "windows"));
            stopwatch.Stop();
            samples.Add((response, stopwatch.Elapsed));
        }

        Assert.All(samples, sample => Assert.Equal(HttpStatusCode.Unauthorized, sample.Response.StatusCode));
        Assert.All(samples, sample => Assert.True(sample.Elapsed >= TimeSpan.FromMilliseconds(75)));
        Assert.True(samples.Max(sample => sample.Elapsed) - samples.Min(sample => sample.Elapsed) < TimeSpan.FromSeconds(1));
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
        Assert.False(await fixture.IsJwtAcceptedAsync(pair.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await fixture.AuthorizeWithDefaultPolicyAsync(pair.AccessToken));

        var disabledLogin = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "电脑", "windows"));
        var disabledRefresh = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(pair.RefreshToken));

        Assert.Equal(HttpStatusCode.Forbidden, disabledLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabledRefresh.StatusCode);

        await fixture.SetDisabledAsync(email, false);
        await fixture.SetEmailVerifiedAsync(email, false);
        Assert.False(await fixture.IsJwtAcceptedAsync(pair.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await fixture.AuthorizeWithDefaultPolicyAsync(pair.AccessToken));
    }

    [DockerFact]
    public async Task Login_rechecks_the_locked_user_after_password_verification()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);

        var (response, reachedLockedRecheck) = await fixture.DisableWhileLoginWaitsAsync(email);

        Assert.True(reachedLockedRecheck);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", body, StringComparison.OrdinalIgnoreCase);
        await using var db = fixture.CreateDbContext();
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
        Assert.False(await db.DeviceSessions.AnyAsync(
            session => session.User!.NormalizedEmail == normalizedEmail));
    }

    [DockerFact]
    public async Task Forgot_password_invalid_missing_and_existing_inputs_share_response_and_coarse_timing()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var missing = UniqueEmail();

        var samples = new List<(HttpResponseMessage Response, TimeSpan Elapsed)>();
        foreach (var candidate in new[] { email, missing, "not-an-email" })
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await fixture.Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = candidate });
            stopwatch.Stop();
            samples.Add((response, stopwatch.Elapsed));
            await AssertAcceptedResponseAsync(response, "如果该邮箱存在，将收到密码重置邮件。");
        }

        Assert.All(samples, sample => Assert.True(sample.Elapsed >= TimeSpan.FromMilliseconds(75)));
        Assert.True(samples.Max(sample => sample.Elapsed) - samples.Min(sample => sample.Elapsed) < TimeSpan.FromMilliseconds(500));
        await using var db = fixture.CreateDbContext();
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
        var normalizedMissing = PostgreSqlAuthFixture.NormalizeEmail(missing);
        Assert.True(await db.EmailTokens.AnyAsync(token => token.Purpose == "reset" && token.User!.NormalizedEmail == normalizedEmail));
        Assert.False(await db.EmailTokens.AnyAsync(token => token.Purpose == "reset" && token.User!.NormalizedEmail == normalizedMissing));
    }

    [DockerFact]
    public async Task Concurrent_forgot_password_requests_create_only_one_token_and_outbox_record()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fixture.Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email })));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));

        await using var db = fixture.CreateDbContext();
        var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
        var userId = await db.Users.Where(user => user.NormalizedEmail == normalizedEmail).Select(user => user.Id).SingleAsync();
        Assert.Equal(1, await db.EmailTokens.CountAsync(token => token.UserId == userId && token.Purpose == "reset"));
        Assert.Equal(2, await db.MailOutbox.CountAsync(message => message.RecipientEmail == email));
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
            var outbox = await db.MailOutbox
                .Where(message => message.RecipientEmail == email)
                .OrderByDescending(message => message.Id)
                .FirstAsync();
            Assert.StartsWith("enc:v1:", outbox.BodyHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(resetToken, outbox.BodyHtml, StringComparison.Ordinal);
            Assert.Equal($"reset:{resetToken}", fixture.DecryptOutboxBody(outbox.BodyHtml));
        }
        var parallelResetToken = await fixture.InsertResetTokenAsync(email);
        const string newPassword = "New-Correct-Horse-2026";

        var reset = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new { token = resetToken, password = newPassword });
        var reuse = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new { token = resetToken, password = "Another-Password-2026" });
        var parallelReuse = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new { token = parallelResetToken, password = "Another-Password-2026" });
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
        Assert.Equal(HttpStatusCode.BadRequest, parallelReuse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        await using var resetDb = fixture.CreateDbContext();
        var normalizedEmailAfterReset = PostgreSqlAuthFixture.NormalizeEmail(email);
        Assert.False(await resetDb.EmailTokens.AnyAsync(
            token => token.Purpose == "reset" &&
                token.User!.NormalizedEmail == normalizedEmailAfterReset &&
                token.UsedAtUtc == null));
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

        await fixture.SetSettingAsync("EmailTokenCooldownSeconds", "10");
        await fixture.AgeEmailTokensAsync(email, "verify", TimeSpan.FromSeconds(11));
        var reissue = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "设置电脑", "windows"));
        Assert.Equal(HttpStatusCode.Forbidden, reissue.StatusCode);
        await using (var db = fixture.CreateDbContext())
        {
            var normalizedEmail = PostgreSqlAuthFixture.NormalizeEmail(email);
            Assert.Equal(2, await db.EmailTokens.CountAsync(
                token => token.Purpose == "verify" && token.User!.NormalizedEmail == normalizedEmail));
        }

        await fixture.DeleteSettingAsync("EmailTokenCooldownSeconds");
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
        var winner = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
        var winnerPair = (await winner.Content.ReadFromJsonAsync<TokenResponse>())!;
        var revokedWinner = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(winnerPair.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, revokedWinner.StatusCode);
    }

    [DockerFact]
    public async Task Disable_committed_during_refresh_prevents_any_successful_token_response()
    {
        var email = UniqueEmail();
        await fixture.RegisterAndVerifyAsync(email);
        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "竞态电脑", "windows"));
        var pair = await ReadTokenPairAsync(login);

        var refresh = await fixture.DisableWhileRefreshWaitsAsync(email, pair.RefreshToken);
        Assert.Equal(HttpStatusCode.Forbidden, refresh.StatusCode);
    }

    [DockerFact]
    public async Task Password_reset_and_refresh_do_not_deadlock()
    {
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var email = UniqueEmail();
            await fixture.RegisterAndVerifyAsync(email);
            var login = await fixture.Client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new LoginRequest(email, PostgreSqlAuthFixture.Password, $"锁序电脑-{iteration}", "windows"));
            var pair = await ReadTokenPairAsync(login);
            await fixture.Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
            var resetToken = await fixture.ReadLatestEmailTokenAsync(email, "reset");

            var (reset, refresh) = await fixture.RaceResetAndRefreshAsync(
                email,
                resetToken,
                pair.RefreshToken,
                $"Deadlock-Free-Password-{iteration}-2026");

            Assert.True(reset.IsSuccessStatusCode, $"Reset returned {(int)reset.StatusCode}.");
            Assert.Contains(refresh.StatusCode, new[]
            {
                HttpStatusCode.OK,
                HttpStatusCode.Unauthorized,
                HttpStatusCode.Forbidden,
            });
        }
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
        var verificationToken = await fixture.ReadLatestEmailTokenAsync(email, "verify");
        var login = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "日志电脑", "windows"));
        var pair = await ReadTokenPairAsync(login);
        await fixture.Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        var resetToken = await fixture.ReadLatestEmailTokenAsync(email, "reset");
        await fixture.Client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(pair.RefreshToken));
        var allLogs = string.Join('\n', fixture.Logs.Messages);

        Assert.DoesNotContain(PostgreSqlAuthFixture.Password, allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(pair.AccessToken, allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(pair.RefreshToken, allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(verificationToken, allLogs, StringComparison.Ordinal);
        Assert.DoesNotContain(resetToken, allLogs, StringComparison.Ordinal);
    }

    private static string UniqueEmail() => $"auth-{Guid.NewGuid():N}@example.com";

    private static async Task<TokenResponse> ReadTokenPairAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task AssertAcceptedResponseAsync(HttpResponseMessage response, string expectedMessage)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedMessage, body.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal(["message", "traceId"], body.EnumerateObject().Select(property => property.Name).Order());
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
    public string? LastAuthenticationFailure { get; private set; }

    public async Task InitializeAsync()
    {
        if (!DockerProcess.IsAvailable())
        {
            if (DockerProcess.IsRequiredForAcceptance())
            {
                throw new InvalidOperationException("Docker is required for MTPlayer real PostgreSQL acceptance tests.");
            }

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
        var bodies = await db.MailOutbox
            .Where(message => message.RecipientEmail == email.Trim())
            .OrderByDescending(message => message.Id)
            .Select(message => message.BodyHtml)
            .ToListAsync();
        var payload = bodies.Select(DecryptOutboxBody)
            .First(value => value.StartsWith($"{purpose}:", StringComparison.Ordinal));
        return payload[(purpose.Length + 1)..];
    }

    public string DecryptOutboxBody(string body)
    {
        const string prefix = "enc:v1:";
        Assert.StartsWith(prefix, body, StringComparison.Ordinal);
        var protector = Factory.Services.GetRequiredService<ISecretProtector>();
        return protector.Unprotect(body[prefix.Length..]);
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

    public async Task SetEmailVerifiedAsync(string email, bool verified)
    {
        await using var db = CreateDbContext();
        var normalizedEmail = NormalizeEmail(email);
        await db.Users
            .Where(user => user.NormalizedEmail == normalizedEmail)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.EmailVerified, verified));
    }

    public async Task<string> InsertResetTokenAsync(string email)
    {
        var tokenFactory = new TokenFactory();
        var token = tokenFactory.CreateRefreshToken();
        await using var db = CreateDbContext();
        var normalizedEmail = NormalizeEmail(email);
        var userId = await db.Users
            .Where(user => user.NormalizedEmail == normalizedEmail)
            .Select(user => user.Id)
            .SingleAsync();
        db.EmailTokens.Add(new EmailTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = tokenFactory.HashToken(token),
            Purpose = "reset",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
        });
        await db.SaveChangesAsync();
        return token;
    }

    public async Task AgeEmailTokensAsync(string email, string purpose, TimeSpan age)
    {
        await using var db = CreateDbContext();
        var normalizedEmail = NormalizeEmail(email);
        var createdAtUtc = DateTimeOffset.UtcNow.Subtract(age);
        await db.EmailTokens
            .Where(token => token.Purpose == purpose && token.User!.NormalizedEmail == normalizedEmail)
            .ExecuteUpdateAsync(update => update.SetProperty(token => token.CreatedAtUtc, createdAtUtc));
    }

    public async Task<HttpResponseMessage> DisableWhileRefreshWaitsAsync(string email, string refreshToken)
    {
        await using var db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var normalizedEmail = NormalizeEmail(email);
        await db.Users
            .Where(user => user.NormalizedEmail == normalizedEmail)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.Disabled, true));
        var refreshTask = Client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken));
        await Task.Delay(150);
        await transaction.CommitAsync();
        return await refreshTask;
    }

    public async Task<(HttpResponseMessage Response, bool ReachedLockedRecheck)> DisableWhileLoginWaitsAsync(
        string email)
    {
        await using var db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var normalizedEmail = NormalizeEmail(email);
        await db.Users
            .Where(user => user.NormalizedEmail == normalizedEmail)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.Disabled, true));
        var loginTask = Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, Password, "登录竞态电脑", "windows"));
        var reachedLockedRecheck = await WaitForDatabaseLockOrCompletionAsync(
            loginTask,
            "%FROM users%",
            "%FOR UPDATE%");
        await transaction.CommitAsync();
        var response = await loginTask.WaitAsync(TimeSpan.FromSeconds(10));
        return (response, reachedLockedRecheck);
    }

    public async Task<(HttpResponseMessage Reset, HttpResponseMessage Refresh)> RaceResetAndRefreshAsync(
        string email,
        string resetToken,
        string refreshToken,
        string newPassword)
    {
        await using var blocker = CreateDbContext();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        var normalizedEmail = NormalizeEmail(email);
        var userId = await blocker.Users
            .Where(user => user.NormalizedEmail == normalizedEmail)
            .Select(user => user.Id)
            .SingleAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM users WHERE \"Id\" = {userId} FOR UPDATE");

        var resetTask = Client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new { token = resetToken, password = newPassword });
        if (!await WaitForDatabaseLockOrCompletionAsync(resetTask, "%UPDATE users%"))
        {
            throw new InvalidOperationException("Reset completed before reaching the controlled user lock.");
        }

        var refreshTask = Client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshRequest(refreshToken));
        if (!await WaitForDatabaseLockOrCompletionAsync(refreshTask, "%FROM users%", "%FOR UPDATE%"))
        {
            throw new InvalidOperationException("Refresh completed before reaching the controlled user lock.");
        }

        await blockerTransaction.CommitAsync();
        var responses = await Task.WhenAll(
            resetTask.WaitAsync(TimeSpan.FromSeconds(10)),
            refreshTask.WaitAsync(TimeSpan.FromSeconds(10)));
        return (responses[0], responses[1]);
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
        LastAuthenticationFailure = result.Failure?.ToString();
        return result.Succeeded;
    }

    public async Task<HttpStatusCode> AuthorizeWithDefaultPolicyAsync(string accessToken)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {accessToken}";
        var policy = await scope.ServiceProvider
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetDefaultPolicyAsync();
        var evaluator = scope.ServiceProvider.GetRequiredService<IPolicyEvaluator>();
        var authentication = await evaluator.AuthenticateAsync(policy, context);
        var authorization = await evaluator.AuthorizeAsync(policy, authentication, context, resource: null);
        return authorization.Succeeded
            ? HttpStatusCode.OK
            : authorization.Challenged
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.Forbidden;
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

    private async Task<bool> WaitForDatabaseLockOrCompletionAsync(
        Task requestTask,
        string firstQueryPattern,
        string secondQueryPattern = "%")
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE @first_pattern
                  AND query ILIKE @second_pattern
            );
            """,
            connection);
        command.Parameters.AddWithValue("first_pattern", firstQueryPattern);
        command.Parameters.AddWithValue("second_pattern", secondQueryPattern);

        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (requestTask.IsCompleted)
            {
                return false;
            }

            if (await command.ExecuteScalarAsync() is true)
            {
                return true;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            $"Request did not reach the expected database lock for {firstQueryPattern} / {secondQueryPattern}.");
    }
}

public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> DockerAvailable = new(DockerProcess.IsAvailable);

    public DockerFactAttribute()
    {
        if (!DockerAvailable.Value && !DockerProcess.IsRequiredForAcceptance())
        {
            Skip = "Docker is unavailable; real PostgreSQL 16 integration test skipped.";
        }
    }
}

internal static class DockerProcess
{
    public static bool IsRequiredForAcceptance() =>
        string.Equals(
            Environment.GetEnvironmentVariable("MTPLAYER_REQUIRE_DOCKER_TESTS"),
            "1",
            StringComparison.Ordinal);

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
