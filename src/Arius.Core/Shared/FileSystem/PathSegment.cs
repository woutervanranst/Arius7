namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Represents one validated relative-path segment.
/// It exists to make path composition explicit and safe, with responsibility for rejecting separators,
/// traversal markers, and other invalid single-segment values before they enter the filesystem domain.
/// </summary>
public readonly record struct PathSegment
{
    private string? RawValue { get; }

    public PathSegment(string? rawValue)
    {
        RawValue = rawValue;
    }

    private string Value => RawValue ?? throw new InvalidOperationException("PathSegment is uninitialized.");

    /// <summary>
    /// Parses a single path segment and throws when the value is invalid.
    /// </summary>
    public static PathSegment Parse(string value)
    {
        if (!TryParse(value, out var segment))
            throw new FormatException($"Invalid path segment: '{value}'.");

        return segment;
    }

    /// <summary>
    /// Validates a single path segment.
    /// </summary>
    public static bool TryParse(string? value, out PathSegment segment)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            segment = default;
            return false;
        }

        if (value.Contains('/') || value.Contains('\\') || value.Any(char.IsControl))
        {
            segment = default;
            return false;
        }

        segment = new PathSegment(value);
        return true;
    }

    public bool Contains(string value, StringComparison comparisonType) =>
        Value.Contains(value, comparisonType);

    public bool StartsWith(string value, StringComparison comparisonType) =>
        Value.StartsWith(value, comparisonType);

    public bool EndsWith(string value, StringComparison comparisonType) =>
        Value.EndsWith(value, comparisonType);

    public bool Equals(PathSegment other, StringComparison comparisonType) =>
        string.Equals(Value, other.Value, comparisonType);

    public int Compare(PathSegment other, StringComparer comparer) =>
        comparer.Compare(Value, other.Value);

    public PathSegment RemoveSuffix(string suffix, StringComparison comparisonType)
    {
        if (!Value.EndsWith(suffix, comparisonType))
            throw new InvalidOperationException($"Path segment '{Value}' does not end with suffix '{suffix}'.");

        return Parse(Value[..^suffix.Length]);
    }

    public override string ToString() => Value;
}
