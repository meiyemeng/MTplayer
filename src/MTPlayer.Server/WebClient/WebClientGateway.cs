using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using WebHtv.Catalogue;
using WebHtv.Configuration;
using WebHtv.Core.Catalogue;
using WebHtv.Core.Configuration;

namespace MTPlayer.Server.WebClient;

public sealed record WebSiteDto(string Key, string Name, string Api);
public sealed record WebLiveDto(string Name, string Address, string? EpgAddress = null);
public sealed record WebConfigRequest(Guid GroupId, string Url);
public sealed record WebConfigResponse(IReadOnlyList<WebSiteDto> Sites, IReadOnlyList<WebLiveDto> Lives);
public sealed record WebCatalogueRequest(IReadOnlyList<WebSiteDto> Sites, string? Keyword = null, int Limit = 60);
public sealed record WebDetailRequest(WebSiteDto Site, string Id);
public sealed record WebSignRequest(string Url);
public sealed record WebEpisodeDto(string Name, string Url, bool IsHls);
public sealed record WebLineDto(string Name, IReadOnlyList<WebEpisodeDto> Episodes);
public sealed record WebItemDto(
    string SourceKey,
    string SourceName,
    string Id,
    string Title,
    string CoverUrl,
    string OriginalCoverUrl,
    string Remarks,
    string TypeName);
public sealed record WebDetailDto(WebItemDto Item, IReadOnlyList<WebLineDto> Sources, string Description, CatalogueMetadata Metadata);

public sealed class WebProxySigner(IConfiguration configuration)
{
    private readonly byte[] _key = Convert.FromBase64String(
        configuration["DATA_ENCRYPTION_KEY"] ?? throw new InvalidOperationException("DATA_ENCRYPTION_KEY is required."));

    public string Sign(string address, string kind, TimeSpan? lifetime = null)
    {
        var expires = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        var payload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{kind}\n{expires}\n{address}"));
        using var hmac = new HMACSHA256(_key);
        var signature = WebEncoders.Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        return $"{payload}.{signature}";
    }

    public bool TryRead(string token, string kind, out Uri? address)
    {
        address = null;
        var parts = (token ?? string.Empty).Split('.', 2);
        if (parts.Length != 2) return false;
        using var hmac = new HMACSHA256(_key);
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[0]));
        byte[] actual;
        try { actual = WebEncoders.Base64UrlDecode(parts[1]); }
        catch (FormatException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return false;

        string[] values;
        try { values = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[0])).Split('\n', 3); }
        catch (FormatException) { return false; }
        return values.Length == 3 && values[0] == kind &&
               long.TryParse(values[1], out var expires) && expires >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
               Uri.TryCreate(values[2], UriKind.Absolute, out address) && WebClientGateway.IsHttp(address);
    }
}

public sealed class WebClientGateway(HttpClient http, WebProxySigner signer)
{
    private const int MaximumConfigurationBytes = 10 * 1024 * 1024;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan[] SendRetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(800),
    ];
    public async Task<WebConfigResponse> InspectAsync(WebConfigRequest request, CancellationToken cancellationToken)
    {
        if (request.GroupId == Guid.Empty) throw new ArgumentException("配置源标识无效。");
        var sites = new Dictionary<string, WebSiteDto>(StringComparer.Ordinal);
        var lives = new Dictionary<string, WebLiveDto>(StringComparer.OrdinalIgnoreCase);
        await InspectCoreAsync(request.GroupId.ToString("N"), RequireAddress(request.Url), 0, sites, lives, cancellationToken);
        return new WebConfigResponse(sites.Values.ToArray(), lives.Values.ToArray());
    }

    public async Task<IReadOnlyList<WebItemDto>> LatestAsync(WebCatalogueRequest request, CancellationToken cancellationToken)
    {
        var sites = NormalizeSites(request.Sites).Take(18).ToArray();
        var tasks = sites.Select(site => QueryLatestAsync(site, cancellationToken)).ToArray();
        var pages = await Task.WhenAll(tasks);
        return Deduplicate(pages.SelectMany(page => page), Math.Clamp(request.Limit, 1, 120));
    }

    public async Task<IReadOnlyList<WebItemDto>> SearchAsync(WebCatalogueRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Keyword)) throw new ArgumentException("请输入搜索关键词。");
        var sites = NormalizeSites(request.Sites).Take(40).ToArray();
        using var gate = new SemaphoreSlim(8);
        var tasks = sites.Select(async site =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var page = await QueryPageAsync(site, new Dictionary<string, string>
                {
                    ["ac"] = "detail", ["wd"] = request.Keyword.Trim(), ["quick"] = "false", ["extend"] = string.Empty,
                }, cancellationToken);
                return page.Items.Select(item => ToItem(site, item)).ToArray();
            }
            catch { return []; }
            finally { gate.Release(); }
        });
        return Deduplicate((await Task.WhenAll(tasks)).SelectMany(page => page), Math.Clamp(request.Limit, 1, 300));
    }

    public async Task<WebDetailDto> DetailAsync(WebDetailRequest request, CancellationToken cancellationToken)
    {
        var site = NormalizeSites([request.Site]).Single();
        var uri = AddQuery(site.Api, new Dictionary<string, string> { ["ac"] = "detail", ["ids"] = request.Id });
        using var response = await SendAsync(uri, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var detail = TvBoxJsonResultParser.ParseDetail(site.Key, await response.Content.ReadAsStringAsync(cancellationToken));
        var sources = detail.Sources.Select(source => new WebLineDto(
            source.Name,
            source.Episodes.Where(episode => Uri.TryCreate(episode.Url, UriKind.Absolute, out var uri) && IsHttp(uri))
                .Select(episode => new WebEpisodeDto(
                    episode.Name,
                    ProxyUrl(episode.Url, "media", TimeSpan.FromHours(12)),
                    LooksLikeHls(episode.Url))).ToArray())).Where(source => source.Episodes.Count > 0).ToArray();
        return new WebDetailDto(ToItem(site, detail.Item), sources, detail.Description, detail.Metadata);
    }

    public string SignMedia(string address) => ProxyUrl(RequireAddress(address).ToString(), "media", TimeSpan.FromHours(12));

    public async Task ProxyAsync(HttpContext context, string kind, string token, CancellationToken cancellationToken)
    {
        if (kind is not ("image" or "media") || !signer.TryRead(token, kind, out var address) || address is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var response = await SendAsync(address, context.Request.Headers.Range.ToString(), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            return;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? GuessContentType(address);
        if (kind == "media" && IsManifest(address, contentType))
        {
            if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
                throw new InvalidDataException("播放清单超过 4 MiB。");
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaximumManifestBytes) throw new InvalidDataException("播放清单过大。");
            var text = RewriteManifest(Encoding.UTF8.GetString(bytes), address);
            context.Response.ContentType = "application/vnd.apple.mpegurl; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(text, cancellationToken);
            return;
        }

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = kind == "image" ? "public,max-age=21600" : "no-store";
        if (response.Content.Headers.ContentLength is { } length) context.Response.ContentLength = length;
        if (response.Content.Headers.ContentRange is { } range) context.Response.Headers.ContentRange = range.ToString();
        if (response.Headers.AcceptRanges.Count > 0) context.Response.Headers.AcceptRanges = string.Join(',', response.Headers.AcceptRanges);
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private async Task InspectCoreAsync(
        string groupKey,
        Uri address,
        int depth,
        Dictionary<string, WebSiteDto> sites,
        Dictionary<string, WebLiveDto> lives,
        CancellationToken cancellationToken)
    {
        if (depth > 3) return;
        using var response = await SendAsync(address, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumConfigurationBytes)
            throw new InvalidDataException("配置文件超过 10 MiB。");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > MaximumConfigurationBytes) throw new InvalidDataException("配置文件超过 10 MiB。");
        var source = DecodeConfiguration(Encoding.UTF8.GetString(bytes));
        var parsed = TvBoxProfileParser.Parse(source);
        if (!parsed.IsValid || parsed.Profile is null)
            throw new InvalidDataException(parsed.Errors.Count > 0 ? parsed.Errors[0] : "配置内容不是有效的 TVBox JSON。");

        var index = 0;
        foreach (var site in parsed.Profile.Sites)
        {
            index++;
            // Browser/server catalogue support is intentionally limited to HTTP CMS
            // sites. CSP/JAR/Python entries require their own trusted runtime and must
            // not be presented as working interfaces in the web client.
            if (site.Type is not (1 or 2 or 4) || site.Searchable == 0) continue;
            if (!Uri.TryCreate(site.Api, UriKind.Absolute, out var api) || !IsHttp(api)) continue;
            var key = $"{groupKey}:{(string.IsNullOrWhiteSpace(site.RuntimeKey) ? index : site.RuntimeKey)}";
            sites[key] = new WebSiteDto(key, string.IsNullOrWhiteSpace(site.Name) ? $"接口 {index}" : site.Name, api.ToString());
        }

        foreach (var live in parsed.Profile.Lives)
        {
            var liveAddress = FirstAddress(live.Url, live.Api, live.Ext);
            if (liveAddress is null) continue;
            var name = string.IsNullOrWhiteSpace(live.Name) ? $"直播源 {lives.Count + 1}" : live.Name;
            lives[$"{name}|{liveAddress}"] = new WebLiveDto(name, liveAddress);
        }

        var child = 0;
        foreach (var depot in parsed.Profile.Urls)
        {
            if (!Uri.TryCreate(address, depot.Url, out var childAddress) || !IsHttp(childAddress)) continue;
            await InspectCoreAsync($"{groupKey}:g{++child}", childAddress, depth + 1, sites, lives, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<WebItemDto>> QueryLatestAsync(WebSiteDto site, CancellationToken cancellationToken)
    {
        try
        {
            var uri = AddQuery(site.Api, new Dictionary<string, string> { ["ac"] = "detail" });
            using var response = await SendAsync(uri, null, cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = TvBoxJsonResultParser.ParsePage(site.Key, await response.Content.ReadAsStringAsync(cancellationToken));
            return page.Items.Select(item => ToItem(site, item)).ToArray();
        }
        catch { return []; }
    }

    private async Task<CataloguePage> QueryPageAsync(
        WebSiteDto site,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var uri = AddQuery(site.Api, parameters);
        using var response = await SendAsync(uri, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return TvBoxJsonResultParser.ParsePage(site.Key, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task<HttpResponseMessage> SendAsync(Uri address, string? range, CancellationToken cancellationToken)
    {
        var current = address;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            await EnsurePublicAsync(current, cancellationToken);
            var response = await SendWithRetryAsync(current, range, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            return response;
        }
        throw new HttpRequestException("远程地址重定向次数过多。");
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Uri address,
        string? range,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (!string.IsNullOrWhiteSpace(range) && RangeHeaderValue.TryParse(range, out var parsedRange))
                request.Headers.Range = parsedRange;
            request.Headers.Referrer = address.Host.EndsWith("doubanio.com", StringComparison.OrdinalIgnoreCase)
                ? new Uri("https://movie.douban.com/") : null;
            // A retry must use a fresh connection. Some Cloudflare edges reset a
            // pooled connection without sending an HTTP response.
            request.Headers.ConnectionClose = attempt > 0;
            try
            {
                return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < SendRetryDelays.Length && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(SendRetryDelays[attempt], cancellationToken);
            }
        }
    }

    private static async Task EnsurePublicAsync(Uri address, CancellationToken cancellationToken)
    {
        if (!IsHttp(address) || address.AbsoluteUri.Length > 4096 || string.IsNullOrWhiteSpace(address.Host))
            throw new ArgumentException("只支持有效的 HTTP/HTTPS 公网地址。");
        var addresses = await Dns.GetHostAddressesAsync(address.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsPrivate)) throw new ArgumentException("不允许访问内网、环回或保留地址。");
    }

    private WebItemDto ToItem(WebSiteDto site, CatalogueItem item)
    {
        var originalCover = Uri.TryCreate(item.CoverUrl, UriKind.Absolute, out var cover) && IsHttp(cover)
            ? cover.ToString()
            : string.Empty;
        return new WebItemDto(
            item.SourceKey,
            site.Name,
            item.Id,
            item.Title,
            originalCover.Length > 0 ? ProxyUrl(originalCover, "image", TimeSpan.FromHours(24)) : string.Empty,
            originalCover,
            item.Remarks,
            item.TypeName);
    }

    private string ProxyUrl(string address, string kind, TimeSpan lifetime) =>
        $"/api/v1/web/proxy/{kind}?token={Uri.EscapeDataString(signer.Sign(address, kind, lifetime))}";

    private string RewriteManifest(string manifest, Uri baseAddress)
    {
        var lines = manifest.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith('#'))
            {
                var segment = new Uri(baseAddress, line);
                if (IsHttp(segment)) lines[i] = ProxyUrl(segment.ToString(), "media", TimeSpan.FromHours(12));
                continue;
            }
            lines[i] = Regex.Replace(lines[i], "URI=\"([^\"]+)\"", match =>
            {
                var resource = new Uri(baseAddress, match.Groups[1].Value);
                return IsHttp(resource)
                    ? $"URI=\"{ProxyUrl(resource.ToString(), "media", TimeSpan.FromHours(12))}\""
                    : match.Value;
            });
        }
        return string.Join('\n', lines);
    }

    private static WebItemDto[] Deduplicate(IEnumerable<WebItemDto> values, int limit) => values
        .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Title))
        .GroupBy(item => $"{item.SourceKey}|{item.Id}", StringComparer.Ordinal)
        .Select(group => group.First()).Take(limit).ToArray();

    private static IEnumerable<WebSiteDto> NormalizeSites(IEnumerable<WebSiteDto> sites) => sites
        .Where(site => !string.IsNullOrWhiteSpace(site.Key) && !string.IsNullOrWhiteSpace(site.Name) &&
                       Uri.TryCreate(site.Api, UriKind.Absolute, out var uri) && IsHttp(uri))
        .GroupBy(site => site.Key, StringComparer.Ordinal).Select(group => group.First());

    private static string DecodeConfiguration(string value)
    {
        value = TvBoxConfigurationPayloadDecoder.Decode(value);
        using var first = JsonDocument.Parse(value, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (first.RootElement.ValueKind == JsonValueKind.String)
        {
            var raw = first.RootElement.GetString() ?? string.Empty;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw)); } catch (FormatException) { return raw; }
        }
        if (first.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "data", "config", "payload" })
            {
                if (!first.RootElement.TryGetProperty(key, out var wrapped) || wrapped.ValueKind != JsonValueKind.String) continue;
                var raw = wrapped.GetString() ?? string.Empty;
                try { raw = Encoding.UTF8.GetString(Convert.FromBase64String(raw)); } catch (FormatException) { }
                if (raw.TrimStart().StartsWith('{')) return raw;
            }
        }
        return value;
    }

    private static string? FirstAddress(string? url, string? api, JsonElement? ext)
    {
        foreach (var value in new[] { url, api, ext is { ValueKind: JsonValueKind.String } e ? e.GetString() : null })
            if (Uri.TryCreate(value, UriKind.Absolute, out var address) && IsHttp(address)) return address.ToString();
        return null;
    }

    private static Uri AddQuery(string address, IReadOnlyDictionary<string, string> values)
    {
        var uri = new Uri(address);
        var query = QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values) query[pair.Key] = pair.Value;
        return new UriBuilder(uri) { Query = string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")) }.Uri;
    }

    private static Uri RequireAddress(string value) => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var address) && IsHttp(address)
        ? address : throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 地址。");

    public static bool IsHttp(Uri value) => value.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                                             value.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    private static bool LooksLikeHls(string value) => value.Split('?', 2)[0].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    private static bool IsManifest(Uri address, string contentType) => LooksLikeHls(address.ToString()) ||
        contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
    private static string GuessContentType(Uri address) => Path.GetExtension(address.AbsolutePath).ToLowerInvariant() switch
    {
        ".m3u8" => "application/vnd.apple.mpegurl", ".ts" => "video/mp2t", ".mp4" => "video/mp4",
        ".webp" => "image/webp", ".png" => "image/png", ".gif" => "image/gif", _ => "image/jpeg",
    };

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.GetAddressBytes()[0] is 0xfc or 0xfd;
        var b = address.GetAddressBytes();
        return b[0] is 0 or 10 or 127 || b[0] >= 224 ||
               b[0] == 100 && b[1] is >= 64 and <= 127 ||
               b[0] == 169 && b[1] == 254 ||
               b[0] == 172 && b[1] is >= 16 and <= 31 ||
               b[0] == 192 && b[1] == 168;
    }
}
