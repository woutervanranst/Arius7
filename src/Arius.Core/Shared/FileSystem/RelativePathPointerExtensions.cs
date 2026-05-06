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
        path.ToString().EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converts a binary-file path into its pointer-file path.
    /// </summary>
    public static RelativePath ToPointerPath(this RelativePath path)
    {
        var value = path.ToString();
        if (value.Length == 0)
            throw new InvalidOperationException("Root path cannot be converted to a pointer path.");

        return RelativePath.Parse(value + PointerSuffix);
    }

    /// <summary>
    /// Converts a pointer-file path into the binary-file path it represents.
    /// </summary>
    public static RelativePath ToBinaryPath(this RelativePath path)
    {
        var value = path.ToString();
        if (!value.EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path is not a pointer path.");

        var binaryPath = value[..^PointerSuffix.Length];
        return RelativePath.Parse(binaryPath);
    }
}
