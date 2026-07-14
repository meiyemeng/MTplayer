using System.Text;

namespace WebHtv.Configuration;

internal static class TvBoxJsonNormalizer
{
    public static string EscapeControlCharactersInsideStrings(string sourceText)
    {
        var result = new StringBuilder(sourceText.Length);
        var insideString = false;
        var escaped = false;

        foreach (var character in sourceText)
        {
            if (!insideString)
            {
                result.Append(character);
                if (character == '"')
                {
                    insideString = true;
                }

                continue;
            }

            if (escaped)
            {
                result.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                result.Append(character);
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                result.Append(character);
                insideString = false;
                continue;
            }

            if (character < ' ')
            {
                result.Append(character switch
                {
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => $"\\u{(int)character:X4}"
                });
                continue;
            }

            result.Append(character);
        }

        return result.ToString();
    }
}
