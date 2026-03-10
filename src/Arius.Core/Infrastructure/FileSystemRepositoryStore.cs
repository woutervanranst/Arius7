using System.Security.Cryptography;
using System.Text.Json;
using Arius.Core.Infrastructure.Crypto;
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

        await using (var configStream = File.Create(configPath))
        {
            await JsonSerializer.SerializeAsync(configStream, repoConfig, JsonDefaults.Options, cancellationToken);
        }

        // Create the first key file (generates and encrypts the master key)
        var keyManager = new KeyManager(repoPath);
        var (_, keyPath) = await keyManager.CreateFirstKeyAsync(passphrase, cancellationToken);

        return (repoConfig.RepoId, configPath, keyPath);
    }

    public async ValueTask<bool> ValidatePassphraseAsync(
        string repoPath,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        var keyManager = new KeyManager(repoPath);
        var masterKey  = await keyManager.TryUnlockAsync(passphrase, cancellationToken);
        return masterKey is not null;
    }

    /// <summary>
    /// Unlocks the master key for the given passphrase.
    /// Throws <see cref="InvalidOperationException"/> if the passphrase is wrong.
    /// </summary>
    private static async Task<byte[]> LoadMasterKeyAsync(
        string repoPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var keyManager = new KeyManager(repoPath);
        return await keyManager.TryUnlockAsync(passphrase, cancellationToken)
            ?? throw new InvalidOperationException("Invalid passphrase.");
    }

    public async ValueTask<(Snapshot Snapshot, int StoredFiles, int DeduplicatedFiles)> BackupAsync(
        string repoPath,
        string passphrase,
        IReadOnlyList<string> inputPaths,
        CancellationToken cancellationToken = default)
    {
        var masterKey  = await LoadMasterKeyAsync(repoPath, passphrase, cancellationToken);
        var blobRoot   = Path.Combine(repoPath, "blobs");
        var snapshotRoot = Path.Combine(repoPath, "snapshots");

        Directory.CreateDirectory(blobRoot);
        Directory.CreateDirectory(snapshotRoot);

        var files = ExpandInputFiles(inputPaths).ToList();
        var snapshotFiles = new List<BackupSnapshotFile>(files.Count);

        var stored      = 0;
        var deduplicated = 0;

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes    = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var blobHash = BlobHash.FromBytes(bytes, masterKey);
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
        string passphrase,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Validate passphrase before listing
        _ = await LoadMasterKeyAsync(repoPath, passphrase, cancellationToken);

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
        string passphrase,
        string snapshotId,
        string? includePattern,
        CancellationToken cancellationToken = default)
    {
        // Validate passphrase
        _ = await LoadMasterKeyAsync(repoPath, passphrase, cancellationToken);

        var snapshotRoot = Path.Combine(repoPath, "snapshots");

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
        string passphrase,
        BackupSnapshotFile file,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        var masterKey = await LoadMasterKeyAsync(repoPath, passphrase, cancellationToken);
        var blobRoot  = Path.Combine(repoPath, "blobs");
        var blobPath  = Path.Combine(blobRoot, file.BlobHash.Value + ".bin");

        if (!File.Exists(blobPath))
            throw new InvalidOperationException($"Blob not found for file: {file.Path} (hash: {file.BlobHash.Value})");

        var relativePath = GetRelativePath(file.Path);
        var outputPath   = Path.Combine(targetPath, relativePath.TrimStart(Path.DirectorySeparatorChar, '/'));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? targetPath);

        var bytes = await File.ReadAllBytesAsync(blobPath, cancellationToken);

        // Verify integrity: HMAC-SHA256(masterKey, plaintext) == stored blobHash
        var actualHash = BlobHash.FromBytes(bytes, masterKey);
        if (actualHash != file.BlobHash)
            throw new InvalidDataException(
                $"Integrity check failed for {file.Path}: expected {file.BlobHash.Value}, got {actualHash.Value}");

        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
    }

    private static string GetRelativePath(string absolutePath)
    {
        if (!Path.IsPathRooted(absolutePath))
            return absolutePath;

        var parts = absolutePath.Replace('\\', '/').Split('/');
        return parts.Length >= 2
            ? string.Join(Path.DirectorySeparatorChar.ToString(), parts[^2..])
            : parts[^1];
    }

    private sealed record BackupSnapshotDocument(Snapshot Snapshot, IReadOnlyList<BackupSnapshotFile> Files);
}
