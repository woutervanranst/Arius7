namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Provides repository-path helpers for Arius pointer-file conventions.
///
/// These helpers keep pointer/binary path derivation attached to the typed path model
/// instead of scattering suffix manipulation across callers.
/// </summary>
public static class RelativePathExtensions
{
    /// <summary>The suffix used for pointer files stored alongside binary files.</summary>
    public const string PointerSuffix = ".pointer.arius";

    extension(RelativePath path)
    {
        /// <summary>Returns the pointer-file path for this repository-relative binary path.</summary>
        public RelativePath ToPointerFilePath() => RelativePath.Parse($"{path}{PointerSuffix}");

        /// <summary>Returns <c>true</c> when this repository-relative path points at a pointer file.</summary>
        public bool IsPointerFilePath() => path.ToString().EndsWith(PointerSuffix, StringComparison.Ordinal);

        /// <summary>Returns the binary-file path represented by a pointer-file path.</summary>
        public RelativePath ToBinaryFilePath()
        {
            var text = path.ToString();
            if (!text.EndsWith(PointerSuffix, StringComparison.Ordinal))
                throw new ArgumentException("Path must be a pointer file path.", nameof(path));

            return RelativePath.Parse(text[..^PointerSuffix.Length]);
        }
    }
}
