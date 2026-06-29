using Arius.Core.Shared.Cost;
using Arius.Tests.Shared.Fakes;
using Arius.Tests.Shared.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Arius.Core.Tests;

public class ServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider(IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStorageCostEstimator>(new FakeStorageCostEstimator());
        services.AddArius(new FakeInMemoryBlobContainerService(), passphrase: null, "acct", "ctr", configuration);
        return services.BuildServiceProvider();
    }

    [Test]
    public void AddArius_NoConfiguration_UsesEmbeddedCentralDefaults()
    {
        using var sp = BuildProvider();

        var options = sp.GetRequiredService<IOptions<FileExclusionOptions>>().Value;
        options.ExcludedDirectoryNames.ShouldContain("@eaDir");
        options.ExcludedFileNames.ShouldContain("thumbs.db");
        options.ExcludeSystemEntries.ShouldBeTrue();
        options.ExcludeHiddenEntries.ShouldBeFalse();

        var filter = sp.GetRequiredService<FileExclusionFilter>();
        filter.ShouldExcludeDirectory(PathSegment.Parse("@eaDir"), default).ShouldBeTrue();
        filter.ShouldExcludeFile(PathSegment.Parse("thumbs.db"), default).ShouldBeTrue();
    }

    [Test]
    public void AddArius_HostConfiguration_OverridesDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arius:Exclusions:ExcludedFileNames:0"] = "custom.junk",
                ["Arius:Exclusions:ExcludeHiddenEntries"] = "true",
            })
            .Build();

        using var sp = BuildProvider(configuration);

        var options = sp.GetRequiredService<IOptions<FileExclusionOptions>>().Value;
        // Scalar override: host value wins over the embedded default (false).
        options.ExcludeHiddenEntries.ShouldBeTrue();
        // The host-supplied entry is applied.
        options.ExcludedFileNames.ShouldContain("custom.junk");

        var filter = sp.GetRequiredService<FileExclusionFilter>();
        filter.ShouldExcludeFile(PathSegment.Parse("custom.junk"), default).ShouldBeTrue();
        filter.ShouldExcludeFile(PathSegment.Parse("x"), FileAttributes.Hidden).ShouldBeTrue();
    }
}
