using Arius.Core.Features.List;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Windows.Media;

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
    private FileHydrationStatus hydrationStatus = FileHydrationStatus.Unknown;

    public FileItemViewModel(RepositoryFileEntry file)
    {
        File = file;

        Name = Path.GetFileName(file.RelativePath);

        PointerFileStateColor      = file.HasPointerFile == true ? Brushes.Black : Brushes.Transparent;
        BinaryFileStateColor       = file.BinaryExists == true ? Brushes.Blue : Brushes.White;
        PointerFileEntryStateColor = file.ExistsInCloud ? Brushes.Black : Brushes.Transparent;
        HydrationStatus = file.Hydrated switch
        {
            true => FileHydrationStatus.Available,
            false => FileHydrationStatus.NeedsRehydration,
            null => FileHydrationStatus.Unknown,
        };

        OriginalLength = file.OriginalSize ?? 0;
    }

    partial void OnHydrationStatusChanged(FileHydrationStatus value)
    {
        ChunkStateColor = value switch
        {
            FileHydrationStatus.Available => Brushes.DarkBlue,
            FileHydrationStatus.NeedsRehydration => Brushes.LightBlue,
            FileHydrationStatus.RehydrationPending => Brushes.Purple,
            _ => Brushes.Transparent,
        };

        StateTooltip = value switch
        {
            FileHydrationStatus.Available => "Cloud chunk is available for download",
            FileHydrationStatus.NeedsRehydration => "Cloud chunk is archived and must be rehydrated first",
            FileHydrationStatus.RehydrationPending => "Cloud chunk rehydration is already pending",
            _ => "Cloud chunk status not loaded yet",
        };
    }
}
