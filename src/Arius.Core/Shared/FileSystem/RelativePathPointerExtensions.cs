namespace Arius.Core.Shared.FileSystem;

internal static class RelativePathPointerExtensions
{
    internal const string PointerSuffix = ".pointer.arius";

    public static bool IsPointerPath(this RelativePath path) =>
        path.ToString().EndsWith(PointerSuffix, StringComparison.Ordinal);

    public static RelativePath ToPointerPath(this RelativePath path)
    {
        var value = path.ToString();
        if (value.Length == 0)
            throw new InvalidOperationException("Root path cannot be converted to a pointer path.");

        return RelativePath.Parse(value + PointerSuffix);
    }

    public static RelativePath ToBinaryPath(this RelativePath path)
    {
        var value = path.ToString();
        if (!value.EndsWith(PointerSuffix, StringComparison.Ordinal))
            throw new InvalidOperationException("Path is not a pointer path.");

        var binaryPath = value[..^PointerSuffix.Length];
        return RelativePath.Parse(binaryPath);
    }
}
