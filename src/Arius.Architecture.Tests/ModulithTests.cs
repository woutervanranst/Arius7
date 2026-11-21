namespace Arius.Architecture.Tests;

/// <summary>
/// Enforces the namespace-scoped meaning of <c>internal</c> within Arius.Core (see <see cref="NamespaceScopedInternals"/>).
/// Types that are intentionally shared across namespaces opt out with <see cref="Core.SharedWithinAssemblyAttribute"/>.
/// </summary>
public class ModulithTests
{
    [Test]
    public void Internal_Types_Should_Only_Be_Used_Within_Their_Namespace_Subtree() =>
        NamespaceScopedInternals.ShouldOnlyBeUsedWithinTheirNamespaceSubtree(typeof(Core.SharedWithinAssemblyAttribute));

    [Test]
    public void SharedWithinAssembly_Should_Only_Decorate_Internal_Types() =>
        NamespaceScopedInternals.SharedWithinAssemblyShouldOnlyDecorateInternalTypes(typeof(Core.SharedWithinAssemblyAttribute));
}
