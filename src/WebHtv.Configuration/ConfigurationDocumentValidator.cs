using WebHtv.Core.Configuration;

namespace WebHtv.Configuration;

public static class ConfigurationDocumentValidator
{
    public static ConfigurationValidationResult Validate(ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = new List<string>();
        if (document.SchemaVersion < 1)
        {
            errors.Add("The configuration schema version must be 1 or later.");
        }

        errors.AddRange(TvBoxProfileParser.Parse(document.SourceText).Errors);

        return errors.Count == 0
            ? ConfigurationValidationResult.Success
            : new ConfigurationValidationResult(false, errors);
    }
}
