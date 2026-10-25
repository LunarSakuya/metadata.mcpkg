using System.Globalization;
using System.Text;

namespace Relay.Core;

/// <summary>
/// A canonical, lowercase SHA-1. Official Mojang metadata uses lowercase;
/// accepting mixed case would make equality, dedupe and revision hashes
/// ambiguous.
/// </summary>
public readonly record struct Sha1
{
    public string Value { get; } = string.Empty;

    private Sha1(string value) => Value = value;

    public static Sha1 Parse(string? value, string path = "$")
    {
        if (value is null || value.Length != 40 || !value.All(IsLowerHex))
            throw new RelayException(RelayErrorCode.BadSha1, path, "expected 40 lowercase hex characters");

        return new Sha1(value);
    }

    public override string ToString() => Value;

    private static bool IsLowerHex(char c)
        => c is >= '0' and <= '9' or >= 'a' and <= 'f';
}

/// <summary>
/// A version identifier that can safely become exactly one path component
/// and one JSON/URL segment. Official ids may contain internal spaces
/// ("1.14 Pre-Release 1"), but never path/device syntax.
/// </summary>
public readonly record struct SafeVersionId
{
    public const int MaxBytes = 128;

    public string Value { get; } = string.Empty;

    private SafeVersionId(string value) => Value = value;

    public static SafeVersionId Parse(string? value, string path = "$")
    {
        if (string.IsNullOrEmpty(value))
            throw new RelayException(RelayErrorCode.UnsafeId, path, "version id is empty");

        if (Encoding.UTF8.GetByteCount(value) > MaxBytes)
            throw new RelayException(RelayErrorCode.UnsafeId, path, $"version id is longer than {MaxBytes} bytes");

        if (value is "." or "..")
            throw new RelayException(RelayErrorCode.UnsafeId, path, "version id is a dot component");

        if (value[0] == '-')
            throw new RelayException(RelayErrorCode.UnsafeId, path, "version id starts with '-'");

        if (value.Trim() != value)
            throw new RelayException(RelayErrorCode.UnsafeId, path, "version id has leading or trailing whitespace");

        if (value[^1] == '.')
            throw new RelayException(RelayErrorCode.UnsafeId, path, "version id has a trailing dot");

        if (value.Contains("${", StringComparison.Ordinal))
            throw new RelayException(RelayErrorCode.UnsafeId, path, "version id contains a placeholder sequence");

        foreach (var c in value)
        {
            if (c > 0x7F)
                throw new RelayException(RelayErrorCode.UnsafeId, path, "version id is not ASCII");

            if (IsUnsafeIdChar(c))
                throw new RelayException(RelayErrorCode.UnsafeId, path, "version id contains a reserved or control character");
        }

        if (ValueRules.IsWindowsReserved(value))
            throw new RelayException(RelayErrorCode.UnsafeId, path, "version id is a reserved Windows name");

        return new SafeVersionId(value);
    }

    public override string ToString() => Value;

    private static bool IsUnsafeIdChar(char c)
        => ValueRules.IsUnsafePathChar(c)
           || c is '$' or '{' or '}'
           || c is '/' or '\\';
}

/// <summary>
/// A relative path that is safe on every supported platform. This deliberately
/// rejects Windows-only syntax even on Unix so that a plan generated on one
/// host cannot become unsafe when consumed on another.
/// </summary>
public readonly record struct SafeRelativePath
{
    public const int MaxBytes = 1024;
    public const int MaxComponentBytes = 255;

    public string Value { get; } = string.Empty;

    private SafeRelativePath(string value) => Value = value;

    public static SafeRelativePath Parse(string? value, string path = "$")
    {
        if (string.IsNullOrEmpty(value))
            throw new RelayException(RelayErrorCode.UnsafePath, path, "path is empty");

        if (Encoding.UTF8.GetByteCount(value) > MaxBytes)
            throw new RelayException(RelayErrorCode.UnsafePath, path, $"path is longer than {MaxBytes} bytes");

        if (value[0] is '/' or '\\')
            throw new RelayException(RelayErrorCode.UnsafePath, path, "path is absolute");

        if (value.Contains('\\'))
            throw new RelayException(RelayErrorCode.UnsafePath, path, "path contains a backslash");

        if (value.Contains("${", StringComparison.Ordinal))
            throw new RelayException(RelayErrorCode.UnsafePath, path, "path contains a placeholder sequence");

        var segments = value.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new RelayException(RelayErrorCode.UnsafePath, path, "path contains an empty segment");

            if (segment is "." or "..")
                throw new RelayException(RelayErrorCode.UnsafePath, path, "path contains a dot segment");

            if (Encoding.UTF8.GetByteCount(segment) > MaxComponentBytes)
                throw new RelayException(RelayErrorCode.UnsafePath, path, $"path component is longer than {MaxComponentBytes} bytes");

            if (segment.Trim() != segment)
                throw new RelayException(RelayErrorCode.UnsafePath, path, "path component has leading or trailing whitespace");

            if (segment[^1] is '.' or ' ')
                throw new RelayException(RelayErrorCode.UnsafePath, path, "path component has a trailing dot or space");

            foreach (var c in segment)
            {
                if (ValueRules.IsUnsafePathChar(c))
                    throw new RelayException(RelayErrorCode.UnsafePath, path, "path component contains a reserved or control character");
            }

            if (ValueRules.IsWindowsReserved(segment))
                throw new RelayException(RelayErrorCode.UnsafePath, path, "path component is a reserved Windows name");
        }

        return new SafeRelativePath(value);
    }

    public override string ToString() => Value;
}

/// <summary>
/// An HTTPS URL on one of the four Mojang-operated hosts, with default port,
/// no userinfo, and no query/fragment. Official artifact URLs do not need
/// query strings; rejecting them prevents accidental token/caching ambiguity.
/// </summary>
public readonly record struct OfficialHttpsUrl
{
    public const int MaxBytes = 2048;

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "piston-data.mojang.com",
        "piston-meta.mojang.com",
        "libraries.minecraft.net",
        "resources.download.minecraft.net",
    };

    public string Value { get; } = string.Empty;

    private OfficialHttpsUrl(string value) => Value = value;

    public static OfficialHttpsUrl Parse(string? value, string path = "$")
    {
        if (string.IsNullOrEmpty(value))
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, "URL is empty");

        if (Encoding.UTF8.GetByteCount(value) > MaxBytes)
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, $"URL is longer than {MaxBytes} bytes");

        if (value.Contains('\\'))
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, "URL contains a backslash");

        if (value.Any(char.IsControl))
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, "URL contains a control character");

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, "URL is not absolute");

        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, "URL is not HTTPS");

        if (!uri.IsDefaultPort)
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, "URL uses a non-default port");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, "URL contains userinfo");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, "URL contains a query or fragment");

        if (!AllowedHosts.Contains(uri.Host))
            throw new RelayException(RelayErrorCode.UnsupportedUrl, path, "URL host is not an official Mojang source");

        return new OfficialHttpsUrl(uri.AbsoluteUri);
    }

    public override string ToString() => Value;
}

/// <summary>
/// A non-negative byte count with an explicit upper bound. The default bound
/// is the single-file limit; callers may pass a smaller role-specific limit.
/// </summary>
public readonly record struct NonNegativeSize
{
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

    public long Value { get; }

    private NonNegativeSize(long value) => Value = value;

    public static NonNegativeSize Parse(long value, string path = "$", long max = MaxFileBytes)
    {
        if (value < 0)
            throw new RelayException(RelayErrorCode.BadSize, path, "size is negative");

        if (value > max)
            throw new RelayException(RelayErrorCode.TooLarge, path, $"size exceeds the {max} byte limit");

        return new NonNegativeSize(value);
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

internal static class ValueRules
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.Ordinal)
    {
        "CON", "PRN", "AUX", "NUL",
        "CONIN$", "CONOUT$", "CLOCK$",
        "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
    };

    public static bool IsUnsafePathChar(char c)
        => char.IsControl(c)
           || c == '\u007f'
           || char.IsSurrogate(c)
           || char.GetUnicodeCategory(c) is UnicodeCategory.Format
               or UnicodeCategory.LineSeparator
               or UnicodeCategory.ParagraphSeparator
           || c is '<' or '>' or '"' or '|' or '?' or '*' or ':';

    public static bool IsWindowsReserved(string component)
    {
        var baseName = component.Split('.')[0].TrimEnd(' ', '.');
        if (baseName.Length == 0) return false;

        var upper = baseName.ToUpperInvariant();
        if (ReservedWindowsNames.Contains(upper)) return true;

        return upper.Length == 4
               && (upper.StartsWith("COM", StringComparison.Ordinal)
                   || upper.StartsWith("LPT", StringComparison.Ordinal))
               && char.IsAsciiDigit(upper[3]);
    }
}
