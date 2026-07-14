namespace WebHtv.Core.Configuration;

public sealed record ConfigurationValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public static ConfigurationValidationResult Success { get; } = new(true, Array.Empty<string>());
}
