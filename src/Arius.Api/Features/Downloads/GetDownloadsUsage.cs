using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

internal sealed class GetDownloadsUsage(DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/downloads/usage", (GetDownloadsUsage endpoint) => endpoint.Handle())
            .WithSummary("Get download staging usage")
            .WithDescription("Returns the number of download staging folders on the server disk and their total size.");

    private IResult Handle() => Results.Ok(DownloadEndpoints.Usage(staging));
}
