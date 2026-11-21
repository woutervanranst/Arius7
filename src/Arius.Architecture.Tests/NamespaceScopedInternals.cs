using System.Reflection;
using ArchUnitNET.Loader;

namespace Arius.Architecture.Tests;

/// <summary>
/// The namespace-scoped meaning of <c>internal</c>, shared by each assembly's architecture tests: a top-level internal
/// type declared in namespace N may be used only from N or a descendant of N (not a parent, not a sibling), turning
/// each namespace/folder into a module boundary. Each assembly declares its own internal
/// <c>[SharedWithinAssembly]</c> in its root namespace; types intentionally shared across namespaces opt out with it.
/// Note (same limitation as DependencyTests): ArchUnitNET does not detect usages that occur only inside lambdas /
/// async state machines.
/// </summary>
internal static class NamespaceScopedInternals
{
    /// <summary>Checks the assembly that declares <paramref name="sharedWithinAssembly"/>, whose namespace is the
    /// assembly's root. <paramref name="compositionRoots"/> wire the modules together and may use any internal type.</summary>
    public static void ShouldOnlyBeUsedWithinTheirNamespaceSubtree(System.Type sharedWithinAssembly, params System.Type[] compositionRoots)
    {
        var assembly      = sharedWithinAssembly.Assembly;
        var rootNamespace = sharedWithinAssembly.Namespace!;

        var declaringNamespaceByType = assembly.GetTypes()
            // Top-level internal types only — skips nested types, public API, compiler-injected attributes
            // (System.*/Microsoft.*), and generated helpers like <PrivateImplementationDetails>.
            .Where(type => type is { IsNested: false, IsNotPublic: true, Namespace: not null })
            .Where(type => IsWithin(type.Namespace!, rootNamespace))
            .Where(type => !type.Name.Contains('<'))
            .Where(type => type != sharedWithinAssembly)
            .Where(type => type.GetCustomAttribute(sharedWithinAssembly) is null)
            .ToDictionary(type => type.FullName!, type => type.Namespace!, StringComparer.Ordinal);

        // Consumers declared in the assembly's own namespaces only: skips the Mediator-generated wiring, which
        // references every handler.
        var consumers = new ArchLoader().LoadAssemblies(assembly).Build().Types
            .Where(type => IsWithin(type.Namespace.FullName, rootNamespace))
            .Where(type => !IsCompositionRoot(type, compositionRoots));

        var violations = new List<string>();
        foreach (var consumer in consumers)
            foreach (var dependency in consumer.Dependencies)
                if (declaringNamespaceByType.TryGetValue(dependency.Target.FullName, out var declaringNamespace)
                    && !IsWithin(consumer.Namespace.FullName, declaringNamespace))
                    violations.Add(
                        $"'{dependency.Target.FullName}' (in '{declaringNamespace}') is used by " +
                        $"'{consumer.FullName}' (in '{consumer.Namespace.FullName}'), outside its namespace subtree. " +
                        "Mark it [SharedWithinAssembly] if cross-namespace sharing is intended.");

        ShouldBeEmpty(violations, $"Internal {rootNamespace} types may only be used within their declaring namespace or a descendant:");
    }

    /// <summary>The attribute relaxes the namespace rule for internal types only; it must never be used to paper over
    /// a type that is actually visible outside the assembly.</summary>
    public static void SharedWithinAssemblyShouldOnlyDecorateInternalTypes(System.Type sharedWithinAssembly)
    {
        var publicDecorated = sharedWithinAssembly.Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute(sharedWithinAssembly) is not null)
            .Where(type => type.IsVisible)
            .Select(type => type.FullName!)
            .ToList();

        ShouldBeEmpty(publicDecorated, "[SharedWithinAssembly] is intended for internal types only:");
    }

    /// <summary>Whether <paramref name="type"/> is one of <paramref name="compositionRoots"/> or nested in one.</summary>
    internal static bool IsCompositionRoot(ArchUnitNET.Domain.IType type, IEnumerable<System.Type> compositionRoots) =>
        compositionRoots.Any(root => type.FullName == root.FullName || type.FullName.StartsWith(root.FullName + "+", StringComparison.Ordinal));

    internal static bool IsWithin(string candidate, string ancestor) =>
        candidate == ancestor || candidate.StartsWith(ancestor + ".", StringComparison.Ordinal);

    internal static void ShouldBeEmpty(List<string> violations, string because)
    {
        violations = violations.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        violations.ShouldBeEmpty(because + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }
}
