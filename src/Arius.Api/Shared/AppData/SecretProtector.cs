using Microsoft.AspNetCore.DataProtection;

namespace Arius.Api.AppData;

/// <summary>
/// Encrypts account keys and passphrases at rest in the app SQLite, using ASP.NET Core Data
/// Protection keyed by the server's key ring (persisted to the mounted volume). This is server-side
/// secret protection — distinct from Arius.Core's passphrase-based content encryption.
/// </summary>
public sealed class SecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("Arius.Api.Secrets.v1");

    /// <summary>Protects a plaintext secret, or returns <c>null</c> for null/empty input.</summary>
    public string? Protect(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? null : _protector.Protect(plaintext);

    /// <summary>Unprotects ciphertext, or returns <c>null</c> for null/empty input.</summary>
    public string? Unprotect(string? ciphertext)
        => string.IsNullOrEmpty(ciphertext) ? null : _protector.Unprotect(ciphertext);
}
