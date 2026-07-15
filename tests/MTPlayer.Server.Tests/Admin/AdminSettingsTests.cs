using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MTPlayer.Contracts;
using MTPlayer.Server.Auth;
using MTPlayer.Server.Data;
using MTPlayer.Server.Tests.Auth;
using MTPlayer.Server.Settings;
using Xunit;

namespace MTPlayer.Server.Tests.Admin;

public sealed class AdminSettingsTests(PostgreSqlAuthFixture fixture) : IClassFixture<PostgreSqlAuthFixture>
{
    [DockerFact]
    public async Task Setup_is_single_use_admin_only_and_sensitive_settings_are_encrypted_without_echo()
    {
        const string setupToken = "one-use-token-with-enough-entropy-2026";
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ADMIN_SETUP_TOKEN", setupToken));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var initialAdminRoot = await client.GetAsync("/admin");
        AssertRedirectWithoutCaching(initialAdminRoot, "/admin/setup");

        var created = await client.PostAsJsonAsync("/admin/setup", new
        {
            token = setupToken,
            email = $"owner-{Guid.NewGuid():N}@example.com",
            password = "Owner-Password-2026",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        AssertRedirectWithoutCaching(await client.GetAsync("/admin"), "/admin/login");

        var retry = await client.PostAsJsonAsync("/admin/setup", new
        {
            token = setupToken,
            email = $"other-{Guid.NewGuid():N}@example.com",
            password = "Owner-Password-2026",
        });
        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/setup")).StatusCode);
        await using (var restartedFactory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ADMIN_SETUP_TOKEN", setupToken)))
        using (var restartedClient = restartedFactory.CreateClient())
        {
            Assert.Equal(HttpStatusCode.NotFound, (await restartedClient.GetAsync("/admin/setup")).StatusCode);
        }

        var ownerEmail = (await created.Content.ReadFromJsonAsync<SetupCreated>())!.Email;
        using (var browser = factory.CreateClient(new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        }))
        {
            var loginPage = await browser.GetStringAsync("/admin/login");
            Assert.Contains("管理员登录", loginPage, StringComparison.Ordinal);
            var loginToken = ReadAntiforgeryToken(loginPage);
            var browserLogin = await browser.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = loginToken,
                ["Input.Email"] = ownerEmail,
                ["Input.Password"] = "Owner-Password-2026",
                ["Input.RememberMe"] = "false",
                ["ReturnUrl"] = "/admin/settings",
            }));
            Assert.Equal(HttpStatusCode.Redirect, browserLogin.StatusCode);
            var adminCookie = Assert.Single(browserLogin.Headers.GetValues("Set-Cookie"), value => value.Contains("__Host-MTPlayerAdmin", StringComparison.Ordinal));
            Assert.Contains("secure", adminCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", adminCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", adminCookie, StringComparison.OrdinalIgnoreCase);
            AssertRedirectWithoutCaching(await browser.GetAsync("/admin"), "/admin/settings");
            var settingsPage = await browser.GetAsync("/admin/settings");
            Assert.Equal(HttpStatusCode.OK, settingsPage.StatusCode);
            var usersPage = await browser.GetAsync("/admin/users");
            Assert.Equal(HttpStatusCode.OK, usersPage.StatusCode);
            Assert.Contains(ownerEmail, await usersPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/admin/logout", new FormUrlEncodedContent([]))).StatusCode);
            var logoutToken = ReadAntiforgeryToken(await settingsPage.Content.ReadAsStringAsync());
            Assert.Equal(
                HttpStatusCode.Redirect,
                (await browser.PostAsync("/admin/logout", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = logoutToken,
                }))).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/admin/settings")).StatusCode);
        }

        var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(ownerEmail, "Owner-Password-2026", "后台测试", "web"));
        var ownerTokens = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerTokens.AccessToken);

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync("/api/v1/admin/email/test", new { recipientEmail = ownerEmail })).StatusCode);
        var invalid = await client.PutAsJsonAsync("/api/v1/admin/settings", new
        {
            publicBaseUrl = "https://example.com/path?query=1#fragment",
            smtpHost = "smtp.example.net",
            smtpPort = 587,
            smtpUsername = "mailer@example.net",
            newSmtpPassword = "password",
            smtpFromName = "MT播放器",
            smtpFromAddress = "mailer@example.net",
            smtpUseTls = true,
            registrationEnabled = true,
            requireVerifiedEmail = true,
            passwordResetEnabled = true,
            emailVerificationTokenExpiryMinutes = 60,
            passwordResetTokenExpiryMinutes = 30,
            verificationSubjectTemplate = "{unknown}",
            verificationBodyTemplate = "{verificationUrl}",
            resetSubjectTemplate = "重置",
            resetBodyTemplate = "{resetUrl}",
            testSubjectTemplate = "测试",
            testBodyTemplate = "{email}",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var publicUrl = $"https://{Guid.NewGuid():N}.example.com";
        var smtpPassword = "smtp-password-that-must-never-echo";
        var saved = await client.PutAsJsonAsync("/api/v1/admin/settings", new
        {
            publicBaseUrl = publicUrl,
            smtpHost = "smtp.example.net",
            smtpPort = 587,
            smtpUsername = "mailer@example.net",
            newSmtpPassword = smtpPassword,
            smtpFromName = "MT播放器",
            smtpFromAddress = "mailer@example.net",
            smtpUseTls = true,
            registrationEnabled = true,
            requireVerifiedEmail = true,
            passwordResetEnabled = true,
            emailVerificationTokenExpiryMinutes = 60,
            passwordResetTokenExpiryMinutes = 30,
            verificationSubjectTemplate = "验证邮箱",
            verificationBodyTemplate = "<p>{email}</p><a href=\"{verificationUrl}\">验证</a><p>{expiresMinutes}</p>",
            resetSubjectTemplate = "重置密码",
            resetBodyTemplate = "<p>{email}</p><a href=\"{resetUrl}\">重置</a><p>{expiresMinutes}</p>",
            testSubjectTemplate = "SMTP 测试",
            testBodyTemplate = "<p>{email}</p>",
        });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var publicUrlProbe = Assert.IsType<AcceptingPublicBaseUrlProbe>(
            factory.Services.GetRequiredService<IPublicBaseUrlProbe>());
        Assert.Contains(new Uri(publicUrl), publicUrlProbe.ProbedUris);

        var returned = await client.GetStringAsync("/api/v1/admin/settings");
        Assert.DoesNotContain(smtpPassword, returned, StringComparison.Ordinal);
        Assert.DoesNotContain(publicUrl, returned, StringComparison.Ordinal);
        Assert.Contains("\"smtpPasswordConfigured\":true", returned, StringComparison.Ordinal);
        Assert.Contains("\"publicBaseUrlConfigured\":true", returned, StringComparison.Ordinal);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/api/v1/admin/email/test", new { recipientEmail = ownerEmail })).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
            var secrets = await db.SystemSettings
                .Where(setting => setting.Key == "SmtpPassword" || setting.Key == "PublicBaseUrl")
                .ToDictionaryAsync(setting => setting.Key);
            Assert.All(secrets.Values, setting => Assert.True(setting.IsEncrypted));
            Assert.DoesNotContain(smtpPassword, secrets["SmtpPassword"].Value, StringComparison.Ordinal);
            Assert.DoesNotContain(publicUrl, secrets["PublicBaseUrl"].Value, StringComparison.Ordinal);
        }

        client.DefaultRequestHeaders.Authorization = null;
        var userEmail = $"user-{Guid.NewGuid():N}@example.com";
        await fixture.RegisterAndVerifyAsync(userEmail);
        var userLogin = await fixture.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(userEmail, PostgreSqlAuthFixture.Password, "普通用户", "windows"));
        var userTokens = (await userLogin.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userTokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/settings")).StatusCode);
    }

    private sealed record SetupCreated(string Email);

    private static string ReadAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, html);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static void AssertRedirectWithoutCaching(HttpResponseMessage response, string location)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
