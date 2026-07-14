using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Globalization;
using WebHtv.Core.Configuration;

namespace WebHtv.Desktop;

internal sealed record LiveChannel(string Group, string Name, string Url, IReadOnlyDictionary<string, string> Headers, string? LogoUrl = null, string? ChannelId = null, string? NowPlaying = null, string? NextPlaying = null);

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
