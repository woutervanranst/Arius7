using System.Reflection;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Arius.Architecture.Tests;

/// <summary>
/// Enforces the namespace-scoped meaning of <c>internal</c> within Arius.Core: an internal type
/// declared in namespace N may only be referenced from N or a descendant of N (not a parent, not a
/// sibling). This turns each namespace/folder into a module boundary. Types that are intentionally
/// shared across namespaces opt out with <see cref="Core.SharedWithinAssemblyAttribute"/>.
/// Note (same limitation as DependencyTests): ArchUnitNET does not detect usages that occur only
/// inside lambdas / async state machines.
/// </summary>
public class ModulithTests
{
    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(Core.AssemblyMarker).Assembly)
        .Build();

    private static readonly ArchUnitNET.Domain.Assembly CoreAssembly =
        Architecture.Assemblies.First(a => a.Name == typeof(Core.AssemblyMarker).Assembly.GetName().Name);

    [Test]
    public void Internal_Types_Should_Only_Be_Used_Within_Their_Namespace_Subtree()
    {
        var violations = new List<string>();

        foreach (var type in NamespaceScopedInternalTypes())
        {
            var declaringNamespace = type.Namespace!;

            // DoNotResideInNamespace does a literal substring match (matching DependencyTests' usage),
            // so passing the declaring namespace also excludes its descendant namespaces — leaving
            // exactly the "declaring namespace + descendants" set as the allowed consumers.
            IArchRule rule = Classes().That().ResideInAssembly(CoreAssembly)
                .And().DoNotResideInNamespace(declaringNamespace)
                .Should().NotDependOnAnyTypesThat().HaveFullName(type.FullName!);

            if (!rule.HasNoViolations(Architecture))
            {
                violations.Add(
                    $"'{type.FullName}' (in '{declaringNamespace}') is used outside its namespace subtree. " +
                    $"Mark it [SharedWithinAssembly] if cross-namespace sharing is intended. {DescribeViolations(rule)}");
            }
        }

        violations.ShouldBeEmpty(
            "Internal types may only be used within their declaring namespace or a descendant:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Test]
    public void SharedWithinAssembly_Should_Only_Decorate_Internal_Types()
    {
        // The attribute relaxes the namespace rule for internal types only; it must never be used to
        // paper over a type that is actually visible outside the assembly.
        var publicDecorated = typeof(Core.AssemblyMarker).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<Core.SharedWithinAssemblyAttribute>() is not null)
            .Where(type => type.IsVisible)
            .Select(type => type.FullName!)
            .ToList();

        publicDecorated.ShouldBeEmpty(
            $"[SharedWithinAssembly] is intended for internal types only. Externally visible types must " +
            $"not carry it: {string.Join(", ", publicDecorated)}");
    }

    private static IEnumerable<System.Type> NamespaceScopedInternalTypes() =>
        typeof(Core.AssemblyMarker).Assembly
            .GetTypes()
            // Top-level internal Arius.Core types only — skips nested types, public API, compiler-injected
            // attributes (System.*/Microsoft.*), and generated helpers like <PrivateImplementationDetails>.
            .Where(type => type is { IsNested: false, IsNotPublic: true, Namespace: not null })
            .Where(type => type.Namespace!.StartsWith("Arius.Core", StringComparison.Ordinal))
            .Where(type => !type.Name.Contains('<'))
            .Where(type => type != typeof(Core.SharedWithinAssemblyAttribute))
            // Opted-out: intentionally shared across namespaces within the assembly.
            .Where(type => type.GetCustomAttribute<Core.SharedWithinAssemblyAttribute>() is null);

    // Helper: produce a human-readable summary of rule violations for failure messages.
    private static string DescribeViolations(IArchRule rule)
    {
        var results = rule.Evaluate(Architecture)
            .Where(r => !r.Passed)
            .Select(r => r.Description)
            .Take(10);
        return string.Join("; ", results);
    }
}
