using System.Text.Json;
using MTPlayer.Client.Core.Library;

namespace MTPlayer.Client.Core.Settings;

public sealed class JsonSettingsStore(string filePath) : IClientSettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public async Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath))
            {
                return new ClientSettings();
            }

            try
            {
                await using var stream = File.OpenRead(FilePath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) &&
                    schemaVersion.TryGetInt32(out var version) && version >= 2)
                {
                    return Normalize(document.RootElement.Deserialize<ClientSettings>(JsonOptions) ?? new ClientSettings());
                }

                return MigrateLegacy(document.RootElement);
            }
            catch (JsonException)
            {
                return new ClientSettings();
            }
            catch (IOException)
            {
                return new ClientSettings();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(FilePath) ??
                throw new InvalidOperationException("Settings file must have a directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, Normalize(settings), JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ClientSettings Normalize(ClientSettings settings)
    {
        settings.SchemaVersion = 2;
        settings.DefaultSpeed = Math.Clamp(settings.DefaultSpeed, 0.5, 2.0);
        settings.DefaultVolume = Math.Clamp(settings.DefaultVolume, 0, 100);
        settings.TmdbApiKey ??= string.Empty;
        settings.DisabledSiteKeys ??= [];
        settings.ConfigurationGroups ??= [];
        settings.CustomLiveSources ??= [];
        settings.PosterDensity = string.IsNullOrWhiteSpace(settings.PosterDensity) ? "standard" : settings.PosterDensity;
        settings.SyncCursor = Math.Max(0, settings.SyncCursor);
        return settings;
    }

    private static ClientSettings MigrateLegacy(JsonElement root)
    {
        var legacy = root.Deserialize<LegacySettings>(JsonOptions) ?? new LegacySettings();
        var activeId = legacy.ActiveConfigurationSourceId ?? string.Empty;
        var settings = new ClientSettings
        {
            HardwareDecode = legacy.HardwareDecode,
            DefaultSpeed = legacy.DefaultSpeed,
            DefaultVolume = legacy.DefaultVolume,
            AutoFullscreen = legacy.AutoFullscreen,
            UseSourceCovers = legacy.UseSourceCovers,
            TmdbApiKey = legacy.TmdbApiKey ?? string.Empty,
            DisabledSiteKeys = legacy.DisabledSiteKeys ?? [],
        };
        foreach (var source in legacy.ConfigurationSources ?? [])
        {
            var id = Guid.TryParse(source.Id, out var parsed)
                ? parsed
                : JsonLibraryStore.StableId("configuration", source.Address ?? string.Empty);
            settings.ConfigurationGroups.Add(new ConfigurationGroupRecord(
                id,
                source.Name ?? string.Empty,
                source.Address ?? string.Empty,
                string.IsNullOrEmpty(activeId) || string.Equals(source.Id, activeId, StringComparison.Ordinal),
                DateTimeOffset.UnixEpoch));
        }

        foreach (var source in legacy.CustomLiveSources ?? [])
        {
            var id = Guid.TryParse(source.Id, out var parsed)
                ? parsed
                : JsonLibraryStore.StableId("live", source.Address ?? string.Empty);
            settings.CustomLiveSources.Add(new CustomLiveSourceRecord(
                id,
                source.Name ?? string.Empty,
                source.Address ?? string.Empty,
                source.EpgAddress,
                DateTimeOffset.UnixEpoch));
        }

        return Normalize(settings);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class LegacySettings
    {
        public bool HardwareDecode { get; set; } = true;
        public double DefaultSpeed { get; set; } = 1.0;
        public int DefaultVolume { get; set; } = 80;
        public bool AutoFullscreen { get; set; }
        public bool UseSourceCovers { get; set; } = true;
        public string? TmdbApiKey { get; set; }
        public List<string>? DisabledSiteKeys { get; set; }
        public List<LegacyConfigurationSource>? ConfigurationSources { get; set; }
        public string? ActiveConfigurationSourceId { get; set; }
        public List<LegacyLiveSource>? CustomLiveSources { get; set; }
    }

    private sealed class LegacyConfigurationSource
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Address { get; set; }
    }

    private sealed class LegacyLiveSource
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Address { get; set; }
        public string? EpgAddress { get; set; }
    }
}

public static class ApplicationDataMigrator
{
    public static void CopyMissingFiles(
        string legacyDirectory,
        string productDirectory,
        IEnumerable<string> fileNames)
    {
        if (!Directory.Exists(legacyDirectory))
        {
            return;
        }

        Directory.CreateDirectory(productDirectory);
        foreach (var fileName in fileNames)
        {
            if (Path.GetFileName(fileName) != fileName)
            {
                throw new ArgumentException("Migration entries must be file names only.", nameof(fileNames));
            }

            var source = Path.Combine(legacyDirectory, fileName);
            var destination = Path.Combine(productDirectory, fileName);
            if (!File.Exists(source) || File.Exists(destination))
            {
                continue;
            }

            var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(source, temporary, overwrite: false);
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
