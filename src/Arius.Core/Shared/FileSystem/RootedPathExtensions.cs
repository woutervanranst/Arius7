namespace Arius.Core.Shared.FileSystem;

public static class RootedPathExtensions
{
    extension(RootedPath path)
    {
        public bool ExistsFile => File.Exists(path.FullPath);

        public bool ExistsDirectory => Directory.Exists(path.FullPath);

        public string Extension => Path.GetExtension(path.FullPath);

        public long Length => path.ExistsFile
            ? new FileInfo(path.FullPath).Length
            : throw new FileNotFoundException($"File does not exist: {path.FullPath}", path.FullPath);

        public DateTime CreationTimeUtc
        {
            get => path.ResolveExistingEntry(
                static fullPath => File.GetCreationTimeUtc(fullPath),
                static fullPath => Directory.GetCreationTimeUtc(fullPath));
            set => path.ResolveExistingEntry(
                fullPath =>
                {
                    File.SetCreationTimeUtc(fullPath, value);
                    return 0;
                },
                fullPath =>
                {
                    Directory.SetCreationTimeUtc(fullPath, value);
                    return 0;
                });
        }

        public DateTime LastWriteTimeUtc
        {
            get => path.ResolveExistingEntry(
                static fullPath => File.GetLastWriteTimeUtc(fullPath),
                static fullPath => Directory.GetLastWriteTimeUtc(fullPath));
            set => path.ResolveExistingEntry(
                fullPath =>
                {
                    File.SetLastWriteTimeUtc(fullPath, value);
                    return 0;
                },
                fullPath =>
                {
                    Directory.SetLastWriteTimeUtc(fullPath, value);
                    return 0;
                });
        }

        public FileStream OpenRead() => File.OpenRead(path.FullPath);

        public FileStream Open(
            FileMode mode,
            FileAccess access,
            FileShare share = FileShare.None,
            int bufferSize = 4096,
            bool useAsync = false)
            => new(path.FullPath, mode, access, share, bufferSize, useAsync);

        public FileStream OpenWrite() => path.Open(FileMode.Create, FileAccess.Write);

        public string ReadAllText() => File.ReadAllText(path.FullPath);

        public byte[] ReadAllBytes() => File.ReadAllBytes(path.FullPath);

        public Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default) => File.ReadAllTextAsync(path.FullPath, cancellationToken);

        public Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default) => File.ReadAllBytesAsync(path.FullPath, cancellationToken);

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default) => File.ReadLinesAsync(path.FullPath, cancellationToken);

        public void WriteAllBytes(ReadOnlySpan<byte> bytes) => File.WriteAllBytes(path.FullPath, bytes);

        public Task WriteAllBytesAsync(byte[] bytes, CancellationToken cancellationToken = default) => File.WriteAllBytesAsync(path.FullPath, bytes, cancellationToken);

        public Task WriteAllTextAsync(string content, CancellationToken cancellationToken = default) => File.WriteAllTextAsync(path.FullPath, content, cancellationToken);

        public Task WriteAllLinesAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default) => File.WriteAllLinesAsync(path.FullPath, lines, cancellationToken);

        public Task AppendAllLinesAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default) => File.AppendAllLinesAsync(path.FullPath, lines, cancellationToken);

        public IEnumerable<RootedPath> EnumerateFiles(string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            foreach (var fullPath in Directory.EnumerateFiles(path.FullPath, searchPattern, searchOption))
            {
                yield return path.Root.GetRelativePath(fullPath).RootedAt(path.Root);
            }
        }

        public IEnumerable<RootedPath> EnumerateDirectories(string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            foreach (var fullPath in Directory.EnumerateDirectories(path.FullPath, searchPattern, searchOption))
            {
                yield return path.Root.GetRelativePath(fullPath).RootedAt(path.Root);
            }
        }

        internal IEnumerable<RootedFileEntry> EnumerateFileEntries(string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            foreach (var fullPath in Directory.EnumerateFiles(path.FullPath, searchPattern, searchOption))
            {
                var fileInfo = new FileInfo(fullPath);
                var rootedPath = path.Root.GetRelativePath(fullPath).RootedAt(path.Root);
                yield return new RootedFileEntry(
                    rootedPath,
                    rootedPath.Name ?? throw new InvalidOperationException($"Enumerated file path '{rootedPath}' did not have a name."),
                    fileInfo.Length,
                    fileInfo.CreationTimeUtc,
                    fileInfo.LastWriteTimeUtc);
            }
        }

        public async ValueTask CopyToAsync(RootedPath destination, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            if (!overwrite && destination.ExistsFile)
                throw new IOException($"Destination file already exists: {destination.FullPath}");

            var destinationParent = destination.RelativePath.Parent;
            if (destinationParent is not null)
                (destination.Root / destinationParent.Value).CreateDirectory();

            await using (var source = path.OpenRead())
            await using (var target = destination.Open(overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            destination.CreationTimeUtc = path.CreationTimeUtc;
            destination.LastWriteTimeUtc = path.LastWriteTimeUtc;
        }

        public void DeleteFile() => File.Delete(path.FullPath);

        public void CreateDirectory() => Directory.CreateDirectory(path.FullPath);

        public void DeleteDirectory(bool recursive = false) => Directory.Delete(path.FullPath, recursive);

        private TResult ResolveExistingEntry<TResult>(Func<string, TResult> forFile, Func<string, TResult> forDirectory)
        {
            if (path.ExistsFile)
                return forFile(path.FullPath);

            if (path.ExistsDirectory)
                return forDirectory(path.FullPath);

            throw new FileNotFoundException($"Filesystem entry does not exist: {path.FullPath}", path.FullPath);
        }
    }
}
