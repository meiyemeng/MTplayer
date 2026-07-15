using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MTPlayer.Contracts;

namespace MTPlayer.Client.Core.Account;

public interface IAccountApiClient
{
    ServerBinding? Binding { get; }
    bool IsAuthenticated { get; }
    bool EmailVerified { get; }
    Task BindAsync(ServerBinding binding, CancellationToken cancellationToken = default);
    Task RegisterAsync(string email, string password, CancellationToken cancellationToken = default);
    Task LoginAsync(
        string email,
        string password,
        string deviceName,
        string platform,
        CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default);
}

public sealed class AccountApiClient(
    HttpClient httpClient,
    ITokenStore tokenStore,
    TimeProvider? timeProvider = null) : IAccountApiClient, IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAtUtc;
    private string? _refreshToken;

    public ServerBinding? Binding { get; private set; }

    public bool IsAuthenticated =>
        _refreshToken is not null ||
        (_accessToken is not null && _accessTokenExpiresAtUtc > _timeProvider.GetUtcNow());

    public bool EmailVerified { get; private set; }

    public async Task BindAsync(ServerBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Binding = binding;
        _accessToken = null;
        _accessTokenExpiresAtUtc = default;
        EmailVerified = false;
        _refreshToken = await tokenStore.ReadRefreshTokenAsync(binding, cancellationToken);
    }

    public async Task RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            Endpoint("api/v1/auth/register"),
            new RegisterRequest(email, password),
            WebJson,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task LoginAsync(
        string email,
        string password,
        string deviceName,
        string platform,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            Endpoint("api/v1/auth/login"),
            new LoginRequest(email, password, deviceName, platform),
            WebJson,
            cancellationToken);
        await AcceptTokensAsync(response, cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        _accessToken = null;
        _accessTokenExpiresAtUtc = default;
        _refreshToken = null;
        EmailVerified = false;
        await tokenStore.ClearAsync(cancellationToken);
    }

    public async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        await EnsureAccessTokenAsync(forceRefresh: false, expectedAccessToken: null, cancellationToken);
        var tokenUsed = _accessToken;
        var response = await SendOnceAsync(requestFactory, tokenUsed, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        await EnsureAccessTokenAsync(forceRefresh: true, tokenUsed, cancellationToken);
        return await SendOnceAsync(requestFactory, _accessToken, cancellationToken);
    }

    private async Task EnsureAccessTokenAsync(
        bool forceRefresh,
        string? expectedAccessToken,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!forceRefresh && _accessToken is not null && _accessTokenExpiresAtUtc > now.AddSeconds(30))
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (forceRefresh && expectedAccessToken is not null &&
                !string.Equals(expectedAccessToken, _accessToken, StringComparison.Ordinal) &&
                _accessToken is not null && _accessTokenExpiresAtUtc > now.AddSeconds(30))
            {
                return;
            }

            if (!forceRefresh && _accessToken is not null && _accessTokenExpiresAtUtc > now.AddSeconds(30))
            {
                return;
            }

            if (string.IsNullOrEmpty(_refreshToken))
            {
                throw new AccountApiException(HttpStatusCode.Unauthorized, "authentication_required");
            }

            using var response = await httpClient.PostAsJsonAsync(
                Endpoint("api/v1/auth/refresh"),
                new RefreshRequest(_refreshToken),
                WebJson,
                cancellationToken);
            await AcceptTokensAsync(response, cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task AcceptTokensAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorCodeAsync(response, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && error == "invalid_refresh_token")
            {
                await LogoutAsync(cancellationToken);
            }

            throw new AccountApiException(response.StatusCode, error);
        }

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(WebJson, cancellationToken) ??
            throw new AccountApiException(HttpStatusCode.BadGateway, "invalid_token_response");
        _accessToken = tokens.AccessToken;
        _accessTokenExpiresAtUtc = tokens.ExpiresAtUtc;
        _refreshToken = tokens.RefreshToken;
        EmailVerified = tokens.EmailVerified;
        await tokenStore.WriteRefreshTokenAsync(RequireBinding(), tokens.RefreshToken, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Func<HttpRequestMessage> requestFactory,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using var request = requestFactory() ?? throw new InvalidOperationException("Request factory returned null.");
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("Authorized request must have a relative or absolute URI.");
        }

        if (!request.RequestUri.IsAbsoluteUri)
        {
            request.RequestUri = new Uri(RequireBinding().BaseUri, request.RequestUri);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private Uri Endpoint(string relativePath) => new(RequireBinding().BaseUri, relativePath);

    private ServerBinding RequireBinding() =>
        Binding ?? throw new InvalidOperationException("Bind a server before using the account API.");

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new AccountApiException(
                response.StatusCode,
                await ReadErrorCodeAsync(response, cancellationToken));
        }
    }

    private static async Task<string> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("code", out var code)
                ? code.GetString() ?? "request_failed"
                : "request_failed";
        }
        catch (JsonException)
        {
            return "request_failed";
        }
    }

    public void Dispose() => _refreshGate.Dispose();
}

public sealed class AccountApiException(HttpStatusCode statusCode, string code) : Exception(code)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
