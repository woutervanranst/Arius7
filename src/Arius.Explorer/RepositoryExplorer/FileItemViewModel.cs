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

        Name = file.RelativePath.Name?.ToString() ?? string.Empty;

        PointerFileStateColor      = file.HasPointerFile == true ? Brushes.Black : Brushes.Transparent;
        BinaryFileStateColor       = file.BinaryExists == true ? Brushes.Blue : Brushes.White;
        PointerFileEntryStateColor = file.ExistsInCloud ? Brushes.Black : Brushes.Transparent;
        HydrationStatus = file.Hydrated switch
        {
            true => ChunkHydrationStatus.Available,
            false => ChunkHydrationStatus.NeedsRehydration,
            null => ChunkHydrationStatus.Unknown,
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
