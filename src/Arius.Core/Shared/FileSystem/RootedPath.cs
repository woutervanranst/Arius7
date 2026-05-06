namespace Arius.Core.Shared.Paths;

public readonly record struct RootedPath
{
    public RootedPath(LocalRootPath root, RelativePath relativePath)
    {
        Root = root;
        RelativePath = relativePath;
    }

    public LocalRootPath Root { get; }

    public RelativePath RelativePath { get; }

    public PathSegment? Name => RelativePath.Name;

    public string FullPath => RelativePath.IsRoot
        ? Root.ToString()
        : Path.Combine(Root.ToString(), RelativePath.ToString().Replace('/', Path.DirectorySeparatorChar));

    public bool Equals(RootedPath other) => Comparer.Equals(FullPath, other.FullPath);

    public override int GetHashCode() => Comparer.GetHashCode(FullPath);

    public override string ToString() => FullPath;

    private static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
