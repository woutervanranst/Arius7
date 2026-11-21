using System.Reflection;
using ArchUnitNET.Loader;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Hubs;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using static Arius.Architecture.Tests.NamespaceScopedInternals;

namespace Arius.Architecture.Tests;

/// <summary>
/// Enforces Arius.Api's vertical-slice layout: <c>Features/&lt;Area&gt;</c> slices over <c>Shared/</c>
/// infrastructure, reaching Arius.Core only through its features. ArchUnitNET does not see what the body of an
/// <c>async IAsyncEnumerable</c> method uses, so the hub's streaming features (StreamContainers, StreamEntries,
/// SearchAll) are checked on their signatures and constructor dependencies only.
/// </summary>
public class ApiArchitectureTests
{
    private const string FeaturesNamespace = "Arius.Api.Features";
    private const string SharedNamespace   = "Arius.Api.Shared";

    private static readonly Assembly ApiAssembly = typeof(Api.AssemblyMarker).Assembly;

    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(ApiAssembly, typeof(Core.AssemblyMarker).Assembly)
        .Build();

    private static readonly Lazy<List<RouteEndpoint>> RestEndpoints = new(MapRestEndpoints);

    /// <summary>The types that wire the slices together: the host maps every endpoint, the hub dispatches
    /// every hub method to its feature. They may reference any feature's entry point.</summary>
    private static readonly System.Type[] CompositionRoots =
    [
        typeof(Api.AriusApiHost),
        typeof(JobsHub),
    ];

    // Types declared in Arius.Api namespaces only: skips the Mediator-generated wiring, which references every handler.
    private static IEnumerable<ArchUnitNET.Domain.IType> ApiTypes =>
        Architecture.Types
            .Where(type => type.Assembly.Name == ApiAssembly.GetName().Name)
            .Where(type => IsWithin(type.Namespace.FullName, "Arius.Api"));

    [Test]
    public void Internal_Types_Should_Only_Be_Used_Within_Their_Namespace_Subtree() =>
        ShouldOnlyBeUsedWithinTheirNamespaceSubtree(typeof(Api.SharedWithinAssemblyAttribute), CompositionRoots);

    [Test]
    public void SharedWithinAssembly_Should_Only_Decorate_Internal_Types() =>
        SharedWithinAssemblyShouldOnlyDecorateInternalTypes(typeof(Api.SharedWithinAssemblyAttribute));

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
    public void Endpoints_And_Hub_Handlers_Should_Live_In_Features()
    {
        var misplaced = ApiAssembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => typeof(IEndpoint).IsAssignableFrom(type) || typeof(IHubHandler).IsAssignableFrom(type))
            .Where(type => FeatureArea(type.Namespace ?? string.Empty) is null)
            .Select(type => type.FullName!)
            .ToList();

        ShouldBeEmpty(misplaced, "An IEndpoint or IHubHandler is a vertical slice and belongs in a feature area:");
    }

    [Test]
    public void Hub_Handler_Parameters_Should_Be_Marked_FromServices()
    {
        // SignalR infers a service only for a hub-method parameter without attributes, and the compiler adds a
        // nullability attribute to some parameters. An unmarked handler then becomes an expected wire argument and
        // every client call fails on the argument count.
        var unmarked = typeof(JobsHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters()
                .Where(parameter => typeof(IHubHandler).IsAssignableFrom(parameter.ParameterType))
                .Where(parameter => parameter.GetCustomAttribute<Microsoft.AspNetCore.Mvc.FromServicesAttribute>() is null)
                .Select(parameter => $"{method.Name}({parameter.ParameterType.FullName} {parameter.Name})"))
            .ToList();

        ShouldBeEmpty(unmarked, "A hub handler parameter on JobsHub must carry [FromServices]:");
    }

    [Test]
    public void Endpoints_Should_Have_An_OpenApi_Summary()
    {
        // The route delegate is a lambda, so XML comments never reach the OpenAPI document; the summary must be set
        // on the mapping.
        var missing = RestEndpoints.Value
            .Where(endpoint => string.IsNullOrWhiteSpace(endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary))
            .Select(endpoint => endpoint.DisplayName!)
            .ToList();

        ShouldBeEmpty(missing, "Chain .WithSummary(...) onto the endpoint mapping:");
    }

    [Test]
    public void Endpoints_Should_Declare_Their_Success_Response()
    {
        // A handler that returns a plain IResult gives OpenAPI no response schema. Return the DTO or a typed
        // Results<...> union, or declare the response with .Produces(...).
        var undeclared = RestEndpoints.Value
            .Where(endpoint => !endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Any(response => response.StatusCode is >= 200 and < 300))
            .Select(endpoint => endpoint.DisplayName!)
            .ToList();

        ShouldBeEmpty(undeclared, "Declare a 2xx response so the OpenAPI document has a response schema:");
    }

    [Test]
    public void Shared_Should_Not_Depend_On_Features()
    {
        var violations = new List<string>();
        foreach (var consumer in ApiTypes.Where(type => IsWithin(type.Namespace.FullName, SharedNamespace) && !IsCompositionRoot(type, CompositionRoots)))
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
        var coreServices = typeof(Core.AssemblyMarker).Assembly.GetTypes()
            .Where(type => type is { IsInterface: true, IsPublic: true })
            .Where(type => IsWithin(type.Namespace ?? string.Empty, "Arius.Core.Shared"))
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);
        coreServices.ShouldContain(typeof(ISnapshotService).FullName!);
        coreServices.ShouldContain(typeof(IBlobServiceFactory).FullName!);

        var violations = new List<string>();
        foreach (var consumer in ApiTypes.Where(type => type.FullName != typeof(AzureRepositoryCoreComposer).FullName))
            foreach (var dependency in consumer.Dependencies)
                if (coreServices.Contains(dependency.Target.FullName))
                    violations.Add($"'{consumer.FullName}' depends on '{dependency.Target.FullName}'.");

        ShouldBeEmpty(violations, "Arius.Api must reach Core through IMediator, not Core's Shared services:");
    }

    /// <summary>The REST endpoints as the feature scan maps them, with the metadata the OpenAPI document is built from.</summary>
    private static List<RouteEndpoint> MapRestEndpoints()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddFeatures();
        // The endpoints' own dependencies are not registered: only their route metadata is read.
        builder.Host.UseDefaultServiceProvider(options => options.ValidateOnBuild = false);

        using var app = builder.Build();
        app.MapEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    /// <summary>The area segment of a namespace under <c>Arius.Api.Features</c>, or null when it is not one.</summary>
    private static string? FeatureArea(string @namespace) =>
        @namespace.StartsWith(FeaturesNamespace + ".", StringComparison.Ordinal)
            ? @namespace[(FeaturesNamespace.Length + 1)..].Split('.')[0]
            : null;
}
