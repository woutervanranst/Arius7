namespace Arius.Api.Shared.Hosting;

/// <summary>
/// Implements one hub method, with its services constructor-injected. <c>JobsHub</c> receives the handler as a
/// method-injected service and forwards the call to it. Registered by <see cref="FeatureRegistration"/>.
/// </summary>
[SharedWithinAssembly]
internal interface IHubHandler;
