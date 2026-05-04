namespace Arius.Core.Shared.Paths;

public readonly record struct PathSegment
{
    private readonly string? _value;

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

        _value = value;
    }

    private string Value => _value ?? throw new InvalidOperationException("PathSegment is uninitialized.");

    public static PathSegment Parse(string value) => new(value);

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

    public override string ToString() => Value;
}
