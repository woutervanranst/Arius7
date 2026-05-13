using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Arius.E2E.Tests")]
[assembly: InternalsVisibleTo("Arius.Core.Tests")]
[assembly: InternalsVisibleTo("Arius.Integration.Tests")]

namespace Arius.Tests.Shared;

public sealed class AssemblyMarker { }
