using System.Text.Json;

namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Centralizes Arius pointer-file format rules.
/// It exists so pointer naming, content parsing, content writing, and timestamp policy stay consistent
/// across archive, restore, and local filesystem enumeration.
/// </summary>
[SharedWithinAssembly]
internal static class PointerFileFormat
{
    /// <summary>
    /// Gets the suffix used for Arius pointer files.
    /// </summary>
    internal const string PointerSuffix = ".pointer.arius";

    /// <summary>
    /// Determines whether a relative path refers to a pointer file.
    /// </summary>
    public static bool IsPointerPath(this RelativePath path) =>
        path.EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converts a binary-file path into its pointer-file path.
    /// </summary>
    public static RelativePath ToPointerPath(this RelativePath path)
    {
        if (path == RelativePath.Root)
            throw new InvalidOperationException("Root path cannot be converted to a pointer path.");

        var value = path.ToString();
        if (value.EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path is already a pointer path.");

        return path.AppendSuffix(PointerSuffix);
    }

    /// <summary>
    /// Converts a pointer-file path into the binary-file path it represents.
    /// </summary>
    public static RelativePath ToBinaryPath(this RelativePath path)
    {
        if (!path.EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path is not a pointer path.");

        var binaryPath = path.ToString()[..^PointerSuffix.Length];
        if (binaryPath.Length == 0)
            throw new InvalidOperationException("Pointer path must target a non-root binary path.");

        if (binaryPath.EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pointer path cannot contain the pointer suffix twice.");

        return RelativePath.Parse(binaryPath);
    }

    public static string Serialize(ContentHash hash) => hash.ToString();

    /// <summary>
    /// Parses the hash from a pointer file's content, accepting both the current (v7) format — the bare
    /// lowercase hex hash — and the legacy (v5) format — a JSON object <c>{"BinaryHash":"&lt;hex&gt;"}</c>.
    /// </summary>
    public static bool TryParseHash(string? content, out ContentHash hash) =>
        TryParseHash(content, out hash, out _);

    /// <summary>
    /// Parses the hash from a pointer file's content and reports whether it was a legacy (v5) JSON pointer.
    /// The archive command upgrades legacy pointers to the current format in place.
    /// </summary>
    public static bool TryParseHash(string? content, out ContentHash hash, out bool isLegacyFormat)
    {
        isLegacyFormat = false;
        var trimmed = content?.Trim();

        // Current (v7) format: the file content is the bare lowercased hex hash.
        if (ContentHash.TryParse(trimmed, out hash))
            return true;

        // Legacy (v5) format: a JSON object {"BinaryHash":"<hex>"} written by old clients.
        if (TryParseLegacyHash(trimmed, out hash))
        {
            isLegacyFormat = true;
            return true;
        }

        return false;
    }

    // A v7 pointer (bare hex) never starts with '{', and v5 JSON never parses as hex, so the two formats are
    // unambiguous and the bare-hex fast path above always wins for v7 pointers.
    private static bool TryParseLegacyHash(string? trimmed, out ContentHash hash)
    {
        hash = default;
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{')
            return false;

        try
        {
            var contents = JsonSerializer.Deserialize<LegacyPointerContents>(trimmed, LegacyJsonOptions);
            return contents?.BinaryHash is { } binaryHash && ContentHash.TryParse(binaryHash, out hash);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions LegacyJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The legacy (v5) pointer-file JSON shape: <c>{"BinaryHash":"&lt;hex&gt;"}</c>.</summary>
    private sealed record LegacyPointerContents(string? BinaryHash);

    public static async Task WriteAsync(
        RelativeFileSystem fileSystem,
        RelativePath        binaryPath,
        ContentHash         hash,
        DateTimeOffset      created,
        DateTimeOffset      modified,
        CancellationToken   cancellationToken)
    {
        var pointerPath = binaryPath.ToPointerPath();
        await fileSystem.WriteAllTextAsync(pointerPath, Serialize(hash), cancellationToken);
        fileSystem.SetTimestamps(pointerPath, created, modified);
    }
}
