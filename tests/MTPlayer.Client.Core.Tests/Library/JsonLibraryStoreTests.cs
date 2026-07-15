using System.Text.Json;
using MTPlayer.Client.Core.Library;
using MTPlayer.Client.Core.Settings;
using Xunit;

namespace MTPlayer.Client.Core.Tests.Library;

public sealed class JsonLibraryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mtplayer-core-{Guid.NewGuid():N}");

    [Fact]
    public async Task Legacy_library_migrates_outro_absolute_position_to_remaining_seconds()
    {
        var path = Write("library.json", """
            {
              "skipMarkers": [
                { "sourceKey": "a", "id": "1", "introEndMs": 60000, "outroStartMs": 120000 }
              ]
            }
            """);
        using var store = new JsonLibraryStore(path);

        var library = await store.LoadAsync();

        var marker = Assert.Single(library.SkipMarkers);
        Assert.Equal(60, marker.IntroEndSeconds);
        Assert.Equal(0, marker.OutroRemainingSeconds);
        Assert.True(library.RequiresDurationRepair);
    }

    [Fact]
    public async Task Stable_records_round_trip_atomically_without_temporary_files()
    {
        var path = Path.Combine(_directory, "nested", "library.json");
        using var store = new JsonLibraryStore(path);
        var favorite = new FavoriteRecord(
            Guid.NewGuid(), "source", "content", "电影", "标题", "说明", "https://img.example/a.jpg", DateTimeOffset.UtcNow);
        var snapshot = new LibrarySnapshot { Favorites = [favorite] };

        await store.SaveAsync(snapshot);
        var restored = await store.LoadAsync();

        Assert.Equal(favorite, Assert.Single(restored.Favorites));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Legacy_settings_keep_the_active_configuration_and_custom_live_source()
    {
        var path = Write("settings.json", """
            {
              "defaultSpeed": 1.25,
              "defaultVolume": 70,
              "configurationSources": [
                { "id": "first", "name": "甲", "address": "https://a.example/config.json" },
                { "id": "second", "name": "乙", "address": "https://b.example/config.json" }
              ],
              "activeConfigurationSourceId": "second",
              "customLiveSources": [
                { "id": "live", "name": "直播", "address": "https://live.example/list.m3u", "epgAddress": "https://live.example/epg.xml" }
              ]
            }
            """);
        using var store = new JsonSettingsStore(path);

        var settings = await store.LoadAsync();

        Assert.Equal(1.25, settings.DefaultSpeed);
        Assert.Equal(70, settings.DefaultVolume);
        Assert.False(settings.ConfigurationGroups[0].IsEnabled);
        Assert.True(settings.ConfigurationGroups[1].IsEnabled);
        Assert.Equal("https://live.example/epg.xml", Assert.Single(settings.CustomLiveSources).EpgAddress);
    }

    [Fact]
    public void Product_folder_migration_copies_only_missing_files_and_never_overwrites()
    {
        var legacy = Path.Combine(_directory, "legacy");
        var product = Path.Combine(_directory, "product");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(product);
        File.WriteAllText(Path.Combine(legacy, "library.json"), "legacy-library");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy-settings");
        File.WriteAllText(Path.Combine(product, "settings.json"), "new-settings");

        ApplicationDataMigrator.CopyMissingFiles(legacy, product, ["library.json", "settings.json"]);

        Assert.Equal("legacy-library", File.ReadAllText(Path.Combine(product, "library.json")));
        Assert.Equal("new-settings", File.ReadAllText(Path.Combine(product, "settings.json")));
    }

    [Fact]
    public void Stable_id_is_repeatable_and_kind_scoped()
    {
        var first = JsonLibraryStore.StableId("favorite", "source", "content");
        Assert.Equal(first, JsonLibraryStore.StableId("favorite", "source", "content"));
        Assert.NotEqual(first, JsonLibraryStore.StableId("playback", "source", "content"));
    }

    [Fact]
    public async Task Local_delete_keeps_versioned_tombstone_for_other_devices()
    {
        var path = Path.Combine(_directory, "tombstone.json");
        using var store = new JsonLibraryStore(path);
        var favorite = new FavoriteRecord(
            Guid.NewGuid(), "source", "content", "电影", "标题", "", "", DateTimeOffset.UtcNow, 4);
        await store.SaveAsync(new LibrarySnapshot { Favorites = [favorite] });

        Assert.False(await store.ToggleFavoriteAsync(favorite with { ModifiedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) }));

        var tombstone = Assert.Single((await store.LoadAsync()).Favorites);
        Assert.True(tombstone.IsDeleted);
        Assert.Equal(4, tombstone.Version);
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
