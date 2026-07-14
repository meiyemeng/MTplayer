using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MTPlayer.Server.Tests;

public sealed class ServerSmokeTests
{
    [Fact]
    public async Task Root_request_starts_server()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
