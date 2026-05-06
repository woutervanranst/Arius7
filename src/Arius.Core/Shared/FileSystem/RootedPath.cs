namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Represents a canonical repository-relative path rooted at a concrete local filesystem root.
///
/// This is Arius' typed bridge between repository path identity and an actual host path on disk.
/// </summary>
public readonly record struct RootedPath
{
    /// <summary>Creates a rooted local path from a local root and a canonical repository-relative path.</summary>
    public RootedPath(LocalRootPath root, RelativePath relativePath)
    {
        Root = root;
        RelativePath = relativePath;
    }

    /// <summary>The absolute local root directory.</summary>
    public LocalRootPath Root { get; }

    /// <summary>The canonical repository-relative path under <see cref="Root"/>.</summary>
    public RelativePath RelativePath { get; }

    /// <summary>The final repository path segment, or <c>null</c> when this rooted path points at the root itself.</summary>
    public PathSegment? Name => RelativePath.Name;

    /// <summary>Returns the host-platform full path for this rooted local path.</summary>
    public string FullPath => RelativePath.IsRoot
        ? Root.ToString()
        : Path.Combine(Root.ToString(), RelativePath.ToString().Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Compares rooted paths using host-filesystem path comparison rules.</summary>
    public bool Equals(RootedPath other) => Comparer.Equals(FullPath, other.FullPath);

    public override int GetHashCode() => Comparer.GetHashCode(FullPath);

    public override string ToString() => FullPath;

    private static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
