namespace Arius.Core.Models;

public enum TreeNodeType
{
    File,
    Directory,
    Symlink
}

public sealed record TreeNode(
    string Name,
    TreeNodeType Type,
    long Size,
    DateTimeOffset MTime,
    string Mode,
    IReadOnlyList<BlobHash> ContentHashes,
    TreeHash? SubtreeHash);
