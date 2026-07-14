using System;
using System.IO;

namespace WebHtv.Desktop;

internal static class ApplicationPaths
{
    private static readonly string ApplicationDataDirectory =
        Environment.GetEnvironmentVariable("WEBHTV_DATA_DIRECTORY") is { Length: > 0 } overrideDirectory
            ? overrideDirectory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WebHomeTVDesktop");

    public static string ConfigurationFilePath { get; } = Path.Combine(ApplicationDataDirectory, "configuration.json");

    public static string PosterWallPreferencesFilePath { get; } = Path.Combine(ApplicationDataDirectory, "poster-wall.json");

    public static string LibraryFilePath { get; } = Path.Combine(ApplicationDataDirectory, "library.json");

    public static string SettingsFilePath { get; } = Path.Combine(ApplicationDataDirectory, "settings.json");
}
