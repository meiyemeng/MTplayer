using System.Net.Http;
using System.Text.Json;
using WebHtv.Core.Catalogue;
using WebHtv.Core.Configuration;

namespace WebHtv.Desktop;

internal sealed class ParserResolver(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<PlayRequest?> ResolveAsync(IReadOnlyList<TvBoxParser> parsers, PlayRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var parser in parsers.Where(item => item.Type == 1 && !string.IsNullOrWhiteSpace(item.Url)))
        {
            try
            {
                var address = BuildAddress(parser.Url!, request.Url);
                using var response = await _httpClient.GetAsync(address, HttpCompletionOption.ResponseContentRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var resolved = TryReadResponse(await response.Content.ReadAsStringAsync(cancellationToken), request);
                if (resolved is not null) return resolved;
            }
            catch (Exception)
            {
                // Parsers are independent fallbacks.
            }
        }

        return null;
    }

    internal static string BuildAddress(string parserUrl, string mediaUrl) =>
        parserUrl.Contains("{url}", StringComparison.OrdinalIgnoreCase)
            ? parserUrl.Replace("{url}", Uri.EscapeDataString(mediaUrl), StringComparison.OrdinalIgnoreCase)
            : parserUrl + Uri.EscapeDataString(mediaUrl);

    private static PlayRequest? TryReadResponse(string responseText, PlayRequest original)
    {
        var candidate = responseText.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var directAddress) &&
            (directAddress.Scheme == Uri.UriSchemeHttp || directAddress.Scheme == Uri.UriSchemeHttps))
        {
            return original with { Url = directAddress.AbsoluteUri, RequiresParser = false };
        }

        try
        {
            using var document = JsonDocument.Parse(candidate);
            var root = document.RootElement;
            if (!root.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var parsedAddress)) return null;

            var headers = new Dictionary<string, string>(original.Headers, StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("header", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in headerElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String) headers[property.Name] = property.Value.GetString()!;
                }
            }

            return new PlayRequest(parsedAddress.AbsoluteUri, original.Flag, false, headers);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
