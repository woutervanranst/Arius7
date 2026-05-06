namespace Arius.Core.Shared.FileSystem;

internal readonly record struct LocalDirectory(string? RawValue)
{
    private string Value => RawValue ?? throw new InvalidOperationException("LocalDirectory is uninitialized.");

    public static LocalDirectory Parse(string value)
    {
        if (!TryParse(value, out var directory))
            throw new FormatException($"Invalid local directory: '{value}'.");

        return directory;
    }

    public static bool TryParse(string? value, out LocalDirectory directory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            directory = default;
            return false;
        }

        if (!Path.IsPathRooted(value))
        {
            directory = default;
            return false;
        }

        var fullPath = Path.GetFullPath(value);

        directory = new LocalDirectory(TrimTrailingDirectorySeparator(fullPath));
        return true;
    }

    public bool TryGetRelativePath(string hostPath, out RelativePath relativePath)
    {
        ArgumentNullException.ThrowIfNull(hostPath);

        var fullPath = Path.GetFullPath(hostPath);
        if (!IsContained(fullPath))
        {
            relativePath = default;
            return false;
        }

        var relative = Path.GetRelativePath(Value, fullPath);
        return RelativePath.TryParse(relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/'), out relativePath);
    }

    public string Resolve(RelativePath path)
    {
        var candidate = Path.GetFullPath(Path.Combine(Value, path.ToString().Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(candidate))
            throw new InvalidOperationException($"Resolved path escapes local root: '{path}'.");

        return candidate;
    }

    public override string ToString() => Value;

    private bool IsContained(string fullPath)
    {
        if (string.Equals(fullPath, Value, StringComparison.Ordinal))
            return true;

        var rootWithSeparator = Value + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private static string TrimTrailingDirectorySeparator(string path)
    {
        if (path.Length <= Path.GetPathRoot(path)?.Length)
            return path;

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
