using System.Security.Cryptography;
using Arius.Core.Infrastructure.Crypto;
using Arius.Core.Models;
using Shouldly;
using TUnit.Core;

namespace Arius.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 3.7  Encryption unit tests
// ─────────────────────────────────────────────────────────────────────────────

public class CryptoServiceTests
{
    // ── Roundtrip ────────────────────────────────────────────────────────────

    [Test]
    public async Task EncryptDecrypt_WithPassphrase_Roundtrips()
    {
        var plaintext  = "Hello, Arius!"u8.ToArray();
        var passphrase = "test-passphrase";

        using var plaintextStream  = new MemoryStream(plaintext);
        using var ciphertextStream = new MemoryStream();
        await CryptoService.EncryptAsync(plaintextStream, ciphertextStream, passphrase);

        ciphertextStream.Position = 0;
        using var decryptedStream = new MemoryStream();
        await CryptoService.DecryptAsync(ciphertextStream, decryptedStream, passphrase);

        decryptedStream.ToArray().ShouldBe(plaintext);
    }

    [Test]
    public async Task EncryptDecrypt_WithMasterKeyBytes_Roundtrips()
    {
        var plaintext = RandomNumberGenerator.GetBytes(4096);
        var masterKey = CryptoService.GenerateMasterKey();

        using var plaintextStream  = new MemoryStream(plaintext);
        using var ciphertextStream = new MemoryStream();
        await CryptoService.EncryptAsync(plaintextStream, ciphertextStream, masterKey);

        ciphertextStream.Position = 0;
        using var decryptedStream = new MemoryStream();
        await CryptoService.DecryptAsync(ciphertextStream, decryptedStream, masterKey);

        decryptedStream.ToArray().ShouldBe(plaintext);
    }

    [Test]
    public async Task EncryptTwice_SamePlaintext_ProducesDifferentCiphertext()
    {
        // Each encryption uses a fresh random salt → output is non-deterministic
        var plaintext  = "same data"u8.ToArray();
        var passphrase = "passphrase";

        var ct1 = await CryptoService.EncryptBytesAsync(plaintext, passphrase);
        var ct2 = await CryptoService.EncryptBytesAsync(plaintext, passphrase);

        ct1.ShouldNotBe(ct2);
    }

    // ── OpenSSL header format ─────────────────────────────────────────────────

    [Test]
    public async Task EncryptedOutput_StartsWithOpenSslSaltedHeader()
    {
        var ct = await CryptoService.EncryptBytesAsync("data"u8.ToArray(), "pass");

        // First 8 bytes must be "Salted__"
        var magic = System.Text.Encoding.ASCII.GetString(ct, 0, 8);
        magic.ShouldBe("Salted__");
        // Total header = 16 bytes (8 magic + 8 salt), so ciphertext follows at offset 16
        ct.Length.ShouldBeGreaterThan(16);
    }

    // ── Wrong passphrase ─────────────────────────────────────────────────────

    [Test]
    public async Task Decrypt_WrongPassphrase_Throws()
    {
        var ct = await CryptoService.EncryptBytesAsync("secret"u8.ToArray(), "correct");

        await Should.ThrowAsync<Exception>(async () =>
            await CryptoService.DecryptBytesAsync(ct, "wrong"));
    }

    // ── Stream-based large data ───────────────────────────────────────────────

    [Test]
    public async Task EncryptDecrypt_LargeData_Roundtrips()
    {
        var plaintext = RandomNumberGenerator.GetBytes(1024 * 1024); // 1 MB
        var masterKey = CryptoService.GenerateMasterKey();

        var ct = await CryptoService.EncryptBytesAsync(plaintext, masterKey);
        var pt = await CryptoService.DecryptBytesAsync(ct, masterKey);

        pt.ShouldBe(plaintext);
    }

    // ── Master key size ───────────────────────────────────────────────────────

    [Test]
    public void GenerateMasterKey_Returns32Bytes()
    {
        var key = CryptoService.GenerateMasterKey();
        key.Length.ShouldBe(32);
    }

    [Test]
    public void GenerateMasterKey_TwoCalls_ProduceDifferentKeys()
    {
        CryptoService.GenerateMasterKey().ShouldNotBe(CryptoService.GenerateMasterKey());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3.8  Key management unit tests
// ─────────────────────────────────────────────────────────────────────────────

public class KeyManagerTests
{
    private static string MakeTempRepo()
    {
        var path = Path.Combine(Path.GetTempPath(), "arius-km-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "keys"));
        return path;
    }

    [Test]
    public async Task CreateFirstKey_ThenUnlock_ReturnsCorrectMasterKey()
    {
        var repo = MakeTempRepo();
        var km   = new KeyManager(repo);

        var (masterKey, _) = await km.CreateFirstKeyAsync("my-pass");
        var unlocked       = await km.TryUnlockAsync("my-pass");

        unlocked.ShouldNotBeNull();
        unlocked!.ShouldBe(masterKey);
    }

    [Test]
    public async Task TryUnlock_WrongPassphrase_ReturnsNull()
    {
        var repo = MakeTempRepo();
        var km   = new KeyManager(repo);

        await km.CreateFirstKeyAsync("correct");
        var result = await km.TryUnlockAsync("wrong");

        result.ShouldBeNull();
    }

    [Test]
    public async Task AddKey_NewPassphrase_UnlocksWithBothPassphrases()
    {
        var repo = MakeTempRepo();
        var km   = new KeyManager(repo);

        var (masterKey, _) = await km.CreateFirstKeyAsync("pass1");
        await km.AddKeyAsync("pass1", "pass2", "second");

        var unlocked1 = await km.TryUnlockAsync("pass1");
        var unlocked2 = await km.TryUnlockAsync("pass2");

        unlocked1.ShouldNotBeNull();
        unlocked2.ShouldNotBeNull();
        unlocked1!.ShouldBe(masterKey);
        unlocked2!.ShouldBe(masterKey); // same master key, different passphrase
    }

    [Test]
    public async Task RemoveKey_LeavesOneRemaining_Succeeds()
    {
        var repo = MakeTempRepo();
        var km   = new KeyManager(repo);

        await km.CreateFirstKeyAsync("pass1");
        await km.AddKeyAsync("pass1", "pass2", "second");

        km.RemoveKey("second");

        km.ListKeys().ShouldBe(["default"]);
        (await km.TryUnlockAsync("pass2")).ShouldBeNull();
    }

    [Test]
    public async Task RemoveKey_LastKey_Throws()
    {
        var repo = MakeTempRepo();
        var km   = new KeyManager(repo);

        await km.CreateFirstKeyAsync("only-pass");

        Should.Throw<InvalidOperationException>(() => km.RemoveKey("default"))
              .Message.ShouldContain("last key");
    }

    [Test]
    public async Task ChangePassword_OldPassphraseNoLongerWorks()
    {
        var repo = MakeTempRepo();
        var km   = new KeyManager(repo);

        await km.CreateFirstKeyAsync("old-pass");
        await km.ChangePasswordAsync("default", "old-pass", "new-pass");

        (await km.TryUnlockAsync("old-pass")).ShouldBeNull();
        (await km.TryUnlockAsync("new-pass")).ShouldNotBeNull();
    }

    [Test]
    public async Task ChangePassword_MasterKeyPreserved()
    {
        var repo = MakeTempRepo();
        var km   = new KeyManager(repo);

        var (masterKey, _) = await km.CreateFirstKeyAsync("old-pass");
        await km.ChangePasswordAsync("default", "old-pass", "new-pass");

        var unlocked = await km.TryUnlockAsync("new-pass");
        unlocked!.ShouldBe(masterKey);
    }

    [Test]
    public async Task ListKeys_ReturnsAllKeyIds()
    {
        var repo = MakeTempRepo();
        var km   = new KeyManager(repo);

        await km.CreateFirstKeyAsync("pass1");
        await km.AddKeyAsync("pass1", "pass2", "second");
        await km.AddKeyAsync("pass1", "pass3", "third");

        km.ListKeys().Count.ShouldBe(3);
        km.ListKeys().ShouldContain("default");
        km.ListKeys().ShouldContain("second");
        km.ListKeys().ShouldContain("third");
    }

    [Test]
    public async Task AddKey_WrongExistingPassphrase_Throws()
    {
        var repo = MakeTempRepo();
        var km   = new KeyManager(repo);

        await km.CreateFirstKeyAsync("correct");

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await km.AddKeyAsync("wrong", "new-pass", "second"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3.9  Manual key recovery test
//      Verifies that the encrypted master key in a key file can be decrypted
//      using only the passphrase + the CryptoService (simulating openssl enc -d).
// ─────────────────────────────────────────────────────────────────────────────

public class ManualKeyRecoveryTests
{
    [Test]
    public async Task KeyFile_EncryptedMasterKey_CanBeDecryptedWithPassphrase()
    {
        // Simulate the openssl recovery procedure:
        //   openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -md sha256 -pass pass:PASSPHRASE
        var repo       = Path.Combine(Path.GetTempPath(), "arius-recovery-test", Guid.NewGuid().ToString("N"));
        var passphrase = "recovery-test-passphrase";

        Directory.CreateDirectory(Path.Combine(repo, "keys"));
        var km = new KeyManager(repo);
        var (originalMasterKey, _) = await km.CreateFirstKeyAsync(passphrase);

        // Read the raw key file to simulate "an operator who only has the JSON file"
        var keyFilePath = Path.Combine(repo, "keys", "default.json");
        var keyFileJson = await File.ReadAllTextAsync(keyFilePath);
        var keyFile     = System.Text.Json.JsonSerializer.Deserialize<Arius.Core.Models.KeyFile>(
            keyFileJson)!;

        // Manually decrypt: base64-decode the EncryptedMasterKey, then decrypt with passphrase
        var ciphertext     = Convert.FromBase64String(keyFile.EncryptedMasterKey);
        var recoveredKey   = await CryptoService.DecryptBytesAsync(ciphertext, passphrase, keyFile.Iterations);

        recoveredKey.ShouldBe(originalMasterKey);
        recoveredKey.Length.ShouldBe(32);
    }

    [Test]
    public async Task KeyFile_IsPlainJson_NotEncrypted()
    {
        var repo = Path.Combine(Path.GetTempPath(), "arius-json-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, "keys"));
        var km = new KeyManager(repo);
        await km.CreateFirstKeyAsync("any-pass");

        var keyFilePath = Path.Combine(repo, "keys", "default.json");
        var json        = await File.ReadAllTextAsync(keyFilePath);

        // Should be readable as JSON (not binary garbage)
        json.TrimStart().ShouldStartWith("{");
        json.ShouldContain("\"Salt\"");
        json.ShouldContain("\"Iterations\"");
        json.ShouldContain("\"EncryptedMasterKey\"");
    }
}
