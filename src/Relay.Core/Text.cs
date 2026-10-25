using System.Globalization;
using System.Text;

namespace Relay.Core;

/// <summary>
/// Renders untrusted text for a terminal/log line without allowing control
/// sequences, bidi overrides or line separators to change the output line.
/// </summary>
public static class Text
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsControl(c)
                || char.IsSurrogate(c)
                || char.GetUnicodeCategory(c) is UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
            {
                builder.Append("\\u{");
                builder.Append(((int)c).ToString("x", CultureInfo.InvariantCulture));
                builder.Append('}');
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
