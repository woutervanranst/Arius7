namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticFileDefinition
{
    public SyntheticFileDefinition(string Path, long SizeBytes, string? ContentId)
    {
        var normalizedPath = SyntheticRepositoryPath.NormalizeRelativePath(Path, nameof(Path));

        if (SizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SizeBytes), "File size must be greater than zero.");

        if (ContentId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(ContentId);

        this.Path      = normalizedPath;
        this.SizeBytes = SizeBytes;
        this.ContentId = ContentId;
    }

    public string  Path      { get; }
    public long    SizeBytes { get; }
    public string? ContentId { get; }
}