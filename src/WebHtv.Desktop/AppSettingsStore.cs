using System.Text.Json;
using System.IO;

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

internal sealed class AppSettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _filePath = filePath;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("设置文件没有目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
