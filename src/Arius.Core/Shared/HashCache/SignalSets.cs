namespace Arius.Core.Shared.HashCache;

/// <summary>Provenance tag stored on a hashcache row so signals are only compared within the same source.</summary>
[SharedWithinAssembly]
internal static class SignalSets
{
    public const int None    = 0;
    public const int Posix   = 1;
    public const int Windows = 2;
}
