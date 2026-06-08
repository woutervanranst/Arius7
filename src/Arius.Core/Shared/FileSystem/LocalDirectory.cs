namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Represents an absolute local filesystem root that Arius is allowed to operate within.
/// It exists to separate host paths from repository-relative paths, with responsibility for normalization,
/// root-containment checks, and safe resolution between the two domains.
/// </summary>
[SharedWithinAssembly]
internal readonly record struct LocalDirectory
{
    private string? RawValue { get; }

    private LocalDirectory(string rawValue)
    {
        RawValue = rawValue;
    }

    private string Value => RawValue ?? throw new InvalidOperationException("LocalDirectory is uninitialized.");

    /// <summary>
    /// Parses and normalizes an absolute local directory path.
    /// </summary>
    public static LocalDirectory Parse(string value)
    {
        if (!TryParse(value, out var directory))
            throw new FormatException($"Invalid local directory: '{value}'.");

        return directory;
    }

    /// <summary>
    /// Validates and normalizes an absolute local directory path.
    /// </summary>
    public static bool TryParse(string? value, out LocalDirectory directory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            directory = default;
            return false;
        }

        if (!Path.IsPathRooted(value))
        {
            directory = default;
            return false;
        }

        var fullPath = Path.GetFullPath(value);

        directory = new LocalDirectory(TrimTrailingDirectorySeparator(fullPath));
        return true;
    }

    /// <summary>
    /// Converts a contained host path into a repository-relative path.
    /// </summary>
    public bool TryGetRelativePath(string hostPath, out RelativePath relativePath)
    {
        ArgumentNullException.ThrowIfNull(hostPath);

        hostPath = Path.GetFullPath(hostPath);
        if (!IsContained(hostPath))
        {
            relativePath = default;
            return false;
        }

        var relative = Path.GetRelativePath(Value, hostPath);
        relative = relative == "." // hostPath is pointing to the root
            ? string.Empty 
            : relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

        return RelativePath.TryParse(relative, out relativePath);
    }

    public bool TryGetRelativePath(LocalDirectory hostPath, out RelativePath relativePath)
        => TryGetRelativePath(hostPath.Value, out relativePath);

    /// <summary>
    /// Resolves a repository-relative path under this local root and rejects root escape.
    /// </summary>
    public string Resolve(RelativePath path)
    {
        var candidate = Path.GetFullPath(Path.Combine(Value, path.ToString().Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(candidate))
            throw new InvalidOperationException($"Resolved path escapes local root: '{path}'.");

        return candidate;
    }

    public string Resolve(PathSegment path)
    {
        var candidate = Path.GetFullPath(Path.Combine(Value, path.ToString()));
        if (!IsContained(candidate))
            throw new InvalidOperationException($"Resolved path escapes local root: '{path}'.");

        return candidate;
    }

    public static LocalDirectory operator /(LocalDirectory directory, RelativePath path)   => new (directory.Resolve(path));
    public static LocalDirectory operator /(LocalDirectory directory, PathSegment segment) => new(directory.Resolve(segment));

    public override string ToString() => Value;

    private bool IsContained(string fullPath)
    {
        var comparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(fullPath, Value, comparison))
            return true;

        var pathRoot = Path.GetPathRoot(Value);
        var rootWithSeparator = Value.EndsWith(Path.DirectorySeparatorChar)
            || string.Equals(Value, pathRoot, comparison)
                ? Value
                : Value + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSeparator, comparison);
    }

    private static string TrimTrailingDirectorySeparator(string path)
    {
        if (path.Length <= Path.GetPathRoot(path)?.Length)
            return path;

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
