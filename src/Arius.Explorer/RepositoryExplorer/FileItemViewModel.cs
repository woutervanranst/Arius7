using System.Windows.Media;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkStorage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Arius.Explorer.RepositoryExplorer;

public partial class FileItemViewModel : ObservableObject
{
    public RepositoryFileEntry File { get; }

    [ObservableProperty]
    private string name;
    
    [ObservableProperty]
    private Brush pointerFileStateColor;
    [ObservableProperty]
    private Brush binaryFileStateColor;
    [ObservableProperty]
    private Brush pointerFileEntryStateColor;
    [ObservableProperty]
    private Brush chunkStateColor;

    [ObservableProperty]
    private long originalLength;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private string stateTooltip = "File state unknown";

    [ObservableProperty]
    private ChunkHydrationStatus hydrationStatus = ChunkHydrationStatus.Unknown;

    public FileItemViewModel(RepositoryFileEntry file)
    {
        File = file;

        Name = file.RelativePath.Name.ToString();

        PointerFileStateColor      = file.State.HasFlag(RepositoryEntryState.LocalPointer) ? Brushes.Black : Brushes.Transparent;
        BinaryFileStateColor       = file.State.HasFlag(RepositoryEntryState.LocalBinary) ? Brushes.Blue : Brushes.White;
        PointerFileEntryStateColor = file.State.HasFlag(RepositoryEntryState.Repository) ? Brushes.Black : Brushes.Transparent;
        HydrationStatus = file.State switch
        {
            _ when file.State.HasFlag(RepositoryEntryState.RepositoryRehydrating) => ChunkHydrationStatus.RehydrationPending,
            _ when file.State.HasFlag(RepositoryEntryState.RepositoryArchived)    => ChunkHydrationStatus.NeedsRehydration,
            _ when file.State.HasFlag(RepositoryEntryState.RepositoryHydrated)    => ChunkHydrationStatus.Available,
            _                                                                     => ChunkHydrationStatus.Unknown,
        };

        OriginalLength = file.OriginalSize ?? 0;
    }

    partial void OnHydrationStatusChanged(ChunkHydrationStatus value)
    {
        ChunkStateColor = value switch
        {
            ChunkHydrationStatus.Available => Brushes.Blue,
            ChunkHydrationStatus.NeedsRehydration => Brushes.LightBlue,
            ChunkHydrationStatus.RehydrationPending => Brushes.Purple,
            _ => Brushes.Transparent,
        };

        StateTooltip = value switch
        {
            ChunkHydrationStatus.Available => "Cloud chunk is available for download",
            ChunkHydrationStatus.NeedsRehydration => "Cloud chunk is archived and must be rehydrated first",
            ChunkHydrationStatus.RehydrationPending => "Cloud chunk rehydration is already pending",
            _ => "Cloud chunk status not loaded yet",
        };
    }
}
