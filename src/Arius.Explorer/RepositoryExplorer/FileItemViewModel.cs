using Arius.Core.Ls;
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

    public FileItemViewModel(RepositoryFileEntry file)
    {
        File = file;

        Name = Path.GetFileName(file.RelativePath);

        PointerFileStateColor      = file.HasPointerFile == true ? Brushes.Black : Brushes.Transparent;
        BinaryFileStateColor       = file.BinaryExists == true ? Brushes.Blue : Brushes.White;
        PointerFileEntryStateColor = file.ExistsInCloud ? Brushes.Black : Brushes.Transparent;
        ChunkStateColor = file.Hydrated switch
        {
            true  => Brushes.Blue,
            false => Brushes.LightBlue,
            null  => Brushes.Transparent,
        };

        OriginalLength = file.OriginalSize ?? 0;

        //StateTooltip = "File is archived and available";


        // TODO add support for HydrationState.Hydrating
        //        return HydrationState switch
        //        {
        //            Core.Facade.HydrationState.Hydrated         => Brushes.Blue,
        //            Core.Facade.HydrationState.NeedsToBeQueried => Brushes.Blue, // for chunked ones - graceful UI for now
        //            Core.Facade.HydrationState.Hydrating        => Brushes.DeepSkyBlue,
        //            Core.Facade.HydrationState.NotHydrated      => Brushes.LightBlue,
        //            null                                        => Brushes.Transparent,
        //            _                                           => throw new ArgumentOutOfRangeException()
    }
}
