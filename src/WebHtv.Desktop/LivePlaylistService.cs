using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Globalization;
using WebHtv.Core.Configuration;

namespace WebHtv.Desktop;

internal sealed record LiveChannel(string Group, string Name, string Url, IReadOnlyDictionary<string, string> Headers, string? LogoUrl = null, string? ChannelId = null, string? NowPlaying = null, string? NextPlaying = null);

internal sealed record LiveChannelSourceOption(string Label, LiveChannel Channel);

internal sealed record LiveChannelGroup(
    string Category,
    string Name,
    string? LogoUrl,
    string? NowPlaying,
    IReadOnlyList<LiveChannelSourceOption> Sources)
{
    public string SourceCountText => $"{Sources.Count} 个源";
    public string ProgrammeText => string.IsNullOrWhiteSpace(NowPlaying) ? "暂无节目预告" : $"正在播放 · {NowPlaying}";
}

internal static class LiveChannelOrganizer
{
    public const string AllCategory = "全部频道";

    public static IReadOnlyList<LiveChannelGroup> GroupChannels(IEnumerable<LiveChannel> channels) =>
        channels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.Name))
            .GroupBy(channel => NormalizeName(channel.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sources = group
                    .DistinctBy(channel => channel.Url, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var options = sources
                    .Select((channel, index) => new LiveChannelSourceOption(BuildSourceLabel(channel, index), channel))
                    .ToArray();
                var representative = sources[0];
                return new LiveChannelGroup(
                    DetermineCategory(sources),
                    representative.Name.Trim(),
                    sources.Select(channel => channel.LogoUrl).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    sources.Select(channel => channel.NowPlaying).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    options);
            })
            .OrderBy(group => CategoryOrder(group.Category))
            .ThenBy(group => NaturalSortKey(group.Name), StringComparer.OrdinalIgnoreCase)
            .Take(3000)
            .ToArray();

    public static IReadOnlyList<string> BuildCategories(IEnumerable<LiveChannelGroup> groups) =>
        new[] { AllCategory }
            .Concat(groups.Select(group => group.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(CategoryOrder)
                .ThenBy(value => value, StringComparer.OrdinalIgnoreCase))
            .ToArray();

    public static bool Matches(LiveChannelGroup group, string? query, string? category)
    {
        if (!string.IsNullOrWhiteSpace(category) &&
            !category.Equals(AllCategory, StringComparison.OrdinalIgnoreCase) &&
            !group.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var keyword = query?.Trim();
        return string.IsNullOrWhiteSpace(keyword) ||
               group.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               group.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               group.Sources.Any(source => source.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormKC).Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        normalized = Regex.Replace(
            normalized,
            @"[\[【(（]\s*(?:高清|超清|蓝光|标清|HD|4K|IPv4|IPv6|线路\s*\d+|源\s*\d+)\s*[\]】)）]",
            string.Empty,
            RegexOptions.IgnoreCase);
        normalized = Regex.Replace(
            normalized,
            @"\s+(?:高清|超清|蓝光|标清|HD|4K|IPv4|IPv6|线路\s*\d+|源\s*\d+)$",
            string.Empty,
            RegexOptions.IgnoreCase);
        return normalized.Trim();
    }

    private static string DetermineCategory(IReadOnlyList<LiveChannel> channels)
    {
        var combined = string.Join(' ', channels.Select(channel => $"{channel.Group} {channel.Name}"));
        if (Regex.IsMatch(combined, @"CCTV|CGTN|央视", RegexOptions.IgnoreCase)) return "央视频道";
        if (combined.Contains("卫视", StringComparison.OrdinalIgnoreCase)) return "卫视频道";
        if (Regex.IsMatch(combined, @"广播|电台|Radio", RegexOptions.IgnoreCase)) return "广播频道";
        return channels.Select(channel => channel.Group.Trim())
                   .FirstOrDefault(group => !string.IsNullOrWhiteSpace(group))
               ?? "其他频道";
    }

    private static string BuildSourceLabel(LiveChannel channel, int index)
    {
        var parts = new List<string> { $"源 {index + 1}" };
        if (!string.IsNullOrWhiteSpace(channel.Group)) parts.Add(channel.Group.Trim());
        if (Uri.TryCreate(channel.Url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)) parts.Add(uri.Host);
        return string.Join(" · ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static int CategoryOrder(string category) => category switch
    {
        AllCategory => 0,
        "央视频道" => 1,
        "卫视频道" => 2,
        "地方频道" => 3,
        "体育频道" => 4,
        "影视频道" => 5,
        "广播频道" => 90,
        "其他频道" => 99,
        _ => 50
    };

    private static string NaturalSortKey(string name) =>
        Regex.Replace(name, @"\d+", match => match.Value.PadLeft(10, '0'));
}

internal sealed class LivePlaylistService : IDisposable
{
    private readonly HttpClient _httpClient = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<IReadOnlyList<LiveChannel>> LoadAsync(IEnumerable<TvBoxLive> sources, CancellationToken cancellationToken = default)
    {
        var tasks = sources.Where(source => !string.IsNullOrWhiteSpace(source.Url)).Select(source => LoadSourceAsync(source, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(item => item)
            .Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out _))
            .Where(item => !Regex.IsMatch(item.Name, "更新日期|更新时间|维护公告|温馨提示|免责声明", RegexOptions.IgnoreCase))
            .DistinctBy(item => $"{item.Name}\n{item.Url}", StringComparer.OrdinalIgnoreCase)
            .Take(3000)
            .ToArray();
    }

    private async Task<IReadOnlyList<LiveChannel>> LoadSourceAsync(TvBoxLive source, CancellationToken cancellationToken)
    {
        try
        {
            var text = await _httpClient.GetStringAsync(source.Url!, cancellationToken);
            return text.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase)
                ? ParseM3u(source, text)
                : ParseTxt(source, text);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return [];
        }
    }

    private static List<LiveChannel> ParseM3u(TvBoxLive source, string text)
    {
        var channels = new List<LiveChannel>();
        string name = source.Name;
        string group = source.Name;
        string? logo = null;
        string? channelId = null;
        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.LastIndexOf(',');
                if (comma >= 0 && comma < line.Length - 1) name = line[(comma + 1)..].Trim();
                var match = Regex.Match(line, "group-title=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                group = match.Success ? match.Groups[1].Value : source.Name;
                logo = ReadAttribute(line, "tvg-logo");
                channelId = ReadAttribute(line, "tvg-id") ?? ReadAttribute(line, "tvg-name");
            }
            else if (!line.StartsWith('#') && Uri.TryCreate(line, UriKind.Absolute, out _))
            {
                channels.Add(new LiveChannel(group, name, line, ExtractHeaders(source), logo, channelId));
            }
        }
        return channels;
    }

    private static string? ReadAttribute(string line, string name)
    {
        var match = Regex.Match(line, $"{Regex.Escape(name)}=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public async Task<IReadOnlyList<LiveChannel>> EnrichWithEpgAsync(IReadOnlyList<LiveChannel> channels, IEnumerable<string> epgAddresses, CancellationToken cancellationToken = default)
    {
        var result = channels.ToArray();
        foreach (var address in epgAddresses.Where(value => Uri.TryCreate(value, UriKind.Absolute, out _)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var xml = XDocument.Parse(await _httpClient.GetStringAsync(address, cancellationToken));
                var icons = xml.Root?.Elements("channel").ToDictionary(
                    element => (string?)element.Attribute("id") ?? string.Empty,
                    element => (string?)element.Element("icon")?.Attribute("src"), StringComparer.OrdinalIgnoreCase) ?? [];
                var now = DateTimeOffset.Now;
                var programmeItems = xml.Root?.Elements("programme").Select(element => new
                {
                    Channel = (string?)element.Attribute("channel") ?? string.Empty,
                    Start = ParseXmlTvTime((string?)element.Attribute("start")),
                    Stop = ParseXmlTvTime((string?)element.Attribute("stop")),
                    Title = element.Element("title")?.Value
                }).Where(item => !string.IsNullOrWhiteSpace(item.Title)).ToArray() ?? [];
                var programmes = programmeItems.GroupBy(item => item.Channel, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => new
                    {
                        Current = group.FirstOrDefault(item => item.Start <= now && item.Stop > now)?.Title,
                        Next = group.Where(item => item.Start >= now).OrderBy(item => item.Start).FirstOrDefault()?.Title
                    }, StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < result.Length; index++)
                {
                    var id = result[index].ChannelId;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    icons.TryGetValue(id, out var icon);
                    programmes.TryGetValue(id, out var programme);
                    result[index] = result[index] with { LogoUrl = result[index].LogoUrl ?? icon, NowPlaying = programme?.Current, NextPlaying = programme?.Next };
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Xml.XmlException or InvalidOperationException)
            {
                // An EPG is optional and must not block live playback.
            }
        }
        return result;
    }

    private static DateTimeOffset ParseXmlTvTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParseExact(value.Trim(), "yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed : DateTimeOffset.MinValue;
    }

    private static List<LiveChannel> ParseTxt(TvBoxLive source, string text)
    {
        var channels = new List<LiveChannel>();
        var group = source.Name;
        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var separator = line.IndexOf(',');
            if (separator < 1) continue;
            var name = line[..separator].Trim();
            var url = line[(separator + 1)..].Trim();
            if (url.Equals("#genre#", StringComparison.OrdinalIgnoreCase)) group = name;
            else if (Uri.TryCreate(url, UriKind.Absolute, out _)) channels.Add(new LiveChannel(group, name, url, ExtractHeaders(source)));
        }
        return channels;
    }

    private static Dictionary<string, string> ExtractHeaders(TvBoxLive source)
    {
        if (source.Header is not { ValueKind: System.Text.Json.JsonValueKind.Object } header) return new Dictionary<string, string>();
        return header.EnumerateObject().Where(item => item.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            .ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose() => _httpClient.Dispose();
}
