using Arius.Api;
using Arius.Api.Composition;

namespace Arius.Api.Testing;

/// <summary>Executable entry point for the scripted-Core host that Playwright boots out-of-process. Uses an
/// explicitly-named class (not top-level statements) so no global <c>Program</c> is emitted — that would clash
/// with Arius.Api's <c>public partial class Program</c> in the integration-test project, which references both.
/// The scripted composer is registered BEFORE <see cref="AriusApiHost.AddAriusApi"/> so its <c>TryAddSingleton</c>
/// of the Azure composer is a no-op and the scripted one wins.</summary>
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
