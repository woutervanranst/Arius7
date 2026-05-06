namespace Arius.Core.Shared.FileSystem;

internal readonly record struct RelativePath(string? RawValue)
{
    public static RelativePath Root => new(string.Empty);

    private string Value => RawValue ?? throw new InvalidOperationException("RelativePath is uninitialized.");

    public PathSegment Name =>
        Value.Length == 0
            ? throw new InvalidOperationException("Root path does not have a name.")
            : PathSegment.Parse(Value[(Value.LastIndexOf('/') + 1)..]);

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

    public IEnumerable<PathSegment> Segments =>
        Value.Length == 0
            ? []
            : Value.Split('/').Select(PathSegment.Parse);

    public static RelativePath Parse(string value)
    {
        if (!TryParse(value, out var path))
            throw new FormatException($"Invalid relative path: '{value}'.");

        return path;
    }

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

    public static RelativePath FromPlatformRelativePath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Parse(value.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/'));
    }

    public bool StartsWith(RelativePath prefix)
    {
        if (prefix.Value.Length == 0)
            return true;

        var currentSegments = Segments.ToArray();
        var prefixSegments = prefix.Segments.ToArray();
        if (prefixSegments.Length > currentSegments.Length)
            return false;

        for (var i = 0; i < prefixSegments.Length; i++)
        {
            if (!string.Equals(currentSegments[i].ToString(), prefixSegments[i].ToString(), StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public static RelativePath operator /(RelativePath path, PathSegment segment) =>
        path.Value.Length == 0 ? new RelativePath(segment.ToString()) : new RelativePath($"{path.Value}/{segment}");

    public static RelativePath operator /(RelativePath path, string segment) => path / PathSegment.Parse(segment);

    public override string ToString() => Value;
}
