namespace Relay.Core;

/// <summary>
/// Public platform token. Once published these tokens are immutable; new
/// platforms may only be appended.
/// </summary>
public sealed record Platform(string Token, string Os, string Arch, int Bits)
{
    public string Separator => Os == "windows" ? ";" : ":";

    public static readonly IReadOnlyList<string> All =
    [
        "windows-x64", "windows-x86", "windows-arm64",
        "linux-x64", "linux-arm64",
        "osx-x64", "osx-arm64",
    ];

    public static Platform Parse(string token) => token switch
    {
        "windows-x64" => new(token, "windows", "x86_64", 64),
        "windows-x86" => new(token, "windows", "x86", 32),
        "windows-arm64" => new(token, "windows", "arm64", 64),
        "linux-x64" => new(token, "linux", "x86_64", 64),
        "linux-arm64" => new(token, "linux", "arm64", 64),
        "osx-x64" => new(token, "osx", "x86_64", 64),
        "osx-arm64" => new(token, "osx", "arm64", 64),
        _ => throw new RelayException(RelayErrorCode.UnsupportedValue, "$.platform", "unknown platform token"),
    };
}
