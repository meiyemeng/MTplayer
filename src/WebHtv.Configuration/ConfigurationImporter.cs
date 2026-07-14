using WebHtv.Core.Configuration;

namespace WebHtv.Configuration;

public static class ConfigurationImporter
{
    public static ConfigurationDocument Import(string sourceText)
    {
        var decodedSourceText = TvBoxConfigurationPayloadDecoder.Decode(sourceText);
        var normalizedSourceText = TvBoxJsonNormalizer.EscapeControlCharactersInsideStrings(decodedSourceText);
        var document = new ConfigurationDocument(
            SchemaVersion: 1,
            SavedAtUtc: DateTimeOffset.UtcNow,
            SourceText: normalizedSourceText);

        var validation = ConfigurationDocumentValidator.Validate(document);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, validation.Errors), nameof(sourceText));
        }

        return document;
    }
}
