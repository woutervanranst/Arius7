namespace Arius.Core.Shared.HashCache;

/// <summary>Cheap, platform-provided change signals for one file. See <see cref="SignalSets"/>.</summary>
[SharedWithinAssembly]
internal readonly record struct FileChangeSignals(long CtimeTicks, string Inode, string Dev, int SignalSet);
