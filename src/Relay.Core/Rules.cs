using System.Text.Json;

namespace Relay.Core;

public sealed class RuleContext
{
    public required Platform Platform { get; init; }
    public IReadOnlyDictionary<string, bool> Features { get; init; } =
        new Dictionary<string, bool>(StringComparer.Ordinal);
}

/// <summary>
/// Evaluate Mojang rule arrays. Last matching rule wins, matching the official
/// launcher and mainstream launchers. Facts not present in the platform token
/// (os.version/versionRange) never match; we do not guess.
/// </summary>
public static class Rules
{
    public static bool Evaluate(JsonElement element, RuleContext context, string path)
    {
        J.Object(element, path);
        var rules = J.OptionalArray(element, "rules", path);
        if (rules is null) return true;

        var rulesPath = J.Field(path, "rules");
        var any = false;
        var decision = false;
        var index = 0;
        foreach (var rule in rules.Value.EnumerateArray())
        {
            var rulePath = J.Index(rulesPath, index++);
            J.Object(rule, rulePath);
            any = true;
            if (Matches(rule, context, rulePath))
                decision = IsAllow(rule, rulePath);
        }

        return any ? decision : true;
    }

    private static bool IsAllow(JsonElement rule, string path)
    {
        var action = J.RequiredString(rule, "action", path);
        return action switch
        {
            "allow" => true,
            "disallow" => false,
            _ => throw new RelayException(RelayErrorCode.UnsupportedValue, J.Field(path, "action"), "unknown rule action"),
        };
    }

    private static bool Matches(JsonElement rule, RuleContext context, string path)
    {
        var os = J.OptionalObject(rule, "os", path);
        if (os is not null)
        {
            var osPath = J.Field(path, "os");
            var name = J.OptionalString(os.Value, "name", osPath);
            if (name is not null && !string.Equals(name, context.Platform.Os, StringComparison.Ordinal))
                return false;

            var arch = J.OptionalString(os.Value, "arch", osPath);
            if (arch is not null && !string.Equals(arch, context.Platform.Arch, StringComparison.Ordinal))
                return false;

            // OS build numbers are not part of the platform token; do not guess.
            if (os.Value.TryGetProperty("version", out _)) return false;
            if (os.Value.TryGetProperty("versionRange", out _)) return false;
        }

        var features = J.OptionalObject(rule, "features", path);
        if (features is not null)
        {
            var featuresPath = J.Field(path, "features");
            foreach (var feature in features.Value.EnumerateObject())
            {
                var valuePath = J.Field(featuresPath, feature.Name);
                bool expected = feature.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new RelayException(RelayErrorCode.WrongType, valuePath, "feature value must be a boolean"),
                };

                var actual = context.Features.TryGetValue(feature.Name, out var enabled) && enabled;
                if (actual != expected) return false;
            }
        }

        return true;
    }
}
