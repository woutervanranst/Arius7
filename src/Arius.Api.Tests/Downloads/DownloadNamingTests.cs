using Arius.Api.Downloads;
using Shouldly;

namespace Arius.Api.Tests.Downloads;

public sealed class DownloadNamingTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 15, 22, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Everything_is_alias_and_start_date()
    {
        DownloadNaming.ArchiveName("Photos", StartedAt, 3, 3, remaining: false).ShouldBe("Photos-2026-11-15.zip");
    }

    [Test]
    public async Task A_partial_set_says_k_of_n()
    {
        DownloadNaming.ArchiveName("Photos", StartedAt, 2, 3, remaining: false).ShouldBe("Photos-2026-11-15 (2 of 3).zip");
    }

    [Test]
    public async Task The_rest_says_remaining()
    {
        DownloadNaming.ArchiveName("Photos", StartedAt, 1, 3, remaining: true).ShouldBe("Photos-2026-11-15 (remaining).zip");
    }
}
