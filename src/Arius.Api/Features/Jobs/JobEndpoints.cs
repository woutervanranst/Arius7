namespace Arius.Api.Endpoints;

/// <summary>Jobs history and single-job detail.</summary>
internal static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        ListJobs.Map(app);
        GetJob.Map(app);
        GetJobWarnings.Map(app);
    }
}
