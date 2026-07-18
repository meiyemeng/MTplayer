using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace MTPlayer.Server.Auth;

public sealed record ClientLocation(string? IpAddress, string? City);

public sealed class ClientLocationService(HttpClient http, IConfiguration configuration, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<ClientLocation> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var ip = ReadClientIp(context);
        var city = NormalizeCity(context.Request.Headers["CF-IPCity"].ToString());
        if (city is not null || ip is null || !IsPublic(ip)) return new ClientLocation(ip?.ToString(), city);

        var key = ip.ToString();
        var now = timeProvider.GetUtcNow();
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
            return new ClientLocation(key, cached.City);

        var template = configuration["IpGeolocation:EndpointTemplate"]
            ?? "https://ipwho.is/{ip}?fields=success,city&lang=zh";
        if (string.IsNullOrWhiteSpace(template)) return new ClientLocation(key, null);
        try
        {
            var address = template.Replace("{ip}", Uri.EscapeDataString(key), StringComparison.Ordinal);
            using var response = await http.GetAsync(address, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                return new ClientLocation(key, null);
            city = root.TryGetProperty("city", out var value) && value.ValueKind == JsonValueKind.String
                ? NormalizeCity(value.GetString())
                : null;
            _cache[key] = new CacheEntry(city, now.AddHours(24));
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            _cache[key] = new CacheEntry(null, now.AddMinutes(10));
        }
        return new ClientLocation(key, city);
    }

    private static IPAddress? ReadClientIp(HttpContext context)
    {
        var cloudflare = context.Request.Headers["CF-Connecting-IP"].ToString();
        if (IPAddress.TryParse(cloudflare, out var parsed)) return Normalize(parsed);
        return context.Connection.RemoteIpAddress is { } remote ? Normalize(remote) : null;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string? NormalizeCity(string? value)
    {
        var city = value?.Trim();
        return string.IsNullOrWhiteSpace(city) ? null : city.Length <= 200 ? city : city[..200];
    }

    private static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] != 10 && bytes[0] != 127 &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] == 168);
        }
        return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal &&
            !(bytes.Length > 0 && (bytes[0] & 0xfe) == 0xfc);
    }

    private sealed record CacheEntry(string? City, DateTimeOffset ExpiresAtUtc);
}
