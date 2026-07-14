namespace MTPlayer.Contracts;

public sealed record RegisterRequest(string Email, string Password)
{
    public override string ToString() => $"{nameof(RegisterRequest)} {{ Email = {Email} }}";
}

public sealed record LoginRequest(string Email, string Password, string DeviceName, string Platform)
{
    public override string ToString() =>
        $"{nameof(LoginRequest)} {{ Email = {Email}, DeviceName = {DeviceName}, Platform = {Platform} }}";
}

public sealed record RefreshRequest(string RefreshToken)
{
    public override string ToString() => $"{nameof(RefreshRequest)} {{ }}";
}

public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    bool EmailVerified)
{
    public override string ToString() =>
        $"{nameof(TokenResponse)} {{ EmailVerified = {EmailVerified} }}";
}

public sealed record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    DateTimeOffset ExpiresAtUtc,
    int PollIntervalSeconds)
{
    public override string ToString() =>
        $"{nameof(DeviceCodeResponse)} {{ UserCode = {UserCode}, PollIntervalSeconds = {PollIntervalSeconds} }}";
}
