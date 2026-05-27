using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Arius.E2E.Tests")]
[assembly: InternalsVisibleTo("Arius.Core.Tests")]
[assembly: InternalsVisibleTo("Arius.Integration.Tests")]
[assembly: InternalsVisibleTo("Arius.Benchmarks")]

namespace Arius.Tests.Shared;

public sealed class AssemblyMarker { }
