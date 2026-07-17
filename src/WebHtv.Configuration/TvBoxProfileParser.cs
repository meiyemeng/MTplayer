using System.Text.Json;
using WebHtv.Core.Configuration;

namespace WebHtv.Configuration;

public sealed record TvBoxProfileParseResult(TvBoxProfile? Profile, IReadOnlyList<string> Errors)
{
    public bool IsValid => Profile is not null && Errors.Count == 0;
}

public static class TvBoxProfileParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static TvBoxProfileParseResult Parse(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return new TvBoxProfileParseResult(null, ["配置内容不能为空。"]);
        }

        try
        {
            var normalizedSourceText = NormalizeSourceText(sourceText);
            using var document = JsonDocument.Parse(normalizedSourceText, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return new TvBoxProfileParseResult(null, ["TVBox 配置根节点必须是 JSON 对象。"]);
            }

            var profile = JsonSerializer.Deserialize<TvBoxProfile>(normalizedSourceText, SerializerOptions);
            if (profile is null)
            {
                return new TvBoxProfileParseResult(null, ["无法读取 TVBox 配置。"]);
            }

            var errors = new List<string>();
            AssignRuntimeKeys(profile);
            ValidateDepot(profile, errors);
            return new TvBoxProfileParseResult(profile, errors);
        }
        catch (JsonException exception)
        {
            return new TvBoxProfileParseResult(null, [$"配置不是有效 JSON：{exception.Message}"]);
        }
    }

    /// <summary>
    /// Makes common TVBox JSON extensions safe for System.Text.Json while
    /// preserving the original value semantics. Comments and trailing commas
    /// are handled by the parser options; raw control characters inside quoted
    /// strings are escaped here.
    /// </summary>
    public static string NormalizeSourceText(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        return TvBoxJsonNormalizer.EscapeControlCharactersInsideStrings(sourceText);
    }

    private static void ValidateUniqueSiteKeys(TvBoxProfile profile, List<string> errors)
    {
        var duplicate = profile.Sites
            .Where(site => !string.IsNullOrWhiteSpace(site.Key))
            .GroupBy(site => site.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            errors.Add($"站点 key“{duplicate.Key}”重复。");
        }
    }

    private static void AssignRuntimeKeys(TvBoxProfile profile)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var site in profile.Sites)
        {
            var key = string.IsNullOrWhiteSpace(site.Key) ? "site" : site.Key;
            counts.TryGetValue(key, out var currentCount);
            currentCount++;
            counts[key] = currentCount;
            site.RuntimeKey = currentCount == 1 ? key : $"{key}#{currentCount}";
        }
    }

    private static void ValidateDepot(TvBoxProfile profile, List<string> errors)
    {
        foreach (var entry in profile.Urls)
        {
            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                errors.Add("配置仓库 urls 中存在空地址。");
                break;
            }
        }
    }
}
