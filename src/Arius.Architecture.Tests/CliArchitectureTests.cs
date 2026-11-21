namespace Arius.Architecture.Tests;

/// <summary>
/// Enforces the namespace-scoped meaning of <c>internal</c> within Arius.Cli (see <see cref="NamespaceScopedInternals"/>):
/// each <c>Commands/&lt;Verb&gt;</c> folder is a module boundary. <see cref="Cli.CliBuilder"/> is the composition root: it
/// adds every verb to the root command.
/// </summary>
public class CliArchitectureTests
{
    [Test]
    public void Internal_Types_Should_Only_Be_Used_Within_Their_Namespace_Subtree() =>
        NamespaceScopedInternals.ShouldOnlyBeUsedWithinTheirNamespaceSubtree(typeof(Cli.SharedWithinAssemblyAttribute),
            typeof(Cli.CliBuilder));

    [Test]
    public void SharedWithinAssembly_Should_Only_Decorate_Internal_Types() =>
        NamespaceScopedInternals.SharedWithinAssemblyShouldOnlyDecorateInternalTypes(typeof(Cli.SharedWithinAssemblyAttribute));
}
