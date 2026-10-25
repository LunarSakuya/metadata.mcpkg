using System.Text.RegularExpressions;

namespace Relay.Core;

/// <summary>
/// Collapses Mojang's historical placeholder vocabulary into the fixed public
/// one. Values that can be known at build time are substituted; runtime values
/// remain as the documented public placeholders.
/// </summary>
public static partial class Placeholders
{
    private static readonly HashSet<string> AllowedRuntime = new(StringComparer.Ordinal)
    {
        "gameDir", "assetsDir", "nativesDir", "classpath", "logConfig",
        "auth_player_name", "auth_uuid", "authToken", "auth_xuid", "clientid",
        "user_type", "user_properties",
    };

    [GeneratedRegex(@"\$\{([a-zA-Z0-9_]+)\}")]
    private static partial Regex Token();

    public static string Apply(string text, string id, string type, string indexId, Platform platform)
    {
        return Token().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            return name switch
            {
                "version_name" => id,
                "version_type" => type,
                "assets_index_name" => indexId,
                "launcher_name" => "relay",
                "launcher_version" => "1",
                "classpath_separator" => platform.Separator,

                "game_directory" => "${gameDir}",
                "game_assets" => "${assetsDir}",
                "assets_root" => "${assetsDir}",
                "natives_directory" => "${nativesDir}",

                "auth_session" => "${authToken}",
                "auth_access_token" => "${authToken}",

                _ => match.Value,
            };
        });
    }

    public static bool IsAllowedRuntime(string name) => AllowedRuntime.Contains(name);

    public static IReadOnlyList<string> FindUnknown(string text)
    {
        var unknown = new List<string>();
        foreach (Match match in Token().Matches(text))
        {
            var name = match.Groups[1].Value;
            if (!AllowedRuntime.Contains(name)) unknown.Add(name);
        }

        return unknown;
    }
}
