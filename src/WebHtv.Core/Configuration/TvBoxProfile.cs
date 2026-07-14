using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebHtv.Core.Configuration;

/// <summary>
/// The portable TVBox configuration surface consumed by the native Windows app.
/// Unknown fields are preserved to avoid destroying newer configuration data.
/// </summary>
public sealed class TvBoxProfile
{
    [JsonPropertyName("spider")]
    public string? Spider { get; init; }

    [JsonPropertyName("wallpaper")]
    public string? Wallpaper { get; init; }

    [JsonPropertyName("logo")]
    public string? Logo { get; init; }

    [JsonPropertyName("notice")]
    public string? Notice { get; init; }

    [JsonPropertyName("danmaku")]
    public string? Danmaku { get; init; }

    [JsonPropertyName("sites")]
    public List<TvBoxSite> Sites { get; init; } = [];

    [JsonPropertyName("lives")]
    public List<TvBoxLive> Lives { get; init; } = [];

    [JsonPropertyName("parses")]
    public List<TvBoxParser> Parses { get; init; } = [];

    [JsonPropertyName("flags")]
    public List<string> Flags { get; init; } = [];

    [JsonPropertyName("rules")]
    public List<JsonElement> Rules { get; init; } = [];

    [JsonPropertyName("headers")]
    public List<JsonElement> Headers { get; init; } = [];

    [JsonPropertyName("proxy")]
    public List<JsonElement> Proxy { get; init; } = [];

    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; init; } = [];

    [JsonPropertyName("ads")]
    public List<string> Ads { get; init; } = [];

    [JsonPropertyName("doh")]
    public List<JsonElement> Doh { get; init; } = [];

    [JsonPropertyName("webHomeExtensions")]
    public JsonElement? WebHomeExtensions { get; init; }

    [JsonPropertyName("urls")]
    public List<TvBoxDepotEntry> Urls { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];

    public bool IsDepot => Urls.Count > 0;

    public bool HasContent => Sites.Count > 0 || Lives.Count > 0 || IsDepot;
}

public sealed class TvBoxSite
{
    [JsonIgnore]
    public string RuntimeKey { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("api")]
    public string Api { get; init; } = string.Empty;

    [JsonPropertyName("ext")]
    public JsonElement? Ext { get; init; }

    [JsonPropertyName("jar")]
    public string? Jar { get; init; }

    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("searchable")]
    public int? Searchable { get; init; }

    [JsonPropertyName("changeable")]
    public int? Changeable { get; init; }

    [JsonPropertyName("quickSearch")]
    public int? QuickSearch { get; init; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; init; } = [];

    [JsonPropertyName("header")]
    public JsonElement? Header { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class TvBoxLive
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("api")]
    public string? Api { get; init; }

    [JsonPropertyName("ext")]
    public JsonElement? Ext { get; init; }

    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("header")]
    public JsonElement? Header { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class TvBoxParser
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("ext")]
    public JsonElement? Ext { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class TvBoxDepotEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("logo")]
    public string? Logo { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}
