namespace Arius.Core.Models;

/// <summary>
/// A key file stored as plain JSON in keys/.
/// Contains the master key encrypted with the passphrase-derived key.
/// The file is NOT encrypted by the master key — it must be readable to bootstrap decryption.
/// </summary>
/// <param name="Salt">Hex-encoded 8-byte salt used for PBKDF2 and AES-CBC.</param>
/// <param name="Iterations">PBKDF2 iteration count (default 10 000).</param>
/// <param name="EncryptedMasterKey">Base64-encoded OpenSSL-format ciphertext: "Salted__" || salt || ciphertext.</param>
public sealed record KeyFile(string Salt, int Iterations, string EncryptedMasterKey);
