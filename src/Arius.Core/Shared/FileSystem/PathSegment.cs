namespace Arius.Core.Shared.FileSystem;

internal readonly record struct PathSegment(string? RawValue)
{
    private string Value => RawValue ?? throw new InvalidOperationException("PathSegment is uninitialized.");

    public static PathSegment Parse(string value)
    {
        if (!TryParse(value, out var segment))
            throw new FormatException($"Invalid path segment: '{value}'.");

        return segment;
    }

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
