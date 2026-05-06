namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Represents a canonical repository-relative path.
///
/// This type gives Arius one stable, separator-normalized representation for
/// repository-internal paths before they are rooted at a local filesystem location.
/// </summary>
public readonly record struct RelativePath
{
    private RelativePath(string value)
    {
        Value = value;
    }

    /// <summary>Represents the repository root.</summary>
    public static RelativePath Root { get; } = new(string.Empty);

    private string Value => field ?? throw new InvalidOperationException("RelativePath is uninitialized.");

    /// <summary>Returns <c>true</c> when this path refers to the repository root.</summary>
    public bool IsRoot => Value.Length == 0;

    /// <summary>Returns the number of canonical path segments in this path.</summary>
    public int SegmentCount => IsRoot ? 0 : Value.Count(static c => c == '/') + 1;

    /// <summary>Returns this path split into canonical repository path segments.</summary>
    public IReadOnlyList<PathSegment> Segments => IsRoot
        ? []
        : Value.Split('/').Select(PathSegment.Parse).ToArray();

    /// <summary>Returns the final path segment, or <c>null</c> for the repository root.</summary>
    public PathSegment? Name => IsRoot ? null : PathSegment.Parse(Value[(Value.LastIndexOf('/') + 1)..]);

    /// <summary>Returns the parent repository-relative path, or <c>null</c> for the repository root.</summary>
    public RelativePath? Parent
    {
        get
        {
            if (IsRoot)
                return null;

            var lastSeparator = Value.LastIndexOf('/');
            return lastSeparator < 0 ? Root : new RelativePath(Value[..lastSeparator]);
        }
    }

    /// <summary>Parses a canonical repository-relative path.</summary>
    public static RelativePath Parse(string value, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            if (allowEmpty)
                return Root;

            throw new ArgumentException("Path must not be empty.", nameof(value));
        }

        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '/'))
        {
            throw new ArgumentException("Path must be repository-relative.", nameof(value));
        }

        if (value.Contains('\\', StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Path must be canonical.", nameof(value));
        }

        foreach (var segment in value.Split('/'))
            _ = PathSegment.Parse(segment);

        return new RelativePath(value);
    }

    /// <summary>Attempts to parse a canonical repository-relative path.</summary>
    public static bool TryParse(string? value, out RelativePath path, bool allowEmpty = false)
    {
        if (value is null)
        {
            path = default;
            return false;
        }

        try
        {
            path = Parse(value, allowEmpty);
            return true;
        }
        catch (ArgumentException)
        {
            path = default;
            return false;
        }
    }

    /// <summary>Parses a host-platform relative path and normalizes it into canonical repository form.</summary>
    public static RelativePath FromPlatformRelativePath(string value, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Parse(value.Replace('\\', '/'), allowEmpty);
    }

    /// <summary>Combines this repository-relative path with a local root to produce a rooted local path.</summary>
    public RootedPath RootedAt(LocalRootPath root) => new(root, this);

    /// <summary>Returns <c>true</c> when this path is equal to or contained under <paramref name="other"/>.</summary>
    public bool StartsWith(RelativePath other)
    {
        if (other.IsRoot)
            return true;

        if (IsRoot || other.Value.Length > Value.Length)
            return false;

        return Value.Length == other.Value.Length
            ? string.Equals(Value, other.Value, StringComparison.Ordinal)
            : Value.StartsWith(other.Value, StringComparison.Ordinal) && Value[other.Value.Length] == '/';
    }

    private RelativePath Append(PathSegment segment)
    {
        return IsRoot
            ? new RelativePath(segment.ToString())
            : new RelativePath($"{Value}/{segment}");
    }

    /// <summary>Appends one canonical segment to a repository-relative path.</summary>
    public static RelativePath operator /(RelativePath left, PathSegment right) => left.Append(right);

    public override string ToString() => Value;
}
