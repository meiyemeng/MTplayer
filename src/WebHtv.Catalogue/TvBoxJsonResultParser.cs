using System.Text.Json;
using WebHtv.Core.Catalogue;

namespace WebHtv.Catalogue;

public static class TvBoxJsonResultParser
{
    public static CataloguePage ParsePage(string sourceKey, string sourceText)
    {
        using var document = JsonDocument.Parse(sourceText);
        var root = document.RootElement;
        var list = root.TryGetProperty("list", out var listElement) && listElement.ValueKind == JsonValueKind.Array
            ? listElement.EnumerateArray().Select(item => ToItem(sourceKey, item)).Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToArray()
            : [];
        var pageCount = root.TryGetProperty("pagecount", out var pageCountElement) && pageCountElement.TryGetInt32(out var parsedPageCount)
            ? parsedPageCount
            : 0;
        return new CataloguePage(list, pageCount);
    }

    public static CatalogueDetail ParseDetail(string sourceKey, string sourceText)
    {
        using var document = JsonDocument.Parse(sourceText);
        var root = document.RootElement;
        if (!root.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("站点详情响应不包含 list 数组。");
        }

        var first = listElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("站点详情响应没有可用条目。");
        }

        var item = ToItem(sourceKey, first);
        var sources = ParseSources(first);
        var metadata = new CatalogueMetadata(
            GetString(first, "vod_year"),
            GetString(first, "vod_area"),
            GetString(first, "vod_lang"),
            GetString(first, "vod_director"),
            GetString(first, "vod_actor"),
            GetString(first, "vod_score"));
        return new CatalogueDetail(item, sources, StripHtml(GetString(first, "vod_content")), metadata);
    }

    private static CatalogueItem ToItem(string sourceKey, JsonElement item) => new(
        sourceKey,
        GetString(item, "vod_id"),
        GetString(item, "vod_name"),
        GetString(item, "vod_pic"),
        GetString(item, "vod_remarks"),
        GetString(item, "type_name"));

    private static List<EpisodeSource> ParseSources(JsonElement item)
    {
        var names = GetString(item, "vod_play_from").Split("$$$", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var groups = GetString(item, "vod_play_url").Split("$$$", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sources = new List<EpisodeSource>();

        for (var index = 0; index < groups.Length; index++)
        {
            var episodes = groups[index]
                .Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseEpisode)
                .Where(episode => !string.IsNullOrWhiteSpace(episode.Url))
                .ToArray();
            if (episodes.Length == 0)
            {
                continue;
            }

            var name = index < names.Length && !string.IsNullOrWhiteSpace(names[index]) ? names[index] : $"线路 {index + 1}";
            sources.Add(new EpisodeSource(name, episodes));
        }

        return sources;
    }

    private static Episode ParseEpisode(string value)
    {
        var separator = value.IndexOf('$');
        return separator < 0
            ? new Episode(value, value)
            : new Episode(value[..separator], value[(separator + 1)..]);
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static string StripHtml(string value) => System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", string.Empty).Trim();
}
