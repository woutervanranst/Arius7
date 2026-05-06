namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Represents an absolute local root directory.
///
/// Arius uses this type to keep local archive and restore roots distinct from
/// repository-relative paths and to centralize containment checks at the root boundary.
/// </summary>
public readonly record struct LocalRootPath
{
    private LocalRootPath(string value)
    {
        Value = value;
    }

    private string Value => field ?? throw new InvalidOperationException("LocalRootPath is uninitialized.");

    /// <summary>Parses an absolute local root path and normalizes it to a full path without a trailing separator.</summary>
    public static LocalRootPath Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Local root path must be absolute.", nameof(value));
        }

        var fullPath = Path.GetFullPath(value);

        return new LocalRootPath(Path.TrimEndingDirectorySeparator(fullPath));
    }

    /// <summary>Attempts to parse an absolute local root path.</summary>
    public static bool TryParse(string? value, out LocalRootPath root)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            root = default;
            return false;
        }

        try
        {
            root = Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            root = default;
            return false;
        }
    }

    /// <summary>Converts an absolute local path under this root into a canonical repository-relative path.</summary>
    public RelativePath GetRelativePath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("Full path must be absolute.", nameof(fullPath));
        }

        var absolute = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(Value, absolute);
        if (relative == ".")
        {
            return RelativePath.Root;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ArgumentOutOfRangeException(nameof(fullPath), "Path must stay within the local root.");
        }

        return RelativePath.FromPlatformRelativePath(relative, allowEmpty: true);
    }

    /// <summary>Attempts to convert an absolute local path under this root into a repository-relative path.</summary>
    public bool TryGetRelativePath(string fullPath, out RelativePath path)
    {
        try
        {
            path = GetRelativePath(fullPath);
            return true;
        }
        catch (ArgumentException)
        {
            path = default;
            return false;
        }
    }

    /// <summary>Returns the parent local root, or <c>null</c> when this root has no parent.</summary>
    public LocalRootPath? Parent
    {
        get
        {
            var parent = Path.GetDirectoryName(Value);
            return string.IsNullOrEmpty(parent) ? null : Parse(parent);
        }
    }

    /// <summary>Returns a child local root formed by appending one canonical directory segment.</summary>
    public LocalRootPath GetSubdirectoryRoot(PathSegment child) => Parse(Path.Combine(Value, child.ToString()));

    /// <summary>Compares local roots using host-filesystem path comparison rules.</summary>
    public bool Equals(LocalRootPath other) => Comparer.Equals(Value, other.Value);

    public override int GetHashCode() => Comparer.GetHashCode(Value);

    /// <summary>Combines a local root with a repository-relative path.</summary>
    public static RootedPath operator /(LocalRootPath left, RelativePath right) => new(left, right);

    public override string ToString() => Value;

    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
