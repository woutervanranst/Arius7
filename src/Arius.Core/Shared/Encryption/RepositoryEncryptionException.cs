namespace Arius.Core.Shared.Encryption;

/// <summary>
/// Thrown when repository data could not be decrypted/decompressed because the passphrase is missing,
/// incorrect, or supplied for a repository that is not encrypted. Hosts map it to a user-facing hint;
/// the message is host-agnostic (no CLI flag names).
/// </summary>
public sealed class RepositoryEncryptionException : Exception
{
    /// <param name="passphraseProvided">
    /// Whether a passphrase was supplied for this run (<see cref="IEncryptionService.IsEncrypted"/>).
    /// <c>false</c> ⇒ the data looks encrypted but no passphrase was given; <c>true</c> ⇒ a passphrase
    /// was given but did not work (wrong passphrase, or the repository is not encrypted).
    /// </param>
    /// <param name="inner">The underlying decompression/decryption failure.</param>
    public RepositoryEncryptionException(bool passphraseProvided, Exception inner)
        : base(BuildMessage(passphraseProvided), inner)
    {
        PassphraseProvided = passphraseProvided;
    }

    public bool PassphraseProvided { get; }

    private static string BuildMessage(bool passphraseProvided) => passphraseProvided
        ? "The repository data could not be decrypted: the passphrase is incorrect, or the repository is not encrypted."
        : "The repository data could not be read: it appears to be encrypted and requires a passphrase.";
}
