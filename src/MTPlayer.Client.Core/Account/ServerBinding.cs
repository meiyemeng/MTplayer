namespace MTPlayer.Client.Core.Account;

public sealed record ServerBinding(Uri BaseUri)
{
    public static bool TryCreate(
        string? value,
        bool allowInsecureLoopback,
        out ServerBinding? binding)
    {
        binding = null;
        var candidate = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(candidate) ||
            !Uri.TryCreate($"{candidate}/", UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var loopbackDebug = allowInsecureLoopback && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;
        if (uri.Scheme != Uri.UriSchemeHttps && !loopbackDebug)
        {
            return false;
        }

        if (uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        if ((uri.Scheme == Uri.UriSchemeHttps && uri.Port != 443) ||
            (uri.Scheme == Uri.UriSchemeHttp && uri.Port != 80))
        {
            return false;
        }

        binding = new ServerBinding(uri);
        return true;
    }

    public override string ToString() => BaseUri.GetLeftPart(UriPartial.Authority);
}

public interface ITokenStore
{
    Task<string?> ReadRefreshTokenAsync(ServerBinding binding, CancellationToken cancellationToken = default);
    Task WriteRefreshTokenAsync(ServerBinding binding, string refreshToken, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
