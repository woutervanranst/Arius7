namespace Arius.E2E.Tests.Datasets;

internal sealed record SyntheticFileDefinition
{
    public SyntheticFileDefinition(RelativePath Path, long SizeBytes, string? ContentId)
    {
        if (SizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SizeBytes), "File size must be greater than zero.");

        if (ContentId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(ContentId);

        this.Path      = Path;
        this.SizeBytes = SizeBytes;
        this.ContentId = ContentId;
    }

    public RelativePath Path      { get; }
    public long         SizeBytes { get; }

    /// <summary>
    /// Synthetic Files with the same SizeBytes and ContentId will be given the same content (ie. identical files)
    /// </summary>
    public string? ContentId { get; }
}
