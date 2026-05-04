namespace Arius.Core.Shared;

internal static class RepositoryRelativePath
{
    public static void ValidateCanonical(string path, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == 0)
        {
            if (allowEmpty)
            {
                return;
            }

            throw new ArgumentException("Path must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty or whitespace.");
        }

        if (Path.IsPathRooted(path)
            || (path.Length >= 3
                && char.IsAsciiLetter(path[0])
                && path[1] == ':'
                && path[2] == '/'))
        {
            throw new ArgumentException("Path must be repository-relative.");
        }

        if (path.Contains('\\')
            || path.Contains("//")
            || path.Contains('\r')
            || path.Contains('\n')
            || path.Contains('\0'))
        {
            throw new ArgumentException("Path must be canonical.");
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0
                || string.IsNullOrWhiteSpace(segment)
                || segment is "." or "..")
            {
                throw new ArgumentException("Path must be canonical.");
            }
        }
    }
}
