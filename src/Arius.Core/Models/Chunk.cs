namespace Arius.Core.Models;

public readonly record struct Chunk(ReadOnlyMemory<byte> Data);
