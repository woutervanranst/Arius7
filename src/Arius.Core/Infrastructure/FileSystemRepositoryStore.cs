using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arius.Core.Models;

namespace Arius.Core.Infrastructure;

public sealed class FileSystemRepositoryStore
{
    private const int RepoVersion = 1;

    public async ValueTask<(RepoId RepoId, string ConfigPath, string KeyPath)> InitAsync(
        string repoPath,
        string passphrase,
        long packSize,
        int chunkMin,
        int chunkAvg,
        int chunkMax,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(repoPath);
        Directory.CreateDirectory(Path.Combine(repoPath, "blobs"));
        Directory.CreateDirectory(Path.Combine(repoPath, "snapshots"));
        Directory.CreateDirectory(Path.Combine(repoPath, "keys"));

        var repoConfig = new RepoConfig(
            RepoId.New(),
            RepoVersion,
            RandomNumberGenerator.GetInt32(int.MaxValue),
            packSize,
            chunkMin,
            chunkAvg,
            chunkMax);

        var configPath = Path.Combine(repoPath, "config.json");
        var keyPath = Path.Combine(repoPath, "keys", "default.json");

        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var keyFile = new KeyFile(
            Convert.ToHexString(saltBytes).ToLowerInvariant(),
            10_000,
            Hash(passphrase));

        await using (var configStream = File.Create(configPath))
        {
            await JsonSerializer.SerializeAsync(configStream, repoConfig, JsonDefaults.Options, cancellationToken);
        }

        await using (var keyStream = File.Create(keyPath))
        {
            await JsonSerializer.SerializeAsync(keyStream, keyFile, JsonDefaults.Options, cancellationToken);
        }

        return (repoConfig.RepoId, configPath, keyPath);
    }

    public async ValueTask<bool> ValidatePassphraseAsync(string repoPath, string passphrase, CancellationToken cancellationToken = default)
    {
        var keyPath = Path.Combine(repoPath, "keys", "default.json");
        if (!File.Exists(keyPath))
        {
            return false;
        }

        await using var keyStream = File.OpenRead(keyPath);
        var keyFile = await JsonSerializer.DeserializeAsync<KeyFile>(keyStream, JsonDefaults.Options, cancellationToken);
        return keyFile is not null && keyFile.PassphraseHash == Hash(passphrase);
    }

    public async ValueTask<(Snapshot Snapshot, int StoredFiles, int DeduplicatedFiles)> BackupAsync(
        string repoPath,
        IReadOnlyList<string> inputPaths,
        CancellationToken cancellationToken = default)
    {
        var blobRoot = Path.Combine(repoPath, "blobs");
        var snapshotRoot = Path.Combine(repoPath, "snapshots");

        Directory.CreateDirectory(blobRoot);
        Directory.CreateDirectory(snapshotRoot);

        var files = ExpandInputFiles(inputPaths).ToList();
        var snapshotFiles = new List<BackupSnapshotFile>(files.Count);

        var stored = 0;
        var deduplicated = 0;

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var blobHash = BlobHash.FromBytes(bytes);
            var blobPath = Path.Combine(blobRoot, blobHash.Value + ".bin");

            if (!File.Exists(blobPath))
            {
                await File.WriteAllBytesAsync(blobPath, bytes, cancellationToken);
                stored++;
            }
            else
            {
                deduplicated++;
            }

            var info = new FileInfo(filePath);
            snapshotFiles.Add(new BackupSnapshotFile(info.FullName, blobHash, info.Length));
        }

        var snapshot = new Snapshot(
            SnapshotId.New(),
            DateTimeOffset.UtcNow,
            TreeHash.Empty,
            inputPaths,
            Environment.MachineName,
            Environment.UserName,
            Array.Empty<string>(),
            null);

        var snapshotPath = Path.Combine(snapshotRoot, snapshot.Id.Value + ".json");
        await using (var stream = File.Create(snapshotPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new BackupSnapshotDocument(snapshot, snapshotFiles),
                JsonDefaults.Options,
                cancellationToken);
        }

        return (snapshot, stored, deduplicated);
    }

    private static IEnumerable<string> ExpandInputFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                yield return Path.GetFullPath(path);
                continue;
            }

            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    yield return Path.GetFullPath(file);
                }
            }
        }
    }

    public async IAsyncEnumerable<Snapshot> ListSnapshotsAsync(
        string repoPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshotRoot = Path.Combine(repoPath, "snapshots");
        if (!Directory.Exists(snapshotRoot))
            yield break;

        foreach (var snapshotFile in Directory.EnumerateFiles(snapshotRoot, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(snapshotFile);
            var doc = await JsonSerializer.DeserializeAsync<BackupSnapshotDocument>(
                stream, JsonDefaults.Options, cancellationToken);
            if (doc is not null)
                yield return doc.Snapshot;
        }
    }

    public async ValueTask<(IReadOnlyList<BackupSnapshotFile> Files, long TotalBytes)> PlanRestoreAsync(
        string repoPath,
        string snapshotId,
        string? includePattern,
        CancellationToken cancellationToken = default)
    {
        var snapshotRoot = Path.Combine(repoPath, "snapshots");

        // Find snapshot by prefix or exact ID
        var snapshotFile = Directory
            .EnumerateFiles(snapshotRoot, "*.json")
            .FirstOrDefault(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.Equals(snapshotId, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(snapshotId, StringComparison.OrdinalIgnoreCase);
            });

        if (snapshotFile is null)
            throw new InvalidOperationException($"Snapshot '{snapshotId}' not found.");

        await using var stream = File.OpenRead(snapshotFile);
        var doc = await JsonSerializer.DeserializeAsync<BackupSnapshotDocument>(
            stream, JsonDefaults.Options, cancellationToken)
            ?? throw new InvalidOperationException("Failed to read snapshot.");

        var files = doc.Files;
        if (!string.IsNullOrEmpty(includePattern))
        {
            files = files
                .Where(f => f.Path.Contains(includePattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var totalBytes = files.Sum(f => f.Size);
        return (files, totalBytes);
    }

    public async ValueTask RestoreFileAsync(
        string repoPath,
        BackupSnapshotFile file,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        var blobRoot = Path.Combine(repoPath, "blobs");
        var blobPath = Path.Combine(blobRoot, file.BlobHash.Value + ".bin");

        if (!File.Exists(blobPath))
            throw new InvalidOperationException($"Blob not found for file: {file.Path} (hash: {file.BlobHash.Value})");

        // Compute relative path: strip common root prefix
        var relativePath = GetRelativePath(file.Path);
        var outputPath = Path.Combine(targetPath, relativePath.TrimStart(Path.DirectorySeparatorChar, '/'));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? targetPath);

        var bytes = await File.ReadAllBytesAsync(blobPath, cancellationToken);

        // Verify integrity before writing
        var actualHash = BlobHash.FromBytes(bytes);
        if (actualHash != file.BlobHash)
            throw new InvalidDataException(
                $"Integrity check failed for {file.Path}: expected {file.BlobHash.Value}, got {actualHash.Value}");

        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
    }

    private static string GetRelativePath(string absolutePath)
    {
        // Return just the file name portion for simple restore
        // If the path is rooted, keep the last 2 components for readability
        if (!Path.IsPathRooted(absolutePath))
            return absolutePath;

        var parts = absolutePath.Replace('\\', '/').Split('/');
        return parts.Length >= 2
            ? string.Join(Path.DirectorySeparatorChar.ToString(), parts[^2..])
            : parts[^1];
    }

    private static string Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private sealed record BackupSnapshotDocument(Snapshot Snapshot, IReadOnlyList<BackupSnapshotFile> Files);
}
