using System.Runtime.CompilerServices;
using Mediator;

// Resolve a handler only when its message is sent, so a provider needs only the services its requests use (ADR-0028).
[assembly: MediatorOptions(ServiceLifetime = ServiceLifetime.Transient)]

[assembly: InternalsVisibleTo("Arius.Api.Tests")]
[assembly: InternalsVisibleTo("Arius.Api.Integration.Tests")]
[assembly: InternalsVisibleTo("Arius.Architecture.Tests")]

namespace Arius.Api;

public sealed class AssemblyMarker { }
