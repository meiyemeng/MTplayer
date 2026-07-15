using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MTPlayer.Mac.Services;

public sealed partial class CatalogueService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(18) };
    public CatalogueService() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("MTPlayer/1.1 macOS");

    public async Task<List<Site>> LoadSitesAsync(IEnumerable<SourceGroup> groups, CancellationToken cancellationToken = default)
    {
        var sites = new List<Site>();
        foreach (var group in groups.Where(x => x.Enabled))
            sites.AddRange(await LoadGroupAsync(group.Url, group.Id.ToString("N"), 0, cancellationToken));
        return sites.GroupBy(x => x.Key).Select(x => x.First()).ToList();
    }

    public async Task<List<MediaEntry>> LatestAsync(IEnumerable<Site> sites, int limit, CancellationToken cancellationToken = default)
    {
        var tasks = sites.Take(12).Select(site => QueryAsync(site, null, cancellationToken)).ToList();
        var result = new List<MediaEntry>();
        foreach (var task in tasks)
        {
            try { result.AddRange(await task); } catch { }
            if (result.Count >= limit) break;
        }
        return result.Take(limit).ToList();
    }

    public async Task<List<MediaEntry>> SearchAsync(IEnumerable<Site> sites, string keyword, CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(8);
        var tasks = sites.Select(async site =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return await QueryAsync(site, keyword, cancellationToken); }
            catch { return []; }
            finally { gate.Release(); }
        });
        return (await Task.WhenAll(tasks)).SelectMany(x => x).GroupBy(x => $"{x.SiteKey}|{x.Id}").Select(x => x.First()).ToList();
    }

    public async Task<MediaDetail> DetailAsync(Site site, string id, CancellationToken cancellationToken = default)
    {
        using var root = await RequestAsync(site.Api, "detail", null, id, null, cancellationToken);
        var vod = List(root.RootElement).FirstOrDefault();
        if (vod.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("该接口没有返回影片详情");
        var detail = new MediaDetail
        {
            Item = Entry(site, vod),
            Content = Html(Text(vod, "vod_content")),
            Actor = Text(vod, "vod_actor"),
            Director = Text(vod, "vod_director")
        };
        var names = Text(vod, "vod_play_from").Split("$$$", StringSplitOptions.None);
        var values = Text(vod, "vod_play_url").Split("$$$", StringSplitOptions.None);
        for (var i = 0; i < values.Length; i++)
        {
            var line = new PlayLine { Name = i < names.Length && names[i].Length > 0 ? names[i] : $"线路 {i + 1}" };
            foreach (var part in values[i].Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = part.IndexOf('$');
                if (split > 0 && split < part.Length - 1) line.Episodes.Add(new Episode { Name = part[..split].Trim(), Url = part[(split + 1)..].Trim() });
            }
            if (line.Episodes.Count > 0) detail.Lines.Add(line);
        }
        return detail;
    }

    private async Task<List<MediaEntry>> QueryAsync(Site site, string? keyword, CancellationToken cancellationToken)
    {
        try
        {
            using var root = await RequestAsync(site.Api, "detail", keyword, null, null, cancellationToken);
            return List(root.RootElement).Select(x => Entry(site, x)).ToList();
        }
        catch when (!string.IsNullOrWhiteSpace(keyword))
        {
            var items = new List<MediaEntry>();
            for (var page = 1; page <= 3; page++)
            {
                using var root = await RequestAsync(site.Api, "detail", null, null, page, cancellationToken);
                items.AddRange(List(root.RootElement).Select(x => Entry(site, x)));
            }
            return items.Where(x => Normalize(x.Name).Contains(Normalize(keyword))).ToList();
        }
    }

    private async Task<JsonDocument> RequestAsync(string api, string action, string? keyword, string? ids, int? page, CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(api);
        var query = ParseQuery(builder.Query);
        query["ac"] = action;
        if (keyword is not null) query["wd"] = keyword;
        if (ids is not null) query["ids"] = ids;
        if (page is not null) query["pg"] = page.Value.ToString(CultureInfo.InvariantCulture);
        builder.Query = string.Join('&', query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        var raw = await _http.GetStringAsync(builder.Uri, cancellationToken);
        var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new InvalidDataException("接口返回格式无效"); }
        return document;
    }

    private async Task<List<Site>> LoadGroupAsync(string url, string groupKey, int depth, CancellationToken cancellationToken)
    {
        if (depth > 3) return [];
        var raw = await _http.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return [];
        var result = new List<Site>();
        if (root.TryGetProperty("sites", out var sites) && sites.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var value in sites.EnumerateArray())
            {
                var api = Text(value, "api");
                var type = value.TryGetProperty("type", out var kind) && kind.TryGetInt32(out var parsed) ? parsed : 0;
                var cms = type == 1 || api.Contains("/provide/vod", StringComparison.OrdinalIgnoreCase) || api.Contains("/api.php/provide/", StringComparison.OrdinalIgnoreCase);
                if (!cms || !Uri.TryCreate(api, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                index++;
                result.Add(new Site { Key = $"{groupKey}:{Text(value, "key", $"site{index}")}", Name = Text(value, "name", $"接口 {index}"), Api = api });
            }
        }
        if (root.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var value in urls.EnumerateArray())
            {
                var child = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Object ? Text(value, "url") : null;
                if (string.IsNullOrWhiteSpace(child)) continue;
                child = new Uri(new Uri(url), child).ToString();
                result.AddRange(await LoadGroupAsync(child, $"{groupKey}:g{++index}", depth + 1, cancellationToken));
            }
        }
        return result;
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length > 1 ? Uri.UnescapeDataString(x[1]) : string.Empty);
    private static IEnumerable<JsonElement> List(JsonElement root) => root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
    private static MediaEntry Entry(Site site, JsonElement vod) => new() { SiteKey = site.Key, SiteName = site.Name, Id = Text(vod, "vod_id", Text(vod, "id")), Name = Text(vod, "vod_name", Text(vod, "name", "未命名影片")), Poster = Text(vod, "vod_pic", Text(vod, "pic")), Remarks = Text(vod, "vod_remarks", Text(vod, "remarks")), Year = Text(vod, "vod_year", Text(vod, "year")), Type = Text(vod, "type_name", Text(vod, "vod_class", "影视")) };
    private static string Text(JsonElement value, string key, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) ? item.ToString() : fallback;
    private static string Normalize(string value) => Punctuation().Replace(value.ToUpperInvariant(), string.Empty);
    private static string Html(string value) => Tags().Replace(value, string.Empty).Replace("&nbsp;", " ").Trim();
    [GeneratedRegex(@"[\p{P}\p{Z}]")] private static partial Regex Punctuation();
    [GeneratedRegex("<[^>]*>")] private static partial Regex Tags();
    public void Dispose() => _http.Dispose();
}
