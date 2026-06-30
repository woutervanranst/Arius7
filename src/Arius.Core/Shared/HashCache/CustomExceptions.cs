namespace Arius.Core.Shared.HashCache;

// Thrown when the local hashcache SQLite file is unreadable or corrupt. ArchiveCommandHandler (a
// different namespace) lets this escape its per-file skip so corruption faults the run loudly with
// actionable guidance instead of silently dropping files, so it is shared across namespaces within
// the assembly. Unlike ChunkIndexLocalStore — which silently recreates a corrupt DB — the hashcache
// has no remote backing and no repair command, so it instructs the operator to delete the directory
// and recover with one full-hash run rather than repairing in place.
[SharedWithinAssembly]
internal sealed class HashCacheLocalStoreException(string message, Exception innerException)
    : InvalidOperationException(message, innerException)
{
}
