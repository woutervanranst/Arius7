using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arius.Core.Encryption;

namespace Arius.Core.FileTree;

/// <summary>
/// Serializes and deserializes <see cref="TreeBlob"/> instances to/from JSON.
///
/// Deterministic JSON rules:
/// - Entries are sorted by <see cref="TreeEntry.Name"/> (ordinal, case-sensitive).
/// - Timestamp fields use ISO-8601 / round-trip format ("O"), UTC only.
/// - No extra whitespace (compact output).
/// - <c>null</c> timestamp fields are omitted from the JSON output.
///
/// Tree hash = SHA256 of the canonical UTF-8 JSON bytes, optionally passphrase-seeded
/// via <see cref="IEncryptionService.ComputeHash(byte[])"/>.
/// </summary>
public static class TreeBlobSerializer
{
    // ── JSON options ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented            = false,
        DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
        Encoder                  = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy     = JsonNamingPolicy.CamelCase,
        Converters               = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // ── Serialize ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a <see cref="TreeBlob"/> to canonical UTF-8 JSON bytes.
    /// Entries are sorted by name before serialization.
    /// </summary>
    public static byte[] Serialize(TreeBlob tree)
    {
        // Ensure deterministic order — sort entries by name (ordinal)
        var sorted = new TreeBlob
        {
            Entries = tree.Entries
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToList()
        };

        return JsonSerializer.SerializeToUtf8Bytes(sorted, s_writeOptions);
    }

    // ── Deserialize ───────────────────────────────────────────────────────────

    /// <summary>
    /// Deserializes a <see cref="TreeBlob"/> from UTF-8 JSON bytes.
    /// </summary>
    public static TreeBlob Deserialize(byte[] json) =>
        JsonSerializer.Deserialize<TreeBlob>(json, s_readOptions)
            ?? throw new InvalidDataException("Failed to deserialize tree blob: null result.");

    /// <summary>Deserializes a <see cref="TreeBlob"/> from a readable stream.</summary>
    public static async Task<TreeBlob> DeserializeAsync(
        Stream            stream,
        CancellationToken cancellationToken = default) =>
        await JsonSerializer.DeserializeAsync<TreeBlob>(stream, s_readOptions, cancellationToken)
            ?? throw new InvalidDataException("Failed to deserialize tree blob: null result.");

    // ── Hash computation (task 5.3) ────────────────────────────────────────────

    /// <summary>
    /// Computes the tree hash: <see cref="IEncryptionService.ComputeHash(byte[])"/> applied to
    /// the canonical JSON bytes. With passphrase: SHA256(passphrase + json); without: SHA256(json).
    /// Returns lowercase hex.
    /// </summary>
    public static string ComputeHash(TreeBlob tree, IEncryptionService encryption)
    {
        var json = Serialize(tree);
        var hash = encryption.ComputeHash(json);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
