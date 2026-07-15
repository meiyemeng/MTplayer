using System.Net;
using System.Net.Http.Json;
using MTPlayer.Client.Core.Account;
using MTPlayer.Contracts;
using Xunit;

namespace MTPlayer.Client.Core.Tests.Account;

public sealed class AccountApiClientTests
{
    [Theory]
    [InlineData("https://sync.example.com", true)]
    [InlineData("https://sync.example.com:443", true)]
    [InlineData("http://sync.example.com", false)]
    [InlineData("https://sync.example.com/path", false)]
    [InlineData("https://user:password@sync.example.com", false)]
    [InlineData("https://sync.example.com:8443", false)]
    public void Production_server_binding_requires_an_https_origin(string value, bool valid)
    {
        Assert.Equal(valid, ServerBinding.TryCreate(value, false, out _));
    }

    [Fact]
    public void Development_binding_allows_only_insecure_loopback()
    {
        Assert.True(ServerBinding.TryCreate("http://127.0.0.1", true, out _));
        Assert.True(ServerBinding.TryCreate("http://localhost", true, out _));
        Assert.False(ServerBinding.TryCreate("http://192.168.1.2", true, out _));
    }

    [Fact]
    public async Task Concurrent_authorized_requests_share_one_refresh_and_rotated_token_is_persisted()
    {
        var store = new FakeTokenStore { Token = "old-refresh" };
        var refreshCount = 0;
        var handler = new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref refreshCount);
                await Task.Delay(50);
                return Json(HttpStatusCode.OK, Tokens("new-access", "new-refresh"));
            }

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("new-access", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK, new { devices = Array.Empty<object>() });
        });
        using var account = new AccountApiClient(new HttpClient(handler), store);
        Assert.True(ServerBinding.TryCreate("https://sync.example.com", false, out var binding));
        await account.BindAsync(binding!);

        var responses = await Task.WhenAll(
            account.SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/v1/devices")),
            account.SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/v1/devices")));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, refreshCount);
        Assert.Equal("new-refresh", store.Token);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public async Task Unauthorized_access_is_refreshed_once_and_request_factory_creates_the_retry()
    {
        var store = new FakeTokenStore();
        var protectedCalls = 0;
        var requestFactoryCalls = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, Tokens("access-one", "refresh-one")));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, Tokens("access-two", "refresh-two")));
            }

            var call = Interlocked.Increment(ref protectedCalls);
            return Task.FromResult(call == 1
                ? Json(HttpStatusCode.Unauthorized, new { code = "expired_access_token" })
                : Json(HttpStatusCode.OK, new { ok = true }));
        });
        using var account = new AccountApiClient(new HttpClient(handler), store);
        Assert.True(ServerBinding.TryCreate("https://sync.example.com", false, out var binding));
        await account.BindAsync(binding!);
        await account.LoginAsync("user@example.com", "password-2026", "电脑", "windows");

        var response = await account.SendAuthorizedAsync(() =>
        {
            Interlocked.Increment(ref requestFactoryCalls);
            return new HttpRequestMessage(HttpMethod.Get, "api/v1/devices");
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, requestFactoryCalls);
        Assert.Equal("refresh-two", store.Token);
    }

    [Fact]
    public async Task Invalid_refresh_clears_tokens_but_keeps_server_binding()
    {
        var store = new FakeTokenStore { Token = "invalid-refresh" };
        var handler = new DelegateHandler(request => Task.FromResult(
            Json(HttpStatusCode.Unauthorized, new { code = "invalid_refresh_token" })));
        using var account = new AccountApiClient(new HttpClient(handler), store);
        Assert.True(ServerBinding.TryCreate("https://sync.example.com", false, out var binding));
        await account.BindAsync(binding!);

        var error = await Assert.ThrowsAsync<AccountApiException>(() =>
            account.SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/v1/devices")));

        Assert.Equal("invalid_refresh_token", error.Code);
        Assert.Null(store.Token);
        Assert.Equal(1, store.ClearCount);
        Assert.Equal(binding, account.Binding);
        Assert.False(account.IsAuthenticated);
    }

    private static TokenResponse Tokens(string access, string refresh) =>
        new(access, refresh, DateTimeOffset.UtcNow.AddMinutes(15), true);

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) => new(status)
    {
        Content = JsonContent.Create(value),
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }

    private sealed class FakeTokenStore : ITokenStore
    {
        public string? Token { get; set; }
        public int WriteCount { get; private set; }
        public int ClearCount { get; private set; }

        public Task<string?> ReadRefreshTokenAsync(
            ServerBinding binding,
            CancellationToken cancellationToken = default) => Task.FromResult(Token);

        public Task WriteRefreshTokenAsync(
            ServerBinding binding,
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            Token = refreshToken;
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Token = null;
            ClearCount++;
            return Task.CompletedTask;
        }
    }
}
