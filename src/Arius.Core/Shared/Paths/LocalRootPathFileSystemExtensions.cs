namespace Arius.Core.Shared.Paths;

public static class LocalRootPathFileSystemExtensions
{
    extension(LocalRootPath root)
    {
        public bool ExistsDirectory => Directory.Exists(root.ToString());

        public void CreateDirectory() => Directory.CreateDirectory(root.ToString());

        public void DeleteDirectory(bool recursive = false) => Directory.Delete(root.ToString(), recursive);
    }
}
