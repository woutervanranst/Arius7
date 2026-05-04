namespace Arius.Core.Shared.Paths;

public readonly record struct LocalRootPath
{
    private LocalRootPath(string value)
    {
        Value = value;
    }

    private string Value => field ?? throw new InvalidOperationException("LocalRootPath is uninitialized.");

    public static LocalRootPath Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Local root path must be absolute.", nameof(value));
        }

        var fullPath = Path.GetFullPath(value);

        return new LocalRootPath(Path.TrimEndingDirectorySeparator(fullPath));
    }

    public static bool TryParse(string? value, out LocalRootPath root)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            root = default;
            return false;
        }

        try
        {
            root = Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            root = default;
            return false;
        }
    }

    public bool Equals(LocalRootPath other) => Comparer.Equals(Value, other.Value);

    public override int GetHashCode() => Comparer.GetHashCode(Value);

    public override string ToString() => Value;

    private static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
