using System;
using System.IO;
using MTPlayer.Client.Core.Settings;

namespace WebHtv.Desktop;

internal static class ApplicationPaths
{
    private static readonly string ApplicationDataDirectory = ResolveApplicationDataDirectory();

    public static string ConfigurationFilePath { get; } = Path.Combine(ApplicationDataDirectory, "configuration.json");

    public static string PosterWallPreferencesFilePath { get; } = Path.Combine(ApplicationDataDirectory, "poster-wall.json");

    public static string LibraryFilePath { get; } = Path.Combine(ApplicationDataDirectory, "library.json");

    public static string SettingsFilePath { get; } = Path.Combine(ApplicationDataDirectory, "settings.json");

    private static string ResolveApplicationDataDirectory()
    {
        if (Environment.GetEnvironmentVariable("WEBHTV_DATA_DIRECTORY") is { Length: > 0 } overrideDirectory)
        {
            return Path.GetFullPath(overrideDirectory);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacy = Path.Combine(local, "WebHomeTVDesktop");
        var product = Path.Combine(local, "MTPlayer");
        try
        {
            ApplicationDataMigrator.CopyMissingFiles(
                legacy,
                product,
                ["configuration.json", "poster-wall.json", "library.json", "settings.json"]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The app remains usable with the new directory; users can retry the copy after fixing permissions.
        }

        return product;
    }
}
