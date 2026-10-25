namespace Relay.Core;

/// <summary>
/// Public schema identity. Breaking changes get a new major path (/v2) and a
/// new <see cref="Current"/>; additive fields stay within the current schema.
/// </summary>
public static class RelaySchema
{
    public const int Current = 1;
    public const int ResolverVersion = 1;
    public const string Prefix = "/v1";
}

public sealed record SourceInfo(
    string VersionSha1,
    string? ManifestSha1,
    string? IndexSha1,
    int Resolver);

public sealed record JavaInfo(int Major, string Component, bool Defaulted);

public sealed record ExtractRule(IReadOnlyList<string> Exclude);

public sealed record PlanFile(
    string Role,
    string Path,
    string Sha1,
    long Size,
    IReadOnlyList<string> Urls,
    ExtractRule? Extract = null);

public sealed record LoggingInfo(string Argument, string Path);

public sealed record LaunchInfo(
    string MainClass,
    IReadOnlyList<string> Jvm,
    IReadOnlyList<string> JvmDefaults,
    IReadOnlyList<string> Game,
    LoggingInfo? Logging);

public sealed record AssetsRef(
    string Index,
    string Mode,
    string Root,
    string Document,
    long TotalBytes);

public sealed record NativesInfo(string Dir);

public sealed record ClientPlan(
    int Schema,
    string Kind,
    string Id,
    string Key,
    string Type,
    string ReleaseTime,
    string Platform,
    string Revision,
    SourceInfo Source,
    JavaInfo Java,
    AssetsRef Assets,
    long TotalBytes,
    LaunchInfo Launch,
    IReadOnlyList<string> Classpath,
    IReadOnlyList<PlanFile> Files,
    NativesInfo Natives,
    IReadOnlyList<string> Directories);

public sealed record ServerPlan(
    int Schema,
    string Kind,
    string Id,
    string Key,
    string Type,
    string ReleaseTime,
    string Revision,
    SourceInfo Source,
    JavaInfo Java,
    long TotalBytes,
    IReadOnlyList<PlanFile> Files,
    IReadOnlyList<string> Directories);

public sealed record AssetObject(
    string Path,
    string Sha1,
    long Size,
    IReadOnlyList<string> Urls);

public sealed record AssetLink(string From, string To);

public sealed record AssetsDocument(
    int Schema,
    string Index,
    string Sha1,
    string Mode,
    string Root,
    long TotalBytes,
    IReadOnlyList<AssetObject> Objects,
    IReadOnlyList<AssetLink> Links);

public sealed record VersionEntry(
    string Id,
    string Key,
    string Type,
    string ReleaseTime,
    int Java,
    IReadOnlyList<string> Platforms,
    bool Server,
    string? AssetsIndex);

public sealed record VersionsDocument(
    int Schema,
    int Resolver,
    IReadOnlyList<VersionEntry> Versions);

public sealed record MetaDocument(
    int Schema,
    int Resolver,
    string LatestRelease,
    string LatestSnapshot,
    IReadOnlyList<string> Platforms,
    int Count,
    int ClientPlans,
    int ServerPlans,
    string GeneratedAt);
