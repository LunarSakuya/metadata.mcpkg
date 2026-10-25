using System.Globalization;
using System.Text.Json;

namespace Relay.Core;

/// <summary>
/// Small, strict JSON accessors. Every accessor validates the JSON kind and
/// throws a <see cref="RelayException"/> with a field path; no raw
/// InvalidOperationException/FormatException escapes the parser.
/// </summary>
internal static class J
{
    public static JsonElement Object(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new RelayException(RelayErrorCode.WrongType, path, "expected a JSON object");
        return element;
    }

    public static JsonElement Required(JsonElement element, string name, string path)
    {
        Object(element, path);
        if (!element.TryGetProperty(name, out var value))
            throw new RelayException(RelayErrorCode.MissingField, Field(path, name), "required field is missing");
        return value;
    }

    public static JsonElement? Optional(JsonElement element, string name, string path)
    {
        Object(element, path);
        return element.TryGetProperty(name, out var value) ? value : null;
    }

    public static JsonElement RequiredArray(JsonElement element, string name, string path)
    {
        var value = Required(element, name, path);
        if (value.ValueKind != JsonValueKind.Array)
            throw new RelayException(RelayErrorCode.WrongType, Field(path, name), "expected a JSON array");
        return value;
    }

    public static JsonElement? OptionalArray(JsonElement element, string name, string path)
    {
        var value = Optional(element, name, path);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (value.Value.ValueKind != JsonValueKind.Array)
            throw new RelayException(RelayErrorCode.WrongType, Field(path, name), "expected a JSON array");
        return value;
    }

    public static JsonElement RequiredObject(JsonElement element, string name, string path)
    {
        var value = Required(element, name, path);
        return Object(value, Field(path, name));
    }

    public static JsonElement? OptionalObject(JsonElement element, string name, string path)
    {
        var value = Optional(element, name, path);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        return Object(value.Value, Field(path, name));
    }

    public static string RequiredString(JsonElement element, string name, string path)
    {
        var value = Required(element, name, path);
        if (value.ValueKind != JsonValueKind.String)
            throw new RelayException(RelayErrorCode.WrongType, Field(path, name), "expected a string");
        return value.GetString()!;
    }

    public static string? OptionalString(JsonElement element, string name, string path)
    {
        var value = Optional(element, name, path);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (value.Value.ValueKind != JsonValueKind.String)
            throw new RelayException(RelayErrorCode.WrongType, Field(path, name), "expected a string");
        return value.Value.GetString();
    }

    public static bool RequiredBool(JsonElement element, string name, string path)
    {
        var value = Required(element, name, path);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new RelayException(RelayErrorCode.WrongType, Field(path, name), "expected a boolean"),
        };
    }

    public static bool? OptionalBool(JsonElement element, string name, string path)
    {
        var value = Optional(element, name, path);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        return value.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new RelayException(RelayErrorCode.WrongType, Field(path, name), "expected a boolean"),
        };
    }

    public static long RequiredLong(JsonElement element, string name, string path)
    {
        var value = Required(element, name, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new RelayException(RelayErrorCode.WrongType, Field(path, name), "expected an integer");
        if (number < 0)
            throw new RelayException(RelayErrorCode.BadSize, Field(path, name), "expected a non-negative integer");
        return number;
    }

    public static long? OptionalLong(JsonElement element, string name, string path)
    {
        var value = Optional(element, name, path);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetInt64(out var number))
            throw new RelayException(RelayErrorCode.WrongType, Field(path, name), "expected an integer");
        if (number < 0)
            throw new RelayException(RelayErrorCode.BadSize, Field(path, name), "expected a non-negative integer");
        return number;
    }

    public static int RequiredInt(JsonElement element, string name, string path)
    {
        var value = Required(element, name, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new RelayException(RelayErrorCode.WrongType, Field(path, name), "expected an integer");
        return number;
    }

    public static string RequiredSha1(JsonElement element, string name, string path)
    {
        var raw = RequiredString(element, name, path);
        return Sha1.Parse(raw, Field(path, name)).Value;
    }

    public static long RequiredSize(JsonElement element, string name, string path)
    {
        var raw = RequiredLong(element, name, path);
        return NonNegativeSize.Parse(raw, Field(path, name)).Value;
    }

    public static string RequiredUrl(JsonElement element, string name, string path)
    {
        var raw = RequiredString(element, name, path);
        return OfficialHttpsUrl.Parse(NormalizeUrl(raw), Field(path, name)).Value;
    }

    public static string Field(string path, string name) => path + "." + name;

    public static string Index(string path, int index) => path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>
    /// Normalizes the two legacy Mojang hosts to their immutable object hosts.
    /// This mirrors the official metadata's historical URL vocabulary.
    /// </summary>
    public static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        if (uri.Scheme != Uri.UriSchemeHttps) return value;

        if (uri.Host == "launcher.mojang.com"
            && uri.AbsolutePath.StartsWith("/v1/objects/", StringComparison.Ordinal))
            return "https://piston-data.mojang.com" + uri.AbsolutePath;

        if (uri.Host == "launchermeta.mojang.com"
            && uri.AbsolutePath.StartsWith("/v1/packages/", StringComparison.Ordinal))
            return "https://piston-meta.mojang.com" + uri.AbsolutePath;

        return value;
    }
}

internal static class JsonRead
{
    public static JsonDocument Parse(byte[] bytes, string path, int maxBytes)
    {
        if (bytes.Length > maxBytes)
            throw new RelayException(RelayErrorCode.TooLarge, path, $"JSON document exceeds {maxBytes} bytes");

        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException error)
        {
            throw new RelayException(RelayErrorCode.UnsupportedValue, path, "invalid JSON", error);
        }
    }
}
