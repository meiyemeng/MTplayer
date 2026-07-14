using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MTPlayer.Server.Mail;

public sealed record MailTemplateValues(
    string Email,
    string? VerificationUrl,
    string? ResetUrl,
    int ExpiresMinutes);

public static partial class MailTemplateRenderer
{
    public static readonly IReadOnlySet<string> VerificationTokens =
        new HashSet<string>(["email", "verificationUrl", "expiresMinutes"], StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> ResetTokens =
        new HashSet<string>(["email", "resetUrl", "expiresMinutes"], StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> TestTokens =
        new HashSet<string>(["email"], StringComparer.Ordinal);
    private static readonly HashSet<string> AllTokens =
        new HashSet<string>(["verificationUrl", "resetUrl", "email", "expiresMinutes"], StringComparer.Ordinal);

    public static void Validate(string template) => Validate(template, AllTokens);

    public static void Validate(string template, IReadOnlySet<string> allowedTokens)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(allowedTokens);
        var matches = TemplateTokenRegex().Matches(template);
        foreach (Match match in matches)
        {
            var token = match.Groups[1].Value;
            if (!AllTokens.Contains(token) || !allowedTokens.Contains(token))
            {
                throw new ArgumentException($"Template token '{{{token}}}' is not allowed.", nameof(template));
            }
        }

        var withoutTokens = TemplateTokenRegex().Replace(template, string.Empty);
        if (withoutTokens.Contains('{', StringComparison.Ordinal) ||
            withoutTokens.Contains('}', StringComparison.Ordinal))
        {
            throw new ArgumentException("Template contains an incomplete or malformed token.", nameof(template));
        }
    }

    public static string Render(string template, MailTemplateValues values) =>
        Render(template, values, AllTokens);

    public static string Render(
        string template,
        MailTemplateValues values,
        IReadOnlySet<string> allowedTokens)
    {
        ArgumentNullException.ThrowIfNull(values);
        Validate(template, allowedTokens);
        var output = new StringBuilder(template.Length + 128);
        var previousEnd = 0;
        foreach (Match match in TemplateTokenRegex().Matches(template))
        {
            output.Append(template, previousEnd, match.Index - previousEnd);
            var replacement = match.Groups[1].Value switch
            {
                "email" => values.Email,
                "verificationUrl" => values.VerificationUrl ?? throw MissingValue("verificationUrl"),
                "resetUrl" => values.ResetUrl ?? throw MissingValue("resetUrl"),
                "expiresMinutes" => values.ExpiresMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Validated token was not recognized."),
            };
            output.Append(WebUtility.HtmlEncode(replacement));
            previousEnd = match.Index + match.Length;
        }

        output.Append(template, previousEnd, template.Length - previousEnd);
        return output.ToString();
    }

    public static string RenderPlainText(
        string template,
        MailTemplateValues values,
        IReadOnlySet<string> allowedTokens)
    {
        ArgumentNullException.ThrowIfNull(values);
        Validate(template, allowedTokens);
        var rendered = TemplateTokenRegex().Replace(template, match => match.Groups[1].Value switch
        {
            "email" => values.Email,
            "verificationUrl" => values.VerificationUrl ?? throw MissingValue("verificationUrl"),
            "resetUrl" => values.ResetUrl ?? throw MissingValue("resetUrl"),
            "expiresMinutes" => values.ExpiresMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("Validated token was not recognized."),
        });
        if (rendered.Contains('\r', StringComparison.Ordinal) || rendered.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rendered mail subject contains a newline.");
        }

        return rendered;
    }

    private static InvalidOperationException MissingValue(string token) =>
        new($"Template value '{{{token}}}' is unavailable for this message.");

    [GeneratedRegex("\\{([A-Za-z][A-Za-z0-9]*)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateTokenRegex();
}
