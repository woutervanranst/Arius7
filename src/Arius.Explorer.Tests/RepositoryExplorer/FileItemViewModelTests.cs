using System.Windows.Media;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkStorage;
using Arius.Explorer.RepositoryExplorer;

namespace Arius.Explorer.Tests.RepositoryExplorer;

public class FileItemViewModelTests
{
    [Test]
    public void Constructor_MapsRepositoryFileStateToPresentationProperties()
    {
        var file = new RepositoryFileEntry(
            RelativePath: "folder/file.txt",
            ContentHash: "hash",
            OriginalSize: 123,
            Created: null,
            Modified: null,
            ExistsInCloud: true,
            ExistsLocally: true,
            HasPointerFile: true,
            BinaryExists: true,
            Hydrated: false);

        var viewModel = new FileItemViewModel(file);

        viewModel.Name.ShouldBe("file.txt");
        viewModel.PointerFileStateColor.ShouldBe(Brushes.Black);
        viewModel.BinaryFileStateColor.ShouldBe(Brushes.Blue);
        viewModel.PointerFileEntryStateColor.ShouldBe(Brushes.Black);
        viewModel.OriginalLength.ShouldBe(123);
        viewModel.HydrationStatus.ShouldBe(ChunkHydrationStatus.NeedsRehydration);
        viewModel.ChunkStateColor.ShouldBe(Brushes.LightBlue);
        viewModel.StateTooltip.ShouldContain("rehydrated");
    }

    [Test]
    public void HydrationStatus_WhenChanged_UpdatesChunkStateColorAndTooltip()
    {
        var file = new RepositoryFileEntry(
            RelativePath: "file.txt",
            ContentHash: "hash",
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
