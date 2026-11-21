using System.Reflection;

namespace Arius.Api.Shared.Hosting;

/// <summary>Finds every <see cref="IEndpoint"/> and <see cref="IHubHandler"/> in this assembly, so adding a
/// feature file is all it takes to register and map it.</summary>
internal static class FeatureRegistration
{
    private const string FeaturesNamespace = "Arius.Api.Features.";

    private static readonly Type[] Endpoints   = ConcreteTypesOf(typeof(IEndpoint));
    private static readonly Type[] HubHandlers = ConcreteTypesOf(typeof(IHubHandler));

    /// <summary>Registers every endpoint and hub handler as a singleton; they are stateless and depend only on
    /// singletons.</summary>
    public static IServiceCollection AddFeatures(this IServiceCollection services)
    {
        foreach (var feature in Endpoints.Concat(HubHandlers))
            services.AddSingleton(feature);

        return services;
    }

    /// <summary>Maps every endpoint, tagged with its feature area (the namespace segment after
    /// <c>Arius.Api.Features</c>) so the OpenAPI document groups operations by area.</summary>
    public static void MapEndpoints(this IEndpointRouteBuilder api)
    {
        foreach (var area in Endpoints.GroupBy(endpoint => endpoint.Namespace![FeaturesNamespace.Length..].Split('.')[0]).OrderBy(area => area.Key, StringComparer.Ordinal))
        {
            var group = api.MapGroup("").WithTags(area.Key);
            foreach (var endpoint in area)
                endpoint.GetMethod(nameof(IEndpoint.Map), BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [group]);
        }
    }

    private static Type[] ConcreteTypesOf(Type contract) =>
        [.. typeof(FeatureRegistration).Assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && contract.IsAssignableFrom(type))];
}
