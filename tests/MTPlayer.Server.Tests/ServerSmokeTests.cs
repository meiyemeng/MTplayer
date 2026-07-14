using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MTPlayer.Server.Tests;

public sealed class ServerSmokeTests
{
    private const string PostgreSqlConfigurationKey = "ConnectionStrings:PostgreSQL";
    private const string TestPostgreSqlConnectionString =
        "Host=localhost;Database=mtplayer_tests;Username=mtplayer_tests";

    [Fact]
    public async Task Root_request_starts_server()
    {
        using var factory = CreateFactory(TestPostgreSqlConnectionString);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Missing_postgresql_connection_string_fails_during_server_startup()
    {
        using var factory = CreateFactory(string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(PostgreSqlConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting(PostgreSqlConfigurationKey, connectionString));
}
