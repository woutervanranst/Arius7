using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Arius.Core.Tests")]
[assembly: InternalsVisibleTo("Arius.Integration.Tests")]
[assembly: InternalsVisibleTo("Arius.Tests.Shared")]

namespace Arius.Core;

/// <summary>Marker class to locate the Arius.Core assembly.</summary>
public sealed class AssemblyMarker { }
