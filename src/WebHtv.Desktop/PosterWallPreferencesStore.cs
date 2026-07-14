using System;
using System.IO;
using System.Text.Json;

namespace WebHtv.Desktop;

internal sealed class PosterWallPreferencesStore(string filePath)
{
    private const double DefaultPosterWidth = 156;
    private readonly string _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

    public async Task<double> LoadWidthAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return DefaultPosterWidth;
        }

        await using var stream = File.OpenRead(_filePath);
        var preference = await JsonSerializer.DeserializeAsync<PosterWallPreference>(stream, cancellationToken: cancellationToken);
        return IsSupportedWidth(preference?.PosterWidth) ? preference!.PosterWidth : DefaultPosterWidth;
    }

    public async Task SaveWidthAsync(double posterWidth, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedWidth(posterWidth))
        {
            throw new ArgumentOutOfRangeException(nameof(posterWidth), "Unsupported poster width.");
        }

        var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("The preference path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, new PosterWallPreference(posterWidth), cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsSupportedWidth(double? posterWidth) => posterWidth is 132 or 156 or 180;

    private sealed record PosterWallPreference(double PosterWidth);
}
