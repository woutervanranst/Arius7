using System.Text.Json;
using Arius.Core.Models;

namespace Arius.Core.Infrastructure.Crypto;

/// <summary>
/// Manages key files stored in the repository's keys/ directory.
///
/// Each key file is a plain JSON document containing the master key encrypted with
/// a passphrase-derived key (PBKDF2-SHA256 → AES-256-CBC).  Multiple key files
/// (= multiple passphrases) are supported; they all encrypt the same master key.
/// </summary>
public sealed class KeyManager
{
    private readonly string _keysDir;

    public KeyManager(string repoPath)
    {
        _keysDir = Path.Combine(repoPath, "keys");
        Directory.CreateDirectory(_keysDir);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Create (called at repo init)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the first key file for a new repository.
    /// Generates a new random master key, encrypts it with <paramref name="passphrase"/>,
    /// writes it to keys/default.json, and returns the master key.
    /// </summary>
    public async Task<(byte[] MasterKey, string KeyPath)> CreateFirstKeyAsync(
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        var masterKey           = CryptoService.GenerateMasterKey();
        var keyPath             = await WriteKeyFileAsync("default", masterKey, passphrase, cancellationToken);
        return (masterKey, keyPath);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Unlock — try all key files with a given passphrase
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to decrypt each key file in keys/ with <paramref name="passphrase"/>.
    /// Returns the master key on success, or null if no key file matches.
    /// </summary>
    public async Task<byte[]?> TryUnlockAsync(
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        foreach (var keyFilePath in Directory.EnumerateFiles(_keysDir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyFile = await ReadKeyFileAsync(keyFilePath, cancellationToken);
            try
            {
                var masterKey = await CryptoService.DecryptMasterKeyAsync(
                    keyFile.EncryptedMasterKey,
                    passphrase,
                    keyFile.Iterations,
                    cancellationToken);
                return masterKey;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Wrong passphrase — try next key file
            }
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Multi-key operations
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a new key file.  Requires the existing passphrase to unlock the master key,
    /// then creates a new key file encrypted with <paramref name="newPassphrase"/>.
    /// </summary>
    /// <returns>The path of the newly created key file.</returns>
    public async Task<string> AddKeyAsync(
        string existingPassphrase,
        string newPassphrase,
        string keyId,
        CancellationToken cancellationToken = default)
    {
        var masterKey = await TryUnlockAsync(existingPassphrase, cancellationToken)
            ?? throw new InvalidOperationException("Existing passphrase is incorrect.");

        return await WriteKeyFileAsync(keyId, masterKey, newPassphrase, cancellationToken);
    }

    /// <summary>
    /// Removes the key file identified by <paramref name="keyId"/> (filename without extension).
    /// Throws if removing it would leave the repository with no keys.
    /// </summary>
    public void RemoveKey(string keyId)
    {
        var keyFilePath = Path.Combine(_keysDir, keyId + ".json");
        if (!File.Exists(keyFilePath))
            throw new InvalidOperationException($"Key '{keyId}' not found.");

        var allKeys = Directory.GetFiles(_keysDir, "*.json");
        if (allKeys.Length <= 1)
            throw new InvalidOperationException(
                "Cannot remove the last key — the repository would become inaccessible.");

        File.Delete(keyFilePath);
    }

    /// <summary>
    /// Changes the password associated with a key file.
    /// Creates a new key file under the same <paramref name="keyId"/>, replacing the old one.
    /// </summary>
    public async Task ChangePasswordAsync(
        string keyId,
        string oldPassphrase,
        string newPassphrase,
        CancellationToken cancellationToken = default)
    {
        var keyFilePath = Path.Combine(_keysDir, keyId + ".json");
        if (!File.Exists(keyFilePath))
            throw new InvalidOperationException($"Key '{keyId}' not found.");

        var keyFile = await ReadKeyFileAsync(keyFilePath, cancellationToken);
        var masterKey = await CryptoService.DecryptMasterKeyAsync(
            keyFile.EncryptedMasterKey,
            oldPassphrase,
            keyFile.Iterations,
            cancellationToken);

        // Overwrite with new passphrase
        await WriteKeyFileAsync(keyId, masterKey, newPassphrase, cancellationToken);
    }

    /// <summary>Returns the IDs (filenames without extension) of all key files.</summary>
    public IReadOnlyList<string> ListKeys() =>
        Directory.GetFiles(_keysDir, "*.json")
                 .Select(f => Path.GetFileNameWithoutExtension(f))
                 .OrderBy(id => id)
                 .ToList();

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> WriteKeyFileAsync(
        string keyId,
        byte[] masterKey,
        string passphrase,
        CancellationToken cancellationToken)
    {
        var encryptedMasterKey = await CryptoService.EncryptMasterKeyAsync(
            masterKey, passphrase, CryptoService.DefaultIterations, cancellationToken);

        // Extract the salt from the encrypted blob header so the key file carries it too
        // (used for display / debugging; actual decryption reads salt from the ciphertext header)
        var ciphertextBytes = Convert.FromBase64String(encryptedMasterKey);
        var saltHex         = Convert.ToHexString(ciphertextBytes[8..16]).ToLowerInvariant(); // skip "Salted__"

        var keyFile = new KeyFile(saltHex, CryptoService.DefaultIterations, encryptedMasterKey);
        var keyPath = Path.Combine(_keysDir, keyId + ".json");

        await using var stream = File.Create(keyPath);
        await JsonSerializer.SerializeAsync(stream, keyFile, JsonDefaults.Options, cancellationToken);
        return keyPath;
    }

    private static async Task<KeyFile> ReadKeyFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<KeyFile>(stream, JsonDefaults.Options, cancellationToken)
            ?? throw new InvalidOperationException($"Failed to read key file '{path}'.");
    }
}
