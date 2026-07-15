using System.Text.Json.Serialization;

namespace MTPlayer.Mac.Services;

public sealed class AppSettings
{
    public List<SourceGroup> ConfigurationGroups { get; set; } = [];
    public List<MediaEntry> Favorites { get; set; } = [];
    public List<MediaEntry> History { get; set; } = [];
    public string ServerUrl { get; set; } = string.Empty;
    public Dictionary<string, SkipSetting> SkipSettings { get; set; } = [];
}

public sealed class SkipSetting
{
    public double IntroSeconds { get; set; }
    public double OutroSeconds { get; set; }
}

public sealed class SourceGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "配置源";
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class Site
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Api { get; set; } = string.Empty;
}

public sealed class MediaEntry
{
    public string SiteKey { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Poster { get; set; } = string.Empty;
    public string Remarks { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public string Type { get; set; } = "影视";
}

public sealed class Episode
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public sealed class PlayLine
{
    public string Name { get; set; } = string.Empty;
    public List<Episode> Episodes { get; set; } = [];
}

public sealed class MediaDetail
{
    public MediaEntry Item { get; set; } = new();
    public string Content { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Director { get; set; } = string.Empty;
    public List<PlayLine> Lines { get; set; } = [];
}
