namespace WebHtv.Core.Configuration;

/// <summary>
/// A user-owned application configuration after it has been parsed and validated.
/// The original source text is retained so imported formats can be round-tripped safely.
/// </summary>
public sealed record ConfigurationDocument(
    int SchemaVersion,
    DateTimeOffset SavedAtUtc,
    string SourceText)
{
    public static ConfigurationDocument CreateEmpty() => new(
        SchemaVersion: 1,
        SavedAtUtc: DateTimeOffset.UtcNow,
        SourceText: "{}");
}
