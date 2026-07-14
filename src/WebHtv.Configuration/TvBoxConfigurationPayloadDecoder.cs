using System.Text;

namespace WebHtv.Configuration;

/// <summary>
/// Decodes the small wrapper used by several TVBox configuration endpoints.
/// Plain JSON is returned unchanged.
/// </summary>
public static class TvBoxConfigurationPayloadDecoder
{
    public static string Decode(string sourceText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);

        var trimmed = sourceText.Trim();
        if (!trimmed.StartsWith("jhSP", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var separatorIndex = trimmed.IndexOf("**", StringComparison.Ordinal);
        if (separatorIndex < 4 || separatorIndex + 2 >= trimmed.Length)
        {
            throw new ArgumentException("The configuration wrapper is incomplete.", nameof(sourceText));
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(trimmed[(separatorIndex + 2)..])).Trim();
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The configuration wrapper contains invalid Base64 data.", nameof(sourceText), exception);
        }
    }
}
