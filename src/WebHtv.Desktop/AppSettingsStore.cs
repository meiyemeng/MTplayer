using MTPlayer.Client.Core.Library;
using MTPlayer.Client.Core.Settings;
using MTPlayer.Client.Core.Sync;
using System.IO;

namespace WebHtv.Desktop;

internal sealed class AppSettings
{
    public ClientSettings CoreState { get; set; } = new();
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
    private readonly SyncQueueStore _queue;

    public AppSettingsStore(string filePath)
    {
        _store = new JsonSettingsStore(filePath);
        _queue = new SyncQueueStore(Path.Combine(Path.GetDirectoryName(filePath)!, "sync-queue.json"));
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _store.LoadAsync(cancellationToken);
        var groups = settings.ConfigurationGroups.Where(item => !item.IsDeleted).ToList();
        return new AppSettings
        {
            CoreState = settings,
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

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var core = settings.CoreState;
        var previousSpeed = core.DefaultSpeed;
        var previousVolume = core.DefaultVolume;
        var previousCovers = core.UseSourceCovers;
        core.HardwareDecode = settings.HardwareDecode;
        core.DefaultSpeed = settings.DefaultSpeed;
        core.DefaultVolume = settings.DefaultVolume;
        core.AutoFullscreen = settings.AutoFullscreen;
        core.UseSourceCovers = settings.UseSourceCovers;
        core.TmdbApiKey = settings.TmdbApiKey;
        core.DisabledSiteKeys = [.. settings.DisabledSiteKeys];
        var existingGroups = core.ConfigurationGroups.ToDictionary(item => item.Id);
        var activeGroups = settings.ConfigurationSources.Select(item =>
        {
            var id = ParseOrCreateId(item.Id, "configuration", item.Address);
            var enabled = string.Equals(item.Id, settings.ActiveConfigurationSourceId, StringComparison.Ordinal);
            if (existingGroups.TryGetValue(id, out var existing) &&
                existing.Name == item.Name && existing.Address == item.Address &&
                existing.IsEnabled == enabled && !existing.IsDeleted)
            {
                return existing;
            }

            return new ConfigurationGroupRecord(
                id,
                item.Name,
                item.Address,
                enabled,
                now,
                existing?.Version ?? 0);
        }).ToList();
        var removedGroups = core.ConfigurationGroups
            .Where(item => !item.IsDeleted && activeGroups.All(active => active.Id != item.Id))
            .Select(item => item with { IsDeleted = true, ModifiedAtUtc = now })
            .ToList();
        core.ConfigurationGroups =
        [
            .. core.ConfigurationGroups.Where(item => item.IsDeleted),
            .. removedGroups,
            .. activeGroups,
        ];
        core.CustomLiveSources = settings.CustomLiveSources.Select(item => new CustomLiveSourceRecord(
                ParseOrCreateId(item.Id, "live", item.Address),
                item.Name,
                item.Address,
                item.EpgAddress,
                now)).ToList();
        await _store.SaveAsync(core, cancellationToken);
        foreach (var group in activeGroups.Concat(removedGroups).Where(item =>
            !existingGroups.TryGetValue(item.Id, out var existing) || existing != item))
        {
            await _queue.EnqueueAsync(SyncMapper.ToMutation(group), cancellationToken);
        }

        if (previousSpeed != core.DefaultSpeed)
        {
            await _queue.EnqueueAsync(SyncMapper.Preference(core, "defaultSpeed", core.DefaultSpeed, now), cancellationToken);
        }

        if (previousVolume != core.DefaultVolume)
        {
            await _queue.EnqueueAsync(SyncMapper.Preference(core, "defaultVolume", core.DefaultVolume, now), cancellationToken);
        }

        if (previousCovers != core.UseSourceCovers)
        {
            await _queue.EnqueueAsync(SyncMapper.Preference(core, "useSourceCovers", core.UseSourceCovers, now), cancellationToken);
        }
    }

    private static Guid ParseOrCreateId(string? value, string kind, string address) =>
        Guid.TryParse(value, out var parsed) ? parsed : JsonLibraryStore.StableId(kind, address);

    public void Dispose()
    {
        _store.Dispose();
        _queue.Dispose();
    }
}
