using Arius.Api.Shared.Composition;

namespace Arius.Api.FakeTestHost;

/// <summary>Entry point of the scripted-Core host that Playwright starts out of process. A named class instead of
/// top-level statements, so no global <c>Program</c> is emitted: it would clash with Arius.Api's <c>Program</c> in
/// the integration-test project, which references both. The scripted composer is registered before
/// <see cref="AriusApiHost.AddAriusApi"/>, so that method's <c>TryAddSingleton</c> of the Azure composer does nothing.</summary>
public static class TestHost
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSingleton<ScenarioRegistry>();
        builder.Services.AddSingleton<ScenarioGate>();
        builder.Services.AddSingleton<IRepositoryCoreComposer, ScriptedRepositoryCoreComposer>();

        builder.AddAriusApi();
        var app = builder.Build();
        app.MapAriusApi();
        app.MapTestingControlEndpoints();
        app.Run();
    }
}
