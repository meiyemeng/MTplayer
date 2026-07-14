using System.Net;
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
    public async Task Root_request_starts_server()
    {
        using var factory = CreateFactory(TestPostgreSqlConnectionString, TestDataEncryptionKey);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
