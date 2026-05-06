namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Provides typed directory operations for local root paths.
///
/// This keeps root-directory filesystem access behind Arius' typed local-root boundary.
/// </summary>
public static class LocalRootPathExtensions
{
    extension(LocalRootPath root)
    {
        /// <summary>Returns <c>true</c> when the local root directory currently exists on disk.</summary>
        public bool ExistsDirectory => Directory.Exists(root.ToString());

        /// <summary>Creates the local root directory if it does not already exist.</summary>
        public void CreateDirectory() => Directory.CreateDirectory(root.ToString());

        /// <summary>Deletes the local root directory.</summary>
        public void DeleteDirectory(bool recursive = false) => Directory.Delete(root.ToString(), recursive);
    }
}
