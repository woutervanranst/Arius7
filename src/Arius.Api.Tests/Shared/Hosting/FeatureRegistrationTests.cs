using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Arius.Api.Tests.Shared.Hosting;

/// <summary>
/// Endpoints and hub handlers are discovered by an assembly scan and constructor-injected, so a feature with a
/// dependency the host does not register would only fail on its first request. Resolve them all up front.
/// </summary>
public class FeatureRegistrationTests
{
    [Test]
    public async Task Every_endpoint_and_hub_handler_resolves_from_the_production_composition()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"arius-regtest-{Guid.NewGuid():N}");
        var builder        = WebApplication.CreateBuilder();
        builder.Configuration["Arius:AppDbPath"] = Path.Combine(stateDirectory, "app.sqlite");
        builder.AddAriusApi();

        try
        {
            await using var app = builder.Build();

            var features = typeof(AriusApiHost).Assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false })
                .Where(type => typeof(IEndpoint).IsAssignableFrom(type) || typeof(IHubHandler).IsAssignableFrom(type))
                .ToList();

            features.ShouldNotBeEmpty();
            foreach (var feature in features)
                app.Services.GetService(feature).ShouldNotBeNull($"{feature.FullName} is not resolvable");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(stateDirectory, recursive: true);
        }
    }
}
