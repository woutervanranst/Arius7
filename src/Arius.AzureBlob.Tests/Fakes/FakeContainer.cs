using Azure;

namespace Arius.AzureBlob.Tests.Fakes;

public sealed class FakeContainer(string name, bool exists, IReadOnlyList<string> blobNames)
{
    public string                Name             { get; }      = name;
    public bool                  Exists           { get; set; } = exists;
    public IReadOnlyList<string> BlobNames        { get; }      = blobNames;
    public bool                  UploadedProbe    { get; set; }
    public bool                  DeletedProbe     { get; set; }
    public bool                  CreatedContainer { get; set; }
    public ETag                  MetadataEtag     { get; set; } = default;
    public ETag                  ListEtag         { get; set; } = default;
}