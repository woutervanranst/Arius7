using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Storage;
using Shouldly;

namespace Arius.Core.Tests.Architecture;

public class FeatureDependencyTests
{
    [Test]
    public void RestoreCommandHandler_DoesNotDependOnBlobContainerService()
    {
        var parameterTypes = typeof(RestoreCommandHandler)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        parameterTypes.ShouldNotContain(typeof(IBlobContainerService));
    }

    [Test]
    public void ArchiveCommandHandler_StillDependsOnBlobContainerService_ForContainerCreation()
    {
        var parameterTypes = typeof(ArchiveCommandHandler)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        parameterTypes.ShouldContain(typeof(IBlobContainerService));
    }
}
