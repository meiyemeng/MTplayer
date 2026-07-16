using System.Net.Http;
using System.Text.Json;
using WebHtv.Core.Catalogue;
using WebHtv.Core.Configuration;

namespace WebHtv.Catalogue;

/// <summary>
/// Native execution for the HTTP JSON site types. Spider-backed type 3 sites are deliberately
/// rejected here and are handled by the dedicated Spider runtime module.
/// </summary>
public sealed class HttpTvBoxCatalogueProvider(HttpClient httpClient) : ITvBoxCatalogueProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public bool CanHandle(TvBoxSite site) =>
        site.Type is 1 or 2 or 4 && Uri.TryCreate(site.Api, UriKind.Absolute, out var address) &&
        (string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    public async Task<CataloguePage> SearchAsync(TvBoxSite site, string keyword, int page, CancellationToken cancellationToken = default)
    {
        EnsureSupported(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        var parameters = new Dictionary<string, string>
        {
            ["ac"] = "detail",
            ["wd"] = keyword,
            ["quick"] = "false",
            ["extend"] = string.Empty
        };
        if (page > 1) parameters["pg"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return TvBoxJsonResultParser.ParsePage(site.Key, await GetStringAsync(site, parameters, cancellationToken));
    }

    public async Task<CatalogueDetail> GetDetailAsync(TvBoxSite site, string id, CancellationToken cancellationToken = default)
    {
        EnsureSupported(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var parameters = new Dictionary<string, string>
        {
            ["ac"] = "detail",
            ["ids"] = id
        };
        return TvBoxJsonResultParser.ParseDetail(site.Key, await GetStringAsync(site, parameters, cancellationToken));
    }

    public PlayRequest CreatePlayRequest(TvBoxSite site, EpisodeSource source, Episode episode)
    {
        EnsureSupported(site);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(episode);
        return new PlayRequest(episode.Url, source.Name, !LooksLikeDirectMedia(episode.Url), ExtractHeaders(site.Header));
    }

    private async Task<string> GetStringAsync(TvBoxSite site, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AddQueryParameters(site.Api, parameters));
        foreach (var header in ExtractHeaders(site.Header))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static void EnsureSupported(TvBoxSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (site.Type == 3)
        {
            throw new NotSupportedException("该站点依赖 Spider 运行时，将由 Spider 模块处理。");
        }

        if (site.Type == 0)
        {
            throw new NotSupportedException("XML 站点将在 XML 兼容模块中处理。");
        }

        if (site.Type is not 1 and not 2 and not 4)
        {
            throw new NotSupportedException($"不支持的站点类型：{site.Type}。");
        }
    }

    private static Uri AddQueryParameters(string address, IReadOnlyDictionary<string, string> parameters)
    {
        var builder = new UriBuilder(address);
        var replacementKeys = parameters.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Select(parts => new KeyValuePair<string, string>(
                Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty))
            .Where(pair => !replacementKeys.Contains(pair.Key))
            .Concat(parameters)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private static Dictionary<string, string> ExtractHeaders(JsonElement? header)
    {
        if (header is not { ValueKind: JsonValueKind.Object } objectHeader)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return objectHeader.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDirectMedia(string value)
    {
        var path = value.Split('?', 2)[0];
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".flv", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
    }
}
