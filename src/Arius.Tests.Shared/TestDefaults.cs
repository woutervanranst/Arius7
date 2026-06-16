using Arius.Core.Shared.Encryption;

namespace Arius.Tests.Shared;

/// <summary>
/// Shared plaintext encryption service for tests. The service is stateless, so one instance is reused.
/// </summary>
public static class TestDefaults
{
    public const string Passphrase = "wouter";

    extension (IEncryptionService)
    {
        public static IEncryptionService PlaintextInstance => new PlaintextPassthroughService();
        public static IEncryptionService EncryptedInstance => new PassphraseEncryptionService(Passphrase);
    }
}
