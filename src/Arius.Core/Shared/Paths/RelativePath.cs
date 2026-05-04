namespace Arius.Core.Shared.Paths;

public readonly record struct RelativePath
{
    private RelativePath(string value)
    {
        Value = value;
    }

    public static RelativePath Root { get; } = new(string.Empty);

    private string Value => field ?? throw new InvalidOperationException("RelativePath is uninitialized.");

    public bool IsRoot => Value.Length == 0;

    public int SegmentCount => IsRoot ? 0 : Value.Count(static c => c == '/') + 1;

    public IReadOnlyList<PathSegment> Segments => IsRoot
        ? []
        : Value.Split('/').Select(PathSegment.Parse).ToArray();

    public PathSegment? Name => IsRoot ? null : PathSegment.Parse(Value[(Value.LastIndexOf('/') + 1)..]);

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

    public static RelativePath FromPlatformRelativePath(string value, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Parse(value.Replace('\\', '/'), allowEmpty);
    }

    public string ToPlatformPath(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return Path.Combine(rootDirectory, Value.Replace('/', Path.DirectorySeparatorChar));
    }


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

    public static RelativePath operator /(RelativePath left, PathSegment right) => left.Append(right);

    public override string ToString() => Value;
}
