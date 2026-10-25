using System.Text;

namespace Relay.Core;

/// <summary>
/// Splits the legacy minecraftArguments string. This is deliberately only a
/// tokenizer: it does not merge "-x value" pairs or interpret anything.
/// </summary>
public static class CommandLine
{
    public static IReadOnlyList<string> Split(string text, string path = "$.minecraftArguments")
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var hasToken = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (quoted)
            throw new RelayException(RelayErrorCode.UnsupportedValue, path, "unterminated quote");

        if (hasToken) result.Add(current.ToString());
        return result;
    }
}
