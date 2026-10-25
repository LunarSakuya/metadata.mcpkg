using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core;

public static class RelayJson
{
    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Default,
    };

    public static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };

    public static byte[] Serialize<T>(T value, bool indented = false)
        => JsonSerializer.SerializeToUtf8Bytes(value, indented ? Indented : Compact);

    public static string SerializeString<T>(T value, bool indented = false)
        => JsonSerializer.Serialize(value, indented ? Indented : Compact);
}
