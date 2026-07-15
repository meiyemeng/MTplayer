using MTPlayer.Client.Core.Library;
using MTPlayer.Client.Core.Settings;

namespace WebHtv.Desktop;

internal sealed class AppSettings
{
    public bool HardwareDecode { get; set; } = true;
    public double DefaultSpeed { get; set; } = 1.0;
    public int DefaultVolume { get; set; } = 80;
    public bool AutoFullscreen { get; set; }
    public bool UseSourceCovers { get; set; } = true;
    public string TmdbApiKey { get; set; } = string.Empty;
    public List<string> DisabledSiteKeys { get; set; } = [];
    public List<ConfigurationSourceEntry> ConfigurationSources { get; set; } = [];
    public string ActiveConfigurationSourceId { get; set; } = string.Empty;
    public List<CustomLiveSourceEntry> CustomLiveSources { get; set; } = [];
}

internal sealed class ConfigurationSourceEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
}

internal sealed class CustomLiveSourceEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? EpgAddress { get; set; }
}

internal sealed class AppSettingsStore : IDisposable
{
    private readonly JsonSettingsStore _store;

    public AppSettingsStore(string filePath)
    {
        _store = new JsonSettingsStore(filePath);
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _store.LoadAsync(cancellationToken);
        var groups = settings.ConfigurationGroups.Where(item => !item.IsDeleted).ToList();
        return new AppSettings
        {
            HardwareDecode = settings.HardwareDecode,
            DefaultSpeed = settings.DefaultSpeed,
            DefaultVolume = settings.DefaultVolume,
            AutoFullscreen = settings.AutoFullscreen,
            UseSourceCovers = settings.UseSourceCovers,
            TmdbApiKey = settings.TmdbApiKey,
            DisabledSiteKeys = [.. settings.DisabledSiteKeys],
            ConfigurationSources = groups.Select(item => new ConfigurationSourceEntry
            {
                Id = item.Id.ToString("N"),
                Name = item.Name,
                Address = item.Address,
            }).ToList(),
            ActiveConfigurationSourceId = groups.FirstOrDefault(item => item.IsEnabled)?.Id.ToString("N") ?? string.Empty,
            CustomLiveSources = settings.CustomLiveSources.Where(item => !item.IsDeleted).Select(item => new CustomLiveSourceEntry
            {
                Id = item.Id.ToString("N"),
                Name = item.Name,
                Address = item.Address,
                EpgAddress = item.EpgAddress,
            }).ToList(),
        };
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var core = new ClientSettings
        {
            HardwareDecode = settings.HardwareDecode,
            DefaultSpeed = settings.DefaultSpeed,
            DefaultVolume = settings.DefaultVolume,
            AutoFullscreen = settings.AutoFullscreen,
            UseSourceCovers = settings.UseSourceCovers,
            TmdbApiKey = settings.TmdbApiKey,
            DisabledSiteKeys = [.. settings.DisabledSiteKeys],
            ConfigurationGroups = settings.ConfigurationSources.Select(item => new ConfigurationGroupRecord(
                ParseOrCreateId(item.Id, "configuration", item.Address),
                item.Name,
                item.Address,
                string.Equals(item.Id, settings.ActiveConfigurationSourceId, StringComparison.Ordinal),
                now)).ToList(),
            CustomLiveSources = settings.CustomLiveSources.Select(item => new CustomLiveSourceRecord(
                ParseOrCreateId(item.Id, "live", item.Address),
                item.Name,
                item.Address,
                item.EpgAddress,
                now)).ToList(),
        };
        return _store.SaveAsync(core, cancellationToken);
    }

    private static Guid ParseOrCreateId(string? value, string kind, string address) =>
        Guid.TryParse(value, out var parsed) ? parsed : JsonLibraryStore.StableId(kind, address);

    public void Dispose() => _store.Dispose();
}
