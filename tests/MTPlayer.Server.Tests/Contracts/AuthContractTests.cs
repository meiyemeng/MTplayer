using System.Globalization;
using System.Text.Json;
using MTPlayer.Contracts;
using Xunit;

namespace MTPlayer.Server.Tests.Contracts;

public sealed class AuthContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RegisterRequest_ToString_does_not_include_password()
    {
        const string secret = "register-password-secret";
        var text = new RegisterRequest("user@example.com", secret).ToString();

        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains("user@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginRequest_ToString_does_not_include_password()
    {
        const string secret = "login-password-secret";
        var text = new LoginRequest("user@example.com", secret, "Living Room", "windows").ToString();

        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains("Living Room", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshRequest_ToString_does_not_include_refresh_token()
    {
        const string secret = "refresh-request-secret";
        var text = new RefreshRequest(secret).ToString();

        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenResponse_ToString_does_not_include_tokens()
    {
        const string accessToken = "access-token-secret";
        const string refreshToken = "refresh-token-secret";
        var value = new TokenResponse(
            accessToken,
            refreshToken,
            DateTimeOffset.Parse("2026-07-14T00:15:00Z", CultureInfo.InvariantCulture),
            true);

        var text = value.ToString();

        Assert.DoesNotContain(accessToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, text, StringComparison.Ordinal);
        Assert.Contains(nameof(TokenResponse.EmailVerified), text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceCodeResponse_ToString_does_not_include_secret_codes()
    {
        const string deviceCode = "device-code-secret";
        const string userCode = "ABCD-EFGH";
        var value = new DeviceCodeResponse(
            deviceCode,
            userCode,
            new Uri("https://mt.example/verify"),
            DateTimeOffset.Parse("2026-07-14T00:10:00Z", CultureInfo.InvariantCulture),
            5);

        var text = value.ToString();

        Assert.DoesNotContain(deviceCode, text, StringComparison.Ordinal);
        Assert.DoesNotContain(userCode, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Auth_contracts_serialize_with_stable_web_json_names()
    {
        var expiresAt = DateTimeOffset.Parse("2026-07-14T00:15:00Z", CultureInfo.InvariantCulture);

        Assert.Equal(
            """{"email":"user@example.com","password":"password-value"}""",
            JsonSerializer.Serialize(new RegisterRequest("user@example.com", "password-value"), WebJson));
        Assert.Equal(
            """{"email":"user@example.com","password":"password-value","deviceName":"Living Room","platform":"windows"}""",
            JsonSerializer.Serialize(
                new LoginRequest("user@example.com", "password-value", "Living Room", "windows"),
                WebJson));
        Assert.Equal(
            """{"refreshToken":"refresh-value"}""",
            JsonSerializer.Serialize(new RefreshRequest("refresh-value"), WebJson));
        Assert.Equal(
            """{"accessToken":"access-value","refreshToken":"refresh-value","expiresAtUtc":"2026-07-14T00:15:00+00:00","emailVerified":true}""",
            JsonSerializer.Serialize(new TokenResponse("access-value", "refresh-value", expiresAt, true), WebJson));
        Assert.Equal(
            """{"deviceCode":"device-value","userCode":"ABCD-EFGH","verificationUri":"https://mt.example/verify","expiresAtUtc":"2026-07-14T00:15:00+00:00","pollIntervalSeconds":5}""",
            JsonSerializer.Serialize(
                new DeviceCodeResponse(
                    "device-value",
                    "ABCD-EFGH",
                    new Uri("https://mt.example/verify"),
                    expiresAt,
                    5),
                WebJson));
    }

    [Fact]
    public void Auth_contracts_deserialize_from_independent_fixed_json()
    {
        var register = JsonSerializer.Deserialize<RegisterRequest>(
            """{"email":"user@example.com","password":"password-value"}""",
            WebJson);
        var login = JsonSerializer.Deserialize<LoginRequest>(
            """{"email":"user@example.com","password":"password-value","deviceName":"Living Room","platform":"windows"}""",
            WebJson);
        var refresh = JsonSerializer.Deserialize<RefreshRequest>(
            """{"refreshToken":"refresh-value"}""",
            WebJson);
        var token = JsonSerializer.Deserialize<TokenResponse>(
            """{"accessToken":"access-value","refreshToken":"refresh-value","expiresAtUtc":"2026-07-14T00:15:00+00:00","emailVerified":true}""",
            WebJson);
        var device = JsonSerializer.Deserialize<DeviceCodeResponse>(
            """{"deviceCode":"device-value","userCode":"ABCD-EFGH","verificationUri":"https://mt.example/verify","expiresAtUtc":"2026-07-14T00:15:00+00:00","pollIntervalSeconds":5}""",
            WebJson);

        Assert.Equal(new RegisterRequest("user@example.com", "password-value"), register);
        Assert.Equal(new LoginRequest("user@example.com", "password-value", "Living Room", "windows"), login);
        Assert.Equal(new RefreshRequest("refresh-value"), refresh);
        Assert.Equal(
            new TokenResponse(
                "access-value",
                "refresh-value",
                DateTimeOffset.Parse("2026-07-14T00:15:00Z", CultureInfo.InvariantCulture),
                true),
            token);
        Assert.Equal(
            new DeviceCodeResponse(
                "device-value",
                "ABCD-EFGH",
                new Uri("https://mt.example/verify"),
                DateTimeOffset.Parse("2026-07-14T00:15:00Z", CultureInfo.InvariantCulture),
                5),
            device);
    }
}
