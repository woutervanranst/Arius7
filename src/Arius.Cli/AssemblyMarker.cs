using Mediator;
using Microsoft.Extensions.DependencyInjection;

// Resolve a handler only when its message is sent, so a provider needs only the services its requests use (ADR-0028).
[assembly: MediatorOptions(ServiceLifetime = ServiceLifetime.Transient)]

namespace Arius.Cli;

/// <summary>Marker class to locate the Arius.Cli assembly.</summary>
public sealed class AssemblyMarker { }
