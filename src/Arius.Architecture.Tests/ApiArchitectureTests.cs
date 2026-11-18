using System.Reflection;
using ArchUnitNET.Loader;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hubs;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;

namespace Arius.Architecture.Tests;

/// <summary>
/// Enforces Arius.Api's vertical-slice layout: <c>Features/&lt;Area&gt;</c> slices over <c>Shared/</c>
/// infrastructure, reaching Arius.Core only through its features.
/// Note (same limitation as DependencyTests): ArchUnitNET does not detect usages that occur only
/// inside lambdas / async state machines, so endpoint lambda bodies are not covered.
/// </summary>
public class ApiArchitectureTests
{
    private const string FeaturesNamespace = "Arius.Api.Features";
    private const string SharedNamespace   = "Arius.Api.Shared";

    private static readonly Assembly ApiAssembly = typeof(Api.AssemblyMarker).Assembly;

    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(ApiAssembly, typeof(Core.AssemblyMarker).Assembly)
        .Build();

    /// <summary>The types that wire the slices together: the host maps every endpoint, the hub dispatches
    /// every hub method to its feature. They may reference any feature's entry point.</summary>
    private static readonly string[] CompositionRoots =
    [
        typeof(Api.AriusApiHost).FullName!,
        typeof(JobsHub).FullName!,
    ];

    private static IEnumerable<ArchUnitNET.Domain.IType> ApiTypes =>
        Architecture.Types.Where(type => type.Assembly.Name == ApiAssembly.GetName().Name);

    [Test]
    public void Internal_Types_Should_Only_Be_Used_Within_Their_Namespace_Subtree()
    {
        var declaringNamespaceByType = ApiAssembly.GetTypes()
            .Where(type => type is { IsNested: false, IsNotPublic: true, Namespace: not null })
            .Where(type => type.Namespace!.StartsWith("Arius.Api", StringComparison.Ordinal))
            .Where(type => !type.Name.Contains('<'))
            .Where(type => type != typeof(Api.SharedWithinAssemblyAttribute))
            .Where(type => type.GetCustomAttribute<Api.SharedWithinAssemblyAttribute>() is null)
            .ToDictionary(type => type.FullName!, type => type.Namespace!, StringComparer.Ordinal);

        var violations = new List<string>();
        foreach (var consumer in ApiTypes.Where(type => !IsCompositionRoot(type)))
        {
            var consumerNamespace = consumer.Namespace.FullName;
            foreach (var dependency in consumer.Dependencies)
            {
                if (!declaringNamespaceByType.TryGetValue(dependency.Target.FullName, out var declaringNamespace))
                    continue;
                if (IsWithin(consumerNamespace, declaringNamespace))
                    continue;

                violations.Add(
                    $"'{dependency.Target.FullName}' is used by '{consumer.FullName}', outside its namespace subtree. " +
                    "Mark it [SharedWithinAssembly] if cross-namespace sharing is intended.");
            }
        }

        ShouldBeEmpty(violations, "Internal Arius.Api types may only be used within their declaring namespace or a descendant:");
    }

    [Test]
    public void SharedWithinAssembly_Should_Only_Decorate_Internal_Types()
    {
        var publicDecorated = ApiAssembly.GetTypes()
            .Where(type => type.GetCustomAttribute<Api.SharedWithinAssemblyAttribute>() is not null)
            .Where(type => type.IsVisible)
            .Select(type => type.FullName!)
            .ToList();

        ShouldBeEmpty(publicDecorated, "[SharedWithinAssembly] is intended for internal types only:");
    }

    [Test]
    public void Feature_Areas_Should_Not_Depend_On_Each_Other()
    {
        var violations = new List<string>();
        foreach (var consumer in ApiTypes.Where(type => FeatureArea(type.Namespace.FullName) is not null))
        {
            var consumerArea = FeatureArea(consumer.Namespace.FullName);
            foreach (var dependency in consumer.Dependencies)
            {
                var targetArea = FeatureArea(dependency.Target.Namespace.FullName);
                if (targetArea is not null && targetArea != consumerArea)
                    violations.Add($"'{consumer.FullName}' (area {consumerArea}) depends on '{dependency.Target.FullName}' (area {targetArea}).");
            }
        }

        ShouldBeEmpty(violations, "A feature area may only use its own types and Shared; move what two areas need to Shared:");
    }

    [Test]
    public void Shared_Should_Not_Depend_On_Features()
    {
        var violations = new List<string>();
        foreach (var consumer in ApiTypes.Where(type => IsWithin(type.Namespace.FullName, SharedNamespace) && !IsCompositionRoot(type)))
            foreach (var dependency in consumer.Dependencies)
                if (FeatureArea(dependency.Target.Namespace.FullName) is { } area)
                    violations.Add($"'{consumer.FullName}' depends on '{dependency.Target.FullName}' (feature area {area}).");

        ShouldBeEmpty(violations, "Arius.Api Shared must not depend on Features:");
    }

    [Test]
    public void Api_Should_Reach_Core_Only_Through_Features()
    {
        // A host sends commands and queries through IMediator; it does not resolve Core's Shared services.
        // The one seam is the composer that opens the container and registers the Core graph.
        var coreServices = new[]
        {
            typeof(ISnapshotService), typeof(IChunkIndexService), typeof(IChunkStorageService), typeof(IFileTreeService),
            typeof(IBlobContainerService), typeof(IBlobService), typeof(IBlobServiceFactory),
        }.Select(type => type.FullName!).ToHashSet(StringComparer.Ordinal);

        var violations = new List<string>();
        foreach (var consumer in ApiTypes.Where(type => type.FullName != typeof(AzureRepositoryCoreComposer).FullName))
            foreach (var dependency in consumer.Dependencies)
                if (coreServices.Contains(dependency.Target.FullName))
                    violations.Add($"'{consumer.FullName}' depends on '{dependency.Target.FullName}'.");

        ShouldBeEmpty(violations, "Arius.Api must reach Core through IMediator, not Core's Shared services:");
    }

    private static bool IsCompositionRoot(ArchUnitNET.Domain.IType type) =>
        CompositionRoots.Any(root => type.FullName == root || type.FullName.StartsWith(root + "+", StringComparison.Ordinal));

    private static bool IsWithin(string candidate, string ancestor) =>
        candidate == ancestor || candidate.StartsWith(ancestor + ".", StringComparison.Ordinal);

    /// <summary>The area segment of a namespace under <c>Arius.Api.Features</c>, or null when it is not one.</summary>
    private static string? FeatureArea(string @namespace) =>
        @namespace.StartsWith(FeaturesNamespace + ".", StringComparison.Ordinal)
            ? @namespace[(FeaturesNamespace.Length + 1)..].Split('.')[0]
            : null;

    private static void ShouldBeEmpty(List<string> violations, string because)
    {
        violations = violations.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        violations.ShouldBeEmpty(because + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }
}
