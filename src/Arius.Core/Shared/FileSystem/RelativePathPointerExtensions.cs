namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Centralizes Arius pointer-file naming rules for repository-relative paths.
/// It exists so pointer detection and binary/pointer path conversion stay consistent across features,
/// with responsibility for applying and removing the <c>.pointer.arius</c> suffix safely.
/// </summary>
internal static class RelativePathPointerExtensions
{
    /// <summary>
    /// Gets the suffix used for Arius pointer files.
    /// </summary>
    internal const string PointerSuffix = ".pointer.arius";

    /// <summary>
    /// Determines whether a relative path refers to a pointer file.
    /// </summary>
    public static bool IsPointerPath(this RelativePath path) =>
        path.EndsWith(PointerSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Converts a binary-file path into its pointer-file path.
    /// </summary>
    public static RelativePath ToPointerPath(this RelativePath path)
    {
        if (path == RelativePath.Root)
            throw new InvalidOperationException("Root path cannot be converted to a pointer path.");

        return path.AppendSuffix(PointerSuffix);
    }

    /// <summary>
    /// Converts a pointer-file path into the binary-file path it represents.
    /// </summary>
    public static RelativePath ToBinaryPath(this RelativePath path)
    {
        if (!path.EndsWith(PointerSuffix, StringComparison.Ordinal))
            throw new InvalidOperationException("Path is not a pointer path.");

        return path.RemoveSuffix(PointerSuffix, StringComparison.Ordinal);
    }
}
