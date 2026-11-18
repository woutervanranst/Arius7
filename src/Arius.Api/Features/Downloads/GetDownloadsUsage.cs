using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

/// <summary><c>GET /downloads/usage</c> — staging folders on the server disk and their total size.</summary>
internal sealed class GetDownloadsUsage(DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/downloads/usage", (GetDownloadsUsage endpoint) => endpoint.Handle());

    public IResult Handle() => Results.Ok(DownloadEndpoints.Usage(staging));
}
