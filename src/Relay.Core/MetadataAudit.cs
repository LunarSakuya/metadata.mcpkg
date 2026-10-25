using System.Security.Cryptography;
using System.Text.Json;

namespace Relay.Core;

public sealed record ManifestEntry(
    string Id,
    string Sha1,
    string? Type,
    string? ReleaseTime,
    string? Url);

/// <summary>
/// Build-time integrity audit. The v2 manifest is the pin for each version
/// document; a v1 manifest without sha1 is rejected.
/// </summary>
public static class MetadataAudit
{
    // Deliberately generous ceilings: the manifest size is the real guard.
    public const int MaxManifestBytes = 256 * 1024 * 1024;
    public const int MaxManifestVersions = 1_000_000;

    public static IReadOnlyDictionary<string, ManifestEntry> LoadManifest(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new RelayException(RelayErrorCode.MissingField, "$.manifest", "manifest file does not exist");
        if (file.Length > MaxManifestBytes)
            throw new RelayException(RelayErrorCode.TooLarge, "$.manifest", "manifest file is too large");

        var bytes = File.ReadAllBytes(path);
        using var document = JsonRead.Parse(bytes, "$.manifest", MaxManifestBytes);
        var root = J.Object(document.RootElement, "$.manifest");
        var versions = J.RequiredArray(root, "versions", "$.manifest");

        var result = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        var index = 0;
        foreach (var version in versions.EnumerateArray())
        {
            var itemPath = J.Index("$.manifest.versions", index++);
            if (index > MaxManifestVersions)
                throw new RelayException(RelayErrorCode.TooLarge, "$.manifest.versions", "too many manifest entries");

            J.Object(version, itemPath);
            var id = SafeVersionId.Parse(J.RequiredString(version, "id", itemPath), J.Field(itemPath, "id")).Value;
            var sha1 = J.RequiredSha1(version, "sha1", itemPath);
            var type = J.OptionalString(version, "type", itemPath);
            var releaseTime = J.OptionalString(version, "releaseTime", itemPath);
            var url = J.OptionalString(version, "url", itemPath);
            if (url is not null)
                url = OfficialHttpsUrl.Parse(J.NormalizeUrl(url), J.Field(itemPath, "url")).Value;

            if (!result.TryAdd(id, new ManifestEntry(id, sha1, type, releaseTime, url)))
                throw new RelayException(RelayErrorCode.DuplicateKey, J.Field(itemPath, "id"), "duplicate version id");
        }

        return result;
    }

    public static string Sha1Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA1.HashData(bytes));

    public static void VerifyBytes(byte[] bytes, string expectedSha1, string path)
    {
        var actual = Sha1Hex(bytes);
        if (!string.Equals(actual, expectedSha1, StringComparison.Ordinal))
            throw new RelayException(RelayErrorCode.BadSha1, path, "SHA-1 mismatch");
    }
}

