using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Storage;
using Shouldly;
using System.Reflection;

namespace Arius.Core.Tests.ChunkStorage;

public class ChunkStorageServiceSurfaceTests
{
    [Test]
    public void ChunkStorageService_ExposesExpectedFeatureFacingMethods()
    {
        typeof(IChunkStorageService).IsAssignableFrom(typeof(ChunkStorageService)).ShouldBeTrue();

        var methods = typeof(ChunkStorageService)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(method => method.Name, method => method);

        methods[nameof(ChunkStorageService.UploadLargeAsync)].ReturnType.ShouldBe(typeof(Task<ChunkUploadResult>));
        methods[nameof(ChunkStorageService.UploadLargeAsync)].GetParameters().Select(parameter => parameter.ParameterType).ShouldBe(
        [
            typeof(string),
            typeof(Stream),
            typeof(long),
            typeof(BlobTier),
            typeof(IProgress<long>),
            typeof(CancellationToken),
        ]);

        methods[nameof(ChunkStorageService.UploadTarAsync)].ReturnType.ShouldBe(typeof(Task<ChunkUploadResult>));
        methods[nameof(ChunkStorageService.UploadThinAsync)].ReturnType.ShouldBe(typeof(Task<bool>));
        methods[nameof(ChunkStorageService.DownloadAsync)].ReturnType.ShouldBe(typeof(Task<Stream>));
        methods[nameof(ChunkStorageService.GetHydrationStatusAsync)].ReturnType.ShouldBe(typeof(Task<ChunkHydrationStatus>));
        methods[nameof(ChunkStorageService.StartRehydrationAsync)].ReturnType.ShouldBe(typeof(Task));
        methods[nameof(ChunkStorageService.PlanRehydratedCleanupAsync)].ReturnType.ShouldBe(typeof(Task<IRehydratedChunkCleanupPlan>));
    }

    [Test]
    public void CleanupPlan_ExposesPreviewAndExecuteSurface()
    {
        typeof(IRehydratedChunkCleanupPlan).GetProperty(nameof(IRehydratedChunkCleanupPlan.ChunkCount))!.PropertyType.ShouldBe(typeof(int));
        typeof(IRehydratedChunkCleanupPlan).GetProperty(nameof(IRehydratedChunkCleanupPlan.TotalBytes))!.PropertyType.ShouldBe(typeof(long));

        var execute = typeof(IRehydratedChunkCleanupPlan).GetMethod(nameof(IRehydratedChunkCleanupPlan.ExecuteAsync));
        execute.ShouldNotBeNull();
        execute.ReturnType.ShouldBe(typeof(Task<RehydratedChunkCleanupResult>));
    }
}
