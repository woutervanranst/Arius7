using Arius.Core.Application.Abstractions;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Core.Application.Key;

// ── Output ──────────────────────────────────────────────────────────────────

public sealed record KeyResult(
    bool   Success,
    string Message,
    IReadOnlyList<string> Keys);

// ── Request ──────────────────────────────────────────────────────────────────

public enum KeyOperation { Add, Remove, List, ChangePassword }

public sealed record KeyRequest(
    string        ConnectionString,
    string        ContainerName,
    string        Passphrase,
    KeyOperation  Operation,
    string?       NewPassphrase = null,
    string?       KeyId         = null) : IRequest<KeyResult>;

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class KeyHandler : IRequestHandler<KeyRequest, KeyResult>
{
    private readonly Func<string, string, AzureRepository> _repoFactory;

    public KeyHandler(Func<string, string, AzureRepository> repoFactory)
        => _repoFactory = repoFactory;

    public async ValueTask<KeyResult> Handle(
        KeyRequest request,
        CancellationToken cancellationToken = default)
    {
        var repo = _repoFactory(request.ConnectionString, request.ContainerName);
        var masterKey = await repo.UnlockAsync(request.Passphrase, cancellationToken);

        return request.Operation switch
        {
            KeyOperation.List           => await ListAsync(repo, masterKey, cancellationToken),
            KeyOperation.Add            => await AddAsync(repo, masterKey, request, cancellationToken),
            KeyOperation.Remove         => await RemoveAsync(repo, masterKey, request, cancellationToken),
            KeyOperation.ChangePassword => await ChangePasswordAsync(repo, masterKey, request, cancellationToken),
            _                           => throw new ArgumentOutOfRangeException()
        };
    }

    // ── Operations ────────────────────────────────────────────────────────────

    private static async Task<KeyResult> ListAsync(
        AzureRepository repo, byte[] masterKey, CancellationToken ct)
    {
        var keys = new List<string>();
        await foreach (var item in repo.ListKeyBlobsAsync(ct))
            keys.Add(item);
        return new KeyResult(true, $"{keys.Count} key(s) in repository.", keys);
    }

    private static async Task<KeyResult> AddAsync(
        AzureRepository repo, byte[] masterKey, KeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.NewPassphrase))
            return new KeyResult(false, "New passphrase is required for 'add'.", []);

        var keyId = request.KeyId ?? Guid.NewGuid().ToString("N")[..8];
        await repo.AddKeyAsync(keyId, masterKey, request.NewPassphrase, ct);
        return new KeyResult(true, $"Key '{keyId}' added.", [keyId]);
    }

    private static async Task<KeyResult> RemoveAsync(
        AzureRepository repo, byte[] masterKey, KeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.KeyId))
            return new KeyResult(false, "Key ID is required for 'remove'.", []);

        var keys = new List<string>();
        await foreach (var item in repo.ListKeyBlobsAsync(ct))
            keys.Add(item);

        if (keys.Count <= 1)
            return new KeyResult(false, "Cannot remove the last key.", keys);

        await repo.RemoveKeyAsync(request.KeyId, ct);
        return new KeyResult(true, $"Key '{request.KeyId}' removed.", []);
    }

    private static async Task<KeyResult> ChangePasswordAsync(
        AzureRepository repo, byte[] masterKey, KeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.NewPassphrase))
            return new KeyResult(false, "New passphrase is required for 'passwd'.", []);

        var keyId = request.KeyId ?? "default";
        await repo.AddKeyAsync(keyId, masterKey, request.NewPassphrase, ct);
        return new KeyResult(true, $"Passphrase changed for key '{keyId}'.", [keyId]);
    }
}
