namespace Relay.Core;

/// <summary>
/// Stable, machine-readable error categories. The strings are part of the
/// public diagnostics contract; do not rename them without a schema bump.
/// </summary>
public enum RelayErrorCode
{
    MissingField,
    WrongType,
    BadSha1,
    UnsafePath,
    UnsafeId,
    UnsupportedUrl,
    BadSize,
    TooLarge,
    Overflow,
    DuplicateKey,
    PathConflict,
    HashSizeConflict,
    LinkConflict,
    IndexMismatch,
    UnsupportedValue,
}

/// <summary>
/// A deterministic, path-aware validation failure. <see cref="Path"/> is a
/// JSON-style field path such as "$.libraries[3].downloads.artifact.sha1".
/// Messages never embed raw untrusted values; callers that log diagnostics
/// must use their own redaction/escaping layer.
/// </summary>
public sealed class RelayException : Exception
{
    public RelayErrorCode Code { get; }
    public string Path { get; }

    public RelayException(RelayErrorCode code, string path, string message, Exception? inner = null)
        : base($"{code} at {path}: {message}", inner)
    {
        Code = code;
        Path = path;
    }
}
