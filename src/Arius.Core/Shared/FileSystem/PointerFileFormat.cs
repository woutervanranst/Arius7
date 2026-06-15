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

    public static bool TryParseHash(string? content, out ContentHash hash) =>
        ContentHash.TryParse(content?.Trim(), out hash);

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
