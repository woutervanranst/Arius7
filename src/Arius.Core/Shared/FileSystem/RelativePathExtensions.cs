namespace Arius.Core.Shared.Paths;

public static class RelativePathExtensions
{
    public const string PointerSuffix = ".pointer.arius";

    extension(RelativePath path)
    {
        public RelativePath ToPointerFilePath() => RelativePath.Parse($"{path}{PointerSuffix}");

        public bool IsPointerFilePath() => path.ToString().EndsWith(PointerSuffix, StringComparison.Ordinal);

        public RelativePath ToBinaryFilePath()
        {
            var text = path.ToString();
            if (!text.EndsWith(PointerSuffix, StringComparison.Ordinal))
                throw new ArgumentException("Path must be a pointer file path.", nameof(path));

            return RelativePath.Parse(text[..^PointerSuffix.Length]);
        }
    }
}
