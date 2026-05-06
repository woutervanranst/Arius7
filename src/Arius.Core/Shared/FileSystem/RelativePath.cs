namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Represents a validated repository-relative path using Arius's canonical forward-slash format.
/// It exists so Arius.Core can reason about repository paths as domain values instead of ad hoc strings,
/// with responsibility for validation, composition, and segment-aware path operations.
/// </summary>
internal readonly record struct RelativePath
{
    private string? RawValue { get; }

    public RelativePath(string? rawValue)
    {
        RawValue = rawValue;
    }

    /// <summary>
    /// Gets the repository root path.
    /// </summary>
    public static RelativePath Root => new(string.Empty);

    private string Value => RawValue ?? throw new InvalidOperationException("RelativePath is uninitialized.");

    /// <summary>
    /// Gets the final segment of a non-root relative path.
    /// </summary>
    public PathSegment Name =>
        Value.Length == 0
            ? throw new InvalidOperationException("Root path does not have a name.")
            : PathSegment.Parse(Value[(Value.LastIndexOf('/') + 1)..]);

    /// <summary>
    /// Gets the parent path, or <c>null</c> when this instance is the root.
    /// </summary>
    public RelativePath? Parent
    {
        get
        {
            if (Value.Length == 0)
                return null;

            var separatorIndex = Value.LastIndexOf('/');
            return separatorIndex < 0 ? Root : new RelativePath(Value[..separatorIndex]);
        }
    }

    /// <summary>
    /// Gets the validated path segments that make up this relative path.
    /// </summary>
    public IEnumerable<PathSegment> Segments =>
        Value.Length == 0
            ? []
            : Value.Split('/').Select(PathSegment.Parse);

    /// <summary>
    /// Parses a canonical repository-relative path and throws when the value is invalid.
    /// </summary>
    public static RelativePath Parse(string value)
    {
        if (!TryParse(value, out var path))
            throw new FormatException($"Invalid relative path: '{value}'.");

        return path;
    }

    /// <summary>
    /// Validates a canonical repository-relative path.
    /// </summary>
    public static bool TryParse(string? value, out RelativePath path)
    {
        if (value is null)
        {
            path = default;
            return false;
        }

        if (value.Length == 0)
        {
            path = Root;
            return true;
        }

        if (value.StartsWith('/') || value.StartsWith('\\') || value.EndsWith('/') || value.EndsWith('\\'))
        {
            path = default;
            return false;
        }

        if (value.Contains('\\') || value.Contains("//", StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            path = default;
            return false;
        }

        foreach (var rawSegment in value.Split('/'))
        {
            if (!PathSegment.TryParse(rawSegment, out _))
            {
                path = default;
                return false;
            }
        }

        path = new RelativePath(value);
        return true;
    }

    /// <summary>
    /// Converts an OS-relative path into Arius's canonical repository-relative format.
    /// </summary>
    public static RelativePath FromPlatformRelativePath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Parse(value.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/'));
    }

    /// <summary>
    /// Determines whether this path starts with the provided path on segment boundaries.
    /// </summary>
    public bool StartsWith(RelativePath prefix)
    {
        if (prefix.Value.Length == 0)
            return true;

        if (Value.Length < prefix.Value.Length)
            return false;

        if (!Value.StartsWith(prefix.Value, StringComparison.Ordinal))
            return false;

        return Value.Length == prefix.Value.Length || Value[prefix.Value.Length] == '/';
    }

    public static RelativePath operator /(RelativePath path, PathSegment segment) =>
        path.Value.Length == 0 ? new RelativePath(segment.ToString()) : new RelativePath($"{path.Value}/{segment}");

    public static RelativePath operator /(RelativePath path, string segment) => path / PathSegment.Parse(segment);

    public override string ToString() => Value;
}
