using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arius.Core.Shared.Snapshot;

/// <summary>
/// JSON converter for <see cref="FileTreeHash"/> values in snapshot payloads.
/// Snapshots persist the root filetree hash as its canonical lowercase hex string.
/// </summary>
internal sealed class FileTreeHashJsonConverter : JsonConverter<FileTreeHash>
{
    public override FileTreeHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => FileTreeHash.Parse(reader.GetString() ?? throw new JsonException("Expected file tree hash string."));

    public override void Write(Utf8JsonWriter writer, FileTreeHash value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}