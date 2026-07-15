using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using MTPlayer.Contracts;
using MTPlayer.Server.Data;
using MTPlayer.Server.Tests.Auth;
using Xunit;

namespace MTPlayer.Server.Tests.Deployment;

public sealed class ForwardedHeadersTests(PostgreSqlAuthFixture fixture) : IClassFixture<PostgreSqlAuthFixture>
{
    [DockerFact]
    public async Task Trusted_forwarded_https_host_is_used_for_generated_client_configuration()
    {
        const string forwardedHost = "media.example.com";
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AllowedHosts", $"localhost;{forwardedHost}");
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", "0.0.0.0/0");
        });
        using var client = factory.CreateClient();
        var email = $"forwarded-{Guid.NewGuid():N}@example.com";
        await fixture.RegisterAndVerifyAsync(email);
        await using (var db = fixture.CreateDbContext())
        {
            var normalized = PostgreSqlAuthFixture.NormalizeEmail(email);
            await db.Users.Where(user => user.NormalizedEmail == normalized)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.Role, "admin"));
        }

        var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(email, PostgreSqlAuthFixture.Password, "部署检查", "web"));
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/client-config");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", forwardedHost);
        request.Headers.Add("X-Forwarded-For", "203.0.113.10");
        request.Headers.Add("X-Request-ID", "deployment-check-2026");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"https://{forwardedHost}", body, StringComparison.Ordinal);
        Assert.DoesNotContain(":8080", body, StringComparison.Ordinal);
        Assert.Equal("deployment-check-2026", Assert.Single(response.Headers.GetValues("X-Request-ID")));
    }

    [DockerFact]
    public async Task Live_and_ready_health_are_healthy_after_migrations()
    {
        using var client = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("Healthy", await ready.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_stays_healthy_but_readiness_fails_when_PostgreSQL_is_unavailable()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "ConnectionStrings:PostgreSQL",
                "Host=127.0.0.1;Port=1;Database=missing;Username=missing;Password=missing;Timeout=1;Command Timeout=1;Pooling=false");
            builder.UseSetting("DATA_ENCRYPTION_KEY", "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
            builder.UseSetting("Mail:WorkerEnabled", "false");
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
