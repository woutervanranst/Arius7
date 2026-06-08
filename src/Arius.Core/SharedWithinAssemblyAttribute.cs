namespace Arius.Core;

/// <summary>
/// Marks an <c>internal</c> type as intentionally shareable across namespaces within this
/// assembly. The namespace-scoped-internal architecture test (see Arius.Architecture.Tests)
/// normally requires an internal type to be used only from its own namespace or a descendant;
/// types carrying this attribute are exempt from that check while remaining assembly-internal.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface
    | AttributeTargets.Enum, Inherited = false)]
internal sealed class SharedWithinAssemblyAttribute : Attribute;
