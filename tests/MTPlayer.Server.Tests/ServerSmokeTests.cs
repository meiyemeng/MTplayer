using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MTPlayer.Server.Tests;

public sealed class ServerSmokeTests
{
    private const string PostgreSqlConfigurationKey = "ConnectionStrings:PostgreSQL";
    private const string DataEncryptionKeyConfigurationKey = "DATA_ENCRYPTION_KEY";
    private const string TestPostgreSqlConnectionString =
        "Host=localhost;Database=mtplayer_tests;Username=mtplayer_tests";
    private static readonly string TestDataEncryptionKey = Convert.ToBase64String(
        Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    [Fact]
    public async Task Root_request_redirects_to_web_player()
    {
        using var factory = CreateFactory(TestPostgreSqlConnectionString, TestDataEncryptionKey);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/player", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Web_player_and_local_media_signing_are_available_without_login()
    {
        using var factory = CreateFactory(TestPostgreSqlConnectionString, TestDataEncryptionKey);
        using var client = factory.CreateClient();

        using var page = await client.GetAsync(new Uri("/player", UriKind.Relative));
        var html = await page.Content.ReadAsStringAsync();
        using var signed = await client.PostAsJsonAsync(
            new Uri("/api/v1/web/media/sign", UriKind.Relative),
            new { url = "https://media.example/video.m3u8" });

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("MT播放器 · 网页客户端", html, StringComparison.Ordinal);
        Assert.Contains("/js/web-client.js", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
    }

    [Fact]
    public void Missing_postgresql_connection_string_fails_during_server_startup()
    {
        using var factory = CreateFactory(string.Empty, TestDataEncryptionKey);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(PostgreSqlConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString,
        string dataEncryptionKey) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(PostgreSqlConfigurationKey, connectionString);
            builder.UseSetting(DataEncryptionKeyConfigurationKey, dataEncryptionKey);
        });
}
