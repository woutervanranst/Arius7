using System.Windows.Media;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkStorage;
using Arius.Explorer.RepositoryExplorer;

namespace Arius.Explorer.Tests.RepositoryExplorer;

public class FileItemViewModelTests
{
    private static readonly ContentHash ContentHashA = FakeContentHash('a');

    [Test]
    public void HydrationStatus_WhenChanged_UpdatesChunkStateColorAndTooltip()
    {
        var file = new RepositoryFileEntry(
            RelativePath: "file.txt",
            ContentHash: ContentHashA,
            OriginalSize: 1,
            Created: null,
            Modified: null,
            ExistsInCloud: true,
            ExistsLocally: true,
            HasPointerFile: false,
            BinaryExists: false,
            Hydrated: null);

        var viewModel = new FileItemViewModel(file);

        viewModel.HydrationStatus = ChunkHydrationStatus.Available;
        viewModel.ChunkStateColor.ShouldBe(Brushes.Blue);
        viewModel.StateTooltip.ShouldBe("Cloud chunk is available for download");

        viewModel.HydrationStatus = ChunkHydrationStatus.RehydrationPending;
        viewModel.ChunkStateColor.ShouldBe(Brushes.Purple);
        viewModel.StateTooltip.ShouldBe("Cloud chunk rehydration is already pending");

        viewModel.HydrationStatus = ChunkHydrationStatus.Unknown;
        viewModel.ChunkStateColor.ShouldBe(Brushes.Transparent);
        viewModel.StateTooltip.ShouldBe("Cloud chunk status not loaded yet");
    }
}
