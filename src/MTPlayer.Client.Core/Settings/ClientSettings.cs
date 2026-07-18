using MTPlayer.Client.Core.Library;

namespace MTPlayer.Client.Core.Settings;

public sealed class ClientSettings
{
    public int SchemaVersion { get; set; } = 2;
    public bool HardwareDecode { get; set; } = true;
    public double DefaultSpeed { get; set; } = 1.0;
    public int DefaultVolume { get; set; } = 80;
    public bool AutoFullscreen { get; set; }
    public bool UseSourceCovers { get; set; } = true;
    public string TmdbApiKey { get; set; } = string.Empty;
    public List<string> DisabledSiteKeys { get; set; } = [];
    public List<ConfigurationGroupRecord> ConfigurationGroups { get; set; } = [];
    public List<CustomLiveSourceRecord> CustomLiveSources { get; set; } = [];
    public string PosterDensity { get; set; } = "standard";
    public string ServerUrl { get; set; } = string.Empty;
    public Guid DeviceId { get; set; }
    public long SyncCursor { get; set; }
    public Dictionary<string, PreferenceSyncState> PreferenceStates { get; set; } = new(StringComparer.Ordinal);
    public List<Guid> ManagedPushConfigurationIds { get; set; } = [];
    public List<Guid> ManagedPushLiveIds { get; set; } = [];
}

public sealed record PreferenceSyncState(long Version, DateTimeOffset ModifiedAtUtc, bool IsDeleted = false);

public sealed record CustomLiveSourceRecord(
    Guid Id,
    string Name,
    string Address,
    string? EpgAddress,
    DateTimeOffset ModifiedAtUtc,
    long Version = 0,
    bool IsDeleted = false);

public interface IClientSettingsStore
{
    Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default);
}
