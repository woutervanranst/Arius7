namespace Arius.Cli;

/// <summary>
/// Exempts an <c>internal</c> type from the architecture rule that restricts internal types to their own namespace
/// subtree, so it can be shared across namespaces within this assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface
    | AttributeTargets.Enum, Inherited = false)]
internal sealed class SharedWithinAssemblyAttribute : Attribute;
