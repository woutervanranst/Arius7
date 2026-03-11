using System.Security.Cryptography;

namespace Arius.Core.Infrastructure.Crypto;

/// <summary>
/// AES-256-CBC encryption/decryption compatible with OpenSSL's "Salted__" format.
///
/// Format: "Salted__" (8 bytes) || salt (8 bytes) || ciphertext
///
/// Key derivation: PBKDF2-SHA256(passphrase_or_key_bytes, salt, iterations) → 32-byte key + 16-byte IV.
///
/// This means any blob produced by this service can be decrypted on the command line:
///   openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -md sha256 -pass pass:PASSPHRASE
/// </summary>
public static class CryptoService
{
    private const string SaltedMagic = "Salted__";
    private const int SaltSize       = 8;
    private const int KeySize        = 32; // 256-bit
    private const int IvSize         = 16; // 128-bit block
    public  const int DefaultIterations = 10_000;

    // ─────────────────────────────────────────────────────────────────────────
    // Master key
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Generates a new random 32-byte master key.</summary>
    public static byte[] GenerateMasterKey() => RandomNumberGenerator.GetBytes(KeySize);

    // ─────────────────────────────────────────────────────────────────────────
    // Stream-based encrypt / decrypt (passphrase overload)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> using AES-256-CBC with a key derived from
    /// <paramref name="passphrase"/> via PBKDF2-SHA256.  The output stream starts with
    /// the OpenSSL magic + salt so the result is directly decryptable by openssl enc.
    /// </summary>
    public static async Task EncryptAsync(
        Stream plaintext,
        Stream ciphertext,
        string passphrase,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        DeriveKeyIv(passphrase, salt, iterations, out var key, out var iv);
        await EncryptCoreAsync(plaintext, ciphertext, key, iv, salt, cancellationToken);
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> using AES-256-CBC with a key derived from the
    /// raw <paramref name="keyBytes"/> (e.g. a master key) via PBKDF2-SHA256.
    /// </summary>
    public static async Task EncryptAsync(
        Stream plaintext,
        Stream ciphertext,
        byte[] keyBytes,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        DeriveKeyIv(keyBytes, salt, iterations, out var key, out var iv);
        await EncryptCoreAsync(plaintext, ciphertext, key, iv, salt, cancellationToken);
    }

    /// <summary>
    /// Decrypts a stream produced by <see cref="EncryptAsync(Stream,Stream,string,int,CancellationToken)"/>.
    /// </summary>
    public static async Task DecryptAsync(
        Stream ciphertext,
        Stream plaintext,
        string passphrase,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        var salt = await ReadSaltedHeaderAsync(ciphertext, cancellationToken);
        DeriveKeyIv(passphrase, salt, iterations, out var key, out var iv);
        await DecryptCoreAsync(ciphertext, plaintext, key, iv, cancellationToken);
    }

    /// <summary>
    /// Decrypts a stream produced by <see cref="EncryptAsync(Stream,Stream,byte[],int,CancellationToken)"/>.
    /// </summary>
    public static async Task DecryptAsync(
        Stream ciphertext,
        Stream plaintext,
        byte[] keyBytes,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        var salt = await ReadSaltedHeaderAsync(ciphertext, cancellationToken);
        DeriveKeyIv(keyBytes, salt, iterations, out var key, out var iv);
        await DecryptCoreAsync(ciphertext, plaintext, key, iv, cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Convenience byte[] overloads (used by key file serialization)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Encrypts bytes with a passphrase; returns the full OpenSSL-format blob.</summary>
    public static async Task<byte[]> EncryptBytesAsync(
        byte[] plaintext,
        string passphrase,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        using var plaintextStream  = new MemoryStream(plaintext);
        using var ciphertextStream = new MemoryStream();
        await EncryptAsync(plaintextStream, ciphertextStream, passphrase, iterations, cancellationToken);
        return ciphertextStream.ToArray();
    }

    /// <summary>Encrypts bytes with a raw key (e.g. master key); returns the full OpenSSL-format blob.</summary>
    public static async Task<byte[]> EncryptBytesAsync(
        byte[] plaintext,
        byte[] keyBytes,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        using var plaintextStream  = new MemoryStream(plaintext);
        using var ciphertextStream = new MemoryStream();
        await EncryptAsync(plaintextStream, ciphertextStream, keyBytes, iterations, cancellationToken);
        return ciphertextStream.ToArray();
    }

    /// <summary>Decrypts an OpenSSL-format blob with a passphrase; returns plaintext bytes.</summary>
    public static async Task<byte[]> DecryptBytesAsync(
        byte[] ciphertext,
        string passphrase,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        using var ciphertextStream = new MemoryStream(ciphertext);
        using var plaintextStream  = new MemoryStream();
        await DecryptAsync(ciphertextStream, plaintextStream, passphrase, iterations, cancellationToken);
        return plaintextStream.ToArray();
    }

    /// <summary>Decrypts an OpenSSL-format blob with a raw key (e.g. master key); returns plaintext bytes.</summary>
    public static async Task<byte[]> DecryptBytesAsync(
        byte[] ciphertext,
        byte[] keyBytes,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        using var ciphertextStream = new MemoryStream(ciphertext);
        using var plaintextStream  = new MemoryStream();
        await DecryptAsync(ciphertextStream, plaintextStream, keyBytes, iterations, cancellationToken);
        return plaintextStream.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Key file helpers — two-level architecture
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts <paramref name="masterKey"/> with <paramref name="passphrase"/> and returns
    /// the OpenSSL-format blob (base64-encoded for JSON storage).
    /// </summary>
    public static async Task<string> EncryptMasterKeyAsync(
        byte[] masterKey,
        string passphrase,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        var encrypted = await EncryptBytesAsync(masterKey, passphrase, iterations, cancellationToken);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts the <paramref name="encryptedMasterKeyBase64"/> (as stored in a key file) and
    /// returns the 32-byte master key.
    /// </summary>
    public static async Task<byte[]> DecryptMasterKeyAsync(
        string encryptedMasterKeyBase64,
        string passphrase,
        int iterations = DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        var ciphertext = Convert.FromBase64String(encryptedMasterKeyBase64);
        var masterKey  = await DecryptBytesAsync(ciphertext, passphrase, iterations, cancellationToken);

        if (masterKey.Length != KeySize)
            throw new CryptographicException(
                $"Decrypted master key has unexpected length {masterKey.Length} (expected {KeySize}).");

        return masterKey;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task EncryptCoreAsync(
        Stream plaintext,
        Stream ciphertext,
        byte[] key,
        byte[] iv,
        byte[] salt,
        CancellationToken cancellationToken)
    {
        // Write OpenSSL header: magic + salt
        var magic = System.Text.Encoding.ASCII.GetBytes(SaltedMagic);
        await ciphertext.WriteAsync(magic, cancellationToken);
        await ciphertext.WriteAsync(salt,  cancellationToken);

        using var aes = Aes.Create();
        aes.KeySize   = 256;
        aes.BlockSize = 128;
        aes.Mode      = CipherMode.CBC;
        aes.Padding   = PaddingMode.PKCS7;
        aes.Key       = key;
        aes.IV        = iv;

        using var encryptor     = aes.CreateEncryptor();
        await using var cryptoStream  = new CryptoStream(ciphertext, encryptor, CryptoStreamMode.Write, leaveOpen: true);
        await plaintext.CopyToAsync(cryptoStream, cancellationToken);
        await cryptoStream.FlushFinalBlockAsync(cancellationToken);
    }

    private static async Task DecryptCoreAsync(
        Stream ciphertext,
        Stream plaintext,
        byte[] key,
        byte[] iv,
        CancellationToken cancellationToken)
    {
        using var aes = Aes.Create();
        aes.KeySize   = 256;
        aes.BlockSize = 128;
        aes.Mode      = CipherMode.CBC;
        aes.Padding   = PaddingMode.PKCS7;
        aes.Key       = key;
        aes.IV        = iv;

        using var decryptor    = aes.CreateDecryptor();
        await using var cryptoStream = new CryptoStream(ciphertext, decryptor, CryptoStreamMode.Read, leaveOpen: true);
        await cryptoStream.CopyToAsync(plaintext, cancellationToken);
    }

    private static async Task<byte[]> ReadSaltedHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[SaltedMagic.Length + SaltSize];
        var read   = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);

        if (read < header.Length)
            throw new CryptographicException("Input stream is too short to contain the OpenSSL Salted__ header.");

        var magic = System.Text.Encoding.ASCII.GetString(header, 0, SaltedMagic.Length);
        if (magic != SaltedMagic)
            throw new CryptographicException(
                $"Invalid OpenSSL header: expected '{SaltedMagic}', got '{magic}'.");

        var salt = new byte[SaltSize];
        Array.Copy(header, SaltedMagic.Length, salt, 0, SaltSize);
        return salt;
    }

    /// <summary>Derives key + IV from a passphrase using PBKDF2-SHA256.</summary>
    private static void DeriveKeyIv(
        string passphrase,
        byte[] salt,
        int iterations,
        out byte[] key,
        out byte[] iv)
    {
        var passphraseBytes = System.Text.Encoding.UTF8.GetBytes(passphrase);
        DeriveKeyIv(passphraseBytes, salt, iterations, out key, out iv);
    }

    /// <summary>Derives key + IV from raw key bytes using PBKDF2-SHA256.</summary>
    private static void DeriveKeyIv(
        byte[] keyMaterial,
        byte[] salt,
        int iterations,
        out byte[] key,
        out byte[] iv)
    {
        // PBKDF2 producing KeySize + IvSize = 48 bytes total
        var derived = Rfc2898DeriveBytes.Pbkdf2(keyMaterial, salt, iterations, HashAlgorithmName.SHA256, KeySize + IvSize);
        key = derived[..KeySize];
        iv  = derived[KeySize..];
    }
}
