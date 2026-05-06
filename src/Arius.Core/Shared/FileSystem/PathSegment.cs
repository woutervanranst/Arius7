namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Represents one canonical repository path segment.
///
/// This type keeps repository-internal path composition out of raw strings so
/// Arius can enforce separator-free, dot-segment-free path identities.
/// </summary>
public readonly record struct PathSegment
{
    /// <summary>
    /// Compares path segments using ordinal case-insensitive semantics for host-facing scenarios
    /// where repository casing should be matched using the local filesystem rules.
    /// </summary>
    public static IEqualityComparer<PathSegment> OrdinalIgnoreCaseComparer { get; } = new OrdinalIgnoreCasePathSegmentComparer();

    private PathSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Path segment must not be empty or whitespace.", nameof(value));

        if (value is "." or ".."
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Path segment must be canonical.", nameof(value));
        }

        Value = value;
    }

    private string Value => field ?? throw new InvalidOperationException("PathSegment is uninitialized.");

    /// <summary>Parses a canonical repository path segment.</summary>
    public static PathSegment Parse(string value) => new(value);

    /// <summary>Attempts to parse a canonical repository path segment.</summary>
    public static bool TryParse(string? value, out PathSegment segment)
    {
        if (value is null)
        {
            segment = default;
            return false;
        }

        try
        {
            segment = Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            segment = default;
            return false;
        }
    }

    /// <summary>Compares two segments using the requested string comparison rules.</summary>
    public bool Equals(PathSegment other, StringComparison comparison) =>
        string.Equals(Value, other.Value, comparison);

    public override string ToString() => Value;

    private sealed class OrdinalIgnoreCasePathSegmentComparer : IEqualityComparer<PathSegment>
    {
        public bool Equals(PathSegment x, PathSegment y) => x.Equals(y, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(PathSegment obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value);
    }
}
