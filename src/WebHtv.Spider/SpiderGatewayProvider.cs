using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WebHtv.Catalogue;
using WebHtv.Core.Catalogue;
using WebHtv.Core.Configuration;

namespace WebHtv.Spider;

public sealed record SpiderGatewayLiveChannel(
    string Group,
    string Name,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? LogoUrl = null,
    string? EpgUrl = null);

/// <summary>
/// Delegates Android-only csp_* spiders to an MTPlayer Android client on the LAN.
/// The Android client remains the runtime owner; this provider never executes an
/// untrusted DEX/JAR inside the Windows process.
/// </summary>
public sealed class SpiderGatewayProvider(HttpClient httpClient) : ITvBoxCatalogueProvider, IAsyncPlayRequestProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private Uri? _gateway;
    private string _token = string.Empty;

    public bool IsConfigured => _gateway is not null && !string.IsNullOrWhiteSpace(_token);

    public void Configure(string? address, string? token)
    {
        _gateway = Uri.TryCreate(address?.Trim().TrimEnd('/'), UriKind.Absolute, out var parsed) &&
                   parsed.Scheme is "http" or "https" ? parsed : null;
        _token = token?.Trim() ?? string.Empty;
    }

    public bool CanHandle(TvBoxSite site) =>
        IsConfigured && site.Type == 3 && site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(site.Jar);

    public async Task ConfigureProfileAsync(
        string profileUrl,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return;
        if (!Uri.TryCreate(profileUrl, UriKind.Absolute, out var source) ||
            source.Scheme is not ("http" or "https"))
            throw new ArgumentException("配置地址必须是有效的 HTTP/HTTPS 地址。", nameof(profileUrl));
        await InvokeAsync("config", new
        {
            url = source.ToString(),
            name = string.IsNullOrWhiteSpace(profileName) ? source.Host : profileName.Trim()
        }, cancellationToken);
    }

    public async Task<CataloguePage> SearchAsync(TvBoxSite site, string keyword, int page, CancellationToken cancellationToken = default) =>
        TvBoxJsonResultParser.ParsePage(site.RuntimeKey, await InvokeSiteAsync("search", site, new { keyword, page }, cancellationToken));

    public async Task<CatalogueDetail> GetDetailAsync(TvBoxSite site, string id, CancellationToken cancellationToken = default) =>
        TvBoxJsonResultParser.ParseDetail(site.RuntimeKey, await InvokeSiteAsync("detail", site, new { id }, cancellationToken));

    public PlayRequest CreatePlayRequest(TvBoxSite site, EpisodeSource source, Episode episode) =>
        new(episode.Url, source.Name, true, new Dictionary<string, string>());

    public async Task<PlayRequest> CreatePlayRequestAsync(
        TvBoxSite site,
        EpisodeSource source,
        Episode episode,
        CancellationToken cancellationToken = default)
    {
        var json = await InvokeSiteAsync("player", site, new { flag = source.Name, id = episode.Url }, cancellationToken);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = document.RootElement;
        var url = root.TryGetProperty("url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String
            ? urlValue.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidDataException("Spider 没有返回播放地址。");
        var requiresParser = root.TryGetProperty("parse", out var parse) &&
                             parse.ValueKind == JsonValueKind.Number && parse.GetInt32() != 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object)
            foreach (var value in header.EnumerateObject()) headers[value.Name] = value.Value.ToString();
        return new PlayRequest(ResolveGatewayResource(url), source.Name, requiresParser, headers);
    }

    public async Task<IReadOnlyList<SpiderGatewayLiveChannel>> GetLiveChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await InvokeAsync("live", new { }, cancellationToken);
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (!document.RootElement.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SpiderGatewayLiveChannel>();
        foreach (var channel in channels.EnumerateArray())
        {
            var name = ReadString(channel, "name");
            var address = ReadString(channel, "address");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address)) continue;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (channel.TryGetProperty("headers", out var headerValues) &&
                headerValues.ValueKind == JsonValueKind.Object)
                foreach (var header in headerValues.EnumerateObject())
                    headers[header.Name] = header.Value.ToString();

            var logoAddress = ReadString(channel, "logoAddress");
            var epgAddress = ReadString(channel, "epgAddress");
            results.Add(new SpiderGatewayLiveChannel(
                ReadString(channel, "group") ?? "直播",
                name,
                ResolveGatewayResource(address),
                headers,
                string.IsNullOrWhiteSpace(logoAddress) ? null : ResolveGatewayResource(logoAddress),
                string.IsNullOrWhiteSpace(epgAddress) ? null : ResolveGatewayResource(epgAddress)));
        }

        return results;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private string ResolveGatewayResource(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https")
            return absolute.ToString();
        if (_gateway is not null &&
            address.StartsWith("/v1/spider/native/", StringComparison.Ordinal) &&
            Uri.TryCreate(_gateway, address, out var gatewayResource))
            return gatewayResource.ToString();
        throw new InvalidDataException("Spider Gateway 返回了不受支持的媒体地址。");
    }

    private async Task<string> InvokeSiteAsync(
        string method,
        TvBoxSite site,
        object values,
        CancellationToken cancellationToken)
    {
        if (!CanHandle(site))
            throw new InvalidOperationException("Spider Gateway 未配置，或当前站点不是 Android CSP 站点。");
        var payload = new Dictionary<string, object?>
        {
            ["site"] = new
            {
                key = site.RuntimeKey,
                name = site.Name,
                api = site.Api,
                type = site.Type,
                jar = site.Jar,
                ext = site.Ext,
                searchable = site.Searchable ?? 1,
            }
        };
        foreach (var property in values.GetType().GetProperties()) payload[property.Name] = property.GetValue(values);
        return await InvokeAsync(method, payload, cancellationToken);
    }

    private async Task<string> InvokeAsync(string method, object payload, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("Spider Gateway 未配置。");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_gateway!, $"/v1/spider/{method}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = content;
            try
            {
                using var error = JsonDocument.Parse(content);
                message = error.RootElement.TryGetProperty("message", out var value)
                    ? value.GetString() ?? content
                    : content;
            }
            catch (JsonException)
            {
            }
            throw new HttpRequestException(
                $"Spider Gateway 返回 HTTP {(int)response.StatusCode}：{message}",
                null,
                response.StatusCode);
        }
        return content;
    }
}
