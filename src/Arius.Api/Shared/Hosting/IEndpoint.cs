namespace Arius.Api.Shared.Hosting;

/// <summary>
/// One REST endpoint: a class that maps its own route and handles it through an instance method, with its
/// services constructor-injected. Discovered and registered by <see cref="FeatureRegistration"/>.
/// </summary>
[SharedWithinAssembly]
internal interface IEndpoint
{
    /// <summary>Maps the route. The route delegate must stay a lambda that binds the wire parameters and
    /// forwards to the resolved endpoint instance, so the OpenAPI description is inferred from it.</summary>
    static abstract void Map(IEndpointRouteBuilder api);
}
