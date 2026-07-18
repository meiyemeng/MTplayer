using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WebHtv.Catalogue;
using WebHtv.Core.Catalogue;
using WebHtv.Core.Configuration;

namespace WebHtv.Spider;

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

    public async Task<CataloguePage> SearchAsync(TvBoxSite site, string keyword, int page, CancellationToken cancellationToken = default) =>
        TvBoxJsonResultParser.ParsePage(site.RuntimeKey, await InvokeAsync("search", site, new { keyword }, cancellationToken));

    public async Task<CatalogueDetail> GetDetailAsync(TvBoxSite site, string id, CancellationToken cancellationToken = default) =>
        TvBoxJsonResultParser.ParseDetail(site.RuntimeKey, await InvokeAsync("detail", site, new { id }, cancellationToken));

    public PlayRequest CreatePlayRequest(TvBoxSite site, EpisodeSource source, Episode episode) =>
        new(episode.Url, source.Name, true, new Dictionary<string, string>());

    public async Task<PlayRequest> CreatePlayRequestAsync(
        TvBoxSite site,
        EpisodeSource source,
        Episode episode,
        CancellationToken cancellationToken = default)
    {
        var json = await InvokeAsync("player", site, new { flag = source.Name, id = episode.Url }, cancellationToken);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = document.RootElement;
        var url = root.TryGetProperty("url", out var urlValue) ? urlValue.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidDataException("Spider 没有返回播放地址。");
        var requiresParser = root.TryGetProperty("parse", out var parse) && parse.ValueKind == JsonValueKind.Number && parse.GetInt32() != 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object)
            foreach (var value in header.EnumerateObject()) headers[value.Name] = value.Value.ToString();
        return new PlayRequest(url, source.Name, requiresParser, headers);
    }

    private async Task<string> InvokeAsync(string method, TvBoxSite site, object values, CancellationToken cancellationToken)
    {
        if (!CanHandle(site)) throw new InvalidOperationException("Spider Gateway 未配置或当前站点不是 Android CSP 站点。");
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
                message = error.RootElement.TryGetProperty("message", out var value) ? value.GetString() ?? content : content;
            }
            catch (JsonException) { }
            throw new HttpRequestException($"Spider Gateway 返回 HTTP {(int)response.StatusCode}：{message}", null, response.StatusCode);
        }
        return content;
    }
}
