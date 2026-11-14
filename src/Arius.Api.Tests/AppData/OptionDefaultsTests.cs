using Arius.Api.AppData;
using Shouldly;

namespace Arius.Api.Tests.AppData;

/// <summary>
/// Covers the archive/restore defaults hierarchy: resolution order (repository, then global settings, then factory),
/// validation of allowed values, the onDisk to archive-flag mapping, and the lossless row round-trip.
/// </summary>
public sealed class OptionDefaultsTests
{
    [Test]
    public async Task Resolve_with_nothing_set_returns_factory_values_from_factory()
    {
        var resolved = OptionDefaults.Resolve(DefaultValues.None, DefaultValues.None);
        var factory  = OptionDefaults.Factory;

        resolved.Tier.Value.ShouldBe(factory.Tier);
        resolved.OnDisk.Value.ShouldBe(factory.OnDisk);
        resolved.FastHash.Value.ShouldBe(factory.FastHash == true);
        resolved.Overwrite.Value.ShouldBe(factory.Overwrite == true);
        resolved.NoPointers.Value.ShouldBe(factory.NoPointers == true);

        new[] { resolved.Tier.Source, resolved.OnDisk.Source, resolved.FastHash.Source, resolved.Overwrite.Source, resolved.NoPointers.Source }
            .ShouldAllBe(s => s == "factory");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Resolve_prefers_repository_over_global_over_factory()
    {
        var global     = DefaultValues.None with { Tier = "cool", FastHash = true };
        var repository = DefaultValues.None with { Tier = "hot" };

        var resolved = OptionDefaults.Resolve(global, repository);

        resolved.Tier.ShouldBe(new ResolvedValue<string>("hot", "repository"));
        resolved.FastHash.ShouldBe(new ResolvedValue<bool>(true, "settings"));
        resolved.OnDisk.ShouldBe(new ResolvedValue<string>("keep", "factory"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Explicit_false_at_repository_overrides_true_at_global()
    {
        var global     = DefaultValues.None with { FastHash = true };
        var repository = DefaultValues.None with { FastHash = false };

        OptionDefaults.Resolve(global, repository).FastHash.ShouldBe(new ResolvedValue<bool>(false, "repository"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_rejects_unknown_tier_and_onDisk()
    {
        var tier = OptionDefaults.Validate(DefaultValues.None with { Tier = "premium" });
        tier.ShouldNotBeNull();
        tier.ShouldContain("tier");
        tier.ShouldContain("premium");

        var onDisk = OptionDefaults.Validate(DefaultValues.None with { OnDisk = "delete" });
        onDisk.ShouldNotBeNull();
        onDisk.ShouldContain("onDisk");

        OptionDefaults.Validate(OptionDefaults.Factory).ShouldBeNull();
        OptionDefaults.Validate(DefaultValues.None).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ToArchiveFlags_maps_each_onDisk_value()
    {
        OptionDefaults.ToArchiveFlags("keep").ShouldBe((false, false));
        OptionDefaults.ToArchiveFlags("keep-pointers").ShouldBe((false, true));
        OptionDefaults.ToArchiveFlags("replace").ShouldBe((true, true));
        Should.Throw<ArgumentOutOfRangeException>(() => OptionDefaults.ToArchiveFlags("delete"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Rows_round_trip_including_false()
    {
        var values = new DefaultValues("cold", "replace", false, true, null);

        var rows = OptionDefaults.ToRows(values).ToList();

        rows.Count.ShouldBe(4);
        OptionDefaults.FromRows(rows).ShouldBe(values);
        await Task.CompletedTask;
    }
}
