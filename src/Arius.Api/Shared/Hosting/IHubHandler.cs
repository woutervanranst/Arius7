namespace Arius.Api.Shared.Hosting;

/// <summary>
/// The behaviour of one hub method: a class with its services constructor-injected, which the hub receives as a
/// method-injected service and forwards to. Discovered and registered by <see cref="FeatureRegistration"/>.
/// </summary>
[SharedWithinAssembly]
internal interface IHubHandler;
