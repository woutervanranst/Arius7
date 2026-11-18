using Arius.Core.Features.ContainerNamesQuery;
using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>Scripted <see cref="ContainerNamesQuery"/>: a fixed container list for any account, so the
/// Add-existing wizard works without Azure.</summary>
public sealed class ScriptedContainerNamesHandler : IStreamQueryHandler<ContainerNamesQuery, string>
{
    public static readonly IReadOnlyList<string> ContainerNames = ["scripted-container-a", "scripted-container-b"];

    public IAsyncEnumerable<string> Handle(ContainerNamesQuery query, CancellationToken cancellationToken) =>
        ContainerNames.ToAsyncEnumerable();
}
