using Arius.Core.Shared.FileSystem;

namespace Arius.AzureBlob;

internal static class RelativePathAzureBlobExtensions
{
    public static string ToBlobPrefix(this RelativePath path) =>
        path == RelativePath.Root ? string.Empty : $"{path}/";
}
