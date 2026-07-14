using System.Text.Json;
using WebHtv.Core.Configuration;

namespace WebHtv.Configuration;

public sealed class AtomicFileConfigurationStore(string filePath) : IConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

    public async Task<ConfigurationDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return ConfigurationDocument.CreateEmpty();
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<ConfigurationDocument>(stream, SerializerOptions, cancellationToken)
            ?? throw new InvalidDataException("The saved configuration file contains no document.");
    }

    public async Task SaveAsync(ConfigurationDocument document, CancellationToken cancellationToken = default)
    {
        var validation = ConfigurationDocumentValidator.Validate(document);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, validation.Errors), nameof(document));
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The configuration path must include a directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken);
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
}
