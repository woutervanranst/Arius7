using Arius.Api.Endpoints;
using Microsoft.Net.Http.Headers;
using Shouldly;

namespace Arius.Api.Tests.Endpoints;

public sealed class DownloadCompletionTests
{
    private static RangeHeaderValue Range(string header) =>
        RangeHeaderValue.Parse(header);

    [Test]
    public async Task An_aborted_transfer_never_counts()
    {
        DownloadEndpoints.CountsAsDownloaded(aborted: true, range: null, length: 10).ShouldBeFalse();
    }

    [Test]
    public async Task A_full_transfer_counts()
    {
        DownloadEndpoints.CountsAsDownloaded(aborted: false, range: null, length: 10).ShouldBeTrue();
    }

    [Test]
    public async Task Only_a_range_that_reaches_the_end_counts()
    {
        DownloadEndpoints.CountsAsDownloaded(false, Range("bytes=0-4"), 10).ShouldBeFalse();
        DownloadEndpoints.CountsAsDownloaded(false, Range("bytes=5-"), 10).ShouldBeTrue();
        DownloadEndpoints.CountsAsDownloaded(false, Range("bytes=5-9"), 10).ShouldBeTrue();
    }
}
