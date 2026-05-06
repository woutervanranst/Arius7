namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Provides typed file and directory operations for rooted local paths.
///
/// This is the main bridge from Arius' typed path model to host filesystem IO.
/// </summary>
public static class RootedPathExtensions
{
    extension(RootedPath path)
    {
        /// <summary>Returns <c>true</c> when this rooted path currently points at an existing file.</summary>
        public bool ExistsFile => File.Exists(path.FullPath);

        /// <summary>Returns <c>true</c> when this rooted path currently points at an existing directory.</summary>
        public bool ExistsDirectory => Directory.Exists(path.FullPath);

        /// <summary>Returns the file extension portion of this rooted path.</summary>
        public string Extension => Path.GetExtension(path.FullPath);

        /// <summary>Returns the file length when this rooted path points at an existing file.</summary>
        public long Length => path.ExistsFile
            ? new FileInfo(path.FullPath).Length
            : throw new FileNotFoundException($"File does not exist: {path.FullPath}", path.FullPath);

        /// <summary>Gets or sets the creation timestamp of the existing filesystem entry at this rooted path.</summary>
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

        /// <summary>Gets or sets the last-write timestamp of the existing filesystem entry at this rooted path.</summary>
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

        /// <summary>Opens the file at this rooted path for reading.</summary>
        public FileStream OpenRead() => File.OpenRead(path.FullPath);

        /// <summary>Opens the file at this rooted path with explicit file-stream options.</summary>
        public FileStream Open(
            FileMode mode,
            FileAccess access,
            FileShare share = FileShare.None,
            int bufferSize = 4096,
            bool useAsync = false)
            => new(path.FullPath, mode, access, share, bufferSize, useAsync);

        /// <summary>Opens the file at this rooted path for writing, creating or overwriting it.</summary>
        public FileStream OpenWrite() => path.Open(FileMode.Create, FileAccess.Write);

        /// <summary>Reads the entire file at this rooted path as text.</summary>
        public string ReadAllText() => File.ReadAllText(path.FullPath);

        /// <summary>Reads the entire file at this rooted path as bytes.</summary>
        public byte[] ReadAllBytes() => File.ReadAllBytes(path.FullPath);

        /// <summary>Reads the entire file at this rooted path as text asynchronously.</summary>
        public Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default) => File.ReadAllTextAsync(path.FullPath, cancellationToken);

        /// <summary>Reads the entire file at this rooted path as bytes asynchronously.</summary>
        public Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default) => File.ReadAllBytesAsync(path.FullPath, cancellationToken);

        /// <summary>Streams all lines from the file at this rooted path asynchronously.</summary>
        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default) => File.ReadLinesAsync(path.FullPath, cancellationToken);

        /// <summary>Writes the provided bytes to the file at this rooted path.</summary>
        public void WriteAllBytes(ReadOnlySpan<byte> bytes) => File.WriteAllBytes(path.FullPath, bytes);

        /// <summary>Writes the provided bytes to the file at this rooted path asynchronously.</summary>
        public Task WriteAllBytesAsync(byte[] bytes, CancellationToken cancellationToken = default) => File.WriteAllBytesAsync(path.FullPath, bytes, cancellationToken);

        /// <summary>Writes text to the file at this rooted path asynchronously.</summary>
        public Task WriteAllTextAsync(string content, CancellationToken cancellationToken = default) => File.WriteAllTextAsync(path.FullPath, content, cancellationToken);

        /// <summary>Writes lines to the file at this rooted path asynchronously.</summary>
        public Task WriteAllLinesAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default) => File.WriteAllLinesAsync(path.FullPath, lines, cancellationToken);

        /// <summary>Appends lines to the file at this rooted path asynchronously.</summary>
        public Task AppendAllLinesAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default) => File.AppendAllLinesAsync(path.FullPath, lines, cancellationToken);

        /// <summary>Enumerates files beneath the directory at this rooted path as typed rooted paths.</summary>
        public IEnumerable<RootedPath> EnumerateFiles(string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            foreach (var fullPath in Directory.EnumerateFiles(path.FullPath, searchPattern, searchOption))
            {
                yield return path.Root.GetRelativePath(fullPath).RootedAt(path.Root);
            }
        }

        /// <summary>Copies this file to another rooted path and preserves timestamps on the destination file.</summary>
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

        /// <summary>Deletes the file at this rooted path.</summary>
        public void DeleteFile() => File.Delete(path.FullPath);

        /// <summary>Creates the directory at this rooted path if it does not already exist.</summary>
        public void CreateDirectory() => Directory.CreateDirectory(path.FullPath);

        /// <summary>Deletes the directory at this rooted path.</summary>
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
