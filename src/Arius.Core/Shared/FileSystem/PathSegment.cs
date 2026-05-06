namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Represents one validated relative-path segment.
/// It exists to make path composition explicit and safe, with responsibility for rejecting separators,
/// traversal markers, and other invalid single-segment values before they enter the filesystem domain.
/// </summary>
internal readonly record struct PathSegment(string? RawValue)
{
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
        if (string.IsNullOrEmpty(value) || value is "." or "..")
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

    public override string ToString() => Value;
}
