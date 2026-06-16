using Arius.Core.Shared.Encryption;

namespace Arius.Tests.Shared.Encryption;

/// <summary>
/// Shared plaintext encryption service for tests. The service is stateless, so one instance is reused.
/// </summary>
public static class TestEncryption
{
    public static IEncryptionService Instance { get; } = new PlaintextPassthroughService();
}
