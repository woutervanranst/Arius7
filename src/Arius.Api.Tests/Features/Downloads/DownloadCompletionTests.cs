using Arius.Api.Features.Downloads;
using Microsoft.Net.Http.Headers;
using Shouldly;

namespace Arius.Api.Tests.Features.Downloads;

public sealed class DownloadCompletionTests
{
    private static ContentRangeHeaderValue Served(string header) =>
        ContentRangeHeaderValue.Parse(header);

    [Test]
    public async Task An_aborted_transfer_never_counts()
    {
        DownloadEndpoints.CountsAsDownloaded(aborted: true, served: null, length: 10).ShouldBeFalse();
    }

    [Test]
    public async Task A_full_response_counts()
    {
        DownloadEndpoints.CountsAsDownloaded(aborted: false, served: null, length: 10).ShouldBeTrue();
    }

    [Test]
    public async Task Only_a_served_range_that_reaches_the_end_counts()
    {
        DownloadEndpoints.CountsAsDownloaded(false, Served("bytes 0-4/10"), 10).ShouldBeFalse();
        DownloadEndpoints.CountsAsDownloaded(false, Served("bytes 5-9/10"), 10).ShouldBeTrue();
    }

    [Test]
    public async Task A_suffix_range_is_judged_by_what_was_served_not_by_the_request()
    {
        // "Range: bytes=-5" on a 10-byte file is served as bytes 5-9.
        DownloadEndpoints.CountsAsDownloaded(false, Served("bytes 5-9/10"), 10).ShouldBeTrue();
    }
}
